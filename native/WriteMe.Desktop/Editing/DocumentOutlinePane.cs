using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: Craft 的目录属于左侧当前文档导航，只收录标题 — 见 .agents/notes/implemented/feature/2026-09-09-editor-sidebar.md
public sealed partial class DocumentOutlinePane : UserControl
{
    private readonly BlockEditor _owner;
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _updated = new() { FontSize = 11, Foreground = Ui.Chrome("#A1A5AC"), Margin = new(0, 3, 0, 0) };
    private readonly ListBox _list = new() { BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0), Margin = new(10, 0, 12, 0), Focusable = true };
    private readonly TextBlock _empty = new() { Text = "使用标题创建目录。", FontSize = 12, Foreground = Ui.Chrome("#93989F"), Margin = new(18, 9, 16, 0), TextWrapping = TextWrapping.Wrap };
    private NoteNode? _root;
    private DocumentSession? _session;
    private OutlineEntry[] _entries = [];
    private bool _queued;

    public DocumentOutlinePane(BlockEditor owner)
    {
        _owner = owner;
        AutomationProperties.SetName(this, "当前文档目录");
        var layout = new Grid { RowDefinitions = new("Auto,Auto,Auto,*") };
        var header = new Grid { ColumnDefinitions = new("38,*"), Margin = new(17, 10, 17, 16) };
        header.Children.Add(new Border
        {
            Width = 29, Height = 36, Background = Ui.Surface, BorderBrush = Ui.Chrome("#E7E9ED"),
            BorderThickness = new(1), CornerRadius = new(5), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new SidebarGlyph(SidebarSymbol.Document, 19) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(_title);
        labels.Children.Add(_updated);
        Grid.SetColumn(labels, 1);
        header.Children.Add(labels);
        layout.Children.Add(header);
        var tabs = BuildNavigationTabs(); Grid.SetRow(tabs, 1); layout.Children.Add(tabs);
        Grid.SetRow(_sectionHeader, 2); layout.Children.Add(_sectionHeader);
        _list.Classes.Add("documentOutline");
        AutomationProperties.SetName(_list, "文档目录");
        AutomationProperties.SetAutomationId(_list, "DocumentOutline");
        _list.ItemTemplate = new FuncDataTemplate<OutlineEntry>((entry, _) =>
        {
            if (entry == null) return new TextBlock();
            var text = new TextBlock
            {
                Text = entry.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = entry.HeadingLevel == 1 ? FontWeight.Medium : FontWeight.Normal,
                Margin = new(entry.Depth * 14, 0, 0, 0)
            };
            ToolTip.SetTip(text, entry.Title);
            return text;
        });
        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_empty);
        InitializeNavigation(body);
        Grid.SetRow(_navigationPanels, 3);
        layout.Children.Add(_navigationPanels);
        Content = layout;
        _list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is Control source
                && (source is ListBoxItem || source.GetVisualAncestors().OfType<ListBoxItem>().Any())) NavigateSelected();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { NavigateSelected(); e.Handled = true; } };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { if (!IsComposing) _owner.FocusText(); e.Handled = true; }
        }, RoutingStrategies.Bubble);
        owner.SelectionChanged += (_, _) => QueueRefresh();
        owner.InputClient.PreeditChanged += (_, _) => QueueRefresh();
        owner.PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) QueueRefresh(); };
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) QueueRefresh(); };
        SetDocument("当前文档", DateTimeOffset.Now.ToUnixTimeMilliseconds());
        Refresh();
    }

    public void SetDocument(string title, long updatedAt)
    {
        _title.Text = string.IsNullOrWhiteSpace(title) ? "无标题" : title;
        ToolTip.SetTip(_title, _title.Text);
        _updated.Text = DateTimeOffset.FromUnixTimeMilliseconds(updatedAt).LocalDateTime.ToString("M月d日 HH:mm") + " 更新";
    }

    public void FocusOutline()
    {
        ShowPanel(DocumentPanel.Outline);
        Refresh();
        if (_list.SelectedIndex < 0 && _entries.Length > 0) _list.SelectedIndex = 0;
        var session = _owner.Session;
        Dispatcher.UIThread.Post(() => { if (IsEffectivelyVisible && ReferenceEquals(session, _owner.Session)) _list.Focus(); }, DispatcherPriority.Input);
    }

    private void NavigateSelected()
    {
        if (!CanNavigate() || _list.SelectedItem is not OutlineEntry entry || !_entries.Contains(entry)) return;
        _owner.NavigateTo(entry.NodeId);
    }

    private bool CanNavigate() => _owner.IsEffectivelyEnabled && IsEffectivelyEnabled && !_owner.IsAnyComposing && !IsComposing
        && ReferenceEquals(_session, _owner.Session) && ReferenceEquals(_root, _owner.Session.Root)
        && _owner.Surface.Document.TextLength == _owner.Session.Projection.Text.Length;

    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        if (!IsVisible) return;
        var session = _owner.Session;
        var changed = !ReferenceEquals(_root, session.Root) || !ReferenceEquals(_session, session);
        if (changed)
        {
            var switched = !ReferenceEquals(_session, session);
            _root = session.Root;
            _session = session;
            _entries = DocumentOutline.Read(session.Root).ToArray();
            _list.ItemsSource = _entries;
            if (switched) ResetNavigationDocument();
        }
        _empty.IsVisible = _entries.Length == 0;
        _list.IsEnabled = CanNavigate();
        if (!_list.IsKeyboardFocusWithin)
            _list.SelectedItem = _entries.FirstOrDefault(entry => entry.NodeId == session.Selection.Caret.NodeId);
        RefreshNavigation(changed);
    }
}
