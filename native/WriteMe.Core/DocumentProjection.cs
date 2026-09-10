using System.Collections.Immutable;
using System.Text;

namespace WriteMe.Core;

public sealed record BlockRow(NoteNode Node, NoteNode Block, int Depth, string Marker = "", bool Quote = false)
{
    public int Index { get; init; }
    public int Start { get; init; }
    public string Text { get; init; } = "";
    public int End => Start + Text.Length;
    public bool IsToggle => Block.Type == "toggleBlock";
    public bool Collapsed => IsToggle && Block.Bool("collapsed");
    public bool IsExpanded => IsToggle && !Collapsed && Block.Content.Length > 1;
    public ImmutableArray<int> GuideDepths { get; init; } = [];
    public bool IsAtomic => !Node.IsTextBlock;
    public int HeadingLevel => Node.Type == "heading" ? Math.Clamp(Node.Int("level", 1), 1, 3) : 0;
    public bool IsTask => Block.Type == "taskItem";
}

// One native text surface projects visible leaves. Hidden descendants stay in the immutable tree.
public sealed class DocumentProjection
{
    public ImmutableArray<BlockRow> Rows { get; }
    public string Text { get; }
    private readonly Dictionary<Guid, BlockRow> _byNode;
    private readonly Dictionary<Guid, BlockRow> _layoutHosts = [];

    public DocumentProjection(NoteNode root)
    {
        var rows = ImmutableArray.CreateBuilder<BlockRow>();
        var text = new StringBuilder();
        void Add(NoteNode node, NoteNode owner, int depth, ImmutableArray<int> guides, string marker, bool quote)
        {
            if (rows.Count > 0) text.Append('\n');
            var value = node.IsTextBlock ? RichText.Plain(node) : node.Type is "horizontalRule" or "image" or "attachment" or "table" or "columnList" ? "\uFFFC" : $"[保留的 {node.Type} 内容]";
            rows.Add(new(node, owner, depth, marker, quote) { Start = text.Length, Index = rows.Count, Text = value, GuideDepths = guides });
            text.Append(value);
        }
        void Walk(NoteNode node, int depth, ImmutableArray<int> guides, bool quote = false, NoteNode? owner = null, string marker = "")
        {
            if (node.IsTextBlock) { Add(node, owner ?? node, depth, guides, marker, quote); return; }
            if (node.Type == "toggleBlock")
            {
                Add(node.Content[0], node, depth, guides, "", quote);
                if (!node.Bool("collapsed"))
                {
                    var childGuides = guides.Add(depth);
                    foreach (var child in node.Content.Skip(1)) Walk(child, depth + 1, childGuides, quote);
                }
                return;
            }
            if (node.Type is "bulletList" or "orderedList" or "taskList")
            {
                var number = node.Int("start", 1);
                foreach (var item in node.Content)
                {
                    var bullet = node.Type == "orderedList" ? $"{number++}." : node.Type == "taskList" ? "task" : "•";
                    if (item.Content.IsEmpty) { Add(item, item, depth, guides, bullet, quote); continue; }
                    Walk(item.Content[0], depth, guides, quote, item, bullet);
                    foreach (var child in item.Content.Skip(1)) Walk(child, depth + 1, guides, quote);
                }
                return;
            }
            if (node.Type is "doc" or "blockquote")
            {
                foreach (var child in node.Content) Walk(child, depth + (node.Type == "blockquote" ? 1 : 0), guides, quote || node.Type == "blockquote");
                return;
            }
            Add(node, owner ?? node, depth, guides, marker, quote);
        }
        Walk(root, 0, []);
        Rows = rows.ToImmutable();
        Text = text.ToString();
        _byNode = Rows.ToDictionary(row => row.Node.Id);
        foreach (var row in Rows.Where(row => row.Node.Type is "table" or "columnList"))
            foreach (var child in NoteTree.Descendants(row.Node).Skip(1)) _layoutHosts[child.Id] = row;
    }

    public BlockRow At(int offset)
    {
        if (Rows.IsEmpty) throw new InvalidOperationException("文档没有可编辑块");
        var lo = 0;
        var hi = Rows.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Rows[mid].Start <= offset) lo = mid; else hi = mid - 1;
        }
        return Rows[lo];
    }

    public BlockRow? Find(Guid nodeId) => _byNode.GetValueOrDefault(nodeId);
    public BlockRow? LayoutHost(Guid nodeId) => _layoutHosts.GetValueOrDefault(nodeId);
    public TextPoint Point(int offset)
    {
        var row = At(offset);
        return new(row.Node.Id, Math.Clamp(offset - row.Start, 0, row.Text.Length));
    }
    public int Offset(TextPoint point) => Find(point.NodeId) is { } row ? row.Start + Math.Clamp(point.Offset, 0, row.Text.Length) : LayoutHost(point.NodeId)?.Start ?? 0;
    public EditorSelection Selection(int start, int end) => new(Point(start), Point(end));
}
