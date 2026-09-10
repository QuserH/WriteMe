using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
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

public sealed class TextColorTests
{
    private static NoteNode Run(string text, params NoteMark[] marks) => new("text") { Text = text, Marks = [.. marks] };
    private static DocumentSession Session(params NoteNode[] blocks) => new(new("doc") { Content = [.. blocks] });

    [Fact]
    public void ColorChangesOnlyTheSelectionPreservingOtherStyleAttributesAndHasNoRepeatedUndoEntry()
    {
        var font = NoteMark.With("textStyle", "fontFamily", "Example Font") with { Extra = System.Collections.Immutable.ImmutableDictionary<string, JsonElement>.Empty.Add("retained", JsonSerializer.SerializeToElement(true)) };
        var session = Session(NoteNode.Paragraph() with { Content = [Run("前"), Run("颜", new("bold"), font), Run("色", NoteMark.With("link", "href", "https://example.com"), NoteMark.With("highlight", "color", "#FFF0A8")), Run("后")] });
        var before = NoteJson.Serialize(session.Root);
        session.SetTextColor(1, 2, "#abc");
        var revision = session.Revision;
        Assert.Equal("#AABBCC", new SelectionFormats(session.Projection, 1, 2).UniformTextColor);
        Assert.Equal(MarkCoverage.Mixed, new SelectionFormats(session.Projection, 0, 4).TextColorCoverage);
        Assert.Null(new SelectionFormats(session.Projection, 0, 4).UniformTextColor);
        Assert.Null(TextColor.Read(session.Root.Content[0].Content[0].Marks));
        Assert.Null(TextColor.Read(session.Root.Content[0].Content[^1].Marks));
        session.SetTextColor(1, 2, "AABBCC");
        Assert.Equal(revision, session.Revision);
        var saved = NoteJson.Serialize(session.Root);
        var roundTrip = new DocumentSession(NoteJson.Parse(saved));
        Assert.Equal("#AABBCC", new SelectionFormats(roundTrip.Projection, 1, 2).UniformTextColor);
        session.SetTextColor(1, 2, null);
        Assert.Equal(before, NoteJson.Serialize(session.Root));
        session.Undo(); Assert.Equal(saved, NoteJson.Serialize(session.Root));
        session.Undo(); Assert.Equal(before, NoteJson.Serialize(session.Root));
        Assert.False(session.CanUndo);
    }

    [Fact]
    public void ColoringAcrossBlocksPreservesHiddenTextCodeBlocksAndSoftBreakOffsets()
    {
        var paragraph = NoteNode.Paragraph() with { Content = [Run("中文🙂"), new("hardBreak"), Run("下行")] };
        var hidden = NoteNode.Paragraph("隐藏内容");
        var code = NoteNode.Paragraph("原始代码") with { Type = "codeBlock" };
        var session = Session(paragraph, NoteNode.Toggle("折叠标题", hidden).WithAttr("collapsed", true), code);
        var text = session.Projection.Text;
        session.SetTextColor(0, text.Length, "#426BB3");
        Assert.Equal(text, session.Projection.Text);
        Assert.Equal("#426BB3", TextColor.Read(session.Root.Content[0].Content[2].Marks));
        Assert.Null(TextColor.Read(session.Root.Content[1].Content[1].Content[0].Marks));
        Assert.Same(code, session.Root.Content[2]);
        var revision = session.Revision;
        session.SetTextColor(0, text.Length, "##426BB3");
        Assert.Equal(revision, session.Revision);
        session.SetTextColor(0, text.Length, "not a color");
        Assert.Equal(revision, session.Revision);
    }

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var first = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        using var second = window.CaptureRenderedFrame();
    }
    private static void Key(Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, ""); window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window);
    }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static (Window Window, BlockEditor Editor, EditorSidebar Sidebar) Open()
    {
        var editor = new BlockEditor(Session(NoteNode.Paragraph("前选择文字后")));
        var sidebar = new EditorSidebar(editor);
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        grid.Children.Add(editor); Grid.SetColumn(sidebar, 1); grid.Children.Add(sidebar);
        var window = new Window { Width = 980, Height = 740, Content = grid };
        window.Show(); editor.FocusText(); Frame(window);
        editor.Surface.Select(1, 4); Frame(window);
        return (window, editor, sidebar);
    }
    private static void SaveImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window);
        using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(directory, name));
    }

    [AvaloniaFact]
    public void SidebarAndFloatingPaletteShareColorsAndDefaultRestoresTheTextWithUndo()
    {
        var (window, editor, sidebar) = Open();
        try
        {
            Click(window, Find<Button>(sidebar, "SidebarStyle"));
            Click(window, Find<Button>(sidebar, "SidebarTextColor_蓝色"));
            Assert.Equal("选择文字", editor.Surface.SelectedText);
            Assert.Equal("#426BB3", new SelectionFormats(editor.Session.Projection, 1, 4).UniformTextColor);
            var colored = editor.Surface.TextArea.TextView.VisualLines[0].Elements.Where(element => element.DocumentLength == 4).Single();
            Assert.Equal(Color.Parse("#426BB3"), Assert.IsAssignableFrom<ISolidColorBrush>(colored.TextRunProperties.ForegroundBrush).Color);
            Click(window, Find<Button>(sidebar, "SidebarClose"));
            Click(window, Find<Button>(editor, "FormatHighlight"));
            Assert.Equal("#426BB3", Find<TextBox>(editor, "FormatTextColorHex").Text);
            Click(window, Find<Button>(editor, "FormatTextColor_默认"));
            Assert.Equal(MarkCoverage.None, new SelectionFormats(editor.Session.Projection, 1, 4).TextColorCoverage);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal("#426BB3", new SelectionFormats(editor.Session.Projection, 1, 4).UniformTextColor);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomColorValidatesInputProtectsCompositionAndRejectsOldDocumentDrafts(bool inSidebar)
    {
        var (window, editor, sidebar) = Open();
        try
        {
            var prefix = inSidebar ? "Sidebar" : "Format";
            Visual host = inSidebar ? sidebar : editor;
            if (inSidebar) Click(window, Find<Button>(sidebar, "SidebarStyle"));
            else Click(window, Find<Button>(editor, "FormatHighlight"));
            var input = Find<TextBox>(host, prefix + "TextColorHex");
            input.Focus(); input.Text = "not a color"; Key(window, Avalonia.Input.Key.Enter);
            Assert.True(Find<TextBlock>(host, prefix + "TextColorError").IsVisible);
            Assert.False(editor.Session.CanUndo);
            input.Text = "#a3c";
            var presenter = input.GetVisualDescendants().OfType<TextPresenter>().Single();
            presenter.PreeditText = "ni";
            Key(window, Avalonia.Input.Key.Enter); Key(window, Avalonia.Input.Key.Escape);
            Assert.True(input.IsEffectivelyVisible);
            Assert.False(editor.Session.CanUndo);
            presenter.PreeditText = null;
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal("#AA33CC", new SelectionFormats(editor.Session.Projection, 1, 4).UniformTextColor);
            if (!inSidebar) Click(window, Find<Button>(editor, "FormatHighlight"));
            input = Find<TextBox>(host, prefix + "TextColorHex");
            input.Focus(); input.Text = "#123456";
            var oldApply = Find<Button>(host, prefix + "TextColorApply");
            var newSession = Session(NoteNode.Paragraph("另一篇文档"));
            editor.Load(newSession); Frame(window);
            oldApply.RaiseEvent(new(Button.ClickEvent)); Frame(window);
            Assert.False(newSession.CanUndo);
            Assert.Equal("另一篇文档", newSession.Projection.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativeWindowSavesTextColorsAndTaskRemovalAndTheColorPanelsFitNarrowWindows()
    {
        using var temporary = new TestDirectory();
        using var probe = new NoteStore(temporary.Path);
        var item = new NoteNode("taskItem") { Content = [NoteNode.Paragraph()] };
        var document = probe.Create("把计划写下来", new("doc") { Content = [NoteNode.Paragraph("用颜色区分重点，保留清晰的层级。"), new("taskList") { Content = [item] }, NoteNode.Toggle("本周计划", NoteNode.Paragraph("折叠内容独立保存。"))] });
        var window = new MainWindow(temporary.Path, false);
        window.Show(); Frame(window);
        try
        {
            var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
            var sidebar = window.GetVisualDescendants().OfType<EditorSidebar>().Single();
            editor.FocusText(); editor.Surface.Select(0, 7); Frame(window);
            Click(window, Find<Button>(sidebar, "SidebarStyle"));
            Click(window, Find<Button>(sidebar, "SidebarTextColor_蓝色"));
            SaveImage(window, "m3-colors-sidebar.png");
            window.Width = 820; window.Height = 560; Frame(window);
            var custom = Find<TextBox>(sidebar, "SidebarTextColorHex");
            custom.BringIntoView(); Frame(window);
            Assert.InRange(custom.TranslatePoint(default, window)!.Value.Y, 0, window.Bounds.Height - custom.Bounds.Height);
            SaveImage(window, "m3-colors-narrow.png");
            Click(window, Find<Button>(sidebar, "SidebarClose"));
            editor.Surface.Select(0, 7); Frame(window);
            Click(window, Find<Button>(editor, "FormatHighlight"));
            var hex = Find<TextBox>(editor, "FormatTextColorHex");
            hex.Focus(); hex.Text = "#476f88"; Key(window, Avalonia.Input.Key.Enter);
            Click(window, Find<Button>(editor, "FormatHighlight"));
            Assert.True(editor.Formatting.Bounds.Bottom <= editor.Bounds.Height);
            SaveImage(window, "m3-colors-floating.png");
            Key(window, Avalonia.Input.Key.Escape);
            var empty = editor.Session.Projection.Rows.Single(row => row.IsTask && row.Text.Length == 0);
            editor.Surface.Select(empty.Start, 0); editor.FocusText(); Key(window, Avalonia.Input.Key.Delete);
            Assert.DoesNotContain(NoteTree.Descendants(editor.Session.Root), node => node.Type == "taskItem");
            var expected = NoteJson.Serialize(editor.Session.Root);
            window.Close();
            for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
            Assert.Equal(expected, probe.Get(document.Id).Content);
            var stored = new DocumentSession(NoteJson.Parse(probe.Get(document.Id).Content));
            Assert.Equal("#476F88", new SelectionFormats(stored.Projection, 0, 7).UniformTextColor);
            Assert.DoesNotContain(NoteTree.Descendants(stored.Root), node => node.Type == "taskItem");
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
}
