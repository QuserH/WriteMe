using System.Collections.Immutable;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class CommentTests
{
    private static DocumentSession Session(params NoteNode[] blocks) => new(new("doc") { Content = [.. blocks] });
    private static CommentThread Thread(DocumentSession session, Guid id) => NoteComments.For(session.Root).Find(id)!;

    [Fact]
    public void DiscussionLifecycleUsesOneRootHistoryAndRejectsStaleActions()
    {
        var session = Session(NoteNode.Paragraph("原文"));
        var id = session.AddComment("  需要补充来源  "); var first = Thread(session, id);
        Assert.False(first.Anchored); Assert.Equal("需要补充来源", first.Messages[0].Text);
        session.ReplyComment(first, "已经补齐"); var replied = Thread(session, id);
        Assert.Throws<InvalidOperationException>(() => session.ReplyComment(first, "过期的回复"));
        session.EditComment(replied, replied.Messages[1].Id, "已补充出处");
        var edited = Thread(session, id); Assert.NotNull(edited.Messages[1].EditedAt);
        var revision = session.Revision; session.EditComment(edited, edited.Messages[1].Id, "已补充出处"); Assert.Equal(revision, session.Revision);
        session.DeleteCommentReply(Thread(session, id), edited.Messages[1].Id); Assert.True(Thread(session, id).Messages[1].Deleted);
        Assert.Throws<InvalidOperationException>(() => session.ReplyComment(Thread(session, id), "不能回复已删除消息", edited.Messages[1].Id));
        session.Undo(); Assert.Equal(2, Thread(session, id).Messages.Length);
        session.DeleteComment(Thread(session, id)); Assert.Empty(NoteComments.For(session.Root).Threads);
        session.Undo(); Assert.Equal(2, Thread(session, id).Messages.Length); session.Redo(); Assert.Empty(NoteComments.For(session.Root).Threads);
    }

    [Fact]
    public void OverlappingAnnotationsSurviveClearFormattingAndDeleteIndependently()
    {
        var session = Session(NoteNode.Paragraph("甲乙丙丁戊"));
        session.Format(0, 5, new("bold"));
        var a = session.AddComment("前半", NoteComments.Capture(session, 0, 3));
        var b = session.AddComment("后半", NoteComments.Capture(session, 2, 3));
        session.Format(0, 5, null);
        Assert.False(new SelectionFormats(session.Projection, 0, 5).HasFormatting);
        Assert.Equal(3, Assert.Single(NoteComments.For(session.Root).Spans(a)).Length);
        Assert.Equal(2, NoteComments.Ids(RichText.Slice(session.Root.Content[0].Content, 2, 1)[0].Marks).Length);
        session.DeleteComment(Thread(session, a));
        Assert.Empty(NoteComments.For(session.Root).Spans(a)); Assert.Equal(3, Assert.Single(NoteComments.For(session.Root).Spans(b)).Length);
        session.Undo(); Assert.Equal(2, NoteComments.For(session.Root).Threads.Length);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteJson.ParseStrict(NoteJson.Serialize(session.Root))));
    }

    [Fact]
    public void InsertionAtEitherBoundaryDoesNotExtendAnnotationButInteriorTypingDoes()
    {
        var session = Session(NoteNode.Paragraph("前甲乙后"));
        var id = session.AddComment("引用", NoteComments.Capture(session, 1, 2));
        session.Edit(1, 0, "A", false); session.Edit(4, 0, "B", false);
        Assert.Equal(new CommentSpan(id, session.Root.Content[0].Id, 2, 2), Assert.Single(NoteComments.For(session.Root).Spans(id)));
        session.Format(3, 0, new("italic")); session.Edit(3, 0, "中文🙂", false);
        Assert.Equal(6, Assert.Single(NoteComments.For(session.Root).Spans(id)).Length);
        session.Edit(2, 6, "替换", false);
        Assert.Empty(NoteComments.For(session.Root).Spans(id)); Assert.Equal("甲乙", Thread(session, id).Quote);
        session.Edit(4, 0, "继续", false); Assert.Empty(NoteComments.For(session.Root).Spans(id));
        session.Undo(); session.Undo(); Assert.Equal(6, Assert.Single(NoteComments.For(session.Root).Spans(id)).Length);
    }

    [Fact]
    public void MovingConvertingSplittingAndDuplicatingBlocksKeepOnlyOriginalAssociations()
    {
        var paragraph = NoteNode.Paragraph("甲乙丙丁"); var parent = NoteNode.Toggle("折叠", paragraph).WithAttr("collapsed", false);
        var after = NoteNode.Paragraph("尾部"); var session = Session(parent, after);
        var row = session.Projection.Find(paragraph.Id)!;
        var id = session.AddComment("移动原文", NoteComments.Capture(session, row.Start, 4));
        session.Move(paragraph.Id, after.Id, DropPlacement.After);
        Assert.Equal(paragraph.Id, Assert.Single(NoteComments.For(session.Root).Spans(id)).NodeId);
        session.ConvertBlock(session.Projection.Find(paragraph.Id)!.Start, "codeBlock");
        Assert.Equal(4, Assert.Single(NoteComments.For(session.Root).Spans(id)).Length);
        session.ConvertBlock(session.Projection.Find(paragraph.Id)!.Start, "heading", 2);
        session.DuplicateBlock(paragraph.Id);
        Assert.Single(NoteComments.For(session.Root).Spans(id));
        row = session.Projection.Find(paragraph.Id)!; session.Enter(row.Start + 2);
        Assert.Equal(2, NoteComments.For(session.Root).Spans(id).Length);
        Assert.Equal(4, NoteComments.For(session.Root).Spans(id).Sum(span => span.Length));
    }

    [Fact]
    public void DeletedSourceBecomesOrphanAndUndoRestoresTheReference()
    {
        var child = NoteNode.Paragraph("保留这段引用"); var parent = NoteNode.Toggle("父项", child).WithAttr("collapsed", false);
        var session = Session(parent); var row = session.Projection.Find(child.Id)!;
        var id = session.AddComment("内容删除后仍可查看", NoteComments.Capture(session, row.Start, row.Text.Length));
        session.DeleteBlock(parent.Id);
        Assert.True(Thread(session, id).Anchored); Assert.Empty(NoteComments.For(session.Root).Spans(id));
        session.Undo(); Assert.Single(NoteComments.For(session.Root).Spans(id));
        session.Undo(); Assert.Empty(NoteComments.For(session.Root).Threads);
    }

    [Fact]
    public void ScopedAnnotationsWriteRootMetadataAndRestoreNestedSelection()
    {
        var table = LayoutBlocks.Table(1, 1); var columns = LayoutBlocks.Columns(); var session = Session(table, columns);
        using var cell = session.CreateScope(table.Content[0].Content[0].Id);
        using var column = session.CreateScope(columns.Content[1].Id);
        cell.Edit(0, 0, "单元格"); column.Edit(0, 0, "栏内文本");
        cell.Selection = cell.Projection.Selection(0, 3);
        var a = cell.AddComment("表格批注", NoteComments.Capture(cell, 0, 3));
        column.Selection = column.Projection.Selection(0, 4);
        var b = column.AddComment("分栏批注", NoteComments.Capture(column, 0, 4));
        Assert.Equal(2, NoteComments.For(session.Root).Threads.Length); Assert.False(cell.Root.Attrs.ContainsKey(NoteComments.Attribute));
        session.Undo(); Assert.Null(NoteComments.For(session.Root).Find(b)); Assert.Single(NoteComments.For(session.Root).Spans(a));
        session.Redo(); Assert.Equal(column.Selection, session.Selection);
        session.DeleteBlock(table.Id); Assert.Throws<InvalidOperationException>(() => cell.AddComment("旧区域"));
    }

    [Fact]
    public void DraftsCanFollowMovesButDoNotGuessAtChangedTextOrAReopenedDifferentVersion()
    {
        var text = NoteNode.Paragraph("相同文字相同文字"); var after = NoteNode.Paragraph("尾"); var session = Session(text, after);
        var anchor = NoteComments.Capture(session, 0, 4)!;
        var reopened = new DocumentSession(NoteJson.ParseStrict(NoteJson.Serialize(session.Root)));
        Assert.NotEqual(Guid.Empty, reopened.AddComment("重开未变文档", anchor));
        session.Move(text.Id, after.Id, DropPlacement.After); Assert.NotEqual(Guid.Empty, session.AddComment("移动后保留", anchor));
        var row = session.Projection.Find(text.Id)!; session.Edit(row.Start, 0, "相同文字", false);
        var before = NoteJson.Serialize(session.Root);
        Assert.Throws<InvalidOperationException>(() => session.AddComment("不猜测重复文字", anchor));
        Assert.Equal(before, NoteJson.Serialize(session.Root));
        Assert.Throws<InvalidOperationException>(() => Session(NoteNode.Paragraph("另一篇")).AddComment("旧草稿", anchor));
    }

    [Fact]
    public void InvalidAndFutureMetadataRemainIntactAndCannotBeOverwritten()
    {
        var invalid = NoteNode.EmptyDocument().WithAttr(NoteComments.Attribute, new { version = 1, threads = "bad" });
        var session = new DocumentSession(invalid);
        Assert.False(NoteComments.For(session.Root).CanEdit);
        Assert.Throws<InvalidOperationException>(() => session.AddComment("不能覆盖"));
        Assert.Throws<InvalidDataException>(() => NoteJson.ParseStrict(NoteJson.Serialize(invalid)));
        var future = invalid.WithAttr(NoteComments.Attribute, new { version = 2, threads = new[] { "future" } });
        var loaded = NoteJson.ParseStrict(NoteJson.Serialize(future));
        Assert.Equal(NoteJson.Serialize(future), NoteJson.Serialize(loaded)); Assert.True(NoteComments.For(loaded).UnsupportedVersion);
        Assert.Throws<InvalidOperationException>(() => Session(NoteNode.Paragraph()).AddComment("  \n "));
        Assert.Throws<InvalidOperationException>(() => Session(NoteNode.Paragraph()).AddComment(new string('字', NoteComments.MaxMessageLength + 1)));
    }

    [Fact]
    public void LongQuotesAndCrossBlockSoftBreakAnchorsPreserveUnicodeOnRoundTrip()
    {
        var session = Session(NoteNode.Paragraph(new string('甲', 1998) + "🙂末尾"), NoteNode.Paragraph("中文\u2028续行"));
        var anchor = NoteComments.Capture(session, 0, session.Projection.Text.Length)!;
        var id = session.AddComment("两个段落的批注", anchor);
        Assert.Equal(2, NoteComments.For(session.Root).Spans(id).Length);
        var saved = NoteJson.ParseStrict(NoteJson.Serialize(session.Root));
        Assert.DoesNotContain('\uFFFD', NoteComments.For(saved).Find(id)!.Quote);
        Assert.Equal(anchor.Quote, NoteComments.For(saved).Find(id)!.Quote);
        Assert.Equal(session.Projection.Text, new DocumentProjection(saved).Text);
    }

    [Fact]
    public void WholeParagraphCommentsSurviveEmptyTextReplacementSplitsAndStyleChanges()
    {
        var paragraph = NoteNode.Paragraph(); var session = Session(paragraph);
        var draft = NoteComments.CaptureBlock(session, paragraph.Id)!;
        session.Edit(0, 0, "草稿期间补充的正文", false);
        var id = session.AddComment("这一整段的意见", draft);
        Assert.True(Thread(session, id).WholeBlock); Assert.Equal("草稿期间补充的正文", Thread(session, id).Quote);
        session.Edit(0, session.Projection.Text.Length, "", false);
        Assert.Equal(0, Assert.Single(NoteComments.For(session.Root).Spans(id)).Length);
        Assert.Single(NoteComments.For(session.Root).InBlock(paragraph.Id));
        session.Edit(0, 0, "重新写过的段落", false); session.Enter(3);
        Assert.Equal(paragraph.Id, Assert.Single(NoteComments.For(session.Root).Spans(id)).NodeId);
        Assert.Empty(NoteComments.BlockIds(session.Root.Content[1]));
        session.ConvertBlock(0, "heading", 2); session.ConvertBlock(0, "toggleBlock");
        var toggle = session.Root.Content[0]; session.DuplicateBlock(toggle.Id);
        Assert.Single(NoteComments.For(session.Root).Spans(id));
        Assert.All(NoteTree.Descendants(session.Root.Content[1]), node => Assert.Empty(NoteComments.BlockIds(node)));
        session.Format(0, 3, null); Assert.Single(NoteComments.For(session.Root).InBlock(paragraph.Id));
    }

    [Fact]
    public void ParagraphDiscussionsFollowMovesAndMergesButWholeBlockDeletionKeepsAnOrphan()
    {
        var a = NoteNode.Paragraph("第一段"); var b = NoteNode.Paragraph("第二段"); var session = Session(a, b);
        var first = session.AddComment("前段", NoteComments.CaptureBlock(session, a.Id));
        var second = session.AddComment("后段", NoteComments.CaptureBlock(session, b.Id));
        Assert.True(session.BackspaceAtStart(session.Projection.Find(b.Id)!.Start));
        Assert.Equal("第一段第二段", session.Projection.Text);
        Assert.Equal(2, NoteComments.For(session.Root).InBlock(a.Id).Length);
        Assert.Equal(a.Id, Assert.Single(NoteComments.For(session.Root).Spans(second)).NodeId);
        session.Undo(); Assert.Single(NoteComments.For(session.Root).InBlock(b.Id));
        session.DeleteBlock(b.Id); Assert.Empty(NoteComments.For(session.Root).Spans(second));
        Assert.Equal("后段", Thread(session, second).Messages[0].Text);
        session.Undo(); session.DeleteComment(Thread(session, first)); Assert.Empty(NoteComments.BlockIds(session.Root.Content[0]));
        Assert.Single(NoteComments.For(session.Root).InBlock(b.Id));
        var parent = NoteNode.Toggle("父级", NoteNode.Toggle("子级", a).WithAttr("collapsed", false)).WithAttr("collapsed", false);
        var nested = Session(parent, b);
        var nestedId = nested.AddComment("移出后保留", NoteComments.CaptureBlock(nested, a.Id));
        Assert.True(nested.Move(a.Id, parent.Id, DropPlacement.After));
        Assert.Equal(a.Id, Assert.Single(NoteComments.For(nested.Root).Spans(nestedId)).NodeId);
    }

    [Fact]
    public void EmptyCellColumnAndAtomicCommentsShareHistoryAndRoundTripWithStableIds()
    {
        var table = LayoutBlocks.Table(1, 1); var columns = LayoutBlocks.Columns(); var session = Session(table, columns);
        using var cell = session.CreateScope(table.Content[0].Content[0].Id);
        using var column = session.CreateScope(columns.Content[0].Id);
        var a = cell.AddComment("空单元格评论", NoteComments.CaptureBlock(cell, cell.Root.Content[0].Id));
        var b = column.AddComment("空栏评论", NoteComments.CaptureBlock(column, column.Root.Content[0].Id));
        var c = session.AddComment("整个表格评论", NoteComments.CaptureBlock(session, table.Id));
        Assert.False(cell.Root.Attrs.ContainsKey(NoteComments.Attribute)); Assert.False(column.Root.Attrs.ContainsKey(NoteComments.Attribute));
        session.Undo(); Assert.Null(NoteComments.For(session.Root).Find(c)); session.Redo();
        session.PasteTableCells(table.Id, 0, 0, "替换后的文字");
        Assert.Equal("替换后的文字".Length, Assert.Single(NoteComments.For(session.Root).Spans(a)).Length);
        session.ReplaceTableRange(table.Id, new CellRange(0, 0, 0, 0), null);
        Assert.Equal(0, Assert.Single(NoteComments.For(session.Root).Spans(a)).Length);
        var loaded = NoteJson.ParseStrict(NoteJson.Serialize(session.Root)); var index = NoteComments.For(loaded);
        Assert.All(new[] { a, b, c }, id => Assert.Single(index.Spans(id)));
        Assert.All(index.Threads, thread => Assert.True(thread.WholeBlock));
        Assert.DoesNotContain(NoteComments.BlockAttribute, NoteMarkdown.Export(loaded));
        session.DuplicateBlock(table.Id); Assert.Single(NoteComments.For(session.Root).Spans(a)); Assert.Single(NoteComments.For(session.Root).Spans(c));
        session.DeleteBlock(table.Id); Assert.Empty(NoteComments.For(session.Root).Spans(a));
        Assert.Throws<InvalidOperationException>(() => cell.AddComment("旧单元格"));
    }

    [Fact]
    public void ParagraphDraftsNeverRetargetADeletedBlockAndInvalidAnchorsAreRejected()
    {
        var paragraph = NoteNode.Paragraph("相同文字"); var session = Session(paragraph, NoteNode.Paragraph("相同文字"));
        var anchor = NoteComments.CaptureBlock(session, paragraph.Id)!;
        var same = new DocumentSession(NoteJson.ParseStrict(NoteJson.Serialize(session.Root)));
        Assert.NotEqual(Guid.Empty, same.AddComment("未修改的重开文档", anchor));
        session.DeleteBlock(paragraph.Id);
        Assert.Throws<InvalidOperationException>(() => session.AddComment("不可挂到相同文字上", anchor));
        session.Undo(); Assert.NotEqual(Guid.Empty, session.AddComment("撤销删除后可继续", anchor));
        var duplicateAnchor = session.Root with { Content = [session.Root.Content[0], session.Root.Content[0] with { Id = Guid.NewGuid() }] };
        Assert.Throws<InvalidDataException>(() => NoteJson.ParseStrict(NoteJson.Serialize(duplicateAnchor)));
        var invalid = new NoteNode("doc") { Content = [NoteNode.Paragraph().WithAttr(NoteComments.BlockAttribute, new[] { "bad-id" })] };
        Assert.Throws<InvalidDataException>(() => NoteJson.ParseStrict(NoteJson.Serialize(invalid)));
    }

    [Fact]
    public void RepliesCanTargetRepliesAndDeletingAnAncestorPreservesTheConversationTree()
    {
        var session = Session(NoteNode.Paragraph("帖子式的段落讨论"));
        var id = session.AddComment("第一条评论", NoteComments.CaptureBlock(session, session.Root.Content[0].Id));
        var original = Thread(session, id).Messages[0];
        session.ReplyComment(Thread(session, id), "回复评论", original.Id); var a = Thread(session, id).Messages[^1];
        session.ReplyComment(Thread(session, id), "回复这条回复", a.Id); var b = Thread(session, id).Messages[^1];
        session.ReplyComment(Thread(session, id), "还可以继续回复", b.Id);
        var final = Thread(session, id); Assert.Equal(a.Id, final.Messages[2].ReplyTo); Assert.Equal(b.Id, final.Messages[3].ReplyTo);
        session.DeleteCommentReply(final, a.Id); final = Thread(session, id);
        Assert.True(final.Messages[1].Deleted); Assert.Equal("", final.Messages[1].Text);
        Assert.Equal(a.Id, final.Messages[2].ReplyTo); Assert.Equal("回复这条回复", final.Messages[2].Text);
        Assert.Equal("还可以继续回复", final.Messages[3].Text);
        var reopened = NoteComments.For(NoteJson.ParseStrict(NoteJson.Serialize(session.Root))).Find(id)!;
        Assert.Equal(final.Messages.ToArray(), reopened.Messages.ToArray());
        Assert.Throws<InvalidOperationException>(() => session.EditComment(final, a.Id, "不能改已删除的回复"));
        session.Undo(); Assert.False(Thread(session, id).Messages[1].Deleted); session.Redo(); Assert.True(Thread(session, id).Messages[1].Deleted);
        var other = session.AddComment("另一条评论");
        Assert.Throws<InvalidOperationException>(() => session.ReplyComment(Thread(session, other), "不能串到另一个帖子", b.Id));
        var root = session.Root;
        var catalog = System.Text.Json.JsonSerializer.SerializeToElement(new { version = 1, threads = new[] { final with { Messages = final.Messages.SetItem(1, a with { ReplyTo = b.Id }) } } },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        Assert.Throws<InvalidDataException>(() => NoteJson.ParseStrict(NoteJson.Serialize(root.WithAttr(NoteComments.Attribute, catalog))));
    }
}
