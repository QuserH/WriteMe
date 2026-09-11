using System.Collections.Immutable;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YDotNet.Infrastructure;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;
using YDotNet.Document.StickyIndexes;
using YDotNet.Document.Transactions;
using YDotNet.Document.Types.Maps;
using YDotNet.Document.UndoManagers;
using YArray = YDotNet.Document.Types.Arrays.Array;
using YText = YDotNet.Document.Types.Texts.Text;

namespace WriteMe.Core;

public sealed record SharedDocumentSnapshot(string Title, NoteNode Root);
public sealed record SharedThreadHeader(Guid Id, string Quote, bool Anchored, bool WholeBlock, Guid FirstMessageId);

// Note: 稳定块对象、文字 CRDT 与逐条评论 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed class SharedDocumentReplica : IDisposable
{
    public const string IdAttribute = "writemeId";
    public const int MaximumStateBytes = 16 * 1024 * 1024;
    public const int MaximumBlocks = 50_000;
    private static readonly Guid RootId = new("00000000-0000-0000-0000-000000000001");
    private static readonly byte[] LocalOrigin = [1];
    private static readonly byte[] RemoteOrigin = [2];
    // YDotNet 0.6.0's default generator has a truncated XOR mask; match Yjs's full-entropy uint32 client IDs.
    private readonly Doc _doc = new(new DocOptions { Encoding = DocEncoding.Utf16,
        Id = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) });
    private readonly Map _blocks;
    private readonly Map _threads;
    private readonly Map _meta;
    private readonly YText _title;
    private readonly UndoManager _undo;

    public SharedDocumentReplica(byte[]? state = null)
    {
        _blocks = _doc.Map("blocks"); _threads = _doc.Map("threads"); _meta = _doc.Map("meta"); _title = _doc.Text("title");
        _undo = new(_doc, _blocks, new() { CaptureTimeoutMilliseconds = 750 });
        _undo.AddScope(_threads); _undo.AddScope(_title); _undo.AddOrigin(LocalOrigin);
        try { if (state is { Length: > 0 }) Apply(state); }
        catch { Dispose(); throw; }
    }

    public bool CanUndo => _undo.CanUndo();
    public bool CanRedo => _undo.CanRedo();
    public void BreakHistory() => _undo.Stop();
    public byte[] State() { using var t = _doc.ReadTransaction(); return t.StateDiffV1(null); }
    public byte[] StateVector() { using var t = _doc.ReadTransaction(); return t.StateVectorV1(); }
    public byte[] Difference(byte[]? vector) { using var t = _doc.ReadTransaction(); return t.StateDiffV1(vector); }
    public void Apply(byte[] update)
    {
        if (update.Length is 0 or > MaximumStateBytes) throw new InvalidDataException("协同更新为空或超过 16 MB。");
        using var t = _doc.WriteTransaction(RemoteOrigin);
        if (t.ApplyV1(update) != TransactionUpdateResult.Ok) throw new InvalidDataException("无法读取协同更新。");
    }
    public byte[] Undo() { var vector = StateVector(); return _undo.Undo() ? Difference(vector) : []; }
    public byte[] Redo() { var vector = StateVector(); return _undo.Redo() ? Difference(vector) : []; }

    private static Dictionary<string, Output> Entries(Map map, Transaction t)
    { using var entries = map.Iterate(t); return entries.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal); }
    private static string? String(Map map, Transaction t, string key) => map.Get(t, key) is { Tag: OutputTag.String } value ? value.String : null;
    private static Map ChildMap(Map map, Transaction t, string key)
    {
        if (map.Get(t, key) is { Tag: OutputTag.Map } existing) return existing.Map;
        using var input = Input.Map(new Dictionary<string, Input>()); map.Insert(t, key, input); return map.Get(t, key)!.Map;
    }
    private static YArray ChildArray(Map map, Transaction t, string key)
    {
        if (map.Get(t, key) is { Tag: OutputTag.Array } existing) return existing.Array;
        using var input = Input.Array([]); map.Insert(t, key, input); return map.Get(t, key)!.Array;
    }
    private static YText ChildText(Map map, Transaction t, string key)
    {
        if (map.Get(t, key) is { Tag: OutputTag.Text } existing) return existing.Text;
        using var input = Input.Text(""); map.Insert(t, key, input); return map.Get(t, key)!.Text;
    }
    private static bool Put(Map map, Transaction t, string key, string value)
    {
        if (String(map, t, key) == value) return false;
        using var input = Input.String(value); map.Insert(t, key, input); return true;
    }
    private static bool ReplaceMap(Map map, Transaction t, IReadOnlyDictionary<string, string> values)
    {
        var changed = false;
        foreach (var key in Entries(map, t).Keys.Where(key => !values.ContainsKey(key))) changed |= map.Remove(t, key);
        foreach (var (key, value) in values) changed |= Put(map, t, key, value);
        return changed;
    }
    private static string[] ReadIds(YArray array, Transaction t)
    {
        if (array.Length(t) > MaximumBlocks * 2) throw new InvalidDataException("共享块顺序超过上限。");
        return Enumerable.Range(0, checked((int)array.Length(t))).Select(index => array.Get(t, (uint)index))
            .Select(item => item?.Tag == OutputTag.String ? item.String : throw new InvalidDataException("共享块顺序无效。")).ToArray();
    }
    private static bool ReplaceIds(YArray array, Transaction t, string[] next)
    {
        var old = ReadIds(array, t); var prefix = 0;
        while (prefix < old.Length && prefix < next.Length && old[prefix] == next[prefix]) prefix++;
        var suffix = 0;
        while (suffix < old.Length - prefix && suffix < next.Length - prefix && old[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        if (prefix == old.Length && prefix == next.Length) return false;
        if (old.Length - prefix - suffix > 0) array.RemoveRange(t, (uint)prefix, (uint)(old.Length - prefix - suffix));
        var cells = next.Skip(prefix).Take(next.Length - prefix - suffix).Select(Input.String).ToArray();
        try { if (cells.Length > 0) array.InsertRange(t, (uint)prefix, cells); }
        finally { foreach (var cell in cells) cell.Dispose(); }
        return true;
    }

    private static (int Start, int OldLength, string Insert) TextChange(string old, string next)
    {
        var prefix = 0; while (prefix < old.Length && prefix < next.Length && old[prefix] == next[prefix]) prefix++;
        if (prefix > 0 && prefix < old.Length && char.IsHighSurrogate(old[prefix - 1]) && char.IsLowSurrogate(old[prefix])) prefix--;
        var suffix = 0;
        while (suffix < old.Length - prefix && suffix < next.Length - prefix && old[^(suffix + 1)] == next[^(suffix + 1)]) suffix++;
        if (suffix > 0 && suffix < old.Length && char.IsLowSurrogate(old[^suffix]) && char.IsHighSurrogate(old[^(suffix + 1)])) suffix--;
        return (prefix, old.Length - prefix - suffix, next.Substring(prefix, next.Length - prefix - suffix));
    }
    private static bool ReplaceText(YText text, Transaction t, string next)
    {
        var old = text.String(t); if (old == next) return false;
        var change = TextChange(old, next);
        if (change.OldLength > 0) text.RemoveRange(t, (uint)change.Start, (uint)change.OldLength);
        if (change.Insert.Length > 0) text.Insert(t, (uint)change.Start, change.Insert);
        return true;
    }
    private static Dictionary<string, string> RunAttributes(NoteNode run)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mark in run.Marks)
        {
            if (mark.Type == NoteComments.MarkType)
            { foreach (var id in NoteComments.Ids([mark])) result["c:" + id.ToString("D")] = "true"; }
            else result["m:" + mark.Type] = JsonSerializer.Serialize(new { attrs = mark.Attrs, extra = mark.Extra }, SyncProtocol.Json);
        }
        if (run.Type is not ("text" or "hardBreak")) result["inline"] = NoteJson.Serialize(run);
        return result;
    }
    private static bool ReplaceRichText(YText text, Transaction t, NoteNode node)
    {
        var changed = ReplaceText(text, t, RichText.Plain(node));
        var actual = new List<(int Start, int End, Dictionary<string, string> Attributes)>(); var offset = 0;
        foreach (var chunk in text.Chunks(t))
        {
            if (chunk.Data.Tag != OutputTag.String) throw new InvalidDataException("协同文字含不支持的嵌入。");
            var length = chunk.Data.String.Length;
            actual.Add((offset, offset + length, chunk.Attributes.Where(pair => pair.Value.Tag == OutputTag.String).ToDictionary(pair => pair.Key, pair => pair.Value.String)));
            offset += length;
        }
        offset = 0;
        foreach (var run in node.Content)
        {
            var end = offset + RichText.Length(run); var desired = RunAttributes(run);
            foreach (var current in actual.Where(item => item.End > offset && item.Start < end))
            {
                var delta = new Dictionary<string, Input>();
                foreach (var key in current.Attributes.Keys.Where(key => !desired.ContainsKey(key))) delta[key] = Input.Null();
                foreach (var (key, value) in desired) if (current.Attributes.GetValueOrDefault(key) != value) delta[key] = Input.String(value);
                if (delta.Count == 0) continue;
                try
                {
                    using var attributes = Input.Object(delta);
                    var start = Math.Max(offset, current.Start); var length = Math.Min(end, current.End) - start;
                    text.Format(t, (uint)start, (uint)length, attributes); changed = true;
                }
                finally { foreach (var value in delta.Values) value.Dispose(); }
            }
            offset = end;
        }
        return changed;
    }

    public byte[] Write(NoteNode root, string? title = null, bool trackHistory = true)
    {
        // Yrs transactions commit on disposal, so reject invalid local input before starting one.
        ValidateTree(root, title);
        var comments = NoteComments.For(root);
        var vector = StateVector(); var changed = false;
        using (var t = _doc.WriteTransaction(trackHistory ? LocalOrigin : RemoteOrigin))
        {
            changed |= Put(_meta, t, "version", "1");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Walk(NoteNode node, string key, string parent, int depth)
            {
                if (depth > 80 || visited.Count >= MaximumBlocks || !visited.Add(key)) throw new InvalidDataException("共享文档层级或块身份无效。");
                var map = ChildMap(_blocks, t, key);
                changed |= Put(map, t, "type", node.Type); changed |= Put(map, t, "parent", parent); changed |= Put(map, t, "deleted", "false");
                changed |= ReplaceMap(ChildMap(map, t, "attrs"), t, node.Attrs.Where(pair => pair.Key is not (IdAttribute or NoteComments.Attribute or NoteComments.BlockAttribute)).ToDictionary(pair => pair.Key, pair => pair.Value.GetRawText()));
                changed |= ReplaceMap(ChildMap(map, t, "anchors"), t, NoteComments.BlockIds(node).ToDictionary(id => id.ToString("D"), _ => "true"));
                changed |= Put(map, t, "extra", JsonSerializer.Serialize(node.Extra, SyncProtocol.Json));
                if (node.IsTextBlock) changed |= ReplaceRichText(ChildText(map, t, "text"), t, node);
                else if (node.IsContentContainer)
                {
                    changed |= ReplaceIds(ChildArray(map, t, "children"), t, node.Content.Select(child => child.Id.ToString("D")).ToArray());
                    foreach (var child in node.Content) Walk(child, child.Id.ToString("D"), key, depth + 1);
                }
                else changed |= Put(map, t, "raw", NoteJson.Serialize(node));
            }
            Walk(root, "root", "", 0);
            foreach (var (key, value) in Entries(_blocks, t))
                if (value.Tag == OutputTag.Map && !visited.Contains(key)) changed |= Put(value.Map, t, "deleted", "true");
            var threadIds = comments.Threads.Select(thread => thread.Id.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var key in Entries(_threads, t).Keys.Where(key => !threadIds.Contains(key))) changed |= _threads.Remove(t, key);
            foreach (var thread in comments.Threads)
            {
                var map = ChildMap(_threads, t, thread.Id.ToString("D"));
                changed |= Put(map, t, "info", JsonSerializer.Serialize(new SharedThreadHeader(thread.Id, thread.Quote, thread.Anchored, thread.WholeBlock, thread.Messages[0].Id), SyncProtocol.Json));
                changed |= ReplaceMap(ChildMap(map, t, "messages"), t, thread.Messages.ToDictionary(message => message.Id.ToString("D"), message => JsonSerializer.Serialize(message, SyncProtocol.Json)));
            }
            if (title != null) changed |= ReplaceText(_title, t, title);
        }
        return changed ? Difference(vector) : [];
    }

    public static void ValidateTree(NoteNode root, string? title = null)
    {
        if (root.Type != "doc" || title?.Length > 500) throw new InvalidDataException("共享文档根节点或标题无效。");
        var ids = new HashSet<Guid>(); var characters = 0;
        void Visit(NoteNode node, int depth)
        {
            if (depth > 80 || !ids.Add(node.Id) || node.Id == Guid.Empty || ids.Count > MaximumBlocks || node.Type.Length is 0 or > 100)
                throw new InvalidDataException("共享文档层级或块身份无效。");
            if (node.IsTextBlock) characters += RichText.Plain(node).Length;
            if (characters > 4_000_000) throw new InvalidDataException("共享文档文字过长。");
            if (node.IsContentContainer) foreach (var child in node.Content) Visit(child, depth + 1);
        }
        Visit(root, 0); NoteComments.Validate(root);
    }

    public SharedDocumentSnapshot Read()
    {
        try { return ReadCore(); }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { throw new InvalidDataException("共享文档包含无效的块、文字格式或评论。", e); }
    }
    private SharedDocumentSnapshot ReadCore()
    {
        using var t = _doc.ReadTransaction();
        if (String(_meta, t, "version") != "1") throw new InvalidDataException("共享文档协议版本不受支持。");
        var all = Entries(_blocks, t);
        if (all.Count > MaximumBlocks || !all.TryGetValue("root", out var rootValue) || rootValue.Tag != OutputTag.Map) throw new InvalidDataException("共享文档尚未初始化或超过块数量上限。");
        var storedCharacters = 0;
        foreach (var (key, value) in all)
        {
            if (value.Tag != OutputTag.Map || key != "root" && (!Guid.TryParseExact(key, "D", out var id) || id == Guid.Empty || id.ToString("D") != key))
                throw new InvalidDataException("共享文档块目录无效。");
            var map = value.Map;
            if (String(map, t, "type") is not { Length: > 0 and <= 100 } || String(map, t, "deleted") is not ("true" or "false")
                || String(map, t, "parent") is not { } parent || (key == "root" ? parent != "" : parent != "root" && !Guid.TryParseExact(parent, "D", out _)))
                throw new InvalidDataException("共享块类型或归属无效。");
            if (map.Get(t, "attrs") is not { Tag: OutputTag.Map } attrs || map.Get(t, "anchors") is not { Tag: OutputTag.Map } anchors)
                throw new InvalidDataException("共享块属性无效。");
            _ = ReadAttributes(attrs.Map, t);
            _ = JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(String(map, t, "extra") ?? "", SyncProtocol.Json)
                ?? throw new InvalidDataException("共享扩展属性无效。");
            foreach (var (anchor, flag) in Entries(anchors.Map, t))
                if (!Guid.TryParseExact(anchor, "D", out var anchorId) || anchorId == Guid.Empty || flag.Tag != OutputTag.String || flag.String != "true")
                    throw new InvalidDataException("段落评论标识无效。");
            if (map.Get(t, "children") is { } children)
            {
                if (children.Tag != OutputTag.Array || ReadIds(children.Array, t).Any(child => !Guid.TryParseExact(child, "D", out var childId) || childId == Guid.Empty))
                    throw new InvalidDataException("共享块顺序无效。");
            }
            if (map.Get(t, "text") is { } text)
            {
                if (text.Tag != OutputTag.Text) throw new InvalidDataException("共享文字类型无效。");
                foreach (var chunk in text.Text.Chunks(t))
                {
                    if (chunk.Data.Tag != OutputTag.String || (storedCharacters += chunk.Data.String.Length) > 4_000_000)
                        throw new InvalidDataException("共享文档文字无效或超过上限。");
                    _ = ReadMarks(chunk.Attributes);
                }
            }
            if (map.Get(t, "raw") is { } raw)
            {
                if (raw.Tag != OutputTag.String) throw new InvalidDataException("保留块无效。");
                using var json = JsonDocument.Parse(raw.String);
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("保留块无效。");
            }
        }
        var live = all.Where(pair => pair.Value.Tag == OutputTag.Map && String(pair.Value.Map, t, "deleted") != "true").ToDictionary(pair => pair.Key, pair => pair.Value.Map);
        if (all.Any(pair => pair.Value.Tag != OutputTag.Map || pair.Key != "root" && !Guid.TryParseExact(pair.Key, "D", out _))
            || !live.ContainsKey("root") || String(rootValue.Map, t, "type") != "doc") throw new InvalidDataException("共享文档块目录无效。");
        var parents = live.Keys.Where(key => key != "root").ToDictionary(key => key, key => String(live[key], t, "parent") ?? "root");
        foreach (var key in parents.Keys.ToArray())
        {
            var chain = new List<string>(); var current = key;
            while (parents.TryGetValue(current, out var parent) && parent != "root")
            {
                if (chain.Contains(current)) { parents[chain.Skip(chain.IndexOf(current)).Min(StringComparer.Ordinal)!] = "root"; break; }
                if (!live.ContainsKey(parent)) break; // Children of a deleted parent remain deleted, without deleting their CRDT text.
                if (chain.Count > 80) throw new InvalidDataException("共享文档层级超过上限。");
                chain.Add(current); current = parent;
            }
        }
        var visited = new HashSet<string>(); var characters = 0;
        NoteNode Node(string key, int depth)
        {
            if (depth > 80 || !visited.Add(key) || key != "root" && !Guid.TryParse(key, out _)) throw new InvalidDataException("共享文档块身份或层级无效。");
            var map = live[key]; var type = String(map, t, "type") ?? throw new InvalidDataException("共享块类型缺失。");
            if (type.Length > 100) throw new InvalidDataException("共享块类型无效。");
            var attrs = map.Get(t, "attrs") is { Tag: OutputTag.Map } attrMap ? ReadAttributes(attrMap.Map, t) : ImmutableDictionary<string, JsonElement>.Empty;
            var extra = JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(String(map, t, "extra") ?? "{}", SyncProtocol.Json) ?? ImmutableDictionary<string, JsonElement>.Empty;
            var node = new NoteNode(type) { Id = key == "root" ? RootId : Guid.Parse(key), Attrs = attrs, Extra = extra };
            if (key != "root") node = node.WithAttr(IdAttribute, key);
            if (map.Get(t, "anchors") is { Tag: OutputTag.Map } anchors)
                node = NoteComments.WithBlockIds(node, Entries(anchors.Map, t).Where(pair => pair.Value.Tag == OutputTag.String && pair.Value.String == "true").Select(pair => Guid.Parse(pair.Key)).ToImmutableArray());
            if (node.IsTextBlock)
            {
                var runs = ImmutableArray.CreateBuilder<NoteNode>();
                if (map.Get(t, "text") is { Tag: OutputTag.Text } text)
                    foreach (var chunk in text.Text.Chunks(t))
                    {
                        if (chunk.Data.Tag != OutputTag.String) throw new InvalidDataException("协同文字格式无效。");
                        var value = chunk.Data.String; characters += value.Length;
                        if (characters > 4_000_000) throw new InvalidDataException("共享文档文字过长。");
                        var marks = ReadMarks(chunk.Attributes);
                        if (chunk.Attributes.TryGetValue("inline", out var inline) && inline.Tag == OutputTag.String)
                        {
                            var inlineNode = NoteJson.Parse("{\"type\":\"doc\",\"content\":[" + inline.String + "]}").Content[0];
                            for (var i = 0; i < value.Length; i++) runs.Add(inlineNode with { Id = Guid.NewGuid(), Marks = marks });
                            continue;
                        }
                        var parts = value.Split('\u2028');
                        for (var i = 0; i < parts.Length; i++)
                        {
                            if (parts[i].Length > 0) runs.Add(new("text") { Text = parts[i], Marks = marks });
                            if (i < parts.Length - 1) runs.Add(new("hardBreak") { Marks = marks });
                        }
                    }
                node = node with { Content = runs.ToImmutable() };
            }
            else if (node.IsContentContainer)
            {
                var ordered = map.Get(t, "children") is { Tag: OutputTag.Array } children ? ReadIds(children.Array, t) : [];
                var ids = ordered.Concat(parents.Where(pair => pair.Value == key).Select(pair => pair.Key).Order(StringComparer.Ordinal))
                    .Where(id => id != key && live.ContainsKey(id) && parents.GetValueOrDefault(id) == key).Distinct().ToArray();
                node = node with { Content = ids.Select(id => Node(id, depth + 1)).ToImmutableArray() };
            }
            else if (String(map, t, "raw") is { } raw)
            {
                var preserved = NoteJson.Parse("{\"type\":\"doc\",\"content\":[" + raw + "]}").Content[0];
                node = preserved with { Id = node.Id, Attrs = node.Attrs, Extra = node.Extra };
            }
            if (node.IsContentContainer)
            {
                var requiresParagraph = node.Content.IsEmpty && node.Type is "doc" or "toggleBlock" or "listItem" or "taskItem" or "tableCell" or "tableHeader" or "column"
                    || node.Type is "toggleBlock" or "listItem" or "taskItem" && node.Content.Length > 0 && node.Content[0].Type != "paragraph";
                if (requiresParagraph)
                {
                    var id = PlaceholderId(key);
                    node = node with { Content = node.Content.Insert(0, (NoteNode.Paragraph() with { Id = id }).WithAttr(IdAttribute, id.ToString("D"))) };
                }
            }
            return node;
        }
        var root = Node("root", 0);
        var threads = new List<CommentThread>();
        var threadEntries = Entries(_threads, t); var messageCount = 0;
        if (threadEntries.Count > NoteComments.MaxThreads) throw new InvalidDataException("评论过多。");
        foreach (var (threadKey, value) in threadEntries)
        {
            if (value.Tag != OutputTag.Map) throw new InvalidDataException("评论目录无效。");
            var info = JsonSerializer.Deserialize<SharedThreadHeader>(String(value.Map, t, "info") ?? "", SyncProtocol.Json) ?? throw new InvalidDataException("评论信息无效。");
            if (info.Id.ToString("D") != threadKey) throw new InvalidDataException("评论身份无效。");
            if (value.Map.Get(t, "messages") is not { Tag: OutputTag.Map } messages) throw new InvalidDataException("评论消息缺失。");
            var messageEntries = Entries(messages.Map, t); messageCount += messageEntries.Count;
            if (messageCount > NoteComments.MaxMessages) throw new InvalidDataException("评论消息过多。");
            var remaining = messageEntries.Select(pair => {
                var message = pair.Value.Tag == OutputTag.String ? JsonSerializer.Deserialize<CommentMessage>(pair.Value.String, SyncProtocol.Json) : null;
                return message != null && message.Id.ToString("D") == pair.Key ? message : throw new InvalidDataException("评论消息身份无效。");
            })
                .OrderBy(message => message.CreatedAt).ThenBy(message => message.Id).ToList();
            var first = remaining.FirstOrDefault(message => message.Id == info.FirstMessageId) ?? throw new InvalidDataException("首条评论缺失。");
            var ordered = new List<CommentMessage> { first }; remaining.Remove(first); var seen = new HashSet<Guid> { first.Id };
            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(message => message.ReplyTo is { } parent && seen.Contains(parent)) ?? throw new InvalidDataException("回复父消息无效。");
                ordered.Add(next); seen.Add(next.Id); remaining.Remove(next);
            }
            threads.Add(new(info.Id, info.Quote, info.Anchored, ordered.ToImmutableArray(), info.WholeBlock));
        }
        root = NoteComments.Write(root, threads.OrderBy(thread => thread.Messages[0].CreatedAt).ThenBy(thread => thread.Id).ToImmutableArray());
        NoteComments.Validate(root);
        var title = _title.String(t); if (title.Length > 500) throw new InvalidDataException("文档标题超过 500 字。");
        return new(title, root);
    }

    // Deterministic across .NET and JavaScript; empty containers cannot manufacture different IDs on each device.
    public static Guid PlaceholderId(string parent)
    {
        var parts = new List<string>();
        for (uint seed = 0; seed < 4; seed++)
        {
            var hash = 2166136261u ^ seed;
            foreach (var character in parent + ":empty:v1") hash = unchecked((hash ^ character) * 16777619u);
            parts.Add(hash.ToString("x8"));
        }
        return Guid.ParseExact(string.Concat(parts), "N");
    }

    private static ImmutableDictionary<string, JsonElement> ReadAttributes(Map map, Transaction t) => Entries(map, t).ToImmutableDictionary(pair => pair.Key,
        pair => pair.Value.Tag == OutputTag.String ? JsonSerializer.Deserialize<JsonElement>(pair.Value.String) : throw new InvalidDataException("共享属性无效。"));
    private static ImmutableArray<NoteMark> ReadMarks(IReadOnlyDictionary<string, Output> attributes)
    {
        var marks = ImmutableArray.CreateBuilder<NoteMark>(); var comments = new List<Guid>();
        foreach (var (key, value) in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (value.Tag != OutputTag.String) throw new InvalidDataException("共享文字属性无效。");
            if (key == "inline") { using var inline = JsonDocument.Parse(value.String); if (inline.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("行内内容无效。"); }
            if (key.StartsWith("c:", StringComparison.Ordinal) && value.Tag == OutputTag.String && value.String == "true") comments.Add(Guid.Parse(key[2..]));
            else if (key.StartsWith("m:", StringComparison.Ordinal) && value.Tag == OutputTag.String)
            {
                using var json = JsonDocument.Parse(value.String); var data = json.RootElement;
                marks.Add(new(key[2..])
                {
                    Attrs = data.GetProperty("attrs").EnumerateObject().ToImmutableDictionary(pair => pair.Name, pair => pair.Value.Clone()),
                    Extra = data.GetProperty("extra").EnumerateObject().ToImmutableDictionary(pair => pair.Name, pair => pair.Value.Clone())
                });
            }
        }
        return NoteComments.WithIds(marks.ToImmutable(), comments.ToImmutableArray());
    }

    public byte[]? CapturePosition(TextPoint point)
    {
        // Yrs splits an item when creating a relative position; its C ABI requires a mutable transaction.
        using var t = _doc.WriteTransaction(RemoteOrigin);
        if (_blocks.Get(t, point.NodeId.ToString("D")) is not { Tag: OutputTag.Map } map || map.Map.Get(t, "text") is not { Tag: OutputTag.Text } text) return null;
        if (point.Offset >= text.Text.Length(t)) return [0];
        using var index = text.Text.StickyIndex(t, (uint)Math.Clamp(point.Offset, 0, (int)text.Text.Length(t)), StickyAssociationType.After);
        if (index == null) return null;
        try { return [1, .. index.Encode()]; } finally { DestroySticky(StickyHandle(index)); }
    }
    public TextPoint RestorePosition(TextPoint point, byte[]? position)
    {
        if (position == null) return point;
        using var t = _doc.ReadTransaction();
        if (position[0] == 0)
            return _blocks.Get(t, point.NodeId.ToString("D")) is { Tag: OutputTag.Map } map && map.Map.Get(t, "text") is { Tag: OutputTag.Text } text
                ? point with { Offset = (int)text.Text.Length(t) } : point;
        using var index = StickyIndex.Decode(position[1..]);
        try { return point with { Offset = (int)index.Read(t) }; } finally { DestroySticky(StickyHandle(index)); }
    }
    // The pinned 0.6.0 StickyIndex.DisposeCore is empty although ysticky_index_destroy is exported.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Handle")]
    private static extern nint StickyHandle(UnmanagedResource resource);
    [DllImport("yrs", EntryPoint = "ysticky_index_destroy", CallingConvention = CallingConvention.Cdecl)]
    private static extern void DestroySticky(nint handle);
    public void Dispose() { _undo.Dispose(); _doc.Dispose(); }
}
