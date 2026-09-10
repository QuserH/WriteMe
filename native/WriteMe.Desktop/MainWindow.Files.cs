using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using WriteMe.Core;

namespace WriteMe.Desktop;

public sealed partial class MainWindow
{
    private async Task SetCoverAsync(string documentId)
    {
        if (_active.Id != documentId || ActiveHasConflict) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "选择封面图片", FileTypeFilter = [new("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"] }] });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync(); var asset = await Task.Run(() => _store.ImportAsset(stream, files[0].Name));
        if (_active.Id != documentId || ActiveHasConflict) { _status.Text = "当前文档已改变，请重新选择封面"; return; }
        _store.SetAppearance(documentId, _store.Appearance(documentId) with { CoverAssetId = asset.Id }); ApplyPageAppearance(); _tools.RefreshExtendedPanel(true);
    }
    private async Task InsertAssetsAsync(bool images)
    {
        if (_editor.IsAnyComposing || _switching) return;
        var editor = _editor.ActiveEditor;
        var session = editor.Session; var revision = session.Revision; var offset = editor.Surface.CaretOffset;
        var files = await StorageProvider.OpenFilePickerAsync(new()
        {
            Title = images ? "插入图片" : "插入附件", AllowMultiple = true,
            FileTypeFilter = images ? [new("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"] }] : [FilePickerFileTypes.All]
        });
        foreach (var file in files)
        {
            await using var stream = await file.OpenReadAsync();
            var asset = await Task.Run(() => _store.ImportAsset(stream, file.Name));
            if (!ReferenceEquals(editor, _editor.ActiveEditor) || !session.IsScopeAttached || session.Revision != revision) { _status.Text = "编辑位置已改变，请重新选择附件插入位置"; return; }
            session.InsertAsset(offset, asset, images); revision = session.Revision; offset = editor.Surface.CaretOffset;
        }
        _editor.FocusText();
    }
    private Task OpenAssetAsync(NoteNode node)
    {
        if (node.String("assetId") is not { } id)
        {
            if (node.String("src") is { } source && LinkAddress.Normalize(source) is { } address && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
            return Task.CompletedTask;
        }
        var asset = _store.Asset(id); var path = _store.AssetPath(id);
        if (asset == null || path == null) { _status.Text = "附件尚未下载，可连接同步服务器后重试"; return Task.CompletedTask; }
        var directory = Path.Combine(Path.GetDirectoryName(_store.DatabasePath)!, "opened-assets"); Directory.CreateDirectory(directory);
        var name = Path.GetFileName(asset.Name.Replace('\\', '/'));
        var destination = Path.Combine(directory, id[..16] + "-" + name);
        File.Copy(path, destination, true);
        Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); return Task.CompletedTask;
    }

    private static string ExportName(string title)
    {
        var name = string.Concat(title.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return name.Length == 0 ? "WriteME-note" : name[..Math.Min(name.Length, 100)];
    }
    private async Task ExportTextAsync(bool markdown)
    {
        var extension = markdown ? "md" : "txt";
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出笔记", SuggestedFileName = ExportName(_title.Text ?? "") + "." + extension, DefaultExtension = extension, FileTypeChoices = [new(markdown ? "Markdown" : "纯文本") { Patterns = ["*." + extension] }] });
        if (file == null) return;
        var root = _editor.Session.Root;
        if (markdown && DocumentAssets.Read(root).Any())
        {
            var location = file.TryGetLocalPath() ?? throw new IOException("请导出到本机目录以一并保存附件，或使用资料库备份");
            var assets = Path.Combine(Path.GetDirectoryName(location)!, "assets"); Directory.CreateDirectory(assets);
            foreach (var id in DocumentAssets.Read(root)) File.Copy(_store.AssetPath(id) ?? throw new IOException("附件尚未下载"), Path.Combine(assets, id), true);
        }
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0);
        await using var writer = new StreamWriter(stream); await writer.WriteAsync(markdown ? NoteMarkdown.Export(root) : DocumentText.Plain(root));
        _status.Text = "笔记已导出";
    }
    private async Task ExportBackupAsync()
    {
        if (!await SaveAsync()) return;
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出资料库备份", SuggestedFileName = "WriteME-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip", DefaultExtension = "zip", FileTypeChoices = [new("WriteME 备份") { Patterns = ["*.zip"] }] });
        if (file == null) return;
        await using var stream = await file.OpenWriteAsync(); stream.SetLength(0); await Task.Run(() => _store.ExportBackup(stream)); _status.Text = "资料库与附件已导出";
    }
    private async Task ImportAsync()
    {
        if (!await SaveAsync()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "导入文档或资料库", AllowMultiple = true, FileTypeFilter = [new("文档与备份") { Patterns = ["*.md", "*.markdown", "*.txt", "*.json", "*.zip"] }] });
        string? last = null; var failures = new List<string>(); var importedCount = 0;
        foreach (var file in files)
        {
            try
            {
                await using var stream = await file.OpenReadAsync();
                if (Path.GetExtension(file.Name).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var imported = await Task.Run(() => _store.ImportBackup(stream)); importedCount += imported.Count; last = imported.LastOrDefault(id => !_store.Location(id).IsDeleted) ?? last; continue;
                }
                using var reader = new StreamReader(stream);
                var buffer = new char[8192]; var text = new System.Text.StringBuilder(); int count;
                while ((count = await reader.ReadAsync(buffer)) > 0) { text.Append(buffer, 0, count); if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("单篇导入文件超过 8 MB"); }
                var extension = Path.GetExtension(file.Name).ToLowerInvariant();
                var root = extension == ".json" ? NoteJson.ParseStrict(text.ToString()) : extension is ".md" or ".markdown" ? NoteMarkdown.Parse(text.ToString()) : NoteJson.Parse(text.ToString());
                if (file.TryGetLocalPath() is { } local) root = await Task.Run(() => _store.ImportLocalAssets(root, Path.GetDirectoryName(local)!));
                last = _store.Create(Path.GetFileNameWithoutExtension(file.Name), root, _spaceId, _folderId).Id; importedCount++;
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            { failures.Add(file.Name + "：" + ex.Message); }
        }
        ReloadDocuments();
        if (last != null)
        {
            var location = _store.Location(last); _spaceId = location.SpaceId; _folderId = location.FolderId; _tagFilter = null; _libraryMode = "all";
            ReloadDocuments(); await SwitchAsync(last);
        }
        if (files.Count > 0) _status.Text = failures.Count == 0 ? $"已导入 {importedCount} 篇笔记，原有笔记已保留" : $"已导入 {importedCount} 篇；{failures.Count} 个文件失败：{string.Join("；", failures)}";
    }
    private async Task RestoreRevisionAsync()
    {
        if (!await SaveAsync()) return;
        var id = _active.Id; var revisions = _store.Revisions(id);
        if (revisions.Count == 0) { _status.Text = "编辑后会自动保留本地备份，最多 50 份"; return; }
        var selected = await ChooseAsync("恢复本地备份", revisions, revision => DateTimeOffset.FromUnixTimeMilliseconds(revision.SavedAt).LocalDateTime.ToString("yyyy/M/d HH:mm") + "\n" + NoteReferences.DisplayTitle(revision.Title) + " · " + DocumentText.Plain(NoteJson.Parse(revision.Content))[..Math.Min(80, DocumentText.Plain(NoteJson.Parse(revision.Content)).Length)]);
        if (selected == null || _active.Id != id) return;
        _store.PreserveRevision(id); _title.Text = selected.Title; _editor.Session.RestoreSnapshot(NoteJson.ParseStrict(selected.Content)); await SaveAsync();
        _status.Text = "已恢复；恢复前的内容也保留为备份";
    }
    private void Present()
    {
        var window = new PresentationWindow(_title.Text ?? "无标题", _editor.Session.Root, _pageAppearance, _store.AssetPath);
        window.Show(this);
    }
}
