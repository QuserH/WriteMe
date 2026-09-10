using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WriteMe.Core;

public static class NoteJson
{
    private static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 256 };

    public static NoteNode ParseStrict(string raw)
    {
        if (raw == null || Encoding.UTF8.GetByteCount(raw) > 8 * 1024 * 1024) throw new InvalidDataException("单篇笔记缺少内容或超过 8 MB");
        using var json = JsonDocument.Parse(raw, ReadOptions);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "doc")
            throw new InvalidDataException("文件不是 WriteME / TipTap 文档 JSON");
        var root = ReadNode(json.RootElement);
        return root.Content.IsEmpty ? root with { Content = [NoteNode.Paragraph()] } : root;
    }

    public static NoteNode Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return NoteNode.EmptyDocument();
        if (raw.TrimStart().StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(raw, ReadOptions);
                if (json.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "doc")
                {
                    var doc = ReadNode(json.RootElement);
                    return doc.Content.IsEmpty ? doc with { Content = [NoteNode.Paragraph()] } : doc;
                }
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // Legacy plaintext can begin with '{'. Keep the original content verbatim.
            }
        }
        return new("doc") { Content = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(NoteNode.Paragraph).ToImmutableArray() };
    }

    private static ImmutableDictionary<string, JsonElement> Map(JsonElement el) => el.ValueKind != JsonValueKind.Object
        ? ImmutableDictionary<string, JsonElement>.Empty
        : el.EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.Clone());

    private static NoteNode ReadNode(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new JsonException("无效的块节点");
        var attrs = el.TryGetProperty("attrs", out var a) ? Map(a) : ImmutableDictionary<string, JsonElement>.Empty;
        var children = el.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(ReadNode).ToImmutableArray() : [];
        var marks = el.TryGetProperty("marks", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().Select(ReadMark).ToImmutableArray() : [];
        if (el.TryGetProperty("text", out var text) && text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new JsonException("无效的文本");
        var node = new NoteNode(type.GetString()!)
        {
            Text = el.TryGetProperty("text", out var t) ? (t.GetString() ?? "").Replace("\r\n", "\n").Replace('\r', '\n') : "",
            Attrs = attrs,
            Content = children,
            Marks = marks,
            Extra = el.EnumerateObject().Where(p => p.Name is not ("type" or "attrs" or "content" or "marks" or "text")).ToImmutableDictionary(p => p.Name, p => p.Value.Clone())
        };
        if (node.Type == "toggleBlock")
        {
            if (attrs.TryGetValue("title", out var title))
                node = node with { Attrs = attrs.Remove("title"), Content = node.Content.Insert(0, NoteNode.Paragraph(title.ValueKind == JsonValueKind.String ? title.GetString() ?? "" : "")) };
            if (node.Content.IsEmpty || node.Content[0].Type != "paragraph")
                node = node with { Content = node.Content.Insert(0, NoteNode.Paragraph()) };
        }
        return node;
    }

    private static NoteMark ReadMark(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new JsonException("无效的文字格式");
        return new(type.GetString()!)
        {
            Attrs = value.TryGetProperty("attrs", out var attributes) ? Map(attributes) : ImmutableDictionary<string, JsonElement>.Empty,
            Extra = value.EnumerateObject().Where(property => property.Name is not ("type" or "attrs")).ToImmutableDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    public static string Serialize(NoteNode root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 256 }))
            Write(writer, root);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static void Write(Utf8JsonWriter writer, NoteNode node)
    {
        writer.WriteStartObject();
        writer.WriteString("type", node.Type);
        WriteMap(writer, "attrs", node.Attrs);
        if (node.Type == "text") writer.WriteString("text", node.Text);
        if (!node.Marks.IsEmpty)
        {
            writer.WriteStartArray("marks");
            foreach (var mark in node.Marks)
            {
                writer.WriteStartObject();
                writer.WriteString("type", mark.Type);
                WriteMap(writer, "attrs", mark.Attrs);
                foreach (var pair in mark.Extra) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        if (!node.Content.IsEmpty)
        {
            writer.WriteStartArray("content");
            foreach (var child in node.Content) Write(writer, child);
            writer.WriteEndArray();
        }
        foreach (var pair in node.Extra) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); }
        writer.WriteEndObject();
    }

    private static void WriteMap(Utf8JsonWriter writer, string key, ImmutableDictionary<string, JsonElement> map)
    {
        if (map.IsEmpty) return;
        writer.WriteStartObject(key);
        foreach (var pair in map) { writer.WritePropertyName(pair.Key); pair.Value.WriteTo(writer); }
        writer.WriteEndObject();
    }
}
