using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

public sealed record PageAppearance(string Font = "sans", double FontSize = 15, double Width = 860, string? Background = null, double LineHeight = 1.2, string? TextColor = null,
    string? CoverAssetId = null, string Divider = "line");
public sealed record AppPreferences(string Theme = "light", double GhostOpacity = .85);
public sealed record AssetInfo(string Id, string Name, string MediaType, long Size);
public sealed record DocumentRevision(long Id, long SavedAt, string Title, string Content);

// Note: 外观元信息、内容寻址附件与完整备份 — 见 .agents/notes/implemented/feature/2026-09-10-page-assets-and-portability.md
public sealed partial class NoteStore
{
    public const long MaximumAssetSize = 50 * 1024 * 1024;
    public string AssetDirectory => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "assets");

    private void InitializeWorkspace()
    {
        Directory.CreateDirectory(AssetDirectory);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS document_appearance(document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS assets(id TEXT PRIMARY KEY,name TEXT NOT NULL,media_type TEXT NOT NULL,size INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS document_revisions(
                id INTEGER PRIMARY KEY AUTOINCREMENT,document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                saved_at INTEGER NOT NULL,title TEXT NOT NULL,content TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_revisions_document ON document_revisions(document_id,saved_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    public string? Setting(string key)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT value FROM library_state WHERE key=$key";
            command.Parameters.AddWithValue("$key", key); return command.ExecuteScalar() as string;
        }
    }
    public void SetSetting(string key, string value)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "INSERT INTO library_state(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery();
        }
    }
    public AppPreferences Preferences()
    {
        try { return JsonSerializer.Deserialize<AppPreferences>(Setting("app_preferences") ?? "{}") ?? new(); }
        catch (JsonException) { return new(); }
    }
    public void SetPreferences(AppPreferences preferences)
    {
        if (preferences.Theme is not ("light" or "dark" or "system") || !double.IsFinite(preferences.GhostOpacity) || preferences.GhostOpacity is < .4 or > 1) throw new ArgumentException("无效的主题或拖动不透明度");
        SetSetting("app_preferences", JsonSerializer.Serialize(preferences));
    }
    public PageAppearance Appearance(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT value FROM document_appearance WHERE document_id=$id"; command.Parameters.AddWithValue("$id", id);
            try { return JsonSerializer.Deserialize<PageAppearance>(command.ExecuteScalar() as string ?? "{}", SyncProtocol.Json) ?? new(); }
            catch (JsonException) { return new(); }
        }
    }
    public static PageAppearance ValidateAppearance(PageAppearance value)
    {
        if (value.Font is not ("sans" or "serif" or "mono" or "round") || !double.IsFinite(value.FontSize) || value.FontSize is < 12 or > 24
            || !double.IsFinite(value.Width) || value.Width is < 560 or > 1300 || !double.IsFinite(value.LineHeight) || value.LineHeight is < 1 or > 2
            || value.CoverAssetId != null && !IsAssetId(value.CoverAssetId) || value.Divider is not ("line" or "dotted" or "none"))
            throw new ArgumentException("页面字体、字号或宽度超出范围");
        if (value.Background != null && TextColor.Normalize(value.Background) == null || value.TextColor != null && TextColor.Normalize(value.TextColor) == null)
            throw new ArgumentException("请输入有效的十六进制颜色");
        return value with { Background = value.Background == null ? null : TextColor.Normalize(value.Background), TextColor = value.TextColor == null ? null : TextColor.Normalize(value.TextColor) };
    }
    public void SetAppearance(string id, PageAppearance value)
    {
        value = ValidateAppearance(value);
        lock (_gate)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO document_appearance(document_id,value) VALUES($id,$value) ON CONFLICT(document_id) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value)); command.ExecuteNonQuery();
            RecordDocumentChange(id, transaction, false); transaction.Commit();
        }
    }

    private void CaptureRevision(string id, SqliteTransaction transaction, bool force = false)
    {
        using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO document_revisions(document_id,saved_at,title,content)
            SELECT id,$now,title,content FROM documents WHERE id=$id AND
                ($force=1 OR NOT EXISTS(SELECT 1 FROM document_revisions WHERE document_id=$id AND saved_at>$cutoff));
            DELETE FROM document_revisions WHERE document_id=$id AND id NOT IN
                (SELECT id FROM document_revisions WHERE document_id=$id ORDER BY id DESC LIMIT 50);
            """;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$now", now); command.Parameters.AddWithValue("$cutoff", now - 300000); command.Parameters.AddWithValue("$force", force ? 1 : 0);
        command.ExecuteNonQuery();
    }
    public IReadOnlyList<DocumentRevision> Revisions(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT id,saved_at,title,content FROM document_revisions WHERE document_id=$id ORDER BY id DESC"; command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader(); var result = new List<DocumentRevision>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
            return result;
        }
    }
    public void PreserveRevision(string id)
    {
        lock (_gate) { using var transaction = _connection.BeginTransaction(); CaptureRevision(id, transaction, true); transaction.Commit(); }
    }

    public StoredDocument DailyNote(DateOnly date, string spaceId = "personal")
    {
        lock (_gate)
        {
            ValidateLocation(spaceId, null);
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT document_id FROM document_locations WHERE space_id=$space AND daily_date=$date";
            command.Parameters.AddWithValue("$space", spaceId); command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
            if (command.ExecuteScalar() is string existing)
            {
                transaction.Commit();
                if (Location(existing).IsDeleted) SetTrashed(existing, false);
                return Get(existing);
            }
            var title = date.ToString("yyyy年M月d日", System.Globalization.CultureInfo.InvariantCulture);
            var root = new NoteNode("doc") { Content = [NoteNode.Paragraph("今日计划") with { Type = "heading" }, new("taskList") { Content = [new("taskItem") { Content = [NoteNode.Paragraph()] }] }, NoteNode.Paragraph("随手记") with { Type = "heading" }, NoteNode.Paragraph()] };
            var id = Guid.NewGuid().ToString(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Sql(transaction, "INSERT INTO documents(id,title,content,created_at,updated_at) VALUES($id,$title,$content,$now,$now)", ("$id", id), ("$title", title), ("$content", NoteJson.Serialize(root)), ("$now", now));
            InsertLocation(id, spaceId, null, transaction);
            command.CommandText = "UPDATE document_locations SET daily_date=$date WHERE document_id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
            ReplaceReferences(id, NoteReferences.Read(root), transaction); ReplaceSearch(id, title, root, transaction);
            RecordDocumentChange(id, transaction, false); transaction.Commit(); return Get(id);
        }
    }

    public static bool IsAssetId(string id) => id.Length == 64 && id.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    public string? AssetPath(string id)
    {
        if (!IsAssetId(id)) return null;
        var path = Path.Combine(AssetDirectory, id);
        return File.Exists(path) ? path : null;
    }
    public AssetInfo? Asset(string id)
    {
        lock (_gate)
        {
            using var command = _connection.CreateCommand(); command.CommandText = "SELECT id,name,media_type,size FROM assets WHERE id=$id"; command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader(); return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)) : null;
        }
    }
    public async Task ReceiveAssetAsync(AssetInfo asset, Stream source, CancellationToken cancellation = default)
    {
        if (!IsAssetId(asset.Id) || asset.Size is < 0 or > MaximumAssetSize) throw new InvalidDataException("同步附件信息无效");
        var temporary = Path.Combine(AssetDirectory, ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var length = 0L; var buffer = new byte[81920]; int read;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, true))
            {
                while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    length += read;
                    if (length > asset.Size) throw new InvalidDataException("同步附件超过声明的大小");
                    hash.AppendData(buffer.AsSpan(0, read)); await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                }
                await output.FlushAsync(cancellation);
            }
            if (length != asset.Size || Convert.ToHexString(hash.GetHashAndReset()) != asset.Id) throw new InvalidDataException("同步附件内容校验失败");
            cancellation.ThrowIfCancellationRequested();
            var destination = Path.Combine(AssetDirectory, asset.Id);
            try { File.Move(temporary, destination, false); } catch (IOException) when (File.Exists(destination)) { File.Delete(temporary); }
            lock (_gate)
            {
                using var command = _connection.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO assets(id,name,media_type,size) VALUES($id,$name,$media,$size)";
                command.Parameters.AddWithValue("$id", asset.Id); command.Parameters.AddWithValue("$name", asset.Name); command.Parameters.AddWithValue("$media", asset.MediaType); command.Parameters.AddWithValue("$size", asset.Size); command.ExecuteNonQuery();
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public NoteNode ImportLocalAssets(NoteNode document, string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        NoteNode Import(NoteNode node)
        {
            if (node.Type is "image" or "attachment")
            {
                var expectedHash = node.String("assetId");
                var source = node.String("src");
                if (expectedHash != null)
                {
                    if (!IsAssetId(expectedHash)) throw new InvalidDataException("附件标识无效");
                    if (AssetPath(expectedHash) != null && Asset(expectedHash) != null) return node;
                    source = "assets/" + expectedHash;
                }
                if (source != null && !Path.IsPathRooted(source) && !Uri.TryCreate(source, UriKind.Absolute, out _))
                {
                    var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(source)));
                    if (path.StartsWith(root, comparison) && File.Exists(path))
                    {
                        var linked = false;
                        for (var current = path; current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
                            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { linked = true; break; }
                        if (!linked)
                        {
                            var fileName = Path.GetFileName(path);
                            if (expectedHash == null && IsAssetId(fileName)) expectedHash = fileName;
                            var name = expectedHash == null ? fileName : node.String("name") ?? node.String("alt") ?? (node.Type == "image" ? "图片.png" : "附件");
                            name = Path.GetFileName(name.Replace('\\', '/'));
                            if (node.Type == "image" && expectedHash != null && string.IsNullOrEmpty(Path.GetExtension(name)))
                                name += node.String("mimeType") switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "image/bmp" => ".bmp", _ => ".png" };
                            if (node.Type != "image" || new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" }.Contains(Path.GetExtension(name).ToLowerInvariant()))
                            {
                                using var file = File.OpenRead(path);
                                var asset = ImportAsset(file, name, expectedHash);
                                return node.WithAttr("assetId", asset.Id).WithAttr("name", asset.Name).WithAttr("mimeType", asset.MediaType).WithAttr("size", asset.Size);
                            }
                        }
                    }
                }
            }
            return node with { Content = node.Content.Select(Import).ToImmutableArray() };
        }
        return Import(document);
    }

    public AssetInfo ImportAsset(Stream source, string name, string? expectedHash = null)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.Any(char.IsControl)) throw new ArgumentException("无效的附件名称");
        var temporary = Path.Combine(AssetDirectory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; int read;
                while ((read = source.Read(buffer)) != 0)
                {
                    length += read;
                    if (length > MaximumAssetSize) throw new InvalidDataException("单个附件不能超过 50 MB");
                    hash.AppendData(buffer.AsSpan(0, read)); output.Write(buffer.AsSpan(0, read));
                }
            }
            var id = Convert.ToHexString(hash.GetHashAndReset());
            if (expectedHash != null && !id.Equals(expectedHash, StringComparison.Ordinal)) throw new InvalidDataException("附件内容校验失败");
            var destination = Path.Combine(AssetDirectory, id);
            if (File.Exists(destination)) File.Delete(temporary); else File.Move(temporary, destination);
            var media = Path.GetExtension(name).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", ".pdf" => "application/pdf", ".txt" or ".md" => "text/plain", _ => "application/octet-stream" };
            lock (_gate)
            {
                using var command = _connection.CreateCommand(); command.CommandText = "INSERT OR IGNORE INTO assets(id,name,media_type,size) VALUES($id,$name,$media,$size)";
                command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$media", media); command.Parameters.AddWithValue("$size", length); command.ExecuteNonQuery();
            }
            return new(id, name, media, length);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class DocumentAssets
{
    public static IEnumerable<string> Read(NoteNode root, PageAppearance? appearance = null) => NoteTree.Descendants(root).Select(node => node.String("assetId"))
        .Append(appearance?.CoverAssetId).OfType<string>().Where(NoteStore.IsAssetId).Distinct(StringComparer.Ordinal);
    public static NoteNode Node(AssetInfo asset, bool image) => new NoteNode(image ? "image" : "attachment").WithAttr("assetId", asset.Id).WithAttr("name", asset.Name).WithAttr("mimeType", asset.MediaType).WithAttr("size", asset.Size);
}

public sealed partial class DocumentSession
{
    public bool InsertAsset(int offset, AssetInfo asset, bool image)
    {
        if (!NoteStore.IsAssetId(asset.Id) || image && !asset.MediaType.StartsWith("image/", StringComparison.Ordinal)) return false;
        var text = NoteNode.Paragraph();
        return InsertAfter(Projection.At(offset), [DocumentAssets.Node(asset, image), text], text);
    }
}
