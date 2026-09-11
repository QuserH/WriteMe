using System.Collections.Immutable;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class DocumentNavigationTests
{
    private static NoteNode Doc(params NoteNode[] nodes) => new("doc") { Content = [.. nodes] };
    private static NoteNode TaskItem(string text, bool done = false) => new NoteNode("taskItem") { Content = [NoteNode.Paragraph(text)] }.WithAttr("checked", done);

    [Fact]
    public void IndexIncludesHiddenAndScopedTasksAndCoalescesFormattedLinks()
    {
        var task = TaskItem("整理摘录");
        var cellTask = TaskItem("检查引用", true);
        var href = NoteMark.With("link", "href", "https://example.com/reference");
        var link = NoteNode.Paragraph() with { Content = [new("text") { Text = "参考", Marks = [href] }, new("text") { Text = "资料", Marks = [href, new("bold")] }] };
        var image = new NoteNode("image").WithAttr("name", "封面.png").WithAttr("assetId", new string('A', 64));
        var file = new NoteNode("attachment").WithAttr("name", "阅读笔记.pdf");
        var table = new NoteNode("table") { Content = [new("tableRow") { Content = [new("tableCell") { Content = [new("taskList") { Content = [cellTask] }, file] }] }] };
        var root = Doc(NoteNode.Toggle("稍后整理", new NoteNode("taskList") { Content = [task] }, table, image, link).WithAttr("collapsed", true),
            new("futureWidget") { Content = [new("taskList") { Content = [TaskItem("未知块不可编辑")] }] });
        var index = DocumentNavigation.For(root);
        Assert.Same(index, DocumentNavigation.For(root));
        Assert.Equal(new[] { "整理摘录", "检查引用" }, index.Tasks.Select(item => item.Text));
        Assert.Equal("稍后整理", index.Tasks[0].Context); Assert.Equal("表格", index.Tasks[1].Context);
        Assert.True(index.Tasks[1].Completed);
        Assert.Equal(3, index.Resources.Length);
        var resource = Assert.Single(index.Resources.Where(item => item.Kind == "link"));
        Assert.Equal("参考资料", resource.Name); Assert.Equal(4, resource.Length); Assert.Equal(link.Id, resource.NodeId);
    }

    [Fact]
    public void FindUsesLiteralTextOffsetsAcrossMarksSoftBreaksAndScopes()
    {
        var text = NoteNode.Paragraph() with { Content = [new("text") { Text = "Review中", Marks = [new("bold")] }, new("text") { Text = "文😀 review" }, new("hardBreak"), new("text") { Text = "继续" }] };
        var column = new NoteNode("columnList") { Content = [new("column") { Content = [NoteNode.Paragraph("review [a].*")] }] };
        var root = Doc(NoteNode.Toggle("隐藏段落", text, column).WithAttr("collapsed", true));
        var index = DocumentNavigation.For(root);
        Assert.Equal(3, index.Search("review").Matches.Length);
        Assert.Single(index.Search("Review", true).Matches);
        var unicode = Assert.Single(index.Search("中文😀").Matches);
        Assert.Equal(6, unicode.Start); Assert.Equal(4, unicode.Length); Assert.Equal(text.Id, unicode.NodeId);
        Assert.Single(index.Search("[a].*").Matches);
        Assert.Single(index.Search("review\n继续").Matches);
        Assert.Empty(index.Search("").Matches);
        Assert.Empty(index.Search("隐藏段落review").Matches);
    }

    [Fact]
    public void ReplaceAllPreservesTreeFormattingParagraphCommentsAndOneRootHistory()
    {
        var paragraph = NoteNode.Paragraph("阅读阅读") with { Content = [new("text") { Text = "阅读阅读", Marks = [new("bold")] }] };
        var cell = new NoteNode("tableCell") { Content = [NoteNode.Paragraph("继续阅读")] };
        var table = new NoteNode("table") { Content = [new("tableRow") { Content = [cell] }] };
        var hidden = NoteNode.Toggle("稍后阅读", paragraph, table);
        var session = new DocumentSession(Doc(hidden));
        var thread = session.AddComment("这一段还有补充", NoteComments.CaptureBlock(session, paragraph.Id));
        session.Toggle(hidden.Id);
        var before = session.Root; var revision = session.Revision;
        var result = DocumentNavigation.For(before).Search("阅读");
        Assert.Equal(4, session.ReplaceSearch(result, "重读"));
        Assert.Equal(revision + 1, session.Revision);
        Assert.True(session.Root.Content[0].Bool("collapsed"));
        var updated = NoteTree.Find(session.Root, paragraph.Id)!;
        Assert.Equal("重读重读", RichText.Plain(updated));
        Assert.Contains(updated.Content[0].Marks, mark => mark.Type == "bold");
        Assert.Equal(paragraph.Id, Assert.Single(NoteComments.For(session.Root).Spans(thread)).NodeId);
        Assert.Equal("继续重读", RichText.Plain(NoteTree.Find(session.Root, cell.Content[0].Id)!));
        session.Undo(); Assert.Same(before, session.Root);
        session.Redo(); Assert.Equal(4, DocumentNavigation.For(session.Root).Search("重读").Matches.Length);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteJson.ParseStrict(NoteJson.Serialize(session.Root))));
    }

    [Fact]
    public void SingleReplaceRevealsItsAncestorsInTheSameUndoStep()
    {
        var content = NoteNode.Paragraph("先阅读，再摘录");
        var child = NoteNode.Toggle("子章节", content).WithAttr("collapsed", true);
        var parent = NoteNode.Toggle("章节", child).WithAttr("collapsed", true);
        var session = new DocumentSession(Doc(parent)); var before = session.Root;
        var result = DocumentNavigation.For(before).Search("阅读");
        Assert.Equal(1, session.ReplaceSearch(result, "重读一遍", result.Matches[0]));
        Assert.Equal(1, session.Revision);
        Assert.False(NoteTree.Find(session.Root, parent.Id)!.Bool("collapsed"));
        Assert.False(NoteTree.Find(session.Root, child.Id)!.Bool("collapsed"));
        Assert.Equal(new EditorSelection(new(content.Id, 1), new(content.Id, 5)), session.Selection);
        session.Undo(); Assert.Same(before, session.Root); Assert.False(session.CanUndo);
        session.Redo(); Assert.Equal(new EditorSelection(new(content.Id, 1), new(content.Id, 5)), session.Selection);
    }

    [Fact]
    public void StaleOrTruncatedSearchCannotPartiallyReplaceAndSameTextIsNoOp()
    {
        var session = new DocumentSession(Doc(NoteNode.Paragraph("文字文字")));
        var initial = DocumentNavigation.For(session.Root).Search("文字");
        Assert.Equal(0, session.ReplaceSearch(initial, "文字")); Assert.False(session.CanUndo);
        session.Edit(0, 0, "新"); var current = session.Root;
        Assert.Throws<InvalidOperationException>(() => session.ReplaceSearch(initial, "替换")); Assert.Same(current, session.Root);
        var many = new DocumentSession(Doc(NoteNode.Paragraph(new string('x', DocumentNavigation.MaximumMatches + 1))));
        var truncated = DocumentNavigation.For(many.Root).Search("x");
        Assert.True(truncated.Truncated); Assert.Equal(DocumentNavigation.MaximumMatches, truncated.Matches.Length);
        Assert.Throws<InvalidOperationException>(() => many.ReplaceSearch(truncated, "y")); Assert.False(many.CanUndo);
        Assert.Equal(1, many.ReplaceSearch(truncated, "y", truncated.Matches[0]));
    }

    [Fact]
    public void ReplacementKeepsUnchangedTextAnnotationsAndInvalidatesTheReplacedRange()
    {
        var session = new DocumentSession(Doc(NoteNode.Paragraph("保留这段，替换那段")));
        var keep = session.AddComment("保留", NoteComments.Capture(session, 0, 4));
        var replace = session.AddComment("原文", NoteComments.Capture(session, 5, 4));
        var before = session.Root;
        session.ReplaceSearch(DocumentNavigation.For(before).Search("替换那段"), "新的文字");
        var comments = NoteComments.For(session.Root);
        Assert.Single(comments.Spans(keep)); Assert.Empty(comments.Spans(replace)); Assert.NotNull(comments.Find(replace));
        session.Undo(); Assert.Same(before, session.Root); Assert.Single(NoteComments.For(session.Root).Spans(replace));
    }
}
