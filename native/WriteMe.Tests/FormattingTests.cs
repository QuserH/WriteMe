using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class FormattingTests
{
    private static NoteNode Run(string text, params NoteMark[] marks) => new("text") { Text = text, Marks = [.. marks] };

    [Fact]
    public void CoverageReflectsSelectedTextAcrossBlocksWithoutIncludingHiddenChildren()
    {
        var bold = new NoteMark("bold");
        var toggle = NoteNode.Toggle("标题", NoteNode.Paragraph("隐藏正文")) with { Content = [NoteNode.Paragraph() with { Content = [Run("标题", bold)] }, NoteNode.Paragraph("隐藏正文")] };
        toggle = toggle.WithAttr("collapsed", true);
        var session = new DocumentSession(new("doc") { Content = [toggle, NoteNode.Paragraph("正文")] });
        Assert.Equal(MarkCoverage.All, new SelectionFormats(session.Projection, 0, 2).Coverage("bold"));
        var mixed = new SelectionFormats(session.Projection, 0, session.Projection.Text.Length);
        Assert.True(mixed.CanFormat);
        Assert.Equal(MarkCoverage.Mixed, mixed.Coverage("bold"));
        session.Format(0, session.Projection.Text.Length, bold);
        Assert.Equal(MarkCoverage.All, new SelectionFormats(session.Projection, 0, session.Projection.Text.Length).Coverage("bold"));
        Assert.Empty(session.Root.Content[0].Content[1].Content[0].Marks);
        session.Undo();
        Assert.Equal(MarkCoverage.Mixed, new SelectionFormats(session.Projection, 0, session.Projection.Text.Length).Coverage("bold"));
    }

    [Fact]
    public void LinkRangeSpansAdjacentFormattingRunsButStopsAtOtherLinksAndParagraphs()
    {
        var link = NoteMark.With("link", "href", "https://example.com");
        var other = NoteMark.With("link", "href", "https://example.org");
        var paragraph = NoteNode.Paragraph() with { Content = [Run("前 "), Run("链接", link), Run("粗体", link, new("bold")), Run("斜体", link, new("italic")), Run("别处", other)] };
        var projection = new DocumentProjection(new("doc") { Content = [paragraph, NoteNode.Paragraph() with { Content = [Run("下一段", link)] }] });
        foreach (var caret in new[] { 2, 3, 4, 5, 6, 7 })
        {
            var range = SelectionFormats.LinkAt(projection, caret);
            Assert.NotNull(range);
            Assert.Equal(2, range.Start);
            Assert.Equal(6, range.Length);
        }
        Assert.Equal("https://example.org", SelectionFormats.LinkAt(projection, 8)!.Mark.String("href"));
        Assert.Null(SelectionFormats.LinkAt(projection, 0));
    }

    [Fact]
    public void SettingTheSameLinkOrHighlightDoesNotToggleItOffOrAddUndoHistory()
    {
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph("需要格式")] });
        var link = NoteMark.With("link", "href", "https://example.com");
        session.Format(0, 4, link, toggle: false);
        var revision = session.Revision;
        session.Format(0, 4, link, toggle: false);
        Assert.Equal(revision, session.Revision);
        Assert.Equal(MarkCoverage.All, new SelectionFormats(session.Projection, 0, 4).Coverage("link"));
        session.Undo();
        Assert.Empty(session.Root.Content[0].Content[0].Marks);
        var highlight = NoteMark.With("highlight", "color", "#D5E9F7");
        session.Format(0, 4, highlight, toggle: false);
        revision = session.Revision;
        session.Format(0, 4, highlight, toggle: false);
        Assert.Equal(revision, session.Revision);
        session.Format(0, 4, new("highlight"), forceRemove: true);
        Assert.Empty(session.Root.Content[0].Content[0].Marks);
    }

    [Fact]
    public void CodeUnknownBlocksAndOnlyNewlinesDoNotOfferInlineFormatting()
    {
        var projection = new DocumentProjection(new("doc") { Content = [NoteNode.Paragraph("正文"), NoteNode.Paragraph("代码") with { Type = "codeBlock" }, new("image")] });
        Assert.False(new SelectionFormats(projection, 2, 1).CanFormat);
        Assert.False(new SelectionFormats(projection, 3, 2).CanFormat);
        Assert.False(new SelectionFormats(projection, 0, projection.Text.Length).CanFormat);
    }

    [Fact]
    public void SoftBreaksDoNotDisagreeWithTheToolbarAboutWhetherBoldCanBeRemoved()
    {
        var bold = new NoteMark("bold");
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph() with { Content = [Run("上行", bold), new("hardBreak"), Run("下行", bold)] }] });
        Assert.Equal(MarkCoverage.All, new SelectionFormats(session.Projection, 0, 5).Coverage("bold"));
        session.Format(0, 5, bold);
        Assert.Equal(MarkCoverage.None, new SelectionFormats(session.Projection, 0, 5).Coverage("bold"));
        Assert.Equal("上行\u2028下行", session.Projection.Text);
    }

    private static (Window Window, BlockEditor Editor) Open(params NoteNode[] blocks)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. blocks] }));
        var window = new Window { Width = 740, Height = 480, Content = editor };
        window.Show();
        editor.FocusText();
        Frame(window);
        return (window, editor);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        using var settledFrame = window.CaptureRenderedFrame();
    }

    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, "");
        window.KeyRelease(key, modifiers, PhysicalKey.None, "");
        Frame(window);
    }

    private static void Click(Window window, Control control)
    {
        Assert.True(control.IsVisible);
        Assert.True(control.Bounds.Width > 0);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Frame(window);
    }

    private static Button Action(BlockEditor editor, string id) => editor.Formatting.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == id);
    private static Button Named(BlockEditor editor, string name) => editor.Formatting.GetVisualDescendants().OfType<Button>().Single(button => AutomationProperties.GetName(button) == name || Equals(button.Content, name));
    private static TextBox LinkInput(BlockEditor editor) => editor.Formatting.GetVisualDescendants().OfType<TextBox>().Single();

    [AvaloniaFact]
    public void ToolbarAppearsForSelectionAndHidesForCaretOrComposition()
    {
        var (window, editor) = Open(NoteNode.Paragraph("选择这段文字"));
        try
        {
            Assert.False(editor.Formatting.IsVisible);
            editor.Surface.Select(0, 4); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            editor.InputClient.SetPreeditText("zhongwen"); Frame(window);
            Assert.False(editor.Formatting.IsVisible);
            editor.InputClient.SetPreeditText(null); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            editor.Surface.Select(4, 0); Frame(window);
            Assert.False(editor.Formatting.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PointerFormattingRetainsTheExactSelectionAndMixedStateAndUndo()
    {
        var (window, editor) = Open(NoteNode.Paragraph("前选择文字后"));
        try
        {
            editor.Surface.Select(1, 4); Frame(window);
            Click(window, Action(editor, "Format_bold"));
            Assert.Equal("选择文字", editor.Surface.SelectedText);
            Assert.True(((ToggleButton)Action(editor, "Format_bold")).IsChecked);
            var runs = editor.Session.Root.Content[0].Content;
            Assert.Equal("选择文字", runs.Single(run => run.Marks.Any(mark => mark.Type == "bold")).Text);
            Click(window, Action(editor, "Format_italic"));
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(MarkCoverage.All, new SelectionFormats(editor.Session.Projection, 1, 4).Coverage("bold"));
            Assert.Equal(MarkCoverage.None, new SelectionFormats(editor.Session.Projection, 1, 4).Coverage("italic"));
            editor.Surface.Select(0, 5); Frame(window);
            Assert.Null(((ToggleButton)Action(editor, "Format_bold")).IsChecked);
            Click(window, Action(editor, "Format_bold"));
            Assert.Equal(MarkCoverage.All, new SelectionFormats(editor.Session.Projection, 0, 5).Coverage("bold"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void HighlightPaletteSetsColorAndRemovesMixedHighlightWithoutChangingOtherMarks()
    {
        var (window, editor) = Open(NoteNode.Paragraph("高亮文字"));
        try
        {
            editor.Surface.Select(0, 2); Frame(window);
            Click(window, Action(editor, "Format_bold"));
            Click(window, Action(editor, "FormatHighlight"));
            Click(window, Named(editor, "浅蓝高亮"));
            Assert.Equal("#D5E9F7", new SelectionFormats(editor.Session.Projection, 0, 2).UniformMark("highlight")?.String("color"));
            editor.Surface.SelectAll(); Frame(window);
            Click(window, Action(editor, "FormatHighlight"));
            Click(window, Named(editor, "移除高亮"));
            Assert.Equal(MarkCoverage.None, new SelectionFormats(editor.Session.Projection, 0, 4).Coverage("highlight"));
            Assert.Equal(MarkCoverage.All, new SelectionFormats(editor.Session.Projection, 0, 2).Coverage("bold"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LinkFormExpandsAcrossBoldRunsPrefillsAndChangesTheWholeLinkWithOneUndo()
    {
        var link = NoteMark.With("link", "href", "https://example.com");
        var paragraph = NoteNode.Paragraph() with { Content = [Run("前"), Run("链接", link), Run("粗体", link, new("bold")), Run("后")] };
        var (window, editor) = Open(paragraph);
        try
        {
            editor.Surface.CaretOffset = 4; Frame(window);
            Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control);
            Assert.Equal("链接粗体", editor.Surface.SelectedText);
            var input = LinkInput(editor);
            Assert.Equal("https://example.com", input.Text);
            Assert.True(input.IsFocused);
            window.KeyTextInput("example.org"); Frame(window);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal("https://example.org", new SelectionFormats(editor.Session.Projection, 1, 4).UniformMark("link")?.String("href"));
            Assert.Contains(editor.Session.Root.Content[0].Content, run => run.Text == "粗体" && run.Marks.Any(mark => mark.Type == "bold"));
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal("https://example.com", new SelectionFormats(editor.Session.Projection, 1, 4).UniformMark("link")?.String("href"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LinkDraftRejectsInvalidAddressesAndImeEnterAndCancelsWithoutMutation()
    {
        var (window, editor) = Open(NoteNode.Paragraph("待添加链接"));
        try
        {
            editor.Surface.Select(0, 5); Frame(window);
            Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control);
            var input = LinkInput(editor);
            window.KeyTextInput("javascript:alert(1)"); Frame(window);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.False(editor.Session.CanUndo);
            Assert.True(editor.Formatting.GetVisualDescendants().OfType<TextBlock>().Single(block => AutomationProperties.GetAutomationId(block) == "LinkError").IsVisible);
            input.Text = "example.com";
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            presenter.PreeditText = "ni";
            Key(window, Avalonia.Input.Key.Enter);
            Assert.False(editor.Session.CanUndo);
            Assert.True(input.IsEffectivelyVisible);
            Key(window, Avalonia.Input.Key.Escape);
            Assert.True(editor.Formatting.IsVisible);
            presenter.PreeditText = null;
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(editor.Formatting.IsVisible);
            Assert.Equal("待添加链接", editor.Surface.Text);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void KeyboardToolbarNavigationPreservesSelectionAndReturnsTabToBlockIndentation()
    {
        var (window, editor) = Open(NoteNode.Toggle("父级"), NoteNode.Toggle("另一块"));
        try
        {
            editor.Surface.Select(3, 3); Frame(window);
            Key(window, Avalonia.Input.Key.F10, RawInputModifiers.Alt);
            Assert.True(Action(editor, "FormatBlock").IsFocused);
            Key(window, Avalonia.Input.Key.Right);
            Assert.True(Action(editor, "Format_bold").IsFocused);
            Key(window, Avalonia.Input.Key.Space);
            Assert.True(Action(editor, "Format_bold").IsFocused);
            Assert.Equal("另一块", editor.Surface.SelectedText);
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(editor.Formatting.IsVisible);
            Key(window, Avalonia.Input.Key.F10, RawInputModifiers.Alt);
            Assert.True(editor.Formatting.IsVisible);
            Assert.True(Action(editor, "FormatBlock").IsFocused);
            Key(window, Avalonia.Input.Key.Escape);
            Key(window, Avalonia.Input.Key.Tab);
            Assert.Single(editor.Session.Root.Content);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Content[1].Type);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DraftIsCancelledByDocumentChangeAndCannotApplyToANewDocument()
    {
        var (window, editor) = Open(NoteNode.Paragraph("旧笔记内容"));
        try
        {
            editor.Surface.SelectAll(); Frame(window);
            Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control);
            LinkInput(editor).Text = "example.com";
            var staleApply = Named(editor, "应用");
            editor.Load(new(new("doc") { Content = [NoteNode.Paragraph("新笔记内容")] }));
            Frame(window);
            Assert.False(editor.Formatting.IsVisible);
            editor.FocusText();
            editor.Surface.SelectAll(); Frame(window);
            staleApply.RaiseEvent(new(Avalonia.Controls.Button.ClickEvent));
            Assert.Equal("新笔记内容", editor.Session.Projection.Text);
            Assert.False(editor.Session.CanUndo);
            Assert.True(editor.Formatting.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarStaysInsideNarrowViewportAndHidesWhenSelectionScrollsAway()
    {
        var (window, editor) = Open(Enumerable.Range(0, 120).Select(index => NoteNode.Paragraph($"第 {index} 段：用于检查工具栏的位置和长文档滚动。")).ToArray());
        try
        {
            window.Width = 340; Frame(window);
            editor.Surface.Select(0, 8); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            Assert.InRange(editor.Formatting.Bounds.Left, 0, editor.Bounds.Width);
            Assert.True(editor.Formatting.Bounds.Right <= editor.Bounds.Width);
            Assert.True(editor.Formatting.Bounds.Bottom <= editor.Bounds.Height);
            window.MouseWheel(new(280, 380), new(0, -12)); Frame(window);
            Assert.True(editor.Surface.VerticalOffset > 0);
            Assert.False(editor.Formatting.IsVisible, $"Scroll offset: {editor.Surface.VerticalOffset}; first visible row: {editor.Surface.TextArea.TextView.VisualLines[0].FirstDocumentLine.LineNumber}");
            editor.Surface.ScrollToHome(); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ToolbarHidesDuringPointerSelectionAndWhenFocusLeavesTheEditor()
    {
        var editor = new BlockEditor(new(new("doc") { Content = [NoteNode.Paragraph("从这里选择文字并检查浮层"), NoteNode.Paragraph("另一段文字")] }));
        var otherInput = new TextBox { Watermark = "其他输入框" };
        var grid = new Grid { RowDefinitions = new("40,*") };
        grid.Children.Add(otherInput);
        Grid.SetRow(editor, 1);
        grid.Children.Add(editor);
        var window = new Window { Width = 740, Height = 480, Content = grid };
        window.Show(); editor.FocusText(); Frame(window);
        try
        {
            editor.Surface.Select(0, 6); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            otherInput.Focus(); Frame(window);
            Assert.False(editor.Formatting.IsVisible);
            editor.FocusText(); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            var start = editor.Surface.TextArea.TextView.TranslatePoint(new(65, 12), window)!.Value;
            var end = editor.Surface.TextArea.TextView.TranslatePoint(new(200, 12), window)!.Value;
            window.MouseDown(start, MouseButton.Left); Frame(window);
            Assert.False(editor.Formatting.IsVisible);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); Frame(window);
            Assert.False(editor.Formatting.IsVisible);
            window.MouseUp(end, MouseButton.Left); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CombinedUnderlineStrikeAndCodeBoldAreAllRendered()
    {
        var (window, editor) = Open(NoteNode.Paragraph() with { Content = [Run("组合文字样式", new("underline"), new("strike"), new("bold"), new("italic"), new("code"))] });
        try
        {
            var properties = editor.Surface.TextArea.TextView.VisualLines[0].Elements.First(element => element.DocumentLength > 0).TextRunProperties;
            Assert.Equal(FontWeight.Bold, properties.Typeface.Weight);
            Assert.Equal(FontStyle.Italic, properties.Typeface.Style);
            Assert.Contains(properties.TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Underline);
            Assert.Contains(properties.TextDecorations!, decoration => decoration.Location == TextDecorationLocation.Strikethrough);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CrossScreenSelectionAndLinkPanelFitWithinTheEditor()
    {
        var (window, editor) = Open(Enumerable.Range(0, 70).Select(index => NoteNode.Paragraph($"第 {index} 段文字，需要跨段落选择和设置格式。")).ToArray());
        try
        {
            window.Width = 380; Frame(window);
            editor.Surface.SelectAll(); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            Click(window, Action(editor, "FormatLink"));
            Assert.True(LinkInput(editor).IsFocused);
            Assert.True(editor.Formatting.Bounds.Right <= editor.Bounds.Width);
            Assert.True(editor.Formatting.Bounds.Bottom <= editor.Bounds.Height);
            Assert.True(editor.Formatting.Bounds.Top >= 0);
            Key(window, Avalonia.Input.Key.Escape);
            editor.Surface.Select(2, 5); Frame(window);
            Assert.True(editor.Formatting.IsVisible);
            Assert.Equal(5, editor.Surface.SelectionLength);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LinkDraftCannotCommitAfterTheDocumentChangesWhileTheFormIsOpen()
    {
        var (window, editor) = Open(NoteNode.Paragraph("原来的文字"));
        try
        {
            editor.Surface.SelectAll(); Frame(window);
            Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control);
            LinkInput(editor).Text = "example.com";
            var staleApply = Named(editor, "应用");
            editor.Session.Edit(0, 0, "新增"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.FocusText(); editor.Surface.SelectAll(); Frame(window);
            Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control);
            var newInput = LinkInput(editor);
            staleApply.RaiseEvent(new(Button.ClickEvent)); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.Same(newInput, LinkInput(editor));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingNearTheViewportEdgeScrollsAndKeepsTheToolbarHidden()
    {
        var blocks = Enumerable.Range(0, 90).Select(index => NoteNode.Toggle($"折叠块 {index}")).ToArray();
        var (window, editor) = Open(blocks);
        try
        {
            var grip = editor.Surface.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("blockGrip") && Equals(button.Tag, blocks[0].Id));
            var start = grip.TranslatePoint(new(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            var end = editor.Surface.TextArea.TextView.TranslatePoint(new(100, editor.Surface.TextArea.TextView.Bounds.Height - 10), window)!.Value;
            window.MouseMove(start); Frame(window);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton); Frame(window);
            for (var i = 0; i < 20 && editor.Surface.VerticalOffset < 1; i++) { await Task.Delay(20); Frame(window); }
            Assert.True(editor.Surface.VerticalOffset > 0);
            Assert.False(editor.Formatting.IsVisible);
            window.MouseUp(end, MouseButton.Left); Frame(window);
            Assert.Equal(90, NoteTree.Descendants(editor.Session.Root).Count(node => node.Type == "toggleBlock"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativeWindowSavesFloatingToolbarFormattingAndRendersTheCompactPanels()
    {
        using var temporary = new TestDirectory();
        using var probe = new NoteStore(temporary.Path);
        var paragraph = NoteNode.Paragraph("选择文字，让想法更清晰。用格式区分重点，让页面保持干净。");
        var document = probe.Create("把想法写下来", new("doc") { Content = [NoteNode.Paragraph("一页笔记，从一个想法开始") with { Type = "heading", Attrs = NoteNode.Paragraph().WithAttr("level", 2).Attrs }, paragraph, NoteNode.Toggle("本周的灵感", NoteNode.Toggle("把一个小想法变成计划", NoteNode.Paragraph("折叠层级与格式独立保存。")))] });
        var window = new MainWindow(temporary.Path, false);
        window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
        try
        {
            editor.FocusText();
            var row = editor.Session.Projection.Rows.Single(block => block.Text == RichText.Plain(paragraph));
            editor.Surface.Select(row.Start, 4); Frame(window);
            Click(window, Action(editor, "Format_bold"));
            SaveImage(window, "toolbar.png");
            Click(window, Action(editor, "FormatHighlight"));
            SaveImage(window, "highlight.png");
            Click(window, Named(editor, "浅蓝高亮"));
            Click(window, Action(editor, "FormatLink"));
            SaveImage(window, "link.png");
            window.KeyTextInput("example.com"); Frame(window);
            Key(window, Avalonia.Input.Key.Enter);
            window.Close();
            for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
            var stored = new DocumentSession(NoteJson.Parse(probe.Get(document.Id).Content));
            var formats = new SelectionFormats(stored.Projection, row.Start, 4);
            Assert.Equal(MarkCoverage.All, formats.Coverage("bold"));
            Assert.Equal("#D5E9F7", formats.UniformMark("highlight")?.String("color"));
            Assert.Equal("https://example.com", formats.UniformMark("link")?.String("href"));
            Assert.Equal(2, NoteTree.Descendants(stored.Root).Count(node => node.Type == "toggleBlock"));
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
                for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            }
        }
    }

    private static void SaveImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory);
        Frame(window);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, name));
    }
}
