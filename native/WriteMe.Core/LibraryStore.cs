using System.Text;
using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

public sealed record SpaceInfo(string Id, string Name);
public sealed record FolderInfo(string Id, string SpaceId, string? ParentId, string Name);
public sealed record DocumentLocation(string SpaceId, string? FolderId, long LastOpenedAt, bool IsDeleted, string? DailyDate);
public sealed record LibraryQuery(string? SpaceId = null, string? FolderId = null, string Mode = "all", string? Tag = null, string Text = "");
public sealed record LibraryResult(DocumentInfo Document, string Preview = "", string? Path = null, int Start = 0, int Length = 0);
public sealed record SearchBlock(string Path, string Text);

public static class DocumentText
{
    public static IReadOnlyList<SearchBlock> Blocks(NoteNode root)
    {
        var blocks = new List<SearchBlock>();
        void Walk(NoteNode node, string path)
        {
            if (node.IsTextBlock) { blocks.Add(new(path, RichText.Plain(node).Replace('\u2028', '\n'))); return; }
            if (node.Type is "image" or "attachment") { blocks.Add(new(path, node.String("name") ?? node.String("alt") ?? "附件")); return; }
            if (node.Type is not ("doc" or "toggleBlock" or "blockquote" or "bulletList" or "orderedList" or "taskList" or "listItem" or "taskItem")) return;
            for (var i = 0; i < node.Content.Length; i++) Walk(node.Content[i], path.Length == 0 ? i.ToString() : path + "/" + i);
        }
        Walk(root, "");
        return blocks;
    }

    public static string Plain(NoteNode root) => string.Join('\n', Blocks(root).Select(block => block.Text));
}

// Note: 空间位置与中文全文索引独立于正文、同事务更新 — 见 .agents/notes/implemented/feature/2026-09-10-library-and-search.md
public sealed partial class NoteStore
{
    private const string InfoSelect = """
        SELECT d.id,d.title,d.created_at,d.updated_at,COALESCE(m.favorite,0),COALESCE(l.space_id,'personal'),l.folder_id,
            COALESCE(l.last_opened_at,0),COALESCE(l.deleted_at,0)
        FROM documents d LEFT JOIN document_metadata m ON m.document_id=d.id LEFT JOIN document_locations l ON l.document_id=d.id
        """;

    private static DocumentInfo ReadInfo(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4),
        reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8) != 0);

    private void InitializeLibrary()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS spaces(id TEXT PRIMARY KEY,name TEXT NOT NULL);
            INSERT OR IGNORE INTO spaces(id,name) VALUES('personal','我的空间');
            CREATE TABLE IF NOT EXISTS folders(
                id TEXT PRIMARY KEY,space_id TEXT NOT NULL REFERENCES spaces(id),
                parent_id TEXT REFERENCES folders(id) ON DELETE SET NULL,name TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_folders_parent ON folders(space_id,parent_id);
            CREATE TABLE IF NOT EXISTS document_locations(
                document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
                space_id TEXT NOT NULL REFERENCES spaces(id),folder_id TEXT REFERENCES folders(id) ON DELETE SET NULL,
                last_opened_at INTEGER NOT NULL DEFAULT 0,deleted_at INTEGER NOT NULL DEFAULT 0,daily_date TEXT);
            CREATE INDEX IF NOT EXISTS idx_document_locations_space ON document_locations(space_id,folder_id);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_daily_note ON document_locations(space_id,daily_date) WHERE daily_date IS NOT NULL;
            INSERT OR IGNORE INTO document_locations(document_id,space_id) SELECT id,'personal' FROM documents;
            CREATE VIRTUAL TABLE IF NOT EXISTS document_search USING fts5(document_id UNINDEXED,block_path UNINDEXED,title,body,tokenize='trigram');
            CREATE TRIGGER IF NOT EXISTS delete_document_search AFTER DELETE ON documents BEGIN
                DELETE FROM document_search WHERE document_id=OLD.id;
            END;
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT value FROM library_state WHERE key='search_index_version'";
        if (!Equals(command.ExecuteScalar(), "1"))
        {
            command.CommandText = "SELECT id,title,content FROM documents";
            var documents = new List<(string Id, string Title, string Content)>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) documents.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            foreach (var document in documents) ReplaceSearch(document.Id, document.Title, NoteJson.Parse(document.Content), transaction);
            command.CommandText = "INSERT OR REPLACE INTO library_state(key,value) VALUES('search_index_version','1')";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void ReplaceSearch(string id, string title, NoteNode root, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM document_search WHERE document_id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO document_search(document_id,block_path,title,body) VALUES($id,$path,$title,$body)";
        command.Parameters.AddWithValue("$title", title);
        var path = command.Parameters.AddWithValue("$path", "");
        var body = command.Parameters.AddWithValue("$body", "");
        var blocks = DocumentText.Blocks(root);
        foreach (var block in blocks.Count == 0 ? [new SearchBlock("", "")] : blocks)
        {
            path.Value = block.Path; body.Value = block.Text;
            command.ExecuteNonQuery();
        }
    }

    private void InsertLocation(string id, string spaceId, string? folderId, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO document_locations(document_id,space_id,folder_id) VALUES($id,$space,$folder)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void ValidateLocation(string spaceId, string? folderId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM spaces WHERE id=$space AND ($folder IS NULL OR EXISTS(SELECT 1 FROM folders WHERE id=$folder AND space_id=$space))";
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
        if (Convert.ToInt64(command.ExecuteScalar()) != 1) throw new ArgumentException("空间或文件夹不存在，或文件夹属于其他空间");
    }

    private static string ContainerName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new ArgumentException("名称需要 1–100 个可见字符");
        return name;
    }

    public IReadOnlyList<SpaceInfo> Spaces()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id,name FROM spaces ORDER BY CASE WHEN id='personal' THEN 0 ELSE 1 END,name,id";
            using var reader = command.ExecuteReader();
            var result = new List<SpaceInfo>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
            return result;
        }
    }

    public SpaceInfo CreateSpace(string name)
    {
        name = ContainerName(name);
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString();
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO spaces(id,name) VALUES($id,$name)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
            RecordLibraryChange("space", id, transaction, false); transaction.Commit();
            return new(id, name);
        }
    }

    public IReadOnlyList<FolderInfo> Folders(string? spaceId = null)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT id,space_id,parent_id,name FROM folders WHERE $space IS NULL OR space_id=$space ORDER BY name,id";
            command.Parameters.AddWithValue("$space", (object?)spaceId ?? DBNull.Value);
            using var reader = command.ExecuteReader();
            var result = new List<FolderInfo>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            return result;
        }
    }

    public FolderInfo CreateFolder(string spaceId, string name, string? parentId = null)
    {
        name = ContainerName(name);
        lock (_gate)
        {
            ValidateLocation(spaceId, parentId);
            var id = Guid.NewGuid().ToString();
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO folders(id,space_id,parent_id,name) VALUES($id,$space,$parent,$name)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$space", spaceId);
            command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value); command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery(); RecordLibraryChange("folder", id, transaction, false); transaction.Commit();
            return new(id, spaceId, parentId, name);
        }
    }

    public void RenameContainer(string id, string name, bool space)
    {
        name = ContainerName(name);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = space ? "UPDATE spaces SET name=$name WHERE id=$id" : "UPDATE folders SET name=$name WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("空间或文件夹不存在");
            RecordLibraryChange(space ? "space" : "folder", id, transaction, false); transaction.Commit();
        }
    }

    public void MoveFolder(string id, string? parentId)
    {
        lock (_gate)
        {
            var folders = Folders();
            var folder = folders.FirstOrDefault(item => item.Id == id) ?? throw new KeyNotFoundException("文件夹不存在");
            ValidateLocation(folder.SpaceId, parentId);
            var ancestor = parentId;
            var seen = new HashSet<string>();
            while (ancestor != null)
            {
                if (ancestor == id || !seen.Add(ancestor)) throw new ArgumentException("文件夹不能移入自己或自己的子文件夹");
                ancestor = folders.First(item => item.Id == ancestor).ParentId;
            }
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE folders SET parent_id=$parent WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
            command.ExecuteNonQuery(); RecordLibraryChange("folder", id, transaction, false); transaction.Commit();
        }
    }

    public void DeleteContainer(string id, bool space)
    {
        lock (_gate)
        {
            if (space && id == "personal") throw new ArgumentException("默认空间不能删除");
            var folders = Folders();
            var parent = space ? null : folders.FirstOrDefault(folder => folder.Id == id)?.ParentId;
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$parent", (object?)parent ?? DBNull.Value);
            command.CommandText = space ? "SELECT document_id FROM document_locations WHERE space_id=$id" : "SELECT document_id FROM document_locations WHERE folder_id=$id";
            var affected = new List<string>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) affected.Add(reader.GetString(0));
            command.CommandText = space
                ? "UPDATE document_locations SET space_id='personal',folder_id=NULL,daily_date=NULL WHERE space_id=$id; DELETE FROM folders WHERE space_id=$id; DELETE FROM spaces WHERE id=$id"
                : "UPDATE document_locations SET folder_id=$parent WHERE folder_id=$id; UPDATE folders SET parent_id=$parent WHERE parent_id=$id; DELETE FROM folders WHERE id=$id";
            command.ExecuteNonQuery();
            foreach (var document in affected) RecordDocumentChange(document, transaction, false);
            foreach (var folder in folders.Where(folder => space ? folder.SpaceId == id : folder.ParentId == id))
                RecordLibraryChange("folder", folder.Id, transaction, space);
            RecordLibraryChange(space ? "space" : "folder", id, transaction, true); transaction.Commit();
        }
    }

    public DocumentLocation Location(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT space_id,folder_id,last_opened_at,deleted_at,daily_date FROM document_locations WHERE document_id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("文档不存在");
            return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    public void MoveDocument(string id, string spaceId, string? folderId = null)
    {
        lock (_gate)
        {
            ValidateLocation(spaceId, folderId);
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE document_locations SET daily_date=CASE WHEN space_id=$space THEN daily_date ELSE NULL END,space_id=$space,folder_id=$folder WHERE document_id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$folder", (object?)folderId ?? DBNull.Value);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("文档不存在");
            RecordDocumentChange(id, transaction, false); transaction.Commit();
        }
    }

    public void MarkOpened(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE document_locations SET last_opened_at=MAX(last_opened_at+1,$now) WHERE document_id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); command.ExecuteNonQuery();
        }
    }

    public void SetTrashed(string id, bool deleted)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE document_locations SET deleted_at=$deleted WHERE document_id=$id";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$deleted", deleted ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0);
            if (command.ExecuteNonQuery() != 1) throw new KeyNotFoundException("文档不存在");
            RecordDocumentChange(id, transaction, false); transaction.Commit();
        }
    }

    public IReadOnlyList<LibraryResult> Query(LibraryQuery query)
    {
        lock (_gate)
        {
            var text = query.Text.Trim();
            if (text.Length > 500) text = text[..500];
            using var command = _connection.CreateCommand();
            var predicate = text.EnumerateRunes().Count() >= 3 ? "document_search MATCH $match" : "(instr(lower(title),lower($query))>0 OR instr(lower(body),lower($query))>0)";
            command.CommandText = InfoSelect + """
                 WHERE ($space IS NULL OR l.space_id=$space) AND (CASE WHEN $trash=1 THEN l.deleted_at<>0 ELSE l.deleted_at=0 END)
                 AND ($folder IS NULL OR l.folder_id IN (WITH RECURSIVE children(id) AS (SELECT $folder UNION SELECT f.id FROM folders f JOIN children c ON f.parent_id=c.id) SELECT id FROM children))
                 AND ($favorite=0 OR m.favorite=1) AND ($recent=0 OR l.last_opened_at>0)
                 AND ($tag IS NULL OR EXISTS(SELECT 1 FROM document_tags t WHERE t.document_id=d.id AND t.tag_key=$tag))
                """ + (text.Length == 0 ? "" : $" AND d.id IN (SELECT document_id FROM document_search WHERE {predicate})")
                + (query.Mode == "recent" ? " ORDER BY l.last_opened_at DESC,d.updated_at DESC,d.id" : " ORDER BY d.updated_at DESC,d.id");
            command.Parameters.AddWithValue("$space", (object?)query.SpaceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$folder", (object?)query.FolderId ?? DBNull.Value);
            command.Parameters.AddWithValue("$trash", query.Mode == "trash" ? 1 : 0);
            command.Parameters.AddWithValue("$favorite", query.Mode == "favorites" ? 1 : 0);
            command.Parameters.AddWithValue("$recent", query.Mode == "recent" ? 1 : 0);
            command.Parameters.AddWithValue("$tag", query.Tag == null ? DBNull.Value : NoteReferences.TagKey(query.Tag));
            command.Parameters.AddWithValue("$query", text);
            command.Parameters.AddWithValue("$match", "\"" + text.Replace("\"", "\"\"") + "\"");
            var documents = new List<DocumentInfo>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) documents.Add(ReadInfo(reader));
            if (text.Length == 0) return documents.Select(document => new LibraryResult(document)).ToArray();
            var result = new List<LibraryResult>();
            command.Parameters.AddWithValue("$id", "");
            command.CommandText = $"SELECT block_path,body FROM document_search WHERE document_id=$id AND {predicate} ORDER BY CASE WHEN instr(lower(body),lower($query))>0 THEN 0 ELSE 1 END,rowid LIMIT 1";
            foreach (var document in documents)
            {
                command.Parameters["$id"].Value = document.Id;
                using var reader = command.ExecuteReader();
                if (!reader.Read()) continue;
                var body = reader.GetString(1);
                var index = body.IndexOf(text, StringComparison.OrdinalIgnoreCase);
                var start = Math.Max(0, index - 30);
                var preview = body.Substring(start, Math.Min(130, body.Length - start)).Replace('\n', ' ');
                result.Add(new(document, (start > 0 ? "…" : "") + preview, index < 0 ? null : reader.GetString(0), Math.Max(0, index), index < 0 ? 0 : text.Length));
            }
            return result;
        }
    }
}
