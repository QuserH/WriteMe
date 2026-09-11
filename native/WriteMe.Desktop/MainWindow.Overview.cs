using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

// Note: 总览复用资料库查询，卡片点击与左栏导航共用保存/打开路径 — 见 .agents/notes/implemented/feature/2026-09-10-library-and-search.md
public sealed partial class MainWindow
{
    private Grid? _overview;
    private readonly WrapPanel _overviewCards = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _overviewHeading = new() { FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Ui.Ink };
    private readonly TextBlock _overviewCount = new() { FontSize = 13, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _overviewEmpty = new() { FontSize = 15, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(4, 45) };
    private readonly Button _overviewMore = new() { Content = "显示更多文档", HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 20, 0, 10) };
    private LibraryResult[] _overviewResults = [];
    private bool _overviewVisible;
    private bool _overviewRequested;
    private int _overviewLimit = 40;
    private readonly List<Bitmap> _overviewImages = [];
    private bool _overviewListMode;
    private int _overviewSort;

    private void InitializeOverview()
    {
        _overview = new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new(32, 28, 24, 20), IsVisible = false };
        AutomationProperties.SetAutomationId(_overview, "LibraryOverview");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 10, 0, 0) }; header.Children.Add(_overviewHeading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var current = new Button { Content = "继续编辑", FontSize = 13, Classes = { "quiet" } }; current.Click += (_, _) => { ShowDocumentView(); _editor.FocusText(); };
        var create = new Button { Content = "+ 新建文档", Classes = { "primary" } }; create.Click += async (_, _) => await RunUiAsync(NewAsync);
        AutomationProperties.SetAutomationId(create, "OverviewNew"); actions.Children.Add(current); actions.Children.Add(create); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        _overview.Children.Add(header);
        var subhead = new Grid { ColumnDefinitions = new("*,Auto,Auto"), Margin = new(0, 14, 0, 24) }; subhead.Children.Add(_overviewCount);
        var sort = new ComboBox { ItemsSource = new[] { "默认排序", "最近更新", "标题 A–Z", "最早创建" }, SelectedIndex = 0, MinWidth = 108, FontSize = 12, Padding = new(9, 5), Margin = new(6, 0, 12, 0), Background = Ui.Surface, BorderBrush = Ui.Line };
        AutomationProperties.SetAutomationId(sort, "OverviewSort"); AutomationProperties.SetName(sort, "文档排序");
        sort.SelectionChanged += (_, _) => { _overviewSort = sort.SelectedIndex; RefreshOverview(); }; Grid.SetColumn(sort, 1); subhead.Children.Add(sort);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var cardsMode = new Button { Content = new SidebarGlyph(SidebarSymbol.Grid, 17), Classes = { "quiet", "active" } };
        var listMode = new Button { Content = new SidebarGlyph(SidebarSymbol.Outline, 18), Classes = { "quiet" } };
        AutomationProperties.SetName(cardsMode, "卡片视图"); AutomationProperties.SetAutomationId(cardsMode, "OverviewCardsMode");
        AutomationProperties.SetName(listMode, "列表视图"); AutomationProperties.SetAutomationId(listMode, "OverviewListMode");
        void Mode(bool list) { _overviewListMode = list; cardsMode.Classes.Set("active", !list); listMode.Classes.Set("active", list); RefreshOverview(); }
        cardsMode.Click += (_, _) => Mode(false); listMode.Click += (_, _) => Mode(true);
        modes.Children.Add(cardsMode); modes.Children.Add(listMode); Grid.SetColumn(modes, 2); subhead.Children.Add(modes);
        Grid.SetRow(subhead, 1); _overview.Children.Add(subhead);
        var body = new StackPanel(); body.Children.Add(_overviewCards); body.Children.Add(_overviewEmpty); body.Children.Add(_overviewMore);
        _overviewMore.Click += (_, _) => { _overviewLimit += 40; RefreshOverview(); };
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); _overview.Children.Add(scroll);
        Grid.SetRow(_overview, 1); Grid.SetColumn(_overview, 1); this.FindControl<Grid>("Shell")!.Children.Add(_overview);
        _overview.SizeChanged += (_, _) => ArrangeOverviewCards();
        Closed += (_, _) => ClearOverviewImages();
    }
    private async Task ShowLibraryOverviewAsync()
    {
        _overviewRequested = true;
        if (!await SaveAsync() || !_overviewRequested) return;
        _overviewVisible = true; _overviewLimit = 40; _tools.Close(); _tools.IsVisible = false;
        CloseComments();
        this.FindControl<Grid>("DocumentRegion")!.IsVisible = false; _overview!.IsVisible = true;
        SetDocumentActionsVisible(false);
        RefreshOverview(); UpdateSidebars();
    }
    private void ShowDocumentView()
    {
        _overviewRequested = _overviewVisible = false;
        if (_overview != null) _overview.IsVisible = false;
        this.FindControl<Grid>("DocumentRegion")!.IsVisible = true; _tools.IsVisible = !_comments.IsVisible; UpdateSidebars();
        SetDocumentActionsVisible(true);
    }
    private void SetDocumentActionsVisible(bool visible)
    {
        foreach (var name in new[] { "FavoriteButton", "UndoButton", "RedoButton", "MoreButton", "CommentsButton" }) this.FindControl<Button>(name)!.IsVisible = visible;
        UpdateHistoryButtons();
    }
    private void RefreshOverview()
    {
        if (!_overviewVisible) return;
        _overviewHeading.Text = _search.Text?.Trim().Length > 0 ? "搜索结果" : _tagFilter != null ? "#" + _tagFilter
            : _folderId != null ? _store.Folders(_spaceId).FirstOrDefault(folder => folder.Id == _folderId)?.Name ?? "文件夹"
            : _libraryMode switch { "recent" => "最近打开", "favorites" => "收藏", "trash" => "回收站", _ => "所有文档" };
        _overviewCount.Text = _store.Spaces().First(space => space.Id == _spaceId).Name + "  ·  " + _overviewResults.Length + " 篇文档";
        _overviewEmpty.Text = _search.Text?.Length > 0 ? "还没有找到匹配的内容。试试其他关键词。" : _libraryMode == "favorites" ? "收藏常用的文档，让它们更容易找到。" : "从一篇新文档开始，记录值得留下的想法。";
        _overviewEmpty.IsVisible = _overviewResults.Length == 0; _overviewMore.IsVisible = _overviewResults.Length > _overviewLimit;
        _overviewCards.Children.Clear();
        ClearOverviewImages();
        var ordered = _overviewSort switch
        {
            1 => _overviewResults.OrderByDescending(result => result.Document.UpdatedAt),
            2 => _overviewResults.OrderBy(result => NoteReferences.DisplayTitle(result.Document.Title), StringComparer.CurrentCultureIgnoreCase),
            3 => _overviewResults.OrderBy(result => result.Document.CreatedAt),
            _ => _overviewResults.AsEnumerable()
        };
        foreach (var result in ordered.Take(_overviewLimit))
        {
            var info = result.Document; var document = _store.Get(info.Id);
            var root = NoteJson.Parse(document.Content);
            var appearance = _store.Appearance(info.Id);
            var card = new Grid { RowDefinitions = new("Auto,*"), ClipToBounds = true };
            var details = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new(20, 18) };
            var symbol = NoteTree.Descendants(root).Any(node => node.Type == "table") ? SidebarSymbol.Table
                : NoteTree.Descendants(root).Any(node => node.Type == "columnList") ? SidebarSymbol.Columns : SidebarSymbol.Document;
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new(0, 0, 0, 11) };
            typeRow.Children.Add(new SidebarGlyph(symbol, 15)); typeRow.Children.Add(new TextBlock { Text = symbol == SidebarSymbol.Table ? "表格笔记" : symbol == SidebarSymbol.Columns ? "分栏笔记" : "文档", FontSize = 11, Foreground = Ui.Muted });
            details.Children.Add(typeRow);
            var title = new TextBlock { Text = NoteReferences.DisplayTitle(info.Title), FontSize = 18, FontWeight = FontWeight.SemiBold, MaxLines = _overviewListMode ? 1 : 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Ink };
            Grid.SetRow(title, 1); details.Children.Add(title);
            var text = result.Preview.Length > 0 ? result.Preview : DocumentText.Plain(root);
            var preview = new TextBlock { Text = text.Length == 0 ? "还没有正文" : text[..Math.Min(300, text.Length)], FontSize = 13, Foreground = Ui.Chrome("#747A84"), LineHeight = 23, MaxLines = _overviewListMode ? 1 : 3, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 11, 0, 14) };
            Grid.SetRow(preview, 2); details.Children.Add(preview);
            var footer = new Grid { ColumnDefinitions = new("*,Auto") };
            footer.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(info.UpdatedAt).LocalDateTime.ToString("M月d日 更新"), FontSize = 11, Foreground = Ui.Muted });
            if (info.IsFavorite) { var star = new SidebarGlyph(SidebarSymbol.Star, 14); Avalonia.Controls.Documents.TextElement.SetForeground(star, Ui.Accent); Grid.SetColumn(star, 1); footer.Children.Add(star); }
            Grid.SetRow(footer, 3); details.Children.Add(footer);
            if (!_overviewListMode && appearance.CoverAssetId is { } cover && _store.AssetPath(cover) is { } path)
            {
                try
                {
                    using var source = File.OpenRead(path); var image = Bitmap.DecodeToWidth(source, 560); _overviewImages.Add(image);
                    card.Children.Add(new Border { Height = 96, CornerRadius = new(14, 14, 0, 0), ClipToBounds = true, Child = new Image { Source = image, Stretch = Stretch.UniformToFill } });
                    typeRow.IsVisible = false; details.RowDefinitions[0].Height = new(0); preview.MaxLines = 2; preview.Margin = new(0, 8, 0, 10);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { }
            }
            if (_overviewListMode)
            {
                typeRow.IsVisible = false; details.RowDefinitions = new("0,Auto,Auto,0"); footer.IsVisible = false;
                details.MinHeight = 0; details.Margin = new(20, 15); title.FontSize = 16; preview.Margin = new(0, 6, 0, 0);
            }
            Grid.SetRow(details, 1); card.Children.Add(details);
            var button = new Button { Content = card, Width = 260, Height = _overviewListMode ? 94 : 278, Margin = new(0, 0, 18, 18), Padding = new(0), ClipToBounds = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            button.Classes.Add("documentCard"); AutomationProperties.SetName(button, NoteReferences.DisplayTitle(info.Title)); AutomationProperties.SetAutomationId(button, "DocumentCard_" + info.Id);
            button.Click += async (_, _) => await RunUiAsync(() => OpenResultAsync(result));
            button.ContextMenu = new ContextMenu { ItemsSource = info.IsDeleted ? new Control[]
            {
                AsyncMenu("恢复文档", () => { _store.SetTrashed(info.Id, false); ReloadDocuments(); return Task.CompletedTask; }),
                AsyncMenu("永久删除…", async () => { if (await ConfirmAsync("永久删除", "这篇文档将从资料库永久删除。", "永久删除")) { _store.Delete(info.Id); ReloadDocuments(); } })
            } : new Control[]
            {
                Menu(info.IsFavorite ? "取消收藏" : "收藏文档", () => { _store.SetFavorite(info.Id, !info.IsFavorite); ReloadDocuments(); }),
                AsyncMenu("创建副本", async () => { await SwitchAsync(info.Id); await DuplicateAsync(); }),
                AsyncMenu("移动到…", async () => { await SwitchAsync(info.Id); await MoveActiveAsync(); }),
                AsyncMenu("移至回收站", async () => { await SwitchAsync(info.Id); await DeleteAsync(); })
            } };
            _overviewCards.Children.Add(button);
        }
        ArrangeOverviewCards();
    }

    private void ArrangeOverviewCards()
    {
        if (_overview == null || !_overviewVisible) return;
        var available = Math.Max(220, _overview.Bounds.Width - 10);
        var count = _overviewListMode ? 1 : Math.Clamp((int)((available + 18) / 258), 1, 4);
        var width = (available - (count - 1) * 18) / count;
        var index = 0;
        foreach (var button in _overviewCards.Children.OfType<Button>())
        {
            button.Width = width; button.Margin = new(0, 0, ++index % count == 0 ? 0 : 18, _overviewListMode ? 10 : 18);
        }
    }
    private void ClearOverviewImages() { foreach (var image in _overviewImages) image.Dispose(); _overviewImages.Clear(); }
}
