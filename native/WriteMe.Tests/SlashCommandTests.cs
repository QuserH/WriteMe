using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class SlashCommandTests
{
    [Theory]
    [InlineData(SlashOperation.Alignment, "center")]
    [InlineData(SlashOperation.Format, "italic")]
    [InlineData(SlashOperation.TextColor, "#426BB3")]
    public void FormattingCommandsRemoveTheQueryAndApplyInOneRootTransaction(SlashOperation operation, string value)
    {
        var paragraph = NoteNode.Paragraph() with { Content = [new("text") { Text = "/查询" }, new("text") { Text = "原有正文", Marks = [new("bold")] }] };
        var table = LayoutBlocks.Table(2, 2);
        var cell = table.Content[0].Content[0] with { Content = [paragraph] };
        table = NoteTree.Update(table, cell.Id, _ => cell);
        using var root = new DocumentSession(new("doc") { Content = [table] });
        using var session = root.CreateScope(cell.Id);
        session.Selection = EditorSelection.At(paragraph.Id, 3);
        var before = NoteJson.Serialize(root.Root); var selection = root.Selection;
        Assert.True(session.ApplySlash(paragraph.Id, "/查询", new(operation, value)));
        Assert.Equal(1, root.Revision);
        var result = NoteTree.Find(root.Root, paragraph.Id)!;
        Assert.Equal("原有正文", RichText.Plain(result));
        Assert.Contains(result.Content[0].Marks, mark => mark.Type == "bold");
        if (operation == SlashOperation.Alignment) Assert.Equal(value, result.String("textAlign"));
        if (operation == SlashOperation.Format) Assert.Contains(result.Content[0].Marks, mark => mark.Type == value);
        if (operation == SlashOperation.TextColor) Assert.Equal(value, TextColor.Read(result.Content[0].Marks));
        root.Undo(); Assert.Equal(before, NoteJson.Serialize(root.Root)); Assert.Equal(selection, root.Selection); Assert.False(root.CanUndo);
        root.Redo(); Assert.Equal("原有正文", session.Projection.Text);
    }

    [Fact]
    public void InvalidQueryOrIndentKeepsBothTheDocumentAndHistoryUntouched()
    {
        var paragraph = NoteNode.Paragraph("/减少缩进正文");
        using var session = new DocumentSession(new("doc") { Content = [paragraph] });
        var before = session.Root;
        Assert.False(session.ApplySlash(paragraph.Id, "/错误查询", new(SlashOperation.Block, "toggleBlock")));
        Assert.False(session.ApplySlash(paragraph.Id, "/减少缩进", new(SlashOperation.Outdent)));
        Assert.False(session.ApplySlash(paragraph.Id, "/", new(SlashOperation.Indent)));
        Assert.Same(before, session.Root); Assert.False(session.CanUndo);
    }

    [Fact]
    public void EmptyBlockFormattingAppliesToFollowingTypingAndCanBeUndone()
    {
        var paragraph = NoteNode.Paragraph("/粗体");
        using var session = new DocumentSession(new("doc") { Content = [paragraph] });
        Assert.True(session.ApplySlash(paragraph.Id, "/粗体", new(SlashOperation.Format, "bold")));
        session.Edit(0, 0, "继续输入");
        Assert.Contains(session.Root.Content[0].Content[0].Marks, mark => mark.Type == "bold");
        session.Undo(); Assert.Equal("", session.Projection.Text);
        session.Undo(); Assert.Equal("/粗体", session.Projection.Text);
    }

    [Fact]
    public void IndentCommandsKeepChildrenAndStateAndMoveOnlyOneLevel()
    {
        var third = NoteNode.Toggle("/减少缩进三级", NoteNode.Paragraph("子内容")).WithAttr("collapsed", true);
        var second = NoteNode.Toggle("二级", third);
        using var session = new DocumentSession(new("doc") { Content = [NoteNode.Toggle("一级", second)] });
        var before = NoteJson.Serialize(session.Root);
        Assert.True(session.ApplySlash(third.Content[0].Id, "/减少缩进", new(SlashOperation.Outdent)));
        var moved = session.Root.Content[0].Content[2];
        Assert.Equal(third.Id, moved.Id); Assert.True(moved.Bool("collapsed"));
        Assert.Equal("三级", RichText.Plain(moved.Content[0])); Assert.Equal(third.Content[1], moved.Content[1]);
        session.Undo(); Assert.Equal(before, NoteJson.Serialize(session.Root)); Assert.False(session.CanUndo);
    }
}
