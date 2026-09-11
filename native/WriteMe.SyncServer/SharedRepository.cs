using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WriteMe.Core;

namespace WriteMe.SyncServer;

public sealed class SharedAccessException(string message, int status = 403) : Exception(message)
{
    public int Status { get; } = status;
}

// Note: 管理员开通账号、显式共享成员和逐条评论作者验证 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed partial class SyncRepository
{
    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _database.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private void InitializeSharedSchema()
    {
        var columns = new HashSet<string>();
        using (var command = Command("PRAGMA table_info(accounts)"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        var migrated = !columns.Contains("is_admin");
        foreach (var (name, definition) in new[] { ("public_id", "TEXT"), ("display_name", "TEXT NOT NULL DEFAULT ''"),
            ("is_admin", "INTEGER NOT NULL DEFAULT 0"), ("disabled", "INTEGER NOT NULL DEFAULT 0"), ("needs_setup", "INTEGER NOT NULL DEFAULT 1") })
            if (!columns.Contains(name)) { using var alter = Command($"ALTER TABLE accounts ADD COLUMN {name} {definition}"); alter.ExecuteNonQuery(); }
        using var schema = Command("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_public_id ON accounts(public_id COLLATE NOCASE) WHERE public_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS shared_workspaces(id TEXT PRIMARY KEY,name TEXT NOT NULL,created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS shared_members(workspace_id TEXT NOT NULL REFERENCES shared_workspaces(id),account_id TEXT NOT NULL REFERENCES accounts(id),role TEXT NOT NULL,PRIMARY KEY(workspace_id,account_id));
            CREATE TABLE IF NOT EXISTS shared_documents(id TEXT PRIMARY KEY,workspace_id TEXT NOT NULL REFERENCES shared_workspaces(id),title TEXT NOT NULL,state BLOB NOT NULL,updated_at INTEGER NOT NULL,deleted INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS idx_shared_documents_workspace ON shared_documents(workspace_id,deleted,updated_at);
            CREATE TABLE IF NOT EXISTS shared_comment_ledger(document_id TEXT NOT NULL REFERENCES shared_documents(id),message_id TEXT NOT NULL,body_hash TEXT NOT NULL,PRIMARY KEY(document_id,message_id,body_hash));
            """); schema.ExecuteNonQuery();
        if (migrated)
        {
            // Preserve existing M6 accounts; only the original bootstrap account becomes the server administrator.
            using var promote = Command("UPDATE accounts SET is_admin=1 WHERE rowid=(SELECT MIN(rowid) FROM accounts)"); promote.ExecuteNonQuery();
        }
    }
    private static SharedProfile ReadProfile(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3) is { Length: > 0 } display ? display : reader.GetString(1), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6));
    public SharedProfile Profile(string account)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,username,public_id,display_name,is_admin,disabled,needs_setup FROM accounts WHERE id=$id", ("$id", account));
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw new SharedAccessException("账号不存在", 401); return ReadProfile(reader);
        }
    }
    private SharedProfile Ready(string account)
    {
        var profile = Profile(account);
        if (profile.Disabled) throw new SharedAccessException("账号已停用", 401);
        if (profile.NeedsSetup) throw new SharedAccessException("请先设置个人 ID 和密码", 409);
        return profile;
    }
    private void Admin(string account) { if (!Ready(account).IsAdmin) throw new SharedAccessException("仅管理员可以执行此操作"); }
    private static string Name(string? value, int maximum)
    {
        value = value?.Trim() ?? ""; if (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("名称为空或超过长度限制"); return value;
    }
    public SharedProfile SetupProfile(string account, SharedProfileSetup setup, string? currentToken = null)
    {
        var publicId = (setup.PublicId ?? "").Trim().TrimStart('@').ToLowerInvariant(); var name = Name(setup.DisplayName, 40);
        if (!Regex.IsMatch(publicId, "^[a-z0-9][a-z0-9_-]{2,31}$", RegexOptions.CultureInvariant)) throw new ArgumentException("ID 使用 3–32 位小写字母、数字、下划线或短横线");
        if (setup.Password is not { Length: >= 12 and <= 1024 }) throw new ArgumentException("密码至少需要 12 个字符");
        var salt = RandomNumberGenerator.GetBytes(32); var hash = Rfc2898DeriveBytes.Pbkdf2(setup.Password, salt, 210000, HashAlgorithmName.SHA256, 32);
        lock (_gate)
        {
            var old = Profile(account); if (!old.NeedsSetup) throw new SharedAccessException("个人 ID 已设置；修改密码请使用账号设置", 409);
            if (old.Disabled) throw new SharedAccessException("账号已停用", 401);
            using var transaction = _database.BeginTransaction();
            using var command = Command("UPDATE accounts SET public_id=$public,display_name=$name,salt=$salt,password_hash=$hash,needs_setup=0 WHERE id=$id",
                ("$public", publicId), ("$name", name), ("$salt", salt), ("$hash", hash), ("$id", account));
            command.Transaction = transaction;
            try { command.ExecuteNonQuery(); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new SharedAccessException("这个 ID 已被使用", 409); }
            command.CommandText = "DELETE FROM sessions WHERE account_id=$id AND token_hash<>$keep";
            command.Parameters.AddWithValue("$keep", currentToken == null ? "" : TokenHash(currentToken)); command.ExecuteNonQuery(); transaction.Commit();
            return Profile(account);
        }
    }
    public SharedProfile[] Accounts(string actor)
    {
        lock (_gate)
        {
            Admin(actor); using var command = Command("SELECT id,username,public_id,display_name,is_admin,disabled,needs_setup FROM accounts ORDER BY rowid");
            using var reader = command.ExecuteReader(); var result = new List<SharedProfile>(); while (reader.Read()) result.Add(ReadProfile(reader)); return [.. result];
        }
    }
    public SharedProfile AddAccount(string actor, SharedAccountInput input)
    {
        lock (_gate)
        {
            Admin(actor); string id;
            try { id = CreateAccount(input.Username, input.Password); } catch (SqliteException e) when (e.SqliteErrorCode == 19) { throw new SharedAccessException("登录账号已存在", 409); }
            using var command = Command("UPDATE accounts SET is_admin=$admin WHERE id=$id", ("$admin", input.IsAdmin), ("$id", id)); command.ExecuteNonQuery(); return Profile(id);
        }
    }
    public SharedProfile ChangeAccount(string actor, string id, SharedAccountChange change)
    {
        lock (_gate)
        {
            Admin(actor); var profile = Profile(id);
            if (actor == id && (change.Disabled == true || change.IsAdmin == false)) throw new SharedAccessException("不能停用或移除自己的管理员权限", 409);
            if (change.Password is { Length: < 12 or > 1024 }) throw new ArgumentException("临时密码至少需要 12 个字符");
            using var transaction = _database.BeginTransaction();
            using var command = Command("UPDATE accounts SET disabled=$disabled,is_admin=$admin WHERE id=$id", ("$disabled", change.Disabled ?? profile.Disabled), ("$admin", change.IsAdmin ?? profile.IsAdmin), ("$id", id));
            command.Transaction = transaction; command.ExecuteNonQuery();
            if (change.Password != null)
            {
                var salt = RandomNumberGenerator.GetBytes(32); var hash = Rfc2898DeriveBytes.Pbkdf2(change.Password, salt, 210000, HashAlgorithmName.SHA256, 32);
                command.CommandText = "UPDATE accounts SET salt=$salt,password_hash=$hash,needs_setup=1 WHERE id=$id";
                command.Parameters.AddWithValue("$salt", salt); command.Parameters.AddWithValue("$hash", hash); command.ExecuteNonQuery();
            }
            if (change.Password != null || change.Disabled == true)
            { command.CommandText = "DELETE FROM sessions WHERE account_id=$id"; command.ExecuteNonQuery(); }
            transaction.Commit(); return Profile(id);
        }
    }
    public SharedWorkspace[] Workspaces(string actor)
    {
        lock (_gate)
        {
            Ready(actor); using var command = Command("SELECT w.id,w.name,m.role,w.created_at FROM shared_workspaces w JOIN shared_members m ON m.workspace_id=w.id WHERE m.account_id=$id ORDER BY w.created_at,w.id", ("$id", actor));
            using var reader = command.ExecuteReader(); var result = new List<SharedWorkspace>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3))); return [.. result];
        }
    }
    public SharedWorkspace CreateWorkspace(string actor, string name)
    {
        name = Name(name, 80);
        lock (_gate)
        {
            Ready(actor); var id = Guid.NewGuid().ToString("N"); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var transaction = _database.BeginTransaction(); using var command = Command("INSERT INTO shared_workspaces(id,name,created_at) VALUES($id,$name,$now); INSERT INTO shared_members(workspace_id,account_id,role) VALUES($id,$actor,'owner')", ("$id", id), ("$name", name), ("$now", now), ("$actor", actor));
            command.Transaction = transaction; command.ExecuteNonQuery(); transaction.Commit(); return new(id, name, "owner", now);
        }
    }
    public string WorkspaceRole(string actor, string workspace)
    {
        lock (_gate)
        {
            Ready(actor); using var command = Command("SELECT role FROM shared_members WHERE workspace_id=$workspace AND account_id=$actor", ("$workspace", workspace), ("$actor", actor));
            return command.ExecuteScalar() as string ?? throw new SharedAccessException("你没有这个工作区的访问权限", 404);
        }
    }
    public SharedMember[] Members(string actor, string workspace)
    {
        lock (_gate)
        {
            _ = WorkspaceRole(actor, workspace); using var command = Command("SELECT a.id,a.public_id,a.display_name,m.role FROM shared_members m JOIN accounts a ON a.id=m.account_id WHERE m.workspace_id=$workspace ORDER BY m.role,a.display_name", ("$workspace", workspace));
            using var reader = command.ExecuteReader(); var result = new List<SharedMember>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetString(2), reader.GetString(3))); return [.. result];
        }
    }
    public void SetMember(string actor, string workspace, SharedMemberInput input)
    {
        if (!SharedProtocol.ValidRole(input.Role)) throw new ArgumentException("成员权限无效");
        lock (_gate)
        {
            if (WorkspaceRole(actor, workspace) != "owner") throw new SharedAccessException("仅工作区所有者可以管理成员");
            using var lookup = Command("SELECT id FROM accounts WHERE public_id=$public AND disabled=0 AND needs_setup=0", ("$public", Name(input.PublicId, 33).TrimStart('@').ToLowerInvariant()));
            var target = lookup.ExecuteScalar() as string ?? throw new SharedAccessException("没有找到这个 ID；对方需要先完成账号设置", 404);
            ProtectOwner(workspace, target, input.Role);
            using var command = Command("INSERT INTO shared_members(workspace_id,account_id,role) VALUES($workspace,$id,$role) ON CONFLICT(workspace_id,account_id) DO UPDATE SET role=excluded.role", ("$workspace", workspace), ("$id", target), ("$role", input.Role)); command.ExecuteNonQuery();
        }
    }
    private void ProtectOwner(string workspace, string target, string? role)
    {
        if (role == "owner") return;
        using var command = Command("SELECT COUNT(*) FROM shared_members WHERE workspace_id=$workspace AND role='owner'", ("$workspace", workspace)); var count = Convert.ToInt64(command.ExecuteScalar());
        command.CommandText = "SELECT role FROM shared_members WHERE workspace_id=$workspace AND account_id=$target"; command.Parameters.AddWithValue("$target", target);
        if (command.ExecuteScalar() as string == "owner" && count <= 1) throw new SharedAccessException("工作区至少需要一位所有者", 409);
    }
    public void RemoveMember(string actor, string workspace, string target)
    {
        lock (_gate)
        {
            if (WorkspaceRole(actor, workspace) != "owner") throw new SharedAccessException("仅工作区所有者可以管理成员");
            ProtectOwner(workspace, target, null);
            using var command = Command("DELETE FROM shared_members WHERE workspace_id=$workspace AND account_id=$target", ("$workspace", workspace), ("$target", target)); command.ExecuteNonQuery();
        }
    }
    public SharedDocumentInfo[] SharedDocuments(string actor, string workspace, bool trash = false)
    {
        lock (_gate)
        {
            _ = WorkspaceRole(actor, workspace); using var command = Command("SELECT id,workspace_id,title,updated_at,deleted FROM shared_documents WHERE workspace_id=$workspace AND deleted=$deleted ORDER BY updated_at DESC,id", ("$workspace", workspace), ("$deleted", trash));
            using var reader = command.ExecuteReader(); var result = new List<SharedDocumentInfo>(); while (reader.Read()) result.Add(ReadDocumentInfo(reader)); return [.. result];
        }
    }
    private static SharedDocumentInfo ReadDocumentInfo(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetBoolean(4));
    public SharedDocumentData CreateSharedDocument(string actor, string workspace, string title)
    {
        title ??= "";
        if (title.Length > 500) throw new ArgumentException("文档标题超过 500 字");
        lock (_gate)
        {
            var role = WorkspaceRole(actor, workspace); if (!SharedProtocol.CanWrite(role)) throw new SharedAccessException("此工作区仅可阅读");
            using var replica = new SharedDocumentReplica(); replica.Write(NoteNode.EmptyDocument(), title, trackHistory: false); var state = replica.State();
            var doc = new SharedDocumentInfo(Guid.NewGuid().ToString("N"), workspace, title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            using var command = Command("INSERT INTO shared_documents(id,workspace_id,title,state,updated_at) VALUES($id,$workspace,$title,$state,$now)", ("$id", doc.Id), ("$workspace", workspace), ("$title", title), ("$state", state), ("$now", doc.UpdatedAt)); command.ExecuteNonQuery();
            return new(doc, role, state);
        }
    }
    public SharedDocumentData SharedDocument(string actor, string document, bool allowDeleted = false)
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,workspace_id,title,updated_at,deleted,state FROM shared_documents WHERE id=$id", ("$id", document));
            SharedDocumentInfo info; byte[] state;
            using (var reader = command.ExecuteReader())
            { if (!reader.Read()) throw new SharedAccessException("文档不存在", 404); info = ReadDocumentInfo(reader); state = (byte[])reader[5]; }
            var role = WorkspaceRole(actor, info.WorkspaceId);
            if (info.Deleted && !allowDeleted) throw new SharedAccessException("文档已移至回收站", 410);
            return new(info, role, state);
        }
    }
    public void TrashSharedDocument(string actor, string id, bool deleted)
    {
        lock (_gate)
        {
            var doc = SharedDocument(actor, id, true); if (!SharedProtocol.CanWrite(doc.Role)) throw new SharedAccessException("此工作区仅可阅读");
            using var command = Command("UPDATE shared_documents SET deleted=$deleted,updated_at=$now WHERE id=$id", ("$deleted", deleted), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", id)); command.ExecuteNonQuery();
        }
    }
    public SharedDocumentData ApplySharedUpdate(string actor, string document, byte[] update, byte[]? vector = null)
    {
        if (update.Length is 0 or > SharedDocumentReplica.MaximumStateBytes) throw new InvalidDataException("协同更新超过限制");
        lock (_gate)
        {
            var current = SharedDocument(actor, document); if (!SharedProtocol.CanWrite(current.Role)) throw new SharedAccessException("此工作区仅可阅读");
            // Validate on a disposable candidate. Rejected CRDT transactions never touch the durable state or a live room.
            using var replica = new SharedDocumentReplica(current.State); var before = replica.Read(); replica.Apply(update); var after = replica.Read();
            if (vector != null && !SharedProtocol.Covers(replica.StateVector(), vector)) throw new SharedAccessException("resync", 409);
            SharedDocumentReplica.ValidateTree(after.Root, after.Title); var state = replica.State();
            if (state.Length > SharedDocumentReplica.MaximumStateBytes) throw new InvalidDataException("文档协同历史超过 16 MB，请导出后拆分文档");
            ValidateCommentChanges(actor, document, current.Role, before.Root, after.Root);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); using var transaction = _database.BeginTransaction();
            using var command = Command("UPDATE shared_documents SET title=$title,state=$state,updated_at=$now WHERE id=$id", ("$title", after.Title), ("$state", state), ("$now", now), ("$id", document));
            command.Transaction = transaction; command.ExecuteNonQuery();
            foreach (var thread in NoteComments.For(after.Root).Threads)
            foreach (var message in thread.Messages)
            {
                command.Parameters.Clear(); command.CommandText = "INSERT OR IGNORE INTO shared_comment_ledger(document_id,message_id,body_hash) VALUES($doc,$message,$hash)";
                command.Parameters.AddWithValue("$doc", document); command.Parameters.AddWithValue("$message", message.Id.ToString("D")); command.Parameters.AddWithValue("$hash", MessageHash(thread.Id, message)); command.ExecuteNonQuery();
            }
            transaction.Commit(); return new(current.Document with { Title = after.Title, UpdatedAt = now }, current.Role, state);
        }
    }
    private static string MessageHash(Guid thread, CommentMessage message) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(thread.ToString("D") + ":" + JsonSerializer.Serialize(message, SyncProtocol.Json))));
    private void ValidateCommentChanges(string actor, string document, string role, NoteNode before, NoteNode after)
    {
        var old = NoteComments.For(before).Threads.ToDictionary(thread => thread.Id); var next = NoteComments.For(after).Threads.ToDictionary(thread => thread.Id); var profile = Profile(actor);
        bool Own(CommentMessage message) => message.AuthorId == actor;
        foreach (var thread in old.Values)
            if (!next.ContainsKey(thread.Id) && !Own(thread.Messages[0]) && role != "owner") throw new SharedAccessException("只能删除自己的讨论");
        foreach (var thread in next.Values)
        {
            var previous = old.GetValueOrDefault(thread.Id); var messages = previous?.Messages.ToDictionary(message => message.Id) ?? [];
            if (previous != null && previous.Messages[0].Id != thread.Messages[0].Id) throw new SharedAccessException("不能替换讨论首条消息");
            if (messages.Keys.Except(thread.Messages.Select(message => message.Id)).Any()) throw new SharedAccessException("删除回复必须保留回复关系");
            foreach (var message in thread.Messages)
            {
                if (messages.TryGetValue(message.Id, out var original))
                {
                    if (message == original) continue;
                    if (message.AuthorId != original.AuthorId || message.Author != original.Author || message.CreatedAt != original.CreatedAt || message.ReplyTo != original.ReplyTo)
                        throw new SharedAccessException("不能更改消息作者或回复对象");
                    if (!Own(original) && !(role == "owner" && message.Deleted && message.Text == "")) throw new SharedAccessException("只能编辑自己的消息");
                }
                else
                {
                    if (Own(message) && message.Author == profile.DisplayName) continue;
                    // Undoing an authorized thread deletion may restore peer messages verbatim, but may never invent them.
                    using var command = Command("SELECT 1 FROM shared_comment_ledger WHERE document_id=$doc AND message_id=$message AND body_hash=$hash", ("$doc", document), ("$message", message.Id.ToString("D")), ("$hash", MessageHash(thread.Id, message)));
                    if (previous != null || command.ExecuteScalar() == null || !Own(thread.Messages[0]) && role != "owner") throw new SharedAccessException("评论作者必须是当前账号");
                }
            }
        }
    }
}
