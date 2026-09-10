using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

public sealed class DocumentItem(DocumentInfo info) : INotifyPropertyChanged
{
    private DocumentInfo _info = info;
    public string Id => _info.Id;
    public DocumentInfo Info => _info;
    public LibraryResult Result { get; private set; } = new(info);
    public string Preview => Result.Preview;
    public bool HasPreview => Preview.Length > 0;
    public string DisplayTitle => string.IsNullOrWhiteSpace(_info.Title) ? "无标题" : _info.Title;
    public string DateLabel => DateTimeOffset.FromUnixTimeMilliseconds(_info.UpdatedAt).LocalDateTime.ToString("M月d日");
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(DocumentInfo info) { _info = info; PropertyChanged?.Invoke(this, new(null)); }
    public void Update(LibraryResult result) { Result = result; Update(result.Document); }
}

public sealed partial class MainWindow : Window
{
    private readonly NoteStore _store;
    private readonly BlockEditor _editor;
    private readonly EditorSidebar _tools;
    private readonly DocumentOutlinePane _outline;
    private readonly TextBox _title;
    private readonly TextBox _search;
    private readonly TextBlock _status;
    private readonly ListBox _list;
    private readonly ObservableCollection<DocumentItem> _documents = [];
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private StoredDocument _active;
    private long _editVersion;
    private long _savedVersion;
    private bool _loading;
    private bool _switching;
    private bool _allowClose;
    private bool _sidebarVisible = true;
    private bool _documentNavigation = true;
    private string? _requestedDocument;
    private string? _trashUndoId;

    public MainWindow() : this(Program.DataDirectory, Program.ImportLegacy) { }

    public MainWindow(string dataDirectory, bool importLegacy)
    {
        AvaloniaXamlLoader.Load(this);
        _store = new(dataDirectory, importLegacy ? NoteStore.LegacyPath : null);
        if (_store.List().Count == 0) _store.Create("欢迎使用 WriteME", WelcomeDocument.Create());
        _active = _store.Get(_store.List()[0].Id);
        _title = this.FindControl<TextBox>("TitleBox")!;
        _search = this.FindControl<TextBox>("SearchBox")!;
        _status = this.FindControl<TextBlock>("StatusLabel")!;
        _list = this.FindControl<ListBox>("DocumentList")!;
        _editor = new(new(NoteJson.Parse(_active.Content)));
        this.FindControl<Grid>("EditorHost")!.Children.Add(_editor);
        _outline = new(_editor);
        _documentPane.Children.Add(_outline);
        Grid.SetRow(_relations, 1); _documentPane.Children.Add(_relations);
        this.FindControl<Grid>("LeftPaneHost")!.Children.Add(_documentPane);
        var spaceMode = this.FindControl<Button>("SpaceModeButton")!;
        var documentMode = this.FindControl<Button>("DocumentModeButton")!;
        spaceMode.Content = new SidebarGlyph(SidebarSymbol.Folder, 20);
        documentMode.Content = new SidebarGlyph(SidebarSymbol.Document, 20);
        this.FindControl<Button>("SidebarButton")!.Content = new SidebarGlyph(SidebarSymbol.Sidebar, 18);
        foreach (var (name, symbol) in new[] { ("UndoButton", SidebarSymbol.Undo), ("RedoButton", SidebarSymbol.Redo), ("MoreButton", SidebarSymbol.More), ("LibraryButton", SidebarSymbol.More), ("NewButton", SidebarSymbol.Plus) })
            this.FindControl<Button>(name)!.Content = new SidebarGlyph(symbol, 18);
        spaceMode.Click += (_, _) => ShowNavigation(false);
        documentMode.Click += (_, _) => ShowNavigation(true);
        _tools = new(_editor) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 36, 16, 14) };
        Grid.SetColumn(_tools, 1);
        Grid.SetRow(_tools, 1);
        _tools.ZIndex = 20;
        this.FindControl<Grid>("Shell")!.Children.Add(_tools);
        _tools.PanelChanged += (_, _) => UpdateSidebars();
        SizeChanged += (_, _) => UpdateSidebars();
        _editor.Notice += message => _status.Text = message;
        _editor.SelectionChanged += (_, _) => UpdateHistoryButtons();
        _editor.Session.Changed += ContentChanged;
        // TextChanged is queued by Avalonia; a remote title load can arrive after _loading is cleared.
        _title.TextChanged += (_, _) => { if (!_loading && (_title.Text ?? "") != _active.Title) { UpdateDocumentLabels(); Dirty(); } };
        _search.TextChanged += (_, _) => FilterDocuments();
        _list.SelectionChanged += async (_, _) =>
        {
            if (!_loading && _list.SelectedItem is DocumentItem item) await OpenResultAsync(item.Result);
        };
        _list.Tapped += async (_, _) =>
        {
            if (!_loading && _list.SelectedItem is DocumentItem item && item.Id == _active.Id) await OpenResultAsync(item.Result);
        };
        _list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && !_loading && _list.SelectedItem is DocumentItem item) { e.Handled = true; await OpenResultAsync(item.Result); }
        };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveAsync(); };
        this.FindControl<Button>("NewButton")!.Click += async (_, _) => await NewAsync();
        this.FindControl<Button>("UndoButton")!.Click += (_, _) => { _editor.Session.Undo(); _editor.FocusText(); };
        this.FindControl<Button>("RedoButton")!.Click += (_, _) => { _editor.Session.Redo(); _editor.FocusText(); };
        this.FindControl<Button>("SidebarButton")!.Click += (_, _) =>
        {
            if (_sidebarVisible && _tools.IsOpen && Bounds.Width < 1080) _tools.Close();
            else _sidebarVisible = !_sidebarVisible;
            UpdateSidebars();
        };
        this.FindControl<Button>("MoreButton")!.Click += (_, _) => OpenDocumentMenu();
        this.FindControl<Button>("LibraryButton")!.Click += (_, _) => OpenLibraryMenu();
        this.FindControl<Button>("TrashButton")!.Click += (_, _) => SelectLibrary("trash");
        this.FindControl<Button>("UndoTrashButton")!.Click += async (_, _) => await RunUiAsync(RestoreLastTrashedAsync);
        this.FindControl<Button>("DismissTrashNoticeButton")!.Click += (_, _) => this.FindControl<Border>("TrashUndoNotice")!.IsVisible = false;
        InitializeLibraryNavigation();
        InitializeWorkspaceUi();
        InitializeOverview();
        _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _editor.FocusText(); e.Handled = true; } };
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.D1 or Key.D2 or Key.D3)
            {
                if (_editor.IsAnyComposing) { e.Handled = true; return; }
                if (e.Key == Key.D3) { ShowNavigation(true); _outline.FocusOutline(); }
                else
                {
                    _tools.Toggle(e.Key == Key.D1 ? EditorPanel.Insert : EditorPanel.Style);
                    if (_tools.IsOpen) _tools.FocusPanel();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S) { e.Handled = true; await SaveAsync(); }
            if (e.Key == Key.N) { e.Handled = true; await NewAsync(); }
            if (e.Key == Key.F) { e.Handled = true; ShowNavigation(false); _search.Focus(); _search.SelectAll(); }
            if (e.Key == Key.O) { e.Handled = true; await RunUiAsync(ImportAsync); }
            if (e.Key == Key.OemComma) { e.Handled = true; await SettingsAsync(); }
        }, RoutingStrategies.Tunnel);
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closingPending) return;
            _closingPending = true;
            IsEnabled = false;
            await StopSyncAsync();
            while (_syncConnecting || _syncDisconnecting) await Task.Delay(20);
            if (await SaveAsync()) { _allowClose = true; Close(); }
            else { _closingPending = false; IsEnabled = true; _syncCancellation.Dispose(); _syncCancellation = new(); _syncTimer.Start(); UpdateEditorAvailability(); }
        };
        Closed += (_, _) => { _closed = true; _saveTimer.Stop(); _syncTimer.Stop(); _syncCancellation.Dispose(); _coverBitmap?.Dispose(); _store.Dispose(); };
        ReloadDocuments();
        ShowActive();
        UpdateSidebars();
        InitializeSyncUi();
        if (_store.ImportedLegacy) _status.Text = "已导入旧版笔记副本 · 后续修改保存在原生版资料库";
    }

    private MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await RunUiAsync(() => { action(); return Task.CompletedTask; });
        return item;
    }

    private void UpdateSidebars()
    {
        var showLibrary = _sidebarVisible && (!_tools.IsOpen || Bounds.Width >= 1080);
        this.FindControl<Grid>("Shell")!.ColumnDefinitions[0].Width = new GridLength(showLibrary ? 292 : 0);
        this.FindControl<Border>("LibrarySidebar")!.IsVisible = showLibrary;
        this.FindControl<Grid>("SpacePane")!.IsVisible = !_documentNavigation;
        _documentPane.IsVisible = _documentNavigation;
        this.FindControl<Button>("SpaceModeButton")!.Classes.Set("active", !_documentNavigation);
        this.FindControl<Button>("DocumentModeButton")!.Classes.Set("active", _documentNavigation);
        _tools.Margin = new(0, _tools.IsOpen ? 4 : 36, 16, 14);
        _tools.MaxHeight = Math.Max(160, Bounds.Height - 56 - _tools.Margin.Top - _tools.Margin.Bottom);
        _tools.Height = _tools.IsOpen ? _tools.MaxHeight : double.NaN;
        var reserved = (_tools.IsOpen ? EditorSidebar.PanelWidth : EditorSidebar.ToolWidth) + 32;
        this.FindControl<Grid>("DocumentRegion")!.Margin = new(0, 0, reserved, 0);
        var available = Bounds.Width - (showLibrary ? 292 : 0) - reserved;
        var left = available < 680 ? -20 : 32;
        var right = available < 680 ? 24 : 40;
        this.FindControl<Grid>("EditorHost")!.Margin = new(left, 24, right, 0);
        this.FindControl<Grid>("TitleRegion")!.Margin = new(left, 58, right, 8);
    }

    private void ShowNavigation(bool document)
    {
        if (document && _overviewVisible) ShowDocumentView();
        _documentNavigation = document;
        _sidebarVisible = true;
        if (_tools.IsOpen && Bounds.Width < 1080) _tools.Close();
        UpdateSidebars();
    }

    private void UpdateDocumentLabels(long? updatedAt = null)
    {
        var title = string.IsNullOrWhiteSpace(_title.Text) ? "无标题" : _title.Text;
        this.FindControl<TextBlock>("NavigationTitle")!.Text = title;
        _outline.SetDocument(title, updatedAt ?? _active.UpdatedAt);
    }

    private void ContentChanged(object? sender, EventArgs args) => Dirty();
    private void Dirty()
    {
        _editVersion++;
        _status.Text = "正在编辑…";
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateHistoryButtons();
    }

    private async Task<bool> SaveAsync()
    {
        _saveTimer.Stop();
        await _saveGate.WaitAsync();
        try
        {
            if (_editVersion == _savedVersion) return true;
            var id = _active.Id;
            var root = _editor.Session.Root;
            var title = _title.Text ?? "";
            var version = _editVersion;
            _status.Text = "正在保存…";
            await Task.Run(() => _store.Save(id, title, root));
            if (_active.Id == id)
            {
                _savedVersion = version;
                _status.Text = _editVersion == version ? "已保存到本机" : "正在编辑…";
                if (_editVersion != version) _saveTimer.Start();
                _active = _store.Get(id);
                ReloadDocuments();
                UpdateDocumentLabels(DateTimeOffset.Now.ToUnixTimeMilliseconds());
                UpdateWordCount(root);
                _tools.RefreshExtendedPanel();
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            _status.Text = $"保存失败，内容仍在窗口中：{ex.Message}";
            return false;
        }
        finally { _saveGate.Release(); }
    }

    private async Task SwitchAsync(string id)
    {
        while (_syncApplying && !_closed) await Task.Delay(25);
        if (_closed) return;
        ShowDocumentView();
        _requestedDocument = id;
        if (_switching) return;
        _switching = true;
        _editor.IsEnabled = false;
        _title.IsReadOnly = true;
        try
        {
            while (_requestedDocument is { } requested && requested != _active.Id)
            {
                _requestedDocument = null;
                if (!await SaveAsync()) { FilterDocuments(); return; }
                var document = await Task.Run(() => _store.Get(requested));
                _editor.Session.Changed -= ContentChanged;
                _active = document;
                _store.MarkOpened(document.Id);
                _editor.Load(new(NoteJson.Parse(document.Content)));
                _editor.Session.Changed += ContentChanged;
                _editVersion = _savedVersion = 0;
                ShowActive();
            }
        }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        {
            _status.Text = $"打开笔记失败：{ex.Message}";
            FilterDocuments();
        }
        finally { _switching = false; UpdateEditorAvailability(); }
    }

    private async Task NewAsync()
    {
        if (_switching || _syncApplying || !await SaveAsync()) return;
        var doc = _store.Create(spaceId: _spaceId, folderId: _folderId);
        _libraryMode = "all"; _tagFilter = null;
        _search.Text = "";
        ReloadDocuments();
        await SwitchAsync(doc.Id);
        _title.Focus();
    }

    private void ReloadDocuments()
    {
        _loading = true;
        _documents.Clear();
        foreach (var info in _store.List().Concat(_store.Query(new(Mode: "trash")).Select(result => result.Document))) _documents.Add(new(info));
        _loading = false;
        RefreshLibraryNavigation();
        FilterDocuments();
        RefreshReferences();
    }

    private void FilterDocuments()
    {
        _loading = true;
        var results = _store.Query(new(_spaceId, _folderId, _libraryMode, _tagFilter, _search.Text ?? ""));
        var items = results.Select(result => { var item = _documents.FirstOrDefault(document => document.Id == result.Document.Id) ?? new(result.Document); item.Update(result); return item; }).ToArray();
        _overviewResults = results.ToArray();
        _list.ItemsSource = items;
        _list.SelectedItem = items.FirstOrDefault(d => d.Id == _active.Id);
        this.FindControl<TextBlock>("DocumentCount")!.Text = items.Length.ToString();
        _loading = false;
        RefreshOverview();
    }

    private void ShowActive(bool refreshPage = true)
    {
        _loading = true;
        _title.Text = _active.Title;
        UpdateDocumentLabels();
        _status.Text = "已保存到本机";
        _loading = false;
        FilterDocuments();
        UpdateHistoryButtons();
        UpdateWordCount(_editor.Session.Root);
        RefreshReferences();
        if (refreshPage) ApplyPageAppearance();
        _tools.RefreshExtendedPanel(refreshPage);
    }

    private void UpdateHistoryButtons()
    {
        this.FindControl<Button>("UndoButton")!.IsEnabled = !_overviewVisible && _editor.IsEnabled && _editor.Session.CanUndo;
        this.FindControl<Button>("RedoButton")!.IsEnabled = !_overviewVisible && _editor.IsEnabled && _editor.Session.CanRedo;
    }

    private void UpdateWordCount(NoteNode root)
    {
        var count = NoteTree.Descendants(root).Where(n => n.Type == "text").Sum(n => n.Text.Count(c => !char.IsWhiteSpace(c)));
        this.FindControl<TextBlock>("WordCount")!.Text = $"{count:N0} 字";
    }

    private void OpenDocumentMenu()
    {
        var export = new MenuItem { Header = "导出笔记 JSON…" };
        export.Click += async (_, _) => await ExportAsync();
        var delete = new MenuItem { Header = "移至回收站" };
        delete.Click += async (_, _) => await DeleteAsync();
        new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Menu(_active.IsFavorite ? "取消收藏" : "收藏文档", ToggleFavorite),
                Menu("链接到笔记… · Ctrl+Shift+K", () => _editor.References.Open()),
                AsyncMenu("移动到…", MoveActiveAsync),
                AsyncMenu("创建副本", DuplicateAsync),
                Menu("页面样式", () => _tools.Open(EditorPanel.Page)),
                Menu("页面信息", () => _tools.Open(EditorPanel.Info)),
                Menu("演示文档", Present),
                AsyncMenu("查看本地备份…", RestoreRevisionAsync),
                new Separator(),
                Menu("展开全部折叠块", () => { if (_editor.IsEnabled) _editor.Session.SetAllCollapsed(false); _editor.FocusText(); }),
                Menu("收起全部折叠块", () => { if (_editor.IsEnabled) _editor.Session.SetAllCollapsed(true); _editor.FocusText(); }),
                export, AsyncMenu("导出 Markdown…", () => ExportTextAsync(true)), AsyncMenu("导出纯文本…", () => ExportTextAsync(false)), new Separator(), delete
            }
        }.Open(this.FindControl<Button>("MoreButton")!);
    }

    private void OpenLibraryMenu()
    {
        new ContextMenu { ItemsSource = new Control[] {
            AsyncMenu("新建空间…", CreateSpaceAsync), AsyncMenu("新建文件夹…", () => CreateFolderAsync(_folderId)),
            AsyncMenu("导入文档或资料库…", ImportAsync), AsyncMenu("导出资料库备份…", ExportBackupAsync), AsyncMenu("设置…", SettingsAsync),
            Menu("回收站", () => SelectLibrary("trash")),
            Menu("打开本地资料库文件夹", () => Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_store.DatabasePath)!) { UseShellExecute = true }))
        } }.Open(this.FindControl<Button>("LibraryButton")!);
    }

    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出笔记", SuggestedFileName = "WriteME-note.json", DefaultExtension = "json", FileTypeChoices = [new("笔记 JSON") { Patterns = ["*.json"] }] });
        if (file == null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(NoteJson.Serialize(_editor.Session.Root));
            _status.Text = "笔记已导出";
        }
        catch (IOException ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    private async Task DeleteAsync()
    {
        var id = _active.Id;
        if (_switching || _syncApplying || ActiveHasConflict || !await SaveAsync() || _active.Id != id) return;
        _store.SetTrashed(id, true);
        _trashUndoId = id;
        if (_store.List().Count == 0) _store.Create();
        _libraryMode = "all"; _tagFilter = null; _folderId = null;
        ReloadDocuments();
        await SwitchAsync(_store.List()[0].Id);
        this.FindControl<Border>("TrashUndoNotice")!.IsVisible = true;
        _status.Text = "已移至回收站";
    }

    private async Task RestoreLastTrashedAsync()
    {
        if (_trashUndoId is not { } id || _switching || _syncApplying || !await SaveAsync()) return;
        _store.SetTrashed(id, false);
        var location = _store.Location(id); _spaceId = location.SpaceId; _folderId = location.FolderId; _tagFilter = null; _libraryMode = "all";
        _trashUndoId = null; this.FindControl<Border>("TrashUndoNotice")!.IsVisible = false;
        ShowDocumentView(); ReloadDocuments(); await SwitchAsync(id); _status.Text = "已恢复文档"; _editor.FocusText();
    }

}
