using System.Collections.Immutable;

namespace WriteMe.Core;

public sealed record OutlineEntry(Guid NodeId, string Title, int Depth, int HeadingLevel);

// Note: 左侧目录只收录标题，导航与侧栏编辑复用文档事务 — 见 .agents/notes/implemented/feature/2026-09-09-editor-sidebar.md
public static class DocumentOutline
{
    public static ImmutableArray<OutlineEntry> Read(NoteNode root)
    {
        var entries = ImmutableArray.CreateBuilder<OutlineEntry>();
        var levels = new Stack<int>();
        void Walk(NoteNode node)
        {
            if (node.Type == "heading")
            {
                var level = Math.Clamp(node.Int("level", 1), 1, 3);
                while (levels.TryPeek(out var previous) && previous >= level) levels.Pop();
                entries.Add(new(node.Id, Label(node, "未命名标题"), levels.Count, level));
                levels.Push(level);
            }
            else if (node.IsContentContainer)
                foreach (var child in node.Content) Walk(child);
        }
        Walk(root);
        return entries.ToImmutable();
    }

    private static string Label(NoteNode node, string fallback)
    {
        var text = RichText.Plain(node).Replace('\u2028', ' ').Trim();
        return text.Length == 0 ? fallback : text;
    }
}

public sealed partial class DocumentSession
{
    public void RestoreSnapshot(NoteNode root)
    {
        if (root.Type != "doc" || root.Content.IsEmpty) throw new ArgumentException("无效的文档快照");
        var projection = new DocumentProjection(root);
        Commit(root, EditorSelection.At(projection.Rows[0].Node.Id));
        BreakTypingGroup();
    }

    public bool InsertBlock(int offset, string kind, int level = 1)
    {
        if (kind == "table") return InsertTable(offset);
        if (kind == "columnList") return InsertColumns(offset, Math.Clamp(level, 2, 3));
        if (kind is not ("paragraph" or "heading" or "toggleBlock" or "bulletList" or "orderedList" or "taskList" or "blockquote" or "codeBlock" or "horizontalRule")) return false;
        var row = Projection.At(offset);
        if (row.Node.Id == row.Block.Id && row.Node.Type == "paragraph" && row.Text.Length == 0)
        {
            if (kind == "paragraph") return false;
            ConvertBlock(row.Start, kind, level);
            return true;
        }

        var text = NoteNode.Paragraph();
        NoteNode block;
        switch (kind)
        {
            case "heading": block = text = text.WithAttr("level", Math.Clamp(level, 1, 3)) with { Type = "heading" }; break;
            case "codeBlock": block = text = text with { Type = "codeBlock" }; break;
            case "toggleBlock": block = new NoteNode(kind) { Content = [text] }.WithAttr("collapsed", true); break;
            case "bulletList":
            case "orderedList":
            case "taskList":
                block = new(kind) { Content = [new(kind == "taskList" ? "taskItem" : "listItem") { Content = [text] }] };
                break;
            case "blockquote": block = new(kind) { Content = [text] }; break;
            case "horizontalRule": block = new(kind); break;
            default: block = text; break;
        }
        NoteNode[] added = kind == "horizontalRule" ? [block, text] : [block];
        return InsertAfter(row, added, text);
    }

    private bool InsertAfter(BlockRow row, NoteNode[] added, NoteNode text)
    {
        NoteNode root;
        if (row.Block.Type is "listItem" or "taskItem")
        {
            var list = NoteTree.Parent(Root, row.Block.Id)!;
            var index = list.Content.FindIndex(node => node.Id == row.Block.Id);
            var before = list with { Content = list.Content.Take(index + 1).ToImmutableArray() };
            var pieces = new List<NoteNode> { before };
            pieces.AddRange(added);
            if (index + 1 < list.Content.Length)
            {
                var after = list with { Id = Guid.NewGuid(), Content = list.Content.Skip(index + 1).ToImmutableArray() };
                if (list.Type == "orderedList") after = after.WithAttr("start", list.Int("start", 1) + index + 1);
                pieces.Add(after);
            }
            root = NoteTree.Replace(Root, list.Id, pieces.ToArray());
        }
        else root = NoteTree.Replace(Root, row.Block.Id, [row.Block, .. added]);
        Commit(root, EditorSelection.At(text.Id));
        return true;
    }

    public bool Reveal(Guid nodeId)
    {
        if (NoteTree.Find(Root, nodeId) is not { } target || !target.IsTextBlock && target.Type is not ("image" or "attachment")) return false;
        var root = Root;
        var ancestor = NoteTree.Parent(Root, nodeId);
        while (ancestor != null)
        {
            // A toggle's own title is always visible; reveal its ancestors, retaining its own state.
            if (ancestor.Type == "toggleBlock" && ancestor.Content[0].Id != nodeId && ancestor.Bool("collapsed"))
                root = NoteTree.Update(root, ancestor.Id, node => node.WithAttr("collapsed", false));
            ancestor = NoteTree.Parent(Root, ancestor.Id);
        }
        var selection = EditorSelection.At(nodeId);
        BreakTypingGroup();
        if (ReferenceEquals(root, Root)) Selection = selection;
        else Commit(root, selection);
        return true;
    }
}
