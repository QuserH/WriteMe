using System.Collections.Immutable;

namespace WriteMe.Core;

public enum MarkCoverage { None, Mixed, All }
public sealed record LinkRange(int Start, int Length, NoteMark Mark);

// Note: 格式状态与跨样式链接范围共用文档事实 — 见 .agents/notes/implemented/feature/2026-09-09-text-formatting-toolbar.md
public sealed class SelectionFormats
{
    private readonly ImmutableArray<NoteNode> _runs;
    public bool CanFormat { get; }
    public bool HasFormatting => _runs.Any(run => run.Marks.Any(mark => mark.Type != NoteComments.MarkType));
    public MarkCoverage TextColorCoverage
    {
        get
        {
            var count = _runs.Count(run => TextColor.Read(run.Marks) != null);
            return count == 0 ? MarkCoverage.None : count == _runs.Length ? MarkCoverage.All : MarkCoverage.Mixed;
        }
    }
    public string? UniformTextColor => _runs.FirstOrDefault() is { } first && TextColor.Read(first.Marks) is { } color
        && _runs.All(run => TextColor.Read(run.Marks) == color) ? color : null;

    public SelectionFormats(DocumentProjection projection, int start, int length)
    {
        if (start < 0 || length <= 0 || start > projection.Text.Length - length)
        {
            _runs = [];
            return;
        }
        var runs = ImmutableArray.CreateBuilder<NoteNode>();
        var end = start + length;
        var supported = true;
        for (var index = projection.At(start).Index; index < projection.Rows.Length; index++)
        {
            var row = projection.Rows[index];
            if (row.Start >= end) break;
            if (row.End <= start) continue;
            if (row.IsAtomic || row.Node.Type == "codeBlock") { supported = false; continue; }
            var from = Math.Max(start, row.Start) - row.Start;
            var count = Math.Min(end, row.End) - row.Start - from;
            runs.AddRange(RichText.Slice(row.Node.Content, from, count).Where(run => run.Type == "text" && run.Text.Length > 0));
        }
        _runs = runs.ToImmutable();
        CanFormat = supported && !_runs.IsEmpty;
    }

    public MarkCoverage Coverage(string type)
    {
        var count = _runs.Count(run => run.Marks.Any(mark => mark.Type == type));
        return count == 0 ? MarkCoverage.None : count == _runs.Length ? MarkCoverage.All : MarkCoverage.Mixed;
    }

    public NoteMark? UniformMark(string type)
    {
        var first = _runs.FirstOrDefault()?.Marks.FirstOrDefault(mark => mark.Type == type);
        return first != null && _runs.All(run => run.Marks.Any(first.Equivalent)) ? first : null;
    }

    public static LinkRange? LinkAt(DocumentProjection projection, int caret)
    {
        if (caret < 0 || caret > projection.Text.Length) return null;
        var row = projection.At(caret);
        if (row.IsAtomic || row.Node.Type == "codeBlock") return null;
        var local = caret - row.Start;
        var runs = row.Node.Content;
        var offsets = new int[runs.Length + 1];
        for (var i = 0; i < runs.Length; i++) offsets[i + 1] = offsets[i] + RichText.Length(runs[i]);
        NoteMark? Link(int i) => runs[i].Type == "text" ? runs[i].Marks.FirstOrDefault(m => m.Type == "link" && !string.IsNullOrEmpty(m.String("href"))) : null;
        var selected = -1;
        // Prefer the run to the right of a boundary; a link's trailing caret still edits that link.
        for (var i = 0; i < runs.Length; i++)
            if (local >= offsets[i] && local < offsets[i + 1] && Link(i) != null) { selected = i; break; }
        if (selected < 0)
            for (var i = 0; i < runs.Length; i++)
                if (local == offsets[i + 1] && Link(i) != null) selected = i;
        if (selected < 0) return null;
        var mark = Link(selected)!;
        var first = selected;
        var last = selected;
        while (first > 0 && Link(first - 1) is { } previous && mark.Equivalent(previous)) first--;
        while (last + 1 < runs.Length && Link(last + 1) is { } next && mark.Equivalent(next)) last++;
        return new(row.Start + offsets[first], offsets[last + 1] - offsets[first], mark);
    }
}
