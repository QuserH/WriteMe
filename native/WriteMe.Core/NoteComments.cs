using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WriteMe.Core;

public sealed record CommentMessage(Guid Id, string Author, string Text, long CreatedAt, long? EditedAt = null, Guid? ReplyTo = null, bool Deleted = false, string? AuthorId = null);
public sealed record CommentThread(Guid Id, string Quote, bool Anchored, ImmutableArray<CommentMessage> Messages, bool WholeBlock = false);
public sealed record CommentSpan(Guid ThreadId, Guid NodeId, int Start, int Length);
public sealed record CommentAnchorRange(Guid NodeId, string Path, int Start, int Length, string BlockText);
public sealed record CommentAnchor(Guid RootId, string Signature, string Quote, ImmutableArray<CommentAnchorRange> Ranges, bool WholeBlock = false);

// Note: 讨论元数据与文字锚点同属文档事务，见 .agents/notes/implemented/feature/2026-09-11-native-comments.md
public sealed class NoteComments
{
    public const string Attribute = "writemeComments";
    public const string MarkType = "comment";
    public const string BlockAttribute = "writemeCommentIds";
    public const int MaxMessageLength = 20_000;
    public const int MaxThreads = 1000;
    public const int MaxMessages = 10_000;
    private sealed record Catalog(int Version, ImmutableArray<CommentThread> Threads);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly ConditionalWeakTable<NoteNode, NoteComments> Cache = new();
    private readonly Dictionary<Guid, CommentThread> _threads = [];
    private readonly Dictionary<Guid, ImmutableArray<CommentSpan>> _spans = [];
    private readonly Dictionary<Guid, ImmutableArray<CommentThread>> _blocks = [];
    public ImmutableArray<CommentThread> Threads { get; private set; } = [];
    public string? Error { get; private set; }
    public bool UnsupportedVersion { get; private set; }
    public bool CanEdit => Error == null;
    public int MessageCount => Threads.Sum(thread => thread.Messages.Count(message => !message.Deleted));
    public static NoteComments For(NoteNode root) => Cache.GetValue(root, node => new(node));
    public CommentThread? Find(Guid id) => _threads.GetValueOrDefault(id);
    public ImmutableArray<CommentSpan> Spans(Guid id) => _spans.GetValueOrDefault(id, []);
    public ImmutableArray<CommentThread> InBlock(Guid nodeId) => _blocks.GetValueOrDefault(nodeId, []);

    private NoteComments(NoteNode root)
    {
        if (!root.Attrs.TryGetValue(Attribute, out var data)) return;
        try
        {
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number))
                throw new InvalidDataException("评论数据格式不完整，原始数据已保留。");
            if (number != 1)
            {
                UnsupportedVersion = true;
                Error = "此文档使用较新的评论格式，请更新 WriteME 后查看。";
                return;
            }
            var catalog = data.Deserialize<Catalog>(JsonOptions);
            if (catalog == null || catalog.Threads.IsDefault || catalog.Threads.Length > MaxThreads)
                throw new InvalidDataException("评论列表无效或超过数量上限。");
            var messageIds = new HashSet<Guid>();
            foreach (var thread in catalog.Threads)
            {
                if (thread == null || thread.Id == Guid.Empty || !_threads.TryAdd(thread.Id, thread)
                    || thread.Quote == null || thread.Quote.Length > 2000 || thread.Messages.IsDefaultOrEmpty || thread.WholeBlock && !thread.Anchored)
                    throw new InvalidDataException("评论讨论标识或引用无效。");
                var previous = new HashSet<Guid>();
                foreach (var message in thread.Messages)
                {
                    if (message == null || message.Id == Guid.Empty || !messageIds.Add(message.Id) || messageIds.Count > MaxMessages
                        || string.IsNullOrWhiteSpace(message.Author) || message.Author.Length > 100
                        || message.Text == null || (!message.Deleted && string.IsNullOrWhiteSpace(message.Text)) || message.Text.Length > MaxMessageLength
                        || message.Deleted && message.Text.Length != 0
                        || previous.Count == 0 && (message.ReplyTo != null || message.Deleted)
                        || message.ReplyTo is { } parent && !previous.Contains(parent)
                        || message.CreatedAt <= 0 || message.CreatedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()
                        || message.EditedAt is <= 0 || message.EditedAt > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                        throw new InvalidDataException("评论内容、时间或消息标识无效。");
                    previous.Add(message.Id);
                }
            }
            Threads = catalog.Threads;
            var spans = new Dictionary<Guid, List<CommentSpan>>();
            foreach (var block in Blocks(root))
            {
                foreach (var id in BlockIds(block).Where(id => Find(id) is { WholeBlock: true }))
                {
                    if (spans.ContainsKey(id)) throw new InvalidDataException("同一段落讨论不能绑定多个段落。");
                    spans[id] = [new(id, block.Id, 0, block.IsTextBlock ? RichText.Plain(block).Length : 0)];
                }
                if (!block.IsTextBlock) continue;
                var offset = 0;
                foreach (var run in block.Content)
                {
                    var length = RichText.Length(run);
                    if (length == 0) continue;
                    foreach (var id in Ids(run.Marks).Where(id => Find(id) is { Anchored: true, WholeBlock: false }))
                    {
                        if (!spans.TryGetValue(id, out var list)) spans[id] = list = [];
                        if (list.Count > 0 && list[^1].NodeId == block.Id && list[^1].Start + list[^1].Length == offset)
                            list[^1] = list[^1] with { Length = list[^1].Length + length };
                        else list.Add(new(id, block.Id, offset, length));
                    }
                    offset += length;
                }
            }
            foreach (var (id, ranges) in spans) _spans[id] = [.. ranges];
            foreach (var group in spans.SelectMany(pair => pair.Value).GroupBy(span => span.NodeId))
                _blocks[group.Key] = [.. group.Select(span => span.ThreadId).Distinct().Select(id => _threads[id])];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            Error = "无法读取评论，原始数据已保留。";
            Threads = []; _threads.Clear(); _spans.Clear(); _blocks.Clear();
        }
    }

    public static void Validate(NoteNode root)
    {
        var index = For(root);
        if (index.Error != null && !index.UnsupportedVersion) throw new InvalidDataException(index.Error);
        foreach (var mark in NoteTree.Descendants(root).SelectMany(node => node.Marks).Where(mark => mark.Type == MarkType))
            if (!mark.Attrs.TryGetValue("ids", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > MaxThreads
                || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var guid) || guid == Guid.Empty))
                throw new InvalidDataException("文字批注标识无效。");
        foreach (var node in NoteTree.Descendants(root).Where(node => node.Attrs.ContainsKey(BlockAttribute)))
        {
            var ids = node.Attrs[BlockAttribute];
            if (node.Type is "doc" or "text" or "hardBreak" || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > MaxThreads
                || ids.EnumerateArray().Any(id => id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var guid) || guid == Guid.Empty)
                || BlockIds(node).Length != ids.GetArrayLength())
                throw new InvalidDataException("段落评论标识无效。");
        }
    }

    internal static NoteNode Write(NoteNode root, ImmutableArray<CommentThread> threads) => threads.IsEmpty
        ? root with { Attrs = root.Attrs.Remove(Attribute) }
        : root with { Attrs = root.Attrs.SetItem(Attribute, JsonSerializer.SerializeToElement(new Catalog(1, threads), JsonOptions)) };

    public static ImmutableArray<Guid> Ids(ImmutableArray<NoteMark> marks)
    {
        var ids = ImmutableArray.CreateBuilder<Guid>();
        foreach (var mark in marks.Where(mark => mark.Type == MarkType))
            if (mark.Attrs.TryGetValue("ids", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var value in values.EnumerateArray())
                    if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty && !ids.Contains(id)) ids.Add(id);
        return ids.ToImmutable();
    }

    internal static ImmutableArray<NoteMark> WithIds(ImmutableArray<NoteMark> marks, IEnumerable<Guid> values)
    {
        var ids = values.Distinct().Order().ToArray();
        var plain = marks.Where(mark => mark.Type != MarkType).ToImmutableArray();
        return ids.Length == 0 ? plain : plain.Add(new(MarkType) { Attrs = ImmutableDictionary<string, JsonElement>.Empty.Add("ids", JsonSerializer.SerializeToElement(ids)) });
    }

    public static ImmutableArray<Guid> BlockIds(NoteNode node) => node.Attrs.TryGetValue(BlockAttribute, out var value) && value.ValueKind == JsonValueKind.Array
        ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String && item.TryGetGuid(out var id) && id != Guid.Empty).Select(item => item.GetGuid()).Distinct()]
        : [];

    internal static NoteNode WithBlockIds(NoteNode node, IEnumerable<Guid> values)
    {
        var ids = values.Distinct().Order().ToArray();
        return node with { Attrs = ids.Length == 0 ? node.Attrs.Remove(BlockAttribute) : node.Attrs.SetItem(BlockAttribute, JsonSerializer.SerializeToElement(ids)) };
    }

    internal static NoteNode MergeBlockAnchors(NoteNode target, NoteNode source) => BlockIds(source).IsEmpty ? target
        : WithBlockIds(target, BlockIds(target).Concat(BlockIds(source)));

    public static string BlockQuote(NoteNode node) => Abbreviate(node.IsTextBlock ? RichText.Plain(node) is { Length: > 0 } text ? text.Replace('\u2028', '\n') : "空白段落"
        : node.Type switch { "table" => "表格", "columnList" => "分栏", "horizontalRule" => "分割线", "image" => node.String("alt") ?? "图片", "attachment" => node.String("name") ?? "附件", _ => "内容块" }, 2000);

    // Only text strictly inside the surviving anchor inherits it. A format override must not
    // resurrect a deleted annotation or extend it at either boundary.
    internal static ImmutableArray<NoteMark> InsertionMarks(NoteNode block, int offset, int removed, ImmutableArray<NoteMark> styles)
    {
        ImmutableArray<Guid> At(int position) => Ids(RichText.Slice(block.Content, position, 1).FirstOrDefault()?.Marks ?? []);
        var length = RichText.Plain(block).Length;
        var ids = offset > 0 && offset + removed < length ? At(offset - 1).Intersect(At(offset + removed)) : [];
        return WithIds(styles, ids);
    }

    internal static NoteNode MarkRange(NoteNode block, int start, int length, Guid id)
    {
        var total = RichText.Plain(block).Length;
        var selected = RichText.Slice(block.Content, start, length).Select(run => run with { Marks = WithIds(run.Marks, Ids(run.Marks).Add(id)) });
        return block with { Content = RichText.Compact([.. RichText.Slice(block.Content, 0, start), .. selected, .. RichText.Slice(block.Content, start + length, total - start - length)]) };
    }

    internal static NoteNode RemoveMarks(NoteNode root, Guid? id = null)
    {
        var children = root.Content.Select(child => RemoveMarks(child, id)).ToImmutableArray();
        var marks = root.Marks;
        if (marks.Any(mark => mark.Type == MarkType)) marks = WithIds(marks, id.HasValue ? Ids(marks).Where(value => value != id) : []);
        var result = children.SequenceEqual(root.Content) && RichText.SameMarks(marks, root.Marks) ? root : root with { Content = children, Marks = marks };
        return !root.Attrs.ContainsKey(BlockAttribute) ? result : WithBlockIds(result, id.HasValue ? BlockIds(root).Where(value => value != id) : []);
    }

    private static string Signature(NoteNode root) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NoteJson.Serialize(root))));

    private static IEnumerable<NoteNode> Blocks(NoteNode node)
    {
        yield return node;
        if (node.IsTextBlock) yield break;
        foreach (var child in node.Content)
            foreach (var block in Blocks(child)) yield return block;
    }

    public static CommentAnchor? Capture(DocumentSession session, int start, int length)
    {
        if (!session.IsScopeAttached || length <= 0 || start < 0 || start > session.Projection.Text.Length - length) return null;
        var root = session.HistoryOwner.Root;
        var paths = new Dictionary<Guid, string>();
        void Visit(NoteNode node, string path)
        {
            paths[node.Id] = path;
            for (var i = 0; i < node.Content.Length; i++) Visit(node.Content[i], path.Length == 0 ? i.ToString() : $"{path}/{i}");
        }
        Visit(root, "");
        var ranges = ImmutableArray.CreateBuilder<CommentAnchorRange>();
        var quotes = new List<string>();
        foreach (var row in session.Projection.Rows.Where(row => row.End > start && row.Start < start + length))
        {
            if (row.IsAtomic) return null;
            var from = Math.Max(0, start - row.Start);
            var count = Math.Min(row.End, start + length) - row.Start - from;
            if (count <= 0 || !paths.TryGetValue(row.Node.Id, out var path)) continue;
            ranges.Add(new(row.Node.Id, path, from, count, row.Text));
            quotes.Add(row.Text.Substring(from, count).Replace('\u2028', '\n'));
        }
        if (ranges.Count == 0) return null;
        var quote = string.Join('\n', quotes);
        if (quote.Length > 2000) quote = Abbreviate(quote, 2000);
        return new(root.Id, Signature(root), quote, ranges.ToImmutable());
    }

    public static CommentAnchor? CaptureBlock(DocumentSession session, Guid nodeId)
    {
        if (!session.IsScopeAttached || session.Projection.Find(nodeId) is not { } row) return null;
        var root = session.HistoryOwner.Root;
        string? Path(NoteNode node, string path)
        {
            if (node.Id == nodeId) return path;
            for (var i = 0; i < node.Content.Length; i++)
                if (Path(node.Content[i], path.Length == 0 ? i.ToString() : $"{path}/{i}") is { } found) return found;
            return null;
        }
        if (Path(root, "") is not { } path) return null;
        return new(root.Id, Signature(root), BlockQuote(row.Node), [new(nodeId, path, 0, row.IsAtomic ? 0 : row.Text.Length, row.Text)], WholeBlock: true);
    }

    public static ImmutableArray<CommentAnchorRange> Resolve(NoteNode root, CommentAnchor anchor)
    {
        var sameRoot = root.Id == anchor.RootId;
        if (!sameRoot && Signature(root) != anchor.Signature) return [];
        if (anchor.WholeBlock && anchor.Ranges.Length != 1) return [];
        // Rich-text slices may share transient inline IDs; only block/container IDs address anchors.
        var nodes = sameRoot ? Blocks(root).ToDictionary(node => node.Id) : null;
        var result = ImmutableArray.CreateBuilder<CommentAnchorRange>();
        foreach (var range in anchor.Ranges)
        {
            var node = sameRoot ? nodes!.GetValueOrDefault(range.NodeId) : NoteReferences.AtPath(root, range.Path);
            if (anchor.WholeBlock)
            {
                if (node == null || node.Type is "doc" or "text" or "hardBreak") return [];
                var text = node.IsTextBlock ? RichText.Plain(node) : "";
                result.Add(range with { NodeId = node.Id, Start = 0, Length = text.Length, BlockText = text });
                continue;
            }
            if (node?.IsTextBlock != true || RichText.Plain(node) != range.BlockText || range.Start < 0 || range.Length <= 0
                || range.Start > range.BlockText.Length - range.Length) return [];
            result.Add(range with { NodeId = node.Id });
        }
        return result.ToImmutable();
    }

    public static string Abbreviate(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        var length = Math.Max(0, maximum - 1);
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length] + "…";
    }

    public string Summary() => Error ?? (Threads.IsEmpty ? "暂无评论" : string.Join("\n\n", Threads.Select(thread =>
    {
        var messages = thread.Messages.ToDictionary(message => message.Id);
        return $"{(thread.Anchored ? (thread.WholeBlock ? "段落：" : "引用：") + thread.Quote : "文档评论")}\n" +
            string.Join("\n", thread.Messages.Select(message =>
            {
                if (message.Deleted) return "[这条回复已删除]";
                var parent = message.Id == thread.Messages[0].Id ? null : messages.GetValueOrDefault(message.ReplyTo ?? thread.Messages[0].Id);
                return $"{message.Author}{(parent != null ? " 回复「" + (parent.Deleted ? "已删除的回复" : Abbreviate(parent.Text, 60)) + "」" : "")}：{message.Text}";
            }));
    })));

    public static CommentMessage? ParentMessage(CommentThread thread, CommentMessage message) => message.Id == thread.Messages[0].Id ? null
        : thread.Messages.FirstOrDefault(parent => parent.Id == (message.ReplyTo ?? thread.Messages[0].Id));

    internal static bool Equivalent(CommentThread left, CommentThread right) => left.Id == right.Id && left.Quote == right.Quote
        && left.Anchored == right.Anchored && left.WholeBlock == right.WholeBlock && left.Messages.SequenceEqual(right.Messages);
}
