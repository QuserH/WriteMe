using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

internal sealed partial class NativeTableView : NativeLayoutView
{
    private sealed record CellView(Border Frame, ContentControl Host, Border Outline, TextBlock Preview, int Row, int Column);
    private readonly Grid _grid = new();
    private readonly TextBlock _size = new() { FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = new() { Text = "Tab 下一格 · Shift+点击 多选", FontSize = 10, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<Guid, CellView> _cells = [];
    private readonly ScrollViewer _scroll;
    private readonly Border _frame;
    private readonly StackPanel _content = new() { Spacing = 4 };
    private BlockEditor? _editor;
    private Guid? _editingCell;
    private int _row;
    private int _column;
    private int _anchorRow;
    private int _anchorColumn;
    private CellRange _range;
    private bool _rangeSelected;
    private bool _clipboardBusy;
    private NoteNode? _rendered;

    public NativeTableView(BlockEditor owner, Guid id) : base(owner, id)
    {
        AutomationProperties.SetName(this, "表格");
        var bar = new Grid { ColumnDefinitions = new("*,Auto"), Height = 28 };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        info.Children.Add(new SidebarGlyph(SidebarSymbol.Table, 14) { VerticalAlignment = VerticalAlignment.Center });
        info.Children.Add(_size); info.Children.Add(_hint); bar.Children.Add(info);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        actions.Children.Add(Action("在当前行下方插入行", SidebarSymbol.AddRow, "TableAddRow", () => InsertRow(_row + 1, _column)));
        actions.Children.Add(Action("在当前列右侧插入列", SidebarSymbol.AddColumn, "TableAddColumn", () => InsertColumn(_row, _column + 1)));
        actions.Children.Add(Action("表格操作", SidebarSymbol.More, "TableMenu", OpenMenu));
        Grid.SetColumn(actions, 1); bar.Children.Add(actions);
        _content.Children.Add(bar);
        _scroll = new ScrollViewer { Content = _grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 620 };
        _frame = new Border { Child = _scroll, BorderBrush = owner.PageLine, BorderThickness = new(1), CornerRadius = new(8), ClipToBounds = true };
        _content.Children.Add(_frame);
        Child = _content;
        AddHandler(KeyDownEvent, OnRangeKey, RoutingStrategies.Bubble);
    }

    public override void Update(NoteNode node, double width)
    {
        if (Disposed) return;
        Node = node; Width = Math.Max(100, width);
        var appearanceChanged = RefreshOwnerAppearance();
        if (appearanceChanged)
        {
            _size.Foreground = _hint.Foreground = Owner.PageMuted;
            _frame.BorderBrush = Owner.PageLine;
            if (_editor != null) Owner.ApplyEmbeddedAppearance(_editor);
        }
        _hint.IsVisible = width >= 520;
        if (!LayoutBlocks.IsEditableTable(node))
        {
            ShowUnsupported("此表格含合并单元格或超出 100 行 / 12 列，内容已原样保留。可通过块菜单复制或删除，导出保留完整结构。");
            return;
        }
        Child = _content;
        var rows = node.Content.Length; var columns = node.Content[0].Content.Length;
        _size.Text = $"{rows} 行 × {columns} 列";
        if (_resizing && (_resizeRevision != Session.Revision || Math.Abs(_resizeViewWidth - width) > 1)) CancelResize();
        var ids = node.Content.SelectMany(row => row.Content).Select(cell => cell.Id).ToArray();
        var rebuild = _cells.Count != ids.Length || ids.Any(id => !_cells.ContainsKey(id))
            || _rendered?.Content[0].Content.Length != columns;
        if (rebuild)
        {
            if (_editingCell != null && !ids.Contains(_editingCell.Value)) StopEditing();
            // Reuse the active editor when only a neighbouring row/column is inserted.
            foreach (var cell in _cells.Values) cell.Host.Content = null;
            _grid.Children.Clear(); _cells.Clear(); _grid.RowDefinitions.Clear(); _grid.ColumnDefinitions.Clear();
            for (var r = 0; r < rows; r++) _grid.RowDefinitions.Add(new(GridLength.Auto));
            for (var c = 0; c < columns; c++) _grid.ColumnDefinitions.Add(new(new(1, GridUnitType.Star)));
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++) AddCell(node.Content[r].Content[c], r, c);
            for (var c = 0; c < columns - 1; c++) AddResizeGrip(c, rows);
        }
        if (!_resizing) ApplyColumnWidths();
        _row = Math.Clamp(_row, 0, rows - 1); _column = Math.Clamp(_column, 0, columns - 1);
        _range = new(Math.Clamp(_range.Top, 0, rows - 1), Math.Clamp(_range.Left, 0, columns - 1), Math.Clamp(_range.Bottom, 0, rows - 1), Math.Clamp(_range.Right, 0, columns - 1));
        if (appearanceChanged || !ReferenceEquals(_rendered, node))
            foreach (var row in node.Content)
                foreach (var cell in row.Content)
                {
                    var view = _cells[cell.Id];
                    view.Frame.BorderBrush = Owner.PageLine;
                    view.Outline.BorderBrush = Owner.PageColor("#6E9AD2", "#94B7EF");
                    if (cell.Id != _editingCell) FillPreview(view.Preview, cell);
                    view.Frame.Background = cell.Type == "tableHeader" ? Owner.PageColor("#F1F3F6", "#303B48")
                        : node.Bool("striped") && view.Row % 2 == 0 ? Owner.PageColor("#F8F9FB", "#2B323B") : Brushes.Transparent;
                }
        _rendered = node;
        if (_editor != null && _editingCell is { } editing)
            _editor.Surface.FontWeight = NoteTree.Find(node, editing)?.Type == "tableHeader" ? FontWeight.SemiBold : FontWeight.Normal;
        PaintSelection();
    }

    private void AddCell(NoteNode cell, int row, int column)
    {
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Math.Max(13, Owner.Surface.FontSize - 1), Foreground = Owner.Surface.Foreground, MinHeight = 26, LineHeight = 23, VerticalAlignment = VerticalAlignment.Top };
        var host = new ContentControl { Content = preview, Margin = new(10, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        var outline = new Border { BorderBrush = Owner.PageColor("#6E9AD2", "#94B7EF"), BorderThickness = new(1.5), IsHitTestVisible = false, IsVisible = false };
        var content = new Grid(); content.Children.Add(host); content.Children.Add(outline);
        var frame = new Border { Child = content, MinHeight = 43, BorderBrush = Owner.PageLine,
            BorderThickness = new(0, 0, column + 1 < Node!.Content[0].Content.Length ? 1 : 0, row + 1 < Node.Content.Length ? 1 : 0) };
        if (_editingCell == cell.Id && _editor != null) { host.Content = _editor; _row = row; _column = column; }
        AutomationProperties.SetName(frame, $"第 {row + 1} 行，第 {column + 1} 列");
        AutomationProperties.SetAutomationId(frame, $"Cell_{BlockId}_{row}_{column}");
        Grid.SetRow(frame, row); Grid.SetColumn(frame, column); _grid.Children.Add(frame);
        _cells.Add(cell.Id, new(frame, host, outline, preview, row, column));
        frame.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!CanEdit || !e.GetCurrentPoint(frame).Properties.IsLeftButtonPressed) return;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { _row = row; _column = column; SelectRange(CellRange.Between(_anchorRow, _anchorColumn, row, column)); e.Handled = true; return; }
            if (_editingCell == cell.Id && !_rangeSelected) return;
            var point = e.GetPosition(frame);
            FocusCell(row, column);
            var editor = _editor;
            Dispatcher.UIThread.Post(() =>
            {
                if (editor == null || !ReferenceEquals(editor, _editor) || !CanEdit) return;
                if (frame.TranslatePoint(point, editor.Surface) is { } location && editor.Surface.GetPositionFromPoint(location) is { } position)
                    editor.Surface.CaretOffset = editor.Surface.Document.GetOffset(position.Line, position.Column);
            }, DispatcherPriority.Input);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void FillPreview(TextBlock text, NoteNode cell)
    {
        text.FontFamily = Owner.Surface.FontFamily;
        text.FontSize = Math.Max(13, Owner.Surface.FontSize - 1);
        text.LineHeight = Math.Max(23, text.FontSize * Owner.Surface.Options.LineHeightFactor);
        text.Foreground = Owner.Surface.Foreground;
        text.Inlines!.Clear();
        static IEnumerable<NoteNode> PreviewBlocks(NoteNode node)
        {
            if (node.IsTextBlock || !node.IsContentContainer) { yield return node; yield break; }
            foreach (var child in node.Content)
                foreach (var block in PreviewBlocks(child)) yield return block;
        }
        var blocks = PreviewBlocks(cell).ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            if (i > 0) text.Inlines.Add(new LineBreak());
            if (!blocks[i].IsTextBlock)
            {
                var label = blocks[i].Type switch
                {
                    "image" => "图片 · " + (blocks[i].String("name") ?? blocks[i].String("alt") ?? "图片"),
                    "attachment" => "附件 · " + (blocks[i].String("name") ?? "文件"),
                    "horizontalRule" => "────", _ => "保留的 " + blocks[i].Type + " 内容"
                };
                text.Inlines.Add(new Run(label) { Foreground = Owner.PageMuted });
                continue;
            }
            foreach (var node in blocks[i].Content)
            {
                if (node.Type == "hardBreak") { text.Inlines.Add(new LineBreak()); continue; }
                if (node.Type != "text") continue;
                var run = new Run(node.Text.Replace('\u2028', '\n'));
                if (node.Marks.Any(mark => mark.Type == "bold") || cell.Type == "tableHeader") run.FontWeight = FontWeight.SemiBold;
                if (node.Marks.Any(mark => mark.Type == "italic")) run.FontStyle = FontStyle.Italic;
                if (TextColor.Read(node.Marks) is { } color) run.Foreground = Brush.Parse(color);
                if (node.Marks.FirstOrDefault(mark => mark.Type == "highlight")?.String("color") is { } highlight && Color.TryParse(highlight, out var fill)) run.Background = new SolidColorBrush(fill);
                if (node.Marks.Any(mark => mark.Type is "underline" or "link" or "noteLink")) run.TextDecorations = TextDecorations.Underline;
                text.Inlines.Add(run);
            }
        }
        text.FontWeight = cell.Type == "tableHeader" ? FontWeight.SemiBold : FontWeight.Normal;
        text.TextAlignment = blocks.FirstOrDefault()?.String("textAlign") switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
    }

    private void PaintSelection()
    {
        foreach (var (id, cell) in _cells)
        {
            var selected = _rangeSelected ? _range.Contains(cell.Row, cell.Column) : _editor != null && cell.Row == _row && cell.Column == _column;
            cell.Outline.IsVisible = selected;
            cell.Frame.Background = _rangeSelected && selected ? Owner.PageColor("#E7F0FD", "#334C6B")
                : Node?.Content[cell.Row].Content[cell.Column].Type == "tableHeader" ? Owner.PageColor("#F1F3F6", "#303B48")
                : Node?.Bool("striped") == true && cell.Row % 2 == 0 ? Owner.PageColor("#F8F9FB", "#2B323B") : Brushes.Transparent;
        }
    }

    private void StopEditing()
    {
        if (_editingCell is { } id && _cells.TryGetValue(id, out var old))
        {
            old.Host.Content = old.Preview;
            if (Node != null && NoteTree.Find(Node, id) is { } cell) FillPreview(old.Preview, cell);
        }
        _editor?.DisposeEmbedded(); _editor = null; _editingCell = null;
    }

    private void FocusCell(int row, int column, TextPoint? point = null)
    {
        if (!CanEdit || Node == null || !LayoutBlocks.IsEditableTable(Node)) return;
        row = Math.Clamp(row, 0, Node.Content.Length - 1); column = Math.Clamp(column, 0, Node.Content[0].Content.Length - 1);
        var cell = Node.Content[row].Content[column];
        _rangeSelected = false;
        if (_editingCell != cell.Id)
        {
            StopEditing();
            _editor = Owner.CreateEmbedded(cell.Id, true);
            _editingCell = cell.Id;
            _editor.EmbeddedKey = OnEditorKey;
            _cells[cell.Id].Host.Content = _editor;
        }
        _anchorRow = _row = row; _anchorColumn = _column = column; _range = new(row, column, row, column);
        _editor!.Surface.FontWeight = cell.Type == "tableHeader" ? FontWeight.SemiBold : FontWeight.Normal;
        if (point is { } target) _editor!.Session.Selection = Session.Selection.Anchor.NodeId == target.NodeId ? Session.Selection : EditorSelection.At(target.NodeId, target.Offset);
        else _editor!.Session.Selection = EditorSelection.At(_editor.Session.Projection.Rows[0].Node.Id);
        _editor.SyncSurface(); _editor.FocusText();
        var editor = _editor;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(editor, _editor) && !Disposed) { editor.FocusText(); _cells[cell.Id].Frame.BringIntoView(); } }, DispatcherPriority.Input);
        PaintSelection();
    }

    public override bool FocusPoint(TextPoint point)
    {
        if (Node == null || !LayoutBlocks.IsEditableTable(Node)) return false;
        for (var r = 0; r < Node.Content.Length; r++)
            for (var c = 0; c < Node.Content[r].Content.Length; c++)
                if (NoteTree.Find(Node.Content[r].Content[c], point.NodeId) != null) { FocusCell(r, c, point); return true; }
        return false;
    }

    private bool OnEditorKey(KeyEventArgs e)
    {
        if (HandleResizeKey(e)) return true;
        if (_editor == null || !CanEdit) return false;
        if (e.Key == Key.Tab && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var columns = Node!.Content[0].Content.Length;
            var index = _row * columns + _column + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            if (index < 0) { Owner.SelectLayout(BlockId); return true; }
            if (index >= Node.Content.Length * columns && !Session.InsertTableRow(BlockId, Node.Content.Length)) { Owner.ShowNotice("已达到 100 行上限"); return true; }
            FocusCell(index / columns, index % columns); return true;
        }
        if (e.Key == Key.Escape) { SelectRange(new(_row, _column, _row, _column)); return true; }
        if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { _ = PasteAsync(false); return true; }
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Owner.SelectLayout(BlockId); Session.Enter(Session.Projection.Find(BlockId)!.Start); Owner.FocusText(); return true; }
        return false;
    }

    private void SelectRange(CellRange range)
    {
        StopEditing(); _range = range; _rangeSelected = true;
        Owner.SelectLayout(BlockId);
        // Keep the containing TextArea as the IME client while the rectangle is selected.
        // A focusable Border has no text input client on Windows.
        Owner.Surface.TextArea.Focus(); PaintSelection();
    }

    private void OnRangeKey(object? sender, KeyEventArgs e)
    {
        if (!_rangeSelected || e.Handled || !CanEdit) return;
        if (e.Key == Key.Escape) { _rangeSelected = false; PaintSelection(); Owner.SelectLayout(BlockId); e.Handled = true; }
        else if (e.Key is Key.Back or Key.Delete) { Session.ClearTableCells(BlockId, _range); Owner.Surface.TextArea.Focus(); e.Handled = true; }
        else if (e.Key is Key.Enter or Key.F2) { FocusCell(_range.Top, _range.Left); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.C or Key.X) { _ = CopyAsync(e.Key == Key.X); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V) { _ = PasteAsync(true); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A) { SelectRange(new(0, 0, Node!.Content.Length - 1, Node.Content[0].Content.Length - 1)); e.Handled = true; }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Z or Key.Y)
        {
            if (e.Key == Key.Y || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) Session.Redo(); else Session.Undo();
            if (Session.Projection.LayoutHost(Session.Selection.Caret.NodeId) != null) { ClearRangeSelection(); Owner.FocusHistorySelection(); }
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var r = Math.Clamp(_row + (e.Key == Key.Down ? 1 : e.Key == Key.Up ? -1 : 0), 0, Node!.Content.Length - 1);
            var c = Math.Clamp(_column + (e.Key == Key.Right ? 1 : e.Key == Key.Left ? -1 : 0), 0, Node.Content[0].Content.Length - 1);
            _row = r; _column = c;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) SelectRange(CellRange.Between(_anchorRow, _anchorColumn, r, c));
            else { _anchorRow = r; _anchorColumn = c; SelectRange(new(r, c, r, c)); }
            e.Handled = true;
        }
    }

    internal bool HandleRangeKey(KeyEventArgs e)
    {
        if (HandleResizeKey(e)) return true;
        if (!_rangeSelected) return false;
        OnRangeKey(this, e);
        return e.Handled;
    }
    internal void ClearRangeSelection() { if (!_rangeSelected) return; _rangeSelected = false; PaintSelection(); }
    internal bool HandleRangeText(string text)
    {
        if (!_rangeSelected || !CanEdit) return false;
        var range = _range;
        Session.ReplaceTableRange(BlockId, range, text);
        FocusCell(range.Top, range.Left, Session.Selection.Caret);
        return true;
    }

    private async Task CopyAsync(bool cut)
    {
        if (_clipboardBusy || !CanEdit || Node == null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var revision = Session.Revision; var range = _range; var value = LayoutBlocks.CopyCells(Node, range);
        _clipboardBusy = true;
        try { await clipboard.SetTextAsync(value); if (cut && CanEdit && Session.Revision == revision && _range == range) Session.ClearTableCells(BlockId, range); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException) { Owner.ShowNotice("无法写入剪贴板，请重试"); }
        finally { _clipboardBusy = false; }
    }

    private async Task PasteAsync(bool cells)
    {
        if (_clipboardBusy || !CanEdit || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var revision = Session.Revision; var row = _row; var column = _column; var editor = _editor;
        var selection = editor?.Session.Selection;
        _clipboardBusy = true;
        try
        {
            using var data = await clipboard.TryGetDataAsync();
            var value = data == null ? null : await data.TryGetTextAsync();
            if (value == null || !CanEdit || Session.Revision != revision || _row != row || _column != column || _editor != editor || editor?.Session.Selection != selection) return;
            if (cells || value.Contains('\t') || value.Contains('\n') || value.Contains('\r'))
            { Session.PasteTableCells(BlockId, row, column, value); FocusCell(row, column); }
            else if (editor != null) editor.Session.Edit(editor.Surface.SelectionStart, editor.Surface.SelectionLength, value, false);
        }
        catch (ArgumentException ex) { Owner.ShowNotice(ex.Message); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException) { Owner.ShowNotice("无法读取剪贴板，请重试"); }
        finally { _clipboardBusy = false; }
    }

    private void OpenMenu()
    {
        if (!CanEdit || Node == null) return;
        var revision = Session.Revision;
        var row = _row; var column = _column; var range = _range;
        MenuItem Item(string title, string id, Action action)
        {
            var item = new MenuItem { Header = title }; AutomationProperties.SetAutomationId(item, id);
            item.Click += (_, _) => { if (CanEdit && Session.Revision == revision) action(); }; return item;
        }
        var menu = new ContextMenu { ItemsSource = new Control[]
        {
            Item("在上方插入行", "TableInsertAbove", () => InsertRow(row, column)),
            Item("在左侧插入列", "TableInsertLeft", () => InsertColumn(row, column)),
            Item("删除当前行", "TableDeleteRow", () => { if (Session.DeleteTableRow(BlockId, row)) FocusAfterRemoval(row, column); }),
            Item("删除当前列", "TableDeleteColumn", () => { if (Session.DeleteTableColumn(BlockId, column)) FocusAfterRemoval(row, column); }),
            new Separator(),
            Item(LayoutBlocks.HasHeader(Node) ? "取消表头" : "首行设为表头", "TableHeader", () => Session.SetTableHeader(BlockId, !LayoutBlocks.HasHeader(Node))),
            Item(Node.Bool("striped") ? "关闭交替行底色" : "交替行底色", "TableStriped", () => Session.SetTableStriped(BlockId, !Node.Bool("striped"))),
            Item("均分列宽", "TableEqualWidths", () => Session.SetTableColumnWidths(BlockId)),
            Item("选择整个表格", "TableSelectAll", () => SelectRange(new(0, 0, Node.Content.Length - 1, Node.Content[0].Content.Length - 1))),
            Item("清空所选单元格", "TableClear", () => Session.ClearTableCells(BlockId, range)),
            new Separator(),
            Item("删除表格", "TableDelete", () => { Session.DeleteBlock(BlockId); Owner.FocusText(); })
        } };
        ContextMenu = menu; menu.Closed += (_, _) => { if (ReferenceEquals(ContextMenu, menu)) ContextMenu = null; }; menu.Open(this);
    }

    private void InsertRow(int row, int column)
    {
        if (Session.InsertTableRow(BlockId, row)) FocusCell(row, column);
        else Owner.ShowNotice("已达到 100 行上限");
    }

    private void InsertColumn(int row, int column)
    {
        if (Session.InsertTableColumn(BlockId, column)) FocusCell(row, column);
        else Owner.ShowNotice("已达到 12 列上限");
    }

    private void FocusAfterRemoval(int row, int column)
    {
        if (CanEdit) FocusCell(row, column);
        else Owner.FocusHistorySelection();
    }

    public override void Dispose() { if (Disposed) return; Disposed = true; CancelResize(); ContextMenu?.Close(); StopEditing(); }
    public override void UpdateCatalogue(IReadOnlyList<DocumentInfo> documents, IReadOnlyList<TagInfo> tags) => _editor?.SetReferenceCatalogue(documents, tags);
}
