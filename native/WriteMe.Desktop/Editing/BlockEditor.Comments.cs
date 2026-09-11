using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed partial class BlockEditor
{
    public event Action<CommentAnchor?>? CommentRequested;
    public event Action<Guid>? CommentInvoked;
    private Guid? _activeComment;
    private (Point Point, DocumentSession Session, long Revision, int Offset)? _commentPress;
    public Guid? ActiveComment => OverlayOwner._activeComment;
    internal NoteComments Comments => NoteComments.For(Session.HistoryOwner.Root);

    public void RequestComment(bool documentOnly = false)
    {
        var editor = ActiveEditor;
        if (!IsEffectivelyEnabled || !editor.IsEffectivelyEnabled || IsAnyComposing || !editor.Session.IsScopeAttached) return;
        editor.ObserveSelection();
        if (!documentOnly && editor.Surface.SelectionLength == 0)
        { editor.OpenParagraphComments(editor.Session.Projection.At(editor.Surface.CaretOffset).Node.Id); return; }
        var anchor = documentOnly ? null : NoteComments.Capture(editor.Session, editor.Surface.SelectionStart, editor.Surface.SelectionLength);
        if (!documentOnly && editor.Surface.SelectionLength > 0 && anchor == null)
        { ShowNotice("请选择正文文字添加批注；整篇评论可从标题旁的评论按钮添加。"); return; }
        editor.Formatting.Dismiss(); editor.References.Dismiss(); editor.DismissCommands();
        OverlayOwner.CommentRequested?.Invoke(anchor);
    }

    public void SelectComment(Guid? id)
    {
        var owner = OverlayOwner;
        if (owner._activeComment == id) return;
        owner._activeComment = id;
        owner.RefreshCommentHighlights();
    }

    private void RefreshCommentHighlights()
    {
        Surface.TextArea.TextView.Redraw(); QueueCommentMarkers();
        foreach (var child in this.GetVisualDescendants().OfType<BlockEditor>()) { child.Surface.TextArea.TextView.Redraw(); child.QueueCommentMarkers(); }
        foreach (var table in this.GetVisualDescendants().OfType<NativeTableView>()) table.RefreshComments();
    }

    internal TextDecoration? CommentDecoration(System.Collections.Immutable.ImmutableArray<NoteMark> marks)
    {
        var ids = NoteComments.Ids(marks);
        var active = ActiveComment is { } selected && ids.Contains(selected);
        if (!active && !ids.Any(id => Comments.Find(id) != null)) return null;
        return new TextDecoration
        {
            Location = TextDecorationLocation.Underline,
            Stroke = PageColor(active ? "#AF812F" : "#CEAC60", active ? "#E7C67E" : "#A98B55"),
            StrokeThickness = active ? 2 : 1, StrokeThicknessUnit = TextDecorationUnit.Pixel,
            StrokeOffset = 2, StrokeOffsetUnit = TextDecorationUnit.Pixel
        };
    }

    private void CommentPointerPressed(PointerPressedEventArgs e)
    {
        _commentPress = null;
        if (e.ClickCount != 1 || e.KeyModifiers != KeyModifiers.None || !e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed
            || !IsEffectivelyEnabled || InputClient.IsComposing || Surface.GetPositionFromPoint(e.GetPosition(Surface)) is not { } position) return;
        var offset = Surface.Document.GetOffset(position.Line, position.Column);
        if (e.GetPosition(Surface.TextArea.TextView).X < TextStart(Session.Projection.At(offset))) return;
        _commentPress = (e.GetPosition(Surface), Session, Session.Revision, offset);
    }

    private void CommentPointerReleased(PointerReleasedEventArgs e)
    {
        var pressed = _commentPress; _commentPress = null;
        if (pressed == null || _dragSource != null) return;
        var delta = pressed.Value.Point - e.GetPosition(Surface);
        if (delta.X * delta.X + delta.Y * delta.Y > 16) return;
        var point = e.GetPosition(Surface.TextArea.TextView);
        Dispatcher.UIThread.Post(() =>
        {
            var target = pressed.Value;
            if (target.Session != Session || target.Revision != Session.Revision || Surface.SelectionLength != 0 || !IsEffectivelyEnabled || InputClient.IsComposing) return;
            var view = Surface.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            var ids = new[] { target.Offset, target.Offset - 1 }.Where(offset => offset >= 0 && offset < Session.Projection.Text.Length)
                .Where(offset => BackgroundGeometryBuilder.GetRectsForSegment(view, new SimpleSegment(offset, 1)).Any(rect => rect.Contains(point)))
                .SelectMany(offset =>
                {
                    var row = Session.Projection.At(offset);
                    return NoteComments.Ids(RichText.Slice(row.Node.Content, offset - row.Start, 1).FirstOrDefault()?.Marks ?? []);
                });
            var id = ids.FirstOrDefault(id => Comments.Find(id) != null);
            if (id == Guid.Empty) return;
            Formatting.Dismiss(); OverlayOwner.CommentInvoked?.Invoke(id);
        }, DispatcherPriority.Input);
    }
}
