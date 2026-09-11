using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

public sealed partial class MainWindow
{
    private CommentsPane _comments = null!;
    private readonly Grid _commentsHost = new() { ZIndex = 21 };
    private bool _commentPositionQueued;

    private void InitializeCommentsUi()
    {
        _comments = new(_editor) { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 4, 16, 14), ZIndex = 21 };
        _commentsHost.Children.Add(_comments);
        Grid.SetColumn(_commentsHost, 1); Grid.SetRow(_commentsHost, 1); this.FindControl<Grid>("Shell")!.Children.Add(_commentsHost);
        _comments.CloseRequested += (_, _) => CloseComments();
        _comments.Updated += (_, _) => { UpdateCommentButtons(); QueueInlineCommentPosition(); };
        _comments.OverviewRequested += (_, _) => OpenComments();
        _editor.CommentRequested += anchor => { OpenComments(); _comments.Compose(anchor); };
        _editor.CommentInvoked += id => { OpenComments(); _comments.ShowThread(id); };
        _editor.ParagraphCommentsRequested += OpenParagraphComments;
        _editor.CommentGeometryChanged += QueueInlineCommentPosition;
        _commentsHost.SizeChanged += (_, _) => QueueInlineCommentPosition();
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (!_comments.IsVisible || !_comments.IsContextual || _comments.IsComposing) return;
            if (args.Source is Visual visual && (visual == _comments || visual.GetVisualAncestors().Contains(_comments))) return;
            CloseComments();
        }, RoutingStrategies.Tunnel);
        var titleButton = this.FindControl<Button>("TitleCommentButton")!;
        titleButton.Content = new SidebarGlyph(SidebarSymbol.Comment, 19);
        titleButton.Click += (_, _) => _editor.RequestComment(documentOnly: true);
        this.FindControl<Button>("CommentsButton")!.Click += (_, _) =>
        {
            if (_comments.IsVisible && !_comments.IsContextual) CloseComments();
            else { OpenComments(); if (NoteComments.For(_editor.Session.Root).Threads.IsEmpty) _comments.FocusComposer(); }
        };
        Closed += (_, _) => _comments.Release();
    }

    private void OpenComments()
    {
        if (_comments.IsComposing) return;
        if (_overviewVisible) ShowDocumentView();
        _comments.ClearContext(); _editor.SelectCommentBlock(null);
        _comments.Width = CommentsPane.PanelWidth; _comments.Height = double.NaN;
        _comments.HorizontalAlignment = HorizontalAlignment.Right; _comments.VerticalAlignment = VerticalAlignment.Stretch;
        _comments.Margin = new(0, 4, 16, 14); _comments.BoxShadow = default;
        _tools.Close(); _tools.IsVisible = false; _comments.IsVisible = true;
        _comments.Bind(_active.Id, _editor.Session); _comments.Refresh(); UpdateSidebars();
    }

    private void OpenParagraphComments(CommentAnchor anchor)
    {
        if (_comments.IsComposing) return;
        if (_overviewVisible) ShowDocumentView();
        _tools.Close(); _tools.IsVisible = true;
        _comments.Bind(_active.Id, _editor.Session); _comments.IsVisible = true;
        _comments.HorizontalAlignment = HorizontalAlignment.Left; _comments.VerticalAlignment = VerticalAlignment.Top;
        _comments.Margin = new(12); _comments.BoxShadow = Ui.FloatingShadow;
        _comments.ShowParagraph(anchor); UpdateSidebars(); QueueInlineCommentPosition();
    }

    private void QueueInlineCommentPosition()
    {
        if (_commentPositionQueued || _comments?.IsVisible != true || !_comments.IsContextual) return;
        _commentPositionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _commentPositionQueued = false;
            if (!_comments.IsVisible || !_comments.IsContextual || _commentsHost.Bounds.Width <= 0) return;
            if (_comments.ContextNode is not { } nodeId) return; // Keep an orphaned draft visible so it can be recovered.
            if (_editor.CommentBounds(nodeId, _commentsHost) is not { } rect)
            { if (!_comments.IsComposing) CloseComments(); return; }
            var width = Math.Min(352, Math.Max(240, _commentsHost.Bounds.Width - 24));
            var preferred = Math.Clamp(_comments.PreferredContextHeight(width), 104, 500);
            var belowSpace = _commentsHost.Bounds.Height - rect.Bottom - 20;
            var aboveSpace = rect.Top - 20;
            var below = belowSpace >= Math.Min(preferred, 260) || belowSpace >= aboveSpace;
            var room = below ? belowSpace : aboveSpace;
            var height = Math.Min(preferred, Math.Min(Math.Max(180, _commentsHost.Bounds.Height - 24), Math.Max(Math.Min(preferred, 240), room)));
            _comments.Width = width; _comments.Height = height; _comments.MaxHeight = height;
            var top = below ? rect.Bottom + 8 : rect.Top - height - 8;
            _comments.Margin = new(Math.Clamp(rect.X, 12, Math.Max(12, _commentsHost.Bounds.Width - width - 12)),
                Math.Clamp(top, 12, Math.Max(12, _commentsHost.Bounds.Height - height - 12)), 0, 0);
        }, DispatcherPriority.Render);
    }

    private void CloseComments()
    {
        if (_comments == null || _comments.IsComposing) return;
        _comments.IsVisible = false; _editor.SelectComment(null); _editor.SelectCommentBlock(null); _tools.IsVisible = !_overviewVisible;
        UpdateSidebars(); UpdateCommentButtons();
    }

    private void UpdateCommentButtons()
    {
        if (_comments == null) return;
        var comments = NoteComments.For(_editor.Session.Root);
        var button = this.FindControl<Button>("CommentsButton")!;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        content.Children.Add(new SidebarGlyph(SidebarSymbol.Comment, 17));
        if (comments.Threads.Length > 0) content.Children.Add(new TextBlock { Text = comments.MessageCount.ToString(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content; button.Classes.Set("active", _comments.IsVisible);
        AutomationProperties.SetName(button, $"评论 · {comments.MessageCount} 条评论和回复");
        ToolTip.SetTip(button, "查看全部评论 · 评论当前段落或选区 Ctrl+Alt+M");
        this.FindControl<Button>("TitleCommentButton")!.IsEnabled = _editor.IsEnabled;
    }
}
