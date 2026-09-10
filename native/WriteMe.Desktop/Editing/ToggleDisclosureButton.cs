using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 按 Craft 的矢量三角、标记列和层级间距绘制 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md
internal static class BlockLayout
{
    public const double TextInset = 48;
    public const double Indent = 28;
    public const double ToggleMarkerWidth = 28;
    public static double TextStart(BlockRow row) => TextInset + row.Depth * Indent + (row.IsToggle || row.IsTask || row.Marker.Length > 0 ? ToggleMarkerWidth : 0);
    public static double GuideX(int depth) => TextInset + 7 + depth * Indent;
}

public sealed class ToggleDisclosureButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public bool IsExpanded { get; }
    private readonly DisclosureGlyph _glyph;

    public ToggleDisclosureButton(bool expanded, Action activate)
    {
        IsExpanded = expanded;
        _glyph = new(expanded);
        Content = _glyph;
        Width = Height = 24;
        MinWidth = MinHeight = 0;
        Padding = new(0);
        Focusable = false;
        Background = Brushes.Transparent;
        BorderThickness = new(0);
        CornerRadius = new(5);
        Foreground = Ui.Chrome("#1F2225");
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Classes.Add("toggleArrow");
        var label = expanded ? "收起折叠块" : "展开折叠块";
        AutomationProperties.SetName(this, label);
        ToolTip.SetTip(this, label);
        Click += (_, _) => activate();
    }

    public void SetEmphasized(bool emphasized) => _glyph.Opacity = emphasized ? 1 : .33;

    private sealed class DisclosureGlyph(bool expanded) : Control
    {
        private static readonly Geometry Down = Geometry.Parse("M0 0 L10 0 L5 10 Z");
        static DisclosureGlyph() => AffectsRender<DisclosureGlyph>(TextElement.ForegroundProperty);
        protected override Size MeasureOverride(Size availableSize) => new(10, 10);
        public override void Render(DrawingContext context)
        {
            var brush = TextElement.GetForeground(this);
            var transform = Matrix.CreateTranslation(1.5, 1.5) * Matrix.CreateScale(10d / 13, 10d / 13);
            if (!expanded)
                transform *= Matrix.CreateTranslation(-5, -5) * Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(5, 5);
            using var state = context.PushTransform(transform);
            context.DrawGeometry(brush, new Pen(brush, 1.5, lineJoin: PenLineJoin.Round), Down);
        }
    }
}
