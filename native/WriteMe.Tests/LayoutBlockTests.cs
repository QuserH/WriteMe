using System.Collections.Immutable;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class LayoutBlockTests
{
    private static DocumentSession Session(params NoteNode[] blocks) => new(new("doc") { Content = [.. blocks] });

    [Fact]
    public void CellsAndColumnsShareTheRootHistoryAndRestoreSelections()
    {
        var table = LayoutBlocks.Table(2, 2);
        var columns = LayoutBlocks.Columns();
        var session = Session(table, columns);
        using var cell = session.CreateScope(table.Content[0].Content[0].Id);
        using var column = session.CreateScope(columns.Content[1].Id);
        cell.Selection = EditorSelection.At(cell.Root.Content[0].Id);
        cell.Edit(0, 0, "单元格中文🙂");
        var afterCell = NoteJson.Serialize(session.Root);
        column.Selection = EditorSelection.At(column.Root.Content[0].Id);
        column.Edit(0, 0, "第二栏");
        Assert.True(session.CanUndo);
        Assert.Equal("第二栏", column.Projection.Text);
        cell.Undo();
        Assert.Equal(afterCell, NoteJson.Serialize(session.Root));
        Assert.Equal("", column.Projection.Text);
        Assert.Equal(column.Root.Content[0].Id, session.Selection.Caret.NodeId);
        cell.Undo();
        Assert.Equal("", cell.Projection.Text);
        Assert.Equal(cell.Root.Content[0].Id, session.Selection.Caret.NodeId);
        session.Redo(); session.Redo();
        Assert.Equal("单元格中文🙂", cell.Projection.Text);
        Assert.Equal("第二栏", column.Projection.Text);
        Assert.False(session.CanRedo);
    }

    [Fact]
    public void TableWidthsSurviveInsertedRowsMarkdownAndUndo()
    {
        var table = LayoutBlocks.Table(2, 3); var session = Session(table);
        session.SetTableColumnWidths(table.Id, 180, 240, 300);
        Assert.False(session.SetTableColumnWidths(table.Id, 180, 240, 300));
        var resized = NoteJson.Serialize(session.Root);
        session.InsertTableRow(table.Id, 0); session.DeleteTableRow(table.Id, 1);
        Assert.Equal(new[] { 180, 240, 300 }, LayoutBlocks.ColumnWidths(session.Root.Content[0]));
        var exported = NoteMarkdown.Export(session.Root);
        Assert.Contains("writeme-json", exported);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteMarkdown.Parse(exported)));
        session.Undo(); session.Undo(); Assert.Equal(resized, NoteJson.Serialize(session.Root));
        session.Undo(); Assert.All(LayoutBlocks.ColumnWidths(session.Root.Content[0]), width => Assert.Equal(0, width));
        Assert.Throws<ArgumentException>(() => session.SetTableColumnWidths(table.Id, 10, 20, 30));
    }

    [Fact]
    public void ScopedSelectionEnterIsOneUndoAndRemovedScopesCannotWrite()
    {
        var table = LayoutBlocks.Table(1, 1);
        var session = Session(table);
        using var cell = session.CreateScope(table.Content[0].Content[0].Id);
        cell.Edit(0, 0, "前中后", false);
        cell.Selection = cell.Projection.Selection(1, 2);
        cell.ReplaceSelectionWithEnter(1, 1);
        Assert.Equal("前\n后", cell.Projection.Text);
        session.Undo();
        Assert.Equal("前中后", cell.Projection.Text);
        Assert.Equal(cell.Projection.Selection(1, 2), cell.Selection);
        session.DeleteBlock(table.Id);
        Assert.False(cell.IsScopeAttached);
        var deleted = NoteJson.Serialize(session.Root);
        cell.Edit(0, 0, "旧回调");
        Assert.Equal(deleted, NoteJson.Serialize(session.Root));
        session.Undo();
        Assert.True(cell.IsScopeAttached);
        Assert.Equal("前中后", cell.Projection.Text);
    }

    [Fact]
    public void TsvPasteExpandsAtomicallyAndClearingKeepsTheGrid()
    {
        var table = LayoutBlocks.Table(2, 2);
        var session = Session(table);
        Assert.True(session.PasteTableCells(table.Id, 1, 1, "\"含\t制表符\"\t\"换\n行\"\r\n\"引号\"\"值\"\t末尾\r\n"));
        var pasted = session.Root.Content[0];
        Assert.Equal(3, pasted.Content.Length);
        Assert.Equal(3, pasted.Content[0].Content.Length);
        Assert.Equal("含\t制表符", LayoutBlocks.CellText(pasted.Content[1].Content[1]));
        Assert.Equal("换\n行", LayoutBlocks.CellText(pasted.Content[1].Content[2]));
        var range = new CellRange(1, 1, 2, 2);
        Assert.Equal(new[] { "含\t制表符", "换\n行", "引号\"值", "末尾" }, LayoutBlocks.ParseTsv(LayoutBlocks.CopyCells(pasted, range)).SelectMany(row => row));
        session.ClearTableCells(table.Id, range);
        Assert.Equal(3, session.Root.Content[0].Content.Length);
        Assert.Equal("", LayoutBlocks.CellText(session.Root.Content[0].Content[1].Content[1]));
        session.Undo(); Assert.Equal(NoteJson.Serialize(pasted), NoteJson.Serialize(session.Root.Content[0]));
        session.Undo(); Assert.Equal(NoteJson.Serialize(table), NoteJson.Serialize(session.Root.Content[0]));
    }

    [Fact]
    public void RowColumnAndHeaderOperationsPreserveContentAndUndo()
    {
        var table = LayoutBlocks.Table(2, 2);
        var session = Session(table);
        session.PasteTableCells(table.Id, 0, 0, "名称\t状态\n设计\t完成");
        var original = NoteJson.Serialize(session.Root);
        session.InsertTableRow(table.Id, 0);
        Assert.True(LayoutBlocks.HasHeader(session.Root.Content[0]));
        Assert.All(session.Root.Content[0].Content[1].Content, cell => Assert.Equal("tableCell", cell.Type));
        session.InsertTableColumn(table.Id, 1);
        Assert.Equal("状态", LayoutBlocks.CellText(session.Root.Content[0].Content[1].Content[2]));
        session.DeleteTableRow(table.Id, 0);
        session.DeleteTableColumn(table.Id, 1);
        Assert.Equal(original, NoteJson.Serialize(session.Root));
        for (var i = 0; i < 4; i++) session.Undo();
        Assert.Equal(original, NoteJson.Serialize(session.Root));
        session.SetTableHeader(table.Id, false);
        Assert.All(session.Root.Content[0].Content[0].Content, cell => Assert.Equal("tableCell", cell.Type));
        session.Undo(); Assert.True(LayoutBlocks.HasHeader(session.Root.Content[0]));
    }

    [Fact]
    public void ReducingAndUnwrappingColumnsPreservesMarkedHiddenContent()
    {
        var marked = NoteNode.Paragraph() with { Content = [new("text") { Text = "第三栏", Marks = [new("bold")] }] };
        var hidden = NoteNode.Toggle("收起", marked).WithAttr("collapsed", true);
        var columns = LayoutBlocks.Columns(3);
        columns = columns with { Content = [columns.Content[0] with { Content = [NoteNode.Paragraph("左")] }, columns.Content[1] with { Content = [NoteNode.Paragraph("中")] }, columns.Content[2] with { Content = [hidden] }] };
        var session = Session(columns);
        var original = NoteJson.Serialize(session.Root);
        session.SetColumns(columns.Id, 2, 2, 1);
        Assert.Equal(new[] { "中", "收起", "第三栏" }, NoteTree.Descendants(session.Root.Content[0].Content[1]).Where(node => node.IsTextBlock).Select(RichText.Plain));
        session.UnwrapColumns(columns.Id);
        Assert.Equal(new[] { "paragraph", "paragraph", "toggleBlock" }, session.Root.Content.Select(node => node.Type));
        Assert.True(session.Root.Content[2].Bool("collapsed"));
        Assert.Equal("bold", session.Root.Content[2].Content[1].Content[0].Marks[0].Type);
        session.Undo(); session.Undo(); Assert.Equal(original, NoteJson.Serialize(session.Root));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("columnList")]
    [InlineData("image")]
    public void CrossBlockReplacementRemovesAtomicContentAndUndoRestoresEverything(string kind)
    {
        var atom = kind == "table" ? LayoutBlocks.Table() : kind == "columnList" ? LayoutBlocks.Columns() : new NoteNode("image").WithAttr("assetId", "asset");
        var session = Session(NoteNode.Paragraph("前缀删除"), atom, NoteNode.Toggle("尾部正文", NoteNode.Paragraph("子树")));
        var original = NoteJson.Serialize(session.Root);
        session.Edit(2, session.Projection.Rows[2].Start + 2 - 2, "替换", false);
        Assert.Equal("前缀替换正文\n子树", session.Projection.Text);
        session.Undo(); Assert.Equal(original, NoteJson.Serialize(session.Root));
        var row = session.Projection.Rows[1];
        session.Edit(row.Start, 1, "");
        Assert.DoesNotContain(session.Root.Content, node => node.Id == atom.Id);
        session.Undo(); Assert.Equal(original, NoteJson.Serialize(session.Root));
    }

    [Fact]
    public void LayoutJsonRoundTripsAndHeadingsRemainNavigable()
    {
        var heading = NoteNode.Paragraph("栏中章节").WithAttr("level", 2) with { Type = "heading" };
        var columns = LayoutBlocks.Columns();
        columns = columns with { Content = columns.Content.SetItem(1, columns.Content[1] with { Content = [NoteNode.Toggle("隐藏", heading).WithAttr("collapsed", true)] }) };
        var table = LayoutBlocks.Table(2, 2).WithAttr("striped", true);
        var session = Session(columns, table);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteJson.Parse(NoteJson.Serialize(session.Root))));
        Assert.Equal(heading.Id, Assert.Single(DocumentOutline.Read(session.Root)).NodeId);
        Assert.True(session.Reveal(heading.Id));
        Assert.False(session.Root.Content[0].Content[1].Content[0].Bool("collapsed"));
        Assert.Equal(heading.Id, session.Selection.Caret.NodeId);
        Assert.Equal(columns.Id, session.Projection.LayoutHost(heading.Id)!.Node.Id);
    }

    [Fact]
    public void UnsupportedMergedTablesAndOversizedPastesNeverLoseData()
    {
        var table = LayoutBlocks.Table(2, 2);
        table = table with { Content = table.Content.SetItem(0, table.Content[0] with { Content = table.Content[0].Content.SetItem(0, table.Content[0].Content[0].WithAttr("colspan", 2)) }) };
        var session = Session(table);
        var original = NoteJson.Serialize(session.Root);
        Assert.False(session.InsertTableRow(table.Id, 0));
        Assert.False(session.PasteTableCells(table.Id, 0, 0, "不要覆盖"));
        Assert.Equal(original, NoteJson.Serialize(session.Root));
        Assert.False(session.CanUndo);
        var regular = LayoutBlocks.Table(1, LayoutBlocks.MaxColumns);
        session = Session(regular);
        Assert.Throws<ArgumentException>(() => session.PasteTableCells(regular.Id, 0, LayoutBlocks.MaxColumns - 1, "1\t2"));
        Assert.Equal(NoteJson.Serialize(regular), NoteJson.Serialize(session.Root.Content[0]));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void MarkdownTablesKeepCellsFormattingAndAlignmentAndComplexLayoutsUseLosslessPayloads()
    {
        var imported = NoteMarkdown.Parse("| **名称** | 状态 |\n| --- | ---: |\n| 设计 \\| 排版 | 进行中 |\n");
        var table = Assert.Single(imported.Content);
        Assert.Equal("table", table.Type); Assert.True(LayoutBlocks.IsEditableTable(table));
        Assert.Equal("bold", table.Content[0].Content[0].Content[0].Content[0].Marks[0].Type);
        Assert.Equal("right", table.Content[1].Content[1].Content[0].String("textAlign"));
        var exported = NoteMarkdown.Export(imported);
        Assert.StartsWith("| **名称**", exported); Assert.DoesNotContain("writeme-json", exported);
        Assert.Equal(NoteJson.Serialize(imported), NoteJson.Serialize(NoteMarkdown.Parse(exported)));
        var session = Session(table, LayoutBlocks.Columns());
        session.SetTableStriped(table.Id, true);
        var complex = NoteMarkdown.Export(session.Root);
        Assert.Contains("writeme-json", complex);
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(NoteMarkdown.Parse(complex)));
    }
}
