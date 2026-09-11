using System.Collections.Immutable;

namespace WriteMe.Core;

// Note: 区域共用前后选区快照，避免重做被后续导航改写 — 见 .agents/notes/implemented/feature/2026-09-11-native-tables-columns-and-editing.md
public sealed partial class DocumentSession
{
    private sealed record Snapshot(NoteNode Root, EditorSelection Selection);
    private sealed record HistoryEntry(Snapshot Before, Snapshot After);
    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private string? _lastGroup;
    private long _lastEdit;
    private bool _batch;
    private bool _isReadOnly;
    private readonly Dictionary<Guid, bool> _viewCollapsed = [];
    private long _viewRevision;
    public bool IsReadOnly
    {
        get => _scopeOwner?.IsReadOnly ?? _isReadOnly;
        set
        {
            if (_scopeOwner != null) { _scopeOwner.IsReadOnly = value; return; }
            if (_isReadOnly == value) return;
            _isReadOnly = value; _viewCollapsed.Clear(); _viewRevision++;
            Projection = Project(); Selection = ResolveSelection(Selection);
            BreakTypingGroup(); Revision++; Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public NoteNode Root { get; private set; }
    public DocumentProjection Projection { get; private set; }
    public EditorSelection Selection
    {
        get => _selection;
        set { _selection = value; if (!_batch && _scopeOwner != null && IsScopeAttached) _scopeOwner.Selection = value; }
    }
    public long Revision { get; private set; }
    public bool CanUndo => !IsReadOnly && (_scopeOwner?.CanUndo ?? _sharedHistory?.CanUndo ?? _undo.Count > 0);
    public bool CanRedo => !IsReadOnly && (_scopeOwner?.CanRedo ?? _sharedHistory?.CanRedo ?? _redo.Count > 0);
    public ImmutableArray<NoteMark>? TypingMarks { get; private set; }
    public event EventHandler? Changed;

    private DocumentProjection Project() => new(Root, HistoryOwner._viewCollapsed);
    private bool IsCollapsed(NoteNode node) => HistoryOwner._viewCollapsed.GetValueOrDefault(node.Id, node.Bool("collapsed"));
    private void ViewFolds(IEnumerable<(Guid Id, bool Collapsed)> changes, EditorSelection? selection = null)
    {
        var owner = HistoryOwner;
        foreach (var (id, collapsed) in changes) owner._viewCollapsed[id] = collapsed;
        owner._viewRevision++; owner.Projection = owner.Project();
        owner.Selection = owner.ResolveSelection(selection ?? owner.Selection);
        owner.Revision++; owner.Changed?.Invoke(owner, EventArgs.Empty);
    }

    public DocumentSession(NoteNode root)
    {
        Root = NoteTree.Normalize(root);
        Projection = Project();
        Selection = EditorSelection.At(Projection.Rows[0].Node.Id);
    }

    private void Commit(NoteNode root, EditorSelection? selection = null, string? group = null)
    {
        if (IsReadOnly) return;
        if (ReferenceEquals(root, Root)) return;
        if (_batch)
        {
            Root = NoteTree.Normalize(root);
            Projection = Project();
            Selection = ResolveSelection(selection ?? Selection);
            return;
        }
        if (_scopeOwner != null) { CommitScope(root, selection ?? Selection, group); return; }
        var now = Environment.TickCount64;
        var separate = !CanUndo || group == null || group != _lastGroup || now - _lastEdit > 750;
        var before = new Snapshot(Root, Selection);
        _lastGroup = group;
        _lastEdit = now;
        _redo.Clear();
        Root = NoteTree.Normalize(root);
        Projection = Project();
        Selection = ResolveSelection(selection ?? Selection);
        // Keep both transaction selections: later caret navigation must not rewrite where
        // redo resumes typing, especially when the two points are in different cell scopes.
        var after = new Snapshot(Root, Selection);
        if (_sharedHistory != null) _sharedHistory.Record(Root, separate);
        else if (separate)
        {
            _undo.Add(new(before, after));
            if (_undo.Count > 200) _undo.RemoveAt(0);
        }
        else _undo[^1] = _undo[^1] with { After = after };
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private EditorSelection ResolveSelection(EditorSelection selection)
    {
        TextPoint Resolve(TextPoint point)
        {
            if (Projection.Find(point.NodeId) is { } row) return point with { Offset = Math.Clamp(point.Offset, 0, row.Text.Length) };
            if (Projection.LayoutHost(point.NodeId) is { } host && NoteTree.Find(Root, point.NodeId) is { } nested)
            {
                var resolved = point with { Offset = Math.Clamp(point.Offset, 0, nested.IsTextBlock ? RichText.Plain(nested).Length : 1) };
                var parent = NoteTree.Parent(Root, point.NodeId);
                while (parent != null && parent.Id != host.Node.Id)
                {
                    if (parent.Type == "toggleBlock" && IsCollapsed(parent) && parent.Content[0].Id != resolved.NodeId)
                        resolved = new(parent.Content[0].Id, RichText.Plain(parent.Content[0]).Length);
                    parent = NoteTree.Parent(Root, parent.Id);
                }
                return resolved;
            }
            var ancestor = NoteTree.Parent(Root, point.NodeId);
            while (ancestor != null)
            {
                var visible = Projection.Rows.FirstOrDefault(r => r.Block.Id == ancestor.Id);
                if (visible != null) return new(visible.Node.Id, visible.Text.Length);
                ancestor = NoteTree.Parent(Root, ancestor.Id);
            }
            return new(Projection.Rows[0].Node.Id, 0);
        }
        return new(Resolve(selection.Anchor), Resolve(selection.Caret));
    }

    public void BreakTypingGroup() { _lastGroup = null; TypingMarks = null; _sharedHistory?.BreakGroup(); _scopeOwner?.BreakTypingGroup(); }

    public void ReplaceSelectionWithEnter(int start, int length, bool sibling = false, bool softBreak = false)
    {
        var originalRoot = Root;
        var originalProjection = Projection;
        var originalSelection = Selection;
        NoteNode result;
        EditorSelection selection;
        _batch = true;
        try
        {
            if (length > 0) Edit(start, length, "", false);
            Enter(length > 0 ? Projection.Offset(Selection.Caret) : start, sibling, softBreak);
            result = Root;
            selection = Selection;
        }
        finally
        {
            _batch = false;
            Root = originalRoot;
            Projection = originalProjection;
            Selection = originalSelection;
        }
        Commit(result, selection);
    }
    public void Undo() { if (IsReadOnly) return; if (_scopeOwner != null) _scopeOwner.Undo(); else if (_sharedHistory != null) _sharedHistory.Undo(); else Restore(_undo, _redo); }
    public void Redo() { if (IsReadOnly) return; if (_scopeOwner != null) _scopeOwner.Redo(); else if (_sharedHistory != null) _sharedHistory.Redo(); else Restore(_redo, _undo, after: true); }
    private void Restore(List<HistoryEntry> from, List<HistoryEntry> to, bool after = false)
    {
        if (from.Count == 0) return;
        var entry = from[^1];
        to.Add(entry);
        from.RemoveAt(from.Count - 1);
        var snapshot = after ? entry.After : entry.Before;
        Root = snapshot.Root;
        Projection = Project();
        Selection = snapshot.Selection;
        BreakTypingGroup();
        Revision++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Edit(int start, int length, string text, bool coalesce = true)
    {
        start = Math.Clamp(start, 0, Projection.Text.Length);
        length = Math.Clamp(length, 0, Projection.Text.Length - start);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (length == 0 && text.Length == 0) return;
        var first = Projection.At(start);
        var last = Projection.At(start + length);
        if (Projection.Rows.Skip(first.Index).Take(last.Index - first.Index + 1).Any(row => row.IsAtomic && row.End > start && row.Start < start + length
            && (start > row.Start || start + length < row.End)))
            throw new InvalidOperationException("这种内容不能按字符修改，请选中整个块后操作。");
        var from = Math.Clamp(start - first.Start, 0, first.Text.Length);
        var to = Math.Clamp(start + length - last.Start, 0, last.Text.Length);
        if (first.IsAtomic && first.Node.Id == last.Node.Id)
        {
            var added = text.Split('\n').Select(chunk => NoteNode.Paragraph(chunk)).ToArray();
            var replacements = length > 0 ? added : from == 0 ? [.. added, first.Block] : new[] { first.Block }.Concat(added).ToArray();
            Commit(NoteTree.Replace(Root, first.Block.Id, replacements), EditorSelection.At(added[^1].Id, text.Split('\n')[^1].Length));
            return;
        }
        if (!first.IsAtomic && first.Node.Id == last.Node.Id && !text.Contains('\n'))
        {
            var updated = RichText.Splice(first.Node, from, to - from, text,
                NoteComments.InsertionMarks(first.Node, from, to - from, TypingMarks ?? NoteReferences.InsertionMarks(first.Node, from, to - from)));
            Commit(NoteTree.Update(Root, first.Node.Id, _ => updated), EditorSelection.At(first.Node.Id, from + text.Length), coalesce ? $"text:{first.Node.Id}" : null);
            return;
        }
        var prefix = first.IsAtomic ? [] : RichText.Slice(first.Node.Content, 0, from);
        var suffix = last.IsAtomic ? [] : RichText.Slice(last.Node.Content, to, last.Text.Length - to);
        var chunks = text.Split('\n');
        var removed = first.Node.Id == last.Node.Id ? Math.Max(0, to - from) : first.Text.Length - from;
        var marks = first.IsAtomic ? [] : NoteComments.InsertionMarks(first.Node, from, removed, TypingMarks ?? NoteReferences.InsertionMarks(first.Node, from, removed));
        var root = Root;
        // Removing a title unwraps its remaining children; a completely selected collapsed title removes its subtree.
        for (var i = last.Index; i > first.Index; i--)
        {
            var row = Projection.Rows[i];
            if (row.IsAtomic && start + length <= row.Start) continue;
            var removeWhole = row.Collapsed && start + length >= row.End;
            root = RemoveRow(root, row, removeWhole);
        }
        var updatedFirst = (first.IsAtomic ? NoteNode.Paragraph() : first.Node) with { Content = RichText.Compact([.. prefix, .. RichText.FromText(chunks[0], marks, first.Node.Type == "codeBlock"), .. (chunks.Length == 1 ? suffix : [])]) };
        // Joining surviving paragraph content carries both discussions to the merged paragraph.
        // Fully deleted later paragraphs keep their discussions as orphans instead.
        var keepLastAnchor = first.Node.Id != last.Node.Id && !last.IsAtomic && (to == 0 || to < last.Text.Length);
        if (chunks.Length == 1 && keepLastAnchor) updatedFirst = NoteComments.MergeBlockAnchors(updatedFirst, last.Node);
        var removeFirstSubtree = first.Collapsed && from == 0 && length > 0 && start + length > first.End;
        root = first.IsAtomic ? NoteTree.Replace(root, first.Block.Id, from == first.Text.Length ? [first.Block, updatedFirst] : [updatedFirst]) : removeFirstSubtree
            ? NoteTree.Replace(root, first.Block.Id, updatedFirst)
            : NoteTree.Update(root, first.Node.Id, _ => updatedFirst);
        var caretId = updatedFirst.Id;
        if (chunks.Length > 1)
        {
            var added = chunks.Skip(1).Select(chunk => NoteNode.Paragraph() with { Content = RichText.FromText(chunk, marks) }).ToArray();
            added[^1] = added[^1] with { Content = RichText.Compact([.. added[^1].Content, .. suffix]) };
            if (keepLastAnchor) added[^1] = NoteComments.MergeBlockAnchors(added[^1], last.Node);
            caretId = added[^1].Id;
            if (first.IsToggle && !removeFirstSubtree)
                root = NoteTree.Update(root, first.Block.Id, block => block.WithAttr("collapsed", false) with { Content = block.Content.InsertRange(1, added) });
            else
                root = NoteTree.Replace(root, updatedFirst.Id, [updatedFirst, .. added]);
        }
        Commit(root, EditorSelection.At(caretId, chunks.Length == 1 ? (first.IsAtomic ? 0 : from) + text.Length : chunks[^1].Length));
    }

    private static NoteNode RemoveRow(NoteNode root, BlockRow row, bool removeWhole)
    {
        if (row.Block.Id != row.Node.Id)
        {
            var owner = NoteTree.Find(root, row.Block.Id);
            if (owner == null) return root;
            if (removeWhole) return NoteTree.Replace(root, owner.Id);
            var remaining = owner.Content.Where(c => c.Id != row.Node.Id).ToArray();
            if (owner.Type == "toggleBlock") return NoteTree.Replace(root, owner.Id, remaining);
            return remaining.Length == 0 ? NoteTree.Replace(root, owner.Id) : NoteTree.Update(root, owner.Id, n => n with { Content = remaining.ToImmutableArray() });
        }
        return NoteTree.Replace(root, row.Node.Id);
    }

    public void Format(int start, int length, NoteMark? mark, bool forceRemove = false, bool toggle = true)
    {
        if (IsReadOnly) return;
        if (length == 0)
        {
            var row = Projection.At(start);
            var existing = (TypingMarks ?? RichText.MarksAt(row.Node, start - row.Start)).Where(m => m.Type != NoteComments.MarkType).ToImmutableArray();
            TypingMarks = mark == null ? [] : forceRemove || toggle && existing.Any(mark.Equivalent)
                ? existing.Where(m => m.Type != mark.Type).ToImmutableArray()
                : [.. existing.Where(m => m.Type != mark.Type), mark];
            _lastGroup = null;
            return;
        }
        var end = start + length;
        var ranges = Projection.Rows.Where(row => !row.IsAtomic && row.Node.Type != "codeBlock" && row.End > start && row.Start < end)
            .Select(row => (Row: row, From: Math.Max(start - row.Start, 0), Length: Math.Min(end, row.End) - Math.Max(start, row.Start))).ToArray();
        var remove = forceRemove || toggle && mark != null && ranges.Length > 0 && ranges.All(r => RichText.Slice(r.Row.Node.Content, r.From, r.Length)
            .Where(run => run.Type == "text" && run.Text.Length > 0).All(run => run.Marks.Any(mark.Equivalent)));
        var root = Root;
        foreach (var range in ranges) root = NoteTree.Update(root, range.Row.Node.Id, block => RichText.SetMark(block, range.From, range.Length, mark, remove));
        Commit(root);
    }

    public void Toggle(Guid blockId)
    {
        var node = NoteTree.Find(Root, blockId);
        if (node?.Type != "toggleBlock") return;
        if (IsReadOnly) { ViewFolds([(blockId, !IsCollapsed(node))]); return; }
        if (node.Content.Length == 1)
        {
            var child = NoteNode.Toggle("");
            Commit(NoteTree.Update(Root, blockId, n => n.WithAttr("collapsed", false) with { Content = n.Content.Add(child) }), EditorSelection.At(child.Content[0].Id));
            return;
        }
        Commit(NoteTree.Update(Root, blockId, n => n.WithAttr("collapsed", !n.Bool("collapsed"))));
    }

    public void ToggleAll()
    {
        SetAllCollapsed(NoteTree.Descendants(Root).Any(n => n.Type == "toggleBlock" && n.Content.Length > 1 && !IsCollapsed(n)));
    }

    public void SetAllCollapsed(bool collapse)
    {
        if (IsReadOnly) { ViewFolds(NoteTree.Descendants(Root).Where(n => n.Type == "toggleBlock").Select(n => (n.Id, collapse))); return; }
        NoteNode Visit(NoteNode n)
        {
            var children = n.Content.Select(Visit).ToImmutableArray();
            var node = children.Where((child, index) => !ReferenceEquals(child, n.Content[index])).Any() ? n with { Content = children } : n;
            return n.Type == "toggleBlock" && n.Content.Length > 1 && n.Bool("collapsed") != collapse ? node.WithAttr("collapsed", collapse) : node;
        }
        Commit(Visit(Root));
    }

    public void ToggleTask(Guid blockId) => Commit(NoteTree.Update(Root, blockId, n => n.Type == "taskItem" ? n.WithAttr("checked", !n.Bool("checked")) : n));
}
