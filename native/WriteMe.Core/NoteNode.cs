using System.Collections.Immutable;
using System.Text.Json;

namespace WriteMe.Core;

// Note: 原生编辑核心、不可变文档与旧 JSON 契约 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
public sealed record NoteNode(string Type)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = "";
    public ImmutableArray<NoteNode> Content { get; init; } = [];
    public ImmutableArray<NoteMark> Marks { get; init; } = [];
    public ImmutableDictionary<string, JsonElement> Attrs { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public ImmutableDictionary<string, JsonElement> Extra { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    public bool IsTextBlock => Type is "paragraph" or "heading" or "codeBlock";
    public bool IsContentContainer => Type is "doc" or "toggleBlock" or "blockquote" or "bulletList" or "orderedList" or "taskList" or "listItem" or "taskItem"
        or "table" or "tableRow" or "tableCell" or "tableHeader" or "columnList" or "column";
    public bool Bool(string key) => Attrs.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;
    public string? String(string key) => Attrs.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public int Int(string key, int fallback = 0) => Attrs.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
    public NoteNode WithAttr<T>(string key, T value) => this with { Attrs = Attrs.SetItem(key, JsonSerializer.SerializeToElement(value)) };
    public static NoteNode Paragraph(string text = "") => new("paragraph") { Content = text.Length == 0 ? [] : [new("text") { Text = text }] };
    public static NoteNode Toggle(string text, params NoteNode[] children) =>
        new NoteNode("toggleBlock") { Content = [Paragraph(text), .. children] }.WithAttr("collapsed", children.Length == 0);
    public static NoteNode EmptyDocument() => new("doc") { Content = [Paragraph()] };
}

public sealed record NoteMark(string Type)
{
    public ImmutableDictionary<string, JsonElement> Attrs { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public ImmutableDictionary<string, JsonElement> Extra { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
    public string? String(string key) => Attrs.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    public static NoteMark With(string type, string key, string value) => new(type) { Attrs = ImmutableDictionary<string, JsonElement>.Empty.Add(key, JsonSerializer.SerializeToElement(value)) };
    public bool Equivalent(NoteMark other) => Type == other.Type && EqualMap(Attrs, other.Attrs) && EqualMap(Extra, other.Extra);
    internal static bool EqualMap(ImmutableDictionary<string, JsonElement> a, ImmutableDictionary<string, JsonElement> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && JsonElement.DeepEquals(pair.Value, value));
}

public readonly record struct TextPoint(Guid NodeId, int Offset);
public readonly record struct EditorSelection(TextPoint Anchor, TextPoint Caret)
{
    public static EditorSelection At(Guid id, int offset = 0) => new(new(id, offset), new(id, offset));
}
