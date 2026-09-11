using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class BlockDragInteractionTests
{
    private static (Window Window, BlockEditor Editor) Open(params NoteNode[] blocks)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. blocks] }));
        var window = new Window { Width = 780, Height = 580, Content = editor };
        window.Show(); editor.FocusText(); Frame(window);
        return (window, editor);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        using var settled = window.CaptureRenderedFrame();
    }

    private static Point Start(Window window, BlockEditor editor, Guid block)
    {
        var grip = editor.Surface.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("blockGrip") && Equals(button.Tag, block));
        var point = grip.TranslatePoint(new(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Frame(window);
        window.MouseDown(point, MouseButton.Left); Frame(window);
        return point;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingThirdLevelOneIndentLeftMakesItASiblingOfSecondLevel(bool collapsed)
    {
        var third = NoteNode.Toggle("三级折叠", NoteNode.Paragraph("保留内容"), NoteNode.Toggle("四级")).WithAttr("collapsed", collapsed);
        var second = NoteNode.Toggle("二级折叠", third, NoteNode.Paragraph("二级的尾部"));
        var first = NoteNode.Toggle("一级折叠", second, NoteNode.Paragraph("一级的尾部"));
        var (window, editor) = Open(first, NoteNode.Paragraph("文档尾部"));
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var start = Start(window, editor, third.Id);
            var target = start - new Vector(28, 0);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            SaveImage(window, collapsed ? "drag-promote-collapsed.png" : "drag-promote-expanded.png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            Assert.Equal(2, editor.Session.Root.Content.Length);
            var updated = editor.Session.Root.Content[0];
            Assert.Equal(new[] { second.Id, third.Id }, updated.Content.Skip(1).Take(2).Select(node => node.Id));
            Assert.Equal(NoteJson.Serialize(third), NoteJson.Serialize(updated.Content[2]));
            Assert.Equal("二级的尾部", RichText.Plain(updated.Content[1].Content[1]));
            editor.Session.Undo(); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            editor.Session.Redo(); Frame(window);
            Assert.Equal(third.Id, editor.Session.Root.Content[0].Content[2].Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GhostKeepsTheActualWidthAndExpandedSubtreeHeight(bool collapsed)
    {
        var parent = NoteNode.Toggle("本周计划", NoteNode.Paragraph("阅读资料"),
            NoteNode.Toggle("整理思路", NoteNode.Paragraph("写下重要结论")), NoteNode.Paragraph("分享进展")).WithAttr("collapsed", collapsed);
        var (window, editor) = Open(parent, NoteNode.Paragraph("结尾"));
        try
        {
            var view = editor.Surface.TextArea.TextView;
            var lastIndex = editor.Session.Projection.Rows.Last(row => NoteTree.Find(parent, row.Node.Id) != null).Index;
            var actualHeight = view.VisualLines.Take(lastIndex + 1).Sum(line => line.Height);
            var start = Start(window, editor, parent.Id);
            window.MouseMove(start + new Vector(80, 16), RawInputModifiers.LeftMouseButton); Frame(window);
            var ghost = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "DragGhost");
            Assert.True(ghost.IsVisible);
            var preview = ghost.GetVisualDescendants().OfType<BlockEditor>().Single();
            Assert.True(preview.Surface.IsReadOnly);
            Assert.Equal(string.Join('\n', editor.Session.Projection.Rows.Take(lastIndex + 1).Select(row => row.Text)), preview.Surface.Text);
            Assert.InRange(ghost.Bounds.Height, actualHeight, actualHeight + 20);
            Assert.True(ghost.Bounds.Width > view.Bounds.Width - 90, $"Ghost width {ghost.Bounds.Width} should match block width {view.Bounds.Width}.");
            if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(directory, collapsed ? "drag-collapsed.png" : "drag-expanded.png"));
                using var isolated = new Avalonia.Media.Imaging.RenderTargetBitmap(new((int)Math.Ceiling(ghost.Bounds.Width), (int)Math.Ceiling(ghost.Bounds.Height)));
                isolated.Render(ghost); isolated.Save(Path.Combine(directory, collapsed ? "drag-collapsed-only.png" : "drag-expanded-only.png"));
            }
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, ""); Frame(window);
            Assert.False(ghost.IsVisible);
        }
        finally { window.Close(); }
    }

    private static void SaveImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(directory, name));
    }

    [AvaloniaFact]
    public void PreviewKeepsWrappedRichTextAndDoesNotRevealCollapsedGrandchildren()
    {
        var longText = string.Concat(Enumerable.Repeat("这是一段需要保持换行位置的中文标题。", 9));
        var hidden = NoteNode.Toggle("保持收起", NoteNode.Paragraph("不可泄漏到预览的隐藏正文")).WithAttr("collapsed", true);
        var parent = NoteNode.Toggle(longText, hidden, NoteNode.Paragraph("正文末尾"));
        parent = parent with { Content = parent.Content.SetItem(0, parent.Content[0] with { Content = [new("text") { Text = longText, Marks = [new("bold")] }] }) };
        var (window, editor) = Open(NoteNode.Toggle("外层", parent), NoteNode.Paragraph("尾部"));
        try
        {
            var view = editor.Surface.TextArea.TextView;
            var row = editor.Session.Projection.Find(parent.Content[0].Id)!;
            var sourceLine = view.VisualLines.Single(line => line.FirstDocumentLine.Offset == row.Start);
            var lineLengths = sourceLine.TextLines.Select(line => line.Length).ToArray();
            var before = NoteJson.Serialize(editor.Session.Root);
            var start = Start(window, editor, parent.Id);
            window.MouseMove(start + new Vector(28, 50), RawInputModifiers.LeftMouseButton); Frame(window);
            var ghost = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "DragGhost");
            var preview = ghost.GetVisualDescendants().OfType<BlockEditor>().Single();
            Assert.DoesNotContain("隐藏正文", preview.Surface.Text);
            Assert.Equal(lineLengths, preview.Surface.TextArea.TextView.VisualLines[0].TextLines.Select(line => line.Length));
            Assert.Contains(preview.Surface.TextArea.TextView.VisualLines[0].Elements, element => element.TextRunProperties.Typeface.Weight == Avalonia.Media.FontWeight.Bold);
            SaveImage(window, "drag-wrapped-rich-text.png");
            window.MouseMove(new(-12, 120), RawInputModifiers.LeftMouseButton); window.MouseUp(new(-12, 120), MouseButton.Left); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.False(ghost.IsVisible);
            Assert.Empty(ghost.GetVisualDescendants().OfType<BlockEditor>());
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedTogglePreviewKeepsTheRegionsLineHeightAndWrapWidth(bool tableCell)
    {
        var title = string.Concat(Enumerable.Repeat("区域内的折叠标题要保持换行位置。", 5));
        var toggle = NoteNode.Toggle(title, NoteNode.Paragraph("保留子内容"));
        var layout = tableCell ? LayoutBlocks.Table(2, 2) : LayoutBlocks.Columns();
        var container = tableCell ? layout.Content[0].Content[0] : layout.Content[0];
        layout = NoteTree.Update(layout, container.Id, node => node with { Content = [toggle, NoteNode.Paragraph("区域尾部")] });
        var (window, editor) = Open(layout, NoteNode.Paragraph("文档尾部"));
        try
        {
            editor.NavigateTo(toggle.Content[0].Id); Frame(window);
            var source = editor.ActiveEditor; Assert.NotSame(editor, source);
            var view = source.Surface.TextArea.TextView;
            var first = view.VisualLines[0];
            var lengths = first.TextLines.Select(line => line.Length).ToArray();
            var height = first.Height;
            var start = Start(window, source, toggle.Id);
            window.MouseMove(start + new Vector(32, 16), RawInputModifiers.LeftMouseButton); Frame(window);
            var ghost = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "DragGhost" && control.IsVisible);
            var preview = ghost.GetVisualDescendants().OfType<BlockEditor>().Single();
            var previewFirst = preview.Surface.TextArea.TextView.VisualLines[0];
            Assert.Equal(height, previewFirst.Height, 3);
            Assert.Equal(lengths, previewFirst.TextLines.Select(line => line.Length));
            Assert.Equal(title + "\n保留子内容", preview.Surface.Text);
            SaveImage(window, tableCell ? "drag-table-region.png" : "drag-column-region.png");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, ""); Frame(window);
            Assert.False(ghost.IsVisible); Assert.False(editor.Session.CanUndo);
            start = Start(window, source, toggle.Id);
            window.MouseMove(start + new Vector(32, 16), RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.True(ghost.IsVisible);
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert.False(ghost.IsVisible); Assert.Null(ghost.Child);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ALongExpandedPreviewKeepsVirtualizationAndCanBeCancelled()
    {
        var parent = NoteNode.Toggle("一万个子块", Enumerable.Range(0, 10_000).Select(index => NoteNode.Paragraph("第 " + index + " 项")).ToArray());
        var (window, editor) = Open(parent, NoteNode.Paragraph("尾部"));
        try
        {
            var start = Start(window, editor, parent.Id);
            window.MouseMove(start + new Vector(40, 16), RawInputModifiers.LeftMouseButton); Frame(window);
            var ghost = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "DragGhost");
            var preview = ghost.GetVisualDescendants().OfType<BlockEditor>().Single();
            Assert.True(ghost.Height > editor.Bounds.Height);
            Assert.InRange(preview.Surface.TextArea.TextView.VisualLines.Count, 1, 60);
            Assert.True(preview.GetVisualDescendants().OfType<Button>().Count() < 60);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, ""); Frame(window);
            Assert.False(editor.Session.CanUndo); Assert.False(ghost.IsVisible);
        }
        finally { window.Close(); }
    }
}
