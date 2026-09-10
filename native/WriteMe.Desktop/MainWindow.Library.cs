using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

// Note: Craft 左侧空间/文档分工，索引导航核对当前文档 — 见 .agents/notes/implemented/feature/2026-09-10-library-and-search.md
public sealed partial class MainWindow
{
    private readonly Grid _documentPane = new() { RowDefinitions = new("*,Auto") };
    private readonly ScrollViewer _relations = new() { MaxHeight = 235, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new(16, 0, 16, 6) };
    private string _spaceId = "personal";
    private string? _folderId;
    private string? _tagFilter;
    private string _libraryMode = "all";

    private void InitializeLibraryNavigation()
    {
        _spaceId = _store.Location(_active.Id).SpaceId;
        _store.MarkOpened(_active.Id);
        this.FindControl<Button>("FavoriteButton")!.Click += (_, _) => ToggleFavorite();
        this.FindControl<Button>("SpaceSwitchButton")!.Click += (_, _) =>
        {
            var entries = _store.Spaces().Select(space => Menu((space.Id == _spaceId ? "✓ " : "") + space.Name, () =>
            {
                _spaceId = space.Id; _folderId = null; _tagFilter = null; _libraryMode = "all"; _search.Text = "";
                RefreshLibraryNavigation(); FilterDocuments();
            })).Cast<Control>().ToList();
            entries.Add(new Separator()); entries.Add(AsyncMenu("新建空间…", CreateSpaceAsync));
            entries.Add(AsyncMenu("重命名空间…", async () =>
            {
                var id = _spaceId;
                if (await AskNameAsync("重命名空间", _store.Spaces().First(space => space.Id == id).Name) is { } name)
                { _store.RenameContainer(id, name, true); RefreshLibraryNavigation(); }
            }));
            if (_spaceId != "personal") entries.Add(AsyncMenu("移除空间并保留笔记…", async () =>
            {
                var id = _spaceId;
                if (!await ConfirmAsync("移除空间", "空间中的笔记会移回「我的空间」，文件夹结构将移除。", "移除空间")) return;
                _store.DeleteContainer(id, true); _spaceId = "personal"; _folderId = null; ReloadDocuments();
            }));
            new ContextMenu { ItemsSource = entries }.Open(this.FindControl<Button>("SpaceSwitchButton")!);
        };
        _editor.CreateReferencedNote = title =>
        {
            var created = _store.Create(title, spaceId: _spaceId, folderId: _folderId);
            ReloadDocuments();
            return _store.List().Single(document => document.Id == created.Id);
        };
        _editor.ReferenceInvoked += async reference =>
        {
            if (reference.Kind == ReferenceKind.Tag) { _tagFilter = reference.Target; _folderId = null; _libraryMode = "all"; ShowNavigation(false); RefreshLibraryNavigation(); FilterDocuments(); }
            else await OpenReferenceAsync(reference.Target);
        };
        _list.ContextRequested += (_, e) =>
        {
            if (_list.SelectedItem is not DocumentItem item) return;
            var id = item.Id;
            var entries = item.Info.IsDeleted ? new Control[]
            {
                AsyncMenu("恢复笔记", async () => { _store.SetTrashed(id, false); _libraryMode = "all"; ReloadDocuments(); await SwitchAsync(id); }),
                AsyncMenu("永久删除…", async () =>
                {
                    if (await ConfirmAsync("永久删除", "这篇笔记将从资料库永久删除，无法恢复。", "永久删除")) { _store.Delete(id); ReloadDocuments(); }
                })
            } : new Control[]
            {
                Menu(item.Info.IsFavorite ? "取消收藏" : "收藏文档", () => { _store.SetFavorite(id, !item.Info.IsFavorite); if (_active.Id == id) _active = _store.Get(id); ReloadDocuments(); }),
                AsyncMenu("移动到…", async () => { await SwitchAsync(id); await MoveActiveAsync(); })
            };
            new ContextMenu { ItemsSource = entries }.Open(_list); e.Handled = true;
        };
    }

    private void ToggleFavorite()
    {
        if (ActiveHasConflict) { _status.Text = "请先在同步面板中选择冲突版本"; return; }
        _store.SetFavorite(_active.Id, !_active.IsFavorite);
        _active = _store.Get(_active.Id); ReloadDocuments();
    }

    private void SelectLibrary(string mode, string? folder = null, string? tag = null)
    {
        _libraryMode = mode; _folderId = folder; _tagFilter = tag; _search.Text = "";
        ShowNavigation(false); RefreshLibraryNavigation(); FilterDocuments();
        _ = RunUiAsync(ShowLibraryOverviewAsync);
    }

    private static Button NavigationButton(string label, string symbol, Action action, bool selected = false, string? automationId = null)
    {
        var button = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(10, 7), MinHeight = 31 };
        button.Classes.Add("quiet"); button.Classes.Set("active", selected);
        var grid = new Grid { ColumnDefinitions = new("24,*") };
        grid.Children.Add(new TextBlock { Text = symbol, Foreground = Ui.Muted, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock { Text = label, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1); grid.Children.Add(text); button.Content = grid;
        AutomationProperties.SetName(button, label);
        if (automationId != null) AutomationProperties.SetAutomationId(button, automationId);
        button.Click += (_, _) => action(); return button;
    }

    private void RefreshLibraryNavigation()
    {
        var spaces = _store.Spaces();
        if (!spaces.Any(space => space.Id == _spaceId)) { _spaceId = "personal"; _folderId = null; }
        this.FindControl<Button>("SpaceSwitchButton")!.Content = spaces.First(space => space.Id == _spaceId).Name + " ⌄";
        var panel = this.FindControl<StackPanel>("LibraryNavigation")!;
        panel.Children.Clear();
        panel.Children.Add(NavigationButton("所有文档", "▤", () => SelectLibrary("all"), _libraryMode == "all" && _folderId == null && _tagFilter == null, "LibraryAll"));
        panel.Children.Add(NavigationButton("最近打开", "◷", () => SelectLibrary("recent"), _libraryMode == "recent", "LibraryRecent"));
        panel.Children.Add(NavigationButton("收藏", "☆", () => SelectLibrary("favorites"), _libraryMode == "favorites", "LibraryFavorites"));
        AddLibraryExtraActions(panel);
        var folderHeader = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(10, 13, 3, 2) };
        folderHeader.Children.Add(new TextBlock { Text = "文件夹", FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
        var add = Ui.Button("+", "新建文件夹", () => _ = CreateFolderAsync(_folderId));
        AutomationProperties.SetAutomationId(add, "CreateFolder"); Grid.SetColumn(add, 1); folderHeader.Children.Add(add); panel.Children.Add(folderHeader);
        var folders = _store.Folders(_spaceId);
        if (_folderId != null && !folders.Any(folder => folder.Id == _folderId)) _folderId = null;
        var visited = new HashSet<string>();
        void FolderRows(string? parent, int depth)
        {
            foreach (var folder in folders.Where(folder => folder.ParentId == parent))
            {
                if (!visited.Add(folder.Id)) continue;
                var row = NavigationButton(folder.Name, "▱", () => SelectLibrary("all", folder.Id), _folderId == folder.Id, "Folder_" + folder.Id);
                row.Margin = new(depth * 14, 0, 0, 0);
                row.ContextMenu = new ContextMenu { ItemsSource = new Control[]
                {
                    AsyncMenu("新建子文件夹…", () => CreateFolderAsync(folder.Id)),
                    AsyncMenu("重命名…", async () => { if (await AskNameAsync("重命名文件夹", folder.Name) is { } name) { _store.RenameContainer(folder.Id, name, false); RefreshLibraryNavigation(); } }),
                    AsyncMenu("移动文件夹…", () => MoveFolderAsync(folder)),
                    Menu("移除文件夹，保留笔记", () => { _store.DeleteContainer(folder.Id, false); if (_folderId == folder.Id) _folderId = folder.ParentId; ReloadDocuments(); })
                } };
                panel.Children.Add(row); FolderRows(folder.Id, depth + 1);
            }
        }
        FolderRows(null, 0);
        if (folders.Count == 0) panel.Children.Add(new TextBlock { Text = "用文件夹整理项目", FontSize = 11, Foreground = Ui.Muted, Margin = new(10, 5) });
        panel.Children.Add(new TextBlock { Text = "标签", FontSize = 11, Foreground = Ui.Muted, Margin = new(10, 13, 10, 5) });
        foreach (var tag in _store.Tags()) panel.Children.Add(NavigationButton(tag.Name + "  " + tag.Count, "#", () => SelectLibrary("all", tag: tag.Key), _tagFilter == tag.Key, "Tag_" + tag.Key));
        if (_store.Tags().Count == 0) panel.Children.Add(new TextBlock { Text = "在正文输入 #标签", FontSize = 11, Foreground = Ui.Muted, Margin = new(10, 5) });
        this.FindControl<TextBlock>("ListHeading")!.Text = _tagFilter != null ? "#" + _tagFilter : _folderId != null ? folders.FirstOrDefault(folder => folder.Id == _folderId)?.Name ?? "笔记"
            : _libraryMode switch { "recent" => "最近打开", "favorites" => "收藏", "trash" => "回收站 · 右键恢复", _ => "笔记" };
    }

    partial void AddLibraryExtraActions(StackPanel panel);

    private void RefreshReferences()
    {
        var active = _store.Get(_active.Id);
        this.FindControl<Button>("FavoriteButton")!.Content = active.IsFavorite ? "★" : "☆";
        _editor.SetReferenceCatalogue(_store.List(), _store.Tags());
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(0, 4, 0, 8) });
        var incoming = _store.Backlinks(active.Id);
        content.Children.Add(new TextBlock { Text = "反向链接  " + incoming.Count, FontSize = 11, Foreground = Ui.Muted, Margin = new(7, 1, 0, 3) });
        foreach (var relation in incoming)
        {
            var button = RelationButton(relation.Title, relation.Preview, async () =>
            {
                await OpenReferenceAsync(relation.DocumentId);
                if (_active.Id == relation.DocumentId) RevealPath(relation.Path, relation.Start, relation.Length);
            });
            AutomationProperties.SetAutomationId(button, "Backlink_" + relation.DocumentId); content.Children.Add(button);
        }
        if (incoming.Count == 0) content.Children.Add(new TextBlock { Text = "被其他笔记链接后，会显示在这里", FontSize = 11, Foreground = Ui.Muted, Margin = new(7, 4, 0, 6), TextWrapping = TextWrapping.Wrap });
        var outgoing = _store.OutgoingLinks(active.Id);
        if (outgoing.Count > 0) content.Children.Add(new TextBlock { Text = "链接到  " + outgoing.Count, FontSize = 11, Foreground = Ui.Muted, Margin = new(7, 9, 0, 3) });
        foreach (var relation in outgoing)
            content.Children.Add(RelationButton(relation.Title + (relation.Exists ? "" : " · 已删除"), relation.Preview, () => OpenReferenceAsync(relation.DocumentId)));
        _relations.Content = content;
    }

    private Button RelationButton(string title, string preview, Func<Task> action)
    {
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock { Text = "↗  " + title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = preview, FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new(7, 7) };
        button.Classes.Add("quiet"); button.Click += async (_, _) => await RunUiAsync(action); return button;
    }

    private async Task OpenReferenceAsync(string id)
    {
        if (!_store.List().Any(document => document.Id == id)) { _status.Text = "这篇笔记已删除，可在回收站查看；原文链接已保留"; return; }
        await SwitchAsync(id);
        _editor.FocusText();
    }

    private async Task OpenResultAsync(LibraryResult result)
    {
        if (result.Document.IsDeleted) { _status.Text = "回收站中的笔记：右键选择恢复或永久删除"; return; }
        await SwitchAsync(result.Document.Id);
        if (_active.Id == result.Document.Id && result.Path != null) RevealPath(result.Path, result.Start, result.Length);
    }

    private void RevealPath(string path, int start, int length)
    {
        if (ActiveHasConflict) { _status.Text = "请先在同步面板中选择冲突版本"; return; }
        var node = NoteReferences.AtPath(_editor.Session.Root, path);
        if (node == null || !_editor.Session.Reveal(node.Id)) return;
        if (_editor.Session.Projection.Find(node.Id) is not { } row) return;
        start = Math.Clamp(start, 0, row.Text.Length);
        _editor.Surface.Select(row.Start + start, Math.Clamp(length, 0, row.Text.Length - start));
        _editor.Surface.ScrollTo(_editor.Surface.TextArea.Caret.Line, _editor.Surface.TextArea.Caret.Column); _editor.FocusText();
    }

    private async Task CreateSpaceAsync()
    {
        if (await AskNameAsync("新建空间") is not { } name) return;
        _spaceId = _store.CreateSpace(name).Id; _folderId = null; _tagFilter = null; _libraryMode = "all"; ReloadDocuments();
    }
    private async Task CreateFolderAsync(string? parent)
    {
        var space = _spaceId;
        if (await AskNameAsync("新建文件夹") is not { } name) return;
        _folderId = _store.CreateFolder(space, name, parent).Id; _libraryMode = "all"; _tagFilter = null; ReloadDocuments();
    }
    private async Task DuplicateAsync()
    {
        if (!await SaveAsync()) return;
        var location = _store.Location(_active.Id);
        var copy = _store.Create(NoteReferences.DisplayTitle(_active.Title) + " 副本", _editor.Session.Root, location.SpaceId, location.FolderId);
        _store.SetAppearance(copy.Id, _store.Appearance(_active.Id));
        ReloadDocuments(); await SwitchAsync(copy.Id);
    }

    private sealed record PlaceChoice(string SpaceId, string? FolderId, string Label);
    private IEnumerable<PlaceChoice> Places(string? spaceId = null)
    {
        foreach (var space in _store.Spaces().Where(space => spaceId == null || space.Id == spaceId))
        {
            yield return new(space.Id, null, space.Name + " / 未分类");
            var folders = _store.Folders(space.Id);
            foreach (var folder in folders)
            {
                var names = new List<string> { folder.Name }; var parent = folder.ParentId; var seen = new HashSet<string> { folder.Id };
                while (parent != null && seen.Add(parent) && folders.FirstOrDefault(item => item.Id == parent) is { } ancestor) { names.Insert(0, ancestor.Name); parent = ancestor.ParentId; }
                yield return new(space.Id, folder.Id, space.Name + " / " + string.Join(" / ", names));
            }
        }
    }
    private async Task MoveActiveAsync()
    {
        if (!await SaveAsync()) return;
        var id = _active.Id;
        if (await ChooseAsync("移动笔记", Places().ToArray(), choice => choice.Label) is not { } place) return;
        _store.MoveDocument(id, place.SpaceId, place.FolderId); _spaceId = place.SpaceId; _folderId = place.FolderId; ReloadDocuments();
    }
    private async Task MoveFolderAsync(FolderInfo folder)
    {
        if (await ChooseAsync("移动文件夹", Places(folder.SpaceId).Where(place => place.FolderId != folder.Id).ToArray(), choice => choice.Label) is not { } place) return;
        _store.MoveFolder(folder.Id, place.FolderId); RefreshLibraryNavigation();
    }

    private MenuItem AsyncMenu(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header }; item.Click += async (_, _) => await RunUiAsync(action); return item;
    }
    private async Task RunUiAsync(Func<Task> action)
    {
        try { while (_syncApplying && !_closed) await Task.Delay(25); if (!_closed) await action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        { _status.Text = ex.Message; }
    }
    private Window Dialog(string title, Control content, double width = 420, double height = 230) => new()
    {
        Title = title, Width = width, Height = height, MinWidth = Math.Min(width, 360), MinHeight = 170, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = content
    };
    private async Task<string?> AskNameAsync(string title, string initial = "")
    {
        var text = new TextBox { Text = initial, Watermark = "输入名称", MaxLength = 100 };
        AutomationProperties.SetAutomationId(text, "DialogName");
        var panel = new StackPanel { Margin = new(24), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold }); panel.Children.Add(text);
        var dialog = Dialog(title, panel); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => dialog.Close();
        var save = new Button { Content = "确定", IsDefault = true }; save.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(text.Text)) dialog.Close(text.Text.Trim()); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        dialog.Opened += (_, _) => { text.Focus(); text.SelectAll(); }; return await dialog.ShowDialog<string?>(this);
    }
    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var panel = new StackPanel { Margin = new(24), Spacing = 22 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog(title, panel); var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "取消" }; cancel.Click += (_, _) => dialog.Close(false);
        var accept = new Button { Content = action }; accept.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); return await dialog.ShowDialog<bool>(this);
    }
    private async Task<T?> ChooseAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label) where T : class
    {
        var panel = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(22) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new(0, 0, 0, 14) });
        var list = new ListBox { ItemsSource = choices, ItemTemplate = new FuncDataTemplate<T>((item, _) => new TextBlock { Text = item == null ? "" : label(item), TextWrapping = TextWrapping.Wrap }) };
        Grid.SetRow(list, 1); panel.Children.Add(list);
        var dialog = Dialog(title, panel, 470, 430);
        var done = new Button { Content = "选择", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 15, 0, 0) };
        done.Click += (_, _) => { if (list.SelectedItem is T choice) dialog.Close(choice); }; Grid.SetRow(done, 2); panel.Children.Add(done);
        return await dialog.ShowDialog<T?>(this);
    }
}
