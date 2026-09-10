using System.Collections.Immutable;

namespace WriteMe.Core;

public static class NoteTree
{
    public static NoteNode? Find(NoteNode root, Guid id)
    {
        if (root.Id == id) return root;
        foreach (var child in root.Content)
            if (Find(child, id) is { } found) return found;
        return null;
    }

    public static NoteNode? Parent(NoteNode root, Guid id)
    {
        foreach (var child in root.Content)
        {
            if (child.Id == id) return root;
            if (Parent(child, id) is { } parent) return parent;
        }
        return null;
    }

    public static IEnumerable<NoteNode> Descendants(NoteNode root)
    {
        yield return root;
        foreach (var child in root.Content)
            foreach (var node in Descendants(child)) yield return node;
    }

    public static NoteNode Update(NoteNode root, Guid id, Func<NoteNode, NoteNode> update)
    {
        if (root.Id == id) return update(root);
        for (var i = 0; i < root.Content.Length; i++)
        {
            var before = root.Content[i];
            var after = Update(before, id, update);
            if (!ReferenceEquals(before, after)) return root with { Content = root.Content.SetItem(i, after) };
        }
        return root;
    }

    public static NoteNode Replace(NoteNode root, Guid id, params NoteNode[] replacements)
    {
        for (var i = 0; i < root.Content.Length; i++)
        {
            var child = root.Content[i];
            if (child.Id == id) return root with { Content = root.Content.RemoveAt(i).InsertRange(i, replacements) };
            var after = Replace(child, id, replacements);
            if (!ReferenceEquals(after, child)) return root with { Content = root.Content.SetItem(i, after) };
        }
        return root;
    }

    public static NoteNode Normalize(NoteNode root)
    {
        if (root.IsTextBlock || root.Type is "text" or "hardBreak") return root;
        var changed = false;
        var children = ImmutableArray.CreateBuilder<NoteNode>();
        foreach (var child in root.Content)
        {
            var updated = Normalize(child);
            if (updated.Content.IsEmpty && updated.Type is "bulletList" or "orderedList" or "taskList" or "blockquote") { changed = true; continue; }
            changed |= !ReferenceEquals(child, updated);
            children.Add(updated);
        }
        if (children.Count == 0 && root.Type is "doc" or "toggleBlock" or "listItem" or "taskItem") { children.Add(NoteNode.Paragraph()); changed = true; }
        if (root.Type is "toggleBlock" or "listItem" or "taskItem" && children[0].Type != "paragraph") { children.Insert(0, NoteNode.Paragraph()); changed = true; }
        return changed ? root with { Content = children.ToImmutable() } : root;
    }
}
