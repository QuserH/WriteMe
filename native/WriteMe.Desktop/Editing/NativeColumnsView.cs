using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

internal sealed class NativeColumnsView : NativeLayoutView
{
    private readonly Grid _columns = new();
    private readonly StackPanel _content = new() { Spacing = 6 };
    private readonly Dictionary<Guid, BlockEditor> _editors = [];
    private readonly TextBlock _label = new() { Text = "分栏", FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private int[] _weights = [];
    private Guid[] _ids = [];
    private bool _stacked;
    private bool _resizing;
    private Point _resizeStart;
    private int[] _resizeWeights = [];
    private long _resizeRevision;
    private IPointer? _resizePointer;
    private double _resizeViewWidth;

    public NativeColumnsView(BlockEditor owner, Guid id) : base(owner, id)
    {
        var bar = new Grid { ColumnDefinitions = new("*,Auto"), Height = 28 };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        info.Children.Add(new SidebarGlyph(SidebarSymbol.Columns, 15) { VerticalAlignment = VerticalAlignment.Center }); info.Children.Add(_label); bar.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.Add(Action("分栏布局与比例", SidebarSymbol.More, "ColumnsMenu", OpenMenu));
        actions.Children.Add(Action("取消分栏，保留全部内容", SidebarSymbol.Unwrap, "ColumnsUnwrap", () => { Session.UnwrapColumns(BlockId); Owner.FocusText(); }));
        Grid.SetColumn(actions, 1); bar.Children.Add(actions); _content.Children.Add(bar); _content.Children.Add(_columns); Child = _content;
        AutomationProperties.SetName(this, "分栏布局");
    }

    public override void Update(NoteNode node, double width)
    {
        if (Disposed) return;
        Node = node; Width = Math.Max(100, width);
        if (RefreshOwnerAppearance())
        {
            _label.Foreground = Owner.PageMuted;
            foreach (var editor in _editors.Values) Owner.ApplyEmbeddedAppearance(editor);
            foreach (var border in _columns.Children.OfType<Border>())
            {
                if (border.Child is BlockEditor) border.BorderBrush = Owner.PageLine;
                else if (border.Child is Border line) line.Background = Owner.PageLine;
            }
        }
        if (!LayoutBlocks.IsEditableColumns(node)) { ShowUnsupported("此分栏结构已原样保留；当前编辑器支持两栏或三栏。"); return; }
        Child = _content;
        var ids = node.Content.Select(column => column.Id).ToArray();
        var weights = node.Content.Select(column => Math.Clamp(column.Int("width", 1), 1, 100)).ToArray();
        var stacked = width < (ids.Length == 3 ? 560 : 380);
        _label.Text = $"{(ids.Length == 2 ? "两" : "三")}栏" + (stacked ? " · 窄窗口纵向排列" : "");
        if (_resizing && (Session.Revision != _resizeRevision || _stacked != stacked || Math.Abs(Width - _resizeViewWidth) > 1)) CancelColumnResize();
        if (_ids.SequenceEqual(ids) && _weights.SequenceEqual(weights) && _stacked == stacked) return;
        foreach (var pair in _editors.Where(pair => !ids.Contains(pair.Key)).ToArray()) { pair.Value.DisposeEmbedded(); _editors.Remove(pair.Key); }
        foreach (var border in _columns.Children.OfType<Border>()) if (border.Child is BlockEditor) border.Child = null;
        _columns.Children.Clear(); _columns.ColumnDefinitions.Clear(); _columns.RowDefinitions.Clear();
        _ids = ids; _weights = weights; _stacked = stacked;
        for (var i = 0; i < ids.Length; i++)
        {
            if (!_editors.TryGetValue(ids[i], out var editor))
            {
                editor = Owner.CreateEmbedded(ids[i]);
                var columnId = ids[i];
                editor.EmbeddedKey = e => HandleColumnKey(columnId, e);
                _editors.Add(ids[i], editor);
            }
            var card = new Border { Child = editor, Padding = new(2, 9, 6, 10), MinHeight = 94, BorderBrush = Owner.PageLine, BorderThickness = new(1), CornerRadius = new(8), Background = Brushes.Transparent };
            AutomationProperties.SetName(card, $"第 {i + 1} 栏"); AutomationProperties.SetAutomationId(card, $"Column_{BlockId}_{i}");
            if (stacked)
            {
                _columns.RowDefinitions.Add(new(GridLength.Auto));
                if (i > 0) card.Margin = new(0, 12, 0, 0);
                Grid.SetRow(card, i);
            }
            else
            {
                if (i > 0)
                {
                    _columns.ColumnDefinitions.Add(new(new(16)));
                    var grip = ResizeGrip(i - 1); Grid.SetColumn(grip, i * 2 - 1); _columns.Children.Add(grip);
                }
                _columns.ColumnDefinitions.Add(new(new(weights[i], GridUnitType.Star)));
                Grid.SetColumn(card, i * 2);
            }
            _columns.Children.Add(card);
        }
    }

    private Border ResizeGrip(int left)
    {
        var grip = new Border { Width = 16, Background = Brushes.Transparent, Cursor = new(StandardCursorType.SizeWestEast), Child = new Border { Width = 2, Height = 36, Background = Owner.PageLine, CornerRadius = new(1), VerticalAlignment = VerticalAlignment.Center } };
        ToolTip.SetTip(grip, "拖动调整栏宽"); AutomationProperties.SetName(grip, "调整分栏宽度"); AutomationProperties.SetAutomationId(grip, $"ColumnResize_{BlockId}_{left}");
        grip.PointerPressed += (_, e) =>
        {
            if (!CanEdit || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            _resizing = true; _resizeStart = e.GetPosition(this); _resizeWeights = [.. _weights]; _resizeRevision = Session.Revision; _resizePointer = e.Pointer; _resizeViewWidth = Width;
            e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!_resizing || Session.Revision != _resizeRevision) return;
            var total = _resizeWeights.Sum();
            var available = Math.Max(100, Width - 16 * (_ids.Length - 1));
            var pair = (_resizeWeights[left] + _resizeWeights[left + 1]) / (double)total;
            var first = Math.Clamp(_resizeWeights[left] / (double)total + (e.GetPosition(this).X - _resizeStart.X) / available, pair * .2, pair * .8);
            _columns.ColumnDefinitions[left * 2].Width = new(first * total, GridUnitType.Star);
            _columns.ColumnDefinitions[(left + 1) * 2].Width = new((pair - first) * total, GridUnitType.Star);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!_resizing) return;
            if (Math.Abs(e.GetPosition(this).X - _resizeStart.X) <= 1) { CancelColumnResize(); e.Handled = true; return; }
            _resizing = false; _resizePointer = null; e.Pointer.Capture(null);
            if (!CanEdit || Session.Revision != _resizeRevision) return;
            var values = Enumerable.Range(0, _ids.Length).Select(i => _columns.ColumnDefinitions[i * 2].Width.Value).ToArray();
            var total = values.Sum(); var weights = values.Select(value => Math.Clamp((int)Math.Round(value / total * 100), 1, 100)).ToArray();
            Session.SetColumns(BlockId, _ids.Length, weights); e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => CancelColumnResize();
        return grip;
    }

    private bool HandleColumnKey(Guid id, KeyEventArgs e)
    {
        if (!CanEdit || !_editors.TryGetValue(id, out var editor)) return false;
        if (_resizing && e.Key == Key.Escape) { CancelColumnResize(); return true; }
        if (e.Key == Key.Escape) { Owner.SelectLayout(BlockId); return true; }
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        { Owner.SelectLayout(BlockId); Session.Enter(Session.Projection.Find(BlockId)!.Start); Owner.FocusText(); return true; }
        if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var index = (Array.IndexOf(_ids, id) + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1) + _ids.Length) % _ids.Length;
            _editors[_ids[index]].FocusText(); return true;
        }
        return false;
    }

    public override bool FocusPoint(TextPoint point)
    {
        if (Node == null) return false;
        foreach (var column in Node.Content)
            if (NoteTree.Find(column, point.NodeId) != null && _editors.TryGetValue(column.Id, out var editor))
            {
                editor.Session.Selection = Session.Selection; editor.SyncSurface(); editor.FocusText(); return true;
            }
        return false;
    }

    private void OpenMenu()
    {
        if (!CanEdit) return;
        var revision = Session.Revision;
        MenuItem Item(string name, string id, Action action)
        {
            var item = new MenuItem { Header = name }; AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => { if (CanEdit && Session.Revision == revision) { action(); Owner.FocusText(); } }; return item;
        }
        var items = new List<Control>
        {
            Item("两栏 · 等宽", "ColumnsEqual2", () => Session.SetColumns(BlockId, 2)),
            Item("两栏 · 左宽右窄", "ColumnsWideLeft", () => Session.SetColumns(BlockId, 2, 2, 1)),
            Item("两栏 · 左窄右宽", "ColumnsWideRight", () => Session.SetColumns(BlockId, 2, 1, 2)),
            Item("三栏 · 等宽", "ColumnsEqual3", () => Session.SetColumns(BlockId, 3)),
            new Separator(),
            Item("取消分栏，保留全部内容", "ColumnsKeepContent", () => Session.UnwrapColumns(BlockId))
        };
        var active = Owner.ActiveEditor.Session.ScopeId;
        if (active is { } scope && Array.IndexOf(_ids, scope) is >= 0 and var index)
            items.Add(Item($"删除第 {index + 1} 栏及其内容", "ColumnsDeleteCurrent", () => Session.DeleteColumn(BlockId, index)));
        var menu = new ContextMenu { ItemsSource = items };
        ContextMenu = menu; menu.Closed += (_, _) => { if (ReferenceEquals(ContextMenu, menu)) ContextMenu = null; }; menu.Open(this);
    }

    public override void Dispose()
    {
        if (Disposed) return; Disposed = true; CancelColumnResize(); ContextMenu?.Close();
        foreach (var editor in _editors.Values) editor.DisposeEmbedded(); _editors.Clear();
    }

    public override void UpdateCatalogue(IReadOnlyList<DocumentInfo> documents, IReadOnlyList<TagInfo> tags)
    {
        foreach (var editor in _editors.Values) editor.SetReferenceCatalogue(documents, tags);
    }

    private void CancelColumnResize()
    {
        if (!_resizing) return;
        _resizing = false;
        var pointer = _resizePointer; _resizePointer = null; pointer?.Capture(null);
        for (var i = 0; i < _resizeWeights.Length && i * 2 < _columns.ColumnDefinitions.Count; i++) _columns.ColumnDefinitions[i * 2].Width = new(_resizeWeights[i], GridUnitType.Star);
    }
}
