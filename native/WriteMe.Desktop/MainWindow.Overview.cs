using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;

namespace WriteMe.Desktop;

// Note: 总览复用资料库查询，卡片点击与左栏导航共用保存/打开路径 — 见 .agents/notes/implemented/feature/2026-09-10-library-and-search.md
public sealed partial class MainWindow
{
    private Grid? _overview;
    private readonly WrapPanel _overviewCards = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _overviewHeading = new() { FontSize = 28, FontWeight = FontWeight.Bold, Foreground = Ui.Ink };
    private readonly TextBlock _overviewCount = new() { FontSize = 12, Foreground = Ui.Muted, Margin = new(0, 10, 0, 20) };
    private readonly TextBlock _overviewEmpty = new() { FontSize = 15, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(4, 45) };
    private readonly Button _overviewMore = new() { Content = "显示更多文档", HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 20, 0, 10) };
    private LibraryResult[] _overviewResults = [];
    private bool _overviewVisible;
    private bool _overviewRequested;
    private int _overviewLimit = 40;

    private void InitializeOverview()
    {
        _overview = new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new(36, 24, 28, 20), IsVisible = false };
        AutomationProperties.SetAutomationId(_overview, "LibraryOverview");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(0, 10, 0, 0) }; header.Children.Add(_overviewHeading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var current = new Button { Content = "继续编辑", Classes = { "quiet" } }; current.Click += (_, _) => ShowDocumentView();
        var create = new Button { Content = "+ 新建文档", Classes = { "primary" } }; create.Click += async (_, _) => await RunUiAsync(NewAsync);
        AutomationProperties.SetAutomationId(create, "OverviewNew"); actions.Children.Add(current); actions.Children.Add(create); Grid.SetColumn(actions, 1); header.Children.Add(actions);
        _overview.Children.Add(header); Grid.SetRow(_overviewCount, 1); _overview.Children.Add(_overviewCount);
        var body = new StackPanel(); body.Children.Add(_overviewCards); body.Children.Add(_overviewEmpty); body.Children.Add(_overviewMore);
        _overviewMore.Click += (_, _) => { _overviewLimit += 40; RefreshOverview(); };
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 2); _overview.Children.Add(scroll);
        Grid.SetRow(_overview, 1); Grid.SetColumn(_overview, 1); this.FindControl<Grid>("Shell")!.Children.Add(_overview);
    }
    private async Task ShowLibraryOverviewAsync()
    {
        _overviewRequested = true;
        if (!await SaveAsync() || !_overviewRequested) return;
        _overviewVisible = true; _overviewLimit = 40; _tools.Close(); _tools.IsVisible = false;
        this.FindControl<Grid>("DocumentRegion")!.IsVisible = false; _overview!.IsVisible = true;
        SetDocumentActionsVisible(false);
        RefreshOverview(); UpdateSidebars();
    }
    private void ShowDocumentView()
    {
        _overviewRequested = _overviewVisible = false;
        if (_overview != null) _overview.IsVisible = false;
        this.FindControl<Grid>("DocumentRegion")!.IsVisible = true; _tools.IsVisible = true; UpdateSidebars();
        SetDocumentActionsVisible(true);
    }
    private void SetDocumentActionsVisible(bool visible)
    {
        foreach (var name in new[] { "FavoriteButton", "UndoButton", "RedoButton", "MoreButton" }) this.FindControl<Button>(name)!.IsVisible = visible;
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
        foreach (var result in _overviewResults.Take(_overviewLimit))
        {
            var info = result.Document; var document = _store.Get(info.Id);
            var card = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(18), Height = 168 };
            card.Children.Add(new TextBlock { Text = NoteReferences.DisplayTitle(info.Title), FontSize = 16, FontWeight = FontWeight.SemiBold, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Ink });
            var text = result.Preview.Length > 0 ? result.Preview : DocumentText.Plain(NoteJson.Parse(document.Content));
            var preview = new TextBlock { Text = text[..Math.Min(300, text.Length)], FontSize = 12, Foreground = Ui.Muted, LineHeight = 21, MaxLines = 4, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 13, 0, 9) };
            Grid.SetRow(preview, 1); card.Children.Add(preview);
            var footer = new Grid { ColumnDefinitions = new("*,Auto") };
            footer.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(info.UpdatedAt).LocalDateTime.ToString("M月d日 更新"), FontSize = 10, Foreground = Ui.Muted });
            if (info.IsFavorite) { var star = new TextBlock { Text = "★", Foreground = Ui.Accent, FontSize = 12 }; Grid.SetColumn(star, 1); footer.Children.Add(star); }
            Grid.SetRow(footer, 2); card.Children.Add(footer);
            var button = new Button { Content = card, Width = 236, Height = 206, Margin = new(0, 0, 16, 16), Padding = new(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
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
    }
}
