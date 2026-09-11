using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

internal sealed class BlockStyleTransformer(BlockEditor owner) : DocumentColorizingTransformer
{
    protected override void ColorizeLine(DocumentLine line)
    {
        var row = owner.Session.Projection.At(line.Offset);
        if (line.Length == 0) return;
        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            var props = element.TextRunProperties;
            if (row.HeadingLevel > 0)
            {
                props.SetFontRenderingEmSize((row.HeadingLevel switch { 1 => 28, 2 => 23, _ => 20 }) * owner.Surface.FontSize / 15);
                props.SetTypeface(new(props.Typeface.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
            }
            if (row.Node.Type == "codeBlock")
            {
                props.SetTypeface(new(new FontFamily("Cascadia Code, Consolas, monospace")));
                props.SetFontRenderingEmSize(owner.Surface.FontSize);
            }
            if (row.IsAtomic) props.SetForegroundBrush(row.Node.Type == "horizontalRule" ? Brushes.Transparent : owner.PageMuted);
            if (row.Quote || owner.PreviewQuote) props.SetForegroundBrush(owner.PageColor("#626871", "#B4BECA"));
            if (row.IsTask && row.Block.Bool("checked"))
            {
                props.SetForegroundBrush(owner.PageMuted);
                props.SetTextDecorations(TextDecorations.Strikethrough);
            }
        });
        foreach (var tag in NoteReferences.InBlock(row.Node).Where(span => span.Kind == ReferenceKind.Tag))
        {
            var start = Math.Max(row.Start + tag.Start, line.Offset);
            var end = Math.Min(row.Start + tag.Start + tag.Length, line.EndOffset);
            if (end > start) ChangeLinePart(start, end, element =>
            {
                element.TextRunProperties.SetForegroundBrush(owner.PageColor("#4A70A1", "#A8C6F0"));
                element.TextRunProperties.SetBackgroundBrush(owner.PageColor("#EFF3FA", "#273D57"));
            });
        }
        var offset = row.Start;
        foreach (var run in row.Node.Content)
        {
            var start = Math.Max(offset, line.Offset);
            var end = Math.Min(offset + RichText.Length(run), line.EndOffset);
            if (end > start && !run.Marks.IsEmpty)
                ChangeLinePart(start, end, element =>
                {
                    var p = element.TextRunProperties;
                    var bold = run.Marks.Any(m => m.Type == "bold");
                    var italic = run.Marks.Any(m => m.Type == "italic");
                    p.SetTypeface(new(p.Typeface.FontFamily, italic ? FontStyle.Italic : p.Typeface.Style, bold ? FontWeight.Bold : p.Typeface.Weight));
                    foreach (var mark in run.Marks)
                        switch (mark.Type)
                        {
                            case "code":
                                p.SetTypeface(new(new FontFamily("Cascadia Code, Consolas, monospace"), p.Typeface.Style, p.Typeface.Weight));
                                p.SetBackgroundBrush(owner.PageColor("#F0EFEA", "#353D48"));
                                p.SetFontRenderingEmSize(owner.Surface.FontSize);
                                break;
                            case "link":
                                p.SetForegroundBrush(owner.PageColor("#3C7298", "#9BC8E8"));
                                break;
                            case "noteLink":
                                p.SetForegroundBrush(owner.IsKnownReference(mark.String("documentId") ?? "") ? owner.PageColor("#4A70A1", "#A8C6F0") : owner.PageColor("#A27355", "#D3A886"));
                                break;
                            case "highlight":
                                if (Color.TryParse(mark.String("color") ?? "#FFF0A8", out var color))
                                {
                                    p.SetBackgroundBrush(new SolidColorBrush(color));
                                    p.SetForegroundBrush(color.R * .299 + color.G * .587 + color.B * .114 < 140 ? Brushes.White : Brush.Parse("#1F2225"));
                                }
                                break;
                        }
                    var decorations = new TextDecorationCollection();
                    if (TextColor.Read(run.Marks) is { } textColor) p.SetForegroundBrush(Brush.Parse(textColor));
                    if (run.Marks.Any(mark => mark.Type is "underline" or "link" or "noteLink"))
                        foreach (var decoration in TextDecorations.Underline) decorations.Add(decoration);
                    if (run.Marks.Any(mark => mark.Type == "strike") || row.IsTask && row.Block.Bool("checked"))
                        foreach (var decoration in TextDecorations.Strikethrough) decorations.Add(decoration);
                    if (decorations.Count > 0) p.SetTextDecorations(decorations);
                });
            offset += RichText.Length(run);
        }
    }
}

internal sealed class BlockPrefixGenerator(BlockEditor owner) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var first = CurrentContext.VisualLine.FirstDocumentLine.Offset;
        return startOffset <= first ? first : -1;
    }
    public override VisualLineElement ConstructElement(int offset)
    {
        var row = owner.Session.Projection.At(offset);
        var inset = owner.PrefixInset(row);
        var panel = new Canvas { Width = owner.TextStart(row), Height = owner.IsTableCell ? 26 : row.IsToggle || row.Depth + owner.PreviewDepth > 0 ? 28 : 36, Background = Brushes.Transparent };
        TextBlock.SetBaselineOffset(panel, 17);
        if (owner.IsTableCell && panel.Width == 0) return new PrefixElement(panel);
        var grip = Ui.Button("⠿", "拖动块；单击打开块菜单", () => { }, 22);
        grip.FontSize = 16;
        grip.MinWidth = 0;
        grip.Height = 24;
        grip.Padding = new(0);
        grip.Foreground = owner.PageColor("#A7AFB7", "#84909F");
        grip.Classes.Add("blockGrip");
        grip.Tag = row.Block.Id;
        grip.Opacity = owner.HoveredBlockId == row.Block.Id ? 1 : 0;
        grip.IsHitTestVisible = grip.Opacity > 0;
        Canvas.SetLeft(grip, BlockLayout.TextInset - 32 + row.Depth * BlockLayout.Indent - inset);
        Canvas.SetTop(grip, 1);
        grip.AddHandler(InputElement.PointerPressedEvent, (_, e) => owner.BeginBlockDrag(row, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        if (!owner.IsDragPreview) panel.Children.Add(grip);
        if (row.IsToggle)
        {
            var arrow = new ToggleDisclosureButton(row.IsExpanded, () => owner.Fold(row.Block.Id));
            arrow.Foreground = owner.Surface.Foreground;
            arrow.Tag = row.Block.Id;
            arrow.SetEmphasized(owner.HoveredBlockId == row.Block.Id || owner.Session.Selection.Caret.NodeId == row.Node.Id);
            AutomationProperties.SetHelpText(arrow, row.Text);
            Canvas.SetLeft(arrow, BlockLayout.GuideX(row.Depth) - 12 - inset);
            Canvas.SetTop(arrow, 0);
            panel.Children.Add(arrow);
        }
        else if (row.IsTask)
        {
            var check = Ui.Button(row.Block.Bool("checked") ? "☑" : "☐", "切换待办状态", () => owner.Session.ToggleTask(row.Block.Id), 24);
            check.FontSize = 17;
            check.MinWidth = 0;
            check.Height = 24;
            check.Padding = new(0);
            check.Foreground = owner.PageMuted;
            Canvas.SetLeft(check, BlockLayout.GuideX(row.Depth) - 12 - inset);
            panel.Children.Add(check);
        }
        else if (row.Marker.Length > 0)
        {
            var marker = new TextBlock { Text = row.Marker, FontSize = 15, Width = 24, TextAlignment = TextAlignment.Center, Foreground = owner.PageMuted, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Canvas.SetLeft(marker, BlockLayout.GuideX(row.Depth) - 12 - inset);
            Canvas.SetTop(marker, 4);
            panel.Children.Add(marker);
        }
        return new PrefixElement(panel);
    }

    internal sealed class PrefixElement(Control element) : InlineObjectElement(0, element)
    {
        public double TextInset => Element.Width;
        public override bool IsWhitespace(int visualColumn) => true;
        public override bool HandlesLineBorders => true;
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode)
            => direction == LogicalDirection.Forward && visualColumn < VisualColumn + VisualLength || direction == LogicalDirection.Backward && visualColumn > VisualColumn + VisualLength
                ? VisualColumn + VisualLength : -1;
    }
}

internal sealed class BlockBackgroundRenderer(BlockEditor owner) : IBackgroundRenderer
{
    public KnownLayer Layer => KnownLayer.Background;
    public void Draw(TextView textView, DrawingContext context)
    {
        if (!textView.VisualLinesValid) return;
        foreach (var line in textView.VisualLines)
        {
            var row = owner.Session.Projection.At(line.FirstDocumentLine.Offset);
            var y = line.VisualTop - textView.ScrollOffset.Y;
            var inset = owner.PrefixInset(row);
            var x = owner.TextStart(row);
            if (row.IsToggle && owner.HoveredBlockId == row.Block.Id && owner.DropTarget == null)
            {
                var left = BlockLayout.TextInset - 6 + row.Depth * BlockLayout.Indent - inset;
                context.DrawRectangle(owner.PageColor("#F5F7FC", "#2D3949"), null, new Rect(left, y + 2, Math.Max(1, textView.Bounds.Width - left), line.Height - 4), 4, 4);
            }
            DrawRow(owner, context, row, y, line.Height, textView.Bounds.Width);
            if (owner.DropTarget is { } drop && drop.IndicatorNode == row.Node.Id)
            {
                var brush = Ui.Accent;
                var targetX = Math.Max(4, BlockLayout.TextInset + drop.Depth * BlockLayout.Indent - inset);
                if (drop.Placement == DropPlacement.Inside)
                    context.DrawRectangle(owner.PageColor("#EDF3FC", "#293F5C"), new Pen(brush, 1), new Rect(x - 6, y, Math.Max(1, textView.Bounds.Width - x), line.Height), 7, 7);
                var lineY = drop.Placement == DropPlacement.Before ? y : y + line.Height;
                context.DrawLine(new Pen(brush, 2, lineCap: PenLineCap.Round), new(targetX, lineY), new(textView.Bounds.Width - 6, lineY));
                context.DrawEllipse(owner.PageBackgroundColor is { } color ? new SolidColorBrush(color) : Ui.Surface, new Pen(brush, 1.5), new Point(targetX, lineY), 3, 3);
            }
        }
    }

    private static void DrawRow(BlockEditor owner, DrawingContext context, BlockRow row, double y, double height, double width)
    {
        var inset = owner.PrefixInset(row);
        var x = owner.TextStart(row);
        if (row.Node.Type == "codeBlock") context.DrawRectangle(owner.PageColor("#F7F6F3", "#303843"), null, new Rect(x - 4, y, Math.Max(1, width - x), height), 7, 7);
        if (row.Node.Type == "horizontalRule" && owner.DividerStyle != "none") context.DrawLine(new Pen(owner.PageLine, 1, owner.DividerStyle == "dotted" ? DashStyle.Dot : null), new(x, y + height / 2), new(width - 5, y + height / 2));
        if (row.Quote || owner.PreviewQuote) context.DrawLine(new Pen(owner.PageLine, 2), new(x - 9, y + 2), new(x - 9, y + height - 2));
        foreach (var depth in row.GuideDepths)
        {
            var continues = row.Index + 1 < owner.Session.Projection.Rows.Length && owner.Session.Projection.Rows[row.Index + 1].GuideDepths.Contains(depth);
            context.DrawLine(new Pen(owner.PageLine, 1.5), new(BlockLayout.GuideX(depth) - inset, y), new(BlockLayout.GuideX(depth) - inset, y + height - (continues ? 0 : 4)));
        }
        if (row.IsExpanded && height > 25)
            context.DrawLine(new Pen(owner.PageLine, 1.5), new(BlockLayout.GuideX(row.Depth) - inset, y + 25), new(BlockLayout.GuideX(row.Depth) - inset, y + height));
    }
}
