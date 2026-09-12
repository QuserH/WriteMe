using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class CommentInteractionTests
{
    private static NoteNode Doc(params NoteNode[] nodes) => new("doc") { Content = [.. nodes] };
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static BlockEditor Editor(MainWindow window) => window.GetVisualDescendants().OfType<BlockEditor>().First();
    private static CommentsPane Pane(MainWindow window) => Find<CommentsPane>(window, "CommentsPane");
    private static TextBox Input(MainWindow window) => Find<TextBox>(window, "CommentInput");
    private static void Frame(Window window) { Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); using var second = window.CaptureRenderedFrame(); }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window);
        Assert.True(control.IsEffectivelyVisible); Assert.True(control.Bounds.Width > 0);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    { window.KeyPress(key, modifiers, PhysicalKey.None, ""); window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window); }
    private static async Task Wait(Func<bool> done) { for (var i = 0; i < 220 && !done(); i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); } Assert.True(done()); }
    private static async Task Close(MainWindow window) { window.Close(); await Wait(() => !window.IsVisible); }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path); Frame(window); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(path, name + ".png"));
    }
    private static MainWindow Open(TestDirectory directory, NoteNode root, int width = 1320, int height = 820)
    {
        using (var store = new NoteStore(directory.Path)) store.Create("让想法，慢慢成形", root);
        var window = new MainWindow(directory.Path, false) { Width = width, Height = height }; window.Show(); Frame(window); return window;
    }
    private static void Send(MainWindow window, string text)
    { Input(window).Focus(); window.KeyTextInput(text); Frame(window); Click(window, Find<Button>(window, "CommentSend")); }
    private static void Menu(MainWindow window, Guid message, int index)
    {
        var button = Find<Button>(window, "CommentMenu_" + message); Click(window, button);
        var menu = button.ContextMenu!; var item = menu.ItemsSource!.Cast<MenuItem>().ElementAt(index);
        item.RaiseEvent(new(MenuItem.ClickEvent)); menu.Close(); Frame(window);
    }

    private static Button ParagraphBubble(MainWindow window, BlockEditor editor, Guid nodeId)
    {
        var row = editor.Session.Projection.Find(nodeId)!;
        var view = editor.Surface.TextArea.TextView; view.EnsureVisualLines();
        var line = view.VisualLines.Single(line => line.FirstDocumentLine.Offset == row.Start);
        var point = view.TranslatePoint(new(100, line.VisualTop - view.ScrollOffset.Y + 10), window)!.Value;
        window.MouseMove(point); Frame(window);
        return Find<Button>(editor, "BlockComment_" + nodeId);
    }

    [AvaloniaTheory]
    [InlineData(1.2)]
    [InlineData(2.4)]
    public void ParagraphSummaryReservesOneFooterAfterWrappedAndMultilineTextAndUndoRestoresGeometry(double spacing)
    {
        var text = string.Concat(Enumerable.Repeat("中文段落自动换行也要对齐。", 6)) + "\n逻辑行尾的文字仍然保持原来的光标。";
        var paragraph = NoteNode.Paragraph(text); var tail = NoteNode.Paragraph("下一段正文不能与评论入口重叠。");
        var editor = new BlockEditor(new(Doc(paragraph, tail)));
        editor.Surface.Options.LineHeightFactor = spacing;
        var window = new Window { Width = 570, Height = 640, Content = editor }; window.Show(); editor.FocusText(); Frame(window);
        try
        {
            var view = editor.Surface.TextArea.TextView;
            var row = editor.Session.Projection.Find(paragraph.Id)!;
            var projection = editor.Session.Projection.Text;
            var before = view.VisualLines.Where(line => line.FirstDocumentLine.Offset <= row.End).ToArray();
            Assert.True(before.Sum(line => line.TextLines.Count) > 2);
            var height = before.Sum(line => line.Height); var baselines = before.SelectMany(line => line.TextLines.Select(textLine => line.GetTextLineVisualYPosition(textLine, VisualYPosition.Baseline))).ToArray();
            var nextTop = view.VisualLines.Single(line => line.FirstDocumentLine.Offset == editor.Session.Projection.Find(tail.Id)!.Start).VisualTop;
            editor.Surface.CaretOffset = row.End; Frame(window); var caret = editor.InputClient.CursorRectangle;
            var thread = editor.Session.AddComment("放在整段最后一行下面", NoteComments.CaptureBlock(editor.Session, paragraph.Id)); Frame(window);
            var after = view.VisualLines.Where(line => line.FirstDocumentLine.Offset <= row.End).ToArray();
            Assert.Equal(height + 28, after.Sum(line => line.Height), 2);
            Assert.Equal(baselines, after.SelectMany(line => line.TextLines.Select(textLine => line.GetTextLineVisualYPosition(textLine, VisualYPosition.Baseline))).ToArray());
            Assert.Equal(caret, editor.InputClient.CursorRectangle); Assert.Equal(projection, editor.Session.Projection.Text);
            var bubble = Find<Button>(editor, "BlockComment_" + paragraph.Id); var top = bubble.TranslatePoint(default, view)!.Value.Y;
            Assert.True(top > caret.Bottom); Assert.True(top + bubble.Bounds.Height <= nextTop + 28 + 1);
            Assert.Equal(nextTop + 28, view.VisualLines.Single(line => line.FirstDocumentLine.Offset == editor.Session.Projection.Find(tail.Id)!.Start).VisualTop, 2);
            Assert.Single(NoteComments.For(editor.Session.Root).Threads, item => item.Id == thread);
            editor.Session.Undo(); Frame(window);
            Assert.Equal(height, view.VisualLines.Where(line => line.FirstDocumentLine.Offset <= row.End).Sum(line => line.Height), 2);
            Assert.Equal(caret, editor.InputClient.CursorRectangle);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TitleComposerReplyEditAndDeletionAreUsableAndSavedOnClose()
    {
        using var directory = new TestDirectory(); var window = Open(directory, Doc(NoteNode.Paragraph("为值得反复推敲的想法，留一点讨论的空间。")));
        var editor = Editor(window);
        try
        {
            Click(window, Find<Button>(window, "TitleCommentButton")); Assert.True(Input(window).IsFocused);
            window.KeyTextInput("补充本周的阅读记录"); Key(window, Avalonia.Input.Key.Enter); window.KeyTextInput("并标注来源。");
            Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Control);
            var thread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            Assert.Equal("补充本周的阅读记录\n并标注来源。", thread.Messages[0].Text); Assert.False(thread.Anchored);
            Click(window, Find<Button>(window, "CommentReply_" + thread.Id)); Send(window, "已经补上两篇参考资料。");
            thread = NoteComments.For(editor.Session.Root).Find(thread.Id)!;
            Assert.Equal(2, thread.Messages.Length); var reply = thread.Messages[1];
            Menu(window, reply.Id, 0); Input(window).SelectAll(); window.KeyTextInput("已补上三篇参考资料。");
            Click(window, Find<Button>(window, "CommentSend"));
            Assert.Equal("已补上三篇参考资料。", NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[1].Text);
            Save(window, "comments-document-thread");
            Pane(window).ShowThread(thread.Id); Frame(window);
            Menu(window, reply.Id, 1); Click(window, Find<Button>(window, "CommentCancelDelete"));
            Assert.Equal(2, NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages.Length);
            Menu(window, reply.Id, 1); Click(window, Find<Button>(window, "CommentConfirmDelete"));
            Assert.Single(NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages.Where(message => !message.Deleted));
            Click(window, Find<Button>(window, "CommentUndoDelete")); Assert.Equal(2, NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages.Length);
            Menu(window, thread.Messages[0].Id, 1); Save(window, "comments-delete-confirm");
            Click(window, Find<Button>(window, "CommentConfirmDelete")); Assert.Empty(NoteComments.For(editor.Session.Root).Threads);
            Click(window, Find<Button>(window, "CommentUndoDelete")); Assert.Single(NoteComments.For(editor.Session.Root).Threads);
        }
        finally { await Close(window); }
        using var store = new NoteStore(directory.Path); var saved = NoteJson.Parse(store.Get(store.List()[0].Id).Content);
        Assert.Equal(2, Assert.Single(NoteComments.For(saved).Threads).Messages.Length);
    }

    [AvaloniaFact]
    public async Task SelectionEntryKeepsItsQuoteHighlightsAndOpensOnAStationaryTextClick()
    {
        using var directory = new TestDirectory(); var window = Open(directory, Doc(NoteNode.Paragraph("把零散的想法整理在一起，让文字更清楚。")));
        var editor = Editor(window);
        try
        {
            editor.FocusText(); editor.Surface.Select(1, 6); Frame(window);
            Click(window, Find<Button>(editor.Formatting, "FormatComment")); Send(window, "这段话可以增加一个具体例子。");
            var thread = Assert.Single(NoteComments.For(editor.Session.Root).Threads); Assert.Equal("零散的想法整", thread.Quote);
            var span = Assert.Single(NoteComments.For(editor.Session.Root).Spans(thread.Id));
            Click(window, Find<Button>(window, "CommentQuote_" + thread.Id)); Assert.Equal(thread.Quote, editor.Surface.SelectedText);
            editor.Surface.Select(2, 0); editor.FocusText(); Click(window, Find<Button>(window, "CommentsClose"));
            var view = editor.Surface.TextArea.TextView; view.EnsureVisualLines();
            var rect = BackgroundGeometryBuilder.GetRectsForSegment(view, new SimpleSegment(6, 1)).First();
            var point = view.TranslatePoint(new(rect.Right - 1, rect.Y + Math.Min(10, rect.Height / 2)), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
            Assert.True(Pane(window).IsVisible); Assert.Equal(thread.Id, editor.ActiveComment);
            Assert.Equal(0, editor.Surface.SelectionLength);
            editor.Session.Format(1, 6, NoteMark.With("highlight", "color", "#D9EED1")); Frame(window);
            editor.Session.Format(1, 6, null); Frame(window); Assert.Single(NoteComments.For(editor.Session.Root).Spans(thread.Id));
            editor.Session.Edit(span.Start, span.Length, "", false); Frame(window);
            Assert.Contains(Pane(window).GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "原文已删除");
            Save(window, "comments-missing-source");
            editor.Session.Undo(); Frame(window); Assert.Single(NoteComments.For(editor.Session.Root).Spans(thread.Id));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ComposerProtectsImeKeepsDraftsAcrossCloseAndRejectsChangedAnchors()
    {
        using var directory = new TestDirectory(); var window = Open(directory, Doc(NoteNode.Paragraph("原文需要核对")));
        var editor = Editor(window);
        try
        {
            editor.FocusText(); editor.Surface.Select(0, 2); Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(Input(window).IsFocused); window.KeyTextInput("等待确认的草稿"); Frame(window);
            var presenter = Input(window).GetVisualDescendants().OfType<TextPresenter>().Single(); presenter.PreeditText = "zhongwen";
            Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Control); Key(window, Avalonia.Input.Key.Escape);
            Find<Button>(window, "CommentSend").RaiseEvent(new(Button.ClickEvent)); Frame(window);
            Assert.Empty(NoteComments.For(editor.Session.Root).Threads); Assert.True(Pane(window).IsVisible);
            Assert.Equal("等待确认的草稿", Input(window).Text); presenter.PreeditText = null;
            Click(window, Find<Button>(window, "CommentsClose")); Click(window, Find<Button>(window, "CommentsButton"));
            Assert.Equal("等待确认的草稿", Input(window).Text);
            editor.Session.Edit(0, 0, "更改", false); Frame(window);
            Click(window, Find<Button>(window, "CommentSend")); Assert.Empty(NoteComments.For(editor.Session.Root).Threads);
            Assert.Equal("等待确认的草稿", Input(window).Text);
            Click(window, Find<Button>(window, "CommentAsDocument"));
            editor.IsEnabled = false; Find<Button>(window, "CommentSend").RaiseEvent(new(Button.ClickEvent)); Assert.Empty(NoteComments.For(editor.Session.Root).Threads);
            editor.IsEnabled = true; Frame(window); Click(window, Find<Button>(window, "CommentSend"));
            Assert.False(Assert.Single(NoteComments.For(editor.Session.Root).Threads).Anchored);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DocumentSwitchRetainsDraftAndOldDiscussionActionsCannotChangeTheNewDocument()
    {
        using var directory = new TestDirectory(); using var store = new NoteStore(directory.Path);
        var other = store.Create("另一篇", Doc(NoteNode.Paragraph("另一篇原文"))); await Task.Delay(5);
        var original = store.Create("当前", Doc(NoteNode.Paragraph("当前原文")));
        var window = new MainWindow(directory.Path, false) { Width = 1320 }; window.Show(); Frame(window); var editor = Editor(window);
        try
        {
            Click(window, Find<Button>(window, "TitleCommentButton")); Send(window, "已经保存的评论");
            var thread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            var oldReply = Find<Button>(window, "CommentReply_" + thread.Id);
            Input(window).Focus(); window.KeyTextInput("尚未发送的草稿"); Frame(window);
            Click(window, Find<Button>(window, "ShowSpaceSidebar"));
            var list = window.FindControl<ListBox>("DocumentList")!;
            list.SelectedItem = list.ItemsSource!.Cast<DocumentItem>().Single(item => item.Id == other.Id);
            await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == "另一篇"); Frame(window);
            oldReply.RaiseEvent(new(Button.ClickEvent)); Frame(window); Assert.Empty(NoteComments.For(editor.Session.Root).Threads); Assert.Equal("", Input(window).Text);
            list.SelectedItem = list.ItemsSource!.Cast<DocumentItem>().Single(item => item.Id == original.Id);
            await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == "当前"); Frame(window);
            Assert.Equal("尚未发送的草稿", Input(window).Text); Assert.Single(Assert.Single(NoteComments.For(editor.Session.Root).Threads).Messages);
            Click(window, Find<Button>(window, "CommentSend")); Assert.Equal(2, NoteComments.For(editor.Session.Root).Threads.Length);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task TableColumnAndFoldedQuotesNavigateAndThePaneFitsLightDarkAndNarrowWindows()
    {
        using var directory = new TestDirectory();
        var columns = LayoutBlocks.Columns(); columns = columns with { Content = [columns.Content[0] with { Content = [NoteNode.Paragraph("留心生活中的小事。" )] }, columns.Content[1] with { Content = [NoteNode.Paragraph("散步时，也许就会有新的答案。") ] }] };
        var table = LayoutBlocks.Table(2, 2); var seed = new DocumentSession(Doc(NoteNode.Paragraph("让想法在讨论中，慢慢成形。"), table, columns, NoteNode.Toggle("本周阅读", NoteNode.Paragraph("留意作者的论证与例子。"))));
        seed.PasteTableCells(table.Id, 0, 0, "阅读事项\t下一步\n整理摘录\t补充来源");
        var window = Open(directory, seed.Root, 1460, 900); var editor = Editor(window);
        try
        {
            var paragraph = NoteTree.Descendants(editor.Session.Root).Single(node => node.IsTextBlock && RichText.Plain(node) == "补充来源");
            editor.NavigateTo(paragraph.Id, 0, 4); Frame(window);
            Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt); Send(window, "补上书名和页码，方便再次查阅。");
            var cellThread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            Click(window, Find<Button>(window, "CommentQuote_" + cellThread.Id)); Assert.Equal("补充来源", editor.ActiveEditor.Surface.SelectedText);
            var columnText = NoteTree.Descendants(editor.Session.Root).Single(node => node.IsTextBlock && RichText.Plain(node).StartsWith("散步时"));
            editor.NavigateTo(columnText.Id, 0, 7); Frame(window);
            Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt); Send(window, "把随手记的片段也放进来。" );
            Assert.Equal(2, NoteComments.For(editor.Session.Root).Threads.Length);
            var nested = NoteTree.Descendants(editor.Session.Root).Single(node => node.IsTextBlock && RichText.Plain(node).StartsWith("留意作者"));
            editor.NavigateTo(nested.Id, 0, 7); Frame(window); Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt); Send(window, "这里可以写下自己的不同看法。" );
            var nestedThread = NoteComments.For(editor.Session.Root).Threads.Single(thread => thread.Quote.StartsWith("留意作者"));
            editor.Session.SetAllCollapsed(true); Frame(window); Click(window, Find<Button>(window, "CommentQuote_" + nestedThread.Id));
            Assert.Equal(nested.Id, editor.Session.Selection.Anchor.NodeId); Assert.Contains(editor.Session.Projection.Rows, row => row.Node.Id == nested.Id);
            editor.Surface.ScrollToHome(); Frame(window); Save(window, "comments-light");
            var pane = Pane(window); var paper = window.FindControl<Border>("PageBackground")!;
            Assert.True(paper.TranslatePoint(new(paper.Bounds.Width, 0), window)!.Value.X < pane.TranslatePoint(default, window)!.Value.X);
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(window); Save(window, "comments-dark");
            window.Width = 820; window.Height = 560; Frame(window); Save(window, "comments-narrow");
            var send = Find<Button>(window, "CommentSend");
            Assert.InRange(send.TranslatePoint(default, window)!.Value.Y + send.Bounds.Height, 0, window.Bounds.Height);
            Assert.True(editor.Bounds.Width > 260); Assert.False(window.FindControl<Border>("LibrarySidebar")!.IsVisible);
            Click(window, Find<Button>(window, "CommentsClose")); Assert.False(editor.ActiveComment.HasValue);
        }
        finally { await Close(window); Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
    }

    [AvaloniaFact]
    public async Task DiscussionAndReplyPagingBoundsTheVisibleControlsWithoutLosingHistory()
    {
        using var directory = new TestDirectory(); var seed = new DocumentSession(Doc(NoteNode.Paragraph("讨论记录")));
        var first = seed.AddComment("第一条讨论");
        for (var i = 0; i < 25; i++) seed.ReplyComment(NoteComments.For(seed.Root).Find(first)!, $"第 {i + 1} 条回复");
        var early = NoteComments.For(seed.Root).Find(first)!.Messages[1];
        seed.ReplyComment(NoteComments.For(seed.Root).Find(first)!, "回应很早的一条消息", early.Id);
        var late = NoteComments.For(seed.Root).Find(first)!.Messages[^1];
        var deleted = NoteComments.For(seed.Root).Find(first)!.Messages.Where((_, i) => i is 10 or 14 or 20).Select(message => message.Id).ToArray();
        foreach (var id in deleted) seed.DeleteCommentReply(NoteComments.For(seed.Root).Find(first)!, id);
        var withoutReplies = seed.AddComment("只剩首评的讨论");
        seed.ReplyComment(NoteComments.For(seed.Root).Find(withoutReplies)!, "随后删除的唯一回复");
        var emptyThread = NoteComments.For(seed.Root).Find(withoutReplies)!; seed.DeleteCommentReply(emptyThread, emptyThread.Messages[1].Id);
        for (var i = 0; i < 33; i++) seed.AddComment($"另一个话题 {i + 1}");
        var window = Open(directory, seed.Root);
        try
        {
            Click(window, Find<Button>(window, "CommentsButton"));
            Assert.Equal(30, Pane(window).GetVisualDescendants().OfType<Border>().Count(border => AutomationProperties.GetAutomationId(border)?.StartsWith("CommentThread_") == true));
            Click(window, Find<Button>(window, "CommentsMore"));
            Assert.Equal(35, Pane(window).GetVisualDescendants().OfType<Border>().Count(border => AutomationProperties.GetAutomationId(border)?.StartsWith("CommentThread_") == true));
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "CommentExpand_" + withoutReplies);
            Pane(window).ShowThread(first); Frame(window);
            Assert.Equal(21, Find<Border>(window, "CommentThread_" + first).GetVisualDescendants().OfType<SelectableTextBlock>().Count());
            Assert.Equal("查看更早的回复（还有 3 条）", Find<Button>(window, "CommentEarlier_" + first).Content);
            Assert.All(deleted, id => Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Border>(), border => AutomationProperties.GetAutomationId(border) == "CommentMessage_" + id));
            Click(window, Find<Button>(window, "CommentParent_" + late.Id));
            Assert.Equal(22, Find<Border>(window, "CommentThread_" + first).GetVisualDescendants().OfType<SelectableTextBlock>().Count());
            Assert.True(Find<Border>(window, "CommentMessage_" + early.Id).IsEffectivelyVisible);
            Assert.Equal("查看更早的回复（还有 2 条）", Find<Button>(window, "CommentEarlier_" + first).Content);
            Click(window, Find<Button>(window, "CommentEarlier_" + first));
            Assert.Equal(24, Find<Border>(window, "CommentThread_" + first).GetVisualDescendants().OfType<SelectableTextBlock>().Count());
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "CommentEarlier_" + first);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ParagraphGutterOpensAnInlineThreadAndRepliesStayWithTheChosenParagraph()
    {
        using var directory = new TestDirectory();
        var window = Open(directory, Doc(NoteNode.Paragraph("把日常观察写下来，再用讨论把它慢慢想清楚。"), NoteNode.Paragraph("另一段有自己的话题，不会混到上一段的回复里。")), 1460, 900);
        var editor = Editor(window); var first = editor.Session.Root.Content[0]; var second = editor.Session.Root.Content[1];
        try
        {
            var width = editor.Bounds.Width;
            Click(window, ParagraphBubble(window, editor, first.Id));
            Assert.True(Pane(window).IsContextual); Assert.True(Pane(window).IsVisible); Assert.True(Input(window).IsFocused);
            Assert.Equal(0, editor.Surface.SelectionLength); Assert.Equal(width, editor.Bounds.Width);
            Save(window, "comments-paragraph-empty"); Send(window, "这一段还可以补一个生活中的例子。");
            var thread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            Assert.True(thread.WholeBlock); Assert.Equal(first.Id, Assert.Single(NoteComments.For(editor.Session.Root).Spans(thread.Id)).NodeId);
            Send(window, "我准备补充昨天散步时的观察。");
            Assert.Equal(2, NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages.Length);
            Assert.Single(NoteComments.For(editor.Session.Root).Threads); Save(window, "comments-paragraph-thread");
            Click(window, Find<Button>(window, "CommentsClose"));
            Click(window, ParagraphBubble(window, editor, second.Id)); Send(window, "这是第二段的独立讨论。");
            var other = NoteComments.For(editor.Session.Root).Threads.Single(item => item.Id != thread.Id);
            Assert.Equal(second.Id, Assert.Single(NoteComments.For(editor.Session.Root).Spans(other.Id)).NodeId);
            Click(window, Find<Button>(window, "CommentsClose")); Click(window, ParagraphBubble(window, editor, first.Id));
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<SelectableTextBlock>(), block => block.Text == "这是第二段的独立讨论。");
            Assert.Contains(Pane(window).GetVisualDescendants().OfType<SelectableTextBlock>(), block => block.Text == "我准备补充昨天散步时的观察。");
            Click(window, Find<Button>(window, "CommentsOverview")); Assert.False(Pane(window).IsContextual);
            Assert.Equal(2, Pane(window).GetVisualDescendants().OfType<Border>().Count(border => AutomationProperties.GetAutomationId(border)?.StartsWith("CommentThread_") == true));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task EmptyParagraphKeyboardCommentsProtectImeAndKeepSeparateParagraphDrafts()
    {
        using var directory = new TestDirectory(); var window = Open(directory, Doc(NoteNode.Paragraph(), NoteNode.Paragraph("第二段")));
        var editor = Editor(window); var first = editor.Session.Root.Content[0]; var second = editor.Session.Root.Content[1];
        try
        {
            editor.NavigateTo(first.Id); Frame(window); Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(Pane(window).IsContextual); window.KeyTextInput("空白段落也有评论草稿"); Frame(window);
            var presenter = Input(window).GetVisualDescendants().OfType<TextPresenter>().Single(); presenter.PreeditText = "zhongwen";
            Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Control); Key(window, Avalonia.Input.Key.Escape);
            Assert.Empty(NoteComments.For(editor.Session.Root).Threads); Assert.True(Pane(window).IsVisible);
            presenter.PreeditText = null; Click(window, Find<Button>(window, "CommentsClose"));
            Click(window, ParagraphBubble(window, editor, second.Id)); Assert.Equal("", Input(window).Text); window.KeyTextInput("第二段的草稿"); Frame(window);
            Click(window, Find<Button>(window, "CommentsClose")); Click(window, ParagraphBubble(window, editor, first.Id));
            Assert.Equal("空白段落也有评论草稿", Input(window).Text); Click(window, Find<Button>(window, "CommentSend"));
            var id = Assert.Single(NoteComments.For(editor.Session.Root).Threads).Id;
            Assert.Equal(0, Assert.Single(NoteComments.For(editor.Session.Root).Spans(id)).Length);
            Click(window, Find<Button>(window, "CommentsClose")); Click(window, ParagraphBubble(window, editor, second.Id));
            Assert.Equal("第二段的草稿", Input(window).Text);
            editor.Session.DeleteBlock(second.Id); Frame(window); Click(window, Find<Button>(window, "CommentSend"));
            Assert.Single(NoteComments.For(editor.Session.Root).Threads); Assert.Equal("第二段的草稿", Input(window).Text);
            Assert.Contains(Pane(window).GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("原段落已删除") == true);
            editor.Session.Undo(); Frame(window); Click(window, Find<Button>(window, "CommentSend"));
            Assert.Equal(2, NoteComments.For(editor.Session.Root).Threads.Length);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task ParagraphMenuUsesItsBlockAndRejectsCallbacksFromTheOldTree()
    {
        using var directory = new TestDirectory(); var window = Open(directory, Doc(NoteNode.Paragraph("点击手柄也可以评论整段")));
        var editor = Editor(window); var paragraph = editor.Session.Root.Content[0];
        try
        {
            ParagraphBubble(window, editor, paragraph.Id);
            var grip = editor.Surface.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("blockGrip"));
            Click(window, grip);
            var menu = editor.Surface.ContextMenu!;
            var comment = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "BlockMenuComment");
            comment.RaiseEvent(new(MenuItem.ClickEvent)); menu.Close(); Frame(window);
            Assert.True(Pane(window).IsContextual); Assert.True(Input(window).IsFocused); Send(window, "来自块菜单的评论");
            Assert.Equal(paragraph.Id, Assert.Single(NoteComments.For(editor.Session.Root).Spans(Assert.Single(NoteComments.For(editor.Session.Root).Threads).Id)).NodeId);
            Click(window, Find<Button>(window, "CommentsClose")); editor.Session.Edit(0, 0, "更改", false); Frame(window);
            comment.RaiseEvent(new(MenuItem.ClickEvent)); Frame(window); Assert.False(Pane(window).IsVisible);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task CellAndColumnParagraphPopupsEscapeCellBoundsAndFitDarkAndNarrowWindows()
    {
        using var directory = new TestDirectory();
        var table = LayoutBlocks.Table(1, 3); var columns = LayoutBlocks.Columns();
        var window = Open(directory, Doc(NoteNode.Paragraph("每一段，都可以展开自己的讨论。"), table, columns), 1460, 900);
        var editor = Editor(window);
        try
        {
            table = editor.Session.Root.Content[1]; columns = editor.Session.Root.Content[2];
            var cell = table.Content[0].Content[0].Content[0];
            editor.NavigateTo(cell.Id); Frame(window); Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(Pane(window).IsContextual); Assert.True(Pane(window).Bounds.Width > editor.ActiveEditor.Bounds.Width);
            Send(window, "这一格可以填写阅读进度。"); Send(window, "先记录到第三章。");
            var cellThread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            Assert.Equal(cell.Id, Assert.Single(NoteComments.For(editor.Session.Root).Spans(cellThread.Id)).NodeId); Save(window, "comments-paragraph-table");
            Click(window, Find<Button>(window, "CommentsClose"));
            var column = columns.Content[1].Content[0]; editor.NavigateTo(column.Id); Frame(window);
            Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt); Send(window, "右边这一栏的讨论。");
            Assert.Equal(2, NoteComments.For(editor.Session.Root).Threads.Length);
            Save(window, "comments-paragraph-column");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(window); Save(window, "comments-paragraph-dark");
            window.Width = 820; window.Height = 560; Frame(window);
            if (!Pane(window).IsVisible) { editor.NavigateTo(column.Id); Frame(window); Key(window, Avalonia.Input.Key.M, RawInputModifiers.Control | RawInputModifiers.Alt); }
            Save(window, "comments-paragraph-narrow");
            var pane = Pane(window); var origin = pane.TranslatePoint(default, window)!.Value;
            Assert.True(pane.IsVisible); Assert.InRange(origin.X, 0, window.Bounds.Width - pane.Bounds.Width + 1);
            Assert.InRange(origin.Y, 0, window.Bounds.Height - pane.Bounds.Height + 1);
            var send = Find<Button>(window, "CommentSend"); Assert.True(send.TranslatePoint(new(0, send.Bounds.Height), window)!.Value.Y <= window.Bounds.Height);
        }
        finally { await Close(window); Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
    }

    [AvaloniaFact]
    public async Task EveryReplyCanBeRepliedToAndDeletingAnIntermediateMessageKeepsItsReplies()
    {
        using var directory = new TestDirectory();
        var window = Open(directory, Doc(NoteNode.Paragraph("像帖子一样，一段文字下面可以一直聊下去。")), 1460, 900);
        var editor = Editor(window); var paragraph = editor.Session.Root.Content[0];
        try
        {
            Click(window, ParagraphBubble(window, editor, paragraph.Id)); Send(window, "这段写得有点笼统，可以举一个例子吗？");
            var thread = Assert.Single(NoteComments.For(editor.Session.Root).Threads);
            Click(window, Find<Button>(window, "CommentReply_" + thread.Id)); Send(window, "我想补充昨天散步时观察到的一件小事。");
            var reply = NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[1];
            Click(window, Find<Button>(window, "CommentReplyMessage_" + reply.Id)); Send(window, "那可以把当时的感受也写进去。");
            var nested = NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[2];
            Assert.Equal(reply.Id, nested.ReplyTo);
            Click(window, Find<Button>(window, "CommentReplyMessage_" + nested.Id)); window.KeyTextInput("我再想想具体怎么写。"); Frame(window);
            Click(window, Find<Button>(window, "CommentsClose")); Click(window, ParagraphBubble(window, editor, paragraph.Id));
            Assert.Equal("我再想想具体怎么写。", Input(window).Text);
            Click(window, Find<Button>(window, "CommentSend"));
            var last = NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[3]; Assert.Equal(nested.Id, last.ReplyTo);
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button)?.StartsWith("CommentResolve_") == true
                || AutomationProperties.GetAutomationId(button)?.StartsWith("CommentsFilter_") == true);
            Click(window, Find<Button>(window, "CommentParent_" + nested.Id));
            Assert.True(Find<Border>(window, "CommentMessage_" + reply.Id).IsEffectivelyVisible);
            Save(window, "comments-paragraph-replies");
            Menu(window, reply.Id, 1); Click(window, Find<Button>(window, "CommentConfirmDelete"));
            var current = NoteComments.For(editor.Session.Root).Find(thread.Id)!;
            Assert.True(current.Messages[1].Deleted); Assert.Equal(reply.Id, current.Messages[2].ReplyTo);
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Border>(), border => AutomationProperties.GetAutomationId(border) == "CommentMessage_" + reply.Id);
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == "CommentParent_" + nested.Id);
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("这条回复已删除") == true);
            Assert.Equal(3, Find<Border>(window, "CommentThread_" + thread.Id).GetVisualDescendants().OfType<SelectableTextBlock>().Count());
            Assert.True(Find<Button>(window, "CommentParent_" + last.Id).IsEffectivelyVisible);
            Save(window, "comments-reply-deleted");
            Click(window, Find<Button>(window, "CommentUndoDelete")); Assert.False(NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[1].Deleted);
            Assert.True(Find<Border>(window, "CommentMessage_" + reply.Id).IsEffectivelyVisible);
            Assert.True(Find<Button>(window, "CommentParent_" + nested.Id).IsEffectivelyVisible);
            editor.Session.Redo(); Frame(window);
            Assert.DoesNotContain(Pane(window).GetVisualDescendants().OfType<Border>(), border => AutomationProperties.GetAutomationId(border) == "CommentMessage_" + reply.Id);
            Click(window, Find<Button>(window, "CommentReplyMessage_" + last.Id)); Send(window, "前面的消息删除后，讨论仍可以继续。");
            Assert.Equal(last.Id, NoteComments.For(editor.Session.Root).Find(thread.Id)!.Messages[^1].ReplyTo);
        }
        finally { await Close(window); }
        using var store = new NoteStore(directory.Path);
        var saved = NoteComments.For(NoteJson.ParseStrict(store.Get(store.List()[0].Id).Content));
        var messages = Assert.Single(saved.Threads).Messages;
        Assert.Equal(messages[1].Id, messages[2].ReplyTo); Assert.Equal(messages[2].Id, messages[3].ReplyTo);
        Assert.True(messages[1].Deleted); Assert.Equal(messages[3].Id, messages[4].ReplyTo);
    }
}
