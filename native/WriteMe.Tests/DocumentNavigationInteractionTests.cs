using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class DocumentNavigationInteractionTests
{
    private static NoteNode Doc(params NoteNode[] nodes) => new("doc") { Content = [.. nodes] };
    private static NoteNode Tasks(params string[] titles) => new("taskList") { Content = [.. titles.Select(title => new NoteNode("taskItem") { Content = [NoteNode.Paragraph(title)] })] };
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Frame(Window window) { Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); using var second = window.CaptureRenderedFrame(); }
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPress(key, modifiers, PhysicalKey.None, ""); window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window); }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window); Assert.True(control.IsEffectivelyVisible);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static (Window Window, BlockEditor Editor, DocumentOutlinePane Pane) Open(NoteNode root)
    {
        var editor = new BlockEditor(new(root)); var pane = new DocumentOutlinePane(editor);
        var layout = new Grid { ColumnDefinitions = new("292,*") }; layout.Children.Add(pane); Grid.SetColumn(editor, 1); layout.Children.Add(editor);
        var window = new Window { Content = layout, Width = 1080, Height = 740 }; window.Show(); Frame(window); return (window, editor, pane);
    }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(directory, name + ".png"));
    }
    private static async Task Wait(Func<bool> done) { for (var i = 0; i < 220 && !done(); i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); } Assert.True(done()); }

    [AvaloniaFact]
    public void TaskCheckboxEditsHiddenTaskWithoutNavigatingAndRowEnterRevealsIt()
    {
        var tasks = Tasks("整理阅读摘录", "核对引用出处");
        var hidden = NoteNode.Toggle("本周阅读", tasks).WithAttr("collapsed", true);
        var (window, editor, pane) = Open(Doc(NoteNode.Paragraph("正文"), hidden));
        try
        {
            Click(window, Find<Button>(pane, "DocumentTabTasks"));
            var list = Find<ListBox>(pane, "DocumentTasks"); Assert.Equal(2, list.ItemCount);
            var initial = editor.Session.Root;
            Click(window, Find<CheckBox>(pane, "DocumentTaskToggle_" + tasks.Content[0].Id));
            Assert.True(NoteTree.Find(editor.Session.Root, tasks.Content[0].Id)!.Bool("checked"));
            Assert.True(NoteTree.Find(editor.Session.Root, hidden.Id)!.Bool("collapsed"));
            editor.Session.Undo(); Frame(window); Assert.Same(initial, editor.Session.Root);
            list.SelectedIndex = 1; list.Focus(); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(tasks.Content[1].Content[0].Id, editor.Session.Selection.Caret.NodeId);
            Assert.False(NoteTree.Find(editor.Session.Root, hidden.Id)!.Bool("collapsed"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ResourceRowsLocateTheirSourceAndOldOpenButtonsCannotTargetAnotherDocument()
    {
        var file = new NoteNode("attachment").WithAttr("name", "阅读笔记.pdf");
        var link = NoteNode.Paragraph() with { Content = [new("text") { Text = "参考文章", Marks = [NoteMark.With("link", "href", "https://example.com/reading")] }] };
        var hidden = NoteNode.Toggle("资料", file, link).WithAttr("collapsed", true);
        var (window, editor, pane) = Open(Doc(NoteNode.Paragraph(), hidden));
        try
        {
            NoteNode? opened = null; editor.AssetInvoked += node => opened = node;
            Click(window, Find<Button>(pane, "DocumentTabResources"));
            var list = Find<ListBox>(pane, "DocumentResources"); Assert.Equal(2, list.ItemCount);
            var button = Find<Button>(pane, "DocumentResourceOpen_" + file.Id + "_0");
            Click(window, button); Assert.Equal(file.Id, opened!.Id);
            list.SelectedIndex = 1; list.Focus(); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(link.Id, editor.Session.Selection.Caret.NodeId); Assert.Equal("参考文章", editor.ActiveEditor.Surface.SelectedText);
            opened = null; editor.Load(new(Doc(NoteNode.Paragraph("另一篇笔记")))); Frame(window);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Null(opened); Assert.Equal(0, list.ItemCount);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FindNextAndPreviousReachTableContentAndKeepTheQueryFocused()
    {
        var cellText = NoteNode.Paragraph("表格里的阅读");
        var table = new NoteNode("table") { Content = [new("tableRow") { Content = [new("tableCell") { Content = [cellText] }, new("tableCell") { Content = [NoteNode.Paragraph("备注")] }] }] };
        var parent = NoteNode.Toggle("收起的章节", table).WithAttr("collapsed", true);
        var (window, editor, pane) = Open(Doc(NoteNode.Paragraph("阅读计划"), parent));
        try
        {
            pane.FocusFind(); Frame(window); var input = Find<TextBox>(pane, "DocumentFindInput");
            window.KeyTextInput("阅读"); Frame(window);
            var list = Find<ListBox>(pane, "DocumentFindResults"); Assert.Equal(2, list.ItemCount);
            Assert.False(editor.Session.CanUndo);
            Key(window, Avalonia.Input.Key.Enter); Assert.True(input.IsKeyboardFocusWithin); Assert.Equal("阅读", editor.Surface.SelectedText);
            Key(window, Avalonia.Input.Key.Enter); Assert.True(input.IsKeyboardFocusWithin); Assert.Equal(cellText.Id, editor.Session.Selection.Caret.NodeId);
            Assert.False(NoteTree.Find(editor.Session.Root, parent.Id)!.Bool("collapsed")); Assert.Equal("阅读", editor.ActiveEditor.Surface.SelectedText);
            Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Shift); Assert.Equal(editor.Session.Root.Content[0].Id, editor.Session.Selection.Caret.NodeId);
            input.Text = "不在这篇文章中"; Frame(window); Assert.Equal(0, list.ItemCount);
            Assert.False(Find<Button>(pane, "DocumentFindNext").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FindReplaceProtectsImeAndClearsTargetsWhenChangingDocuments()
    {
        var (window, editor, pane) = Open(Doc(NoteNode.Paragraph("阅读后再阅读")));
        try
        {
            pane.FocusFind(true); Frame(window);
            var find = Find<TextBox>(pane, "DocumentFindInput"); find.Text = "阅读";
            var replace = Find<TextBox>(pane, "DocumentReplaceInput"); replace.Text = "整理"; Frame(window);
            var initial = editor.Session.Root;
            var presenter = find.GetVisualDescendants().OfType<TextPresenter>().Single(); presenter.PreeditText = "zhongwen";
            Key(window, Avalonia.Input.Key.Enter);
            Find<Button>(pane, "DocumentReplaceAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Same(initial, editor.Session.Root);
            Click(window, Find<Button>(pane, "DocumentTabTasks")); Assert.Equal(DocumentPanel.Find, pane.ActivePanel);
            presenter.PreeditText = null; Frame(window);
            Click(window, Find<Button>(pane, "DocumentReplaceAll")); Assert.Equal("整理后再整理", editor.Session.Projection.Text);
            editor.FocusText(); Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control); Assert.Same(initial, editor.Session.Root);
            editor.Load(new(Doc(NoteNode.Paragraph("阅读另一篇文章")))); Frame(window);
            Assert.Equal("", find.Text); Assert.Equal("", replace.Text); Assert.Equal(0, Find<ListBox>(pane, "DocumentFindResults").ItemCount);
            Find<Button>(pane, "DocumentReplaceAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Assert.Equal("阅读另一篇文章", editor.Session.Projection.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WorkspaceShortcutsPanelsCommentsAndThemesStayUsableAtNarrowWidth()
    {
        using var directory = new TestDirectory();
        var introduction = NoteNode.Paragraph("读到喜欢的句子，就先记下来。让每一段都留一点继续思考的空间。");
        var reference = NoteNode.Paragraph() with { Content = [new("text") { Text = "延伸阅读 · 资料链接", Marks = [NoteMark.With("link", "href", "https://example.com/reading")] }] };
        var root = Doc(NoteNode.Paragraph("阅读与日常").WithAttr("level", 1) with { Type = "heading" }, introduction,
            NoteNode.Toggle("本周的阅读计划", Tasks("读完一本书的第二章", "摘录三个值得重读的段落", "把想法写成自己的话")),
            reference, new NoteNode("attachment").WithAttr("name", "秋日阅读清单.pdf").WithAttr("assetId", new string('B', 64)),
            NoteNode.Paragraph("让笔记慢慢生长").WithAttr("level", 2) with { Type = "heading" }, NoteNode.Paragraph("先记录，再整理；有疑问的地方，就在段落下面继续讨论。"));
        var seed = new DocumentSession(root);
        var id = seed.AddComment("这一段可以留一个自己的例子。", NoteComments.CaptureBlock(seed, introduction.Id));
        seed.ReplyComment(NoteComments.For(seed.Root).Find(id)!, "想补上今天散步时想到的一件小事。");
        var thread = NoteComments.For(seed.Root).Find(id)!;
        seed.ReplyComment(thread, "也把当时的感受记下来吧。", thread.Messages[1].Id);
        using (var store = new NoteStore(directory.Path)) store.Create("让想法，慢慢成形", seed.Root);
        var window = new MainWindow(directory.Path, false) { Width = 1460, Height = 900 }; window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().First();
        try
        {
            Save(window, "workspace-document-outline");
            Click(window, Find<Button>(window, "DocumentTabTasks")); Save(window, "workspace-document-tasks");
            var task = DocumentNavigation.For(editor.Session.Root).Tasks[0];
            Click(window, Find<CheckBox>(window, "DocumentTaskToggle_" + task.TaskId));
            Click(window, Find<Button>(window, "DocumentTabResources")); Save(window, "workspace-document-resources");
            editor.FocusText(); Key(window, Avalonia.Input.Key.H, RawInputModifiers.Control);
            var find = Find<TextBox>(window, "DocumentFindInput"); Assert.True(find.IsKeyboardFocusWithin);
            window.KeyTextInput("段落"); Frame(window); Find<TextBox>(window, "DocumentReplaceInput").Text = "文字片段"; Frame(window);
            Save(window, "workspace-document-find");
            Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(window.FindControl<TextBox>("SearchBox")!.IsKeyboardFocusWithin);
            Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control); Assert.True(find.IsKeyboardFocusWithin);
            Click(window, Find<Button>(window, "DocumentTabOutline"));
            var source = NoteComments.For(editor.Session.Root).Spans(id)[0]; editor.NavigateTo(source.NodeId); editor.RequestComment(); Frame(window);
            Save(window, "workspace-paragraph-discussion");
            Click(window, Find<Button>(window, "CommentsClose"));
            window.Width = 820; window.Height = 560; Frame(window);
            Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control); Save(window, "workspace-document-find-narrow");
            var pane = window.GetVisualDescendants().OfType<DocumentOutlinePane>().Single();
            Assert.True(pane.Bounds.Width >= 260); Assert.True(editor.Bounds.Width >= 330);
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(window); Save(window, "workspace-document-find-dark");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light; window.Close(); await Wait(() => !window.IsVisible);
        }
        using var reopened = new NoteStore(directory.Path);
        Assert.True(DocumentNavigation.For(NoteJson.Parse(reopened.Get(reopened.List()[0].Id).Content)).Tasks[0].Completed);
    }
}
