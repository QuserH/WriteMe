using System.Collections.Immutable;
using System.Text;

namespace WriteMe.Core;

public static class RichText
{
    public static int Length(NoteNode inline) => inline.Type == "text" ? inline.Text.Length : 1;
    public static string Plain(NoteNode block)
    {
        var text = new StringBuilder();
        foreach (var run in block.Content)
            text.Append(run.Type == "text" ? run.Text.Replace("\r\n", "\n").Replace('\n', '\u2028') : run.Type == "hardBreak" ? "\u2028" : "\uFFFC");
        return text.ToString();
    }

    public static ImmutableArray<NoteNode> Slice(ImmutableArray<NoteNode> runs, int start, int length)
    {
        var result = ImmutableArray.CreateBuilder<NoteNode>();
        var end = start + length;
        var pos = 0;
        foreach (var run in runs)
        {
            var size = Length(run);
            var from = Math.Max(start - pos, 0);
            var to = Math.Min(end - pos, size);
            if (to > from)
                result.Add(run.Type == "text" ? run with { Text = run.Text.Substring(from, to - from) } : run);
            pos += size;
        }
        return result.ToImmutable();
    }

    public static ImmutableArray<NoteMark> MarksAt(NoteNode block, int offset)
    {
        var pos = 0;
        foreach (var run in block.Content)
        {
            var end = pos + Length(run);
            if (end >= offset && (offset > pos || pos == 0)) return run.Marks;
            pos = end;
        }
        return [];
    }

    public static ImmutableArray<NoteNode> FromText(string text, ImmutableArray<NoteMark> marks, bool code = false)
    {
        if (text.Length == 0) return [];
        if (code) return [new("text") { Text = text.Replace('\u2028', '\n'), Marks = marks }];
        var parts = text.Replace('\n', '\u2028').Split('\u2028');
        var result = ImmutableArray.CreateBuilder<NoteNode>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) result.Add(new("hardBreak") { Marks = marks });
            if (parts[i].Length > 0) result.Add(new("text") { Text = parts[i], Marks = marks });
        }
        return result.ToImmutable();
    }

    public static NoteNode Splice(NoteNode block, int start, int length, string text, ImmutableArray<NoteMark>? marks = null)
    {
        var total = block.Content.Sum(Length);
        var inserted = FromText(text, marks ?? MarksAt(block, start), block.Type == "codeBlock");
        return block with { Content = Compact([.. Slice(block.Content, 0, start), .. inserted, .. Slice(block.Content, start + length, total - start - length)]) };
    }

    public static bool SameMarks(ImmutableArray<NoteMark> a, ImmutableArray<NoteMark> b) => a.Length == b.Length && a.All(m => b.Any(m.Equivalent));

    public static ImmutableArray<NoteNode> Compact(IEnumerable<NoteNode> runs)
    {
        var result = ImmutableArray.CreateBuilder<NoteNode>();
        foreach (var run in runs)
        {
            if (run.Type == "text" && run.Text.Length == 0) continue;
            if (result.Count > 0 && run.Type == "text" && result[^1].Type == "text" && SameMarks(result[^1].Marks, run.Marks) && NoteMark.EqualMap(result[^1].Extra, run.Extra))
                result[^1] = result[^1] with { Text = result[^1].Text + run.Text };
            else result.Add(run);
        }
        return result.ToImmutable();
    }

    public static NoteNode SetMark(NoteNode block, int start, int length, NoteMark? mark, bool remove = false)
    {
        if (length <= 0) return block;
        var total = block.Content.Sum(Length);
        var selectedRuns = Slice(block.Content, start, length);
        if (selectedRuns.All(run => mark == null ? run.Marks.All(m => m.Type == NoteComments.MarkType) : remove
                ? run.Marks.All(m => m.Type != mark.Type)
                : run.Marks.Any(mark.Equivalent))) return block;
        var selected = selectedRuns.Select(run => run with
        {
            Marks = mark == null ? run.Marks.Where(m => m.Type == NoteComments.MarkType).ToImmutableArray() : remove
                ? run.Marks.Where(m => m.Type != mark.Type).ToImmutableArray()
                : [.. run.Marks.Where(m => m.Type != mark.Type), mark]
        });
        return block with { Content = Compact([.. Slice(block.Content, 0, start), .. selected, .. Slice(block.Content, start + length, total - start - length)]) };
    }
}
