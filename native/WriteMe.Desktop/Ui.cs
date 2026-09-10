using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace WriteMe.Desktop;

internal static partial class Ui
{
    private static readonly Dictionary<string, SolidColorBrush> Palette = [];
    private static bool _dark;
    public static readonly SolidColorBrush Ink = Chrome("#1F2225");
    public static readonly SolidColorBrush Muted = Chrome("#92979E");
    public static readonly SolidColorBrush Line = Chrome("#ECEEF0");
    public static readonly SolidColorBrush Surface = Chrome("#FFFFFF");
    public static readonly SolidColorBrush Shell = Chrome("#FBFCFD");
    public static readonly SolidColorBrush Subtle = Chrome("#F0F2F5");
    public static readonly SolidColorBrush Accent = Chrome("#4A78C5");

    public static SolidColorBrush Chrome(string value)
    {
        value = value.ToUpperInvariant();
        if (Palette.TryGetValue(value, out var brush)) return brush;
        brush = new(ThemeColor(value)); Palette.Add(value, brush);
        return brush;
    }
    private static Color ThemeColor(string value)
    {
        var color = Color.Parse(value);
        if (!_dark) return color;
        var maximum = Math.Max(color.R, Math.Max(color.G, color.B)); var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
        if (value == "#FFFFFF") return Color.Parse("#252A31");
        if (value is "#FBFCFD" or "#FCFDFE" or "#F4F5F7") return Color.Parse("#1B2027");
        if (maximum - minimum > 24) return Color.Parse(minimum > 165 ? "#30435F" : "#94B7EF");
        if (minimum > 220 && minimum < 245) return Color.Parse("#39414B");
        if (minimum >= 200) return Color.Parse("#303740");
        return Color.Parse(maximum < 125 ? "#E0E5EC" : "#A3ADB9");
    }
    public static void InitializePalette(Application application)
    {
        foreach (var value in MarkupColors) application.Resources["Chrome" + value[1..]] = Chrome(value);
        application.PropertyChanged += (_, args) =>
        {
            if (args.Property == Application.ActualThemeVariantProperty) ApplyDarkTheme(application.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);
        };
    }
    public static void ApplyDarkTheme(bool dark)
    {
        _dark = dark;
        foreach (var (value, brush) in Palette) brush.Color = ThemeColor(value);
    }
    public static readonly (string Name, string Color)[] HighlightColors =
        [("淡黄", "#FFF0A8"), ("浅绿", "#D9EED1"), ("浅蓝", "#D5E9F7"), ("浅紫", "#E6DAF1"), ("浅粉", "#F7DDE5")];
    public static readonly (string Name, string Color)[] TextColors =
        [("灰色", "#757A83"), ("红色", "#B94D55"), ("橙色", "#A66B2C"), ("黄色", "#89732C"),
         ("绿色", "#3D7C54"), ("青色", "#377B87"), ("蓝色", "#426BB3"), ("紫色", "#7C56A6"), ("粉色", "#AC527D")];
    public static Button Button(string text, string label, Action action, double? width = null)
    {
        var button = new Button { Content = text, Focusable = false, FontSize = 14, MinWidth = 29, Height = 29, Padding = new Thickness(7, 3) };
        if (width != null) button.Width = width.Value;
        button.Classes.Add("quiet");
        ToolTip.SetTip(button, label);
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action();
        return button;
    }
}
