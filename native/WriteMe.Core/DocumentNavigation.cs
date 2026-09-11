using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace WriteMe.Core;

public sealed record DocumentTask(Guid TaskId, Guid NodeId, string Text, bool Completed, string Context);
public sealed record DocumentResource(Guid NodeId, int Start, int Length, string Kind, string Name, string Source, string Context);
public sealed record DocumentSearchMatch(Guid NodeId, int Start, int Length, string Text, string Context);
public sealed record DocumentSearchResult(NoteNode Root, string Query, bool CaseSensitive, ImmutableArray<DocumentSearchMatch> Matches, bool Truncated);

// Note: 当前文档任务/附件/查找遍历完整树，替换共用根历史 — 见 .agents/notes/implemented/feature/2026-09-09-editor-sidebar.md
public sealed class DocumentNavigation
{
    public const int MaximumMatches = 20_000;
    private static readonly ConditionalWeakTable<NoteNode, DocumentNavigation> Cache = new();
    private readonly ImmutableArray<(NoteNode Node, string Text, string Context)> _blocks;
    private readonly NoteNode _root;
    public ImmutableArray<DocumentTask> Tasks { get; }
    public ImmutableArray<DocumentResource> Resources { get; }
    public static DocumentNavigation For(NoteNode root) => Cache.GetValue(root, node => new(node));

    private DocumentNavigation(NoteNode root)
    {
        _root = root;
        var blocks = ImmutableArray.CreateBuilder<(NoteNode, string, string)>();
        var tasks = ImmutableArray.CreateBuilder<DocumentTask>();
        var resources = ImmutableArray.CreateBuilder<DocumentResource>();
        void Walk(NoteNode node, string context)
        {
            if (node.IsTextBlock)
            {
                var text = RichText.Plain(node);
                blocks.Add((node, text, context));
                var offset = 0;
                DocumentResource? previous = null;
                foreach (var run in node.Content)
                {
                    var length = RichText.Length(run);
                    if (run.Marks.FirstOrDefault(mark => mark.Type == "link")?.String("href") is { Length: > 0 } href)
                    {
                        var label = text.Substring(offset, length);
                        if (previous != null && previous.Source == href && previous.Start + previous.Length == offset)
                        {
                            previous = previous with { Name = previous.Name + label, Length = previous.Length + length };
                            resources[^1] = previous;
                        }
                        else
                        {
                            previous = new(node.Id, offset, length, "link", label, href, context);
                            resources.Add(previous);
                        }
                    }
                    else previous = null;
                    offset += length;
                }
                return;
            }
            if (node.Type is "image" or "attachment")
            {
                resources.Add(new(node.Id, 0, 0, node.Type, node.String("name") ?? node.String("alt") ?? (node.Type == "image" ? "图片" : "附件"),
                    node.String("src") ?? node.String("assetId") ?? "", context));
                return;
            }
            if (!node.IsContentContainer) return;
            if (node.Type == "taskItem" && FirstText(node) is { } paragraph)
            {
                var text = RichText.Plain(paragraph).Replace('\u2028', ' ').Trim();
                tasks.Add(new(node.Id, paragraph.Id, text.Length > 0 ? text : "未命名任务", node.Bool("checked"), context));
            }
            if (node.Type == "toggleBlock" && node.Content.FirstOrDefault() is { } title)
                context = Label(RichText.Plain(title), "折叠内容");
            else if (node.Type == "table") context = "表格";
            else if (node.Type == "columnList") context = "分栏";
            foreach (var child in node.Content)
            {
                Walk(child, context);
                if (child.Type == "heading") context = Label(RichText.Plain(child), "未命名标题");
            }
        }
        Walk(root, "");
        _blocks = blocks.ToImmutable(); Tasks = tasks.ToImmutable(); Resources = resources.ToImmutable();
    }

    private static NoteNode? FirstText(NoteNode node) => node.IsTextBlock ? node : node.IsContentContainer
        ? node.Content.Select(FirstText).FirstOrDefault(child => child != null) : null;
    private static string Label(string text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : NoteComments.Abbreviate(text.Replace('\u2028', ' ').Trim(), 70);

    public DocumentSearchResult Search(string query, bool caseSensitive = false)
    {
        query = query.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u2028');
        var matches = ImmutableArray.CreateBuilder<DocumentSearchMatch>();
        if (query.Length is 0 or > 2048 || query.Contains('\uFFFC')) return new(_root, query, caseSensitive, [], false);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var (node, text, context) in _blocks)
        {
            for (var from = 0; from <= text.Length - query.Length;)
            {
                var start = text.IndexOf(query, from, comparison);
                if (start < 0) break;
                if (matches.Count == MaximumMatches) return new(_root, query, caseSensitive, matches.ToImmutable(), true);
                matches.Add(new(node.Id, start, query.Length, text, context));
                from = start + query.Length;
            }
        }
        return new(_root, query, caseSensitive, matches.ToImmutable(), false);
    }
}

public sealed partial class DocumentSession
{
    public int ReplaceSearch(DocumentSearchResult expected, string replacement, DocumentSearchMatch? only = null)
    {
        var owner = HistoryOwner;
        if (!IsScopeAttached || !ReferenceEquals(expected.Root, owner.Root)) throw new InvalidOperationException("文档已变化，请查看最新查找结果后重试。");
        if (expected.Truncated && only == null) throw new InvalidOperationException("匹配过多，请缩小查找范围后再全部替换。");
        if (only != null && !expected.Matches.Contains(only)) throw new InvalidOperationException("查找结果已失效。");
        replacement = replacement.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', '\u2028');
        if (replacement.Length > 20_000) throw new InvalidOperationException("替换内容过长。");
        var matches = only == null ? expected.Matches : [only];
        var groups = matches.GroupBy(match => match.NodeId).ToDictionary(group => group.Key, group => group.OrderBy(match => match.Start).ToArray());
        var changed = 0;
        NoteNode ReplaceNode(NoteNode node)
        {
            if (node.IsTextBlock)
            {
                if (!groups.TryGetValue(node.Id, out var hits)) return node;
                var block = node;
                foreach (var hit in hits.Reverse())
                {
                    var original = RichText.Plain(node);
                    if (hit.Start < 0 || hit.Start + hit.Length > original.Length || hit.Length != expected.Query.Length
                        || !original.AsSpan(hit.Start, hit.Length).Equals(expected.Query, expected.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("查找结果已失效。");
                    if (original.AsSpan(hit.Start, hit.Length).SequenceEqual(replacement)) continue;
                    block = RichText.Splice(block, hit.Start, hit.Length, replacement,
                        NoteComments.InsertionMarks(block, hit.Start, hit.Length, NoteReferences.InsertionMarks(block, hit.Start, hit.Length)));
                    changed++;
                }
                return block;
            }
            if (!node.IsContentContainer) return node;
            var content = node.Content.Select(ReplaceNode).ToImmutableArray();
            return content.Where((child, index) => !ReferenceEquals(child, node.Content[index])).Any() ? node with { Content = content } : node;
        }
        var root = ReplaceNode(owner.Root);
        if (changed == 0) return 0;
        if (only != null)
        {
            var ancestor = NoteTree.Parent(root, only.NodeId);
            while (ancestor != null)
            {
                if (ancestor.Type == "toggleBlock" && ancestor.Content[0].Id != only.NodeId && ancestor.Bool("collapsed"))
                    root = NoteTree.Update(root, ancestor.Id, node => node.WithAttr("collapsed", false));
                ancestor = NoteTree.Parent(root, ancestor.Id);
            }
        }
        TextPoint Map(TextPoint point)
        {
            if (!groups.TryGetValue(point.NodeId, out var hits)) return point;
            var delta = 0;
            foreach (var hit in hits)
            {
                if (point.Offset < hit.Start) break;
                if (point.Offset <= hit.Start + hit.Length) return point with { Offset = hit.Start + delta + replacement.Length };
                delta += replacement.Length - hit.Length;
            }
            return point with { Offset = point.Offset + delta };
        }
        var selection = only != null ? new EditorSelection(new(only.NodeId, only.Start), new(only.NodeId, only.Start + replacement.Length))
            : new(Map(owner.Selection.Anchor), Map(owner.Selection.Caret));
        owner.BreakTypingGroup(); owner.Commit(root, selection);
        return changed;
    }
}
