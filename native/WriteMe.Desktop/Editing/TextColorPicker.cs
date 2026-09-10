using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 侧栏与浮层共用颜色选择，草稿提交仍核对文档选区 — 见 .agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md
internal sealed class TextColorPicker : StackPanel
{
    private readonly TextBox _input;
    private readonly Button _default;
    public TextColorPicker(SelectionFormats? formats, Action<string?> apply, Action cancel, string idPrefix)
    {
        Spacing = 8;
        Classes.Add("textColorPicker");
        var current = formats?.UniformTextColor;
        Children.Add(new TextBlock
        {
            Text = formats?.TextColorCoverage != MarkCoverage.None && current == null && formats != null ? "文字颜色 · 混合" : "文字颜色",
            FontSize = 11, Foreground = Ui.Muted
        });
        var palette = new UniformGrid { Columns = 5, Rows = 2 };
        _default = Swatch("默认", null);
        palette.Children.Add(_default);
        foreach (var (name, color) in Ui.TextColors) palette.Children.Add(Swatch(name, color));
        Button Swatch(string name, string? color)
        {
            var selected = color == null ? formats?.TextColorCoverage == MarkCoverage.None : current == color;
            var button = new Button
            {
                Content = new TextBlock { Text = "A", Foreground = color == null ? Ui.Ink : Brush.Parse(color), FontSize = 16, FontWeight = FontWeight.SemiBold },
                Width = 31, Height = 31, MinWidth = 0, Padding = new(0), Margin = new(0, 1), CornerRadius = new(15),
                Background = selected ? Ui.Chrome("#EAF0FA") : Brushes.Transparent,
                BorderBrush = Ui.Chrome("#8FA9CE"), BorderThickness = new(selected ? 1 : 0),
                HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            button.Classes.Add("quiet");
            AutomationProperties.SetName(button, name == "默认" ? "恢复默认文字颜色" : name + "文字");
            AutomationProperties.SetAutomationId(button, idPrefix + "TextColor_" + name);
            AutomationProperties.SetHelpText(button, selected ? "当前颜色" : "设置所选文字颜色");
            ToolTip.SetTip(button, name == "默认" ? "默认文字颜色" : name + " · " + color);
            button.Click += (_, _) => { if (!IsComposing()) apply(color); };
            return button;
        }
        Children.Add(palette);
        var custom = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 7 };
        _input = new TextBox { Text = current ?? "", Watermark = "自定义色，如 #426BB3", MinHeight = 30, FontSize = 11, VerticalContentAlignment = VerticalAlignment.Center };
        _input.Classes.Add("colorInput");
        AutomationProperties.SetName(_input, "自定义文字颜色");
        AutomationProperties.SetAutomationId(_input, idPrefix + "TextColorHex");
        var error = new TextBlock { Text = "请输入 3 位或 6 位十六进制颜色", FontSize = 11, Foreground = Ui.Chrome("#B65355"), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        AutomationProperties.SetAutomationId(error, idPrefix + "TextColorError");
        var confirm = new Button { Content = "应用", FontSize = 11, Padding = new(9, 0), MinHeight = 30 };
        confirm.Classes.Add("quiet");
        AutomationProperties.SetAutomationId(confirm, idPrefix + "TextColorApply");
        void Commit()
        {
            if (IsComposing()) return;
            var color = TextColor.Normalize(_input.Text);
            error.IsVisible = color == null;
            if (color != null) apply(color); else _input.Focus();
        }
        confirm.Click += (_, _) => Commit();
        Grid.SetColumn(confirm, 1);
        custom.Children.Add(_input); custom.Children.Add(confirm);
        Children.Add(custom); Children.Add(error);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (IsComposing()) { e.Handled = true; return; }
            if (e.Key == Key.Escape) { cancel(); e.Handled = true; }
            else if (_input.IsKeyboardFocusWithin) { Commit(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }
    private bool IsComposing() => _input?.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)) == true;
    public void FocusPalette() => _default.Focus();
}
