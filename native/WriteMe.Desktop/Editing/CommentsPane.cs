using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 评论草稿与正文引用分开，提交仍核对会话和原文，见 .agents/notes/implemented/feature/2026-09-11-native-comments.md
public sealed class CommentsPane : Border
{
    private sealed class Draft
    {
        public string Text = "";
        public CommentAnchor? Anchor;
        public CommentThread? Thread;
        public Guid? Message;
        public Guid? ReplyTo;
        public long Used;
    }
    public const double PanelWidth = 324;
    private readonly BlockEditor _editor;
    private readonly Dictionary<(string Document, string Key), Draft> _drafts = [];
    private readonly Dictionary<string, string> _lastDraft = [];
    private readonly Dictionary<Guid, int> _replyLimits = [];
    private readonly StackPanel _list = new() { Spacing = 10, Margin = new(12, 6, 12, 18) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _count = new() { FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heading = new() { Text = "评论", FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly StackPanel _composer;
    private readonly Grid _header;
    private readonly Button _all;
    private readonly Button _new;
    private readonly TextBox _input = new() { Watermark = "输入你的评论…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 66, MaxHeight = 150, FontSize = 13, Padding = new(0), BorderThickness = new(0), Background = Brushes.Transparent };
    private readonly TextBlock _target = new() { FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new(0, 0, 0, 6) };
    private readonly Button _send;
    private readonly Button _document;
    private readonly Button _cancel;
    private readonly Button _undo;
    private DocumentSession? _session;
    private string _documentId = "";
    private string _draftKey = "new";
    private Draft _draft = new();
    private bool _loading;
    private bool _queued;
    private int _limit = 30;
    private Guid? _selected;
    private Guid? _highlightedMessage;
    private (CommentThread Thread, Guid? Message)? _delete;
    private JsonElement _listedData;
    private HashSet<Guid> _listedOrphans = [];
    private bool _hasList;
    private long? _undoRevision;
    private NoteNode? _checkedRoot;
    private CommentAnchor? _checkedAnchor;
    private bool _anchorValid;
    private long _draftSequence;
    private string _contextDraftKey = "new";
    private NoteNode? _contextRoot;
    private CommentAnchor? _resolvedContext;
    private Guid? _contextNode;
    public CommentAnchor? ContextAnchor { get; private set; }
    public bool IsContextual => ContextAnchor != null;
    public int ContextThreadCount { get; private set; }
    internal Guid? ContextNode
    {
        get
        {
            if (_session == null || ContextAnchor == null) return null;
            if (!ReferenceEquals(_contextRoot, _session.Root) || !ReferenceEquals(_resolvedContext, ContextAnchor))
            {
                _contextRoot = _session.Root; _resolvedContext = ContextAnchor;
                _contextNode = NoteComments.Resolve(_contextRoot, _resolvedContext).FirstOrDefault()?.NodeId;
            }
            return _contextNode;
        }
    }
    private string NewDraftKey => IsContextual ? _contextDraftKey : "new";
    public bool IsComposing => _input.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
    public event EventHandler? CloseRequested;
    public event EventHandler? Updated;
    public event EventHandler? OverviewRequested;

    public CommentsPane(BlockEditor editor)
    {
        _editor = editor;
        Width = PanelWidth; IsVisible = false; Background = Ui.Surface;
        BorderBrush = Ui.Line; BorderThickness = new(1); CornerRadius = new(18);
        AutomationProperties.SetAutomationId(this, "CommentsPane"); AutomationProperties.SetName(this, "文档评论");
        var layout = new Grid { RowDefinitions = new("Auto,*,Auto") };
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new(18, 15, 12, 10) };
        _header = header;
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new SidebarGlyph(SidebarSymbol.Comment, 18));
        label.Children.Add(_heading);
        header.Children.Add(label); Grid.SetColumn(_count, 1); _count.Margin = new(9, 0, 0, 0); header.Children.Add(_count);
        var close = Button(new SidebarGlyph(SidebarSymbol.Close, 15), "关闭评论", "CommentsClose", () => { if (!IsComposing) CloseRequested?.Invoke(this, EventArgs.Empty); });
        _new = Button("写评论", "为此段写一条新评论", "ParagraphNewComment", () => { if (!IsComposing) { ChooseDraft(NewDraftKey); FocusComposer(); } });
        _all = Button("全文", "查看整篇文档的评论", "CommentsOverview", () => { if (!IsComposing) OverviewRequested?.Invoke(this, EventArgs.Empty); });
        _new.IsVisible = false; _all.IsVisible = false;
        Grid.SetColumn(_new, 2); header.Children.Add(_new); Grid.SetColumn(_all, 3); header.Children.Add(_all);
        Grid.SetColumn(close, 4); header.Children.Add(close); layout.Children.Add(header);
        _scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); layout.Children.Add(_scroll);

        var composer = new StackPanel { Spacing = 9, Margin = new(14, 12) };
        _composer = composer;
        var targetRow = new Grid { ColumnDefinitions = new("*,Auto") }; targetRow.Children.Add(_target);
        _cancel = Button(new SidebarGlyph(SidebarSymbol.Close, 12), "保留草稿，返回新评论", "CommentCancelMode", () => { if (!IsComposing) ChooseDraft(NewDraftKey); });
        Grid.SetColumn(_cancel, 1); targetRow.Children.Add(_cancel); composer.Children.Add(targetRow);
        _input.Classes.Add("clean"); AutomationProperties.SetAutomationId(_input, "CommentInput"); AutomationProperties.SetName(_input, "评论草稿");
        ScrollViewer.SetVerticalScrollBarVisibility(_input, ScrollBarVisibility.Auto);
        _input.TextChanged += (_, _) => { if (!_loading) _draft.Text = _input.Text ?? ""; UpdateComposer(); if (IsContextual) Updated?.Invoke(this, EventArgs.Empty); };
        composer.Children.Add(_input);
        _document = Button("改为文档评论", "保留草稿并取消原文关联", "CommentAsDocument", () =>
        {
            if (IsComposing) return;
            var text = _draft.Text; _drafts.Remove((_documentId, _draftKey)); ClearContext(); ChooseDraft("new");
            _draft.Anchor = null; _draft.Text = text; LoadText(); SetStatus(null); UpdateComposer(); OverviewRequested?.Invoke(this, EventArgs.Empty);
        });
        _document.HorizontalAlignment = HorizontalAlignment.Left; composer.Children.Add(_document);
        var statusRow = new StackPanel(); statusRow.Children.Add(_status);
        _undo = Button("撤销删除", "恢复刚刚删除的评论", "CommentUndoDelete", () =>
        {
            if (!CanEdit || IsComposing || _session!.Revision != _undoRevision) return;
            _session.Undo(); _undoRevision = null; SetStatus("评论已恢复"); Refresh(true);
        });
        _undo.HorizontalAlignment = HorizontalAlignment.Left; _undo.IsVisible = false; statusRow.Children.Add(_undo); composer.Children.Add(statusRow);
        var footer = new Grid { ColumnDefinitions = new("*,Auto") };
        footer.Children.Add(new TextBlock { Text = "Ctrl+Enter 发送 · Enter 换行", FontSize = 10, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
        _send = Button("发送", "发送评论 · Ctrl+Enter", "CommentSend", Submit); _send.Classes.Add("primary"); Grid.SetColumn(_send, 1); footer.Children.Add(_send); composer.Children.Add(footer);
        var frame = new Border { Child = composer, BorderBrush = Ui.Line, BorderThickness = new(0, 1, 0, 0) };
        Grid.SetRow(frame, 2); layout.Children.Add(frame); Child = layout;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape && IsComposing) { e.Handled = true; return; }
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; Submit(); }
            if (e.Key == Key.Escape) { e.Handled = true; CloseRequested?.Invoke(this, EventArgs.Empty); _editor.FocusText(); }
        }, RoutingStrategies.Tunnel);
        editor.PropertyChanged += (_, e) => { if (e.Property == IsEnabledProperty) UpdateComposer(); };
    }

    private static Button Button(object content, string name, string id, Action action)
    {
        var button = new Button { Content = content, Padding = new(8, 5), MinHeight = 28, FontSize = 11, Classes = { "quiet" } };
        AutomationProperties.SetName(button, name); AutomationProperties.SetAutomationId(button, id); ToolTip.SetTip(button, name);
        button.Click += (_, _) => action(); return button;
    }

    private bool CanEdit => _session != null && _session == _editor.Session.HistoryOwner && _session.IsScopeAttached && _editor.IsEffectivelyEnabled && IsEffectivelyEnabled
        && !_editor.IsAnyComposing && NoteComments.For(_session.Root).CanEdit;
    private bool Current(DocumentSession? expected) => expected != null && expected == _session && CanEdit && IsEffectivelyVisible && !IsComposing;

    public void Bind(string documentId, DocumentSession session)
    {
        session = session.HistoryOwner;
        if (_documentId == documentId && _session == session) { Refresh(); return; }
        if (IsContextual && IsVisible) CloseRequested?.Invoke(this, EventArgs.Empty);
        ClearContext();
        if (_session != null) _session.Changed -= Changed;
        _documentId = documentId; _session = session; _session.Changed += Changed;
        _selected = null; _highlightedMessage = null; _delete = null; _limit = 30; _hasList = false; _undoRevision = null;
        _replyLimits.Clear();
        ChooseDraft(_lastDraft.GetValueOrDefault(documentId, "new")); Refresh(true);
    }

    public void Release() { if (_session != null) _session.Changed -= Changed; _drafts.Clear(); }

    private void Changed(object? sender, EventArgs args)
    {
        if (_queued) return;
        _queued = true; Dispatcher.UIThread.Post(() => { _queued = false; Refresh(); }, DispatcherPriority.Background);
    }

    public void Refresh(bool force = false)
    {
        if (_session == null) return;
        var index = NoteComments.For(_session.Root);
        var scoped = IsContextual ? ContextNode is { } node ? index.InBlock(node) : [] : index.Threads;
        ContextThreadCount = scoped.Length;
        _heading.Text = IsContextual ? "段落评论" : "评论";
        _input.MinHeight = IsContextual ? 32 : 66; _input.MaxHeight = IsContextual ? 100 : 150;
        _composer.Spacing = IsContextual ? 7 : 9; _composer.Margin = IsContextual ? new(14, 10) : new(14, 12);
        _header.Margin = IsContextual ? new(16, 9, 10, 7) : new(18, 15, 12, 10);
        _all.IsVisible = IsContextual; _new.IsVisible = IsContextual && scoped.Length > 0;
        _scroll.IsVisible = !IsContextual || scoped.Length > 0;
        _count.Text = IsContextual || index.Threads.IsEmpty ? "" : $"{index.MessageCount} 条留言";
        var data = _session.Root.Attrs.GetValueOrDefault(NoteComments.Attribute);
        var orphans = index.Threads.Where(thread => thread.Anchored && index.Spans(thread.Id).IsEmpty).Select(thread => thread.Id).ToHashSet();
        if (force || !_hasList || !data.Equals(_listedData) || !_listedOrphans.SetEquals(orphans))
        {
            _hasList = true; _listedData = data; _listedOrphans = orphans;
            var offset = _scroll.Offset; _list.Children.Clear();
            var threads = scoped.OrderByDescending(thread => thread.Messages[0].CreatedAt).ThenBy(thread => thread.Id).ToArray();
            if (index.Error != null) _list.Children.Add(Empty("评论暂不可用", index.Error));
            else if (threads.Length == 0) _list.Children.Add(Empty("把想法留在这里", "在段落右侧点击评论气泡，\n可以留言，也可以回复每一条消息。"));
            foreach (var thread in threads.Take(_limit)) _list.Children.Add(Card(thread, index));
            if (threads.Length > _limit) _list.Children.Add(Button($"显示更多（还有 {threads.Length - _limit} 条）", "显示更多评论", "CommentsMore", () => { _limit += 30; Refresh(true); }));
            _scroll.Offset = offset;
        }
        if (_selected is { } id && index.Find(id) == null) { _selected = null; _editor.SelectComment(null); }
        _undo.IsVisible = _undoRevision == _session.Revision;
        UpdateComposer(); Updated?.Invoke(this, EventArgs.Empty);
    }

    private static Control Empty(string title, string detail)
    {
        var content = new StackPanel { Spacing = 12, Margin = new(8, 32) };
        content.Children.Add(new SidebarGlyph(SidebarSymbol.Comment, 30) { HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Medium, FontSize = 14, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = detail, Foreground = Ui.Muted, FontSize = 12, LineHeight = 21, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        return content;
    }

    private Control Card(CommentThread thread, NoteComments index)
    {
        var session = _session; var expanded = IsContextual || _selected == thread.Id;
        var body = new StackPanel { Spacing = 10 };
        var card = new Border { Child = body, Padding = new(13), CornerRadius = new(12), BorderThickness = new(1),
            BorderBrush = expanded ? Ui.Chrome("#C8D6E9") : Ui.Line, Background = expanded ? Ui.Chrome("#FBFCFD") : Ui.Surface };
        AutomationProperties.SetAutomationId(card, "CommentThread_" + thread.Id);
        if (thread.Anchored)
        {
            var missing = index.Spans(thread.Id).IsEmpty;
            var quote = Button(new TextBlock { Text = (thread.WholeBlock ? "段落 · " : "") + thread.Quote.Replace('\n', ' '), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = expanded ? 3 : 2, TextTrimming = TextTrimming.CharacterEllipsis },
                missing ? "原文已删除" : thread.WholeBlock ? "定位评论段落" : "定位批注原文", "CommentQuote_" + thread.Id, () =>
                {
                    if (!Current(session)) return;
                    ShowThread(thread.Id);
                    if (NoteComments.For(session!.Root).Spans(thread.Id).FirstOrDefault() is { } span)
                    { _editor.NavigateTo(span.NodeId, span.Start, thread.WholeBlock ? 0 : span.Length); _editor.SelectCommentBlock(thread.WholeBlock ? span.NodeId : null); _editor.ActiveEditor.Formatting.Dismiss(); }
                    else SetStatus("原文已删除，讨论和引用仍然保留。");
                });
            quote.HorizontalAlignment = HorizontalAlignment.Stretch; quote.HorizontalContentAlignment = HorizontalAlignment.Left;
            quote.Padding = new(9, 6); quote.Background = Ui.Chrome("#F4F5F7");
            body.Children.Add(quote);
            if (missing) body.Children.Add(new TextBlock { Text = "原文已删除", FontSize = 10, Foreground = Ui.Muted });
        }
        else body.Children.Add(new TextBlock { Text = "文档评论", FontSize = 10, Foreground = Ui.Muted });
        var replyLimit = _replyLimits.GetValueOrDefault(thread.Id, 20);
        var recent = thread.Messages.Skip(Math.Max(1, thread.Messages.Length - replyLimit)).Select(message => message.Id).ToHashSet();
        var messages = expanded ? thread.Messages.Where(message => message.Id == thread.Messages[0].Id || recent.Contains(message.Id) || message.Id == _highlightedMessage) : [thread.Messages[0]];
        var repliesBody = new StackPanel { Spacing = 14 };
        var replyFrame = new Border { Child = repliesBody, Padding = new(10, 11), CornerRadius = new(9), Background = Ui.Chrome("#F4F5F7") };
        var byId = thread.Messages.ToDictionary(message => message.Id);
        foreach (var message in messages)
        {
            var isReply = message.Id != thread.Messages[0].Id;
            var parent = isReply ? byId.GetValueOrDefault(message.ReplyTo ?? thread.Messages[0].Id) : null;
            var messageBody = new StackPanel { Spacing = 6 };
            var messageFrame = new Border { Child = messageBody, Margin = new(isReply && parent?.Id != thread.Messages[0].Id ? 10 : 0, 0, 0, 0),
                CornerRadius = new(5), Background = _highlightedMessage == message.Id ? Ui.Chrome("#E8EEF7") : Brushes.Transparent };
            AutomationProperties.SetAutomationId(messageFrame, "CommentMessage_" + message.Id);
            if (message.Deleted)
            {
                messageBody.Children.Add(new TextBlock { Text = "这条回复已删除", FontSize = 11, Foreground = Ui.Muted, Margin = new(0, 4) });
                repliesBody.Children.Add(messageFrame); continue;
            }
            var head = new Grid { ColumnDefinitions = new("Auto,*,Auto") };
            var avatar = new Border { Width = 24, Height = 24, CornerRadius = new(12), Background = Ui.Chrome("#E8EEF7"), Child = new TextBlock { Text = "我", FontSize = 10, Foreground = Ui.Chrome("#5477A5"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            head.Children.Add(avatar);
            var byline = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Margin = new(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            byline.Children.Add(new TextBlock { Text = message.Author, FontSize = 12, FontWeight = FontWeight.Medium });
            byline.Children.Add(new TextBlock { Text = DateTimeOffset.FromUnixTimeMilliseconds(message.CreatedAt).LocalDateTime.ToString("M/d HH:mm") + (message.EditedAt != null ? " · 已编辑" : ""), FontSize = 9, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(byline, 1); head.Children.Add(byline);
            Button? menuButton = null;
            menuButton = Button(new SidebarGlyph(SidebarSymbol.More, 14), "评论操作", "CommentMenu_" + message.Id, () =>
            {
                if (!Current(session)) return;
                var edit = new MenuItem { Header = "编辑评论" }; edit.Click += (_, _) => { if (Current(session)) BeginEdit(thread, message); };
                var delete = new MenuItem { Header = message.Id == thread.Messages[0].Id ? "删除讨论及回复…" : "删除这条回复…" };
                delete.Click += (_, _) => { if (!Current(session)) return; _delete = (thread, message.Id == thread.Messages[0].Id ? null : message.Id); _selected = thread.Id; Refresh(true); };
                menuButton!.ContextMenu = new ContextMenu { ItemsSource = new[] { edit, delete } };
                menuButton.ContextMenu.Open(menuButton);
            });
            Grid.SetColumn(menuButton, 2); head.Children.Add(menuButton); messageBody.Children.Add(head);
            if (parent != null)
            {
                var target = Button(new TextBlock { Text = $"回复 {parent.Author} · {(parent.Deleted ? "这条回复已删除" : NoteComments.Abbreviate(parent.Text.Replace('\n', ' '), 60))}",
                    FontSize = 10, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis },
                    "查看这条消息回复的原消息", "CommentParent_" + message.Id, () => { if (Current(session)) ShowMessage(thread.Id, parent.Id); });
                target.Padding = new(0); target.MinHeight = 16; target.HorizontalContentAlignment = HorizontalAlignment.Left;
                messageBody.Children.Add(target);
            }
            messageBody.Children.Add(new SelectableTextBlock { Text = expanded ? message.Text : NoteComments.Abbreviate(message.Text, 240), FontSize = 13, LineHeight = 21, TextWrapping = TextWrapping.Wrap });
            var reply = Button("回复", "回复这条消息", isReply ? "CommentReplyMessage_" + message.Id : "CommentReply_" + thread.Id,
                () => { if (Current(session)) BeginReply(thread, message); });
            reply.HorizontalAlignment = HorizontalAlignment.Left; reply.Padding = new(0, 2); reply.MinHeight = 20; reply.Foreground = Ui.Chrome("#5477A5");
            messageBody.Children.Add(reply);
            if (isReply) repliesBody.Children.Add(messageFrame); else body.Children.Add(messageFrame);
            if (expanded && message.Id == thread.Messages[0].Id && thread.Messages.Length > replyLimit + 1)
                body.Children.Add(Button($"查看更早的回复（还有 {thread.Messages.Length - replyLimit - 1} 条）", "显示更早的回复", "CommentEarlier_" + thread.Id,
                    () => { _replyLimits[thread.Id] = replyLimit + 20; Refresh(true); }));
        }
        if (repliesBody.Children.Count > 0) body.Children.Add(replyFrame);
        if (_delete is { } pending && pending.Thread.Id == thread.Id)
        {
            body.Children.Add(new TextBlock { Text = pending.Message == null ? $"删除这条评论及全部 {thread.Messages.Count(message => !message.Deleted) - 1} 条回复？" : "删除这条回复？后续回复会保留。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var confirm = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            confirm.Children.Add(Button("确认删除", "确认删除评论，可撤销", "CommentConfirmDelete", () =>
            {
                if (Run(session, () => { if (pending.Message is { } message) session!.DeleteCommentReply(pending.Thread, message); else session!.DeleteComment(pending.Thread); }, "评论已删除"))
                { _delete = null; _undoRevision = session!.Revision; Refresh(true); }
            }));
            confirm.Children.Add(Button("取消", "取消删除", "CommentCancelDelete", () => { _delete = null; Refresh(true); })); body.Children.Add(confirm);
        }
        else if (!expanded && (thread.Messages.Length > 1 || thread.Messages[0].Text.Length > 240))
            body.Children.Add(Button(thread.Messages.Length > 1 ? $"查看 {thread.Messages.Count(message => !message.Deleted) - 1} 条回复" : "展开评论", "显示完整评论和回复", "CommentExpand_" + thread.Id, () => { if (Current(session)) ShowThread(thread.Id); }));
        return card;
    }

    private void ShowMessage(Guid threadId, Guid messageId)
    {
        if (_session == null || NoteComments.For(_session.Root).Find(threadId) is not { } thread) return;
        var position = Array.FindIndex(thread.Messages.ToArray(), message => message.Id == messageId);
        if (position < 0) return;
        _highlightedMessage = messageId;
        ShowThread(threadId);
        Dispatcher.UIThread.Post(() => _list.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == "CommentMessage_" + messageId)?.BringIntoView());
    }

    internal double PreferredContextHeight(double width)
    {
        var size = new Size(width - 2, double.PositiveInfinity);
        _header.Measure(size); _composer.Measure(size);
        if (_scroll.IsVisible) _list.Measure(size);
        return _header.DesiredSize.Height + _composer.DesiredSize.Height + (_scroll.IsVisible ? _list.DesiredSize.Height : 0) + 8;
    }

    public void ShowThread(Guid id)
    {
        if (_session == null || IsComposing || NoteComments.For(_session.Root).Find(id) is not { } thread) return;
        _selected = id; _delete = null;
        var ordered = NoteComments.For(_session.Root).Threads.OrderByDescending(item => item.Messages[0].CreatedAt).ThenBy(item => item.Id).ToList();
        _limit = Math.Max(_limit, ordered.FindIndex(item => item.Id == id) + 1);
        _editor.SelectComment(id); Refresh(true);
        Dispatcher.UIThread.Post(() => _list.Children.FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == "CommentThread_" + id)?.BringIntoView());
    }

    public void Compose(CommentAnchor? anchor)
    {
        if (!CanEdit || IsComposing) return;
        ChooseDraft("new"); _draft.Anchor = anchor; SetStatus(null); UpdateComposer(); FocusComposer();
    }

    public void ClearContext()
    {
        ContextAnchor = null; _contextDraftKey = "new"; _hasList = false;
    }

    public void ShowParagraph(CommentAnchor anchor)
    {
        if (!CanEdit || IsComposing || !anchor.WholeBlock || NoteComments.Resolve(_session!.Root, anchor).FirstOrDefault() is not { } range) return;
        ContextAnchor = anchor; _selected = null; _delete = null; _limit = 30; _hasList = false;
        var threads = NoteComments.For(_session.Root).InBlock(range.NodeId);
        var threadIds = threads.Select(thread => thread.Id).ToHashSet();
        var last = _drafts.Where(pair => pair.Key.Document == _documentId && (pair.Value.Thread is { } thread && threadIds.Contains(thread.Id)
            || pair.Value.Anchor is { WholeBlock: true } saved && NoteComments.Resolve(_session.Root, saved).FirstOrDefault()?.NodeId == range.NodeId))
            .OrderByDescending(pair => pair.Value.Used).FirstOrDefault().Key.Key;
        _contextDraftKey = _drafts.FirstOrDefault(pair => pair.Key.Document == _documentId && pair.Value.Anchor is { WholeBlock: true } saved
            && NoteComments.Resolve(_session.Root, saved).FirstOrDefault()?.NodeId == range.NodeId).Key.Key ?? "block:" + range.NodeId;
        ChooseDraft(last ?? NewDraftKey);
        _editor.SelectCommentBlock(range.NodeId);
        if (last == null && threads.OrderByDescending(thread => thread.Messages[^1].CreatedAt).FirstOrDefault() is { } latest) BeginReply(latest);
        Refresh(true); FocusComposer();
    }

    public void FocusComposer() { if (CanEdit) { _input.Focus(); _input.CaretIndex = _input.Text?.Length ?? 0; } }

    private void BeginReply(CommentThread thread, CommentMessage? target = null)
    {
        target ??= thread.Messages[0];
        if (target.Deleted) return;
        ShowThread(thread.Id); ChooseDraft($"reply:{thread.Id}:{target.Id}"); _draft.Thread = thread; _draft.Message = null; _draft.ReplyTo = target.Id;
        UpdateComposer(); FocusComposer();
    }

    private void BeginEdit(CommentThread thread, CommentMessage message)
    {
        ShowThread(thread.Id); var key = "edit:" + message.Id; var exists = _drafts.ContainsKey((_documentId, key)); ChooseDraft(key);
        _draft.Thread = thread; _draft.Message = message.Id;
        if (!exists) { _draft.Text = message.Text; LoadText(); }
        UpdateComposer(); FocusComposer();
    }

    private void ChooseDraft(string key)
    {
        _draftKey = key; _lastDraft[_documentId] = key;
        if (!_drafts.TryGetValue((_documentId, key), out var draft)) _drafts[(_documentId, key)] = draft = new();
        draft.Used = ++_draftSequence;
        if (IsContextual && key == NewDraftKey && draft.Thread == null) draft.Anchor = ContextAnchor;
        _draft = draft; LoadText(); SetStatus(null); UpdateComposer();
    }

    private void LoadText()
    {
        _loading = true;
        if (_input.Text != _draft.Text) _input.Text = _draft.Text;
        _loading = false;
    }

    private void UpdateComposer()
    {
        if (_send == null) return;
        _input.IsReadOnly = !CanEdit;
        _send.IsEnabled = CanEdit && !string.IsNullOrWhiteSpace(_draft.Text) && _draft.Text.Length <= NoteComments.MaxMessageLength;
        _send.Content = _draft.Message != null ? "保存" : "发送";
        _cancel.IsVisible = _draftKey != NewDraftKey;
        _document.IsVisible = _draft.Thread == null && _draft.Anchor != null && (!IsContextual || !_anchorValid);
        var replyTarget = _draft.Thread?.Messages.FirstOrDefault(message => message.Id == (_draft.ReplyTo ?? _draft.Thread.Messages[0].Id));
        _target.Text = _draft.Message != null ? "编辑评论" : replyTarget != null ? $"回复 {replyTarget.Author} · {NoteComments.Abbreviate(replyTarget.Text.Replace('\n', ' '), 80)}"
            : _draft.Anchor != null ? (_draft.Anchor.WholeBlock ? "评论段落 · " : "引用 · ") + _draft.Anchor.Quote.Replace('\n', ' ') : "文档评论";
        if (_draft.Anchor != null && _session != null)
        {
            if (!ReferenceEquals(_checkedRoot, _session.Root) || !ReferenceEquals(_checkedAnchor, _draft.Anchor))
            {
                _checkedRoot = _session.Root; _checkedAnchor = _draft.Anchor;
                _anchorValid = !NoteComments.Resolve(_checkedRoot, _checkedAnchor).IsEmpty;
            }
            if (!_anchorValid) _target.Text = _draft.Anchor.WholeBlock ? "原段落已删除 · 草稿已保留" : "引用原文已变化 · 请重新选取或改为文档评论";
            _document.IsVisible = !IsContextual || !_anchorValid;
        }
        if (_draft.Message == null && _draft.Thread is { } replying && _session != null
            && NoteComments.For(_session.Root).Find(replying.Id)?.Messages.FirstOrDefault(message => message.Id == (_draft.ReplyTo ?? replying.Messages[0].Id)) is not { Deleted: false })
        { _send.IsEnabled = false; _target.Text = "要回复的消息已删除 · 草稿已保留"; }
    }

    private void Submit()
    {
        if (!CanEdit || IsComposing) return;
        var session = _session; var draft = _draft; Guid? created = null;
        if (!Run(session, () =>
        {
            if (draft.Message is { } message) session!.EditComment(draft.Thread!, message, draft.Text);
            else if (draft.Thread != null) session!.ReplyComment(draft.Thread, draft.Text, draft.ReplyTo);
            else created = session!.AddComment(draft.Text, draft.Anchor);
        }, draft.Message != null ? "评论已更新" : "评论已添加")) return;
        var selected = created ?? draft.Thread?.Id;
        _drafts.Remove((_documentId, _draftKey)); ChooseDraft(NewDraftKey);
        if (selected is { } id)
        {
            if ((IsContextual || draft.Thread != null) && NoteComments.For(_session!.Root).Find(id) is { } thread)
                BeginReply(thread, thread.Messages.FirstOrDefault(message => message.Id == draft.ReplyTo && !message.Deleted));
            else ShowThread(id);
        }
        SetStatus(IsContextual ? null : draft.Message != null ? "评论已更新" : "评论已添加"); FocusComposer();
    }

    private bool Run(DocumentSession? expected, Action action, string success)
    {
        if (!Current(expected)) return false;
        try { action(); SetStatus(success); Refresh(true); return true; }
        catch (InvalidOperationException ex) { SetStatus(ex.Message); Refresh(); return false; }
    }

    private void SetStatus(string? text) { _status.Text = text; _status.IsVisible = !string.IsNullOrWhiteSpace(text); }
}
