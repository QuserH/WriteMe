using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;

namespace WriteMe.Desktop.Editing;

internal sealed class TableSizePicker : StackPanel
{
    public TableSizePicker(Action<int, int> insert)
    {
        Spacing = 10; Margin = new(4);
        var label = new TextBlock { Text = "选择表格行列", FontSize = 13, Foreground = Ui.Ink };
        Children.Add(label);
        var grid = new UniformGrid { Columns = 6, Rows = 5 };
        var buttons = new List<(Button Button, int Row, int Column)>();
        for (var r = 1; r <= 5; r++)
            for (var c = 1; c <= 6; c++)
            {
                var rows = r; var columns = c;
                var button = new Button { Width = 25, Height = 25, Padding = new(0), Margin = new(2), Background = Ui.Subtle, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(3) };
                AutomationProperties.SetName(button, $"插入 {rows} 行 {columns} 列表格"); AutomationProperties.SetAutomationId(button, $"TableSize_{rows}_{columns}");
                void Preview() { label.Text = $"{rows} 行 × {columns} 列"; foreach (var cell in buttons) cell.Button.Background = cell.Row <= rows && cell.Column <= columns ? Ui.Chrome("#DCE9F9") : Ui.Subtle; }
                button.PointerEntered += (_, _) => Preview(); button.GotFocus += (_, _) => Preview();
                button.Click += (_, _) => insert(rows, columns);
                buttons.Add((button, rows, columns)); grid.Children.Add(button);
            }
        Children.Add(grid);
        Children.Add(new TextBlock { Text = "插入后可以继续增减行列", FontSize = 11, Foreground = Ui.Muted });
    }
}
