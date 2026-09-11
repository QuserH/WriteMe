using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class SlashMenuInteractionTests
{
    private static (Window Window, BlockEditor Editor) Open(params NoteNode[] blocks)
    {
        var editor = new BlockEditor(new(new("doc") { Content = [.. blocks] }));
        var window = new Window { Width = 780, Height = 580, Content = new Grid { Margin = new(30), Children = { editor } } };
        window.Show(); editor.FocusText(); Frame(window);
        return (window, editor);
    }
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs(); using var settled = window.CaptureRenderedFrame();
    }
    private static T Find<T>(Visual root, string id) where T : Control
        => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, ""); window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window);
    }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static void Image(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window);
        using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(directory, name));
    }

    [AvaloniaFact]
    public void EnterNavigatesCategoriesAndOnlyASecondConfirmationConvertsTheBlock()
    {
        var (window, editor) = Open(NoteNode.Paragraph("保留的正文"));
        try
        {
            window.KeyTextInput("/"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root); var revision = editor.Session.Revision;
            Assert.True(Find<Border>(editor, "SlashMenu").IsVisible);
            Assert.Contains("selected", Find<Button>(editor, "Slash_lists").Classes);
            Image(window, "slash-root.png");
            Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.Equal(revision, editor.Session.Revision);
            Assert.True(editor.Surface.TextArea.IsFocused);
            Assert.Contains("selected", Find<Button>(editor, "Slash_taskList").Classes);
            Image(window, "slash-lists.png");
            Key(window, Avalonia.Input.Key.Left);
            Assert.Contains("selected", Find<Button>(editor, "Slash_lists").Classes);
            Key(window, Avalonia.Input.Key.Right); Key(window, Avalonia.Input.Key.Down); Key(window, Avalonia.Input.Key.Enter);
            Assert.False(Find<Border>(editor, "SlashMenu").IsVisible);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Type);
            Assert.True(editor.Session.Root.Content[0].Bool("collapsed"));
            Assert.Equal("保留的正文", editor.Session.Projection.Text);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Type);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MouseNavigationReturnsAndChoosesAListWithoutStealingTheCaret()
    {
        var (window, editor) = Open(NoteNode.Paragraph("内容"));
        try
        {
            window.KeyTextInput("/"); Frame(window); var before = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Button>(editor, "Slash_lists"));
            Assert.True(editor.Surface.TextArea.IsFocused); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Click(window, Find<Button>(editor, "SlashBack"));
            Click(window, Find<Button>(editor, "Slash_lists"));
            Click(window, Find<Button>(editor, "Slash_bulletList"));
            Assert.Equal("bulletList", editor.Session.Root.Content[0].Type);
            Assert.Equal("内容", editor.Session.Projection.Text);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TableCategorySelectsTheRequestedSizeAndKeepsFollowingRichText()
    {
        var text = NoteNode.Paragraph() with { Content = [new("text") { Text = "保留粗体", Marks = [new("bold")] }] };
        var (window, editor) = Open(text);
        try
        {
            window.KeyTextInput("/"); Frame(window); var before = NoteJson.Serialize(editor.Session.Root);
            Click(window, Find<Button>(editor, "Slash_tables"));
            Image(window, "slash-tables.png");
            Key(window, Avalonia.Input.Key.Down); Key(window, Avalonia.Input.Key.Down); Key(window, Avalonia.Input.Key.Enter);
            var table = editor.Session.Root.Content[0];
            Assert.Equal("table", table.Type); Assert.Equal(4, table.Content.Length);
            Assert.All(table.Content, row => Assert.Equal(4, row.Content.Length));
            Assert.Equal("保留粗体", RichText.Plain(editor.Session.Root.Content[1]));
            Assert.Contains(editor.Session.Root.Content[1].Content[0].Marks, mark => mark.Type == "bold");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SearchFromASubmenuFindsLeafCommandsAndEscapeLeavesTheQueryUntouched()
    {
        var (window, editor) = Open(NoteNode.Paragraph());
        try
        {
            window.KeyTextInput("/"); Frame(window); Key(window, Avalonia.Input.Key.Enter);
            window.KeyTextInput("zhedie"); Frame(window);
            Assert.Contains("selected", Find<Button>(editor, "Slash_toggleBlock").Classes);
            Key(window, Avalonia.Input.Key.Enter); Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Type);
            editor.Load(new(new("doc") { Content = [NoteNode.Paragraph()] })); editor.FocusText();
            window.KeyTextInput("/不存在的命令"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            Key(window, Avalonia.Input.Key.Enter); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Key(window, Avalonia.Input.Key.Escape); Assert.False(Find<Border>(editor, "SlashMenu").IsVisible);
            Assert.Equal("/不存在的命令", editor.Surface.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OldMenusSelectionChangesAndImeCannotExecuteAStaleCommand()
    {
        var (window, editor) = Open(NoteNode.Paragraph("末尾"));
        try
        {
            window.KeyTextInput("/"); Frame(window); Key(window, Avalonia.Input.Key.Enter);
            var oldButton = Find<Button>(editor, "Slash_toggleBlock");
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.InputClient.SetPreeditText("zhong"); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Assert.False(Find<Border>(editor, "SlashMenu").IsVisible);
            editor.InputClient.Cancel();
            editor.Load(new(new("doc") { Content = [NoteNode.Paragraph("另一篇")] })); Frame(window);
            oldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Frame(window);
            Assert.Equal("另一篇", editor.Surface.Text); Assert.False(editor.Session.CanUndo);
            editor.FocusText(); window.KeyTextInput("/"); Frame(window); Key(window, Avalonia.Input.Key.Enter);
            var current = NoteJson.Serialize(editor.Session.Root);
            oldButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Frame(window);
            Assert.Equal(current, NoteJson.Serialize(editor.Session.Root));
            Assert.True(Find<Border>(editor, "SlashMenu").IsVisible);
            window.KeyTextInput("zhedie"); Frame(window);
            editor.Surface.Select(0, 2); Frame(window);
            Assert.False(Find<Border>(editor, "SlashMenu").IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SlashMenuInATableUsesThePageOverlayAndTheSharedHistory()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table, NoteNode.Paragraph("末尾"));
        try
        {
            editor.NavigateTo(LayoutBlocks.FirstText(table).Id, 0, 0); Frame(window);
            var cellEditor = editor.ActiveEditor; Assert.NotSame(editor, cellEditor);
            window.KeyTextInput("/"); Frame(window);
            var menu = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "SlashMenu" && control.IsVisible);
            Assert.True(menu.Bounds.Height > cellEditor.Bounds.Height);
            var origin = menu.TranslatePoint(default, editor)!.Value;
            Assert.True(origin.Y + menu.Bounds.Height <= editor.Bounds.Height + 1);
            var before = NoteJson.Serialize(editor.Session.Root);
            Key(window, Avalonia.Input.Key.Enter); Key(window, Avalonia.Input.Key.Escape);
            Assert.True(menu.IsVisible); Assert.Same(cellEditor, editor.ActiveEditor);
            Assert.Contains("selected", Find<Button>(menu, "Slash_lists").Classes);
            Key(window, Avalonia.Input.Key.Enter); Key(window, Avalonia.Input.Key.Down); Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal("toggleBlock", editor.Session.Root.Content[0].Content[0].Content[0].Content[0].Type);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            window.KeyTextInput("折叠"); Frame(window);
            Assert.True(menu.IsVisible);
            window.Close(); Dispatcher.UIThread.RunJobs();
            Assert.False(menu.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void APageOverlayCannotApplyCommandsToADisabledEmbeddedRegion()
    {
        var table = LayoutBlocks.Table(2, 2);
        var (window, editor) = Open(table);
        try
        {
            editor.NavigateTo(LayoutBlocks.FirstText(table).Id, 0, 0); Frame(window);
            var cellEditor = editor.ActiveEditor;
            window.KeyTextInput("/"); Frame(window); Key(window, Avalonia.Input.Key.Enter);
            var menu = editor.GetVisualDescendants().OfType<Border>().Single(control => AutomationProperties.GetAutomationId(control) == "SlashMenu" && control.IsVisible);
            var button = Find<Button>(menu, "Slash_toggleBlock");
            var before = NoteJson.Serialize(editor.Session.Root); var revision = editor.Session.Revision;
            var container = Assert.IsAssignableFrom<Control>(cellEditor.Parent);
            container.IsEnabled = false; Frame(window);
            Assert.True(cellEditor.IsEnabled); Assert.False(cellEditor.IsEffectivelyEnabled);
            Click(window, button);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root)); Assert.Equal(revision, editor.Session.Revision);
            Assert.False(menu.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OutsideClickDismissesWithoutRemovingTheQuery()
    {
        var (window, editor) = Open(NoteNode.Paragraph());
        try
        {
            window.KeyTextInput("/"); Frame(window);
            var before = NoteJson.Serialize(editor.Session.Root);
            window.MouseDown(new(10, 10), MouseButton.Left); window.MouseUp(new(10, 10), MouseButton.Left); Frame(window);
            Assert.False(Find<Border>(editor, "SlashMenu").IsVisible);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void MenuFitsAtTheBottomAndRemainsReadableInDarkAndNarrowWindows()
    {
        var theme = Application.Current!.RequestedThemeVariant;
        var (window, editor) = Open(Enumerable.Range(0, 10).Select(index => NoteNode.Paragraph("第 " + index + " 段正文")).Append(NoteNode.Paragraph()).ToArray());
        try
        {
            window.Width = 370; window.Height = 490; Frame(window);
            editor.Surface.CaretOffset = editor.Surface.Document.TextLength;
            editor.Surface.ScrollToEnd(); Frame(window); window.KeyTextInput("/"); Frame(window);
            var menu = Find<Border>(editor, "SlashMenu"); var origin = menu.TranslatePoint(default, editor)!.Value;
            Assert.True(origin.Y >= 0); Assert.True(origin.Y + menu.Bounds.Height <= editor.Bounds.Height + 1);
            Assert.True(origin.X + menu.Bounds.Width <= editor.Bounds.Width + 1);
            Image(window, "slash-narrow.png");
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark; Frame(window);
            Image(window, "slash-dark.png");
            Key(window, Avalonia.Input.Key.End); Key(window, Avalonia.Input.Key.Enter);
            Assert.Contains(editor.Session.Root.Content, node => node.Type == "horizontalRule");
        }
        finally { window.Close(); Application.Current.RequestedThemeVariant = theme; }
    }
}
