using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class CraftInteractionTests
{
    private static (Window Window, BlockEditor Editor, EditorSidebar Sidebar) Open(params NoteNode[] nodes)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. nodes] }));
        var sidebar = new EditorSidebar(editor);
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(16, 0, 0, 0) };
        grid.Children.Add(editor);
        Grid.SetColumn(sidebar, 1);
        grid.Children.Add(sidebar);
        var window = new Window { Width = 960, Height = 710, Content = grid };
        window.Show(); editor.FocusText(); Frame(window);
        return (window, editor, sidebar);
    }
    private static (Window Window, BlockEditor Editor, DocumentOutlinePane Outline) OpenOutline(params NoteNode[] nodes)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. nodes] }));
        var outline = new DocumentOutlinePane(editor);
        var grid = new Grid { ColumnDefinitions = new("292,*") };
        grid.Children.Add(outline);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        var window = new Window { Width = 1080, Height = 710, Content = grid };
        window.Show(); editor.FocusText(); Frame(window);
        return (window, editor, outline);
    }
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var first = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        using var settled = window.CaptureRenderedFrame();
    }
    private static void Key(Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, "");
        window.KeyRelease(key, modifiers, PhysicalKey.None, "");
        Frame(window);
    }
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Frame(window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Frame(window);
    }
    private static ToggleDisclosureButton Arrow(BlockEditor editor, int row) => editor.Surface.GetVisualDescendants().OfType<ToggleDisclosureButton>()
        .Single(button => button.Classes.Contains("toggleArrow") && Equals(button.Tag, editor.Session.Projection.Rows[row].Block.Id));
    private static void SaveImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(directory, name));
    }

    [AvaloniaTheory]
    [InlineData("markdown")]
    [InlineData("slash")]
    [InlineData("shortcut")]
    public void EveryKeyboardCreationStartsWithRightArrowAndEnterOpensTheParent(string path)
    {
        var (window, editor, _) = Open(NoteNode.Paragraph());
        try
        {
            if (path == "markdown") { window.KeyTextInput("+"); Key(window, Avalonia.Input.Key.Space); }
            else if (path == "slash") { window.KeyTextInput("/折叠"); Frame(window); Key(window, Avalonia.Input.Key.Enter); }
            else Key(window, Avalonia.Input.Key.D7, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Type);
            Assert.True(editor.Session.Root.Content[0].Bool("collapsed"));
            Assert.False(Arrow(editor, 0).IsExpanded);
            window.KeyTextInput("把一个想法收纳起来"); Frame(window);
            if (path == "markdown") SaveImage(window, "toggle-new.png");
            var before = NoteJson.Serialize(editor.Session.Root);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(Arrow(editor, 0).IsExpanded);
            Assert.False(Arrow(editor, 1).IsExpanded);
            Assert.Equal(new[] { 0 }, editor.Session.Projection.Rows[1].GuideDepths);
            Assert.Equal(editor.Session.Projection.Rows[1].Start, editor.Surface.CaretOffset);
            window.KeyTextInput("这里是折叠块内部的内容"); Frame(window);
            if (path == "markdown") SaveImage(window, "toggle-enter.png");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(Arrow(editor, 0).IsExpanded);
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Assert.True(Arrow(editor, 0).IsExpanded);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyTitleEnterCreatesAChildAndMovingItOutKeepsTheRightArrow()
    {
        var (window, editor, _) = Open(NoteNode.Toggle(""));
        try
        {
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(2, editor.Session.Projection.Rows.Length);
            Assert.True(Arrow(editor, 0).IsExpanded);
            Assert.False(Arrow(editor, 1).IsExpanded);
            Key(window, Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
            Assert.Equal(2, editor.Session.Root.Content.Length);
            Assert.All(editor.Session.Root.Content, node => Assert.Equal("toggleBlock", node.Type));
            Assert.False(Arrow(editor, 0).IsExpanded);
            Assert.False(Arrow(editor, 1).IsExpanded);
            Assert.All(editor.Session.Projection.Rows, row => Assert.Empty(row.GuideDepths));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarIndentationMovesTheWholeToggleAndSharesUndoHistory()
    {
        var parent = NoteNode.Toggle("本周计划").WithAttr("collapsed", true);
        var child = NoteNode.Toggle("阅读与整理", NoteNode.Paragraph("保留这段正文")).WithAttr("collapsed", true);
        var (window, editor, sidebar) = Open(parent, child);
        try
        {
            editor.Surface.CaretOffset = editor.Session.Projection.Rows[1].Start; Frame(window);
            Click(window, Find<Button>(sidebar, "SidebarStyle"));
            var indent = Find<Button>(sidebar, "SidebarIndent");
            var outdent = Find<Button>(sidebar, "SidebarOutdent");
            Assert.True(indent.IsEnabled);
            Assert.False(outdent.IsEnabled);
            Click(window, indent);
            Assert.Single(editor.Session.Root.Content);
            Assert.Equal(child, editor.Session.Root.Content[0].Content[1]);
            Assert.False(editor.Session.Root.Content[0].Bool("collapsed"));
            Assert.True(outdent.IsEnabled);
            Assert.False(Arrow(editor, 1).IsExpanded);
            Click(window, outdent);
            Assert.Equal(child, editor.Session.Root.Content[1]);
            Assert.False(outdent.IsEnabled);
            Assert.True(editor.Surface.IsKeyboardFocusWithin);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Single(editor.Session.Root.Content);
            Assert.Equal(child, editor.Session.Root.Content[0].Content[1]);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(new[] { parent, child }, editor.Session.Root.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarSearchInsertsWithoutReplacingTheSelectionAndCanSwitchPanelsRepeatedly()
    {
        var (window, editor, sidebar) = Open(NoteNode.Paragraph("原文保持完整"));
        try
        {
            editor.Surface.Select(0, 4); Frame(window);
            Click(window, Find<Button>(sidebar, "SidebarInsert"));
            var search = Find<TextBox>(sidebar, "SidebarSearch");
            search.Focus(); window.KeyTextInput("zhedie"); Frame(window);
            Assert.Single(sidebar.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button)?.StartsWith("Insert_") == true);
            Click(window, Find<Button>(sidebar, "Insert_toggleBlock1"));
            Assert.Equal("原文保持完整", RichText.Plain(editor.Session.Root.Content[0]));
            Assert.True(editor.Session.Root.Content[1].Bool("collapsed"));
            Assert.False(Arrow(editor, 1).IsExpanded);
            Assert.True(editor.Surface.IsKeyboardFocusWithin);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Single(editor.Session.Root.Content);
            for (var i = 0; i < 3; i++)
                foreach (var page in new[] { EditorPanel.Style, EditorPanel.Insert }) { sidebar.Open(page); Frame(window); Assert.Equal(page, sidebar.ActivePanel); }
            search.Text = "没有这种类型"; Frame(window);
            var before = editor.Session.Revision;
            search.Focus(); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(before, editor.Session.Revision);
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(sidebar.IsOpen);
            Assert.True(editor.Surface.IsKeyboardFocusWithin);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarFormattingPreservesSelectionMixedStateAndSingleStepUndo()
    {
        var paragraph = NoteNode.Paragraph() with { Content = [new("text") { Text = "粗体", Marks = [new("bold")] }, new("text") { Text = "普通文字" }] };
        var (window, editor, sidebar) = Open(paragraph, NoteNode.Toggle("折叠标题", NoteNode.Paragraph("不能修改的内容")).WithAttr("collapsed", true));
        try
        {
            editor.Surface.Select(0, 4); Frame(window);
            Click(window, Find<Button>(sidebar, "SidebarStyle"));
            var bold = Find<ToggleButton>(sidebar, "SidebarFormat_bold");
            Assert.Null(bold.IsChecked);
            Click(window, bold);
            Assert.True(bold.IsChecked);
            Assert.Equal(4, editor.Surface.SelectionLength);
            Assert.Equal(MarkCoverage.All, new SelectionFormats(editor.Session.Projection, 0, 4).Coverage("bold"));
            Assert.Empty(editor.Session.Root.Content[1].Content[1].Content[0].Marks);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Null(bold.IsChecked);
            Click(window, Find<Button>(sidebar, "SidebarHighlight浅绿"));
            var revision = editor.Session.Revision;
            Click(window, Find<Button>(sidebar, "SidebarHighlight浅绿"));
            Assert.Equal(revision, editor.Session.Revision);
            Assert.Equal("#D9EED1", new SelectionFormats(editor.Session.Projection, 0, 4).UniformMark("highlight")?.String("color"));
            Click(window, Find<Button>(sidebar, "SidebarLink"));
            Assert.True(Find<TextBox>(editor, "LinkAddress").IsKeyboardFocusWithin);
            window.KeyTextInput("example.com"); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal("https://example.com", new SelectionFormats(editor.Session.Projection, 0, 4).UniformMark("link")?.String("href"));
            editor.Surface.Select(0, 0); Frame(window);
            Assert.False(bold.IsEnabled);
            var before = NoteJson.Serialize(editor.Session.Root);
            bold.RaiseEvent(new(Button.ClickEvent)); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OutlineNavigatesIntoHiddenContentScrollsAndRefreshesWhenChangingDocuments()
    {
        var heading = NoteNode.Paragraph("隐藏的小节").WithAttr("level", 2) with { Type = "heading" };
        var nested = NoteNode.Toggle("隐藏的子计划", heading, NoteNode.Paragraph("隐藏的正文")).WithAttr("collapsed", true);
        var parent = NoteNode.Toggle("远处的计划", nested).WithAttr("collapsed", true);
        var nodes = Enumerable.Range(0, 80).Select(index => NoteNode.Paragraph($"第 {index} 段正文")).Append(parent).ToArray();
        var (window, editor, navigator) = OpenOutline(nodes);
        try
        {
            var outline = Find<ListBox>(navigator, "DocumentOutline");
            var entry = Assert.Single(outline.ItemsSource!.Cast<OutlineEntry>());
            Assert.Equal("隐藏的小节", entry.Title);
            var index = outline.ItemsSource!.Cast<OutlineEntry>().ToList().IndexOf(entry);
            Click(window, outline.ContainerFromIndex(index)!);
            Assert.Equal(heading.Id, editor.Session.Selection.Caret.NodeId);
            Assert.False(NoteTree.Find(editor.Session.Root, parent.Id)!.Bool("collapsed"));
            Assert.False(NoteTree.Find(editor.Session.Root, nested.Id)!.Bool("collapsed"));
            Assert.True(editor.Surface.VerticalOffset > 100);
            Key(window, Avalonia.Input.Key.T, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(NoteTree.Find(editor.Session.Root, parent.Id)!.Bool("collapsed"));
            Key(window, Avalonia.Input.Key.T, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Contains("隐藏的正文", editor.Session.Projection.Text);
            editor.Session.ConvertBlock(editor.Session.Projection.Find(heading.Id)!.Start, "paragraph"); Frame(window);
            Assert.Empty(outline.ItemsSource!.Cast<OutlineEntry>());
            editor.Session.Undo(); Frame(window);
            Assert.Single(outline.ItemsSource!.Cast<OutlineEntry>());
            editor.Load(new(NoteNode.EmptyDocument())); Frame(window);
            Assert.Empty(outline.ItemsSource!.Cast<OutlineEntry>());
            outline.SelectedItem = entry; Frame(window);
            Assert.Single(editor.Session.Root.Content);
            Assert.False(editor.Session.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SidebarRespectsEditorCompositionAndSearchImeConfirmation()
    {
        var (window, editor, sidebar) = Open(NoteNode.Paragraph("原文"));
        try
        {
            sidebar.Open(EditorPanel.Insert); Frame(window);
            editor.InputClient.SetPreeditText("zhongwen"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            Find<Button>(sidebar, "Insert_toggleBlock1").RaiseEvent(new(Button.ClickEvent)); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            editor.InputClient.SetPreeditText(null); Frame(window);
            var search = Find<TextBox>(sidebar, "SidebarSearch");
            search.Focus(); search.Text = "折叠"; Frame(window);
            var presenter = search.GetVisualDescendants().OfType<TextPresenter>().Single();
            presenter.PreeditText = "zhedie";
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Key(window, Avalonia.Input.Key.Escape);
            Assert.True(sidebar.IsOpen);
            presenter.PreeditText = null;
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[1].Type);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void LargeOutlineCreatesOnlyVisibleContainersAndCaretChangesReuseItsEntries()
    {
        var blocks = Enumerable.Range(0, 10000).Select(index => NoteNode.Paragraph($"第 {index} 节").WithAttr("level", 2) with { Type = "heading" }).ToArray();
        var (window, editor, navigator) = OpenOutline(blocks);
        try
        {
            var list = Find<ListBox>(navigator, "DocumentOutline");
            Assert.Equal(10000, list.ItemCount);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 70);
            var entries = list.ItemsSource;
            editor.Surface.CaretOffset = editor.Session.Projection.Rows[2].Start; Frame(window);
            Assert.Same(entries, list.ItemsSource);
            navigator.FocusOutline(); Frame(window);
            list.SelectedIndex = 9999; Frame(window);
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(blocks[^1].Id, editor.Session.Selection.Caret.NodeId);
            Assert.True(editor.Surface.VerticalOffset > 1000);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 70);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task NativeWindowSidebarSavesNewFoldsAndAdaptsToANarrowWindow()
    {
        using var temporary = new TestDirectory();
        using var probe = new NoteStore(temporary.Path);
        var heading = NoteNode.Paragraph("阅读与整理").WithAttr("level", 2) with { Type = "heading" };
        var document = probe.Create("留一页空间，写下今天", new("doc") { Content = [NoteNode.Paragraph("从这里开始，把一个小想法写成清晰的计划。"), NoteNode.Toggle("本周计划", heading, NoteNode.Toggle("阅读札记", NoteNode.Paragraph("读完一本书，留下一点自己的思考。"))).WithAttr("collapsed", true)] });
        var window = new MainWindow(temporary.Path, false);
        window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
        var sidebar = window.GetVisualDescendants().OfType<EditorSidebar>().Single();
        try
        {
            Assert.False(window.FindControl<Grid>("SpacePane")!.IsVisible);
            var directory = Find<ListBox>(window, "DocumentOutline");
            Assert.Equal("阅读与整理", Assert.Single(directory.ItemsSource!.Cast<OutlineEntry>()).Title);
            Assert.DoesNotContain(sidebar.GetVisualDescendants(), control => AutomationProperties.GetAutomationId(control) is "SidebarOutline" or "DocumentOutline");
            SaveImage(window, "sidebar-closed.png");
            editor.FocusText(); editor.Surface.Select(0, 6); Frame(window);
            Click(window, Find<Button>(sidebar, "SidebarStyle"));
            Click(window, Find<Button>(sidebar, "SidebarFormat_bold"));
            SaveImage(window, "sidebar-style.png");
            Click(window, Find<Button>(sidebar, "SidebarInsert"));
            Click(window, Find<Button>(sidebar, "Insert_toggleBlock1"));
            window.KeyTextInput("今天的灵感"); Frame(window);
            SaveImage(window, "sidebar-insert.png");
            Key(window, Avalonia.Input.Key.Enter);
            window.KeyTextInput("把一个小想法慢慢展开"); Frame(window);
            Assert.True(Arrow(editor, 1).IsExpanded);
            Assert.False(Arrow(editor, 2).IsExpanded);
            Click(window, Find<Button>(sidebar, "SidebarClose"));
            Click(window, Find<Button>(window, "ShowSpaceSidebar"));
            Assert.True(window.FindControl<Grid>("SpacePane")!.IsVisible);
            Click(window, Find<Button>(window, "ShowDocumentSidebar"));
            Assert.False(window.FindControl<Grid>("SpacePane")!.IsVisible);
            Assert.True(directory.TranslatePoint(new(0, 0), window)!.Value.X < editor.TranslatePoint(new(0, 0), window)!.Value.X);
            Assert.Single(directory.ItemsSource!.Cast<OutlineEntry>());
            SaveImage(window, "sidebar-outline.png");
            window.Width = 820; Frame(window);
            SaveImage(window, "sidebar-narrow-outline.png");
            Click(window, Find<Button>(sidebar, "SidebarInsert"));
            Assert.False(window.FindControl<Border>("LibrarySidebar")!.IsVisible);
            Assert.True(editor.Bounds.Width >= 400);
            Assert.True(sidebar.Bounds.Right <= window.Bounds.Width);
            SaveImage(window, "sidebar-narrow.png");
            Click(window, Find<Button>(sidebar, "SidebarClose"));
            Assert.True(window.FindControl<Border>("LibrarySidebar")!.IsVisible);
            window.Height = 560; Frame(window);
            Key(window, Avalonia.Input.Key.D1, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal(EditorPanel.Insert, sidebar.ActivePanel);
            var search = Find<TextBox>(sidebar, "SidebarSearch");
            Assert.True(search.IsKeyboardFocusWithin);
            SaveImage(window, "sidebar-compact.png");
            var scroll = search.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            var lastInsert = Find<Button>(sidebar, "Insert_horizontalRule1");
            lastInsert.BringIntoView(); Frame(window);
            Assert.True(scroll.Offset.Y > 0);
            var lastBottom = lastInsert.TranslatePoint(new(0, lastInsert.Bounds.Height), window)!.Value.Y;
            Assert.InRange(lastBottom, scroll.TranslatePoint(new(0, 0), window)!.Value.Y, window.Bounds.Height);
            SaveImage(window, "sidebar-compact-scrolled.png");
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(sidebar.IsOpen);
            Key(window, Avalonia.Input.Key.D2, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(sidebar.IsOpen);
            Key(window, Avalonia.Input.Key.D3, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.False(sidebar.IsOpen);
            Assert.True(window.FindControl<Border>("LibrarySidebar")!.IsVisible);
            Assert.True(directory.IsKeyboardFocusWithin);
            Key(window, Avalonia.Input.Key.Escape);
            Assert.True(editor.Surface.IsKeyboardFocusWithin);
            var expected = NoteJson.Serialize(editor.Session.Root);
            window.Close();
            for (var i = 0; i < 100 && window.IsVisible; i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
            Assert.False(window.IsVisible);
            Assert.Equal(expected, probe.Get(document.Id).Content);
            var stored = new DocumentSession(NoteJson.Parse(probe.Get(document.Id).Content));
            Assert.False(stored.Root.Content[1].Bool("collapsed"));
            Assert.True(stored.Root.Content[1].Content[1].Bool("collapsed"));
            Assert.True(stored.Root.Content[2].Bool("collapsed"));
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
