using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 原生只读预览复用实际排版；展开子树决定尺寸，收起后代不进入投影 — 见 .agents/notes/implemented/feature/2026-09-09-settings-ghost-opacity.md
internal sealed class BlockDragPreview : Decorator, IDisposable
{
    private readonly BlockEditor _preview;
    public double SourceLeft { get; }
    public double SourceTop { get; }

    public BlockDragPreview(BlockEditor owner, BlockRow source, IReadOnlyList<BlockRow> rows)
    {
        IsHitTestVisible = false; ClipToBounds = true;
        var view = owner.Surface.TextArea.TextView;
        SourceLeft = Math.Max(0, BlockLayout.TextInset - 6 + source.Depth * BlockLayout.Indent - owner.PrefixInset(source));
        var firstDocumentLine = owner.Surface.Document.GetLineByOffset(source.Start);
        var first = view.GetOrConstructVisualLine(firstDocumentLine);
        SourceTop = first.VisualTop - view.ScrollOffset.Y;
        Width = Math.Max(1, view.Bounds.Width - SourceLeft);
        var maximumPaintHeight = Math.Max(1, view.Bounds.Height);
        var height = 0d;
        var last = owner.Surface.Document.GetLineByOffset(rows[^1].End);
        for (var documentLine = firstDocumentLine; documentLine != null && documentLine.LineNumber <= last.LineNumber;)
        {
            var line = view.GetOrConstructVisualLine(documentLine);
            height += line.Height;
            documentLine = line.LastDocumentLine.NextLine;
            if (height >= maximumPaintHeight && documentLine != null && documentLine.LineNumber <= last.LineNumber)
            {
                // Offscreen lines keep the editor's cached/estimated height. The preview has a
                // viewport, so a large outline never creates a giant bitmap or thousands of controls.
                height += Math.Max(0, view.GetVisualTopByDocumentLine(last.LineNumber) + view.DefaultLineHeight - view.GetVisualTopByDocumentLine(documentLine.LineNumber));
                break;
            }
        }
        Height = height;
        var block = source.Block.Type is "listItem" or "taskItem"
            ? new NoteNode(source.IsTask ? "taskList" : source.Marker == "•" ? "bulletList" : "orderedList") { Content = [source.Block] }
            : source.Block;
        _preview = new(new(new("doc") { Content = [block], Attrs = owner.Session.HistoryOwner.Root.Attrs }), owner.IsCompact, preview: true)
        {
            PreviewInset = BlockLayout.TextInset - 6,
            PreviewDepth = source.Depth, PreviewQuote = source.Quote, IsTableCell = owner.IsTableCell,
            Width = Width, Height = Math.Min(height, maximumPaintHeight),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            PageBackgroundColor = owner.PageBackgroundColor, DividerStyle = owner.DividerStyle,
            ResolveAsset = owner.ResolveAsset, CommentAvatarFactory = owner.ResolvedCommentAvatarFactory
        };
        _preview.Surface.FontFamily = owner.Surface.FontFamily;
        _preview.Surface.FontSize = owner.Surface.FontSize;
        _preview.Surface.Foreground = owner.Surface.Foreground;
        _preview.Surface.Options.LineHeightFactor = owner.Surface.Options.LineHeightFactor;
        _preview.Surface.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _preview.Surface.Options.AllowScrollBelowDocument = false;
        _preview.SetReferenceCatalogue(owner.ReferenceDocuments, owner.ReferenceTags);
        Child = _preview;
    }

    public void Dispose() => _preview.DisposeEmbedded();
}
