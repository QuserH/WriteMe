using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

// Note: 本地修改和同步前沿同事务保存，冲突版本可显式恢复 — 见 .agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md
public sealed partial class NoteStore
{
    private bool _syncReady;
    private string _deviceId = "";
    public string DeviceId => _deviceId;

    private void InitializeSync()
    {
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE IF NOT EXISTS sync_items(entity_key TEXT PRIMARY KEY,state TEXT NOT NULL,dirty INTEGER NOT NULL DEFAULT 1)";
            command.ExecuteNonQuery();
        }
        _deviceId = ImportedLegacy ? Guid.NewGuid().ToString("N") : Setting("sync_device_id") ?? Guid.NewGuid().ToString("N");
        SetSetting("sync_device_id", _deviceId); _syncReady = true;
        if (ImportedLegacy)
        {
            SetSetting("sync_credentials", "");
            SetSetting("sync_counter", "0");
            MarkAllForSync();
        }
        if (Setting("sync_seed_version") == "1") return;
        var documents = List().Concat(Query(new(Mode: "trash")).Select(result => result.Document)).ToArray();
        var spaces = Spaces(); var folders = Folders();
        using var transaction = _connection.BeginTransaction();
        foreach (var space in spaces) RecordLibraryChange("space", space.Id, transaction, false);
        foreach (var folder in folders) RecordLibraryChange("folder", folder.Id, transaction, false);
        foreach (var document in documents) RecordDocumentChange(document.Id, transaction, false);
        Sql(transaction, "INSERT OR REPLACE INTO library_state(key,value) VALUES('sync_seed_version','1')"); transaction.Commit();
    }

    private SyncEntity? ReadSync(string key, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT state FROM sync_items WHERE entity_key=$key"; command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? SyncProtocol.Read<SyncEntity>(json) : null;
    }
    public SyncEntity? SyncState(string key) { lock (_gate) return ReadSync(key); }

    partial void RecordDocumentChange(string id, SqliteTransaction transaction, bool deleted)
    {
        if (!_syncReady) return;
        WriteLocal("document/" + id, deleted ? null : SyncProtocol.Encode(DocumentPayload(id, transaction)), transaction);
    }
    partial void RecordLibraryChange(string kind, string id, SqliteTransaction transaction, bool deleted)
    {
        if (!_syncReady) return;
        string? value = null;
        if (!deleted)
        {
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = kind == "space" ? "SELECT id,name FROM spaces WHERE id=$id" : "SELECT id,space_id,parent_id,name FROM folders WHERE id=$id";
            command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("同步容器不存在");
            value = kind == "space" ? SyncProtocol.Encode(new SpaceInfo(reader.GetString(0), reader.GetString(1)))
                : SyncProtocol.Encode(new FolderInfo(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }
        WriteLocal(kind + "/" + id, value, transaction);
    }

    private SyncDocumentPayload DocumentPayload(string id, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            SELECT d.title,d.content,d.created_at,d.updated_at,COALESCE(m.favorite,0),l.space_id,l.folder_id,l.deleted_at,l.daily_date,a.value
            FROM documents d JOIN document_locations l ON l.document_id=d.id LEFT JOIN document_metadata m ON m.document_id=d.id
            LEFT JOIN document_appearance a ON a.document_id=d.id WHERE d.id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        PortableDocument document; NoteNode root;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new KeyNotFoundException("同步笔记不存在");
            root = NoteJson.Parse(reader.GetString(1));
            document = new(id, reader.GetString(0), NoteJson.Serialize(root), reader.GetInt64(2), reader.GetInt64(3), reader.GetBoolean(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7) != 0, reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? new() : SyncProtocol.Read<PageAppearance>(reader.GetString(9)));
        }
        var assets = new List<AssetInfo>();
        foreach (var assetId in DocumentAssets.Read(root, document.Appearance))
        {
            command.CommandText = "SELECT name,media_type,size FROM assets WHERE id=$asset";
            command.Parameters.Clear(); command.Parameters.AddWithValue("$asset", assetId);
            using var reader = command.ExecuteReader();
            if (reader.Read()) assets.Add(new(assetId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            else
            {
                var node = NoteTree.Descendants(root).FirstOrDefault(node => node.String("assetId") == assetId);
                var name = Path.GetFileName((node?.String("name") ?? "封面.png").Replace('\\', '/'));
                assets.Add(new(assetId, name, node?.String("mimeType") ?? "image/png", node?.Int("size") ?? 0));
            }
        }
        return new(document, assets.ToArray());
    }

    private SyncEntity WriteLocal(string key, string? payload, SqliteTransaction transaction, bool resolving = false)
    {
        var previous = ReadSync(key, transaction) ?? new(key, []);
        if (!resolving && SyncProtocol.HasConflict(previous)) throw new InvalidOperationException("此项目有同步冲突，请先在同步面板中选择要保留的版本");
        if (previous.Versions.Length == 1 && previous.Versions[0].Payload == payload) return previous;
        var clock = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var version in previous.Versions)
            foreach (var (actor, tick) in version.Clock) clock[actor] = Math.Max(clock.GetValueOrDefault(actor), tick);
        using var counter = _connection.CreateCommand(); counter.Transaction = transaction; counter.CommandText = "SELECT value FROM library_state WHERE key='sync_counter'";
        var tickNow = checked(Math.Max(long.TryParse(counter.ExecuteScalar() as string, out var saved) ? saved : 0, clock.GetValueOrDefault(_deviceId)) + 1);
        clock[_deviceId] = tickNow;
        var next = new SyncEntity(key, [new(_deviceId + ":" + tickNow, clock, payload)]);
        SyncProtocol.Validate(next);
        Sql(transaction, "INSERT OR REPLACE INTO library_state(key,value) VALUES('sync_counter',$counter)", ("$counter", tickNow.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Sql(transaction, "INSERT INTO sync_items(entity_key,state,dirty) VALUES($key,$state,1) ON CONFLICT(entity_key) DO UPDATE SET state=excluded.state,dirty=1", ("$key", key), ("$state", SyncProtocol.Canonical(next)));
        return next;
    }

    public IReadOnlyList<SyncEntity> PendingSync()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT state FROM sync_items WHERE dirty=1 ORDER BY entity_key";
            using var reader = command.ExecuteReader(); var result = new List<SyncEntity>(); var bytes = 0;
            while (reader.Read())
            {
                var state = reader.GetString(0); var size = Encoding.UTF8.GetByteCount(state);
                if (result.Count == 0 && size > SyncProtocol.MaximumBatchBytes - 65536) throw new InvalidDataException("待同步文档超过单批大小限制");
                if (result.Count == 100 || bytes + size > SyncProtocol.MaximumBatchBytes - 65536) break;
                bytes += size; result.Add(SyncProtocol.Read<SyncEntity>(state));
            }
            return result;
        }
    }
    public int PendingSyncCount
    {
        get { lock (_gate) { using var command = _connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sync_items WHERE dirty=1"; return Convert.ToInt32(command.ExecuteScalar()); } }
    }
    public IReadOnlyList<AssetInfo> MissingSyncAssets()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT state FROM sync_items WHERE entity_key LIKE 'document/%'";
            using var reader = command.ExecuteReader(); var entities = new List<SyncEntity>();
            while (reader.Read()) entities.Add(SyncProtocol.Read<SyncEntity>(reader.GetString(0)));
            return SyncProtocol.Assets(entities).Where(asset => AssetPath(asset.Id) == null).ToArray();
        }
    }
    public void MarkAllForSync() { lock (_gate) { using var command = _connection.CreateCommand(); command.CommandText = "UPDATE sync_items SET dirty=1"; command.ExecuteNonQuery(); } }
    private static string CursorKey(string target) => "sync_cursor_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target)));
    public long SyncCursor(string target) => long.TryParse(Setting(CursorKey(target)), out var value) ? Math.Max(0, value) : 0;
    public void ResetSyncCursor(string target) { SetSetting(CursorKey(target), "0"); MarkAllForSync(); }

    public IReadOnlySet<string> ApplySync(string target, SyncResponse response)
    {
        if (response.Cursor < 0 || response.Entities == null || response.Entities.Length > 1000 || response.Entities.Any(entity => entity == null) || response.Entities.Select(entity => entity.Key).Distinct(StringComparer.Ordinal).Count() != response.Entities.Length)
            throw new InvalidDataException("同步响应无效");
        foreach (var entity in response.Entities) SyncProtocol.Validate(entity);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction(); var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var received in response.Entities)
            {
                var existing = ReadSync(received.Key, transaction) ?? new(received.Key, []);
                var merged = SyncProtocol.Merge(existing, received); var state = SyncProtocol.Canonical(merged);
                if (state != SyncProtocol.Canonical(existing)) changed.Add(received.Key);
                Sql(transaction, "INSERT INTO sync_items(entity_key,state,dirty) VALUES($key,$state,$dirty) ON CONFLICT(entity_key) DO UPDATE SET state=excluded.state,dirty=excluded.dirty",
                    ("$key", received.Key), ("$state", state), ("$dirty", state == SyncProtocol.Canonical(received) ? 0 : 1));
            }
            Materialize(changed, transaction);
            Sql(transaction, "INSERT OR REPLACE INTO library_state(key,value) VALUES($key,$value)", ("$key", CursorKey(target)), ("$value", response.Cursor.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            transaction.Commit(); return changed;
        }
    }

    private void Materialize(IReadOnlySet<string> changed, SqliteTransaction transaction)
    {
        if (changed.Any(key => !key.StartsWith("document/", StringComparison.Ordinal))) MaterializeContainers(transaction);
        foreach (var key in changed.Where(key => key.StartsWith("document/", StringComparison.Ordinal)))
        {
            var state = ReadSync(key, transaction)!; var id = SyncProtocol.Subject(key).Id;
            if (SyncProtocol.Preferred(state)?.Payload is not { } json)
            {
                Sql(transaction, "DELETE FROM documents WHERE id=$id", ("$id", id)); continue;
            }
            var payload = SyncProtocol.Read<SyncDocumentPayload>(json); var document = payload.Document; var root = NoteJson.ParseStrict(document.Content);
            CaptureRevision(id, transaction, true);
            var space = ResolveSpace(document.SpaceId, transaction);
            string? folder = null;
            if (document.FolderId != null)
            {
                using var find = _connection.CreateCommand(); find.Transaction = transaction;
                find.CommandText = "SELECT id FROM folders WHERE id=$id AND space_id=$space"; find.Parameters.AddWithValue("$id", document.FolderId); find.Parameters.AddWithValue("$space", space);
                folder = find.ExecuteScalar() as string;
            }
            Sql(transaction, """
                INSERT INTO documents(id,title,content,created_at,updated_at) VALUES($id,$title,$content,$created,$updated)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,content=excluded.content,created_at=excluded.created_at,updated_at=excluded.updated_at
                """, ("$id", id), ("$title", document.Title), ("$content", document.Content), ("$created", document.CreatedAt), ("$updated", document.UpdatedAt));
            // Daily identity collisions retain both notes; only one owns the quick-open date.
            using var daily = _connection.CreateCommand(); daily.Transaction = transaction;
            daily.CommandText = "SELECT document_id FROM document_locations WHERE space_id=$space AND daily_date=$date AND document_id<>$id";
            daily.Parameters.AddWithValue("$space", space); daily.Parameters.AddWithValue("$date", (object?)document.DailyDate ?? DBNull.Value); daily.Parameters.AddWithValue("$id", id);
            var date = document.DailyDate;
            if (daily.ExecuteScalar() is string collision)
            {
                if (StringComparer.Ordinal.Compare(id, collision) < 0) Sql(transaction, "UPDATE document_locations SET daily_date=NULL WHERE document_id=$id", ("$id", collision));
                else date = null;
            }
            Sql(transaction, """
                INSERT INTO document_locations(document_id,space_id,folder_id,deleted_at,daily_date) VALUES($id,$space,$folder,$deleted,$date)
                ON CONFLICT(document_id) DO UPDATE SET space_id=excluded.space_id,folder_id=excluded.folder_id,deleted_at=excluded.deleted_at,daily_date=excluded.daily_date
                """, ("$id", id), ("$space", space), ("$folder", folder), ("$deleted", document.Deleted ? Math.Max(1, document.UpdatedAt) : 0), ("$date", date));
            Sql(transaction, "INSERT INTO document_metadata(document_id,favorite) VALUES($id,$favorite) ON CONFLICT(document_id) DO UPDATE SET favorite=excluded.favorite", ("$id", id), ("$favorite", document.Favorite ? 1 : 0));
            Sql(transaction, "INSERT INTO document_appearance(document_id,value) VALUES($id,$value) ON CONFLICT(document_id) DO UPDATE SET value=excluded.value", ("$id", id), ("$value", SyncProtocol.Encode(document.Appearance)));
            foreach (var asset in payload.Assets) Sql(transaction, "INSERT OR IGNORE INTO assets(id,name,media_type,size) VALUES($id,$name,$mime,$size)", ("$id", asset.Id), ("$name", asset.Name), ("$mime", asset.MediaType), ("$size", asset.Size));
            ReplaceReferences(id, NoteReferences.Read(root), transaction); ReplaceSearch(id, document.Title, root, transaction);
        }
    }

    private string ResolveSpace(string id, SqliteTransaction transaction)
    {
        if (ReadSync("space/" + id, transaction) is { } state && SyncProtocol.Preferred(state)?.Payload == null) id = "personal";
        Sql(transaction, "INSERT OR IGNORE INTO spaces(id,name) VALUES($id,$name)", ("$id", id), ("$name", id == "personal" ? "我的空间" : "同步空间")); return id;
    }
    private void MaterializeContainers(SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT state FROM sync_items WHERE entity_key LIKE 'space/%' OR entity_key LIKE 'folder/%'";
        var states = new List<SyncEntity>(); using (var reader = command.ExecuteReader()) while (reader.Read()) states.Add(SyncProtocol.Read<SyncEntity>(reader.GetString(0)));
        var folders = new Dictionary<string, FolderInfo>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var (kind, id) = SyncProtocol.Subject(state.Key); var value = SyncProtocol.Preferred(state)?.Payload;
            if (value == null)
            {
                if (kind == "folder") Sql(transaction, "UPDATE document_locations SET folder_id=NULL WHERE folder_id=$id; UPDATE folders SET parent_id=NULL WHERE parent_id=$id; DELETE FROM folders WHERE id=$id", ("$id", id));
                continue;
            }
            if (kind == "space")
            {
                var space = SyncProtocol.Read<SpaceInfo>(value);
                Sql(transaction, "INSERT INTO spaces(id,name) VALUES($id,$name) ON CONFLICT(id) DO UPDATE SET name=excluded.name", ("$id", space.Id), ("$name", space.Name));
            }
            else folders[id] = SyncProtocol.Read<FolderInfo>(value);
        }
        foreach (var folder in folders.Values)
        {
            var space = ResolveSpace(folder.SpaceId, transaction);
            Sql(transaction, "INSERT INTO folders(id,space_id,parent_id,name) VALUES($id,$space,NULL,$name) ON CONFLICT(id) DO UPDATE SET space_id=excluded.space_id,parent_id=NULL,name=excluded.name", ("$id", folder.Id), ("$space", space), ("$name", folder.Name));
        }
        foreach (var folder in folders.Values)
        {
            var parent = folder.ParentId;
            if (parent == null || !folders.TryGetValue(parent, out var parentFolder) || parentFolder.SpaceId != folder.SpaceId) continue;
            var path = new List<string> { folder.Id }; var cursor = parent;
            while (cursor != null && folders.TryGetValue(cursor, out var ancestor))
            {
                var loop = path.IndexOf(cursor);
                if (loop >= 0)
                {
                    if (path.Skip(loop).Min(StringComparer.Ordinal) == folder.Id) parent = null;
                    break;
                }
                path.Add(cursor); cursor = ancestor.ParentId;
            }
            Sql(transaction, "UPDATE folders SET parent_id=$parent WHERE id=$id", ("$id", folder.Id), ("$parent", parent));
        }
        foreach (var state in states.Where(state => state.Key.StartsWith("space/", StringComparison.Ordinal) && state.Key != "space/personal" && SyncProtocol.Preferred(state)?.Payload == null))
        {
            var id = SyncProtocol.Subject(state.Key).Id;
            Sql(transaction, "UPDATE document_locations SET space_id='personal',folder_id=NULL,daily_date=NULL WHERE space_id=$id; UPDATE folders SET space_id='personal',parent_id=NULL WHERE space_id=$id; DELETE FROM spaces WHERE id=$id", ("$id", id));
        }
        // A document can arrive before its folder on a paginated pull. Rebind from its retained snapshot.
        command.CommandText = "SELECT state FROM sync_items WHERE entity_key LIKE 'document/%'";
        var documents = new List<SyncEntity>(); using (var reader = command.ExecuteReader()) while (reader.Read()) documents.Add(SyncProtocol.Read<SyncEntity>(reader.GetString(0)));
        foreach (var state in documents)
        {
            if (SyncProtocol.Preferred(state)?.Payload is not { } json) continue;
            var document = SyncProtocol.Read<SyncDocumentPayload>(json).Document;
            if (document.FolderId != null && folders.TryGetValue(document.FolderId, out var folder) && folder.SpaceId == document.SpaceId)
                Sql(transaction, "UPDATE document_locations SET folder_id=$folder WHERE document_id=$id AND space_id=$space", ("$id", document.Id), ("$space", ResolveSpace(document.SpaceId, transaction)), ("$folder", folder.Id));
        }
    }

    public IReadOnlyList<SyncConflict> SyncConflicts()
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT state FROM sync_items";
            using var reader = command.ExecuteReader(); var result = new List<SyncConflict>();
            while (reader.Read())
            {
                var entity = SyncProtocol.Read<SyncEntity>(reader.GetString(0)); if (!SyncProtocol.HasConflict(entity)) continue;
                var value = SyncProtocol.Preferred(entity)?.Payload;
                var (kind, id) = SyncProtocol.Subject(entity.Key);
                var title = value == null ? id : kind switch { "document" => SyncProtocol.Read<SyncDocumentPayload>(value).Document.Title, "space" => SyncProtocol.Read<SpaceInfo>(value).Name, _ => SyncProtocol.Read<FolderInfo>(value).Name };
                result.Add(new(NoteReferences.DisplayTitle(title), entity));
            }
            return result;
        }
    }
    public void ResolveSyncConflict(SyncEntity expected, string versionId)
    {
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            var current = ReadSync(expected.Key, transaction) ?? throw new KeyNotFoundException("冲突已经不存在");
            if (SyncProtocol.Canonical(expected) != SyncProtocol.Canonical(current)) throw new InvalidOperationException("冲突版本已更新，请重新打开并选择");
            var selected = current.Versions.Single(version => version.Id == versionId);
            var (kind, id) = SyncProtocol.Subject(current.Key);
            if (kind == "document")
            {
                foreach (var version in current.Versions.Where(version => version.Payload != null))
                {
                    var doc = SyncProtocol.Read<SyncDocumentPayload>(version.Payload!).Document;
                    Sql(transaction, "INSERT INTO document_revisions(document_id,saved_at,title,content) SELECT id,$now,$title,$content FROM documents WHERE id=$id", ("$id", id), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$title", doc.Title), ("$content", doc.Content));
                }
            }
            WriteLocal(current.Key, selected.Payload, transaction, resolving: true); Materialize(new HashSet<string> { current.Key }, transaction); transaction.Commit();
        }
    }
}
