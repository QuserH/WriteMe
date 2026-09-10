using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace WriteMe.Desktop.Editing;

internal enum SidebarSymbol
{
    Plus, Format, Outline, Close, Search, Folder, Document, Sidebar, Text, Heading1, Heading2, Heading3,
    Toggle, BulletList, OrderedList, Task, Quote, Code, Divider,
    Bold, Italic, Underline, Strike, Link, Clear, Indent, Outdent, Expand, Collapse, Check, NoColor, Paint, Info, Image, Attachment, Star, Clock
}

// Small vector controls keep icon weight and alignment independent of Unicode fallback fonts.
internal sealed class SidebarGlyph : Control
{
    private readonly SidebarSymbol _symbol;
    private static readonly IReadOnlyDictionary<SidebarSymbol, Geometry> Paths = new Dictionary<SidebarSymbol, string>
    {
        [SidebarSymbol.Plus] = "M12 5 V19 M5 12 H19",
        [SidebarSymbol.Paint] = "M14 3 L21 10 L12 19 L5 12 Z M5 12 L2 15 L9 22 L12 19 M16 11 L21 6 Q23 4 21 2 Q19 0 17 3 L12 8",
        [SidebarSymbol.Info] = "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 10 V17 M12 6 H12.1",
        [SidebarSymbol.Image] = "M4 3 H20 Q22 3 22 5 V19 Q22 21 20 21 H4 Q2 21 2 19 V5 Q2 3 4 3 M2 17 L8 11 L13 16 L17 12 L22 17 M17 7 H17.1",
        [SidebarSymbol.Attachment] = "M8 13 L16 5 Q20 1 23 5 Q25 8 21 12 L11 22 Q7 26 3 21 Q0 17 4 13 L14 3 M7 17 L17 7",
        [SidebarSymbol.Star] = "M12 2 L15 8 L22 9 L17 14 L18 21 L12 18 L6 21 L7 14 L2 9 L9 8 Z",
        [SidebarSymbol.Clock] = "M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12 M12 6 V12 L16 15",
        [SidebarSymbol.Outline] = "M9 6 H20 M9 12 H20 M9 18 H20 M4 6 H4.1 M4 12 H4.1 M4 18 H4.1",
        [SidebarSymbol.Close] = "M6 6 L18 18 M18 6 L6 18",
        [SidebarSymbol.Check] = "M5 12 L10 17 L20 6",
        [SidebarSymbol.NoColor] = "M5 5 L19 19 M22 12 A10 10 0 1 1 2 12 A10 10 0 1 1 22 12",
        [SidebarSymbol.Search] = "M16.5 16.5 L21 21 M18 10.5 A7.5 7.5 0 1 1 3 10.5 A7.5 7.5 0 1 1 18 10.5",
        [SidebarSymbol.Folder] = "M3 7 V5 Q3 3 5 3 H10 L13 6 H20 Q22 6 22 8 V19 Q22 21 20 21 H4 Q2 21 2 19 V9 Q2 7 4 7 H21",
        [SidebarSymbol.Document] = "M5 2 H14 L21 9 V21 Q21 22 20 22 H5 Q3 22 3 20 V4 Q3 2 5 2 M14 2 V9 H21 M7 14 H17 M7 18 H15",
        [SidebarSymbol.Sidebar] = "M4 4 H20 Q22 4 22 6 V18 Q22 20 20 20 H4 Q2 20 2 18 V6 Q2 4 4 4 M9 4 V20",
        [SidebarSymbol.Text] = "M5 5 H19 M12 5 V19 M8 19 H16",
        [SidebarSymbol.Toggle] = "M8 5 L18 12 L8 19 Z",
        [SidebarSymbol.BulletList] = "M9 6 H21 M9 12 H21 M9 18 H21 M4 6 H4.1 M4 12 H4.1 M4 18 H4.1",
        [SidebarSymbol.OrderedList] = "M10 6 H21 M10 12 H21 M10 18 H21 M3 3 L5 2 V8 M3 8 H7 M3 12 Q7 10 7 13 L3 18 H7",
        [SidebarSymbol.Task] = "M5 3 H19 Q21 3 21 5 V19 Q21 21 19 21 H5 Q3 21 3 19 V5 Q3 3 5 3 M7 12 L10 15 L17 8",
        [SidebarSymbol.Quote] = "M4 6 H10 V12 H7 Q7 16 4 18 M14 6 H20 V12 H17 Q17 16 14 18",
        [SidebarSymbol.Code] = "M7 6 L2 12 L7 18 M17 6 L22 12 L17 18 M14 4 L10 20",
        [SidebarSymbol.Divider] = "M3 12 H21",
        [SidebarSymbol.Link] = "M9 14 L15 8 M9 7 L11 5 Q16 0 20 4 Q24 8 19 13 L17 15 M15 17 L13 19 Q8 24 4 20 Q0 16 5 11 L7 9",
        [SidebarSymbol.Clear] = "M3 4 H17 M10 4 V18 M6 18 H13 M16 15 L22 21 M22 15 L16 21",
        [SidebarSymbol.Indent] = "M11 5 H21 M11 10 H21 M11 15 H21 M3 20 H21 M3 8 L7 12 L3 16",
        [SidebarSymbol.Outdent] = "M11 5 H21 M11 10 H21 M11 15 H21 M3 20 H21 M7 8 L3 12 L7 16",
        [SidebarSymbol.Expand] = "M6 8 L12 2 L18 8 M6 16 L12 22 L18 16",
        [SidebarSymbol.Collapse] = "M6 2 L12 8 L18 2 M6 22 L12 16 L18 22"
    }.ToDictionary(pair => pair.Key, pair => Geometry.Parse(pair.Value));

    static SidebarGlyph() => AffectsRender<SidebarGlyph>(TextElement.ForegroundProperty);

    public SidebarGlyph(SidebarSymbol symbol, double size = 18)
    {
        _symbol = symbol;
        Width = Height = size;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        var brush = TextElement.GetForeground(this);
        using var transform = context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24));
        if (Paths.TryGetValue(_symbol, out var path))
        {
            context.DrawGeometry(null, new Pen(brush, 1.65, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), path);
            return;
        }
        var text = _symbol switch
        {
            SidebarSymbol.Format => "Aa", SidebarSymbol.Heading1 => "H1", SidebarSymbol.Heading2 => "H2", SidebarSymbol.Heading3 => "H3",
            SidebarSymbol.Bold => "B", SidebarSymbol.Italic => "I", SidebarSymbol.Underline => "U", SidebarSymbol.Strike => "S", _ => ""
        };
        var typeface = new Typeface(new FontFamily("Segoe UI, sans-serif"), _symbol == SidebarSymbol.Italic ? FontStyle.Italic : FontStyle.Normal,
            _symbol == SidebarSymbol.Bold ? FontWeight.Bold : FontWeight.Medium);
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, _symbol == SidebarSymbol.Format ? 18 : text.Length > 1 ? 16 : 20, brush);
        context.DrawText(label, new((24 - label.Width) / 2, (24 - label.Height) / 2 - .5));
        if (_symbol == SidebarSymbol.Underline) context.DrawLine(new Pen(brush, 1.2), new(5, 22), new(19, 22));
        if (_symbol == SidebarSymbol.Strike) context.DrawLine(new Pen(brush, 1.2), new(4, 12), new(20, 12));
    }
}
