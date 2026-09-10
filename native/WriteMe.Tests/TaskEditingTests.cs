using Avalonia;
using Avalonia.Controls;
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

public sealed class TaskEditingTests
{
    private static NoteNode Item(string text, bool completed = false, params NoteNode[] children)
        => new NoteNode("taskItem") { Content = [NoteNode.Paragraph(text), .. children] }.WithAttr("checked", completed);
    private static NoteNode List(params NoteNode[] items) => new("taskList") { Content = [.. items] };
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Key(Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, "");
        window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackspaceAtTaskStartRemovesTheMarkerAndRetainsNeighborsAndChildren(bool completed)
    {
        var first = Item("前一项", true);
        var child = NoteNode.Toggle("保留折叠子树", NoteNode.Paragraph("不能丢失")).WithAttr("collapsed", true);
        var middle = Item("当前事项", completed, child);
        var last = Item("后一项");
        var session = new DocumentSession(new("doc") { Content = [List(first, middle, last)] });
        var before = NoteJson.Serialize(session.Root);
        var row = session.Projection.Find(middle.Content[0].Id)!;
        session.Selection = EditorSelection.At(row.Node.Id);
        Assert.True(session.BackspaceAtStart(row.Start));
        Assert.Equal("paragraph", session.Root.Content[1].Type);
        Assert.Equal("当前事项", RichText.Plain(session.Root.Content[1]));
        Assert.Same(first, session.Root.Content[0].Content[0]);
        Assert.Same(child, session.Root.Content[2]);
        Assert.Same(last, session.Root.Content[3].Content[0]);
        session.Undo();
        Assert.Equal(before, NoteJson.Serialize(session.Root));
    }

    [AvaloniaTheory]
    [InlineData(Avalonia.Input.Key.Back)]
    [InlineData(Avalonia.Input.Key.Delete)]
    [InlineData(Avalonia.Input.Key.Enter)]
    public void EmptyTaskCanBeRemovedWithTheKeyboardAndRestoredWithUndo(Avalonia.Input.Key key)
    {
        var item = Item("待办");
        var editor = new BlockEditor(new(new("doc") { Content = [List(item)] }));
        var window = new Window { Width = 680, Height = 480, Content = editor };
        window.Show(); editor.FocusText(); Frame(window);
        try
        {
            editor.Surface.SelectAll(); Key(window, Avalonia.Input.Key.Back);
            Assert.Equal("", editor.Surface.Text);
            Assert.True(editor.Session.Projection.Rows[0].IsTask);
            Key(window, key);
            Assert.Equal("paragraph", Assert.Single(editor.Session.Root.Content).Type);
            Assert.False(editor.Session.Projection.Rows[0].IsTask);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.True(editor.Session.Projection.Rows[0].IsTask);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Assert.Equal("待办", editor.Surface.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TaskHandleIsSeparateFromCheckboxAndItsMenuDeletesOnlyTheChosenItem()
    {
        var first = Item("保留这一项", true);
        var second = Item("删除这一项");
        var editor = new BlockEditor(new(new("doc") { Content = [List(first, second)] }));
        var window = new Window { Width = 680, Height = 480, Content = editor };
        window.Show(); editor.FocusText(); Frame(window);
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root);
            var grip = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("blockGrip") && Equals(button.Tag, second.Id));
            var panel = Assert.IsType<Canvas>(grip.GetVisualParent());
            var checkbox = panel.Children.OfType<Button>().Single(button => !button.Classes.Contains("blockGrip"));
            Assert.True(grip.Bounds.Right <= checkbox.Bounds.Left);
            var point = grip.TranslatePoint(new Point(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); Frame(window);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
            var menu = Assert.IsType<ContextMenu>(editor.Surface.ContextMenu);
            Assert.True(menu.IsOpen);
            var delete = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "删除此块"));
            delete.RaiseEvent(new(Avalonia.Controls.MenuItem.ClickEvent)); menu.Close(); Frame(window);
            Assert.Same(first, Assert.Single(Assert.Single(editor.Session.Root.Content).Content));
            editor.Session.Undo(); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(directory, "task-editing.png"));
            }
        }
        finally { window.Close(); }
    }
}
