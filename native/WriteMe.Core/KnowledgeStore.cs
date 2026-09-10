using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

// Note: 正文与派生关联索引同事务保存，收藏独立于正文历史 — 见 .agents/notes/implemented/feature/2026-09-09-m3-note-connections.md
public sealed partial class NoteStore
{
    private void InitializeKnowledge()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS document_metadata (
                document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
                favorite INTEGER NOT NULL DEFAULT 0 CHECK(favorite IN (0,1))
            );
            CREATE TABLE IF NOT EXISTS document_tags (
                document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                tag_key TEXT NOT NULL, tag_name TEXT NOT NULL,
                PRIMARY KEY(document_id,tag_key)
            );
            CREATE INDEX IF NOT EXISTS idx_document_tags_key ON document_tags(tag_key);
            CREATE TABLE IF NOT EXISTS document_links (
                source_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                target_id TEXT NOT NULL, block_path TEXT NOT NULL,
                start INTEGER NOT NULL, length INTEGER NOT NULL, label TEXT NOT NULL, preview TEXT NOT NULL,
                PRIMARY KEY(source_id,block_path,start,target_id)
            );
            CREATE INDEX IF NOT EXISTS idx_document_links_target ON document_links(target_id);
            CREATE TABLE IF NOT EXISTS library_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM library_state WHERE key='reference_index_version'";
        if (!Equals(command.ExecuteScalar(), "1"))
        {
            command.CommandText = "SELECT id,content FROM documents";
            var documents = new List<(string Id, string Content)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) documents.Add((reader.GetString(0), reader.GetString(1)));
            foreach (var document in documents) ReplaceReferences(document.Id, NoteReferences.Read(NoteJson.Parse(document.Content)), transaction);
            command.CommandText = "INSERT INTO library_state(key,value) VALUES('reference_index_version','1') ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void ReplaceReferences(string id, IReadOnlyList<IndexedReference> references, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM document_tags WHERE document_id=$id; DELETE FROM document_links WHERE source_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        foreach (var entry in references)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$target", entry.Span.Target);
            command.Parameters.AddWithValue("$label", entry.Span.Label);
            if (entry.Span.Kind == ReferenceKind.Tag)
                command.CommandText = "INSERT OR IGNORE INTO document_tags(document_id,tag_key,tag_name) VALUES($id,$target,$label)";
            else
            {
                command.CommandText = "INSERT INTO document_links(source_id,target_id,block_path,start,length,label,preview) VALUES($id,$target,$path,$start,$length,$label,$preview)";
                command.Parameters.AddWithValue("$path", entry.Path);
                command.Parameters.AddWithValue("$start", entry.Span.Start);
                command.Parameters.AddWithValue("$length", entry.Span.Length);
                command.Parameters.AddWithValue("$preview", entry.Preview);
            }
            command.ExecuteNonQuery();
        }
    }

    public void SetFavorite(string id, bool favorite)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO document_metadata(document_id,favorite) SELECT id,$favorite FROM documents WHERE id=$id ON CONFLICT(document_id) DO UPDATE SET favorite=excluded.favorite";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("文档不存在");
            RecordDocumentChange(id, transaction, false);
            transaction.Commit();
        }
    }

    public IReadOnlyList<TagInfo> Tags(string? documentId = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT tag_key, MIN(tag_name), COUNT(*) FROM document_tags t JOIN document_locations l ON l.document_id=t.document_id WHERE l.deleted_at=0 AND ($id IS NULL OR t.document_id=$id) GROUP BY tag_key ORDER BY tag_key";
            command.Parameters.AddWithValue("$id", (object?)documentId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var result = new List<TagInfo>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return result;
        }
    }

    public IReadOnlySet<string> DocumentsWithTag(string tag)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT document_id FROM document_tags WHERE tag_key=$key";
            command.Parameters.AddWithValue("$key", NoteReferences.TagKey(tag));
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    public IReadOnlyList<DocumentRelation> Backlinks(string documentId) => Relations(documentId, true);
    public IReadOnlyList<DocumentRelation> OutgoingLinks(string documentId) => Relations(documentId, false);
    private IReadOnlyList<DocumentRelation> Relations(string id, bool incoming)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = incoming
                ? "SELECT d.id,d.title,l.block_path,l.start,l.length,l.preview,1 FROM document_links l JOIN documents d ON d.id=l.source_id JOIN document_locations location ON location.document_id=d.id AND location.deleted_at=0 WHERE l.target_id=$id AND l.source_id<>$id ORDER BY d.updated_at DESC,l.rowid"
                : "SELECT l.target_id,COALESCE(d.title,l.label),l.block_path,l.start,l.length,l.preview,d.id IS NOT NULL AND location.deleted_at=0 FROM document_links l LEFT JOIN documents d ON d.id=l.target_id LEFT JOIN document_locations location ON location.document_id=d.id WHERE l.source_id=$id ORDER BY l.rowid";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            var result = new List<DocumentRelation>();
            while (reader.Read()) result.Add(new(reader.GetString(0), NoteReferences.DisplayTitle(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5), reader.GetBoolean(6)));
            return result;
        }
    }
}
