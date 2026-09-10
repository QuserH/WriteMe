using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

public sealed record DocumentInfo(string Id, string Title, long CreatedAt, long UpdatedAt, bool IsFavorite = false,
    string SpaceId = "personal", string? FolderId = null, long LastOpenedAt = 0, bool IsDeleted = false);
public sealed record StoredDocument(string Id, string Title, string Content, long CreatedAt, long UpdatedAt, bool IsFavorite = false);

// Note: 原生版使用独立数据库和 SQLite 在线备份导入 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
public sealed partial class NoteStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    public string DatabasePath { get; }
    public bool ImportedLegacy { get; }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.writeme.native");
    public static string LegacyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "com.writeme.desktop", "writeme.db");

    public NoteStore(string dataDirectory, string? legacyPath = null)
    {
        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(Path.GetFullPath(dataDirectory), "writeme.db");
        if (!File.Exists(DatabasePath) && legacyPath != null && File.Exists(legacyPath))
        {
            var temporary = DatabasePath + ".import-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = legacyPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
                {
                    source.Open();
                    destination.Open();
                    source.BackupDatabase(destination);
                    using var check = destination.CreateCommand();
                    check.CommandText = "PRAGMA quick_check";
                    if (!Equals(check.ExecuteScalar(), "ok")) throw new InvalidDataException("旧笔记副本未通过完整性检查");
                    check.CommandText = "SELECT id, title, content, created_at, updated_at FROM documents LIMIT 0";
                    using var reader = check.ExecuteReader();
                }
                File.Move(temporary, DatabasePath);
                ImportedLegacy = true;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false, DefaultTimeout = 10 }.ToString());
        _connection.Open();
        using var initialize = _connection.CreateCommand();
        initialize.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY, title TEXT NOT NULL DEFAULT '', content TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_documents_updated ON documents(updated_at DESC);
            """;
        initialize.ExecuteNonQuery();
        try { InitializeKnowledge(); InitializeLibrary(); InitializeWorkspace(); InitializeSync(); }
        catch { _connection.Dispose(); throw; }
    }

    public IReadOnlyList<DocumentInfo> List()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = InfoSelect + " WHERE COALESCE(l.deleted_at,0)=0 ORDER BY d.updated_at DESC, d.id";
            using var reader = command.ExecuteReader();
            var result = new List<DocumentInfo>();
            while (reader.Read()) result.Add(ReadInfo(reader));
            return result;
        }
    }

    public StoredDocument Get(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT d.id, d.title, d.content, d.created_at, d.updated_at, COALESCE(m.favorite,0) FROM documents d LEFT JOIN document_metadata m ON m.document_id=d.id WHERE d.id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("文档不存在");
            return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetBoolean(5));
        }
    }

    public StoredDocument Create(string title = "", NoteNode? content = null, string spaceId = "personal", string? folderId = null)
    {
        var root = content ?? NoteNode.EmptyDocument();
        var json = NoteJson.Serialize(root);
        var references = NoteReferences.Read(root);
        lock (_gate)
        {
            ValidateLocation(spaceId, folderId);
            var id = Guid.NewGuid().ToString();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO documents(id,title,content,created_at,updated_at) VALUES($id,$title,$content,$now,$now)";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$content", json);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
            InsertLocation(id, spaceId, folderId, transaction);
            ReplaceReferences(id, references, transaction);
            ReplaceSearch(id, title, root, transaction);
            RecordDocumentChange(id, transaction, false);
            transaction.Commit();
            return Get(id);
        }
    }

    public void Save(string id, string title, NoteNode content)
    {
        var json = NoteJson.Serialize(content);
        var references = NoteReferences.Read(content);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT title,content FROM documents WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new IOException("保存失败：文档已不存在");
                if (reader.GetString(0) == title && NoteJson.Serialize(NoteJson.Parse(reader.GetString(1))) == json) return;
            }
            CaptureRevision(id, transaction);
            command.CommandText = "UPDATE documents SET title=$title,content=$content,updated_at=MAX(updated_at+1,$now) WHERE id=$id";
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$content", json);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            if (command.ExecuteNonQuery() != 1) throw new IOException("保存失败：文档已不存在");
            ReplaceReferences(id, references, transaction);
            ReplaceSearch(id, title, content, transaction);
            RecordDocumentChange(id, transaction, false);
            transaction.Commit();
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM documents WHERE id=$id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            RecordDocumentChange(id, transaction, true);
            transaction.Commit();
        }
    }

    partial void RecordDocumentChange(string id, SqliteTransaction transaction, bool deleted);
    partial void RecordLibraryChange(string kind, string id, SqliteTransaction transaction, bool deleted);
    public void Dispose() { lock (_gate) _connection.Dispose(); }
}
