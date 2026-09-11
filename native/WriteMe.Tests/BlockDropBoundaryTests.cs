using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class BlockDropBoundaryTests
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
        Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs(); using var settled = window.CaptureRenderedFrame();
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

    private static double RowTop(BlockEditor editor, Guid node)
    {
        var view = editor.Surface.TextArea.TextView;
        var row = editor.Session.Projection.Find(node)!;
        return view.VisualLines.Single(line => line.FirstDocumentLine.Offset == row.Start).VisualTop - view.ScrollOffset.Y;
    }

    private static Point AtY(Window window, BlockEditor editor, Point start, double y)
        => new(start.X, editor.Surface.TextArea.TextView.TranslatePoint(new(0, y), window)!.Value.Y);

    // Read the actual painted horizontal line, independently of the resolver's target record.
    private static Point? DropLine(BlockEditor editor)
    {
        var view = editor.Surface.TextArea.TextView;
        var size = new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height));
        using var image = new RenderTargetBitmap(size); image.Render(view);
        using var pixels = new WriteableBitmap(size, new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = pixels.Lock(); image.CopyPixels(buffer, AlphaFormat.Premul);
        var bytes = new byte[buffer.RowBytes * size.Height]; Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        bool Blue(int x, int y)
        {
            var index = y * buffer.RowBytes + x * 4;
            return bytes[index + 3] > 150 && bytes[index] > bytes[index + 2] + 35 && bytes[index] > bytes[index + 1] + 20;
        }
        var probeLeft = size.Width / 2; var probeRight = size.Width - 16;
        var lineRows = Enumerable.Range(0, size.Height).Where(y =>
            Enumerable.Range(probeLeft, probeRight - probeLeft).Count(x => Blue(x, y)) > (probeRight - probeLeft) * .9).ToArray();
        if (lineRows.Length == 0) return null;
        var bottom = lineRows[^1];
        var left = Enumerable.Range(0, size.Width).First(x => Blue(x, bottom));
        return new(left, bottom);
    }

    private static void AssertDropLine(BlockEditor editor, double expectedY, double expectedX)
    {
        var line = DropLine(editor);
        Assert.NotNull(line);
        Assert.InRange(line.Value.Y, expectedY - 2, expectedY + 2);
        Assert.InRange(line.Value.X, expectedX - 8, expectedX + 3);
    }

    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(directory, name));
    }

    [AvaloniaTheory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void ParentTitleAndFirstChildShareOneDropBoundary(int side, bool wrapped)
    {
        var first = NoteNode.Toggle("原来的第一项", NoteNode.Paragraph("第一项的子内容"));
        var moving = NoteNode.Toggle("移到最前面的折叠项", NoteNode.Paragraph("随块保留的正文"));
        var title = wrapped ? string.Concat(Enumerable.Repeat("父级长标题也必须按整块换行后的真实边界来插入。", 6)) : "父级";
        var parent = NoteNode.Toggle(title, first, NoteNode.Paragraph("中间内容"), moving);
        var (window, editor) = Open(parent, NoteNode.Paragraph("文档尾部"));
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, first.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var target = AtY(window, editor, start, boundary + side);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, boundary, 76);
            Save(window, "drop-first-child-" + (wrapped ? "wrapped-" : "") + side + ".png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            Assert.Equal(2, editor.Session.Root.Content.Length);
            Assert.Equal(moving.Id, editor.Session.Root.Content[0].Content[1].Id);
            Assert.Equal(NoteJson.Serialize(moving), NoteJson.Serialize(editor.Session.Root.Content[0].Content[1]));
            var after = NoteJson.Serialize(editor.Session.Root);
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
            editor.Session.Redo(); Frame(window); Assert.Equal(after, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void LastChildAndNextToggleShareDepthAtTheirBoundary(int side)
    {
        var original = NoteNode.Paragraph("目标内的已有子项");
        var destination = NoteNode.Toggle("目标折叠", original);
        var moving = NoteNode.Toggle("要移动的三级项", NoteNode.Paragraph("保留第四级内容"));
        var following = NoteNode.Toggle("后一个折叠", moving, NoteNode.Paragraph("留在原位置"));
        var parent = NoteNode.Toggle("顶层", destination, following);
        var (window, editor) = Open(parent, NoteNode.Paragraph("文档尾部"));
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, following.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var target = AtY(window, editor, start, boundary + side);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, boundary, 104);
            Save(window, "drop-nested-tail-" + side + ".png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            var updated = NoteTree.Find(editor.Session.Root, destination.Id)!;
            Assert.Equal(new[] { original.Id, moving.Id }, updated.Content.Skip(1).Select(node => node.Id));
            Assert.Equal(destination.Id, NoteTree.Parent(editor.Session.Root, moving.Id)!.Id);
            Assert.Equal(NoteJson.Serialize(moving), NoteJson.Serialize(updated.Content[2]));
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DraggingRightAtTheOriginalGapNestsUnderThePreviousToggle(bool collapsed)
    {
        var existing = NoteNode.Paragraph("已存在的内容");
        var previous = NoteNode.Toggle("前一项", existing).WithAttr("collapsed", collapsed);
        var moving = NoteNode.Toggle("移动项", NoteNode.Paragraph("跟随的内容"));
        var parent = NoteNode.Toggle("父级", previous, moving, NoteNode.Paragraph("末尾"));
        var (window, editor) = Open(parent);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, moving.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var target = start + new Vector(28, 0);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, boundary, 104);
            Save(window, collapsed ? "drop-indent-closed.png" : "drop-indent-open.png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            var updated = NoteTree.Find(editor.Session.Root, previous.Id)!;
            Assert.False(updated.Bool("collapsed"));
            Assert.Equal(collapsed ? new[] { moving.Id, existing.Id } : new[] { existing.Id, moving.Id }, updated.Content.Skip(1).Select(node => node.Id));
            Assert.Equal(NoteJson.Serialize(moving), NoteJson.Serialize(NoteTree.Find(editor.Session.Root, moving.Id)!));
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ExpandedSiblingFooterOffersTheSameSiblingSlotFromBothSides(int side)
    {
        var moving = NoteNode.Toggle("从前面移下来的同级项", NoteNode.Paragraph("保留子项"));
        var previous = NoteNode.Toggle("前项", NoteNode.Toggle("内层", NoteNode.Paragraph("内层末尾")));
        var next = NoteNode.Toggle("后项", NoteNode.Paragraph("后项内容"));
        var parent = NoteNode.Toggle("父级", moving, previous, next);
        var (window, editor) = Open(parent);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, next.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var target = AtY(window, editor, start, boundary + side);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, boundary, 76);
            window.MouseUp(target, MouseButton.Left); Frame(window);
            Assert.Equal(new[] { previous.Id, moving.Id, next.Id }, editor.Session.Root.Content[0].Content.Skip(1).Select(node => node.Id));
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(1)]
    public void OutdentAtTheFirstChildBoundaryIsStableOnBothSides(int side)
    {
        var moving = NoteNode.Toggle("三级项", NoteNode.Paragraph("保留内容"));
        var second = NoteNode.Toggle("二级项", moving, NoteNode.Paragraph("二级尾部"));
        var tail = NoteNode.Paragraph("一级尾部");
        var parent = NoteNode.Toggle("一级项", second, tail);
        var (window, editor) = Open(parent);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, moving.Content[0].Id);
            var indicator = RowTop(editor, tail.Id);
            var start = Start(window, editor, moving.Id);
            var target = AtY(window, editor, start - new Vector(28, 0), boundary + side);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, indicator, 76);
            window.MouseUp(target, MouseButton.Left); Frame(window);
            Assert.Equal(new[] { second.Id, moving.Id, tail.Id }, editor.Session.Root.Content[0].Content.Skip(1).Select(node => node.Id));
            Assert.Equal(NoteJson.Serialize(moving), NoteJson.Serialize(NoteTree.Find(editor.Session.Root, moving.Id)!));
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AnUnchangedGapOrOwnDescendantDoesNotReparentOrCreateHistory()
    {
        var previous = NoteNode.Toggle("前项", NoteNode.Paragraph("前项内容"));
        var descendant = NoteNode.Toggle("自己的子折叠", NoteNode.Paragraph("自己的正文"));
        var moving = NoteNode.Toggle("移动项", descendant);
        var parent = NoteNode.Toggle("父级", previous, moving, NoteNode.Paragraph("尾部"));
        var (window, editor) = Open(parent);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var originalBoundary = RowTop(editor, moving.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var unchanged = AtY(window, editor, start, originalBoundary - 1);
            window.MouseMove(unchanged, RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.Null(DropLine(editor));
            window.MouseUp(unchanged, MouseButton.Left); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.False(editor.Session.CanUndo);
            start = Start(window, editor, moving.Id);
            var cycle = AtY(window, editor, start + new Vector(56, 0), RowTop(editor, descendant.Content[0].Id) + 14);
            window.MouseMove(cycle, RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.Null(DropLine(editor));
            window.MouseUp(cycle, MouseButton.Left); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedDropUsesTheMovedBlocksDestinationIndent(bool tableCell)
    {
        var moving = NoteNode.Toggle("移出子项", NoteNode.Paragraph("保留的内容"));
        var parent = NoteNode.Toggle("区域内父项", moving);
        var tail = NoteNode.Paragraph("区域尾部");
        var layout = tableCell ? LayoutBlocks.Table(2, 2) : LayoutBlocks.Columns();
        var container = tableCell ? layout.Content[0].Content[0] : layout.Content[0];
        layout = NoteTree.Update(layout, container.Id, node => node with { Content = [parent, tail] });
        var (window, editor) = Open(layout);
        try
        {
            editor.NavigateTo(moving.Content[0].Id); Frame(window);
            var region = editor.ActiveEditor; Assert.NotSame(editor, region);
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(region, tail.Id);
            var start = Start(window, region, moving.Id);
            var target = AtY(window, region, start - new Vector(28, 0), boundary + 1);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(region, boundary, 24);
            Save(window, tableCell ? "drop-cell-outdent.png" : "drop-column-outdent.png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            var updated = NoteTree.Find(editor.Session.Root, container.Id)!;
            Assert.Equal(new[] { parent.Id, moving.Id, tail.Id }, updated.Content.Select(node => node.Id));
            Assert.Equal(NoteJson.Serialize(moving), NoteJson.Serialize(updated.Content[1]));
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ScrolledChildBoundaryUsesTheVisibleDocumentCoordinates()
    {
        var first = NoteNode.Toggle("第一项", NoteNode.Paragraph("第一项内容"));
        var moving = NoteNode.Toggle("移动项", NoteNode.Paragraph("移动项内容"));
        var parent = NoteNode.Toggle("滚动后的父级", first, NoteNode.Paragraph("中间项"), moving);
        var (window, editor) = Open([.. Enumerable.Range(0, 30).Select(index => NoteNode.Paragraph("前文 " + index)), parent]);
        try
        {
            editor.NavigateTo(moving.Content[0].Id); Frame(window);
            Assert.True(editor.Surface.TextArea.TextView.ScrollOffset.Y > 0);
            var before = NoteJson.Serialize(editor.Session.Root);
            var boundary = RowTop(editor, first.Content[0].Id);
            var start = Start(window, editor, moving.Id);
            var target = AtY(window, editor, start, boundary - 1);
            window.MouseMove(target, RawInputModifiers.LeftMouseButton); Frame(window);
            AssertDropLine(editor, boundary, 76);
            Save(window, "drop-scrolled-first-child.png");
            window.MouseUp(target, MouseButton.Left); Frame(window);
            Assert.Equal(moving.Id, NoteTree.Find(editor.Session.Root, parent.Id)!.Content[1].Id);
            editor.Session.Undo(); Frame(window); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }
}
