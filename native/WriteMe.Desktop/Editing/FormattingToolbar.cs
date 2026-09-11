using System.Text.Json;
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
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 原生选区浮层、格式状态与草稿焦点 — 见 .agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md
public sealed class FormattingToolbar : Border
{
    private sealed record Target(DocumentSession Session, long Revision, int Start, int Length, EditorSelection Selection);
    private readonly BlockEditor _owner;
    private readonly WrapPanel _bar = new() { Orientation = Orientation.Horizontal };
    private readonly ContentControl _panel = new() { IsVisible = false, Margin = new(5, 7, 5, 4) };
    private readonly Dictionary<string, ToggleButton> _marks = [];
    private readonly List<Button> _actions = [];
    private readonly Button _highlight;
    private readonly Border _colorIcon;
    private readonly Button _link;
    private readonly Button _clear;
    private readonly Button _block;
    private Target? _target;
    private Target? _dismissed;
    private SelectionFormats? _formats;
    private TextBox? _linkInput;
    private Window? _window;
    private bool _inactive;
    private bool _queued;
    private bool _keyboard;

    public FormattingToolbar(BlockEditor owner)
    {
        _owner = owner;
        IsVisible = false;
        Padding = new(5);
        Background = Ui.Chrome("#FAFAFA");
        BorderBrush = Ui.Chrome("#EDEEEF");
        BorderThickness = new(1);
        CornerRadius = new(14);
        BoxShadow = Ui.FloatingShadow;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(this, "文字格式工具栏");
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);

        _block = ActionButton("正文 ▾", "转换当前块", "FormatBlock", OpenBlocks);
        _block.MinWidth = 70;
        _block.FontSize = 11;
        _bar.Children.Add(_block);
        Divider();
        Mark("bold", new TextBlock { Text = "B", FontWeight = FontWeight.Bold }, "粗体 · Ctrl+B");
        Mark("italic", new TextBlock { Text = "I", FontStyle = FontStyle.Italic }, "斜体 · Ctrl+I");
        Mark("underline", new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, "下划线 · Ctrl+U");
        Mark("strike", new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "删除线");
        Mark("code", "‹›", "行内代码");
        Divider();
        _colorIcon = new Border { Child = new TextBlock { Text = "A", FontSize = 14 }, BorderBrush = Ui.Chrome("#E5C86C"), BorderThickness = new(0, 0, 0, 3) };
        _highlight = ActionButton(_colorIcon, "文字颜色与高亮", "FormatHighlight", OpenHighlight);
        _link = ActionButton("↗", "编辑链接 · Ctrl+K", "FormatLink", OpenLink);
        _clear = ActionButton("Tₓ", "清除文字格式", "FormatClear", () => Apply(null));
        _bar.Children.Add(_highlight);
        _bar.Children.Add(_link);
        _bar.Children.Add(ActionButton(new SidebarGlyph(SidebarSymbol.Comment, 17), "添加批注 · Ctrl+Alt+M", "FormatComment", () =>
        {
            if (ValidTarget() && _owner.IsEffectivelyEnabled && !_owner.InputClient.IsComposing && !PanelIsComposing()) _owner.RequestComment();
        }));
        _bar.Children.Add(_clear);
        Child = new StackPanel { Children = { _bar, _panel } };

        owner.SelectionChanged += (_, _) => QueueRefresh();
        owner.InputClient.PreeditChanged += (_, _) => QueueRefresh();
        var view = owner.Surface.TextArea.TextView;
        view.VisualLinesChanged += (_, _) => QueueRefresh();
        view.ScrollOffsetChanged += (_, _) => QueueRefresh();
        owner.AddHandler(InputElement.GotFocusEvent, (_, _) => QueueRefresh(), RoutingStrategies.Bubble, handledEventsToo: true);
        owner.AddHandler(InputElement.LostFocusEvent, (_, _) => QueueRefresh(), RoutingStrategies.Bubble, handledEventsToo: true);
        owner.SizeChanged += (_, _) => QueueRefresh();
        SizeChanged += (_, _) => QueueRefresh();
        AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => _keyboard = false, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window == null) return;
            _window.Activated += Activated;
            _window.Deactivated += Deactivated;
            QueueRefresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window != null) { _window.Activated -= Activated; _window.Deactivated -= Deactivated; }
            _window = null;
            Reset();
        };
    }

    private Button ActionButton(object content, string label, string id, Action action)
    {
        var button = new Button { Content = content, MinWidth = 30, Height = 30, Padding = new(7, 3), FontSize = 14 };
        button.Classes.Add("formatAction");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, id);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => action();
        _actions.Add(button);
        return button;
    }

    private void Mark(string type, object content, string label)
    {
        var button = new ToggleButton { Content = content, MinWidth = 30, Height = 30, Padding = new(7, 3), FontSize = 14 };
        button.Classes.Add("formatAction");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, "Format_" + type);
        ToolTip.SetTip(button, label);
        button.Click += (_, _) => Apply(new(type));
        _marks.Add(type, button);
        _actions.Add(button);
        _bar.Children.Add(button);
    }

    private void Divider() => _bar.Children.Add(new Border { Width = 1, Height = 17, Background = Ui.Line, Margin = new(4, 0), VerticalAlignment = VerticalAlignment.Center });
    private void Activated(object? sender, EventArgs args) { _inactive = false; QueueRefresh(); }
    private void Deactivated(object? sender, EventArgs args) { _inactive = true; Reset(); }

    internal void QueueRefresh()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }

    private Target? Capture()
    {
        var surface = _owner.Surface;
        var session = _owner.Session;
        if (surface.Document.TextLength != session.Projection.Text.Length || surface.SelectionLength == 0) return null;
        return new(session, session.Revision, surface.SelectionStart, surface.SelectionLength, session.Selection);
    }

    private bool ValidTarget() => _target != null && _target == Capture();

    private void Refresh()
    {
        if (_owner.GetVisualRoot() == null || _inactive || _owner.IsInteracting || _owner.InputClient.IsComposing
            || !(_owner.Surface.IsKeyboardFocusWithin || IsKeyboardFocusWithin)) { Hide(); return; }
        var current = Capture();
        if (current == null || current == _dismissed) { Hide(); return; }
        if (_target != current)
        {
            ClearPanel();
            _target = current;
            _formats = new(current.Session.Projection, current.Start, current.Length);
        }
        if (_formats?.CanFormat != true) { Hide(); return; }
        UpdateStates();
        Reposition();
    }

    private void UpdateStates()
    {
        foreach (var (type, button) in _marks)
        {
            var coverage = _formats!.Coverage(type);
            button.IsChecked = coverage == MarkCoverage.Mixed ? null : coverage == MarkCoverage.All;
            AutomationProperties.SetHelpText(button, coverage switch { MarkCoverage.All => "所选文字已全部设置", MarkCoverage.Mixed => "所选文字部分已设置", _ => "所选文字未设置" });
        }
        _highlight.Classes.Set("active", _formats!.Coverage("highlight") != MarkCoverage.None || _formats.TextColorCoverage != MarkCoverage.None);
        ((TextBlock)_colorIcon.Child!).Foreground = _formats.UniformTextColor is { } currentColor ? Brush.Parse(currentColor) : Ui.Ink;
        _colorIcon.BorderBrush = Color.TryParse(_formats.UniformMark("highlight")?.String("color"), out var highlightColor)
            ? new SolidColorBrush(highlightColor) : Ui.Chrome("#E5C86C");
        _link.Classes.Set("active", _formats.Coverage("link") != MarkCoverage.None);
        _clear.IsEnabled = _formats.HasFormatting;
        var row = _owner.Session.Projection.At(_owner.Surface.CaretOffset);
        _block.Content = (row.IsToggle ? "折叠块" : row.HeadingLevel > 0 ? $"标题 {row.HeadingLevel}" : row.IsTask ? "待办" : row.Marker.Length > 0 ? "列表" : row.Quote ? "引用" : "正文") + " ▾";
    }

    private void Reposition()
    {
        var view = _owner.Surface.TextArea.TextView;
        // A format transaction invalidates text layout. Keep keyboard focus in the existing
        // toolbar until VisualLinesChanged supplies the replacement geometry.
        if (!view.VisualLinesValid) return;
        if (_target == null || _owner.Bounds.Width < 80 || _owner.Bounds.Height < 60) { IsVisible = false; return; }
        var origin = view.TranslatePoint(default, _owner) ?? default;
        var viewport = new Rect(0, 0, view.Bounds.Width, view.Bounds.Height);
        var rects = BackgroundGeometryBuilder.GetRectsForSegment(view, new SimpleSegment(_target.Start, _target.Length))
            .Where(rect => rect.Width > 0 && rect.Height > 0 && rect.Intersects(viewport)).Select(rect => rect.Intersect(viewport)).ToArray();
        if (rects.Length == 0) { Hide(); return; }
        var first = rects[0];
        var last = rects[^1];
        Width = Math.Min(398, _owner.Bounds.Width - 16);
        MaxHeight = _owner.Bounds.Height - 12;
        IsVisible = true;
        // Measure content independently of the old placement, without repeatedly invalidating
        // the parent text viewport by clearing and restoring this margin.
        Child!.Measure(new(Width - Padding.Left - Padding.Right - BorderThickness.Left - BorderThickness.Right, MaxHeight));
        var height = Math.Min(MaxHeight, Child.DesiredSize.Height + Padding.Top + Padding.Bottom + BorderThickness.Top + BorderThickness.Bottom);
        var above = origin.Y + first.Top - height - 8;
        var below = origin.Y + last.Bottom + 8;
        var y = above >= 6 ? above : below + height <= _owner.Bounds.Height - 6 ? below : 6;
        var x = Math.Clamp(origin.X + first.Left, 8, Math.Max(8, _owner.Bounds.Width - Width - 8));
        Margin = new(x, Math.Clamp(y, 6, Math.Max(6, _owner.Bounds.Height - height - 6)), 0, 0);
    }

    private void ClearPanel()
    {
        _panel.IsVisible = false;
        _panel.Content = null;
        _linkInput = null;
    }

    private void Hide() { IsVisible = false; ClearPanel(); _target = null; _formats = null; }
    internal void Reset() { Hide(); _dismissed = null; _keyboard = false; }
    internal void Dismiss(bool focusText = false)
    {
        _dismissed = Capture();
        Hide();
        if (focusText) _owner.FocusText();
    }

    public void FocusToolbar()
    {
        _dismissed = null;
        _owner.Surface.TextArea.TextView.EnsureVisualLines();
        Refresh();
        if (!IsVisible) return;
        _keyboard = true;
        _actions.First(button => button.IsEnabled).Focus(NavigationMethod.Tab);
    }

    private void Apply(NoteMark? mark, bool remove = false, bool toggle = true, Target? expected = null)
    {
        if (expected != null && expected != _target) return;
        if (!ValidTarget() || _owner.InputClient.IsComposing) { Reset(); return; }
        var target = _target!;
        var focusText = !_keyboard || _panel.IsVisible;
        ClearPanel();
        target.Session.Selection = target.Selection;
        target.Session.Format(target.Start, target.Length, mark, remove, toggle);
        _target = null;
        if (focusText) _owner.FocusText();
        QueueRefresh();
    }

    private void ShowPanel(Control content)
    {
        _panel.Content = content;
        _panel.IsVisible = true;
        Reposition();
    }

    private void OpenHighlight()
    {
        if (!ValidTarget()) return;
        var target = _target!;
        ClearPanel();
        var layout = new StackPanel { Spacing = 9 };
        var textColors = new TextColorPicker(_formats, color => ApplyTextColor(color, target), () => Dismiss(true), "Format");
        layout.Children.Add(textColors);
        layout.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(0, 2) });
        layout.Children.Add(new TextBlock { Text = "文字高亮", FontSize = 11, Foreground = Ui.Muted });
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        var current = _formats?.UniformMark("highlight")?.String("color");
        foreach (var (name, color) in Ui.HighlightColors)
        {
            var swatch = new Button { Content = current == color ? "✓" : "", Background = Brush.Parse(color), Width = 35, Height = 31, Padding = new(0), CornerRadius = new(5), BorderThickness = new(current == color ? 1.5 : 0), BorderBrush = Ui.Ink };
            AutomationProperties.SetName(swatch, name + "高亮");
            ToolTip.SetTip(swatch, name);
            swatch.Click += (_, _) => Apply(NoteMark.With("highlight", "color", color), toggle: false, expected: target);
            swatches.Children.Add(swatch);
        }
        layout.Children.Add(swatches);
        var remove = new Button { Content = "移除高亮", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 11, IsEnabled = _formats?.Coverage("highlight") != MarkCoverage.None };
        remove.Classes.Add("quiet");
        remove.Click += (_, _) => Apply(new("highlight"), true, expected: target);
        layout.Children.Add(remove);
        ShowPanel(new ScrollViewer { Content = layout, MaxHeight = Math.Max(80, _owner.Bounds.Height - 86), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (_keyboard) textColors.FocusPalette();
    }

    private void ApplyTextColor(string? color, Target expected)
    {
        if (_target != expected || !ValidTarget() || _owner.InputClient.IsComposing) return;
        expected.Session.Selection = expected.Selection;
        ClearPanel();
        expected.Session.SetTextColor(expected.Start, expected.Length, color);
        _target = null;
        _owner.FocusText();
        QueueRefresh();
    }

    public void OpenLink()
    {
        if (_owner.InputClient.IsComposing) return;
        var row = _owner.Session.Projection.At(_owner.Surface.CaretOffset);
        if (NoteReferences.At(row.Node, _owner.Surface.CaretOffset - row.Start, true) is { Kind: ReferenceKind.Note })
        { _owner.References.Open(); return; }
        if (_owner.Surface.SelectionLength == 0 && SelectionFormats.LinkAt(_owner.Session.Projection, _owner.Surface.CaretOffset) is { } link)
            _owner.Surface.Select(link.Start, link.Length);
        var current = Capture();
        if (current == null || !new SelectionFormats(current.Session.Projection, current.Start, current.Length).CanFormat)
        {
            _owner.ShowNotice("先选择要添加链接的文字");
            return;
        }
        _dismissed = null;
        _owner.Surface.TextArea.TextView.EnsureVisualLines();
        Refresh();
        if (!IsVisible) return;
        var target = _target!;
        ClearPanel();
        var existing = _formats?.UniformMark("link");
        var layout = new StackPanel { Spacing = 9 };
        layout.Children.Add(new TextBlock { Text = "链接地址", FontSize = 11, Foreground = Ui.Muted });
        var input = new TextBox { Text = existing?.String("href") ?? "", Watermark = "输入网址，例如 example.com", FontSize = 12, MinHeight = 32 };
        _linkInput = input;
        AutomationProperties.SetName(input, "链接地址");
        AutomationProperties.SetAutomationId(input, "LinkAddress");
        var error = new TextBlock { Foreground = Ui.Chrome("#B75F4A"), FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(error, "LinkError");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 7 };
        var remove = new Button { Content = "移除链接", FontSize = 11, IsEnabled = _formats?.Coverage("link") != MarkCoverage.None };
        remove.Click += (_, _) => Apply(new("link"), true, expected: target);
        var cancel = new Button { Content = "取消", FontSize = 11 };
        cancel.Click += (_, _) => { if (_target == target) Dismiss(true); };
        var apply = new Button { Content = "应用", FontSize = 11 };
        apply.Classes.Add("primary");
        void Commit()
        {
            if (_target != target || !ValidTarget() || PanelIsComposing()) return;
            if (LinkAddress.Normalize(input.Text ?? "") is not { } address)
            {
                error.Text = "请输入有效的网址、邮箱链接或电话号码链接";
                error.IsVisible = true;
                Reposition();
                input.Focus();
                return;
            }
            var mark = existing ?? new NoteMark("link");
            Apply(mark with { Attrs = mark.Attrs.SetItem("href", JsonSerializer.SerializeToElement(address)) }, toggle: false, expected: target);
        }
        apply.Click += (_, _) => Commit();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter && !PanelIsComposing()) { Commit(); e.Handled = true; } };
        actions.Children.Add(remove);
        actions.Children.Add(cancel);
        actions.Children.Add(apply);
        layout.Children.Add(input);
        layout.Children.Add(error);
        layout.Children.Add(actions);
        ShowPanel(layout);
        input.Focus();
        input.SelectAll();
    }

    private bool PanelIsComposing() => _panel.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));

    private void OpenBlocks()
    {
        if (!ValidTarget()) return;
        var target = _target!;
        ClearPanel();
        var list = new StackPanel { Spacing = 1 };
        foreach (var command in BlockCommand.All)
        {
            var item = new Button { Content = command.Name, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, FontSize = 12 };
            item.Classes.Add("quiet");
            item.Click += (_, _) =>
            {
                if (_target != target || !ValidTarget()) return;
                target.Session.Selection = target.Selection;
                target.Session.ConvertBlock(target.Session.Projection.Offset(target.Selection.Caret), command.Kind, command.Level);
                Dismiss(true);
            };
            list.Children.Add(item);
        }
        ShowPanel(new ScrollViewer { Content = list, MaxHeight = Math.Max(80, Math.Min(300, _owner.Bounds.Height - 90)) });
        if (_keyboard) list.Children.OfType<Button>().First().Focus();
    }

    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (PanelIsComposing())
        {
            if (e.Key is Key.Enter or Key.Escape) e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape) { Dismiss(true); e.Handled = true; return; }
        if (_linkInput?.IsKeyboardFocusWithin == true || _panel.IsKeyboardFocusWithin) return;
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            var buttons = _actions.Where(button => button.IsEnabled).ToArray();
            var index = Array.FindIndex(buttons, button => button.IsKeyboardFocusWithin);
            index = e.Key switch { Key.Home => 0, Key.End => buttons.Length - 1, Key.Right => (index + 1) % buttons.Length, _ => (index + buttons.Length - 1) % buttons.Length };
            _keyboard = true;
            buttons[index].Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }
}
