using System.Collections.Immutable;

namespace WriteMe.Core;

// Note: 单元格和分栏复用主文档历史，见 .agents/notes/implemented/feature/2026-09-11-native-tables-columns-and-editing.md
public sealed partial class DocumentSession : IDisposable
{
    private DocumentSession? _scopeOwner;
    private Guid _scopeId;
    private bool _scopeDisposed;
    private EditorSelection _selection;
    public bool IsScopeAttached => !_scopeDisposed && (_scopeOwner == null || _scopeOwner.IsScopeAttached && NoteTree.Find(_scopeOwner.Root, _scopeId) != null);
    public Guid? ScopeId => _scopeOwner == null ? null : _scopeId;
    public DocumentSession HistoryOwner => _scopeOwner?.HistoryOwner ?? this;

    public DocumentSession CreateScope(Guid containerId)
    {
        var container = NoteTree.Find(Root, containerId);
        if (container?.Type is not ("tableCell" or "tableHeader" or "column"))
            throw new ArgumentException("编辑区域必须是表格单元格或分栏", nameof(containerId));
        var session = new DocumentSession(new("doc") { Id = containerId, Content = container.Content })
        { _scopeOwner = this, _scopeId = containerId, Revision = Revision };
        Changed += session.RefreshScope;
        return session;
    }

    private void RefreshScope(object? sender, EventArgs args)
    {
        if (_scopeOwner == null || _scopeDisposed) return;
        Revision = _scopeOwner.Revision;
        if (NoteTree.Find(_scopeOwner.Root, _scopeId) is not { } container)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var changed = Root.Content != container.Content;
        if (changed)
        {
            Root = NoteTree.Normalize(new("doc") { Id = _scopeId, Content = container.Content });
            Projection = new(Root);
        }
        var active = _scopeOwner.Selection;
        var selection = NoteTree.Find(Root, active.Anchor.NodeId) != null && NoteTree.Find(Root, active.Caret.NodeId) != null
            ? ResolveSelection(active) : ResolveSelection(_selection);
        var moved = selection != _selection;
        _selection = selection;
        if (changed || moved) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_scopeOwner != null) _scopeOwner.Changed -= RefreshScope;
        _scopeDisposed = true;
    }

    private void CommitScope(NoteNode root, EditorSelection selection, string? group)
    {
        if (_scopeOwner == null || !IsScopeAttached) return;
        var normalized = NoteTree.Normalize(root);
        var updated = NoteTree.Update(_scopeOwner.Root, _scopeId, node => node with { Content = normalized.Content });
        _scopeOwner.Commit(updated, selection, group == null ? null : $"scope:{_scopeId}:{group}");
    }

    public void SetAlignment(int start, int length, string alignment)
    {
        if (alignment is not ("left" or "center" or "right" or "justify")) throw new ArgumentException("无效的对齐方式", nameof(alignment));
        var first = Projection.At(Math.Clamp(start, 0, Projection.Text.Length));
        var last = Projection.At(Math.Clamp(start + Math.Max(0, length) - (length > 0 ? 1 : 0), 0, Projection.Text.Length));
        var root = Root;
        foreach (var row in Projection.Rows.Skip(first.Index).Take(last.Index - first.Index + 1).Where(row => !row.IsAtomic))
        {
            if ((row.Node.String("textAlign") ?? "left") == alignment) continue;
            root = NoteTree.Update(root, row.Node.Id, node => alignment == "left"
                ? node with { Attrs = node.Attrs.Remove("textAlign") } : node.WithAttr("textAlign", alignment));
        }
        Commit(root);
    }
}
