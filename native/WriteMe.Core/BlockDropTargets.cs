namespace WriteMe.Core;

public sealed record BlockDropTarget(Guid Target, DropPlacement Placement, Guid IndicatorNode, int Depth);

public sealed partial class DocumentSession
{
    // Note: 落点按相邻可见块的共同边界解析，显式左拖保留子树退出 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md
    public BlockDropTarget? ResolveBlockDrop(Guid sourceId, int rowIndex, bool after, int depth, bool inside = false)
    {
        if (rowIndex < 0 || rowIndex >= Projection.Rows.Length) return null;
        var source = NoteTree.Find(Root, sourceId);
        var sourceRow = Projection.Rows.FirstOrDefault(candidate => candidate.Block.Id == sourceId);
        if (source == null || sourceRow == null) return null;
        var row = Projection.Rows[rowIndex];
        depth = Math.Max(0, depth);

        BlockDropTarget? Valid(BlockDropTarget? target) => target != null && CanMove(sourceId, target.Target, target.Placement) ? target : null;
        // The source still occupies its original footprint. A horizontal drag at its header
        // can outdent it or use the preceding gap to indent; its own children remain illegal.
        var overSource = NoteTree.Find(source, row.Node.Id) != null;
        if (overSource && depth < sourceRow.Depth) return Valid(OutdentDrop(sourceRow, depth));
        if (!overSource && inside && row.IsToggle && depth > row.Depth)
            return Valid(new(row.Block.Id, DropPlacement.Inside, row.Node.Id, row.Depth + 1));

        var gap = rowIndex + (after ? 1 : 0);
        var previous = gap > 0 ? Projection.Rows[gap - 1] : null;
        var next = gap < Projection.Rows.Length ? Projection.Rows[gap] : null;
        var targets = new List<BlockDropTarget>();
        if (next != null)
            targets.Add(new(next.Block.Id, DropPlacement.Before, next.Node.Id, next.Depth));
        if (previous != null)
        {
            if (previous.IsToggle)
                targets.Add(new(previous.Block.Id, DropPlacement.Inside, previous.Node.Id, previous.Depth + 1));
            var sibling = previous;
            // An expanded header's bottom is its first-child boundary, not the end of
            // its whole subtree. Only ancestors ending at this gap offer an after slot.
            while (LastVisibleRow(sibling.Block)?.Index == previous.Index)
            {
                targets.Add(new(sibling.Block.Id, DropPlacement.After, previous.Node.Id, sibling.Depth));
                if (NoteTree.Parent(Root, sibling.Block.Id) is not { Type: "toggleBlock" } parent
                    || Projection.Find(parent.Content[0].Id) is not { } parentRow) break;
                sibling = parentRow;
            }
        }
        if (targets.Count == 0) return null;

        // A deliberate left drag can leave the hovered subtree even before its last row.
        // With vertical movement alone, use the levels actually offered by this gap.
        if (depth < sourceRow.Depth && targets.All(target => target.Depth > depth)
            && OutdentDrop(next ?? previous!, depth) is { } outdent) return Valid(outdent);
        var closestDepth = targets.MinBy(target => Math.Abs(target.Depth - depth))!.Depth;
        // Filter no-ops only after choosing a level. Falling back to another level would
        // unexpectedly nest a block when hovering its unchanged position.
        return targets.Where(target => target.Depth == closestDepth).Select(Valid).FirstOrDefault(target => target != null);
    }

    private BlockDropTarget? OutdentDrop(BlockRow row, int depth)
    {
        var originalDepth = row.Depth;
        while (row.Depth > depth)
        {
            if (NoteTree.Parent(Root, row.Block.Id) is not { Type: "toggleBlock" } parent
                || Projection.Find(parent.Content[0].Id) is not { } parentRow) break;
            row = parentRow;
        }
        return row.Depth < originalDepth && LastVisibleRow(row.Block) is { } last
            ? new(row.Block.Id, DropPlacement.After, last.Node.Id, row.Depth) : null;
    }

    private BlockRow? LastVisibleRow(NoteNode node)
    {
        if (Projection.Find(node.Id) is { } row) return row;
        if (node.Type == "toggleBlock" && node.Bool("collapsed")) return Projection.Find(node.Content[0].Id);
        for (var index = node.Content.Length - 1; index >= 0; index--)
            if (LastVisibleRow(node.Content[index]) is { } last) return last;
        return null;
    }
}
