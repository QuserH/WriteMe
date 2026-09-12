using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WriteMe.Core;

namespace WriteMe.SyncServer;

// Note: 后台查看服务器已接收的数据和登录设备，暂停写入保留草稿与账号身份 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed partial class SyncRepository
{
    private void RememberDisplayName(string account, string name, SqliteTransaction transaction)
    {
        using var command = Command("INSERT OR IGNORE INTO account_display_names(account_id,name) VALUES($id,$name)", ("$id", account), ("$name", name));
        command.Transaction = transaction; command.ExecuteNonQuery();
    }
    private void InitializeSessionMetadata()
    {
        var columns = new HashSet<string>();
        using (var command = Command("PRAGMA table_info(sessions)"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        foreach (var (name, definition) in new[] { ("session_id", "TEXT"), ("created_at", "INTEGER"), ("last_seen_at", "INTEGER"), ("device", "TEXT NOT NULL DEFAULT '已有登录设备'") })
            if (!columns.Contains(name)) { using var alter = Command($"ALTER TABLE sessions ADD COLUMN {name} {definition}"); alter.ExecuteNonQuery(); }
        using var initialize = Command("UPDATE sessions SET session_id=lower(hex(randomblob(16))) WHERE session_id IS NULL; CREATE UNIQUE INDEX IF NOT EXISTS idx_sessions_id ON sessions(session_id)");
        initialize.ExecuteNonQuery();
    }
    public static string SessionDevice(string userAgent)
    {
        if (userAgent.Contains("WriteME", StringComparison.OrdinalIgnoreCase)) return "Windows · WriteME 原生端";
        var platform = userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android" : userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iOS" : userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) ? "macOS" : "网页";
        var browser = userAgent.Contains("Edg", StringComparison.OrdinalIgnoreCase) ? "Edge" : userAgent.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ? "Chrome" : userAgent.Contains("Firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox" : userAgent.Contains("Safari", StringComparison.OrdinalIgnoreCase) ? "Safari" : "浏览器或接口";
        return platform + " · " + browser;
    }
    public SharedProfile EditProfile(string account, SharedProfileChange input)
    {
        var publicId = AccountProfiles.PublicId(input.PublicId); var name = Name(input.DisplayName, 40);
        var image = input.Avatar == null ? null : AccountProfiles.ValidateAvatar(input.Avatar);
        lock (_gate)
        {
            var previous = Ready(account); using var transaction = _database.BeginTransaction();
            using var command = Command("UPDATE accounts SET public_id=$public,display_name=$name WHERE id=$id", ("$public", publicId), ("$name", name), ("$id", account)); command.Transaction = transaction;
            try { command.ExecuteNonQuery(); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new SharedAccessException("这个 ID 已被使用", 409); }
            RememberDisplayName(account, previous.DisplayName, transaction); RememberDisplayName(account, name, transaction);
            if (input.Avatar is { } avatar)
            {
                command.CommandText = "UPDATE accounts SET avatar_color=$color,avatar_text=$text WHERE id=$id";
                command.Parameters.AddWithValue("$color", avatar.Color.ToUpperInvariant()); command.Parameters.AddWithValue("$text", avatar.Text.Trim()); command.ExecuteNonQuery();
                if (image != null || avatar.RemoveImage)
                {
                    command.CommandText = "UPDATE accounts SET avatar_image=$image,avatar_version=$version WHERE id=$id";
                    command.Parameters.AddWithValue("$image", (object?)image ?? DBNull.Value); command.Parameters.AddWithValue("$version", image == null ? DBNull.Value : Convert.ToHexString(SHA256.HashData(image))); command.ExecuteNonQuery();
                }
            }
            transaction.Commit(); return Profile(account);
        }
    }
    public SharedProfile ChangeOwnPassword(string account, SharedPasswordChange input, string currentToken)
    {
        AccountProfiles.ValidatePassword(input.Password);
        if (input.CurrentPassword is not { Length: > 0 and <= 1024 }) throw new SharedAccessException("当前密码不正确", 403);
        lock (_gate)
        {
            _ = Ready(account); byte[] salt, expected;
            using (var lookup = Command("SELECT salt,password_hash FROM accounts WHERE id=$id", ("$id", account)))
            using (var reader = lookup.ExecuteReader()) { if (!reader.Read()) throw new SharedAccessException("账号不存在", 401); salt = (byte[])reader[0]; expected = (byte[])reader[1]; }
            var actual = Rfc2898DeriveBytes.Pbkdf2(input.CurrentPassword, salt, 210000, HashAlgorithmName.SHA256, 32);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new SharedAccessException("当前密码不正确", 403);
            salt = RandomNumberGenerator.GetBytes(32); var hash = Rfc2898DeriveBytes.Pbkdf2(input.Password, salt, 210000, HashAlgorithmName.SHA256, 32);
            using var transaction = _database.BeginTransaction();
            using var command = Command("UPDATE accounts SET salt=$salt,password_hash=$hash WHERE id=$id; DELETE FROM sessions WHERE account_id=$id AND token_hash<>$keep", ("$salt", salt), ("$hash", hash), ("$id", account), ("$keep", TokenHash(currentToken)));
            command.Transaction = transaction; command.ExecuteNonQuery(); transaction.Commit(); return Profile(account);
        }
    }
    public byte[]? AvatarImage(string actor, string account, string version)
    {
        if (!SyncProtocol.ValidId(account) || !NoteStore.IsAssetId(version)) return null;
        lock (_gate)
        {
            var profile = Ready(actor);
            if (actor != account && !profile.IsAdmin)
            {
                using var membership = Command("SELECT 1 FROM shared_members a JOIN shared_members b ON a.workspace_id=b.workspace_id WHERE a.account_id=$actor AND b.account_id=$account LIMIT 1", ("$actor", actor), ("$account", account));
                if (membership.ExecuteScalar() == null) throw new SharedAccessException("无法访问此头像", 404);
            }
            using var command = Command("SELECT avatar_image FROM accounts WHERE id=$id AND avatar_version=$version", ("$id", account), ("$version", version)); return command.ExecuteScalar() as byte[];
        }
    }
    private void EnsureSyncWrite(string account)
    {
        var profile = Profile(account);
        if (profile.Disabled) throw new SharedAccessException("账号已停用", 401);
        if (profile.SyncPaused) throw new SharedAccessException("管理员已暂停此账号写入，未发送的修改保留在设备上", 423);
    }
    private long Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters); var value = command.ExecuteScalar(); return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value);
    }
    private SharedAccountUsage AccountUsage(string account)
    {
        var profile = Profile(account); long? login, sync;
        using (var command = Command("SELECT last_login_at,last_sync_at FROM accounts WHERE id=$id", ("$id", account)))
        using (var reader = command.ExecuteReader()) { reader.Read(); login = reader.IsDBNull(0) ? null : reader.GetInt64(0); sync = reader.IsDBNull(1) ? null : reader.GetInt64(1); }
        var personalCount = Scalar("SELECT COUNT(*) FROM sync_entities WHERE account_id=$id AND entity_key LIKE 'document/%'", ("$id", account));
        var conflicts = Scalar("SELECT COUNT(*) FROM sync_entities WHERE account_id=$id AND json_array_length(state,'$.versions')>1", ("$id", account));
        var personalBytes = Scalar("SELECT COALESCE(SUM(length(CAST(state AS BLOB))),0) FROM sync_entities WHERE account_id=$id", ("$id", account));
        const string shared = " FROM shared_documents d JOIN shared_members m ON m.workspace_id=d.workspace_id WHERE m.account_id=$id";
        var sharedCount = Scalar("SELECT COUNT(*)" + shared + " AND d.deleted=0", ("$id", account));
        var sharedBytes = Scalar("SELECT COALESCE(SUM(length(d.state)),0)" + shared, ("$id", account));
        var workspaces = Scalar("SELECT COUNT(*) FROM shared_members WHERE account_id=$id", ("$id", account));
        var sessions = Scalar("SELECT COUNT(*) FROM sessions WHERE account_id=$id AND expires_at>$now", ("$id", account), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        return new(profile, (int)personalCount, (int)conflicts, personalBytes, (int)sharedCount, sharedBytes, (int)workspaces, (int)sessions, login, sync);
    }
    public SharedAccountUsage[] AccountUsages(string actor)
    {
        lock (_gate) { Admin(actor); return Accounts(actor).Select(profile => AccountUsage(profile.Id)).ToArray(); }
    }
    public SharedAccountData AccountData(string actor, string account, string currentToken)
    {
        lock (_gate)
        {
            Admin(actor); var usage = AccountUsage(account); var workspaces = new List<SharedWorkspaceUsage>();
            using (var command = Command("SELECT w.id,w.name,m.role,SUM(CASE WHEN d.deleted=0 THEN 1 ELSE 0 END),SUM(CASE WHEN d.deleted=1 THEN 1 ELSE 0 END),COALESCE(SUM(length(d.state)),0),MAX(d.updated_at) FROM shared_workspaces w JOIN shared_members m ON m.workspace_id=w.id LEFT JOIN shared_documents d ON d.workspace_id=w.id WHERE m.account_id=$id GROUP BY w.id ORDER BY w.created_at", ("$id", account)))
            using (var reader = command.ExecuteReader()) while (reader.Read()) workspaces.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6)));
            var sessions = new List<SharedSessionInfo>();
            using (var command = Command("SELECT session_id,device,created_at,last_seen_at,expires_at,token_hash FROM sessions WHERE account_id=$id AND expires_at>$now ORDER BY COALESCE(last_seen_at,0) DESC LIMIT 200", ("$id", account), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
            using (var reader = command.ExecuteReader()) while (reader.Read()) sessions.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5) == TokenHash(currentToken)));
            var documents = new List<SharedStoredDocument>();
            using (var command = Command("SELECT entity_key,state FROM sync_entities WHERE account_id=$id AND entity_key LIKE 'document/%' ORDER BY sequence DESC LIMIT 200", ("$id", account)))
            using (var reader = command.ExecuteReader()) while (reader.Read())
            {
                var raw = reader.GetString(1); var entity = SyncProtocol.Read<SyncEntity>(raw); var live = entity.Versions.FirstOrDefault(version => version.Payload != null); var title = "已删除的文档";
                if (live?.Payload is { } payload) { using var json = JsonDocument.Parse(payload, new() { MaxDepth = 256 }); if (json.RootElement.TryGetProperty("document", out var document) && document.TryGetProperty("title", out var name)) title = name.GetString() ?? "无标题"; }
                documents.Add(new(reader.GetString(0)[9..], title, entity.Versions.Length, Encoding.UTF8.GetByteCount(raw), live == null));
            }
            var assetCount = 0; var assetBytes = 0L; var directory = Path.Combine(DataDirectory, "assets", account);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles()) if (NoteStore.IsAssetId(file.Name) && (file.Attributes & FileAttributes.ReparsePoint) == 0) { assetCount++; assetBytes += file.Length; }
            return new(usage, [.. workspaces], [.. documents], [.. sessions], assetCount, assetBytes);
        }
    }
    public void RevokeAccountSessions(string actor, string account, string? session, string currentToken)
    {
        lock (_gate)
        {
            Admin(actor); _ = Profile(account);
            using var command = Command(session == null ? "DELETE FROM sessions WHERE account_id=$id AND token_hash<>$keep" : "DELETE FROM sessions WHERE account_id=$id AND session_id=$session",
                ("$id", account), ("$keep", actor == account ? TokenHash(currentToken) : ""), ("$session", session)); command.ExecuteNonQuery();
        }
    }
}
