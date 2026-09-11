using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public enum DocumentPanel { Outline, Tasks, Resources, Find }

public sealed partial class DocumentOutlinePane
{
    private readonly Grid _navigationPanels = new();
    private readonly Grid _sectionHeader = new() { ColumnDefinitions = new("*,Auto"), Margin = new(18, 0, 16, 8) };
    private readonly TextBlock _caption = new() { Text = "目录", FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<DocumentPanel, Button> _navigationTabs = [];
    private readonly Dictionary<DocumentPanel, Control> _navigationViews = [];
    private readonly ListBox _tasks = ResultsList("DocumentTasks", "当前文档任务");
    private readonly ListBox _resources = ResultsList("DocumentResources", "当前文档附件与链接");
    private readonly ListBox _findResults = ResultsList("DocumentFindResults", "文内查找结果");
    private readonly TextBlock _taskEmpty = EmptyMessage("本文档中的任务将出现在此处。");
    private readonly TextBlock _resourceEmpty = EmptyMessage("添加到本文档的图片、文件和链接将显示在此处。");
    private readonly TextBox _findBox = new() { Watermark = "在文档中查找文本", FontSize = 12, MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "clean" } };
    private readonly TextBox _replaceBox = new() { Watermark = "替换为…", FontSize = 12, MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "clean" } };
    private readonly TextBlock _findCount = new() { FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _findStatus = new() { FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new(0, 8, 0, 0) };
    private readonly StackPanel _replaceControls = new() { Spacing = 8, IsVisible = false };
    private Button _viewOptions = null!;
    private Button _findMode = null!;
    private Button _matchCase = null!;
    private Button _replaceOne = null!;
    private Button _replaceAll = null!;
    private Button _findPrevious = null!;
    private Button _findNext = null!;
    private Button _findClear = null!;
    private DocumentSearchResult? _findResult;
    private int _currentMatch = -1;
    private int _taskFilter;
    private string _resourceFilter = "";
    private bool _caseSensitive;
    private bool _refreshingFind;
    public DocumentPanel ActivePanel { get; private set; }
    public bool IsComposing => this.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
    public event EventHandler? PanelChanged;

    private static TextBlock EmptyMessage(string text) => new() { Text = text, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(18, 9, 18, 0), LineHeight = 21 };
    private static ListBox ResultsList(string id, string label)
    {
        var list = new ListBox { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Margin = new(10, 0, 12, 0), Focusable = true, Classes = { "documentOutline" } };
        AutomationProperties.SetAutomationId(list, id); AutomationProperties.SetName(list, label);
        return list;
    }
    private static Button NavButton(object content, string label, string id, Action action)
    {
        var button = new Button { Content = content, Classes = { "quiet" }, Padding = new(6, 4), MinHeight = 26, FontSize = 11, Foreground = Ui.Chrome("#747A84") };
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
        button.Click += (_, _) => action(); return button;
    }

    private Control BuildNavigationTabs()
    {
        var row = new Grid { ColumnDefinitions = new("*,*,*,*"), Margin = new(2) };
        foreach (var (panel, symbol, label) in new[] { (DocumentPanel.Outline, SidebarSymbol.Outline, "目录"), (DocumentPanel.Tasks, SidebarSymbol.TaskCircle, "任务"),
                     (DocumentPanel.Resources, SidebarSymbol.Attachment, "附件与链接"), (DocumentPanel.Find, SidebarSymbol.Search, "查找与替换") })
        {
            var button = NavButton(new SidebarGlyph(symbol, 17), label, "DocumentTab" + panel, () => { ShowPanel(panel); if (panel == DocumentPanel.Find) FocusFind(); });
            button.Classes.Add("documentTab"); button.Height = 28; button.Padding = new(0); button.HorizontalAlignment = HorizontalAlignment.Stretch;
            _navigationTabs.Add(panel, button); Grid.SetColumn(button, (int)panel); row.Children.Add(button);
        }
        return new Border { Child = row, Background = Ui.Chrome("#F1F3F5"), CornerRadius = new(17), Margin = new(16, 0, 18, 16) };
    }

    private void InitializeNavigation(Control outline)
    {
        _sectionHeader.Children.Add(_caption);
        _viewOptions = NavButton("全部", "筛选当前列表", "DocumentViewOptions", OpenViewOptions);
        Grid.SetColumn(_viewOptions, 1); _sectionHeader.Children.Add(_viewOptions);
        _tasks.ItemTemplate = new FuncDataTemplate<DocumentTask>((task, _) =>
        {
            if (task == null) return new TextBlock();
            var root = _root; var session = _session;
            var row = new Grid { ColumnDefinitions = new("28,*") };
            var check = new CheckBox { IsChecked = task.Completed, MinWidth = 22, MinHeight = 22, VerticalAlignment = VerticalAlignment.Top, Padding = new(0), Classes = { "documentTaskCheck" } };
            AutomationProperties.SetAutomationId(check, "DocumentTaskToggle_" + task.TaskId); AutomationProperties.SetName(check, (task.Completed ? "标为未完成：" : "完成任务：") + task.Text);
            check.Click += (_, e) =>
            {
                e.Handled = true;
                if (!Current(root, session) || NoteTree.Find(session!.Root, task.TaskId)?.Bool("checked") != task.Completed) { check.IsChecked = task.Completed; Refresh(); return; }
                session.BreakTypingGroup(); session.ToggleTask(task.TaskId); Refresh();
            };
            row.Children.Add(check);
            var content = new StackPanel { Spacing = 3, Margin = new(2, 1, 0, 0) };
            content.Children.Add(new TextBlock { Text = task.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = task.Completed ? Ui.Muted : Ui.Ink, TextDecorations = task.Completed ? TextDecorations.Strikethrough : null });
            if (task.Context.Length > 0) content.Children.Add(new TextBlock { Text = task.Context, FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(content, 1); row.Children.Add(content); return row;
        });
        _resources.ItemTemplate = new FuncDataTemplate<DocumentResource>((resource, _) =>
        {
            if (resource == null) return new TextBlock();
            var root = _root; var session = _session;
            var row = new Grid { ColumnDefinitions = new("31,*,26") };
            row.Children.Add(new Border { Width = 24, Height = 30, Background = Ui.Subtle, CornerRadius = new(5), VerticalAlignment = VerticalAlignment.Top,
                Child = new SidebarGlyph(resource.Kind == "image" ? SidebarSymbol.Image : resource.Kind == "link" ? SidebarSymbol.Link : SidebarSymbol.Document, 15)
                    { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
            var labels = new StackPanel { Spacing = 4 };
            labels.Children.Add(new TextBlock { Text = resource.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            labels.Children.Add(new TextBlock { Text = resource.Context.Length > 0 ? resource.Context : resource.Kind == "link" ? resource.Source : resource.Kind == "image" ? "图片" : "文件",
                FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(labels, 1); row.Children.Add(labels);
            var open = NavButton(new SidebarGlyph(SidebarSymbol.Open, 13), "打开 " + resource.Name, "DocumentResourceOpen_" + resource.NodeId + "_" + resource.Start, () =>
            {
                if (!Current(root, session)) return;
                if (resource.Kind == "link") _owner.OpenAsset(new NoteNode("attachment").WithAttr("src", resource.Source));
                else if (NoteTree.Find(session!.Root, resource.NodeId) is { } node) _owner.OpenAsset(node);
            });
            open.VerticalAlignment = VerticalAlignment.Top; open.Padding = new(3); Grid.SetColumn(open, 2); row.Children.Add(open); return row;
        });
        _navigationViews[DocumentPanel.Outline] = outline;
        _navigationViews[DocumentPanel.Tasks] = new Grid { Children = { _tasks, _taskEmpty } };
        _navigationViews[DocumentPanel.Resources] = new Grid { Children = { _resources, _resourceEmpty } };
        _navigationViews[DocumentPanel.Find] = BuildFindPanel();
        foreach (var view in _navigationViews.Values) _navigationPanels.Children.Add(view);
        WireNavigation(_tasks, () => { if (CanNavigate() && _tasks.SelectedItem is DocumentTask task) _owner.NavigateTo(task.NodeId); });
        WireNavigation(_resources, () => { if (CanNavigate() && _resources.SelectedItem is DocumentResource resource) _owner.NavigateTo(resource.NodeId, resource.Start, resource.Length); });
        WireNavigation(_findResults, () => { if (_findResults.SelectedItem is DocumentSearchMatch match && _findResult != null) NavigateMatch(_findResult.Matches.IndexOf(match), false); });
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) UpdateSearchHighlights(); };
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!IsComposing || e.Source is not Visual source) return;
            if (source == _findBox || source == _replaceBox || source.GetVisualAncestors().Any(ancestor => ancestor == _findBox || ancestor == _replaceBox)) return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        ShowPanel(DocumentPanel.Outline);
    }

    private static void WireNavigation(ListBox list, Action action)
    {
        list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Control source) return;
            if (source is Button || source.GetVisualAncestors().OfType<Button>().Any()) return;
            if (source is ListBoxItem || source.GetVisualAncestors().OfType<ListBoxItem>().Any()) action();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { action(); e.Handled = true; } };
    }

    private bool Current(NoteNode? root, DocumentSession? session) => CanNavigate() && ReferenceEquals(root, _owner.Session.Root) && ReferenceEquals(session, _owner.Session);

    public void ShowPanel(DocumentPanel panel)
    {
        if (IsComposing || _owner.IsAnyComposing || !_navigationViews.ContainsKey(panel)) return;
        ActivePanel = panel;
        foreach (var (key, button) in _navigationTabs) button.Classes.Set("active", key == panel);
        foreach (var (key, view) in _navigationViews) view.IsVisible = key == panel;
        _sectionHeader.IsVisible = panel != DocumentPanel.Find;
        _viewOptions.IsVisible = panel is DocumentPanel.Tasks or DocumentPanel.Resources;
        _caption.Text = panel switch { DocumentPanel.Tasks => "任务", DocumentPanel.Resources => "附件与链接", _ => "目录" };
        Refresh(); UpdateSearchHighlights(); PanelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OpenViewOptions()
    {
        if (!CanNavigate()) return;
        var options = new List<MenuItem>();
        if (ActivePanel == DocumentPanel.Tasks)
            foreach (var (name, value) in new[] { ("全部任务", 0), ("未完成", 1), ("已完成", 2) })
            {
                var item = new MenuItem { Header = name, IsChecked = _taskFilter == value, ToggleType = MenuItemToggleType.Radio };
                item.Click += (_, _) => { _taskFilter = value; RefreshNavigation(true); }; options.Add(item);
            }
        else
            foreach (var (name, value) in new[] { ("全部类型", ""), ("图片", "image"), ("文件", "attachment"), ("链接", "link") })
            {
                var item = new MenuItem { Header = name, IsChecked = _resourceFilter == value, ToggleType = MenuItemToggleType.Radio };
                item.Click += (_, _) => { _resourceFilter = value; RefreshNavigation(true); }; options.Add(item);
            }
        _viewOptions.ContextMenu = new ContextMenu { ItemsSource = options }; _viewOptions.ContextMenu.Open(_viewOptions);
    }

    private Control BuildFindPanel()
    {
        var layout = new Grid { RowDefinitions = new("Auto,*") };
        var controls = new StackPanel { Margin = new(18, 0, 18, 8), Spacing = 8 };
        var modes = new Grid { ColumnDefinitions = new("*,Auto") };
        _findMode = NavButton("查找 ⌄", "选择查找或查找和替换", "DocumentFindMode", () =>
        {
            if (IsComposing) return;
            var find = new MenuItem { Header = "查找" }; var replace = new MenuItem { Header = "查找和替换" };
            find.Click += (_, _) => SetReplaceMode(false); replace.Click += (_, _) => SetReplaceMode(true);
            _findMode.ContextMenu = new ContextMenu { ItemsSource = new[] { find, replace } }; _findMode.ContextMenu.Open(_findMode);
        });
        _findMode.HorizontalAlignment = HorizontalAlignment.Left; _findMode.Padding = new(0, 0, 6, 0); modes.Children.Add(_findMode);
        _matchCase = NavButton("Aa", "区分大小写", "DocumentFindCase", () => { if (!IsComposing) { _caseSensitive = !_caseSensitive; _matchCase.Classes.Set("active", _caseSensitive); RefreshFind(); } });
        Grid.SetColumn(_matchCase, 1); modes.Children.Add(_matchCase); controls.Children.Add(modes);
        Control Field(TextBox input, SidebarSymbol icon, string id, string name)
        {
            AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, name); input.MaxLength = 2048;
            var row = new Grid { ColumnDefinitions = new("22,*,Auto"), Margin = new(9, 6) };
            row.Children.Add(new SidebarGlyph(icon, 13) { VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(input, 1); row.Children.Add(input);
            if (ReferenceEquals(input, _findBox))
            {
                _findClear = NavButton(new SidebarGlyph(SidebarSymbol.Close, 10), "清空查找", "DocumentFindClear", () => { if (!IsComposing) { _findBox.Text = ""; _findBox.Focus(); } });
                _findClear.IsVisible = false; _findClear.MinHeight = 16; _findClear.Padding = new(3, 0); Grid.SetColumn(_findClear, 2); row.Children.Add(_findClear);
            }
            return new Border { Background = Ui.Chrome("#F4F5F7"), BorderThickness = new(1), CornerRadius = new(9), Child = row, Classes = { "documentSearchField" } };
        }
        controls.Children.Add(Field(_findBox, SidebarSymbol.Search, "DocumentFindInput", "在当前文档中查找"));
        _replaceControls.Children.Add(Field(_replaceBox, SidebarSymbol.Redo, "DocumentReplaceInput", "替换为"));
        var replaceRow = new Grid { ColumnDefinitions = new("*,*") };
        _replaceOne = NavButton("替换", "替换当前匹配", "DocumentReplaceOne", () => ReplaceMatches(false));
        _replaceAll = NavButton("全部替换", "一次替换本文档的全部匹配，可撤销", "DocumentReplaceAll", () => ReplaceMatches(true));
        foreach (var button in new[] { _replaceOne, _replaceAll }) { button.Classes.Add("findAction"); button.HorizontalAlignment = HorizontalAlignment.Stretch; }
        _replaceOne.Margin = new(0, 0, 4, 0); _replaceAll.Margin = new(4, 0, 0, 0);
        replaceRow.Children.Add(_replaceOne); Grid.SetColumn(_replaceAll, 1); replaceRow.Children.Add(_replaceAll); _replaceControls.Children.Add(replaceRow); controls.Children.Add(_replaceControls);
        var summary = new Grid { ColumnDefinitions = new("*,Auto,Auto"), Margin = new(0, 4, 0, 0) }; summary.Children.Add(_findCount);
        _findPrevious = NavButton(new SidebarGlyph(SidebarSymbol.ArrowUp, 15), "上一处 · Shift+Enter", "DocumentFindPrevious", () => MoveMatch(-1));
        _findNext = NavButton(new SidebarGlyph(SidebarSymbol.ArrowDown, 15), "下一处 · Enter", "DocumentFindNext", () => MoveMatch(1));
        Grid.SetColumn(_findPrevious, 1); summary.Children.Add(_findPrevious); Grid.SetColumn(_findNext, 2); summary.Children.Add(_findNext); controls.Children.Add(summary);
        controls.Children.Add(_findStatus); layout.Children.Add(controls);
        Grid.SetRow(_findResults, 1); layout.Children.Add(_findResults);
        _findBox.TextChanged += (_, _) => { if (!_refreshingFind) { _findStatus.IsVisible = false; QueueRefresh(); } };
        _findBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true; if (!IsComposing) MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
        }, RoutingStrategies.Tunnel);
        _replaceBox.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; if (!IsComposing) ReplaceMatches(e.KeyModifiers.HasFlag(KeyModifiers.Control)); } }, RoutingStrategies.Tunnel);
        _findResults.ItemTemplate = new FuncDataTemplate<DocumentSearchMatch>((hit, _) =>
        {
            if (hit == null) return new TextBlock();
            var start = Math.Max(0, hit.Start - 26); var end = Math.Min(hit.Text.Length, hit.Start + hit.Length + 55);
            var text = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 20 };
            text.Inlines!.Add(new Run((start > 0 ? "…" : "") + hit.Text[start..hit.Start].Replace('\u2028', ' ')));
            text.Inlines.Add(new Run(hit.Text.Substring(hit.Start, hit.Length).Replace('\u2028', ' ')) { FontWeight = FontWeight.Medium, Background = Ui.Chrome("#FFF0C2"), Foreground = Ui.Ink });
            text.Inlines.Add(new Run(hit.Text[(hit.Start + hit.Length)..end].Replace('\u2028', ' ') + (end < hit.Text.Length ? "…" : "")));
            return new StackPanel { Spacing = 4, Children = { text, new TextBlock { Text = hit.Context, IsVisible = hit.Context.Length > 0, FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis } } };
        });
        return layout;
    }

    public void FocusFind(bool replace = false)
    {
        if (IsComposing || _owner.IsAnyComposing) return;
        ShowPanel(DocumentPanel.Find); if (replace) SetReplaceMode(true);
        var session = _owner.Session;
        Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible && ReferenceEquals(session, _owner.Session)) { _findBox.Focus(); _findBox.SelectAll(); } }, DispatcherPriority.Input);
    }
    private void SetReplaceMode(bool replace)
    {
        if (IsComposing) return;
        _replaceControls.IsVisible = replace; _findMode.Content = replace ? "查找和替换 ⌄" : "查找 ⌄"; RefreshFind();
    }
    private void ResetNavigationDocument()
    {
        _refreshingFind = true; _findBox.Text = ""; _replaceBox.Text = ""; _refreshingFind = false;
        _findResult = null; _currentMatch = -1; _findStatus.IsVisible = false; _owner.SetSearchHighlights(null);
    }
    private void RefreshNavigation(bool changed)
    {
        if (_root == null) return;
        var index = DocumentNavigation.For(_root);
        if (changed || _tasks.ItemsSource == null)
        {
            var tasks = index.Tasks.Where(task => _taskFilter == 0 || task.Completed == (_taskFilter == 2)).ToArray();
            _tasks.ItemsSource = tasks; _taskEmpty.IsVisible = tasks.Length == 0;
            _taskEmpty.Text = index.Tasks.Length == 0 ? "本文档中的任务将出现在此处。" : "没有符合筛选的任务。";
            var resources = index.Resources.Where(resource => _resourceFilter.Length == 0 || resource.Kind == _resourceFilter).ToArray();
            _resources.ItemsSource = resources; _resourceEmpty.IsVisible = resources.Length == 0;
        }
        if (ActivePanel == DocumentPanel.Tasks)
        {
            _caption.Text = $"任务 · {index.Tasks.Count(task => task.Completed)}/{index.Tasks.Length}";
            _viewOptions.Content = _taskFilter switch { 1 => "未完成 ⌄", 2 => "已完成 ⌄", _ => "全部 ⌄" };
        }
        if (ActivePanel == DocumentPanel.Resources) _viewOptions.Content = _resourceFilter switch { "image" => "图片 ⌄", "attachment" => "文件 ⌄", "link" => "链接 ⌄", _ => "全部 ⌄" };
        _tasks.IsEnabled = CanNavigate(); _resources.IsEnabled = CanNavigate();
        RefreshFind();
    }
    private void RefreshFind()
    {
        if (_root == null || IsComposing) return;
        var query = _findBox.Text ?? "";
        if (_findResult == null || !ReferenceEquals(_findResult.Root, _root) || _findResult.Query != query || _findResult.CaseSensitive != _caseSensitive)
        {
            var current = _currentMatch >= 0 && _findResult != null && _currentMatch < _findResult.Matches.Length ? _findResult.Matches[_currentMatch] : null;
            var sameQuery = _findResult?.Query == query && _findResult?.CaseSensitive == _caseSensitive;
            _findResult = DocumentNavigation.For(_root).Search(query, _caseSensitive);
            _currentMatch = sameQuery && current != null ? FindMatchIndex(current.NodeId, current.Start) : -1;
            _findResults.ItemsSource = _findResult.Matches;
            _findResults.SelectedIndex = _currentMatch;
        }
        var count = _findResult.Matches.Length;
        _findClear.IsVisible = query.Length > 0;
        _findCount.Text = count == 0 ? "暂无结果" : (_currentMatch >= 0 ? $"{_currentMatch + 1} / " : "") + count.ToString("N0") + (_findResult.Truncated ? "+ 个结果" : " 个结果");
        _findPrevious.IsEnabled = _findNext.IsEnabled = count > 0 && CanNavigate();
        _replaceOne.IsEnabled = count > 0 && CanNavigate(); _replaceAll.IsEnabled = _replaceOne.IsEnabled && !_findResult.Truncated;
        _findResults.IsEnabled = CanNavigate(); UpdateSearchHighlights();
    }
    private void UpdateSearchHighlights() => _owner.SetSearchHighlights(IsEffectivelyVisible && ActivePanel == DocumentPanel.Find ? _findResult : null,
        _currentMatch >= 0 && _findResult != null && _currentMatch < _findResult.Matches.Length ? _findResult.Matches[_currentMatch] : null);
    internal void RefreshVisibility() => UpdateSearchHighlights();
    private int FindMatchIndex(Guid nodeId, int start)
    {
        if (_findResult != null)
            for (var index = 0; index < _findResult.Matches.Length; index++)
                if (_findResult.Matches[index].NodeId == nodeId && _findResult.Matches[index].Start == start) return index;
        return -1;
    }

    private void MoveMatch(int direction)
    {
        Refresh();
        if (_findResult == null || _findResult.Matches.IsEmpty || !CanNavigate()) return;
        var next = _currentMatch < 0 ? direction > 0 ? 0 : _findResult.Matches.Length - 1 : (_currentMatch + direction + _findResult.Matches.Length) % _findResult.Matches.Length;
        NavigateMatch(next, true);
    }
    private void NavigateMatch(int index, bool keepInput)
    {
        if (!CanNavigate() || _findResult == null || !ReferenceEquals(_findResult.Root, _owner.Session.Root) || index < 0 || index >= _findResult.Matches.Length) return;
        var hit = _findResult.Matches[index]; _currentMatch = index;
        _owner.NavigateTo(hit.NodeId, hit.Start, hit.Length); _owner.ActiveEditor.Formatting.Dismiss(); Refresh();
        _currentMatch = FindMatchIndex(hit.NodeId, hit.Start);
        _findResults.SelectedIndex = _currentMatch;
        if (_findResults.SelectedItem is { } selected) _findResults.ScrollIntoView(selected);
        RefreshFind();
        if (keepInput) RestoreFindFocus(_findBox);
    }
    private void ReplaceMatches(bool all)
    {
        if (!CanNavigate() || _findResult == null || _findResult.Matches.IsEmpty) return;
        var hit = all ? null : _findResult.Matches[Math.Max(0, _currentMatch)];
        try
        {
            var count = _owner.Session.ReplaceSearch(_findResult, _replaceBox.Text ?? "", hit);
            Refresh();
            if (hit != null && count > 0) { _owner.NavigateTo(hit.NodeId, hit.Start, (_replaceBox.Text ?? "").Length); _owner.ActiveEditor.Formatting.Dismiss(); }
            _findStatus.Text = count == 0 ? "内容相同，无需替换" : $"已替换 {count} 处 · 可在正文中撤销"; _findStatus.IsVisible = true;
            RestoreFindFocus(_replaceBox);
        }
        catch (InvalidOperationException ex) { _findStatus.Text = ex.Message; _findStatus.IsVisible = true; Refresh(); }
    }
    private void RestoreFindFocus(TextBox input)
    {
        input.Focus();
        var session = _owner.Session; var revision = session.Revision;
        // Entering a table cell also queues native editor focus after it is mounted.
        // Return the search composer after that work, without stealing focus after another edit.
        Dispatcher.UIThread.Post(() =>
        {
            if (ActivePanel == DocumentPanel.Find && IsEffectivelyVisible && ReferenceEquals(session, _owner.Session) && session.Revision == revision && !IsComposing)
                input.Focus();
        }, DispatcherPriority.Input);
    }
}
