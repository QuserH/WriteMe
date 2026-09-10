using System.Collections.Immutable;
using System.Text.Json.Nodes;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class DocumentTests
{
    private static DocumentSession Session(params NoteNode[] nodes) => new(new("doc") { Content = [.. nodes] });
    private static string Json(DocumentSession session) => NoteJson.Serialize(session.Root);

    [Fact]
    public void TiptapRichJsonRoundTripsWithoutLosingContainersMarksOrUnknownData()
    {
        const string raw = """
            {"type":"doc","customVersion":4,"content":[
              {"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"中文🙂","marks":[{"type":"bold"},{"type":"link","attrs":{"href":"https://example.com","target":"_blank"}}]}]},
              {"type":"toggleBlock","attrs":{"collapsed":true},"content":[{"type":"paragraph"},{"type":"paragraph","content":[{"type":"text","text":"子块","marks":[{"type":"highlight","attrs":{"color":"#D9EED1"}},{"type":"italic"},{"type":"underline"},{"type":"strike"}]},{"type":"hardBreak"},{"type":"text","text":"下一行"}]}]},
              {"type":"orderedList","attrs":{"start":3},"content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"第一项"}]},{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"嵌套列表"}]}]}]}]}]},
              {"type":"taskList","content":[{"type":"taskItem","attrs":{"checked":true},"content":[{"type":"paragraph","content":[{"type":"text","text":"已完成"}]}]}]},
              {"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"引用"}]}]},
              {"type":"codeBlock","attrs":{"language":"ts"},"content":[{"type":"text","text":"let a = 1;\nprint(a);"}]},
              {"type":"horizontalRule"},{"type":"futureCard","attrs":{"id":"card-1"},"extra":{"keep":true}},{"type":"paragraph"}
            ]}
            """;
        var root = NoteJson.Parse(raw);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(raw), JsonNode.Parse(NoteJson.Serialize(root))));
        var projection = new DocumentProjection(root);
        Assert.DoesNotContain("子块", projection.Text);
        Assert.Contains("嵌套列表", projection.Text);
        Assert.Contains("\u2028", projection.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("旧标题")]
    public void LegacyEmptyToggleTitleNeverConsumesFirstChild(string title)
    {
        var old = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject
        {
            ["type"] = "toggleBlock", ["attrs"] = new JsonObject { ["title"] = title, ["collapsed"] = true },
            ["content"] = new JsonArray(JsonNode.Parse("""{"type":"paragraph","content":[{"type":"text","text":"原始正文"}]}"""))
        }) };
        var root = NoteJson.Parse(old.ToJsonString());
        var toggle = root.Content[0];
        Assert.Equal(title, RichText.Plain(toggle.Content[0]));
        Assert.Equal("原始正文", RichText.Plain(toggle.Content[1]));
        Assert.False(toggle.Attrs.ContainsKey("title"));
        Assert.Equal(NoteJson.Serialize(root), NoteJson.Serialize(NoteJson.Parse(NoteJson.Serialize(root))));
    }

    [Fact]
    public void PlaintextRetainsEmptyLinesAndWhitespace()
    {
        var root = NoteJson.Parse("第一行\r\n\r\n  保留缩进  \n");
        Assert.Equal(new[] { "第一行", "", "  保留缩进  ", "" }, root.Content.Select(RichText.Plain));
    }

    [Fact]
    public void FiveLevelsFoldIndependentlyAndHiddenCaretReturnsToTitle()
    {
        var leaf = NoteNode.Toggle("第五级", NoteNode.Paragraph("深层正文"));
        var third = NoteNode.Toggle("第三级", NoteNode.Toggle("第四级", leaf));
        var first = NoteNode.Toggle("第一级", NoteNode.Toggle("第二级", third));
        var session = Session(first);
        session.Selection = EditorSelection.At(leaf.Content[1].Id, 2);
        session.Toggle(third.Id);
        Assert.Equal(third.Content[0].Id, session.Selection.Caret.NodeId);
        session.Toggle(first.Id);
        session.Toggle(first.Id);
        Assert.True(NoteTree.Find(session.Root, third.Id)!.Bool("collapsed"));
        Assert.False(NoteTree.Find(session.Root, leaf.Id)!.Bool("collapsed"));
        session.Toggle(third.Id);
        Assert.Contains("深层正文", session.Projection.Text);
        Assert.Equal(5, session.Projection.Rows.Max(row => row.Depth));
    }

    [Fact]
    public void EnterSplitsRichToggleAndUndoRestoresItInOneStep()
    {
        var title = NoteNode.Paragraph() with { Content = [new("text") { Text = "标题正文", Marks = [new("bold"), NoteMark.With("link", "href", "https://example.com")] }] };
        var toggle = new NoteNode("toggleBlock") { Content = [title, NoteNode.Paragraph("原有子块")] };
        var session = Session(toggle);
        var before = Json(session);
        session.Enter(2);
        Assert.Equal("标题", RichText.Plain(session.Root.Content[0].Content[0]));
        var child = session.Root.Content[0].Content[1];
        Assert.Equal("正文", RichText.Plain(child.Content[0]));
        Assert.Equal(2, child.Content[0].Content[0].Marks.Length);
        Assert.Equal("原有子块", RichText.Plain(session.Root.Content[0].Content[2]));
        session.Undo(); Assert.Equal(before, Json(session));
        session.Redo(); Assert.Equal(child.Id, session.Root.Content[0].Content[1].Id);
    }

    [Fact]
    public void TextFormatFoldAndMoveShareOneChronologicalHistory()
    {
        var a = NoteNode.Toggle("想法", NoteNode.Paragraph("子块"));
        var b = NoteNode.Toggle("计划");
        var session = Session(a, b);
        var states = new List<string> { Json(session) };
        session.Edit(2, 0, "中文🙂", false); states.Add(Json(session));
        session.Format(0, 2, new("bold")); states.Add(Json(session));
        session.Toggle(a.Id); states.Add(Json(session));
        Assert.True(session.Move(a.Id, b.Id, DropPlacement.Inside)); states.Add(Json(session));
        for (var i = states.Count - 2; i >= 0; i--) { session.Undo(); Assert.Equal(states[i], Json(session)); }
        for (var i = 1; i < states.Count; i++) { session.Redo(); Assert.Equal(states[i], Json(session)); }
    }

    [Fact]
    public void CrossBlockReplacementRetainsUntouchedMarksAndSupportsUndo()
    {
        var p1 = NoteNode.Paragraph() with { Content = [new("text") { Text = "甲乙丙", Marks = [new("bold")] }] };
        var p2 = NoteNode.Paragraph() with { Content = [new("text") { Text = "丁戊己", Marks = [NoteMark.With("link", "href", "https://example.com")] }] };
        var session = Session(p1, p2);
        var before = Json(session);
        session.Edit(1, 5, "替换", false);
        Assert.Equal("甲替换己", session.Projection.Text);
        Assert.Equal("link", session.Root.Content[0].Content[^1].Marks[0].Type);
        session.Undo(); Assert.Equal(before, Json(session));
    }

    [Fact]
    public void SelectionReplacedByEnterIsOneUndoTransaction()
    {
        var session = Session(NoteNode.Paragraph("甲乙丙"), NoteNode.Paragraph("丁戊己"));
        var original = Json(session);
        session.ReplaceSelectionWithEnter(1, 5);
        Assert.Equal("甲\n己", session.Projection.Text);
        session.Undo(); Assert.Equal(original, Json(session));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void RemovingCollapsedTitleAsAWholeRemovesItsHiddenSubtreeAndUndoRestoresIt()
    {
        var hidden = NoteNode.Toggle("收起", NoteNode.Paragraph("隐含正文")).WithAttr("collapsed", true);
        var session = Session(hidden, NoteNode.Paragraph("留下"));
        var before = Json(session);
        session.Edit(0, 3, "", false);
        Assert.Equal("留下", session.Projection.Text);
        Assert.DoesNotContain("隐含正文", Json(session));
        session.Undo(); Assert.Equal(before, Json(session));
    }

    [Fact]
    public void BackspaceAtCollapsedBoundaryExpandsBeforeAnyMerge()
    {
        var hidden = NoteNode.Toggle("标题", NoteNode.Paragraph("正文")).WithAttr("collapsed", true);
        var session = Session(hidden, NoteNode.Paragraph("下一段"));
        session.BackspaceAtStart(3);
        Assert.False(session.Root.Content[0].Bool("collapsed"));
        Assert.Equal("标题\n正文\n下一段", session.Projection.Text);
    }

    [Fact]
    public void SubtreeMoveRejectsCyclesAndPreservesNestedCollapsedStates()
    {
        var child = NoteNode.Toggle("子项", NoteNode.Paragraph("内容")).WithAttr("collapsed", true);
        var a = NoteNode.Toggle("A", child);
        var b = NoteNode.Toggle("B").WithAttr("collapsed", true);
        var session = Session(a, b);
        var before = Json(session);
        Assert.False(session.Move(a.Id, child.Id, DropPlacement.Inside));
        Assert.Equal(before, Json(session));
        Assert.True(session.Move(child.Id, b.Id, DropPlacement.Inside));
        Assert.False(session.Root.Content[1].Bool("collapsed"));
        Assert.True(NoteTree.Find(session.Root, child.Id)!.Bool("collapsed"));
        Assert.True(session.Outdent(child.Id));
        Assert.Equal(child.Id, session.Root.Content[2].Id);
    }

    [Fact]
    public void UnwrapTogglePreservesTitleFormattingAndAllChildren()
    {
        var title = NoteNode.Paragraph() with { Content = [new("text") { Text = "标题", Marks = [new("bold")] }] };
        var session = Session(new NoteNode("toggleBlock") { Content = [title, NoteNode.Toggle("子项"), NoteNode.Paragraph()] });
        session.ConvertBlock(0, "heading", 2);
        Assert.Equal(3, session.Root.Content.Length);
        Assert.Equal("heading", session.Root.Content[0].Type);
        Assert.Equal("bold", session.Root.Content[0].Content[0].Marks[0].Type);
        Assert.Equal("toggleBlock", session.Root.Content[1].Type);
        Assert.Empty(session.Root.Content[2].Content);
    }

    [Fact]
    public void UnknownBlocksRemainUnchangedWhileEditingSupportedNeighbors()
    {
        var root = NoteJson.Parse("""{"type":"doc","content":[{"type":"futureCard","attrs":{"value":"保留"}},{"type":"paragraph","content":[{"type":"text","text":"正文"}]}]}""");
        var session = new DocumentSession(root);
        session.Edit(session.Projection.Rows[1].End, 0, "添加");
        Assert.Same(root.Content[0], session.Root.Content[0]);
        var before = Json(session);
        Assert.Throws<InvalidOperationException>(() => session.Edit(0, 2, "不能覆盖"));
        Assert.Equal(before, Json(session));
    }

    [Fact]
    public void EmojiAndSoftBreakKeepCorrectUtf16OffsetsAndRichText()
    {
        var session = Session(NoteNode.Paragraph("中文🙂结尾"));
        session.Format(2, 2, new("bold"));
        session.Edit(4, 0, "\u2028", false);
        Assert.Equal("中文🙂\u2028结尾", session.Projection.Text);
        Assert.Equal("hardBreak", session.Root.Content[0].Content[2].Type);
        session.Edit(2, 2, "", false);
        Assert.Equal("中文\u2028结尾", session.Projection.Text);
        session.Undo(); Assert.Equal("中文🙂\u2028结尾", session.Projection.Text);
    }

    [Fact]
    public void TenThousandHiddenBlocksDoNotEnterTheTextProjection()
    {
        var blocks = Enumerable.Range(0, 10000).Select(i => NoteNode.Paragraph("第" + i + "块")).ToArray();
        var session = Session(NoteNode.Toggle("大文档", blocks).WithAttr("collapsed", true));
        Assert.Single(session.Projection.Rows);
        session.Toggle(session.Root.Content[0].Id);
        Assert.Equal(10001, session.Projection.Rows.Length);
        Assert.Equal(10000, session.Root.Content[0].Content.Length - 1);
    }

    [Theory]
    [InlineData("example.com", "https://example.com")]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("mailto:a@example.com", "mailto:a@example.com")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("data:text/html,x", null)]
    [InlineData("https://", null)]
    [InlineData("two words", null)]
    public void LinkAddressesAreNormalizedAndValidated(string input, string? expected) => Assert.Equal(expected, LinkAddress.Normalize(input));
}
