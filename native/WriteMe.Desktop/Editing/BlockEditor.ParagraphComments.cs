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
    private readonly Canvas _commentMarkers = new() { HorizontalAlignment = HorizontalAlignment.Right, ZIndex = 12, Background = Brushes.Transparent };
    private readonly Dictionary<Guid, Button> _commentButtons = [];
    private bool _commentMarkersQueued;
    private Guid? _commentBlock;
    internal Guid? CommentBlock => OverlayOwner._commentBlock;
    internal event Action? CommentGeometryChanged;
    public event Action<CommentAnchor>? ParagraphCommentsRequested;

    private void InitializeParagraphComments()
    {
        var width = IsCompact ? 24 : 38;
        _commentMarkers.Width = width;
        Surface.Margin = new(0, 0, width, 0);
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
        if (IsDragPreview || _disposed || _commentMarkersQueued) return;
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
        if (_disposed || IsDragPreview) return;
        var view = Surface.TextArea.TextView;
        if (!view.VisualLinesValid) return;
        var visible = new HashSet<Guid>();
        foreach (var line in view.VisualLines)
        {
            var row = Session.Projection.At(line.FirstDocumentLine.Offset);
            var threads = Comments.InBlock(row.Node.Id);
            var count = threads.Sum(thread => thread.Messages.Count(message => !message.Deleted));
            var selected = CommentBlock == row.Node.Id || threads.Any(thread => thread.Id == ActiveComment);
            if (_dragSource != null || (count == 0 && !selected && HoveredBlockId != row.Block.Id)) continue;
            var y = line.VisualTop - view.ScrollOffset.Y;
            if (y + line.Height < 0 || y > view.Bounds.Height) continue;
            visible.Add(row.Node.Id);
            if (!_commentButtons.TryGetValue(row.Node.Id, out var button))
            {
                var id = row.Node.Id; var session = Session;
                button = new Button { Width = _commentMarkers.Width - 2, Height = 26, MinHeight = 0, MinWidth = 0, Padding = new(0), CornerRadius = new(8), Classes = { "quiet" } };
                button.Click += (_, _) => { if (session == Session) OpenParagraphComments(id); };
                AutomationProperties.SetAutomationId(button, "BlockComment_" + id);
                _commentButtons[id] = button; _commentMarkers.Children.Add(button);
            }
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(new SidebarGlyph(SidebarSymbol.Comment, 15));
            if (count > 0 && !IsCompact) content.Children.Add(new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
            button.Background = selected ? PageColor("#E9EFF7", "#31445D") : count > 0 ? PageColor("#F4F6F9", "#303C4D") : Brushes.Transparent;
            button.Foreground = count > 0 || selected ? PageColor("#58789D", "#B4CCE9") : PageMuted;
            button.IsEnabled = IsEffectivelyEnabled && !IsAnyComposing && Comments.CanEdit;
            var label = count == 0 ? "评论这一段" : $"查看此段的 {count} 条评论和回复";
            AutomationProperties.SetName(button, label); ToolTip.SetTip(button, label);
            var origin = view.TranslatePoint(new(0, Math.Clamp(y + 1, 0, Math.Max(0, view.Bounds.Height - 26))), _commentMarkers) ?? default;
            Canvas.SetLeft(button, 1); Canvas.SetTop(button, origin.Y);
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
            var line = view.VisualLines.FirstOrDefault(item => item.FirstDocumentLine.Offset == row.Start);
            if (line == null) continue;
            var top = line.VisualTop - view.ScrollOffset.Y;
            if (top + line.Height < 0 || top > view.Bounds.Height) continue;
            if (view.TranslatePoint(new(editor.TextStart(row), Math.Max(0, top)), relativeTo) is { } point)
                return new(point, new Size(Math.Max(1, view.Bounds.Width - editor.TextStart(row)), Math.Min(line.Height, view.Bounds.Height - Math.Max(0, top))));
        }
        return null;
    }
}
