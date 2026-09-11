using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed partial class BlockEditor
{
    private readonly Dictionary<Guid, NativeLayoutView> _layouts = [];
    private BlockEditor? _parentEditor;
    private BlockEditor? _activeChild;
    private bool _sizingEmbedded;
    private bool _layoutWidthQueued;
    private bool _appearanceQueued;
    private bool _disposed;
    private bool _detaching;
    public bool IsCompact { get; }
    internal bool IsDragPreview { get; }
    internal double? PreviewInset { get; init; }
    internal int PreviewDepth { get; init; }
    internal bool PreviewQuote { get; init; }
    internal bool IsTableCell { get; set; }
    internal Func<KeyEventArgs, bool>? EmbeddedKey { get; set; }
    public BlockEditor ActiveEditor => _activeChild is { _disposed: false } child && child.Session.IsScopeAttached ? child.ActiveEditor : this;
    public bool IsAnyComposing => ActiveEditor.InputClient.IsComposing;
    private BlockEditor OverlayOwner
    {
        get { var owner = this; while (owner._parentEditor != null) owner = owner._parentEditor; return owner; }
    }

    private void MountOverlay(Control control, bool restore = false)
    {
        if (IsDragPreview) return;
        var host = restore ? _layout : OverlayOwner._layout;
        if (control.Parent == host) return;
        if (restore && _detaching)
        {
            // Avalonia is enumerating the ancestor's children during detach. Reparenting
            // a page overlay here invalidates that enumeration when an embedded editor closes.
            Dispatcher.UIThread.Post(() => { if (!control.IsVisible) MountOverlay(control, restore: true); });
            return;
        }
        if (control.Parent is Panel previous) previous.Children.Remove(control);
        host.Children.Add(control);
    }

    internal double PrefixInset(BlockRow row) => PreviewInset ?? (IsTableCell && row.Depth == 0 && !row.IsToggle && !row.Quote && row.Marker.Length == 0
        && row.Node.Type is "paragraph" or "heading" ? BlockLayout.TextStart(row) : IsCompact ? 24 : 0);
    internal double TextStart(BlockRow row) => BlockLayout.TextStart(row) - PrefixInset(row);

    public void RefreshPageAppearance()
    {
        if (_disposed) return;
        RefreshLayouts();
        Surface.TextArea.TextView.Redraw();
    }

    private void QueuePageAppearance()
    {
        if (_appearanceQueued || _disposed) return;
        _appearanceQueued = true;
        Dispatcher.UIThread.Post(() => { _appearanceQueued = false; RefreshPageAppearance(); }, DispatcherPriority.Background);
    }

    internal void ApplyEmbeddedAppearance(BlockEditor editor)
    {
        editor.PageBackgroundColor = PageBackgroundColor;
        editor.DividerStyle = DividerStyle;
        editor.GhostOpacity = GhostOpacity;
        editor.Surface.FontSize = Math.Max(13, Surface.FontSize - (editor.IsTableCell ? 1 : 0));
        editor.Surface.FontFamily = Surface.FontFamily;
        editor.Surface.Foreground = Surface.Foreground;
        editor.Surface.Options.LineHeightFactor = Surface.Options.LineHeightFactor;
        editor.RefreshPageAppearance();
    }

    private bool OwnsInput(object? source)
    {
        if (source is not Visual visual) return true;
        foreach (var item in new[] { visual }.Concat(visual.GetVisualAncestors()))
        {
            if (item is BlockEditor editor) return ReferenceEquals(editor, this);
            if (item is NativeLayoutView) return false;
        }
        return false;
    }

    internal void Activate()
    {
        _activeChild = null;
        var child = this;
        for (var parent = _parentEditor; parent != null; parent = parent._parentEditor)
        {
            parent._activeChild = child;
            parent.Formatting.Dismiss();
            child = parent;
        }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal BlockEditor CreateEmbedded(Guid container, bool cell = false)
    {
        var editor = new BlockEditor(Session.CreateScope(container), true, IsDragPreview)
        {
            _parentEditor = this, IsTableCell = cell, ResolveAsset = id => ResolveAsset?.Invoke(id),
            CreateReferencedNote = name => CreateReferencedNote?.Invoke(name),
            Height = cell ? 32 : 84, MinHeight = cell ? 30 : 72,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            PageBackgroundColor = PageBackgroundColor, DividerStyle = DividerStyle
        };
        ApplyEmbeddedAppearance(editor);
        editor.SetReferenceCatalogue(ReferenceDocuments, ReferenceTags);
        editor.ReferenceInvoked += reference => ReferenceInvoked?.Invoke(reference);
        editor.AssetInvoked += node => AssetInvoked?.Invoke(node);
        editor.Notice += message => Notice?.Invoke(message);
        editor.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        editor.InputClient.PreeditChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);
        editor.Surface.TextArea.TextView.VisualLinesChanged += (_, _) => editor.QueueEmbeddedSize(cell ? 360 : 720);
        AutomationProperties.SetName(editor.Surface, cell ? "单元格正文" : "分栏正文");
        AutomationProperties.SetAutomationId(editor, "Scope_" + container);
        return editor;
    }

    private void QueueEmbeddedSize(double maximum)
    {
        if (_sizingEmbedded || _disposed) return;
        _sizingEmbedded = true;
        Dispatcher.UIThread.Post(() =>
        {
            _sizingEmbedded = false;
            if (_disposed) return;
            var height = Math.Clamp(Surface.TextArea.TextView.DocumentHeight + 5, MinHeight, maximum);
            if (Math.Abs(Height - height) > .5) Height = height;
        }, DispatcherPriority.Background);
    }

    internal NativeLayoutView LayoutView(NoteNode node, double width)
    {
        if (!_layouts.TryGetValue(node.Id, out var view))
        {
            view = node.Type == "table" ? new NativeTableView(this, node.Id) : new NativeColumnsView(this, node.Id);
            _layouts.Add(node.Id, view);
        }
        view.Update(node, width);
        return view;
    }

    private void RefreshLayouts()
    {
        foreach (var (id, view) in _layouts.ToArray())
        {
            var node = NoteTree.Find(Session.Root, id);
            if (node == null) { view.Dispose(); _layouts.Remove(id); }
            else view.Update(node, view.Width);
        }
        if (_activeChild != null && !_activeChild.Session.IsScopeAttached) _activeChild = null;
    }

    private void ClearLayouts()
    {
        foreach (var view in _layouts.Values) view.Dispose();
        _layouts.Clear();
        _activeChild = null;
    }

    internal void DisposeEmbedded()
    {
        if (_disposed) return;
        _disposed = true;
        ClearLayouts();
        Session.Changed -= SessionChanged;
        Session.Dispose();
        _dragScroll.Stop();
        HideCommands(); CancelDrag();
        InputClient.Cancel();
        Formatting.Reset(); References.Reset();
        ClearAssetCache();
    }

    private bool TryFocusLayoutSelection()
    {
        var point = Session.Selection.Caret;
        if (Session.Projection.LayoutHost(point.NodeId) is not { } host) return false;
        Surface.ScrollToLine(Surface.Document.GetLineByOffset(host.Start).LineNumber);
        Surface.TextArea.TextView.EnsureVisualLines();
        return _layouts.TryGetValue(host.Node.Id, out var view) && view.FocusPoint(point);
    }

    public void NavigateTo(Guid node, int start = 0, int length = 0)
    {
        if (!IsEnabled || IsAnyComposing || !Session.Reveal(node)) return;
        Session.Selection = new(new(node, start), new(node, start + length));
        SyncSurface();
        FocusText();
        var active = ActiveEditor;
        active.SyncSurface();
        active.Surface.ScrollTo(active.Surface.TextArea.Caret.Line, active.Surface.TextArea.Caret.Column);
    }

    internal void SelectLayout(Guid id)
    {
        if (Session.Projection.Find(id) is not { } row) return;
        Session.BreakTypingGroup();
        Session.Selection = Session.Projection.Selection(row.Start, row.End);
        _activeChild = null;
        SyncSurface();
        Surface.TextArea.Focus();
    }

    private bool SelectionHasAtomic() => Session.Projection.Rows.Any(row => row.IsAtomic && row.End > Surface.SelectionStart && row.Start < Surface.SelectionStart + Surface.SelectionLength);

    private void HandleAtomicText(object? sender, TextInputEventArgs e)
    {
        if (!OwnsInput(e.Source) || !IsEnabled || e.Text is not { Length: > 0 } text || Surface.Document.TextLength != Session.Projection.Text.Length) return;
        // TextInput carries committed text. The table range command must see the end of
        // composition before it decides whether a structural replacement is permitted.
        InputClient.SetPreeditText(null);
        foreach (var table in _layouts.Values.OfType<NativeTableView>())
            if (table.HandleRangeText(text)) { e.Handled = true; return; }
        if (SelectionHasAtomic() || Session.Projection.At(Surface.CaretOffset).IsAtomic)
        {
            InputClient.SetPreeditText(null);
            try { Session.Edit(Surface.SelectionStart, Surface.SelectionLength, text, false); }
            catch (InvalidOperationException ex) { ShowNotice(ex.Message); }
            SyncSurface();
            e.Handled = true;
        }
    }

    private bool HandleLayoutKey(KeyEventArgs e)
    {
        if (EmbeddedKey?.Invoke(e) == true) { e.Handled = true; return true; }
        foreach (var table in _layouts.Values.OfType<NativeTableView>())
            if (table.HandleRangeKey(e)) return true;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && (SelectionHasAtomic() || Session.Projection.At(Surface.CaretOffset).IsAtomic))
        {
            if (e.Key is Key.C or Key.X or Key.V) { _ = AtomicClipboardAsync(e.Key); e.Handled = true; return true; }
        }
        if (e.Key is Key.Back or Key.Delete && Surface.SelectionLength > 0 && SelectionHasAtomic())
        {
            var row = Session.Projection.At(Surface.SelectionStart);
            if (row.IsAtomic && Surface.SelectionStart == row.Start && Surface.SelectionLength == row.Text.Length) Session.DeleteBlock(row.Block.Id);
            else
            {
                try { Session.Edit(Surface.SelectionStart, Surface.SelectionLength, "", false); }
                catch (InvalidOperationException ex) { ShowNotice(ex.Message); }
            }
            SyncSurface(); e.Handled = true; return true;
        }
        if (Surface.SelectionLength != 0 || e.KeyModifiers.HasFlag(KeyModifiers.Control)) return false;
        var current = Session.Projection.At(Surface.CaretOffset);
        if (e.Key is Key.Back or Key.Delete && current.IsAtomic)
        {
            SelectLayout(current.Node.Id); e.Handled = true; return true;
        }
        if (e.Key == Key.Delete && Surface.CaretOffset == current.End && current.Index + 1 < Session.Projection.Rows.Length
            && Session.Projection.Rows[current.Index + 1] is { IsAtomic: true } next)
        {
            SelectLayout(next.Node.Id); e.Handled = true; return true;
        }
        return false;
    }

    internal void FocusHistorySelection()
    {
        var root = this;
        while (root._parentEditor != null) root = root._parentEditor;
        root.FocusText();
    }

    private async Task AtomicClipboardAsync(Key key)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard || InputClient.IsComposing || !IsEffectivelyEnabled) return;
        var session = Session; var revision = session.Revision; var selection = session.Selection;
        var start = Surface.SelectionStart; var length = Surface.SelectionLength;
        bool Valid() => !_disposed && IsEffectivelyEnabled && !InputClient.IsComposing && ReferenceEquals(Session, session) && Session.Revision == revision && Session.Selection == selection;
        try
        {
            if (key == Key.V)
            {
                using var data = await clipboard.TryGetDataAsync();
                var text = data == null ? null : await data.TryGetTextAsync();
                if (text != null && Valid()) Session.Edit(start, length, text, false);
            }
            else if (length > 0)
            {
                var parts = Session.Projection.Rows.Where(row => row.Start < start + length && row.End > start).Select(row =>
                {
                    if (row.IsAtomic) return row.Node.Type == "table" && LayoutBlocks.IsEditableTable(row.Node)
                        ? LayoutBlocks.CopyCells(row.Node, new(0, 0, row.Node.Content.Length - 1, row.Node.Content[0].Content.Length - 1)) : DocumentText.Plain(row.Node);
                    var from = Math.Max(0, start - row.Start); var end = Math.Min(row.Text.Length, start + length - row.Start);
                    return row.Text[from..end];
                });
                await clipboard.SetTextAsync(string.Join('\n', parts));
                if (key == Key.X && Valid())
                {
                    var row = Session.Projection.At(start);
                    if (row.IsAtomic && start == row.Start && length == row.Text.Length) Session.DeleteBlock(row.Block.Id);
                    else Session.Edit(start, length, "", false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException) { ShowNotice(ex is InvalidOperationException ? ex.Message : "剪贴板暂不可用，请重试"); }
    }

    private void QueueLayoutWidths()
    {
        if (_layoutWidthQueued || _disposed) return;
        _layoutWidthQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _layoutWidthQueued = false;
            if (_disposed) return;
            var changed = false;
            foreach (var (id, view) in _layouts)
            {
                if (Session.Projection.Find(id) is not { } row) continue;
                var width = Math.Max(100, Surface.TextArea.TextView.Bounds.Width - TextStart(row) - 16);
                if (Math.Abs(view.Width - width) < 1) continue;
                view.Update(row.Node, width); changed = true;
            }
            if (changed) Surface.TextArea.TextView.Redraw();
        }, DispatcherPriority.Background);
    }
}
