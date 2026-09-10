using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public enum EditorPanel { Insert, Style, Page, Info }

// Note: Craft 风格右侧插入/格式面板及焦点保护 — 见 .agents/notes/implemented/feature/2026-09-09-editor-sidebar.md
public sealed class EditorSidebar : UserControl
{
    public const double ToolWidth = 40;
    public const double PanelWidth = 280;
    private sealed record Target(DocumentSession Session, long Revision, int Start, int Length, int Caret, EditorSelection Selection);
    private readonly BlockEditor _owner;
    private BlockEditor Editor => _owner.ActiveEditor;
    private readonly Grid _layout = new() { RowDefinitions = new("Auto,*"), Width = ToolWidth };
    private readonly Border _panel;
    private readonly Border _rail;
    private readonly Button _close;
    private readonly ContentControl _content = new();
    private readonly StackPanel _railItems = new() { Spacing = 2 };
    private readonly Dictionary<EditorPanel, Func<Control>> _extraPanels = [];
    private readonly StackPanel _assetActions = new() { Spacing = 6 };
    private readonly Dictionary<EditorPanel, Button> _tabs = [];
    private readonly Dictionary<string, ToggleButton> _marks = [];
    private readonly Dictionary<string, Button> _swatches = [];
    private readonly Dictionary<string, Button> _alignments = [];
    private readonly Dictionary<BlockCommand, Button> _blockStyles = [];
    private readonly StackPanel _insert = new() { Spacing = 12, Margin = new(14, 0, 14, 14) };
    private readonly StackPanel _style = new() { Spacing = 10, Margin = new(14, 0, 14, 16) };
    private readonly StackPanel _insertItems = new() { Spacing = 6 };
    private readonly TextBox _search = new() { Watermark = "搜索块类型", FontSize = 11, MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _insertEmpty = new() { Text = "没有找到这种块类型", FontSize = 12, Foreground = Ui.Muted, IsVisible = false };
    private readonly ScrollViewer _insertScroll;
    private readonly ScrollViewer _styleScroll;
    private readonly Button _clear;
    private readonly Button _removeHighlight;
    private readonly Button _link;
    private readonly Button _indent;
    private readonly Button _outdent;
    private readonly ContentControl _textColors = new() { Margin = new(0, 10, 0, 0) };
    private Target? _colorTarget;
    private bool _queued;
    public EditorPanel? ActivePanel { get; private set; }
    public bool IsOpen => ActivePanel != null;
    public event EventHandler? PanelChanged;

    public EditorSidebar(BlockEditor owner)
    {
        _owner = owner;
        _insertScroll = new() { Content = _insert, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _styleScroll = new() { Content = _style, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(this, "编辑工具侧栏");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        header.Children.Add(_railItems);
        _close = Button(new SidebarGlyph(SidebarSymbol.Close, 13), "收起工具面板 · Esc", "SidebarClose", Close);
        _close.Width = _close.Height = 25;
        _close.Padding = new(0); _close.IsVisible = false; _close.VerticalAlignment = VerticalAlignment.Center;
        _close.Classes.Add("sidebarClose");
        Grid.SetColumn(_close, 1);
        header.Children.Add(_close);
        _panel = new Border
        {
            Child = _content, IsVisible = false, Background = Brushes.Transparent,
            Padding = new(0, 12, 0, 0), ClipToBounds = true
        };
        Grid.SetRow(_panel, 1);
        _layout.Children.Add(_panel);
        var railItems = _railItems;
        Tab(EditorPanel.Insert, SidebarSymbol.Plus, "插入", "Ctrl+Alt+1");
        Tab(EditorPanel.Style, SidebarSymbol.Format, "格式", "Ctrl+Alt+2");
        void Tab(EditorPanel page, SidebarSymbol icon, string label, string shortcut)
        {
            var button = Button(new SidebarGlyph(icon, 18), label + " · " + shortcut, "Sidebar" + page, () => Toggle(page));
            button.Width = 32;
            button.Height = 38;
            button.Padding = new(0);
            button.Classes.Add("sidebarTool");
            _tabs.Add(page, button);
            railItems.Children.Add(button);
        }
        _rail = new Border
        {
            Child = header, Padding = new(3), Background = Ui.Chrome("#FCFDFE"), BorderBrush = Ui.Chrome("#E4E6EA"),
            BorderThickness = new(1), CornerRadius = new(12), VerticalAlignment = VerticalAlignment.Top
        };
        _layout.Children.Add(_rail);
        Content = _layout;

        AutomationProperties.SetName(_search, "搜索插入块类型");
        AutomationProperties.SetAutomationId(_search, "SidebarSearch");
        _insert.Children.Add(Hint("选择要插入的内容"));
        _search.Classes.Add("clean");
        var searchLayout = new Grid { ColumnDefinitions = new("24,*"), Margin = new(9, 0) };
        searchLayout.Children.Add(new SidebarGlyph(SidebarSymbol.Search, 13) { VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_search, 1);
        searchLayout.Children.Add(_search);
        _insert.Children.Add(new Border { Child = searchLayout, Height = 30, Background = Ui.Chrome("#F2F4F6"), CornerRadius = new(7) });
        _insert.Children.Add(_insertItems);
        _insert.Children.Add(_insertEmpty);
        _insert.Children.Add(_assetActions);
        _insert.Children.Add(Hint("输入 / 也能快速插入", new(1, 2, 0, 0)));
        _search.TextChanged += (_, _) => FilterInsert();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !SearchIsComposing() && BlockCommand.Search(_search.Text?.Trim() ?? "").FirstOrDefault() is { } command)
            {
                Insert(command);
                e.Handled = true;
            }
        };
        FilterInsert();

        _style.Children.Add(Section("文本"));
        var textStyles = new UniformGrid { Columns = 3, Rows = 2, Margin = new(-2, 0, -2, 8) };
        var textCommands = new[] { ("heading", 1), ("heading", 2), ("heading", 3), ("paragraph", 1), ("blockquote", 1), ("codeBlock", 1) };
        foreach (var (kind, level) in textCommands)
        {
            var command = BlockCommand.All.Single(command => command.Kind == kind && command.Level == level);
            var button = TextStyleButton(command, () => Convert(command));
            _blockStyles.Add(command, button);
            textStyles.Children.Add(button);
        }
        _style.Children.Add(textStyles);
        _style.Children.Add(Section("格式"));
        var marks = new List<Button>();
        Mark("bold", SidebarSymbol.Bold, "粗体 · Ctrl+B");
        Mark("italic", SidebarSymbol.Italic, "斜体 · Ctrl+I");
        Mark("underline", SidebarSymbol.Underline, "下划线 · Ctrl+U");
        Mark("strike", SidebarSymbol.Strike, "删除线");
        Mark("code", SidebarSymbol.Code, "行内代码");
        void Mark(string type, SidebarSymbol symbol, string label)
        {
            var button = new ToggleButton { Content = new SidebarGlyph(symbol, 17), Padding = new(0) };
            button.Classes.Add("sidebarSegment");
            Identify(button, label, "SidebarFormat_" + type);
            button.Click += (_, _) => Format(new(type));
            _marks.Add(type, button);
            marks.Add(button);
        }
        _style.Children.Add(Segments(marks));
        var lists = new List<Button>();
        foreach (var kind in new[] { "taskList", "toggleBlock", "bulletList", "orderedList" })
        {
            var command = BlockCommand.All.Single(command => command.Kind == kind);
            var button = IconButton(Symbol(command), command.Name, "Style_" + command.Kind + command.Level, () => Convert(command));
            _blockStyles.Add(command, button);
            lists.Add(button);
        }
        _style.Children.Add(Segments(lists));
        var alignments = new List<Button>();
        foreach (var (value, name, symbol) in new[] { ("left", "左对齐", SidebarSymbol.AlignLeft), ("center", "居中", SidebarSymbol.AlignCenter), ("right", "右对齐", SidebarSymbol.AlignRight), ("justify", "两端对齐", SidebarSymbol.AlignJustify) })
        {
            var button = IconButton(symbol, name, "SidebarAlign_" + value, () =>
            {
                if (ActivePanel != EditorPanel.Style || Capture() is not { } target) return;
                target.Session.SetAlignment(target.Start, target.Length, value); _owner.FocusText(); QueueRefresh();
            });
            _alignments.Add(value, button); alignments.Add(button);
        }
        _style.Children.Add(Segments(alignments));
        _outdent = IconButton(SidebarSymbol.Outdent, "减少一级缩进 · Shift+Tab", "SidebarOutdent", () => ChangeIndent(true));
        _indent = IconButton(SidebarSymbol.Indent, "增加一级缩进 · Tab", "SidebarIndent", () => ChangeIndent(false));
        _link = IconButton(SidebarSymbol.Link, "添加或修改链接 · Ctrl+K", "SidebarLink", () =>
        {
            if (Capture() == null) return;
            _owner.FocusText();
            Editor.Formatting.OpenLink();
        });
        _clear = IconButton(SidebarSymbol.Clear, "清除所选文字格式", "SidebarClear", () => Format(null));
        _style.Children.Add(Segments([_outdent, _indent, _link, _clear]));
        _style.Children.Add(_textColors);
        _style.Children.Add(Section("高亮", new(0, 10, 0, 1)));
        var colors = new UniformGrid { Columns = 6, Rows = 1, Margin = new(-2, 1, -2, 0) };
        foreach (var (name, color) in Ui.HighlightColors)
        {
            var swatch = new Button { Width = 30, Height = 30, Padding = new(0), Background = Brush.Parse(color), BorderBrush = Ui.Chrome("#8090A7"), BorderThickness = new(0), CornerRadius = new(15), HorizontalAlignment = HorizontalAlignment.Center };
            swatch.Classes.Add("sidebarSwatch");
            Identify(swatch, name + "高亮", "SidebarHighlight" + name);
            swatch.Click += (_, _) => Format(NoteMark.With("highlight", "color", color), toggle: false);
            _swatches.Add(color, swatch);
            colors.Children.Add(swatch);
        }
        _removeHighlight = Button(new SidebarGlyph(SidebarSymbol.NoColor, 19), "移除所选文字高亮", "SidebarRemoveHighlight", () => Format(new("highlight"), true));
        _removeHighlight.Width = _removeHighlight.Height = 30;
        _removeHighlight.CornerRadius = new(15);
        _removeHighlight.Padding = new(0);
        _removeHighlight.HorizontalAlignment = HorizontalAlignment.Center;
        _removeHighlight.Classes.Add("sidebarSwatch");
        colors.Children.Add(_removeHighlight);
        _style.Children.Add(colors);

        owner.SelectionChanged += (_, _) => QueueRefresh();
        owner.InputClient.PreeditChanged += (_, _) => QueueRefresh();
        owner.PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) QueueRefresh(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (SearchIsComposing()) return;
            if (e.Key == Key.Escape) { Close(); _owner.FocusText(); e.Handled = true; }
        }, RoutingStrategies.Bubble);
        Refresh();
    }

    public void Toggle(EditorPanel page)
    {
        if (ActivePanel == page) Close();
        else Open(page);
    }

    public void Open(EditorPanel page)
    {
        if (page is EditorPanel.Page or EditorPanel.Info && !_extraPanels.ContainsKey(page)) return;
        ActivePanel = page;
        _panel.IsVisible = true;
        UpdatePresentation();
        _content.Content = page == EditorPanel.Insert ? _insertScroll : page == EditorPanel.Style ? _styleScroll : ExtraContent(page);
        Editor.Formatting.Dismiss();
        Refresh();
        PanelChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        ActivePanel = null;
        _panel.IsVisible = false;
        UpdatePresentation();
        foreach (var button in _tabs.Values) button.Classes.Set("active", false);
        PanelChanged?.Invoke(this, EventArgs.Empty);
        _owner.FocusText();
    }

    private void UpdatePresentation()
    {
        _layout.Width = IsOpen ? PanelWidth : ToolWidth;
        _railItems.Orientation = IsOpen ? Orientation.Horizontal : Orientation.Vertical;
        _railItems.Spacing = IsOpen ? 0 : 2;
        _close.IsVisible = IsOpen;
        _rail.Padding = IsOpen ? new(8, 4) : new(3);
        _rail.CornerRadius = IsOpen ? new(0) : new(12);
        _rail.BorderThickness = IsOpen ? new(0, 0, 0, 1) : new(1);
        _rail.Background = IsOpen ? Brushes.Transparent : Ui.Chrome("#FCFDFE");
        foreach (var (kind, button) in _tabs)
        {
            var (symbol, label) = kind switch
            {
                EditorPanel.Insert => (SidebarSymbol.Plus, "插入"), EditorPanel.Style => (SidebarSymbol.Format, "格式"),
                EditorPanel.Page => (SidebarSymbol.Paint, "样式"), _ => (SidebarSymbol.Info, "信息")
            };
            button.Content = IsOpen ? new TextBlock { Text = label, FontSize = 12 } : new SidebarGlyph(symbol, 18);
            button.Width = IsOpen ? 52 : 32;
            button.Height = IsOpen ? 34 : 38;
            button.Classes.Set("sidebarTab", IsOpen);
        }
    }

    public void FocusPanel()
    {
        if (ActivePanel == EditorPanel.Insert) _search.Focus();
        else if (ActivePanel == EditorPanel.Style) (_blockStyles.Values.FirstOrDefault(button => button.IsEnabled) ?? _tabs[EditorPanel.Style]).Focus();
        else _content.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEnabled)?.Focus();
    }

    public void ConfigureExtendedPanels(Func<Control> page, Func<Control> info, Action<bool> attach)
    {
        _extraPanels[EditorPanel.Page] = page; _extraPanels[EditorPanel.Info] = info;
        foreach (var (kind, symbol, label) in new[] { (EditorPanel.Page, SidebarSymbol.Paint, "页面样式"), (EditorPanel.Info, SidebarSymbol.Info, "页面信息") })
        {
            if (_tabs.ContainsKey(kind)) continue;
            var button = Button(new SidebarGlyph(symbol, 18), label, "Sidebar" + kind, () => Toggle(kind));
            button.Width = 32; button.Height = 38; button.Padding = new(0); button.Classes.Add("sidebarTool"); _tabs[kind] = button; _railItems.Children.Add(button);
        }
        UpdatePresentation();
        _assetActions.Children.Clear();
        _assetActions.Children.Add(Section("文件与图片"));
        foreach (var (image, label, symbol) in new[] { (true, "图片", SidebarSymbol.Image), (false, "文件附件", SidebarSymbol.Attachment) })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new SidebarGlyph(symbol, 16)); content.Children.Add(new TextBlock { Text = label });
            var button = Button(content, "插入" + label, image ? "InsertImage" : "InsertAttachment", () => attach(image));
            button.Classes.Add("sidebarInsert"); button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Height = 34; _assetActions.Children.Add(button);
        }
    }

    private ScrollViewer ExtraContent(EditorPanel panel) => new() { Content = _extraPanels[panel](), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public void RefreshExtendedPanel(bool includePage = false)
    {
        if (ActivePanel is { } panel && (panel == EditorPanel.Info || includePage && panel == EditorPanel.Page)) _content.Content = ExtraContent(panel);
    }

    private bool SearchIsComposing() => _search.IsKeyboardFocusWithin && _search.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    private static TextBlock Hint(string text, Thickness? margin = null) => new() { Text = text, FontSize = 10, Foreground = Ui.Chrome("#A0A4AC"), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new(0) };
    private static TextBlock Section(string text, Thickness? margin = null) => new() { Text = text, FontSize = 11, Foreground = Ui.Chrome("#91959D"), Margin = margin ?? new(0) };
    private static void Identify(Control control, string label, string id)
    {
        AutomationProperties.SetName(control, label);
        AutomationProperties.SetAutomationId(control, id);
        ToolTip.SetTip(control, label);
    }
    private static Button Button(object content, string label, string id, Action action)
    {
        var button = new Button { Content = content, FontSize = 11, Padding = new(8, 6), MinHeight = 0 };
        button.Classes.Add("quiet");
        button.Classes.Add("sidebarButton");
        Identify(button, label, id);
        button.Click += (_, _) => action();
        return button;
    }
    private static Button BlockButton(BlockCommand command, Action action)
    {
        var row = new Grid { ColumnDefinitions = new("26,*,18") };
        row.Children.Add(new SidebarGlyph(Symbol(command), 16) { VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = command.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        var add = new SidebarGlyph(SidebarSymbol.Plus, 13) { VerticalAlignment = VerticalAlignment.Center };
        add.Classes.Add("insertAffordance");
        Grid.SetColumn(add, 2);
        row.Children.Add(add);
        var button = Button(row, command.Name, "Insert_" + command.Kind + command.Level, action);
        button.Height = 34;
        button.Padding = new(11, 0, 9, 0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Classes.Add("sidebarInsert");
        return button;
    }
    private static Button TextStyleButton(BlockCommand command, Action action)
    {
        var label = command.Kind switch { "heading" => command.Level switch { 1 => "标题", 2 => "副标题", _ => "小标题" }, "blockquote" => "引用", "codeBlock" => "代码", _ => "正文" };
        var button = Button(new TextBlock { Text = label, FontSize = command.Kind == "heading" ? 13 : 12, FontWeight = command.Kind == "heading" ? FontWeight.SemiBold : FontWeight.Normal }, command.Name, "Style_" + command.Kind + command.Level, action);
        button.Height = 39;
        button.Margin = new(2);
        button.Padding = new(0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.Classes.Add("sidebarTextStyle");
        return button;
    }
    private static SidebarSymbol Symbol(BlockCommand command) => command.Kind switch
    {
        "heading" => command.Level switch { 1 => SidebarSymbol.Heading1, 2 => SidebarSymbol.Heading2, _ => SidebarSymbol.Heading3 },
        "toggleBlock" => SidebarSymbol.Toggle, "bulletList" => SidebarSymbol.BulletList, "orderedList" => SidebarSymbol.OrderedList,
        "taskList" => SidebarSymbol.Task, "blockquote" => SidebarSymbol.Quote, "codeBlock" => SidebarSymbol.Code,
        "horizontalRule" => SidebarSymbol.Divider, "table" => SidebarSymbol.Table, "columnList" => SidebarSymbol.Columns, _ => SidebarSymbol.Text
    };
    private static Button IconButton(SidebarSymbol symbol, string label, string id, Action action)
    {
        var button = Button(new SidebarGlyph(symbol, 17), label, id, action);
        button.Padding = new(0);
        button.Classes.Add("sidebarSegment");
        return button;
    }
    private static Border Segments(IEnumerable<Button> controls)
    {
        var buttons = controls.ToArray();
        var grid = new Grid { ColumnDefinitions = new(string.Join(',', buttons.Select(_ => "*"))) };
        for (var index = 0; index < buttons.Length; index++)
        {
            var button = buttons[index];
            button.Height = 30;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            Grid.SetColumn(button, index);
            grid.Children.Add(button);
            if (index == 0) continue;
            var separator = new Border { Width = 1, Background = Ui.Chrome("#E9EBEF"), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
            Grid.SetColumn(separator, index);
            grid.Children.Add(separator);
        }
        return new Border { Child = grid, Height = 32, Background = Ui.Chrome("#F5F6F8"), BorderBrush = Ui.Chrome("#E9EBEF"), BorderThickness = new(1), CornerRadius = new(8), ClipToBounds = true };
    }

    private Target? Capture()
    {
        var editor = Editor;
        if (!_owner.IsEnabled || editor.InputClient.IsComposing || !editor.Session.IsScopeAttached || editor.Surface.Document.TextLength != editor.Session.Projection.Text.Length) return null;
        return new(editor.Session, editor.Session.Revision, editor.Surface.SelectionStart, editor.Surface.SelectionLength, editor.Surface.CaretOffset, editor.Session.Selection);
    }
    private void FilterInsert()
    {
        var commands = BlockCommand.Search(_search.Text?.Trim() ?? "");
        _insertItems.Children.Clear();
        foreach (var command in commands)
        {
            var button = BlockButton(command, () => { if (command.Kind != "table") Insert(command); });
            if (command.Kind == "table") button.Click += (_, _) => OpenTablePicker(button);
            _insertItems.Children.Add(button);
        }
        _insertEmpty.IsVisible = commands.Length == 0;
    }
    private void Insert(BlockCommand command)
    {
        if (ActivePanel != EditorPanel.Insert || Capture() is not { } target) return;
        target.Session.BreakTypingGroup();
        target.Session.InsertBlock(target.Caret, command.Kind, command.Level);
        _owner.FocusText();
        _owner.Surface.ScrollTo(_owner.Surface.TextArea.Caret.Line, _owner.Surface.TextArea.Caret.Column);
        QueueRefresh();
    }
    private void OpenTablePicker(Button anchor)
    {
        if (ActivePanel != EditorPanel.Insert || Capture() is not { } target) return;
        var flyout = new Flyout();
        flyout.Content = new TableSizePicker((rows, columns) =>
        {
            if (ActivePanel != EditorPanel.Insert || Capture() != target) { flyout.Hide(); return; }
            target.Session.InsertTable(target.Caret, rows, columns); flyout.Hide(); _owner.FocusText(); QueueRefresh();
        });
        flyout.ShowAt(anchor);
    }
    private void Convert(BlockCommand command)
    {
        if (ActivePanel != EditorPanel.Style || Capture() is not { } target || target.Session.Projection.At(target.Caret).IsAtomic) return;
        target.Session.BreakTypingGroup();
        target.Session.ConvertBlock(target.Caret, command.Kind, command.Level);
        _owner.FocusText();
        QueueRefresh();
    }
    private void Format(NoteMark? mark, bool remove = false, bool toggle = true)
    {
        if (ActivePanel != EditorPanel.Style || Capture() is not { } target || !new SelectionFormats(target.Session.Projection, target.Start, target.Length).CanFormat) return;
        target.Session.Selection = target.Selection;
        target.Session.Format(target.Start, target.Length, mark, remove, toggle);
        _owner.FocusText();
        QueueRefresh();
    }
    private void ChangeIndent(bool outdent)
    {
        if (ActivePanel != EditorPanel.Style || Capture() is not { } target) return;
        var row = target.Session.Projection.At(target.Caret);
        if (outdent) target.Session.Outdent(row.Block.Id); else target.Session.Indent(row.Block.Id);
        _owner.FocusText();
        QueueRefresh();
    }
    private void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }
    private void Refresh()
    {
        foreach (var (page, button) in _tabs) button.Classes.Set("active", page == ActivePanel);
        if (!IsOpen) return;
        var target = Capture();
        if (ActivePanel == EditorPanel.Insert)
        {
            _insertItems.IsEnabled = target != null;
            return;
        }
        if (ActivePanel != EditorPanel.Style) return;
        var formats = target == null ? null : new SelectionFormats(target.Session.Projection, target.Start, target.Length);
        var canFormat = formats?.CanFormat == true;
        if (_colorTarget != target || _textColors.Content == null)
        {
            _colorTarget = target;
            _textColors.Content = new TextColorPicker(formats, color =>
            {
                if (target == null || ActivePanel != EditorPanel.Style || Capture() != target) return;
                target.Session.Selection = target.Selection;
                target.Session.SetTextColor(target.Start, target.Length, color);
                _owner.FocusText(); QueueRefresh();
            }, () => _owner.FocusText(), "Sidebar");
        }
        _textColors.IsEnabled = canFormat;
        foreach (var (type, button) in _marks)
        {
            var coverage = formats?.Coverage(type) ?? MarkCoverage.None;
            button.IsEnabled = canFormat;
            button.IsChecked = coverage == MarkCoverage.Mixed ? null : coverage == MarkCoverage.All;
            AutomationProperties.SetHelpText(button, coverage switch { MarkCoverage.Mixed => "所选文字部分已设置", MarkCoverage.All => "所选文字已全部设置", _ => "所选文字未设置" });
        }
        foreach (var (color, button) in _swatches)
        {
            var selected = formats?.UniformMark("highlight")?.String("color") == color;
            button.IsEnabled = canFormat;
            button.Content = selected ? new SidebarGlyph(SidebarSymbol.Check, 15) : null;
            button.BorderThickness = new(selected ? 1.5 : 0);
        }
        _clear.IsEnabled = canFormat && formats!.HasFormatting;
        _removeHighlight.IsEnabled = canFormat && formats!.Coverage("highlight") != MarkCoverage.None;
        _link.IsEnabled = canFormat || target != null && SelectionFormats.LinkAt(target.Session.Projection, target.Caret) != null;
        var row = target?.Session.Projection.At(target.Caret);
        foreach (var (value, button) in _alignments)
        {
            button.IsEnabled = row is { IsAtomic: false };
            button.Classes.Set("active", row != null && (row.Node.String("textAlign") ?? "left") == value);
        }
        var parent = row == null ? null : NoteTree.Parent(target!.Session.Root, row.Block.Id);
        var index = row == null || parent == null ? -1 : parent.Content.IndexOf(row.Block);
        _outdent.IsEnabled = row is { IsAtomic: false } && parent?.Type == "toggleBlock" && index > 0;
        _indent.IsEnabled = row is { IsAtomic: false } && index > 0 && parent!.Content[index - 1].Type == "toggleBlock";
        foreach (var (command, button) in _blockStyles)
        {
            button.IsEnabled = row is { IsAtomic: false };
            button.Classes.Set("active", row != null && command.Name == CurrentBlock(row));
        }
    }
    private static string CurrentBlock(BlockRow row) => row.IsToggle ? "折叠块" : row.HeadingLevel switch
    {
        1 => "一级标题", 2 => "二级标题", 3 => "三级标题",
        _ => row.Node.Type == "codeBlock" ? "代码块" : row.IsTask ? "待办事项" : row.Marker == "•" ? "无序列表" : row.Marker.Length > 0 ? "有序列表" : row.Quote ? "引用" : "正文"
    };
}
