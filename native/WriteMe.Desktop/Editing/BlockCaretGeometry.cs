using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;

namespace WriteMe.Desktop.Editing;

// Note: 光标保留原生闪烁，只按文字度量裁掉块留白；IME 共用坐标 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
internal static class BlockCaretGeometry
{
    public static void Attach(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        // The pinned AvaloniaEdit exposes its layer collection, but not the caret layer type.
        // Use the existing layer's public Clip instead of replacing its blink/focus lifecycle.
        var layer = view.Layers.Single(control => control.GetType().FullName == "AvaloniaEdit.Editing.CaretLayer");
        var clip = new RectangleGeometry();
        layer.Clip = clip;
        var updating = false;
        void Update()
        {
            if (updating) return;
            updating = true;
            try
            {
                var rectangle = GetRectangle(editor);
                clip.Rect = view.VisualLinesValid ? new(0, rectangle.Y, view.Bounds.Width, rectangle.Height) : default;
            }
            finally { updating = false; }
        }
        editor.TextArea.Caret.PositionChanged += (_, _) => Update();
        view.VisualLinesChanged += (_, _) => Update();
        view.ScrollOffsetChanged += (_, _) => Update();
        view.SizeChanged += (_, _) => Update();
    }

    public static Rect GetRectangle(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        if (!view.VisualLinesValid) return new(0, 0, 1, editor.FontSize);
        var position = editor.TextArea.Caret.Position;
        var line = view.GetVisualLine(position.Line);
        if (line == null) return new(0, 0, 1, editor.FontSize);
        var textLine = line.GetTextLine(position.VisualColumn, position.IsAtEndOfLine);
        var properties = GetTextProperties(editor);
        var typeface = properties?.Typeface ?? new Typeface(editor.FontFamily);
        var fontSize = properties?.FontRenderingEmSize ?? editor.FontSize;
        var metrics = typeface.GlyphTypeface.Metrics;
        var scale = fontSize / metrics.DesignEmHeight;
        var baseline = line.GetTextLineVisualYPosition(textLine, VisualYPosition.Baseline);
        var top = Math.Max(line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop), baseline - Math.Abs(metrics.Ascent) * scale);
        var bottom = Math.Min(line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineBottom), baseline + Math.Abs(metrics.Descent) * scale);
        var pixelScale = TopLevel.GetTopLevel(view)?.RenderScaling ?? 1;
        top = Math.Round((top - view.VerticalOffset) * pixelScale) / pixelScale;
        bottom = Math.Round((bottom - view.VerticalOffset) * pixelScale) / pixelScale;
        return new(line.GetTextLineVisualXPosition(textLine, position.VisualColumn) - view.HorizontalOffset, top,
            1 / pixelScale, Math.Max(1 / pixelScale, bottom - top));
    }

    public static TextRunProperties? GetTextProperties(TextEditor editor)
    {
        var view = editor.TextArea.TextView;
        if (!view.VisualLinesValid || view.GetVisualLine(editor.TextArea.Caret.Line) is not { } line) return null;
        var offset = editor.CaretOffset - line.FirstDocumentLine.Offset;
        return line.Elements.LastOrDefault(element => element.DocumentLength > 0 && element.RelativeTextOffset <= offset)?.TextRunProperties;
    }
}
