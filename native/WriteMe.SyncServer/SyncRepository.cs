using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using WriteMe.Core;

namespace WriteMe.SyncServer;

// Note: 账号隔离的 CRDT 中转库，只存密码/会话哈希 — 见 .agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md
public sealed partial class SyncRepository : IDisposable
{
    private readonly SqliteConnection _database;
    private readonly object _gate = new();
    public string DataDirectory { get; }

    public SyncRepository(string directory, string? setupUser = null, string? setupPassword = null)
    {
        DataDirectory = Path.GetFullPath(directory); Directory.CreateDirectory(DataDirectory);
        _database = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, "sync.db"), Pooling = false, DefaultTimeout = 15 }.ToString()); _database.Open();
        try
        {
            using var command = _database.CreateCommand(); command.CommandText = """
                PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;
                CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY,username TEXT NOT NULL UNIQUE COLLATE NOCASE,salt BLOB NOT NULL,password_hash BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS sessions(token_hash TEXT PRIMARY KEY,account_id TEXT NOT NULL REFERENCES accounts(id),expires_at INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS sync_entities(account_id TEXT NOT NULL REFERENCES accounts(id),entity_key TEXT NOT NULL,state TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(account_id,entity_key));
                CREATE INDEX IF NOT EXISTS idx_sync_entities_sequence ON sync_entities(account_id,sequence);
                CREATE TABLE IF NOT EXISTS server_counter(id INTEGER PRIMARY KEY,value INTEGER NOT NULL);
                INSERT OR IGNORE INTO server_counter(id,value) VALUES(1,0);
                """; command.ExecuteNonQuery();
            InitializeSharedSchema();
            command.CommandText = "SELECT COUNT(*) FROM accounts";
            if (Convert.ToInt64(command.ExecuteScalar()) == 0)
            {
                if (string.IsNullOrEmpty(setupUser) || string.IsNullOrEmpty(setupPassword)) throw new InvalidOperationException("首次启动请设置 WRITEME_SETUP_USER 和 WRITEME_SETUP_PASSWORD（至少 6 个字符）");
                var admin = CreateAccount(setupUser, setupPassword);
                command.CommandText = "UPDATE accounts SET is_admin=1 WHERE id=$id"; command.Parameters.AddWithValue("$id", admin); command.ExecuteNonQuery();
            }
        }
        catch { _database.Dispose(); throw; }
    }

    public string CreateAccount(string username, string password)
    {
        username = username?.Trim() ?? "";
        if (username.Length is < 1 or > 100 || username.Any(char.IsControl)) throw new ArgumentException("账号名称无效");
        AccountProfiles.ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(32); var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210000, HashAlgorithmName.SHA256, 32); var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            using var command = _database.CreateCommand(); command.CommandText = "INSERT INTO accounts(id,username,salt,password_hash) VALUES($id,$user,$salt,$hash)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$user", username); command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery();
        }
        return id;
    }
    public SyncLoginResult? Login(SyncLogin login, string device = "桌面端或接口")
    {
        if (login.Username is not { Length: > 0 and <= 100 } || login.Password is not { Length: > 0 and <= 1024 }) return null;
        lock (_gate)
        {
            using var command = _database.CreateCommand(); command.CommandText = "SELECT id,salt,password_hash FROM accounts WHERE username=$user AND disabled=0"; command.Parameters.AddWithValue("$user", login.Username.Trim());
            string? id = null; byte[] salt = new byte[32]; byte[] expected = new byte[32];
            using (var reader = command.ExecuteReader()) if (reader.Read()) { id = reader.GetString(0); salt = (byte[])reader[1]; expected = (byte[])reader[2]; }
            var actual = Rfc2898DeriveBytes.Pbkdf2(login.Password, salt, 210000, HashAlgorithmName.SHA256, 32);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected) || id == null) return null;
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();
            command.CommandText = "DELETE FROM sessions WHERE expires_at<$now; INSERT INTO sessions(token_hash,account_id,expires_at,session_id,created_at,last_seen_at,device) VALUES($hash,$id,$expiry,$session,$now,$now,$device); UPDATE accounts SET last_login_at=$now WHERE id=$id";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$hash", TokenHash(token)); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$expiry", expiry);
            command.Parameters.AddWithValue("$session", Guid.NewGuid().ToString("N")); command.Parameters.AddWithValue("$device", new string(device.Where(c => !char.IsControl(c)).Take(160).ToArray())); command.ExecuteNonQuery();
            return new(token, id, expiry);
        }
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public string? Authenticate(string token)
    {
        if (token.Length is < 32 or > 128) return null;
        lock (_gate)
        {
            using var command = _database.CreateCommand(); command.CommandText = "SELECT account_id FROM sessions JOIN accounts ON accounts.id=sessions.account_id WHERE token_hash=$hash AND expires_at>$now AND disabled=0";
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            command.Parameters.AddWithValue("$hash", TokenHash(token)); command.Parameters.AddWithValue("$now", now); var account = command.ExecuteScalar() as string;
            if (account != null) { command.CommandText = "UPDATE sessions SET last_seen_at=$now WHERE token_hash=$hash AND (last_seen_at IS NULL OR last_seen_at<$before)"; command.Parameters.AddWithValue("$before", now - 60000); command.ExecuteNonQuery(); }
            return account;
        }
    }
    public void Logout(string token)
    {
        lock (_gate) { using var command = _database.CreateCommand(); command.CommandText = "DELETE FROM sessions WHERE token_hash=$hash"; command.Parameters.AddWithValue("$hash", TokenHash(token)); command.ExecuteNonQuery(); }
    }

    public SyncResponse Exchange(string account, SyncRequest request)
    {
        if (request.Cursor < 0 || request.Entities == null || request.Entities.Length > 100) throw new InvalidDataException("同步批次无效");
        foreach (var entity in request.Entities) SyncProtocol.Validate(entity);
        lock (_gate)
        {
            if (request.Entities.Length > 0) EnsureSyncWrite(account);
            using var transaction = _database.BeginTransaction();
            using var command = _database.CreateCommand(); command.Transaction = transaction;
            var acknowledgements = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entity in request.Entities)
            {
                command.CommandText = "SELECT state FROM sync_entities WHERE account_id=$account AND entity_key=$key"; command.Parameters.Clear(); command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$key", entity.Key);
                var old = command.ExecuteScalar() as string;
                var merged = SyncProtocol.Merge(old == null ? new(entity.Key, []) : SyncProtocol.Read<SyncEntity>(old), entity); var state = SyncProtocol.Canonical(merged);
                acknowledgements[entity.Key] = state;
                if (old == state) continue;
                command.CommandText = "UPDATE server_counter SET value=value+1 WHERE id=1 RETURNING value"; var sequence = Convert.ToInt64(command.ExecuteScalar());
                command.CommandText = "INSERT INTO sync_entities(account_id,entity_key,state,sequence) VALUES($account,$key,$state,$sequence) ON CONFLICT(account_id,entity_key) DO UPDATE SET state=excluded.state,sequence=excluded.sequence";
                command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$sequence", sequence); command.ExecuteNonQuery();
            }
            command.Parameters.Clear(); command.CommandText = "SELECT value FROM server_counter WHERE id=1";
            var latest = Convert.ToInt64(command.ExecuteScalar()); var cursor = request.Cursor > latest ? 0 : request.Cursor;
            command.CommandText = "SELECT state,sequence FROM sync_entities WHERE account_id=$account AND sequence>$cursor ORDER BY sequence";
            command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$cursor", cursor);
            var result = new Dictionary<string, SyncEntity>(StringComparer.Ordinal); var bytes = 0; var more = false;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var state = reader.GetString(0); var size = Encoding.UTF8.GetByteCount(state);
                    if (result.Count >= 100 || result.Count > 0 && bytes + size > SyncProtocol.MaximumBatchBytes - 65536) { more = true; break; }
                    var entity = SyncProtocol.Read<SyncEntity>(state); result[entity.Key] = entity; bytes += size; cursor = reader.GetInt64(1);
                }
            }
            foreach (var (key, state) in acknowledgements)
            {
                if (result.ContainsKey(key)) continue;
                var size = Encoding.UTF8.GetByteCount(state); if (bytes + size > SyncProtocol.MaximumBatchBytes - 65536) continue;
                result[key] = SyncProtocol.Read<SyncEntity>(state); bytes += size;
            }
            command.Parameters.Clear(); command.CommandText = "UPDATE accounts SET last_sync_at=$now WHERE id=$account"; command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$account", account); command.ExecuteNonQuery();
            transaction.Commit(); return new(cursor, more, result.Values.ToArray());
        }
    }

    public string? AssetPath(string account, string hash)
    {
        if (!SyncProtocol.ValidId(account) || !NoteStore.IsAssetId(hash)) return null;
        var path = Path.Combine(DataDirectory, "assets", account, hash); return File.Exists(path) ? path : null;
    }
    public async Task SaveAssetAsync(string account, string id, Stream source, CancellationToken cancellation)
    {
        if (!SyncProtocol.ValidId(account) || !NoteStore.IsAssetId(id)) throw new InvalidDataException("附件标识无效");
        EnsureSyncWrite(account);
        var directory = Path.Combine(DataDirectory, "assets", account); Directory.CreateDirectory(directory); var temporary = Path.Combine(directory, ".upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var bytes = 0L; var buffer = new byte[81920]; int read;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    bytes += read; if (bytes > NoteStore.MaximumAssetSize) throw new InvalidDataException("附件超过 50 MB");
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
                await output.FlushAsync(cancellation);
            }
            if (Convert.ToHexString(hash.GetHashAndReset()) != id) throw new InvalidDataException("附件内容与哈希不一致");
            var path = Path.Combine(directory, id);
            lock (_gate)
            {
                EnsureSyncWrite(account);
                try { File.Move(temporary, path, false); } catch (IOException) when (File.Exists(path)) { File.Delete(temporary); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { lock (_gate) _database.Dispose(); }
}
