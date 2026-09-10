using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class LayoutInteractionTests
{
    private static (Window Window, BlockEditor Editor) Open(params NoteNode[] blocks)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. blocks] }));
        var window = new Window { Width = 1000, Height = 750, Content = editor };
        window.Show(); editor.FocusText(); Frame(window); return (window, editor);
    }
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs();
    }
    private static void PressKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, ""); window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window);
    }
    private static T Find<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = control.TranslatePoint(new Point(Math.Min(20, control.Bounds.Width / 2), Math.Min(16, control.Bounds.Height / 2)), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers); Frame(window);
    }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window); using var bitmap = window.CaptureRenderedFrame(); bitmap?.Save(Path.Combine(directory, name + ".png"));
    }

    private static void MenuAction(Window window, Guid block, string trigger, string item)
    {
        Click(window, Find<Button>(window, trigger + "_" + block));
        var menu = Assert.IsType<ContextMenu>(Find<Border>(window, "Layout_" + block).ContextMenu);
        var action = menu.Items.OfType<MenuItem>().Single(control => AutomationProperties.GetAutomationId(control) == item);
        action.RaiseEvent(new(MenuItem.ClickEvent)); menu.Close(); Frame(window);
    }

    private static async Task Wait(Func<bool> ready)
    {
        for (var i = 0; i < 100 && !ready(); i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Assert.True(ready());
    }

    [AvaloniaFact]
    public void TableTypingKeepsTheNativeEditorFocusAndImeAndSharesUndo()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(NoteNode.Paragraph("表格编辑"), table, NoteNode.Paragraph("尾部"));
        try
        {
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0"));
            var active = editor.ActiveEditor;
            Assert.NotSame(editor, active);
            window.KeyTextInput("中文"); Frame(window);
            Assert.Same(active, editor.ActiveEditor); Assert.True(active.Surface.TextArea.IsFocused);
            Assert.Equal("中文", LayoutBlocks.CellText(editor.Session.Root.Content[1].Content[0].Content[0]));
            active.InputClient.SetPreeditText("shuru");
            var before = NoteJson.Serialize(editor.Session.Root);
            PressKey(window, Key.Tab); PressKey(window, Key.Delete); PressKey(window, Key.Enter);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.Same(active, editor.ActiveEditor);
            active.InputClient.SetPreeditText(null); window.KeyTextInput("输入"); Frame(window);
            Assert.Equal("中文输入", active.Surface.Text);
            PressKey(window, Key.Tab); window.KeyTextInput("第二格"); Frame(window);
            Assert.Equal(table.Content[0].Content[1].Id, editor.ActiveEditor.Session.ScopeId);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal("", LayoutBlocks.CellText(editor.Session.Root.Content[1].Content[0].Content[1]));
            Assert.Equal("中文输入", LayoutBlocks.CellText(editor.Session.Root.Content[1].Content[0].Content[0]));
            PressKey(window, Key.Tab, RawInputModifiers.Shift); Assert.Equal(table.Content[0].Content[0].Id, editor.ActiveEditor.Session.ScopeId);
            Save(window, "table-editing");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TableRangeDeleteClearsCellsAndTheLastTabAddsOneUndoableRow()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table);
        try
        {
            editor.Session.PasteTableCells(table.Id, 0, 0, "名称\t状态\n设计\t完成"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0"));
            Click(window, Find<Border>(window, $"Cell_{table.Id}_1_1"), RawInputModifiers.Shift);
            PressKey(window, Key.Delete);
            Assert.Equal(2, editor.Session.Root.Content[0].Content.Length);
            Assert.All(editor.Session.Root.Content[0].Content.SelectMany(row => row.Content), cell => Assert.Equal("", LayoutBlocks.CellText(cell)));
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Escape);
            Click(window, Find<Border>(window, $"Cell_{table.Id}_1_1"));
            PressKey(window, Key.Tab); Assert.Equal(3, editor.Session.Root.Content[0].Content.Length);
            Assert.Equal(editor.Session.Root.Content[0].Content[2].Content[0].Id, editor.ActiveEditor.Session.ScopeId);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void BackspaceSelectsAnAtomicBlockThenDeletesAndRestoresItsContent()
    {
        var table = LayoutBlocks.Table();
        var tail = NoteNode.Paragraph("后文");
        var (window, editor) = Open(NoteNode.Paragraph("前文"), table, tail);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.Surface.CaretOffset = editor.Session.Projection.Find(tail.Id)!.Start;
            PressKey(window, Key.Back);
            Assert.Equal(1, editor.Surface.SelectionLength); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Back);
            Assert.DoesNotContain(editor.Session.Root.Content, node => node.Id == table.Id);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            editor.Surface.Select(1, editor.Session.Projection.Find(tail.Id)!.Start);
            window.KeyTextInput("替换"); Frame(window);
            Assert.Equal("前替换文", editor.Surface.Text);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ColumnsKeepFocusNavigateAndUnwrapWithoutLosingText()
    {
        var columns = LayoutBlocks.Columns();
        var (window, editor) = Open(NoteNode.Paragraph("布局试验").WithAttr("level", 1) with { Type = "heading" }, columns);
        try
        {
            editor.NavigateTo(columns.Content[0].Content[0].Id);
            window.KeyTextInput("左栏的中文内容，保持稳定输入。"); Frame(window);
            var left = editor.ActiveEditor; Assert.True(left.Surface.TextArea.IsFocused);
            PressKey(window, Key.Tab, RawInputModifiers.Control);
            Assert.NotSame(left, editor.ActiveEditor);
            window.KeyTextInput("右栏内容"); Frame(window);
            Assert.Equal("右栏内容", editor.ActiveEditor.Surface.Text);
            var before = NoteJson.Serialize(editor.Session.Root);
            Save(window, "columns-editing");
            Click(window, Find<Button>(window, "ColumnsUnwrap_" + columns.Id));
            Assert.DoesNotContain(editor.Session.Root.Content, node => node.Type == "columnList");
            Assert.Contains("左栏的中文内容", editor.Surface.Text); Assert.Contains("右栏内容", editor.Surface.Text);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            window.Width = 430; Frame(window); Save(window, "columns-narrow");
            Assert.True(Find<Border>(window, $"Column_{columns.Id}_1").Bounds.Y > Find<Border>(window, $"Column_{columns.Id}_0").Bounds.Y);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarFormattingTargetsTheActiveColumnAndRealAlignmentChangesTextPosition()
    {
        var columns = LayoutBlocks.Columns();
        var editor = new BlockEditor(new(new("doc") { Content = [columns] }));
        var sidebar = new EditorSidebar(editor); var content = new Grid { ColumnDefinitions = new("*,330") };
        content.Children.Add(editor); Grid.SetColumn(sidebar, 1); content.Children.Add(sidebar);
        var window = new Window { Width = 1220, Height = 760, Content = content };
        window.Show(); Frame(window);
        try
        {
            editor.NavigateTo(columns.Content[0].Content[0].Id);
            window.KeyTextInput("栏内文字"); Frame(window);
            var active = editor.ActiveEditor; active.Surface.Select(0, 4); sidebar.Open(EditorPanel.Style); Frame(window);
            Click(window, Find<Button>(window, "SidebarFormat_bold"));
            Assert.Equal("bold", editor.Session.Root.Content[0].Content[0].Content[0].Content[0].Marks[0].Type);
            Click(window, Find<Button>(window, "SidebarAlign_right"));
            Assert.Equal("right", editor.Session.Root.Content[0].Content[0].Content[0].String("textAlign"));
            var line = active.Surface.TextArea.TextView.VisualLines[0].TextLines[0];
            Assert.True(line.Start > 25);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Null(editor.Session.Root.Content[0].Content[0].Content[0].String("textAlign"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RepeatedShiftArrowsSelectARectangleAndCommittedImeReplacesItInOneUndo()
    {
        var table = LayoutBlocks.Table(3, 3);
        var (window, editor) = Open(table);
        try
        {
            editor.Session.PasteTableCells(table.Id, 0, 0, "甲\t乙\t丙\n丁\t戊\t己\n庚\t辛\t壬"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0")); PressKey(window, Key.Escape);
            Assert.True(editor.Surface.TextArea.IsFocused);
            PressKey(window, Key.Right, RawInputModifiers.Shift); PressKey(window, Key.Right, RawInputModifiers.Shift);
            PressKey(window, Key.Down, RawInputModifiers.Shift); PressKey(window, Key.Down, RawInputModifiers.Shift);
            editor.InputClient.SetPreeditText("zhongwen");
            PressKey(window, Key.Delete); PressKey(window, Key.Enter); PressKey(window, Key.Tab);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            window.KeyTextInput("中文"); Frame(window);
            Assert.Single(editor.Session.Root.Content);
            var cells = editor.Session.Root.Content[0].Content.SelectMany(row => row.Content).ToArray();
            Assert.Equal("中文", LayoutBlocks.CellText(cells[0]));
            Assert.All(cells.Skip(1), cell => Assert.Equal("", LayoutBlocks.CellText(cell)));
            Assert.Equal(cells[0].Id, editor.ActiveEditor.Session.ScopeId);
            Assert.True(editor.ActiveEditor.Surface.TextArea.IsFocused);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void UndoAndRedoAcrossCellsReturnTheCaretToTheChangedCell()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table);
        try
        {
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0")); window.KeyTextInput("第一格"); Frame(window);
            PressKey(window, Key.Tab); window.KeyTextInput("第二格"); Frame(window);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(table.Content[0].Content[1].Id, editor.ActiveEditor.Session.ScopeId);
            Assert.Equal("", editor.ActiveEditor.Surface.Text);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(table.Content[0].Content[0].Id, editor.ActiveEditor.Session.ScopeId);
            Assert.True(editor.ActiveEditor.Surface.TextArea.IsFocused);
            Assert.Equal("", editor.ActiveEditor.Surface.Text);
            PressKey(window, Key.Y, RawInputModifiers.Control); Assert.Equal("第一格", editor.ActiveEditor.Surface.Text);
            PressKey(window, Key.Y, RawInputModifiers.Control); Assert.Equal("第二格", editor.ActiveEditor.Surface.Text);
            Assert.Equal(3, editor.ActiveEditor.Surface.CaretOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClipboardPastesQuotedCellsExpandsAndCutsWithOneUndo()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table);
        try
        {
            var clipboard = window.Clipboard!; Assert.NotNull(clipboard);
            await clipboard.SetTextAsync("甲\t乙\n\"多\n行\"\t尾");
            Click(window, Find<Border>(window, $"Cell_{table.Id}_1_1"));
            var before = NoteJson.Serialize(editor.Session.Root);
            PressKey(window, Key.V, RawInputModifiers.Control);
            await Wait(() => editor.Session.Root.Content[0].Content.Length == 3); Frame(window);
            var changed = editor.Session.Root.Content[0];
            Assert.Equal(3, changed.Content[0].Content.Length);
            Assert.Equal("甲", LayoutBlocks.CellText(changed.Content[1].Content[1]));
            Assert.Equal("多\n行", LayoutBlocks.CellText(changed.Content[2].Content[1]));
            Assert.Equal("尾", LayoutBlocks.CellText(changed.Content[2].Content[2]));
            var pasted = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Border>(window, $"Cell_{table.Id}_2_2"), RawInputModifiers.Shift);
            PressKey(window, Key.X, RawInputModifiers.Control);
            await Wait(() => LayoutBlocks.CellText(editor.Session.Root.Content[0].Content[1].Content[1]) == "");
            using (var data = await clipboard.TryGetDataAsync()) Assert.Equal("甲\t乙\n\"多\n行\"\t尾", await data!.TryGetTextAsync());
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(pasted, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0"));
            string? notice = null; editor.Notice += text => notice = text;
            await clipboard.SetTextAsync(string.Join('\t', Enumerable.Repeat("超限", 13)));
            PressKey(window, Key.V, RawInputModifiers.Control); await Wait(() => notice != null);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.Contains("12 列", notice);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TableMenuInsertsAtTheRequestedPositionAndKeepsTheOriginalCells()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table);
        try
        {
            editor.Session.PasteTableCells(table.Id, 0, 0, "甲\t乙\n丙\t丁"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0"));
            MenuAction(window, table.Id, "TableMenu", "TableInsertAbove");
            var updated = editor.Session.Root.Content[0];
            Assert.Equal("甲", LayoutBlocks.CellText(updated.Content[1].Content[0]));
            Assert.Equal(updated.Content[0].Content[0].Id, editor.ActiveEditor.Session.ScopeId);
            MenuAction(window, table.Id, "TableMenu", "TableInsertLeft"); updated = editor.Session.Root.Content[0];
            Assert.Equal("甲", LayoutBlocks.CellText(updated.Content[1].Content[1]));
            Assert.Equal(updated.Content[0].Content[0].Id, editor.ActiveEditor.Session.ScopeId);
            PressKey(window, Key.Z, RawInputModifiers.Control); PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Click(window, Find<Border>(window, $"Cell_{table.Id}_1_1"));
            MenuAction(window, table.Id, "TableMenu", "TableDeleteRow");
            Assert.Single(editor.Session.Root.Content[0].Content); Assert.Equal("乙", editor.ActiveEditor.Surface.Text);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DraggingTheColumnDividerCommitsOneUndoableWidthChange()
    {
        var columns = LayoutBlocks.Columns(); var (window, editor) = Open(columns);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var grip = Find<Border>(window, $"ColumnResize_{columns.Id}_0");
            var start = grip.TranslatePoint(new(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left); Frame(window);
            Assert.False(editor.Session.CanUndo);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(110, 0), RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            window.MouseUp(start + new Vector(110, 0), MouseButton.Left); Frame(window);
            var updated = editor.Session.Root.Content[0];
            Assert.True(updated.Content[0].Int("width") > updated.Content[1].Int("width"));
            Assert.True(Find<Border>(window, $"Column_{columns.Id}_0").Bounds.Width > Find<Border>(window, $"Column_{columns.Id}_1").Bounds.Width);
            editor.FocusText(); PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TableColumnResizeCanCancelAndPersistsOnlyTheReleasedWidth()
    {
        var table = LayoutBlocks.Table(2, 3); var (window, editor) = Open(table);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var grip = Find<Border>(window, $"TableResize_{table.Id}_0");
            var start = grip.TranslatePoint(new(4, 15), window)!.Value;
            window.MouseDown(start, MouseButton.Left); window.MouseUp(start, MouseButton.Left); Frame(window);
            Assert.False(editor.Session.CanUndo);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(80, 0), RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Escape); window.MouseUp(start + new Vector(80, 0), MouseButton.Left); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.False(editor.Session.CanUndo);
            window.MouseDown(start, MouseButton.Left); window.MouseMove(start + new Vector(80, 0), RawInputModifiers.LeftMouseButton); Frame(window);
            window.MouseUp(start + new Vector(80, 0), MouseButton.Left); Frame(window);
            var widths = LayoutBlocks.ColumnWidths(editor.Session.Root.Content[0]);
            Assert.True(widths[0] > widths[1]); Assert.All(widths, width => Assert.InRange(width, 80, 1200));
            Assert.True(Find<Border>(window, $"Cell_{table.Id}_0_0").Bounds.Width > Find<Border>(window, $"Cell_{table.Id}_0_1").Bounds.Width);
            PressKey(window, Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Y, RawInputModifiers.Control); Assert.Equal(widths.ToArray(), LayoutBlocks.ColumnWidths(editor.Session.Root.Content[0]).ToArray());
            MenuAction(window, table.Id, "TableMenu", "TableEqualWidths"); Assert.All(LayoutBlocks.ColumnWidths(editor.Session.Root.Content[0]), width => Assert.Equal(0, width));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AppearanceChangesReachCachedCellsAndColumnsWithoutReplacingTheirEditors()
    {
        var table = LayoutBlocks.Table(2, 2); var columns = LayoutBlocks.Columns();
        var (window, editor) = Open(table, columns);
        try
        {
            Click(window, Find<Border>(window, $"Cell_{table.Id}_0_0")); window.KeyTextInput("字号和主题"); Frame(window);
            var cellEditor = editor.ActiveEditor;
            editor.NavigateTo(columns.Content[0].Content[0].Id); var columnEditor = editor.ActiveEditor;
            editor.SetReferenceCatalogue([new("new-note", "新的链接目标", 1, 2)], [new("标签", "标签", 1)]);
            Assert.Equal("new-note", Assert.Single(cellEditor.ReferenceDocuments).Id);
            Assert.Equal("new-note", Assert.Single(columnEditor.ReferenceDocuments).Id);
            var revision = editor.Session.Revision;
            editor.Surface.FontSize = 20; editor.Surface.FontFamily = new("SimSun"); editor.Surface.Options.LineHeightFactor = 1.7;
            editor.PageBackgroundColor = Color.Parse("#252A31"); editor.Surface.Foreground = Brushes.Wheat; Frame(window);
            Assert.Equal(19, cellEditor.Surface.FontSize); Assert.Equal(20, columnEditor.Surface.FontSize);
            Assert.Equal(editor.Surface.FontFamily, cellEditor.Surface.FontFamily); Assert.Equal(1.7, columnEditor.Surface.Options.LineHeightFactor);
            var preview = Find<Border>(window, $"Cell_{table.Id}_1_1").GetVisualDescendants().OfType<TextBlock>().First();
            Assert.Equal(19, preview.FontSize); Assert.Equal(editor.Surface.FontFamily, preview.FontFamily);
            Assert.Equal(Colors.Wheat, Assert.IsAssignableFrom<ISolidColorBrush>(preview.Foreground).Color);
            Assert.Equal(Color.Parse("#3E4856"), Assert.IsAssignableFrom<ISolidColorBrush>(Find<Border>(window, $"Cell_{table.Id}_1_1").BorderBrush).Color);
            Assert.Same(columnEditor, editor.ActiveEditor); Assert.Equal(revision, editor.Session.Revision);
            Save(window, "layout-custom-appearance");
            editor.PageBackgroundColor = null; editor.Surface.Foreground = Brushes.White;
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(window);
            Assert.Equal(Color.Parse("#3E4856"), Assert.IsAssignableFrom<ISolidColorBrush>(Find<Border>(window, $"Column_{columns.Id}_0").BorderBrush).Color);
            editor.NavigateTo(table.Content[0].Content[0].Content[0].Id); Assert.Same(cellEditor, editor.ActiveEditor);
        }
        finally { window.Close(); Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
    }

    [AvaloniaFact]
    public void EnterOnAPartialUnknownBlockSelectionPreservesTheImportedData()
    {
        var unknown = new NoteNode("customWidget").WithAttr("value", "保留数据");
        var (window, editor) = Open(unknown);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root); editor.Surface.Select(1, 1);
            PressKey(window, Key.Enter); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            window.KeyTextInput("a"); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            PressKey(window, Key.Delete); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InactiveCellsShowAttachmentNamesAndKeepTheirNodesWhenOpened()
    {
        var table = LayoutBlocks.Table(1, 1);
        var image = new NoteNode("image").WithAttr("name", "参考图.png");
        table = table with { Content = [table.Content[0] with { Content = [table.Content[0].Content[0] with { Content = [image] }] }] };
        var (window, editor) = Open(table);
        try
        {
            var cell = Find<Border>(window, $"Cell_{table.Id}_0_0");
            Assert.Contains(cell.GetVisualDescendants().OfType<TextBlock>().SelectMany(text => text.Inlines?.OfType<Run>() ?? []), run => run.Text == "图片 · 参考图.png");
            var before = NoteJson.Serialize(editor.Session.Root); Click(window, cell);
            Assert.Equal("image", editor.ActiveEditor.Session.Root.Content[0].Type);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }
}
