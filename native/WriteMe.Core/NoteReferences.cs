using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace WriteMe.Core;

public enum ReferenceKind { Tag, Note }
public sealed record ReferenceSpan(ReferenceKind Kind, string Target, string Label, int Start, int Length);
public sealed record IndexedReference(string Path, string Preview, ReferenceSpan Span);
public sealed record TagInfo(string Key, string Name, int Count);
public sealed record DocumentRelation(string DocumentId, string Title, string Path, int Start, int Length, string Preview, bool Exists = true);

// Note: M3 标签与稳定文档链接、可重建索引 — 见 .agents/notes/implemented/feature/2026-09-09-m3-note-connections.md
public static class NoteReferences
{
    private static readonly Regex TagPattern = new(@"#[\p{L}\p{N}_][\p{L}\p{M}\p{N}_/-]*", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TagNamePattern = new(@"^[\p{L}\p{N}_][\p{L}\p{M}\p{N}_/-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static string? NormalizeTag(string value)
    {
        var name = value.Trim().TrimStart('#').Normalize(NormalizationForm.FormC);
        return TagNamePattern.IsMatch(name) ? name : null;
    }
    public static string TagKey(string name) => name.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    public static bool IsTagBoundary(string text, int start) => start == 0 || char.IsWhiteSpace(text[start - 1]) || "([{<（【《，。；：！？、\"'“‘".Contains(text[start - 1]);
    public static string DisplayTitle(string title) => string.IsNullOrWhiteSpace(title) ? "无标题" : title;

    public static bool IsProtected(NoteNode block, int start, int length)
    {
        if (!block.IsTextBlock || block.Type == "codeBlock") return true;
        var offset = 0;
        foreach (var run in block.Content)
        {
            var end = offset + RichText.Length(run);
            if (end > start && offset < start + length && (run.Type != "text" || run.Marks.Any(mark => mark.Type is "code" or "link" or "noteLink"))) return true;
            offset = end;
        }
        return false;
    }

    public static IReadOnlyList<ReferenceSpan> InBlock(NoteNode block)
    {
        var result = new List<ReferenceSpan>();
        if (!block.IsTextBlock || block.Type == "codeBlock") return result;
        var text = RichText.Plain(block);
        foreach (Match match in TagPattern.Matches(text))
        {
            if (IsTagBoundary(text, match.Index) && !IsProtected(block, match.Index, match.Length) && NormalizeTag(match.Value) is { } name)
                result.Add(new(ReferenceKind.Tag, TagKey(name), name, match.Index, match.Length));
        }
        var position = 0;
        ReferenceSpan? previous = null;
        foreach (var run in block.Content)
        {
            var length = RichText.Length(run);
            var target = run.Type == "text" ? run.Marks.FirstOrDefault(mark => mark.Type == "noteLink")?.String("documentId") : null;
            if (!string.IsNullOrWhiteSpace(target) && length > 0)
            {
                if (previous != null && previous.Target == target && previous.Start + previous.Length == position)
                {
                    result.Remove(previous);
                    previous = previous with { Length = previous.Length + length, Label = previous.Label + run.Text };
                }
                else previous = new(ReferenceKind.Note, target, run.Text, position, length);
                result.Add(previous);
            }
            else previous = null;
            position += length;
        }
        return result.OrderBy(span => span.Start).ToArray();
    }

    public static ReferenceSpan? At(NoteNode block, int offset, bool includeEnd = false)
        => InBlock(block).FirstOrDefault(span => offset >= span.Start && (offset < span.Start + span.Length || includeEnd && offset == span.Start + span.Length));

    public static IReadOnlyList<IndexedReference> Read(NoteNode root)
    {
        var result = new List<IndexedReference>();
        void Walk(NoteNode node, string path)
        {
            if (node.IsTextBlock)
            {
                var text = RichText.Plain(node).Replace('\u2028', ' ');
                foreach (var span in InBlock(node))
                {
                    var start = Math.Max(0, span.Start - 40);
                    var preview = text.Substring(start, Math.Min(150, text.Length - start));
                    result.Add(new(path, (start > 0 ? "…" : "") + preview + (start + preview.Length < text.Length ? "…" : ""), span));
                }
                return;
            }
            if (!node.IsContentContainer) return;
            for (var i = 0; i < node.Content.Length; i++) Walk(node.Content[i], path.Length == 0 ? i.ToString() : path + "/" + i);
        }
        Walk(root, "");
        return result;
    }

    public static NoteNode? AtPath(NoteNode root, string path)
    {
        var node = root;
        foreach (var part in path.Split('/'))
        {
            if (!int.TryParse(part, out var index) || index < 0 || index >= node.Content.Length) return null;
            node = node.Content[index];
        }
        return node;
    }

    internal static ImmutableArray<NoteMark> InsertionMarks(NoteNode block, int offset, int removalLength)
    {
        var marks = RichText.MarksAt(block, offset);
        if (removalLength > 0 || !marks.Any(mark => mark.Type == "noteLink")) return marks;
        var link = At(block, offset, true);
        return link is { Kind: ReferenceKind.Note } && offset > link.Start && offset < link.Start + link.Length
            ? marks : marks.Where(mark => mark.Type != "noteLink").ToImmutableArray();
    }
}

public sealed partial class DocumentSession
{
    public bool InsertNoteLink(int start, int length, string documentId, string label, bool keepSelectedText = false)
    {
        if (start < 0 || length < 0 || start > Projection.Text.Length - length || string.IsNullOrWhiteSpace(documentId)) return false;
        var row = Projection.At(start);
        if (row.IsAtomic || row.Node.Type == "codeBlock" || start + length > row.End) return false;
        var local = start - row.Start;
        var mark = NoteMark.With("noteLink", "documentId", documentId);
        var block = row.Node;
        if (keepSelectedText && length > 0)
        {
            block = RichText.SetMark(block, local, length, new("link"), true);
            block = RichText.SetMark(block, local, length, mark);
            Commit(NoteTree.Update(Root, block.Id, _ => block));
        }
        else
        {
            label = NoteReferences.DisplayTitle(label.Replace('\r', ' ').Replace('\n', ' ').Replace('\u2028', ' '));
            var marks = RichText.MarksAt(block, local).Where(item => item.Type is not ("link" or "noteLink")).ToImmutableArray();
            var content = RichText.FromText(label, marks.Add(mark));
            var space = local + length == row.Text.Length || !char.IsWhiteSpace(row.Text[local + length]);
            block = block with { Content = RichText.Compact([.. RichText.Slice(block.Content, 0, local), .. content,
                .. RichText.FromText(space ? " " : "", marks), .. RichText.Slice(block.Content, local + length, row.Text.Length - local - length)]) };
            Commit(NoteTree.Update(Root, block.Id, _ => block), EditorSelection.At(block.Id, local + label.Length + (space ? 1 : 0)));
        }
        BreakTypingGroup();
        return true;
    }
}
