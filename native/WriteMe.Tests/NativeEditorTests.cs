using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(WriteMe.Tests.TestApplication))]

namespace WriteMe.Tests;

public static class TestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class NativeEditorTests
{
    private static (Window Window, BlockEditor Editor) Open(params NoteNode[] blocks)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. blocks] }));
        var window = new Window { Width = 780, Height = 540, Content = editor };
        window.Show();
        editor.FocusText();
        Frame(window);
        return (window, editor);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
    }

    private static void PressKey(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, "");
        window.KeyRelease(key, modifiers, PhysicalKey.None, "");
        Frame(window);
    }

    [AvaloniaFact]
    public void NativeChineseTextInputAndToggleEnterUseTheCSharpDocument()
    {
        var (window, editor) = Open(NoteNode.Toggle(""));
        try
        {
            window.KeyTextInput("中文输入🙂"); Frame(window);
            Assert.Equal("中文输入🙂", editor.Session.Projection.Text);
            PressKey(window, Key.Enter);
            window.KeyTextInput("子折叠项"); Frame(window);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Content[1].Type);
            Assert.Equal("子折叠项", RichText.Plain(editor.Session.Root.Content[0].Content[1].Content[0]));
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal("", RichText.Plain(editor.Session.Root.Content[0].Content[1].Content[0]));
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Single(editor.Session.Root.Content[0].Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CompositionPreeditIsNotSavedAndEnterDoesNotCreateABlock()
    {
        var (window, editor) = Open(NoteNode.Toggle("标题"));
        try
        {
            editor.Surface.CaretOffset = 2;
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.InputClient.SetPreeditText("zhongwen");
            PressKey(window, Key.Enter);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(editor.Session.CanUndo);
            editor.InputClient.SetPreeditText(null);
            window.KeyTextInput("中文"); Frame(window);
            Assert.Equal("标题中文", editor.Session.Projection.Text);
            Assert.Single(editor.Session.Root.Content[0].Content);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void RichFormattingIsActuallyRenderedByNativeTextRuns()
    {
        var p = NoteNode.Paragraph() with { Content = [new("text") { Text = "粗体", Marks = [new("bold")] }, new("text") { Text = " 高亮", Marks = [NoteMark.With("highlight", "color", "#FFF0A8")] }] };
        var (window, editor) = Open(p, NoteNode.Paragraph("二级标题").WithAttr("level", 2) with { Type = "heading" });
        try
        {
            var lines = editor.Surface.TextArea.TextView.VisualLines;
            Assert.Contains(lines[0].Elements, e => e.TextRunProperties.Typeface.Weight == FontWeight.Bold);
            Assert.Contains(lines[0].Elements, e => e.TextRunProperties.BackgroundBrush is ISolidColorBrush color && color.Color == Color.Parse("#FFF0A8"));
            Assert.Contains(lines[1].Elements, e => Math.Abs(e.TextRunProperties.FontRenderingEmSize - 23) < .01);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CrossBlockSelectionCanBeEditedAndUndoneThroughTheNativeKeyboard()
    {
        var (window, editor) = Open(NoteNode.Paragraph("甲乙丙"), NoteNode.Paragraph("丁戊己"));
        try
        {
            editor.Surface.Select(1, 5);
            window.KeyTextInput("替换"); Frame(window);
            Assert.Equal("甲替换己", editor.Surface.Text);
            Assert.Single(editor.Session.Root.Content);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal("甲乙丙\n丁戊己", editor.Surface.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HandlesAreHiddenUntilThePointerHoversOneBlockAndHideOnExit()
    {
        var (window, editor) = Open(NoteNode.Toggle("父级", NoteNode.Toggle("子级")), NoteNode.Paragraph("另一块"));
        try
        {
            window.MouseMove(new(760, 530)); Frame(window);
            var grips = editor.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("blockGrip")).ToArray();
            Assert.NotEmpty(grips);
            Assert.All(grips, b => Assert.Equal(0, b.Opacity));
            var row = editor.Surface.TextArea.TextView.VisualLines[1];
            var point = editor.Surface.TextArea.TextView.TranslatePoint(new Point(120, row.VisualTop + 12), window)!.Value;
            window.MouseMove(point); Frame(window);
            Assert.Single(grips, b => b.Opacity > 0);
            window.MouseMove(new(779, 539)); Frame(window);
            Assert.All(grips, b => Assert.Equal(0, b.Opacity));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointerDragOutPreservesToggleTypeChildrenAndCollapsedState(bool collapsed)
    {
        var child = NoteNode.Toggle("移动的子折叠块", NoteNode.Toggle("孙级", NoteNode.Paragraph("不能丢失"))).WithAttr("collapsed", collapsed);
        var parent = NoteNode.Toggle("父级", child);
        var (window, editor) = Open(parent, NoteNode.Paragraph("尾部"));
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var view = editor.Surface.TextArea.TextView;
            var line = view.VisualLines[1];
            var grip = view.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("blockGrip") && Equals(button.Tag, child.Id));
            var gripPoint = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            var targetPoint = view.TranslatePoint(new Point(10, line.VisualTop + 23), window)!.Value;
            window.MouseMove(gripPoint); Frame(window);
            Assert.Equal(1, grip.Opacity);
            window.MouseDown(gripPoint, MouseButton.Left); Frame(window);
            window.MouseMove(targetPoint, RawInputModifiers.LeftMouseButton); Frame(window);
            window.MouseUp(targetPoint, MouseButton.Left); Frame(window);
            Assert.Equal(3, editor.Session.Root.Content.Length);
            var moved = editor.Session.Root.Content[1];
            Assert.Equal(child.Id, moved.Id);
            Assert.Equal("toggleBlock", moved.Type);
            Assert.Equal(collapsed, moved.Bool("collapsed"));
            Assert.Equal(NoteJson.Serialize(child), NoteJson.Serialize(moved));
            editor.Session.Toggle(moved.Id); Frame(window);
            Assert.Equal(!collapsed, editor.Session.Root.Content[1].Bool("collapsed"));
            editor.Session.Undo(); editor.Session.Undo();
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DraggingSelectedTextCannotBypassBlockTransactions()
    {
        var (window, editor) = Open(NoteNode.Toggle("折叠标题", NoteNode.Paragraph("子内容")), NoteNode.Paragraph("结尾"));
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.Surface.Select(0, 4);
            Frame(window);
            var view = editor.Surface.TextArea.TextView;
            var start = view.TranslatePoint(new Point(70, 12), window)!.Value;
            var end = view.TranslatePoint(new Point(90, view.VisualLines[^1].VisualTop + 12), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ThousandsOfBlocksOnlyCreateNativeControlsForVisibleLines()
    {
        var (window, editor) = Open(Enumerable.Range(0, 10000).Select(i => NoteNode.Toggle($"第 {i} 块")).ToArray());
        try
        {
            Assert.Equal(10000, editor.Session.Projection.Rows.Length);
            Assert.InRange(editor.Surface.TextArea.TextView.VisualLines.Count, 2, 50);
            Assert.InRange(editor.GetVisualDescendants().OfType<Button>().Count(), 2, 100);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DeepLongTitlesWrapWithIndentationAndKeepEveryCharacter()
    {
        var defaultFormatter = TextFormatter.Current;
        var longText = string.Concat(Enumerable.Repeat("第五级长标题需要自动换行并继续编辑。", 12));
        var leaf = NoteNode.Toggle(longText);
        var nested = Enumerable.Range(0, 4).Aggregate(leaf, (child, i) => NoteNode.Toggle($"层级{i}", child));
        var (window, editor) = Open(nested);
        try
        {
            Assert.Same(defaultFormatter, TextFormatter.Current);
            foreach (var width in new[] { 780, 360 })
            {
                window.Width = width; Frame(window); Frame(window);
                var row = editor.Session.Projection.Rows.Single(r => r.Block.Id == leaf.Id);
                var view = editor.Surface.TextArea.TextView;
                var line = view.VisualLines.Single(l => l.FirstDocumentLine.Offset == row.Start);
                Assert.True(line.TextLines.Count > 1);
                Assert.Equal(longText, row.Text);
                Assert.All(line.TextLines, textLine => Assert.True(textLine.Width <= view.Bounds.Width + 1));
                AssertContinuationIndent(line, 188);

                // Different rows keep the two rapid test clicks from becoming a double click.
                var continuation = line.TextLines[width == 780 ? 1 : 2];
                var relativeOffset = line.GetRelativeOffset(continuation.FirstTextSourceIndex);
                var local = new Point(190, line.GetTextLineVisualYPosition(continuation, VisualYPosition.TextTop) + 5) - view.ScrollOffset;
                var point = view.TranslatePoint(local, window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
                Assert.Equal(row.Start + relativeOffset, editor.Surface.CaretOffset);
                Assert.Equal(0, editor.Surface.SelectionLength);
                Assert.Equal(188, editor.InputClient.CursorRectangle.X, 2);
                window.KeyTextInput("测"); Frame(window);
                Assert.Equal(longText.Insert(relativeOffset, "测"), editor.Session.Projection.Rows.Single(r => r.Block.Id == leaf.Id).Text);
                PressKey(window, Key.Z, RawInputModifiers.Control);
                Assert.Equal(longText, editor.Session.Projection.Rows.Single(r => r.Block.Id == leaf.Id).Text);
                editor.Surface.Select(row.Start + relativeOffset, 8); Frame(window);
                Assert.Equal(longText.Substring(relativeOffset, 8), editor.Surface.SelectedText);
                editor.Surface.Select(row.Start, 0); Frame(window);
            }
            window.Width = 780; Frame(window);
            if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(directory, "toggle-nested-long.png"));
            }
        }
        finally { window.Close(); }
    }

    private static void AssertContinuationIndent(VisualLine line, double inset)
    {
        Assert.All(line.TextLines.Skip(1), textLine =>
        {
            Assert.Equal(inset, textLine.GetDistanceFromCharacterHit(new(textLine.FirstTextSourceIndex)), 2);
            foreach (var bounds in textLine.GetTextBounds(textLine.FirstTextSourceIndex, 1))
            {
                Assert.True(bounds.Rectangle.Left >= inset);
                Assert.All(bounds.TextRunBounds, run => Assert.True(run.Rectangle.Left >= inset));
            }
        });
    }

    [AvaloniaTheory]
    [InlineData("paragraph", 48)]
    [InlineData("heading", 48)]
    [InlineData("toggleChild", 76)]
    public void SoftBreaksAndWrappedRichTextKeepTheBlockIndentWithoutChangingSavedText(string kind, double inset)
    {
        var paragraph = NoteNode.Paragraph() with
        {
            Content = [new("text") { Text = "上行🙂" }, new("hardBreak"), new("hardBreak"), new("text")
            {
                Text = string.Concat(Enumerable.Repeat("换行后的中文🙂与粗体高亮保持一致。", 8)),
                Marks = [new("bold"), NoteMark.With("highlight", "color", "#FFF0A8")]
            }]
        };
        if (kind == "heading") paragraph = paragraph with { Type = "heading" };
        var block = kind == "toggleChild" ? NoteNode.Toggle("父级", paragraph) : paragraph;
        var (window, editor) = Open(block);
        try
        {
            var saved = NoteJson.Serialize(editor.Session.Root);
            window.Width = 400; Frame(window);
            var view = editor.Surface.TextArea.TextView;
            var row = editor.Session.Projection.Rows.Single(r => r.Node.Id == paragraph.Id);
            var line = view.VisualLines.Single(l => l.FirstDocumentLine.Offset == row.Start);
            Assert.True(line.TextLines.Count > 3);
            AssertContinuationIndent(line, inset);
            var textLine = line.TextLines.First(t => t.FirstTextSourceIndex > 6 && t.Length > 4);
            var relativeOffset = line.GetRelativeOffset(textLine.FirstTextSourceIndex);
            editor.Surface.Select(row.Start + relativeOffset, 4); Frame(window);
            Assert.Equal(row.Text.Substring(relativeOffset, 4), editor.Surface.SelectedText);
            Assert.Equal(saved, NoteJson.Serialize(editor.Session.Root));
            editor.Surface.Select(row.Start + relativeOffset, 0); Frame(window);
            Assert.Equal(inset, editor.InputClient.CursorRectangle.X, 2);
            editor.InputClient.SetPreeditText("zhong"); Frame(window);
            Assert.Equal(saved, NoteJson.Serialize(editor.Session.Root));
            editor.InputClient.SetPreeditText(null);
            window.KeyTextInput("中"); Frame(window);
            Assert.Equal(row.Text.Insert(relativeOffset, "中"), editor.Session.Projection.Rows.Single(r => r.Node.Id == paragraph.Id).Text);
            PressKey(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(saved, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SlashSearchConvertsTheWholeQueryAndPreservesTrailingText()
    {
        var (window, editor) = Open(NoteNode.Paragraph("后面的正文"));
        try
        {
            window.KeyTextInput("/zhedie"); Frame(window);
            PressKey(window, Key.Enter);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Type);
            Assert.Equal("后面的正文", RichText.Plain(editor.Session.Root.Content[0].Content[0]));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImmediateDocumentSwitchAndWindowCloseFlushPendingEdits()
    {
        using var temporary = new TestDirectory();
        using var probe = new NoteStore(temporary.Path);
        var first = probe.Create("第一篇", new("doc") { Content = [NoteNode.Paragraph("旧正文甲")] });
        var second = probe.Create("第二篇", new("doc") { Content = [NoteNode.Paragraph("旧正文乙")] });
        var window = new MainWindow(temporary.Path, false);
        window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
        var list = window.FindControl<ListBox>("DocumentList")!;
        var activeId = ((DocumentItem)list.SelectedItem!).Id;
        var nextId = activeId == first.Id ? second.Id : first.Id;
        editor.FocusText(); editor.Surface.SelectAll();
        window.KeyTextInput("切换前修改"); Frame(window);
        list.SelectedItem = list.Items.Cast<DocumentItem>().Single(item => item.Id == nextId);
        for (var i = 0; i < 100 && editor.Surface.Text == "切换前修改"; i++) { await Task.Delay(15); Frame(window); }
        Assert.Equal("切换前修改", new DocumentProjection(NoteJson.Parse(probe.Get(activeId).Content)).Text);
        Assert.NotEqual("切换前修改", editor.Surface.Text);
        editor.FocusText(); editor.Surface.SelectAll();
        window.KeyTextInput("关闭前修改"); Frame(window);
        window.Close();
        for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
        Assert.False(window.IsVisible);
        Assert.Equal("关闭前修改", new DocumentProjection(NoteJson.Parse(probe.Get(nextId).Content)).Text);
    }
}
