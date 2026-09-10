using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WriteMe.Core;

namespace WriteMe.Desktop;

public sealed class PresentationWindow : Window
{
    private readonly List<NoteNode[]> _slides = [];
    private readonly StackPanel _body = new() { Spacing = 20, Margin = new(70, 35), MaxWidth = 1100 };
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
    private readonly List<Bitmap> _images = [];
    private readonly Func<string, string?> _assetPath;
    private readonly FontFamily _font;
    private readonly ScrollViewer _scroll;
    private int _index;

    public PresentationWindow(string title, NoteNode root, PageAppearance appearance, Func<string, string?> assetPath)
    {
        _assetPath = assetPath;
        _font = MainWindow.PageFont(appearance.Font);
        Title = title + " · 演示"; Width = 1180; Height = 780; MinWidth = 640; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = appearance.Background == null ? Ui.Surface : Brush.Parse(appearance.Background);
        Foreground = MainWindow.PageInk(appearance);
        var group = new List<NoteNode>();
        foreach (var node in root.Content)
        {
            if ((node.Type == "horizontalRule" || node.Type == "heading" && node.Int("level", 1) == 1) && group.Count > 0) { _slides.Add(group.ToArray()); group.Clear(); }
            if (node.Type != "horizontalRule") group.Add(node);
        }
        if (group.Count > 0 || _slides.Count == 0) _slides.Add(group.ToArray());
        var layout = new Grid { RowDefinitions = new("60,*,60") };
        layout.Children.Add(new TextBlock { Text = title, FontSize = 17, Margin = new(34, 20), Foreground = Ui.Muted });
        _scroll = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }; Grid.SetRow(_scroll, 1); layout.Children.Add(_scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 25, HorizontalAlignment = HorizontalAlignment.Center };
        var previous = new Button { Content = "← 上一页" }; var next = new Button { Content = "下一页 →" }; var close = new Button { Content = "退出 · Esc" };
        foreach (var button in new[] { previous, next, close }) { button.Foreground = Foreground; button.Background = Brushes.Transparent; button.Classes.Add("quiet"); }
        AutomationProperties.SetAutomationId(next, "PresentationNext"); AutomationProperties.SetAutomationId(_position, "PresentationPosition");
        previous.Click += (_, _) => ShowSlide(_index - 1); next.Click += (_, _) => ShowSlide(_index + 1); close.Click += (_, _) => Close();
        footer.Children.Add(previous); footer.Children.Add(_position); footer.Children.Add(next); footer.Children.Add(close); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        Content = layout; ShowSlide(0);
        Opened += (_, _) => next.Focus();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Right or Key.PageDown or Key.Space) ShowSlide(_index + 1);
            else if (e.Key is Key.Left or Key.PageUp) ShowSlide(_index - 1);
            else if (e.Key == Key.Home) ShowSlide(0); else if (e.Key == Key.End) ShowSlide(_slides.Count - 1); else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => { foreach (var image in _images) image.Dispose(); };
    }
    private void ShowSlide(int index)
    {
        _index = Math.Clamp(index, 0, _slides.Count - 1); _body.Children.Clear(); foreach (var image in _images) image.Dispose(); _images.Clear();
        void Add(NoteNode node, int depth = 0, string marker = "")
        {
            if (node.IsTextBlock)
            {
                var text = new TextBlock { FontFamily = _font, FontSize = node.Type == "heading" ? 36 : 25, FontWeight = node.Type == "heading" ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, Margin = new(depth * 25, 0, 0, 0) };
                text.Inlines!.Add(new Run(marker));
                foreach (var part in node.Content)
                {
                    if (part.Type == "hardBreak") { text.Inlines.Add(new LineBreak()); continue; }
                    var run = new Run(part.Text.Replace('\u2028', '\n'));
                    if (part.Marks.Any(mark => mark.Type == "bold")) run.FontWeight = FontWeight.Bold;
                    if (part.Marks.Any(mark => mark.Type == "italic")) run.FontStyle = FontStyle.Italic;
                    if (part.Marks.Any(mark => mark.Type == "code")) run.FontFamily = new("Cascadia Code, Consolas, monospace");
                    var decorations = new TextDecorationCollection();
                    if (part.Marks.Any(mark => mark.Type is "underline" or "link" or "noteLink")) decorations.AddRange(TextDecorations.Underline);
                    if (part.Marks.Any(mark => mark.Type == "strike")) decorations.AddRange(TextDecorations.Strikethrough);
                    run.TextDecorations = decorations;
                    if (part.Marks.FirstOrDefault(mark => mark.Type == "textStyle")?.String("color") is { } color && TextColor.Normalize(color) is { } ink) run.Foreground = Brush.Parse(ink);
                    text.Inlines.Add(run);
                }
                if (node.Type == "codeBlock") text.FontFamily = new("Cascadia Code, Consolas, monospace");
                _body.Children.Add(text); return;
            }
            if (node.Type == "image" && node.String("assetId") is { } id && _assetPath(id) is { } path)
            {
                try { using var source = File.OpenRead(path); var bitmap = Bitmap.DecodeToWidth(source, 1000); _images.Add(bitmap); _body.Children.Add(new Image { Source = bitmap, MaxHeight = 400, Stretch = Stretch.Uniform }); }
                catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { _body.Children.Add(new TextBlock { Text = node.String("name") ?? "图片", FontSize = 24 }); }
                return;
            }
            if (node.Type is "attachment" or "image") { _body.Children.Add(new TextBlock { Text = (node.Type == "image" ? "图片 · " : "附件 · ") + (node.String("name") ?? node.String("alt") ?? "尚未下载"), FontSize = 24 }); return; }
            var number = node.Int("start", 1);
            foreach (var child in node.Content)
            {
                if (child.Type is "listItem" or "taskItem")
                {
                    var prefix = node.Type == "orderedList" ? number++ + ". " : child.Type == "taskItem" ? child.Bool("checked") ? "☑ " : "☐ " : "• ";
                    for (var i = 0; i < child.Content.Length; i++) Add(child.Content[i], depth + (i == 0 ? 0 : 1), i == 0 ? prefix : "");
                }
                else Add(child, depth + (node.Type == "toggleBlock" && child != node.Content[0] ? 1 : 0));
            }
        }
        foreach (var node in _slides[_index]) Add(node);
        _position.Text = $"{_index + 1} / {_slides.Count}";
        _scroll.Offset = default;
    }
}
