using System.Collections.Immutable;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class DocumentToolsTests
{
    private static DocumentSession Session(params NoteNode[] nodes) => new(new("doc") { Content = [.. nodes] });

    [Fact]
    public void NewToggleStartsCollapsedAndEmptyEnterOpensOnlyItsParent()
    {
        var session = Session(NoteNode.Paragraph());
        session.ConvertBlock(0, "toggleBlock");
        var original = NoteJson.Serialize(session.Root);
        Assert.True(session.Root.Content[0].Bool("collapsed"));
        Assert.False(session.Projection.Rows[0].IsExpanded);
        Assert.Empty(session.Projection.Rows[0].GuideDepths);
        for (var depth = 1; depth <= 5; depth++)
        {
            session.Enter(session.Projection.Offset(session.Selection.Caret));
            Assert.Equal(depth + 1, session.Projection.Rows.Length);
            Assert.All(session.Projection.Rows.Take(depth), row => Assert.True(row.IsExpanded));
            var child = session.Projection.Rows[^1];
            Assert.True(child.Collapsed);
            Assert.False(child.IsExpanded);
            Assert.Equal(Enumerable.Range(0, depth), child.GuideDepths);
            Assert.Equal(child.Node.Id, session.Selection.Caret.NodeId);
        }
        for (var depth = 0; depth < 5; depth++) session.Undo();
        Assert.Equal(original, NoteJson.Serialize(session.Root));
    }

    [Fact]
    public void SiblingEnterKeepsEmptyFoldTypesAndOwnCollapsedState()
    {
        var child = NoteNode.Toggle("");
        var session = Session(NoteNode.Toggle("父级", child));
        session.Enter(session.Projection.Find(child.Content[0].Id)!.Start, sibling: true);
        Assert.Equal(3, session.Root.Content[0].Content.Length);
        Assert.All(session.Root.Content[0].Content.Skip(1), node => { Assert.Equal("toggleBlock", node.Type); Assert.True(node.Bool("collapsed")); });
        Assert.Equal(new[] { 0 }, session.Projection.Rows[^1].GuideDepths);
        Assert.True(session.Outdent(session.Projection.Rows[^1].Block.Id));
        Assert.Empty(session.Projection.Rows[^1].GuideDepths);
        Assert.Equal("toggleBlock", session.Root.Content[1].Type);
    }

    [Fact]
    public void LegacyFoldStatesStayIntactAndOnlyToggleAncestorsProduceGuides()
    {
        var root = NoteJson.Parse("""{"type":"doc","content":[{"type":"toggleBlock","content":[{"type":"paragraph"},{"type":"blockquote","content":[{"type":"paragraph","content":[{"type":"text","text":"引用"}]}]}]},{"type":"toggleBlock","attrs":{"collapsed":true},"content":[{"type":"paragraph"},{"type":"paragraph"}]},{"type":"blockquote","content":[{"type":"paragraph"}]}]}""");
        var session = new DocumentSession(root);
        Assert.True(session.Projection.Rows[0].IsExpanded);
        Assert.False(root.Content[0].Attrs.ContainsKey("collapsed"));
        Assert.True(root.Content[1].Bool("collapsed"));
        Assert.Equal(2, session.Projection.Rows[1].Depth);
        Assert.Equal(new[] { 0 }, session.Projection.Rows[1].GuideDepths);
        Assert.Empty(session.Projection.Rows[^1].GuideDepths);
        session.Toggle(root.Content[0].Id);
        Assert.All(session.Projection.Rows, row => Assert.Empty(row.GuideDepths));
    }

    [Fact]
    public void ClickingAnEmptyArrowCreatesEditableContentAndUndoRestoresTheClosedLeaf()
    {
        var toggle = NoteNode.Toggle("待整理");
        var session = Session(toggle);
        session.Toggle(toggle.Id);
        Assert.True(session.Projection.Rows[0].IsExpanded);
        Assert.Equal(2, session.Projection.Rows.Length);
        Assert.Equal(session.Projection.Rows[1].Node.Id, session.Selection.Caret.NodeId);
        session.Undo();
        Assert.Same(toggle, session.Root.Content[0]);
    }

    [Fact]
    public void InsertAfterCollapsedBlockPreservesItsEntireSubtreeAndIsOneUndoStep()
    {
        var original = NoteNode.Toggle("计划", NoteNode.Toggle("子项", NoteNode.Paragraph("保留"))).WithAttr("collapsed", true);
        var session = Session(original);
        Assert.True(session.InsertBlock(1, "toggleBlock"));
        Assert.Same(original, session.Root.Content[0]);
        Assert.True(session.Root.Content[1].Bool("collapsed"));
        Assert.Equal(session.Root.Content[1].Content[0].Id, session.Selection.Caret.NodeId);
        session.Undo();
        Assert.Single(session.Root.Content);
        Assert.Same(original, session.Root.Content[0]);
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void InsertReusesEmptyBodyButNeverOverwritesAToggleTitle()
    {
        var body = NoteNode.Paragraph();
        var session = Session(NoteNode.Toggle("父级", body));
        session.InsertBlock(session.Projection.Find(body.Id)!.Start, "heading", 2);
        Assert.Equal(2, session.Root.Content[0].Content.Length);
        Assert.Equal("heading", session.Root.Content[0].Content[1].Type);
        Assert.Equal(2, session.Root.Content[0].Content[1].Int("level"));
        var empty = Session(NoteNode.Toggle(""));
        empty.InsertBlock(0, "paragraph");
        Assert.Equal(new[] { "toggleBlock", "paragraph" }, empty.Root.Content.Select(node => node.Type));
    }

    [Fact]
    public void InsertingBetweenNumberedItemsPreservesListGrammarAndNumbering()
    {
        var items = new[] { "第一项", "第二项", "第三项" }.Select(text => new NoteNode("listItem") { Content = [NoteNode.Paragraph(text)] }).ToImmutableArray();
        var original = new NoteNode("orderedList") { Content = items }.WithAttr("start", 3);
        var session = Session(original);
        session.InsertBlock(1, "toggleBlock");
        Assert.Equal(new[] { "orderedList", "toggleBlock", "orderedList" }, session.Root.Content.Select(node => node.Type));
        Assert.Equal(3, session.Root.Content[0].Int("start"));
        Assert.Equal(4, session.Root.Content[2].Int("start"));
        Assert.Same(items[1], session.Root.Content[2].Content[0]);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteJson.Parse(NoteJson.Serialize(session.Root))));
        session.Undo();
        Assert.Same(original, session.Root.Content[0]);
    }

    [Fact]
    public void OutlineIncludesHiddenHeadingsAndNavigationOpensOnlyRequiredAncestors()
    {
        var heading = NoteNode.Paragraph("隐藏标题").WithAttr("level", 2) with { Type = "heading" };
        var inner = NoteNode.Toggle("子计划", heading).WithAttr("collapsed", true);
        var outer = NoteNode.Toggle("项目", inner).WithAttr("collapsed", true);
        var unrelated = NoteNode.Toggle("另一个项目", NoteNode.Paragraph("保持收起")).WithAttr("collapsed", true);
        var session = Session(outer, unrelated);
        var original = NoteJson.Serialize(session.Root);
        Assert.Equal(new[] { "隐藏标题" }, DocumentOutline.Read(session.Root).Select(entry => entry.Title));
        Assert.True(session.Reveal(inner.Content[0].Id));
        Assert.False(session.Root.Content[0].Bool("collapsed"));
        Assert.True(NoteTree.Find(session.Root, inner.Id)!.Bool("collapsed"));
        Assert.Null(session.Projection.Find(heading.Id));
        Assert.True(session.Reveal(heading.Id));
        Assert.NotNull(session.Projection.Find(heading.Id));
        Assert.True(session.Root.Content[1].Bool("collapsed"));
        Assert.Equal(heading.Id, session.Selection.Caret.NodeId);
        session.Undo(); session.Undo();
        Assert.Equal(original, NoteJson.Serialize(session.Root));
        var revision = session.Revision;
        Assert.True(session.Reveal(outer.Content[0].Id));
        Assert.Equal(revision, session.Revision);
        Assert.False(session.Reveal(Guid.NewGuid()));
    }

    [Fact]
    public void OutlineExcludesPlainTogglesRegardlessOfCollapsedState()
    {
        var inner = NoteNode.Toggle("普通子折叠", NoteNode.Paragraph("正文"));
        var session = Session(NoteNode.Toggle("普通折叠", inner), NoteNode.Toggle("收起的折叠", NoteNode.Paragraph("隐藏正文")).WithAttr("collapsed", true));
        Assert.Empty(DocumentOutline.Read(session.Root));
        session.SetAllCollapsed(true);
        Assert.Empty(DocumentOutline.Read(session.Root));
        session.SetAllCollapsed(false);
        Assert.Empty(DocumentOutline.Read(session.Root));
    }

    [Fact]
    public void OutlineHierarchyFollowsHeadingLevelsWithoutAddingToggleIndentation()
    {
        static NoteNode Heading(string title, int level) => NoteNode.Paragraph(title).WithAttr("level", level) with { Type = "heading" };
        var session = Session(Heading("从二级开始", 2), NoteNode.Toggle("普通折叠", NoteNode.Toggle("更深的折叠", Heading("三级标题", 3))).WithAttr("collapsed", true), Heading("一级标题", 1), Heading("跳级的标题", 3));
        var entries = DocumentOutline.Read(session.Root);
        Assert.Equal(new[] { "从二级开始", "三级标题", "一级标题", "跳级的标题" }, entries.Select(entry => entry.Title));
        Assert.Equal(new[] { 0, 1, 0, 1 }, entries.Select(entry => entry.Depth));
        Assert.Equal(new[] { 2, 3, 1, 3 }, entries.Select(entry => entry.HeadingLevel));
        session.ConvertBlock(session.Projection.Rows[^1].Start, "paragraph");
        Assert.Equal(3, DocumentOutline.Read(session.Root).Length);
        session.Undo();
        Assert.Equal(entries.ToArray(), DocumentOutline.Read(session.Root).ToArray());
    }

    [Fact]
    public void ExplicitFoldAllDoesNotAddEmptyChildrenOrNoOpHistory()
    {
        var empty = NoteNode.Toggle("空标题");
        var session = Session(empty, NoteNode.Toggle("有内容", NoteNode.Paragraph("正文")));
        session.SetAllCollapsed(false);
        Assert.False(session.CanUndo);
        session.SetAllCollapsed(true);
        Assert.Single(session.Root.Content[0].Content);
        Assert.Equal(2, session.Projection.Rows.Length);
        var revision = session.Revision;
        session.SetAllCollapsed(true);
        Assert.Equal(revision, session.Revision);
        session.SetAllCollapsed(false);
        Assert.Equal(3, session.Projection.Rows.Length);
        Assert.Same(empty, session.Root.Content[0]);
    }

    [Fact]
    public void ApplyingTheCurrentBlockStyleKeepsCheckedTasksAndDoesNotAddHistory()
    {
        var task = new NoteNode("taskItem") { Content = [NoteNode.Paragraph("已完成的事")] }.WithAttr("checked", true);
        var session = Session(new NoteNode("taskList") { Content = [task] });
        session.ConvertBlock(0, "taskList");
        Assert.Same(task, session.Root.Content[0].Content[0]);
        Assert.False(session.CanUndo);
        var heading = Session(NoteNode.Paragraph("标题").WithAttr("level", 2) with { Type = "heading" });
        heading.ConvertBlock(0, "heading", 2);
        Assert.False(heading.CanUndo);
    }

    [Fact]
    public void ConvertingOneQuotedParagraphToBodyPreservesTheOtherQuotedParagraphs()
    {
        var quote = new NoteNode("blockquote") { Content = [NoteNode.Paragraph("前面的引用"), NoteNode.Paragraph("变成正文"), NoteNode.Paragraph("后面的引用")] };
        var session = Session(quote);
        session.ConvertBlock(session.Projection.Rows[1].Start, "paragraph");
        Assert.Equal(new[] { "blockquote", "paragraph", "blockquote" }, session.Root.Content.Select(node => node.Type));
        Assert.False(session.Projection.Rows[1].Quote);
        Assert.True(session.Projection.Rows[0].Quote);
        Assert.True(session.Projection.Rows[2].Quote);
        session.Undo();
        Assert.Same(quote, session.Root.Content[0]);
    }

    [Fact]
    public void ChangingANumberedItemToAToggleRetainsTheFollowingItemNumber()
    {
        var list = new NoteNode("orderedList") { Content = [new("listItem") { Content = [NoteNode.Paragraph("第一项")] }, new("listItem") { Content = [NoteNode.Paragraph("第二项")] }] }.WithAttr("start", 5);
        var session = Session(list);
        session.ConvertBlock(0, "toggleBlock");
        Assert.True(session.Root.Content[0].Bool("collapsed"));
        Assert.Equal("6.", session.Projection.Rows[1].Marker);
        session.Undo();
        Assert.Same(list, session.Root.Content[0]);
    }
}
