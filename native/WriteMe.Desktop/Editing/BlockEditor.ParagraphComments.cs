using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 段落入口和浮层定位跟随可见原生行，见 .agents/notes/implemented/feature/2026-09-11-native-comments.md
public sealed partial class BlockEditor
{
    private readonly Canvas _commentMarkers = new() { HorizontalAlignment = HorizontalAlignment.Stretch, ZIndex = 12 };
    private readonly Dictionary<Guid, Button> _commentButtons = [];
    private bool _commentMarkersQueued;
    private Guid? _commentBlock;
    internal Guid? CommentBlock => OverlayOwner._commentBlock;
    internal event Action? CommentGeometryChanged;
    public event Action<CommentAnchor>? ParagraphCommentsRequested;
    public Func<CommentMessage, double, Control>? CommentAvatarFactory { get; set; }
    internal Func<CommentMessage, double, Control>? ResolvedCommentAvatarFactory => OverlayOwner.CommentAvatarFactory ?? CommentAvatarFactory;
    internal double CommentFooterHeight(Guid node) => Comments.InBlock(node).Any(thread => thread.Messages.Any(message => !message.Deleted)) ? 28 : 0;
    internal void RefreshAccountComments() => RefreshCommentHighlights();

    private void InitializeParagraphComments()
    {
        var width = IsCompact ? 24 : 38;
        Surface.Margin = IsDragPreview ? new(0) : new(0, 0, width, 0);
        _layout.Children.Add(_commentMarkers);
        var view = Surface.TextArea.TextView;
        view.VisualLinesChanged += (_, _) => QueueCommentMarkers();
        view.ScrollOffsetChanged += (_, _) => QueueCommentMarkers();
        SizeChanged += (_, _) => QueueCommentMarkers();
        _commentMarkers.PointerMoved += (_, e) =>
        {
            if (_dragSource != null) return;
            var point = e.GetPosition(view);
            UpdateHover(new(Math.Min(point.X, Math.Max(0, view.Bounds.Width - 1)), point.Y));
        };
        PointerExited += (_, _) => { if (_dragSource == null) SetHoveredBlock(null); };
    }

    private void QueueCommentMarkers()
    {
        if (_disposed || _commentMarkersQueued) return;
        _commentMarkersQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _commentMarkersQueued = false;
            RefreshCommentMarkers();
            OverlayOwner.CommentGeometryChanged?.Invoke();
        }, DispatcherPriority.Render);
    }

    private void RefreshCommentMarkers()
    {
        if (_disposed) return;
        var view = Surface.TextArea.TextView;
        if (!view.VisualLinesValid) return;
        var visible = new HashSet<Guid>();
        foreach (var line in view.VisualLines)
        {
            var row = Session.Projection.At(line.FirstDocumentLine.Offset);
            var threads = Comments.InBlock(row.Node.Id);
            var count = threads.Sum(thread => thread.Messages.Count(message => !message.Deleted));
            if (count > 0 && line.LastDocumentLine.EndOffset < row.End) continue;
            var selected = CommentBlock == row.Node.Id || threads.Any(thread => thread.Id == ActiveComment);
            if (_dragSource != null || (count == 0 && !selected && HoveredBlockId != row.Block.Id)) continue;
            var y = line.VisualTop - view.ScrollOffset.Y;
            if (y + line.Height < 0 || y > view.Bounds.Height) continue;
            visible.Add(row.Node.Id);
            if (!_commentButtons.TryGetValue(row.Node.Id, out var button))
            {
                var id = row.Node.Id; var session = Session;
                button = new Button { Height = 26, MinHeight = 0, MinWidth = 0, Padding = new(0), CornerRadius = new(6), HorizontalContentAlignment = HorizontalAlignment.Left, Classes = { "quiet" } };
                button.Click += (_, _) => { if (session == Session) OpenParagraphComments(id); };
                AutomationProperties.SetAutomationId(button, "BlockComment_" + id);
                _commentButtons[id] = button; _commentMarkers.Children.Add(button);
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            var available = Math.Max(36, view.Bounds.Width - TextStart(row));
            if (count > 0)
            {
                var first = threads.SelectMany(thread => thread.Messages).First(message => !message.Deleted);
                if (available > 115)
                {
                    var factory = ResolvedCommentAvatarFactory;
                    content.Children.Add(factory?.Invoke(first, 19) ?? new Border { Width = 19, Height = 19, CornerRadius = new(10), Background = PageColor("#ECF1FA", "#354157"),
                        Child = new TextBlock { Text = System.Globalization.StringInfo.GetNextTextElement(first.Author), FontSize = 9, Foreground = PageMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
                }
                content.Children.Add(new TextBlock { Text = count + " 条评论", FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
                if (available > 210)
                {
                    var latest = threads.SelectMany(thread => thread.Messages).Where(message => !message.Deleted).Max(message => message.CreatedAt);
                    var minutes = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - latest) / 60000);
                    content.Children.Add(new TextBlock { Text = minutes < 1 ? "刚刚" : minutes < 60 ? minutes + " 分钟前" : minutes < 1440 ? minutes / 60 + " 小时前" : DateTimeOffset.FromUnixTimeMilliseconds(latest).LocalDateTime.ToString("M/d"), FontSize = 9, Foreground = PageMuted, VerticalAlignment = VerticalAlignment.Center });
                }
            }
            else content.Children.Add(new SidebarGlyph(SidebarSymbol.Comment, 15));
            button.Content = content;
            button.Width = count > 0 ? double.NaN : 26; button.MaxWidth = available;
            button.Background = selected ? PageColor("#E9EFF7", "#31445D") : Brushes.Transparent;
            button.Foreground = count > 0 || selected ? PageColor("#58789D", "#B4CCE9") : PageMuted;
            button.IsEnabled = IsEffectivelyEnabled && !IsAnyComposing && Comments.CanEdit;
            var label = count == 0 ? "评论这一段" : $"查看此段的 {count} 条评论和回复";
            AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
            var origin = view.TranslatePoint(new(count > 0 ? TextStart(row) : view.Bounds.Width + 2,
                count > 0 ? y + line.Height - CommentFooterHeight(row.Node.Id) : y + 1), _commentMarkers) ?? default;
            Canvas.SetLeft(button, origin.X); Canvas.SetTop(button, origin.Y);
        }
        foreach (var (id, button) in _commentButtons.ToArray())
            if (!visible.Contains(id)) { _commentMarkers.Children.Remove(button); _commentButtons.Remove(id); }
    }

    public void OpenParagraphComments(Guid nodeId)
    {
        if (!IsEffectivelyEnabled || OverlayOwner.IsAnyComposing || !Session.IsScopeAttached || !Comments.CanEdit) return;
        if (NoteComments.CaptureBlock(Session, nodeId) is not { } anchor) return;
        Activate(); Formatting.Dismiss(); References.Dismiss(); DismissCommands();
        OverlayOwner.ParagraphCommentsRequested?.Invoke(anchor);
    }

    public void SelectCommentBlock(Guid? nodeId)
    {
        var owner = OverlayOwner;
        if (owner._commentBlock == nodeId) return;
        owner._commentBlock = nodeId;
        owner.RefreshCommentHighlights();
    }

    internal Rect? CommentBounds(Guid nodeId, Visual relativeTo)
    {
        foreach (var editor in new[] { this }.Concat(this.GetVisualDescendants().OfType<BlockEditor>()))
        {
            if (editor.Session.Projection.Find(nodeId) is not { } row) continue;
            var view = editor.Surface.TextArea.TextView;
            if (!view.VisualLinesValid) continue;
            var lines = view.VisualLines.Where(item => item.FirstDocumentLine.Offset >= row.Start && item.FirstDocumentLine.Offset <= row.End).ToArray();
            if (lines.Length == 0) continue;
            var top = lines[0].VisualTop - view.ScrollOffset.Y; var bottom = lines[^1].VisualTop + lines[^1].Height - view.ScrollOffset.Y;
            if (bottom < 0 || top > view.Bounds.Height) continue;
            if (view.TranslatePoint(new(editor.TextStart(row), Math.Max(0, top)), relativeTo) is { } point)
                return new(point, new Size(Math.Max(1, view.Bounds.Width - editor.TextStart(row)), Math.Min(bottom, view.Bounds.Height) - Math.Max(0, top)));
        }
        return null;
    }
}
