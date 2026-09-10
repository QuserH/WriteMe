using System.Collections.Immutable;
using System.Text.Json;

namespace WriteMe.Core;

// Note: M3 文字颜色使用 textStyle.color，恢复默认不删除其他样式属性 — 见 .agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md
public static class TextColor
{
    public static string? Normalize(string? value)
    {
        var hex = value?.Trim();
        if (hex?.StartsWith('#') == true) hex = hex[1..];
        if (hex is not { Length: 3 or 6 } || !hex.All(Uri.IsHexDigit)) return null;
        if (hex.Length == 3) hex = string.Concat(hex.Select(character => new string(character, 2)));
        return "#" + hex.ToUpperInvariant();
    }

    public static string? Read(ImmutableArray<NoteMark> marks)
        => Normalize(marks.FirstOrDefault(mark => mark.Type == "textStyle")?.String("color"));

    internal static ImmutableArray<NoteMark> Set(ImmutableArray<NoteMark> marks, string? color)
    {
        var existing = marks.FirstOrDefault(mark => mark.Type == "textStyle");
        if (color != null && Read(marks) == color || color == null && existing?.Attrs.ContainsKey("color") != true) return marks;
        var style = existing ?? new NoteMark("textStyle");
        style = style with { Attrs = color == null ? style.Attrs.Remove("color") : style.Attrs.SetItem("color", JsonSerializer.SerializeToElement(color)) };
        var others = marks.Where(mark => mark.Type != "textStyle").ToImmutableArray();
        return style.Attrs.IsEmpty && style.Extra.IsEmpty ? others : others.Add(style);
    }

    internal static NoteNode Set(NoteNode block, int start, int length, string? color)
    {
        var selected = RichText.Slice(block.Content, start, length);
        var changed = selected.Select(run => run with { Marks = Set(run.Marks, color) }).ToImmutableArray();
        if (selected.Zip(changed).All(pair => RichText.SameMarks(pair.First.Marks, pair.Second.Marks))) return block;
        return block with { Content = RichText.Compact([
            .. RichText.Slice(block.Content, 0, start), .. changed,
            .. RichText.Slice(block.Content, start + length, RichText.Plain(block).Length - start - length)]) };
    }
}

public sealed partial class DocumentSession
{
    public void SetTextColor(int start, int length, string? color)
    {
        if (start < 0 || length < 0 || start > Projection.Text.Length - length) return;
        var normalized = TextColor.Normalize(color);
        if (color != null && normalized == null) return;
        if (length == 0)
        {
            var row = Projection.At(start);
            if (row.IsAtomic || row.Node.Type == "codeBlock") return;
            TypingMarks = TextColor.Set(TypingMarks ?? RichText.MarksAt(row.Node, start - row.Start), normalized);
            _lastGroup = null;
            return;
        }
        var end = start + length;
        var root = Root;
        foreach (var row in Projection.Rows.Where(row => !row.IsAtomic && row.Node.Type != "codeBlock" && row.End > start && row.Start < end))
        {
            var from = Math.Max(start, row.Start) - row.Start;
            var count = Math.Min(end, row.End) - row.Start - from;
            root = NoteTree.Update(root, row.Node.Id, block => TextColor.Set(block, from, count, normalized));
        }
        Commit(root);
    }
}
