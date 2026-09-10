using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;

namespace WriteMe.Desktop.Editing;

// Note: 折叠长标题的续行、选区和命中统一缩进 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md
internal sealed class BlockTextFormatter(TextFormatter inner) : TextFormatter
{
    public static void Attach(TextView view)
    {
        Install();
        view.DocumentChanged += (_, _) => Install();
        void Install()
        {
            ref var formatter = ref Formatter(view);
            if (formatter is not null and not BlockTextFormatter)
                formatter = new BlockTextFormatter(formatter);
        }
    }

    public override TextLine? FormatLine(ITextSource textSource, int firstTextSourceIndex, double paragraphWidth,
        TextParagraphProperties paragraphProperties, TextLineBreak? previousLineBreak = null)
    {
        var inset = firstTextSourceIndex > 0 && textSource is ITextRunConstructionContext context &&
            context.VisualLine.Elements.FirstOrDefault() is BlockPrefixGenerator.PrefixElement prefix
            ? prefix.TextInset : 0;
        var line = inner.FormatLine(textSource, firstTextSourceIndex, Math.Max(1, paragraphWidth - inset),
            paragraphProperties, previousLineBreak);
        return line is not null && inset > 0 ? new IndentedLine(line, inset) : line;
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

    private sealed class IndentedLine(TextLine line, double inset) : TextLine
    {
        public override IReadOnlyList<TextRun> TextRuns => line.TextRuns;
        public override int FirstTextSourceIndex => line.FirstTextSourceIndex;
        public override int Length => line.Length;
        public override TextLineBreak? TextLineBreak => line.TextLineBreak;
        public override double Baseline => line.Baseline;
        public override double Extent => line.Extent;
        public override bool HasCollapsed => line.HasCollapsed;
        public override bool HasOverflowed => line.HasOverflowed;
        public override double Height => line.Height;
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
            => line.Draw(drawingContext, lineOrigin + new Vector(inset, 0));

        public override TextLine Collapse(params TextCollapsingProperties?[] collapsingPropertiesList)
        {
            var collapsed = line.Collapse(collapsingPropertiesList);
            return ReferenceEquals(collapsed, line) ? this : new IndentedLine(collapsed, inset);
        }

        public override void Justify(JustificationProperties justificationProperties) => line.Justify(justificationProperties);
        public override CharacterHit GetCharacterHitFromDistance(double distance) => line.GetCharacterHitFromDistance(distance - inset);
        public override double GetDistanceFromCharacterHit(CharacterHit characterHit) => line.GetDistanceFromCharacterHit(characterHit) + inset;
        public override CharacterHit GetNextCaretCharacterHit(CharacterHit characterHit) => line.GetNextCaretCharacterHit(characterHit);
        public override CharacterHit GetPreviousCaretCharacterHit(CharacterHit characterHit) => line.GetPreviousCaretCharacterHit(characterHit);
        public override CharacterHit GetBackspaceCaretCharacterHit(CharacterHit characterHit) => line.GetBackspaceCaretCharacterHit(characterHit);

        public override IReadOnlyList<TextBounds> GetTextBounds(int firstTextSourceCharacterIndex, int textLength)
            => line.GetTextBounds(firstTextSourceCharacterIndex, textLength).Select(bounds =>
                CreateBounds(bounds.Rectangle.Translate(new(inset, 0)), bounds.FlowDirection,
                    bounds.TextRunBounds.Select(run => CreateRunBounds(run.Rectangle.Translate(new(inset, 0)),
                        run.TextSourceCharacterIndex, run.Length, run.TextRun)).ToArray())).ToArray();

        public override void Dispose() => line.Dispose();
    }
}
