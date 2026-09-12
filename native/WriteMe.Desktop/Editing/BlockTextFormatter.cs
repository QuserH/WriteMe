using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace WriteMe.Desktop.Editing;

// Note: 折叠长标题的续行、选区和命中统一缩进 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md
// Note: 段落评论页脚追加在原有行盒之后，绘制、基线与选区保持一致 — 见 .agents/notes/implemented/feature/2026-09-11-native-comments.md
internal sealed class BlockTextFormatter(TextFormatter inner, BlockEditor owner) : TextFormatter
{
    public static void Attach(TextView view, BlockEditor owner)
    {
        Install();
        view.DocumentChanged += (_, _) => Install();
        void Install()
        {
            ref var formatter = ref Formatter(view);
            if (formatter is not null and not BlockTextFormatter)
                formatter = new BlockTextFormatter(formatter, owner);
        }
    }

    public override TextLine? FormatLine(ITextSource textSource, int firstTextSourceIndex, double paragraphWidth,
        TextParagraphProperties paragraphProperties, TextLineBreak? previousLineBreak = null)
    {
        if (textSource is ITextRunConstructionContext source)
        {
            var row = owner.Session.Projection.At(source.VisualLine.FirstDocumentLine.Offset);
            var alignment = row.Node.String("textAlign") switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
            if (alignment != TextAlignment.Left && !row.IsAtomic) paragraphProperties = new AlignedProperties(paragraphProperties, alignment);
        }
        var inset = firstTextSourceIndex > 0 && textSource is ITextRunConstructionContext context &&
            context.VisualLine.Elements.FirstOrDefault() is BlockPrefixGenerator.PrefixElement prefix
            ? prefix.TextInset : 0;
        var line = inner.FormatLine(textSource, firstTextSourceIndex, Math.Max(1, paragraphWidth - inset),
            paragraphProperties, previousLineBreak);
        var footer = 0d;
        if (line != null && textSource is ITextRunConstructionContext paragraph && line.FirstTextSourceIndex + line.Length >= paragraph.VisualLine.VisualLength)
        {
            var row = owner.Session.Projection.At(paragraph.VisualLine.FirstDocumentLine.Offset);
            if (paragraph.VisualLine.LastDocumentLine.EndOffset >= row.End) footer = owner.CommentFooterHeight(row.Node.Id);
        }
        return line is not null && (inset > 0 || footer > 0) ? new IndentedLine(line, inset, footer, owner.Surface.TextArea.TextView.DefaultLineHeight) : line;
    }

    private sealed class AlignedProperties(TextParagraphProperties source, TextAlignment alignment) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => source.FlowDirection;
        public override TextAlignment TextAlignment => alignment;
        public override double LineHeight => source.LineHeight;
        public override bool FirstLineInParagraph => source.FirstLineInParagraph;
        public override TextRunProperties DefaultTextRunProperties => source.DefaultTextRunProperties;
        public override TextWrapping TextWrapping => source.TextWrapping;
        public override double Indent => source.Indent;
        public override double DefaultIncrementalTab => source.DefaultIncrementalTab;
    }

    // AvaloniaEdit 11.4.1 never initializes its firstLineInParagraph flag, and Avalonia 11.3.21
    // does not implement TextParagraphProperties.Indent. Keep the compatibility seam local to
    // this TextView: no global formatter, source text changes, or additional shaping pass.
    // These signatures are tied to the pinned packages and exercised by native layout tests.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_formatter")]
    private static extern ref TextFormatter? Formatter(TextView view);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern TextBounds CreateBounds(Rect bounds, FlowDirection flowDirection, IList<TextRunBounds> runBounds);

    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    private static extern TextRunBounds CreateRunBounds(Rect bounds, int firstCharacterIndex, int length, TextRun textRun);

    private sealed class IndentedLine(TextLine line, double inset, double footer = 0, double minimumHeight = 0) : TextLine
    {
        // AvaloniaEdit centers short text lines inside DefaultLineHeight. Wrapping only Height
        // consumes that centering space, moving the glyphs/caret and shortening the footer.
        private double TopInset => footer > 0 ? Math.Max(0, minimumHeight - line.Height) / 2 : 0;
        public override IReadOnlyList<TextRun> TextRuns => line.TextRuns;
        public override int FirstTextSourceIndex => line.FirstTextSourceIndex;
        public override int Length => line.Length;
        public override TextLineBreak? TextLineBreak => line.TextLineBreak;
        public override double Baseline => line.Baseline + TopInset;
        public override double Extent => line.Extent;
        public override bool HasCollapsed => line.HasCollapsed;
        public override bool HasOverflowed => line.HasOverflowed;
        public override double Height => (footer > 0 ? Math.Max(line.Height, minimumHeight) : line.Height) + footer;
        public override int NewLineLength => line.NewLineLength;
        public override double OverhangAfter => line.OverhangAfter;
        public override double OverhangLeading => line.OverhangLeading;
        public override double OverhangTrailing => line.OverhangTrailing;
        public override double Start => line.Start + inset;
        // AvaloniaEdit uses these as right-edge coordinates for hit testing and selections.
        public override double Width => line.Width + inset;
        public override double WidthIncludingTrailingWhitespace => line.WidthIncludingTrailingWhitespace + inset;
        public override int TrailingWhitespaceLength => line.TrailingWhitespaceLength;

        public override void Draw(DrawingContext drawingContext, Point lineOrigin)
            => line.Draw(drawingContext, lineOrigin + new Vector(inset, TopInset));

        public override TextLine Collapse(params TextCollapsingProperties?[] collapsingPropertiesList)
        {
            var collapsed = line.Collapse(collapsingPropertiesList);
            return ReferenceEquals(collapsed, line) ? this : new IndentedLine(collapsed, inset, footer, minimumHeight);
        }

        public override void Justify(JustificationProperties justificationProperties) => line.Justify(justificationProperties);
        public override CharacterHit GetCharacterHitFromDistance(double distance) => line.GetCharacterHitFromDistance(distance - inset);
        public override double GetDistanceFromCharacterHit(CharacterHit characterHit) => line.GetDistanceFromCharacterHit(characterHit) + inset;
        public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit) => line.GetNextCaretCharacterHit(characterHit);
        public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit) => line.GetPreviousCaretCharacterHit(characterHit);
        public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) => line.GetBackspaceCaretCharacterHit(characterHit);

        public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength)
            => line.GetTextBounds(firstTextSourceCharacterIndex, textLength).Select(bounds =>
                CreateBounds(bounds.Rectangle.Translate(new(inset, TopInset)), bounds.FlowDirection,
                    bounds.TextRunBounds.Select(run => CreateRunBounds(run.Rectangle.Translate(new(inset, TopInset)),
                        run.TextSourceCharacterIndex, run.Length, run.TextRun)).ToArray())).ToArray();

        public override void Dispose() => line.Dispose();
    }
}
