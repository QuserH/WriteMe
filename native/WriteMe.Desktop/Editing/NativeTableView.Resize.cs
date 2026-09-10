using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

internal sealed partial class NativeTableView
{
    private bool _resizing;
    private long _resizeRevision;
    private double _resizeViewWidth;
    private Point _resizeStart;
    private double[] _resizeWidths = [];
    private IPointer? _resizePointer;

    private void ApplyColumnWidths()
    {
        if (Node == null || !LayoutBlocks.IsEditableTable(Node)) return;
        var widths = LayoutBlocks.ColumnWidths(Node).Select(width => width > 0 ? width : 132).ToArray();
        _grid.Width = Math.Max(Width - 2, widths.Sum());
        for (var column = 0; column < widths.Length && column < _grid.ColumnDefinitions.Count; column++)
            _grid.ColumnDefinitions[column].Width = new(widths[column], GridUnitType.Star);
    }

    private void AddResizeGrip(int left, int rows)
    {
        var grip = new Border { Width = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 0, -4, 0),
            Background = Brushes.Transparent, Cursor = new(StandardCursorType.SizeWestEast), ZIndex = 5 };
        Grid.SetColumn(grip, left); Grid.SetRowSpan(grip, rows); _grid.Children.Add(grip);
        AutomationProperties.SetName(grip, $"调整第 {left + 1} 列宽度"); AutomationProperties.SetAutomationId(grip, $"TableResize_{BlockId}_{left}");
        ToolTip.SetTip(grip, "拖动调整列宽");
        grip.PointerPressed += (_, e) =>
        {
            if (!CanEdit || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            _resizeWidths = _grid.ColumnDefinitions.Select(column => column.ActualWidth).ToArray();
            _resizeStart = e.GetPosition(this); _resizeRevision = Session.Revision; _resizeViewWidth = Width; _resizing = true;
            _resizePointer = e.Pointer; e.Pointer.Capture(grip); e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!_resizing || Session.Revision != _resizeRevision) return;
            var total = _resizeWidths[left] + _resizeWidths[left + 1];
            var first = Math.Clamp(_resizeWidths[left] + e.GetPosition(this).X - _resizeStart.X,
                Math.Max(LayoutBlocks.MinColumnWidth, total - LayoutBlocks.MaxColumnWidth), Math.Min(LayoutBlocks.MaxColumnWidth, total - LayoutBlocks.MinColumnWidth));
            for (var column = 0; column < _resizeWidths.Length; column++)
                _grid.ColumnDefinitions[column].Width = new(column == left ? first : column == left + 1 ? total - first : _resizeWidths[column]);
            e.Handled = true;
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!_resizing) return;
            var widths = _grid.ColumnDefinitions.Select(column => Math.Clamp((int)Math.Round(column.ActualWidth), LayoutBlocks.MinColumnWidth, LayoutBlocks.MaxColumnWidth)).ToArray();
            var moved = Math.Abs(e.GetPosition(this).X - _resizeStart.X) > 1;
            _resizing = false; _resizePointer = null; e.Pointer.Capture(null);
            if (moved && CanEdit && Session.Revision == _resizeRevision) Session.SetTableColumnWidths(BlockId, widths);
            ApplyColumnWidths(); e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => CancelResize();
    }

    private void CancelResize()
    {
        if (!_resizing) return;
        _resizing = false;
        var pointer = _resizePointer; _resizePointer = null; pointer?.Capture(null);
        ApplyColumnWidths();
    }

    private bool HandleResizeKey(KeyEventArgs e)
    {
        if (!_resizing || e.Key != Key.Escape) return false;
        CancelResize(); e.Handled = true; return true;
    }
}
