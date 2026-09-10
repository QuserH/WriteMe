using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

public sealed record PortableDocument(string Id, string Title, string Content, long CreatedAt, long UpdatedAt, bool Favorite, string SpaceId, string? FolderId, bool Deleted, string? DailyDate, PageAppearance Appearance);
public sealed record LibraryBackup(int Version, SpaceInfo[] Spaces, FolderInfo[] Folders, PortableDocument[] Documents, AssetInfo[] Assets);

public sealed partial class NoteStore
{
    private void Sql(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public PortableDocument Portable(string id)
    {
        lock (_gate)
        {
            var document = Get(id); var location = Location(id);
            return new(id, document.Title, NoteJson.Serialize(NoteJson.Parse(document.Content)), document.CreatedAt, document.UpdatedAt, document.IsFavorite, location.SpaceId, location.FolderId, location.IsDeleted, location.DailyDate, Appearance(id));
        }
    }
    public void ExportBackup(Stream output)
    {
        LibraryBackup snapshot;
        lock (_gate)
        {
            var documents = List().Concat(Query(new(Mode: "trash")).Select(item => item.Document)).Select(info => Portable(info.Id)).ToArray();
            var assets = documents.SelectMany(document => DocumentAssets.Read(NoteJson.ParseStrict(document.Content), document.Appearance)).Distinct().Select(id => Asset(id) ?? throw new IOException("笔记引用的附件记录缺失")).ToArray();
            snapshot = new(1, Spaces().ToArray(), Folders().ToArray(), documents, assets);
        }
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, true, Encoding.UTF8);
        using (var manifest = archive.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(manifest, snapshot);
        foreach (var asset in snapshot.Assets)
        {
            var path = AssetPath(asset.Id) ?? throw new IOException("附件尚未下载，无法完整导出：" + asset.Name);
            using var source = File.OpenRead(path); using var target = archive.CreateEntry("assets/" + asset.Id, CompressionLevel.Fastest).Open(); source.CopyTo(target);
        }
    }

    public IReadOnlyList<string> ImportBackup(Stream input)
    {
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, true, Encoding.UTF8);
        if (archive.Entries.Count > 20000 || archive.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != archive.Entries.Count) throw new InvalidDataException("备份中的文件过多或包含重复路径");
        var manifest = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("找不到 WriteME 备份清单");
        if (manifest.Length > 128 * 1024 * 1024) throw new InvalidDataException("备份清单过大");
        LibraryBackup backup;
        using (var source = manifest.Open()) backup = JsonSerializer.Deserialize<LibraryBackup>(source) ?? throw new InvalidDataException("无效的备份清单");
        if (backup.Version != 1 || backup.Documents == null || backup.Spaces == null || backup.Folders == null || backup.Assets == null
            || backup.Documents.Length > 10000 || backup.Spaces.Length > 1000 || backup.Folders.Length > 10000 || backup.Assets.Length > 10000
            || backup.Documents.Any(document => document == null || !SyncProtocol.ValidId(document.Id))
            || backup.Spaces.Any(space => space == null || !SyncProtocol.ValidId(space.Id))
            || backup.Folders.Any(folder => folder == null || !SyncProtocol.ValidId(folder.Id))
            || backup.Assets.Any(asset => asset == null || asset.Id == null || !IsAssetId(asset.Id) || asset.Size is < 0 or > MaximumAssetSize))
            throw new InvalidDataException("备份版本不受支持或超出导入范围");
        if (backup.Assets.Sum(asset => asset.Size) > 1024L * 1024 * 1024
            || backup.Documents.Select(document => document.Id).Distinct(StringComparer.Ordinal).Count() != backup.Documents.Length
            || backup.Spaces.Select(space => space.Id).Distinct(StringComparer.Ordinal).Count() != backup.Spaces.Length
            || backup.Folders.Select(folder => folder.Id).Distinct(StringComparer.Ordinal).Count() != backup.Folders.Length
            || backup.Assets.Select(asset => asset.Id).Distinct(StringComparer.Ordinal).Count() != backup.Assets.Length
            || backup.Documents.Where(document => document.DailyDate != null).GroupBy(document => (document.SpaceId, document.DailyDate)).Any(group => group.Count() > 1))
            throw new InvalidDataException("备份过大或包含重复标识");
        var documentIds = backup.Documents.ToDictionary(document => document.Id, _ => Guid.NewGuid().ToString());
        var spaceIds = backup.Spaces.ToDictionary(space => space.Id, _ => Guid.NewGuid().ToString());
        var folderIds = backup.Folders.ToDictionary(folder => folder.Id, _ => Guid.NewGuid().ToString());
        var assetMap = backup.Assets.ToDictionary(asset => asset.Id);
        var roots = new Dictionary<string, NoteNode>();
        foreach (var space in backup.Spaces) SyncProtocol.Validate(new("space/" + space.Id, [new("backup:1", new() { ["backup"] = 1 }, SyncProtocol.Encode(space))]));
        foreach (var folder in backup.Folders)
        {
            SyncProtocol.Validate(new("folder/" + folder.Id, [new("backup:1", new() { ["backup"] = 1 }, SyncProtocol.Encode(folder))]));
            if (!spaceIds.ContainsKey(folder.SpaceId) || folder.ParentId != null && !folderIds.ContainsKey(folder.ParentId)) throw new InvalidDataException("文件夹位置无效");
            var seen = new HashSet<string> { folder.Id }; var parent = folder.ParentId;
            while (parent != null)
            {
                if (!seen.Add(parent)) throw new InvalidDataException("备份包含循环文件夹");
                var ancestor = backup.Folders.First(item => item.Id == parent);
                if (ancestor.SpaceId != folder.SpaceId) throw new InvalidDataException("文件夹父级属于其他空间");
                parent = ancestor.ParentId;
            }
        }
        foreach (var document in backup.Documents)
        {
            var source = NoteJson.ParseStrict(document.Content);
            var assets = DocumentAssets.Read(source, document.Appearance).Select(id => assetMap.TryGetValue(id, out var asset) ? asset : throw new InvalidDataException("备份缺少附件清单")).ToArray();
            SyncProtocol.Validate(new("document/" + document.Id, [new("backup:1", new() { ["backup"] = 1 }, SyncProtocol.Encode(new SyncDocumentPayload(document, assets)))]));
            if (!spaceIds.ContainsKey(document.SpaceId) || document.FolderId != null && !backup.Folders.Any(folder => folder.Id == document.FolderId && folder.SpaceId == document.SpaceId)) throw new InvalidDataException("笔记位置无效");
            ValidateAppearance(document.Appearance);
            roots.Add(document.Id, RemapLinks(source, documentIds));
        }
        foreach (var asset in backup.Assets)
        {
            if (!IsAssetId(asset.Id) || asset.Size is < 0 or > MaximumAssetSize) throw new InvalidDataException("备份附件标识或大小无效");
            var entry = archive.GetEntry("assets/" + asset.Id) ?? throw new InvalidDataException("备份缺少附件：" + asset.Name);
            if (entry.Length != asset.Size) throw new InvalidDataException("备份附件大小不符");
            using var source = entry.Open(); ImportAsset(source, asset.Name, asset.Id);
        }
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var space in backup.Spaces)
            {
                Sql(transaction, "INSERT INTO spaces(id,name) VALUES($id,$name)", ("$id", spaceIds[space.Id]), ("$name", space.Name[..Math.Min(94, space.Name.Length)] + "（导入）"));
                RecordLibraryChange("space", spaceIds[space.Id], transaction, false);
            }
            var remaining = backup.Folders.ToList(); var inserted = new HashSet<string>();
            while (remaining.Count > 0)
            {
                foreach (var folder in remaining.Where(folder => folder.ParentId == null || inserted.Contains(folder.ParentId)).ToArray())
                {
                    Sql(transaction, "INSERT INTO folders(id,space_id,parent_id,name) VALUES($id,$space,$parent,$name)", ("$id", folderIds[folder.Id]), ("$space", spaceIds[folder.SpaceId]), ("$parent", folder.ParentId == null ? null : folderIds[folder.ParentId]), ("$name", folder.Name));
                    RecordLibraryChange("folder", folderIds[folder.Id], transaction, false); inserted.Add(folder.Id); remaining.Remove(folder);
                }
            }
            foreach (var document in backup.Documents)
            {
                var id = documentIds[document.Id]; var root = roots[document.Id];
                Sql(transaction, "INSERT INTO documents(id,title,content,created_at,updated_at) VALUES($id,$title,$content,$created,$updated)", ("$id", id), ("$title", document.Title), ("$content", NoteJson.Serialize(root)), ("$created", document.CreatedAt), ("$updated", document.UpdatedAt));
                InsertLocation(id, spaceIds[document.SpaceId], document.FolderId == null ? null : folderIds[document.FolderId], transaction);
                Sql(transaction, "UPDATE document_locations SET deleted_at=$deleted,daily_date=$date WHERE document_id=$id", ("$deleted", document.Deleted ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0), ("$date", document.DailyDate), ("$id", id));
                Sql(transaction, "INSERT INTO document_metadata(document_id,favorite) VALUES($id,$favorite)", ("$id", id), ("$favorite", document.Favorite ? 1 : 0));
                Sql(transaction, "INSERT INTO document_appearance(document_id,value) VALUES($id,$value)", ("$id", id), ("$value", JsonSerializer.Serialize(document.Appearance)));
                ReplaceReferences(id, NoteReferences.Read(root), transaction); ReplaceSearch(id, document.Title, root, transaction); RecordDocumentChange(id, transaction, false);
            }
            transaction.Commit();
        }
        return backup.Documents.Select(document => documentIds[document.Id]).ToArray();
    }

    private static NoteNode RemapLinks(NoteNode node, IReadOnlyDictionary<string, string> ids) => node with
    {
        Content = node.Content.Select(child => RemapLinks(child, ids)).ToImmutableArray(),
        Marks = node.Marks.Select(mark => mark.Type == "noteLink" && mark.String("documentId") is { } target && ids.TryGetValue(target, out var mapped)
            ? mark with { Attrs = mark.Attrs.SetItem("documentId", JsonSerializer.SerializeToElement(mapped)) } : mark).ToImmutableArray()
    };
}
