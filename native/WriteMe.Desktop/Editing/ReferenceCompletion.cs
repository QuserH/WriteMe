using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed record ReferenceChoice(string Id, string Label, string Detail, bool Create = false);

// Note: [[ 与 # 补全使用会话/修订/选区快照，输入法确认不提交 — 见 .agents/notes/implemented/feature/2026-09-09-m3-note-connections.md
public sealed class ReferenceCompletion : Border
{
    private sealed record Target(DocumentSession Session, long Revision, int Start, int Length, int SelectionStart, int SelectionLength, int Caret, EditorSelection Selection, bool KeepText);
    private readonly BlockEditor _owner;
    private readonly TextBlock _heading = new() { FontSize = 11, Foreground = Ui.Muted, Margin = new(13, 11, 13, 7) };
    private readonly TextBox _search = new() { Watermark = "搜索笔记，或输入新标题", FontSize = 12, Margin = new(10, 0, 10, 6), MinHeight = 30 };
    private readonly ListBox _list = new() { Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(5, 0), MaxHeight = 256 };
    private readonly TextBlock _empty = new() { Text = "没有匹配的笔记", FontSize = 12, Foreground = Ui.Muted, Margin = new(13, 10) };
    private readonly Button _remove = new() { Content = "移除笔记链接", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(7, 0) };
    private ReferenceChoice[] _choices = [];
    private Target? _target;
    private ReferenceKind _kind;
    private bool _manual;
    private bool _queued;
    private bool _allowAutoComplete;
    private bool _applying;
    private string? _dismissed;
    private string _query = "";

    public ReferenceCompletion(BlockEditor owner)
    {
        _owner = owner;
        Background = Ui.Surface; BorderBrush = Ui.Line; BorderThickness = new(1); CornerRadius = new(11);
        BoxShadow = new(new BoxShadow { Blur = 22, OffsetY = 5, Color = Color.Parse("#19000000") });
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top; IsVisible = false; ZIndex = 50;
        AutomationProperties.SetAutomationId(this, "ReferenceCompletion");
        AutomationProperties.SetName(_search, "搜索要链接的笔记");
        AutomationProperties.SetAutomationId(_search, "ReferenceSearch");
        AutomationProperties.SetAutomationId(_list, "ReferenceChoices");
        AutomationProperties.SetName(_list, "笔记与标签补全");
        AutomationProperties.SetAutomationId(_remove, "RemoveNoteLink");
        _remove.Classes.Add("quiet");
        var layout = new StackPanel();
        layout.Children.Add(_heading); layout.Children.Add(_search); layout.Children.Add(_list); layout.Children.Add(_empty); layout.Children.Add(_remove);
        layout.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(10, 5) });
        layout.Children.Add(new TextBlock { Text = "↑↓ 选择   Enter 确认   Esc 关闭", FontSize = 10, Foreground = Ui.Muted, Margin = new(13, 0, 13, 10) });
        Child = layout;
        _list.ItemTemplate = new FuncDataTemplate<ReferenceChoice>((choice, _) =>
        {
            if (choice == null) return new TextBlock();
            var grid = new Grid { ColumnDefinitions = new("27,*"), Margin = new(0, 1) };
            grid.Children.Add(_kind == ReferenceKind.Tag ? new TextBlock { Text = "#", FontSize = 18, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }
                : new SidebarGlyph(choice.Create ? SidebarSymbol.Plus : SidebarSymbol.Link, 17) { VerticalAlignment = VerticalAlignment.Center });
            var label = new StackPanel { Spacing = 3 };
            label.Children.Add(new TextBlock { Text = choice.Create && _kind == ReferenceKind.Note ? "新建「" + choice.Label + "」并链接" : choice.Label,
                FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = choice.Detail, FontSize = 10, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(label, 1); grid.Children.Add(label);
            return grid;
        });
        _list.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is Control control
                && (control is ListBoxItem || control.GetVisualAncestors().OfType<ListBoxItem>().Any())) Accept();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        _remove.Click += (_, _) =>
        {
            if (!Valid() || _target is not { } target) return;
            Dismiss(); target.Session.Selection = target.Selection;
            target.Session.Format(target.Start, target.Length, new("noteLink"), true);
            _owner.FocusText();
        };
        _search.TextChanged += (_, _) => { if (_manual && !_applying) { _query = _search.Text?.Trim() ?? ""; Fill(); } };
        AddHandler(KeyDownEvent, (_, e) => HandleKey(e), RoutingStrategies.Tunnel);
        _owner.Surface.TextArea.TextEntered += (_, _) => Queue(true);
        _owner.SelectionChanged += (_, _) => Queue();
        _owner.InputClient.PreeditChanged += (_, _) => { if (_owner.InputClient.IsComposing) IsVisible = false; else Queue(); };
        _owner.Surface.TextArea.TextView.ScrollOffsetChanged += (_, _) => Position();
        _owner.SizeChanged += (_, _) => Position();
        _owner.Surface.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!IsKeyboardFocusWithin && !IsPointerOver && !_owner.Surface.IsKeyboardFocusWithin) Dismiss();
        });
    }

    private bool IsComposing() => _owner.InputClient.IsComposing || _search.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
    private Target Capture(int start, int length, bool keepText = false) => new(_owner.Session, _owner.Session.Revision, start, length,
        _owner.Surface.SelectionStart, _owner.Surface.SelectionLength, _owner.Surface.CaretOffset, _owner.Session.Selection, keepText);
    private bool Valid() => _target is { } target && _owner.IsEnabled && !IsComposing()
        && ReferenceEquals(target.Session, _owner.Session) && target.Revision == _owner.Session.Revision
        && target.SelectionStart == _owner.Surface.SelectionStart && target.SelectionLength == _owner.Surface.SelectionLength
        && target.Caret == _owner.Surface.CaretOffset && _owner.Surface.Document.TextLength == _owner.Session.Projection.Text.Length;

    private void Queue(bool allowAutoComplete = false)
    {
        _allowAutoComplete |= allowAutoComplete;
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; var complete = _allowAutoComplete; _allowAutoComplete = false; Update(complete); }, DispatcherPriority.Background);
    }

    private string QueryKey()
    {
        var row = _owner.Session.Projection.At(_owner.Surface.CaretOffset);
        return row.Node.Id + ":" + row.Text[..Math.Clamp(_owner.Surface.CaretOffset - row.Start, 0, row.Text.Length)];
    }
    public void Dismiss()
    {
        _dismissed = QueryKey(); IsVisible = false; _manual = false; _target = null;
    }
    internal void Reset() { IsVisible = false; _manual = false; _target = null; _dismissed = null; _allowAutoComplete = false; }

    public void Open()
    {
        if (!_owner.IsEnabled || IsComposing()) return;
        var surface = _owner.Surface;
        var row = _owner.Session.Projection.At(surface.CaretOffset);
        if (row.IsAtomic || row.Node.Type == "codeBlock" || surface.SelectionStart < row.Start || surface.SelectionStart + surface.SelectionLength > row.End) return;
        var start = surface.SelectionStart;
        var length = surface.SelectionLength;
        if (length == 0 && NoteReferences.At(row.Node, surface.CaretOffset - row.Start, true) is { Kind: ReferenceKind.Note } existing)
        {
            start = row.Start + existing.Start; length = existing.Length;
            surface.Select(start, length);
        }
        _manual = true; _kind = ReferenceKind.Note; _dismissed = null;
        _target = Capture(start, length, length > 0);
        _query = length > 0 ? surface.SelectedText : "";
        _search.IsVisible = true; _search.Text = _query;
        _remove.IsVisible = length > 0 && NoteReferences.InBlock(row.Node).Any(span => span.Kind == ReferenceKind.Note && row.Start + span.Start < start + length && row.Start + span.Start + span.Length > start);
        _owner.Formatting.Dismiss();
        Fill(); _search.Focus(); _search.SelectAll();
    }

    private void Update(bool allowAutoComplete)
    {
        if (_applying) return;
        if (_manual) { if (!Valid() && !IsComposing()) Dismiss(); return; }
        var surface = _owner.Surface;
        if (!_owner.IsEnabled || _owner.InputClient.IsComposing || !surface.IsKeyboardFocusWithin || surface.SelectionLength != 0 || surface.Document.TextLength != _owner.Session.Projection.Text.Length)
        { IsVisible = false; return; }
        var row = _owner.Session.Projection.At(surface.CaretOffset);
        var prefix = row.Text[..Math.Clamp(surface.CaretOffset - row.Start, 0, row.Text.Length)];
        if (QueryKey() == _dismissed) return;
        var start = prefix.LastIndexOf("[[", StringComparison.Ordinal);
        var closed = false;
        if (start >= 0 && (start == 0 || prefix[start - 1] != '['))
        {
            _kind = ReferenceKind.Note;
            _query = prefix[(start + 2)..];
            closed = _query.EndsWith("]]", StringComparison.Ordinal);
            if (closed) _query = _query[..^2];
            if (_query.Length > 240 || _query.IndexOfAny(['[', ']', '\u2028', '\n']) >= 0) start = -1;
        }
        else
        {
            start = prefix.LastIndexOf('#'); _kind = ReferenceKind.Tag;
            _query = start >= 0 ? prefix[(start + 1)..] : "";
            if (start >= 0 && (!NoteReferences.IsTagBoundary(prefix, start) || _query.Length > 0 && NoteReferences.NormalizeTag(_query) != _query)) start = -1;
        }
        if (start < 0 || NoteReferences.IsProtected(row.Node, start, prefix.Length - start)) { IsVisible = false; _target = null; return; }
        _target = Capture(row.Start + start, prefix.Length - start);
        _search.IsVisible = false; _remove.IsVisible = false;
        _query = _query.Trim();
        Fill();
        if (closed && allowAutoComplete && _kind == ReferenceKind.Note)
        {
            var exact = _choices.Where(choice => !choice.Create && choice.Label.Equals(_query, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) { _list.SelectedItem = exact[0]; Accept(); }
        }
    }

    private void Fill()
    {
        if (_target == null) return;
        _heading.Text = _kind == ReferenceKind.Note ? "链接到笔记" : "添加标签";
        var choices = new List<ReferenceChoice>();
        if (_kind == ReferenceKind.Note)
        {
            var documents = _owner.ReferenceDocuments.Where(document => NoteReferences.DisplayTitle(document.Title).Contains(_query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(document => NoteReferences.DisplayTitle(document.Title).Equals(_query, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(document => document.IsFavorite).ThenByDescending(document => document.UpdatedAt).Take(50);
            foreach (var document in documents)
                choices.Add(new(document.Id, NoteReferences.DisplayTitle(document.Title), (document.IsFavorite ? "收藏 · " : "") + DateTimeOffset.FromUnixTimeMilliseconds(document.UpdatedAt).LocalDateTime.ToString("M月d日 HH:mm") + " 更新"));
            if (_query.Length is > 0 and <= 240 && !choices.Any(choice => choice.Label.Equals(_query, StringComparison.OrdinalIgnoreCase)) && _owner.CreateReferencedNote != null)
                choices.Add(new("", _query, "创建后留在当前笔记", true));
        }
        else
        {
            foreach (var tag in _owner.ReferenceTags.Where(tag => tag.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Key).Take(50))
                choices.Add(new(tag.Key, tag.Name, tag.Count + " 篇笔记"));
            if (NoteReferences.NormalizeTag(_query) is { } tagName && !choices.Any(choice => choice.Id == NoteReferences.TagKey(tagName)))
                choices.Add(new(NoteReferences.TagKey(tagName), tagName, "新标签", true));
        }
        _choices = choices.ToArray(); _list.ItemsSource = _choices; _list.SelectedIndex = _choices.Length > 0 ? 0 : -1;
        _empty.Text = _kind == ReferenceKind.Tag ? "输入标签名称" : "没有匹配的笔记";
        _empty.IsVisible = _choices.Length == 0;
        IsVisible = true; _owner.Formatting.Reset(); Position();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsVisible) return false;
        if (IsComposing())
        {
            if (e.Key is Key.Enter or Key.Escape or Key.Tab) e.Handled = true;
            return e.Handled;
        }
        if (e.Key == Key.Escape) { Dismiss(); _owner.FocusText(); e.Handled = true; return true; }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        if (e.Key is Key.Up or Key.Down)
        {
            if (_choices.Length > 0) { _list.SelectedIndex = (_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1) + _choices.Length) % _choices.Length; _list.ScrollIntoView(_list.SelectedItem!); }
            e.Handled = true; return true;
        }
        if (e.Key is Key.Enter or Key.Tab && _list.SelectedItem is ReferenceChoice)
        { Accept(); e.Handled = true; return true; }
        return false;
    }

    private void Accept()
    {
        if (!Valid() || _target is not { } target || _list.SelectedItem is not ReferenceChoice choice || !_choices.Contains(choice) || _applying) return;
        _applying = true;
        try
        {
            var id = choice.Id;
            if (_kind == ReferenceKind.Note && choice.Create)
            {
                if (_owner.CreateReferencedNote?.Invoke(choice.Label) is not { } created || !Valid()) return;
                id = created.Id;
            }
            else if (_kind == ReferenceKind.Note && !_owner.ReferenceDocuments.Any(document => document.Id == id)) return;
            var kind = _kind;
            Dismiss(); target.Session.Selection = target.Selection;
            if (kind == ReferenceKind.Note) target.Session.InsertNoteLink(target.Start, target.Length, id, choice.Label, target.KeepText);
            else { target.Session.BreakTypingGroup(); target.Session.Edit(target.Start, target.Length, "#" + choice.Label + " ", false); }
            _owner.FocusText();
            _owner.Surface.ScrollTo(_owner.Surface.TextArea.Caret.Line, _owner.Surface.TextArea.Caret.Column);
        }
        finally { _applying = false; }
    }

    private void Position()
    {
        if (!IsVisible) return;
        var view = _owner.Surface.TextArea.TextView;
        if (!view.VisualLinesValid || _owner.Bounds.Width < 100) return;
        Width = Math.Min(328, _owner.Bounds.Width - 8);
        _list.MaxHeight = Math.Max(42, Math.Min(256, _owner.Bounds.Height - (_manual ? 146 : 100)));
        Measure(new Size(Width, double.PositiveInfinity));
        var point = view.GetVisualPosition(_owner.Surface.TextArea.Caret.Position, VisualYPosition.LineBottom) - view.ScrollOffset;
        point = view.TranslatePoint(point, _owner) ?? point;
        var height = DesiredSize.Height - Margin.Top - Margin.Bottom;
        Margin = new(Math.Clamp(point.X, 0, Math.Max(0, _owner.Bounds.Width - Width - 4)), Math.Clamp(point.Y + 5, 0, Math.Max(0, _owner.Bounds.Height - height - 4)), 0, 0);
    }
}
