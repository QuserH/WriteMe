using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WriteMe.Desktop;

public sealed class PageDivider : Control
{
    public IBrush Stroke { get; set; } = Brushes.LightGray;
    public string Variant { get; set; } = "line";
    public override void Render(DrawingContext context)
    {
        if (Variant == "none") return;
        var pen = new Pen(Stroke, 1, Variant == "dotted" ? DashStyle.Dot : null);
        context.DrawLine(pen, new(0, .5), new(Bounds.Width, .5));
    }
}
