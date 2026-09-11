using System.Collections.Immutable;

namespace WriteMe.Core;

public enum DropPlacement { Before, After, Inside }

public sealed partial class DocumentSession
{
    // Note: 新建折叠项收起，Enter 展开父级并进入子项 — 见 .agents/notes/implemented/feature/2026-09-09-toggle-block.md
    public void Enter(int offset, bool sibling = false, bool softBreak = false)
    {
        var row = Projection.At(offset);
        if (row.IsAtomic)
        {
            var paragraph = NoteNode.Paragraph();
            Commit(NoteTree.Replace(Root, row.Block.Id, row.Block, paragraph), EditorSelection.At(paragraph.Id));
            return;
        }
        var local = Math.Clamp(offset - row.Start, 0, row.Text.Length);
        if (softBreak || row.Node.Type == "codeBlock" && !sibling) { Edit(offset, 0, "\u2028", false); return; }
        if (!sibling && ExitEmptyList(offset)) return;
        var before = row.Node with { Content = RichText.Slice(row.Node.Content, 0, local) };
        var after = NoteNode.Paragraph() with { Content = RichText.Slice(row.Node.Content, local, row.Text.Length - local) };
        var root = NoteTree.Update(Root, row.Node.Id, _ => before);
        if (row.IsToggle)
        {
            var added = new NoteNode("toggleBlock") { Content = [after] }.WithAttr("collapsed", true);
            root = sibling
                ? NoteTree.Replace(root, row.Block.Id, NoteTree.Find(root, row.Block.Id)!, added)
                : NoteTree.Update(root, row.Block.Id, n => n.WithAttr("collapsed", false) with { Content = n.Content.Insert(1, added) });
        }
        else if (row.Block.Type is "listItem" or "taskItem")
        {
            var added = new NoteNode(row.Block.Type) { Content = [after], Attrs = row.Block.Attrs };
            if (added.Type == "taskItem") added = added.WithAttr("checked", false);
            root = NoteTree.Replace(root, row.Block.Id, NoteTree.Find(root, row.Block.Id)!, added);
        }
        else root = NoteTree.Replace(root, row.Node.Id, before, after);
        Commit(root, EditorSelection.At(after.Id));
    }

    public bool BackspaceAtStart(int offset)
    {
        var row = Projection.At(offset);
        if (offset != row.Start || row.IsAtomic) return false;
        if (row.IsToggle) { ConvertBlock(offset, "paragraph"); return true; }
        if (row.Block.Type is "listItem" or "taskItem") { ConvertBlock(offset, "paragraph"); return true; }
        if (row.Depth > 0 && Outdent(row.Block.Id)) return true;
        if (row.Index == 0) return true;
        var previous = Projection.Rows[row.Index - 1];
        if (previous.IsAtomic) { Selection = Projection.Selection(previous.Start, previous.End); return true; }
        if (previous.Collapsed) { Toggle(previous.Block.Id); return true; }
        Edit(previous.End, row.Start - previous.End, "", false);
        return true;
    }

    // Note: 空待办退出与段首退格解除列表包装，保留邻项/子树 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
    public bool ExitEmptyList(int offset)
    {
        var row = Projection.At(offset);
        if (row.Text.Length != 0 || row.Block.Type is not ("listItem" or "taskItem")) return false;
        ConvertBlock(offset, "paragraph");
        return true;
    }

    public bool Indent(Guid blockId)
    {
        var parent = NoteTree.Parent(Root, blockId);
        if (parent == null) return false;
        var index = parent.Content.FindIndex(n => n.Id == blockId);
        if (index < 1) return false;
        var previous = parent.Content[index - 1];
        if (previous.Type != "toggleBlock") return false;
        return Move(blockId, previous.Id, DropPlacement.Inside, append: true);
    }

    public bool Outdent(Guid blockId)
    {
        var parent = NoteTree.Parent(Root, blockId);
        if (parent?.Type != "toggleBlock" || parent.Content[0].Id == blockId) return false;
        return Move(blockId, parent.Id, DropPlacement.After);
    }

    public bool CanMove(Guid sourceId, Guid targetId, DropPlacement placement, bool append = false)
    {
        if (sourceId == targetId) return false;
        var source = NoteTree.Find(Root, sourceId);
        var target = NoteTree.Find(Root, targetId);
        if (source == null || target == null || NoteTree.Find(source, targetId) != null) return false;
        // Titles and list-item paragraphs are not detachable units. The UI supplies their owning block ID.
        var sourceParent = NoteTree.Parent(Root, sourceId);
        if (sourceParent?.Type is "toggleBlock" or "listItem" or "taskItem" && sourceParent.Content[0].Id == sourceId) return false;
        if (source.Type is "listItem" or "taskItem") return false;
        var targetParent = NoteTree.Parent(Root, targetId);
        if (placement != DropPlacement.Inside && targetParent?.Type is "bulletList" or "orderedList" or "taskList") return false;
        if (placement == DropPlacement.Inside && target.Type != "toggleBlock") return false;
        if (placement == DropPlacement.Inside && target.Id == sourceParent?.Id)
        {
            var index = target.Content.FindIndex(node => node.Id == sourceId);
            if (index == (append ? target.Content.Length - 1 : 1)) return false;
        }
        if (placement != DropPlacement.Inside && targetParent?.Id == sourceParent?.Id && sourceParent != null)
        {
            var sourceIndex = sourceParent.Content.FindIndex(node => node.Id == sourceId);
            var targetIndex = sourceParent.Content.FindIndex(node => node.Id == targetId);
            if (placement == DropPlacement.Before && sourceIndex + 1 == targetIndex
                || placement == DropPlacement.After && sourceIndex == targetIndex + 1) return false;
        }
        return true;
    }

    public bool Move(Guid sourceId, Guid targetId, DropPlacement placement, bool append = false)
    {
        if (!CanMove(sourceId, targetId, placement, append)) return false;
        var source = NoteTree.Find(Root, sourceId)!;
        var target = NoteTree.Find(Root, targetId)!;
        var root = NoteTree.Replace(Root, sourceId);
        if (placement == DropPlacement.Inside)
            root = NoteTree.Update(root, targetId, n => n.WithAttr("collapsed", false) with { Content = n.Content.Insert(append ? n.Content.Length : 1, source) });
        else
        {
            target = NoteTree.Find(root, targetId)!;
            root = placement == DropPlacement.Before ? NoteTree.Replace(root, targetId, source, target) : NoteTree.Replace(root, targetId, target, source);
        }
        Commit(root);
        return true;
    }

    public void ConvertBlock(int offset, string kind, int level = 1, int removePrefix = 0)
    {
        var row = Projection.At(offset);
        if (row.IsAtomic) return;
        if (removePrefix == 0)
        {
            var parent = NoteTree.Parent(Root, row.Block.Id);
            if (row.Block.Type is "listItem" or "taskItem" && parent?.Type == kind) return;
            if (kind == "blockquote" && row.Quote) return;
            if (row.Block.Id == row.Node.Id && row.Node.Type == kind
                && (kind != "heading" || row.HeadingLevel == level)
                && (kind != "paragraph" || parent?.Type != "blockquote")) return;
        }
        var text = row.Node with { Type = "paragraph" };
        if (removePrefix > 0) text = RichText.Splice(text, 0, Math.Min(removePrefix, row.Text.Length), "");
        var tail = row.Block.Id == row.Node.Id ? ImmutableArray<NoteNode>.Empty : row.Block.Content.RemoveAt(0);
        NoteNode replacement;
        switch (kind)
        {
            case "toggleBlock":
                if (row.IsToggle && removePrefix == 0) return;
                replacement = new NoteNode("toggleBlock") { Content = [text, .. tail] }.WithAttr("collapsed", true);
                break;
            case "heading":
                text = text.WithAttr("level", level) with { Type = "heading" };
                replacement = text;
                break;
            case "bulletList":
            case "orderedList":
            case "taskList":
                var itemType = kind == "taskList" ? "taskItem" : "listItem";
                replacement = new(kind) { Content = [new(itemType) { Content = [text, .. tail] }] };
                break;
            case "blockquote": replacement = new(kind) { Content = [text, .. tail] }; break;
            case "codeBlock":
                text = text with { Type = "codeBlock", Content = RichText.FromText(RichText.Plain(text), [], true) };
                replacement = text;
                break;
            case "horizontalRule":
                var rule = new NoteNode("horizontalRule");
                Commit(NoteTree.Replace(Root, row.Block.Id, [rule, text, .. tail]), EditorSelection.At(text.Id));
                return;
            default: replacement = text; break;
        }
        var root = Root;
        if (row.Block.Type is "listItem" or "taskItem")
        {
            // Preserve the surrounding list grammar: replace a whole item with a compatible item,
            // or split the list around the converted block when leaving the list.
            var list = NoteTree.Parent(root, row.Block.Id)!;
            var index = list.Content.FindIndex(n => n.Id == row.Block.Id);
            if (replacement.Type == list.Type)
                root = NoteTree.Update(root, list.Id, n => n with { Content = n.Content.SetItem(index, replacement.Content[0]) });
            else
            {
                var pieces = new List<NoteNode>();
                if (index > 0) pieces.Add(list with { Content = list.Content.Take(index).ToImmutableArray() });
                pieces.Add(replacement);
                if (kind is "paragraph" or "heading" or "codeBlock") pieces.AddRange(tail);
                if (index + 1 < list.Content.Length)
                {
                    var after = list with { Id = Guid.NewGuid(), Content = list.Content.Skip(index + 1).ToImmutableArray() };
                    if (list.Type == "orderedList") after = after.WithAttr("start", list.Int("start", 1) + index + 1);
                    pieces.Add(after);
                }
                root = NoteTree.Replace(root, list.Id, pieces.ToArray());
            }
        }
        else root = kind is "paragraph" or "heading" or "codeBlock"
            ? NoteTree.Replace(root, row.Block.Id, [replacement, .. tail])
            : NoteTree.Replace(root, row.Block.Id, replacement);
        if (kind == "paragraph")
        {
            while (NoteTree.Parent(root, text.Id) is { Type: "blockquote" } quote)
            {
                var index = quote.Content.FindIndex(node => node.Id == text.Id);
                var pieces = new List<NoteNode>();
                if (index > 0) pieces.Add(quote with { Content = quote.Content.Take(index).ToImmutableArray() });
                pieces.Add(text);
                if (index + 1 < quote.Content.Length) pieces.Add(quote with { Id = Guid.NewGuid(), Content = quote.Content.Skip(index + 1).ToImmutableArray() });
                root = NoteTree.Replace(root, quote.Id, pieces.ToArray());
            }
        }
        Commit(root, EditorSelection.At(text.Id, Math.Max(0, offset - row.Start - removePrefix)));
    }

    public void DeleteBlock(Guid id)
    {
        var row = Projection.Rows.FirstOrDefault(r => r.Block.Id == id);
        if (row == null) return;
        var previous = Projection.Rows[Math.Max(0, row.Index - 1)];
        Commit(NoteTree.Replace(Root, id), EditorSelection.At(previous.Node.Id, previous.Text.Length));
    }

    public void DuplicateBlock(Guid id)
    {
        if (NoteTree.Find(Root, id) is not { } node) return;
        NoteNode Copy(NoteNode n) => n with { Id = Guid.NewGuid(), Content = n.Content.Select(Copy).ToImmutableArray() };
        Commit(NoteTree.Replace(Root, id, node, Copy(node)));
    }
}

internal static class ImmutableArrayExtensions
{
    public static int FindIndex<T>(this ImmutableArray<T> array, Func<T, bool> predicate)
    {
        for (var i = 0; i < array.Length; i++) if (predicate(array[i])) return i;
        return -1;
    }
}
