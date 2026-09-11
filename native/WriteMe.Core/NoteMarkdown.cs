using System.Collections.Immutable;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;
using MarkdownTask = Markdig.Extensions.TaskLists.TaskList;

namespace WriteMe.Core;

// Note: Markdown 用于常见文字交换，JSON/ZIP 保留完整文档结构 — 见 .agents/notes/implemented/feature/2026-09-10-page-assets-and-portability.md
public static class NoteMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static NoteNode Parse(string text)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("导入文本超过 8 MB");
        var document = Markdown.Parse(text, Pipeline);
        var children = document.SelectMany(ConvertBlock).ToImmutableArray();
        return new("doc") { Content = children.IsEmpty ? [NoteNode.Paragraph()] : children };
    }

    private static IEnumerable<NoteNode> ConvertBlock(Block block)
    {
        switch (block)
        {
            case FencedCodeBlock fence when fence.Info == "writeme-json":
                foreach (var node in NoteJson.ParseStrict(fence.Lines.ToString()).Content) yield return node;
                break;
            case CodeBlock code:
                yield return (NoteNode.Paragraph(code.Lines.ToString()) with { Type = "codeBlock" }).WithAttr("language", (code as FencedCodeBlock)?.Info ?? "");
                break;
            case HeadingBlock heading:
                yield return new NoteNode("heading") { Content = Inline(heading.Inline) }.WithAttr("level", Math.Clamp(heading.Level, 1, 3));
                break;
            case ParagraphBlock paragraph:
                if (paragraph.Inline?.FirstChild is LinkInline { IsImage: false, Url: { } attachmentUrl } attachment
                    && ReferenceEquals(paragraph.Inline.LastChild, attachment)
                    && attachmentUrl.StartsWith("assets/", StringComparison.Ordinal) && NoteStore.IsAssetId(attachmentUrl[7..]))
                {
                    var name = string.Concat(attachment.Descendants<LiteralInline>().Select(literal => literal.Content.ToString()));
                    yield return new NoteNode("attachment").WithAttr("assetId", attachmentUrl[7..]).WithAttr("name", name.Length == 0 ? "附件" : name);
                    break;
                }
                var images = paragraph.Inline?.Descendants<LinkInline>().Where(link => link.IsImage).ToArray() ?? [];
                var runs = Inline(paragraph.Inline);
                if (!runs.IsEmpty || images.Length == 0) yield return new("paragraph") { Content = runs };
                foreach (var image in images)
                {
                    var name = string.Concat(image.Descendants<LiteralInline>().Select(literal => literal.Content.ToString()));
                    yield return new NoteNode("image").WithAttr("src", image.Url ?? "").WithAttr("name", name.Length == 0 ? "图片" : name).WithAttr("alt", name);
                }
                break;
            case ListBlock list:
                var tasks = list.Descendants<MarkdownTask>().Any();
                var items = new List<NoteNode>();
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var content = item.SelectMany(ConvertBlock).ToImmutableArray();
                    if (content.IsEmpty) content = [NoteNode.Paragraph()];
                    var node = new NoteNode(tasks ? "taskItem" : "listItem") { Content = content };
                    if (tasks) node = node.WithAttr("checked", item.Descendants<MarkdownTask>().FirstOrDefault()?.Checked ?? false);
                    items.Add(node);
                }
                yield return new NoteNode(tasks ? "taskList" : list.IsOrdered ? "orderedList" : "bulletList") { Content = [.. items] }
                    .WithAttr("start", list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1);
                break;
            case QuoteBlock quote:
                yield return new("blockquote") { Content = quote.SelectMany(ConvertBlock).ToImmutableArray() };
                break;
            case ThematicBreakBlock:
                yield return new("horizontalRule");
                break;
            case Table table:
                var rows = new List<NoteNode>();
                foreach (var row in table.OfType<TableRow>())
                {
                    var cells = new List<NoteNode>();
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        var content = cell.SelectMany(ConvertBlock).ToImmutableArray();
                        if (content.IsEmpty) content = [NoteNode.Paragraph()];
                        var columnIndex = cell.ColumnIndex >= 0 ? cell.ColumnIndex : cells.Sum(existing => existing.Int("colspan", 1));
                        var alignment = columnIndex < table.ColumnDefinitions.Count ? table.ColumnDefinitions[columnIndex].Alignment?.ToString().ToLowerInvariant() : null;
                        if (alignment is "center" or "right") content = content.Select(block => block.IsTextBlock ? block.WithAttr("textAlign", alignment) : block).ToImmutableArray();
                        var converted = new NoteNode(row.IsHeader ? "tableHeader" : "tableCell") { Content = content };
                        if (cell.ColumnSpan > 1) converted = converted.WithAttr("colspan", cell.ColumnSpan);
                        if (cell.RowSpan > 1) converted = converted.WithAttr("rowspan", cell.RowSpan);
                        cells.Add(converted);
                    }
                    rows.Add(new("tableRow") { Content = [.. cells] });
                }
                yield return new("table") { Content = [.. rows] };
                break;
            case LeafBlock leaf:
                yield return leaf.Inline != null ? new("paragraph") { Content = Inline(leaf.Inline) } : NoteNode.Paragraph(leaf.Lines.ToString());
                break;
            case ContainerBlock container:
                foreach (var node in container.SelectMany(ConvertBlock)) yield return node;
                break;
        }
    }

    private static ImmutableArray<NoteNode> Inline(ContainerInline? container, ImmutableArray<NoteMark> marks = default)
    {
        if (container == null) return [];
        if (marks.IsDefault) marks = [];
        var result = new List<NoteNode>();
        var afterTask = false;
        foreach (var item in container)
        {
            switch (item)
            {
                case LiteralInline literal:
                    var literalText = literal.Content.ToString();
                    if (afterTask && literalText.StartsWith(' ')) literalText = literalText[1..];
                    afterTask = false; result.AddRange(RichText.FromText(literalText, marks)); break;
                case LineBreakInline line: result.AddRange(RichText.FromText(line.IsHard ? "\n" : " ", marks)); break;
                case CodeInline code: result.AddRange(RichText.FromText(code.Content, marks.Add(new("code")))); break;
                case EmphasisInline emphasis:
                    var next = marks;
                    if (emphasis.DelimiterChar == '~') next = next.Add(new("strike"));
                    else if (emphasis.DelimiterChar == '=') next = next.Add(NoteMark.With("highlight", "color", "#FFF0A8"));
                    else
                    {
                        if (emphasis.DelimiterCount >= 2) next = next.Add(new("bold"));
                        if (emphasis.DelimiterCount % 2 != 0) next = next.Add(new("italic"));
                    }
                    result.AddRange(Inline(emphasis, next)); break;
                case LinkInline link when !link.IsImage:
                    var url = link.Url ?? "";
                    var linkMarks = url.StartsWith("writeme://note/", StringComparison.Ordinal) ? marks.Add(NoteMark.With("noteLink", "documentId", Uri.UnescapeDataString(url[15..])))
                        : LinkAddress.Normalize(url) is { } normalized ? marks.Add(NoteMark.With("link", "href", normalized)) : marks;
                    result.AddRange(Inline(link, linkMarks)); break;
                case LinkInline: break;
                case AutolinkInline auto:
                    result.AddRange(RichText.FromText(auto.Url, marks.Add(NoteMark.With("link", "href", auto.IsEmail ? "mailto:" + auto.Url : auto.Url)))); break;
                case HtmlInline html: result.AddRange(RichText.FromText(html.Tag, marks)); break;
                case MarkdownTask: afterTask = true; break;
                case ContainerInline nested: result.AddRange(Inline(nested, marks)); break;
            }
        }
        return RichText.Compact([.. result]);
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Runs(NoteNode block)
    {
        var text = new StringBuilder();
        foreach (var run in block.Content)
        {
            if (run.Type == "hardBreak") { text.Append("  \n"); continue; }
            if (run.Type != "text") continue;
            var value = Escape(run.Text).Replace("\u2028", "  \n");
            if (run.Marks.Any(mark => mark.Type == "code")) { var ticks = new string('`', Math.Max(1, LongestTicks(run.Text) + 1)); value = ticks + " " + run.Text + " " + ticks; }
            if (run.Marks.Any(mark => mark.Type == "bold")) value = "**" + value + "**";
            if (run.Marks.Any(mark => mark.Type == "italic")) value = "*" + value + "*";
            if (run.Marks.Any(mark => mark.Type == "strike")) value = "~~" + value + "~~";
            var link = run.Marks.FirstOrDefault(mark => mark.Type is "link" or "noteLink");
            if (link != null)
            {
                var target = link.Type == "noteLink" ? "writeme://note/" + Uri.EscapeDataString(link.String("documentId") ?? "") : link.String("href") ?? "";
                value = "[" + value + "](" + target.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29") + ")";
            }
            text.Append(value);
        }
        return text.ToString();
    }
    private static int LongestTicks(string value)
    {
        var current = 0; var longest = 0;
        foreach (var character in value) { current = character == '`' ? current + 1 : 0; longest = Math.Max(longest, current); }
        return longest;
    }

    public static string Export(NoteNode root, string assetsPrefix = "assets/")
    {
        // Markdown carries readable body content; JSON/ZIP carry discussions and anchors.
        // Remove anchors from custom fenced blocks too, so they cannot become dangling IDs.
        root = NoteComments.RemoveMarks(root) with { Attrs = root.Attrs.Remove(NoteComments.Attribute) };
        string Node(NoteNode node)
        {
            switch (node.Type)
            {
                case "doc": return string.Join("\n\n", node.Content.Select(Node));
                case "paragraph": return Runs(node);
                case "heading": return new string('#', Math.Clamp(node.Int("level", 1), 1, 3)) + " " + Runs(node);
                case "codeBlock":
                    var code = RichText.Plain(node).Replace('\u2028', '\n'); var fence = new string('`', Math.Max(3, LongestTicks(code) + 1));
                    return fence + (node.String("language") ?? "") + "\n" + code + "\n" + fence;
                case "blockquote": return string.Join("\n\n", node.Content.Select(Node)).Replace("\n", "\n> ").Insert(0, "> ");
                case "horizontalRule": return "---";
                case "bulletList": case "orderedList": case "taskList":
                    return string.Join('\n', node.Content.Select((item, index) =>
                    {
                        var prefix = node.Type == "orderedList" ? (node.Int("start", 1) + index) + ". " : node.Type == "taskList" ? item.Bool("checked") ? "- [x] " : "- [ ] " : "- ";
                        var body = string.Join("\n\n", item.Content.Select(Node));
                        return prefix + body.Replace("\n", "\n" + new string(' ', prefix.Length));
                    }));
                case "image": case "attachment":
                    var name = node.String("name") ?? node.String("alt") ?? "附件";
                    var source = node.String("assetId") is { } asset ? assetsPrefix + asset : node.String("src") ?? "";
                    return (node.Type == "image" ? "!" : "") + "[" + Escape(name) + "](" + source.Replace(" ", "%20").Replace(")", "%29") + ")";
                case "table" when SimpleTable(node) is { } markdown: return markdown;
                default:
                    // Custom blocks retain their exact structure in an explicit fenced payload.
                    var json = NoteJson.Serialize(new("doc") { Content = [node] });
                    var delimiter = new string('`', Math.Max(3, LongestTicks(json) + 1));
                    return delimiter + "writeme-json\n" + json + "\n" + delimiter;
            }
        }
        return Node(root).TrimEnd() + "\n";
    }

    private static string? SimpleTable(NoteNode table)
    {
        if (!LayoutBlocks.IsEditableTable(table) || !LayoutBlocks.HasHeader(table) || table.Attrs.Count > 0 || table.Extra.Count > 0) return null;
        var cells = table.Content.SelectMany(row => row.Content).ToArray();
        if (cells.Any(cell => cell.Attrs.Count > 0 || cell.Extra.Count > 0 || cell.Content.Length != 1 || cell.Content[0].Type != "paragraph"
            || cell.Content[0].Attrs.Keys.Any(key => key != "textAlign") || cell.Content[0].Content.Any(run => run.Type != "text" || run.Text.Contains('\u2028') || run.Text.Contains('\n')
                || run.Marks.Any(mark => mark.Type is not ("bold" or "italic" or "strike" or "link" or "noteLink" or "code"))))) return null;
        var alignments = table.Content[0].Content.Select(cell => cell.Content[0].String("textAlign") ?? "left").ToArray();
        if (table.Content.Skip(1).Any(row => row.Content.Where((cell, index) => (cell.Content[0].String("textAlign") ?? "left") != alignments[index] || cell.Type != "tableCell").Any())) return null;
        var lines = table.Content.Select(row => "| " + string.Join(" | ", row.Content.Select(cell => Runs(cell.Content[0]).Replace("|", "\\|"))) + " |").ToList();
        lines.Insert(1, "| " + string.Join(" | ", alignments.Select(alignment => alignment switch { "center" => ":---:", "right" => "---:", _ => "---" })) + " |");
        return string.Join('\n', lines);
    }
}
