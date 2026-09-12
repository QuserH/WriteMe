using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed record BlockCommand(string Name, string Kind, string Icon, string Aliases, int Level = 1)
{
    public static readonly BlockCommand[] All =
    [
        new("正文", "paragraph", "T", "text paragraph zhengwen"),
        new("一级标题", "heading", "H₁", "h1 heading biaoti", 1),
        new("二级标题", "heading", "H₂", "h2 heading biaoti", 2),
        new("三级标题", "heading", "H₃", "h3 heading biaoti", 3),
        new("折叠块", "toggleBlock", "▸", "toggle zhedie 折叠"),
        new("无序列表", "bulletList", "•", "bullet list wuxu"),
        new("有序列表", "orderedList", "1.", "ordered numbered list youxu"),
        new("待办事项", "taskList", "☐", "todo task daiban"),
        new("引用", "blockquote", "❞", "quote yinyong"),
        new("代码块", "codeBlock", "</>", "code daima"),
        new("分割线", "horizontalRule", "—", "divider rule fengexian"),
        new("表格", "table", "▦", "table grid biaoge"),
        new("两栏布局", "columnList", "Ⅱ", "columns layout fenlan lianglan", 2),
        new("三栏布局", "columnList", "Ⅲ", "columns layout fenlan sanlan", 3)
    ];
    public static BlockCommand[] Search(string query) => All.Where(c => (c.Name + " " + c.Aliases).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
}

public sealed partial class BlockEditor : UserControl
{
    public TextEditor Surface { get; } = new();
    public DocumentSession Session { get; private set; }
    public NativeInputClient InputClient { get; }
    public FormattingToolbar Formatting { get; }
    public ReferenceCompletion References { get; }
    public IReadOnlyList<DocumentInfo> ReferenceDocuments { get; private set; } = [];
    public IReadOnlyList<TagInfo> ReferenceTags { get; private set; } = [];
    public Func<string, DocumentInfo?>? CreateReferencedNote { get; set; }
    public event Action<ReferenceSpan>? ReferenceInvoked;
    public Func<string, string?>? ResolveAsset { get; set; }
    public event Action<NoteNode>? AssetInvoked;
    private readonly Dictionary<string, Bitmap> _assetImages = [];
    private BlockDragPreview? _ghostPreview;
    private IReadOnlyList<BlockRow> _dragRows = [];
    private int _dragDepth;
    private Vector _ghostGrabOffset;
    private readonly Border _ghost = new() { IsVisible = false, IsHitTestVisible = false, Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(12), Padding = new(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, ZIndex = 60 };
    // Note: 设置滑块即时控制实际指针预览，偏好只留本机 — 见 .agents/notes/implemented/feature/2026-09-09-settings-ghost-opacity.md
    public double GhostOpacity
    {
        get => _ghost.Opacity;
        set { var opacity = Math.Clamp(value, .4, 1); if (_ghost.Opacity == opacity) return; _ghost.Opacity = opacity; QueuePageAppearance(); }
    }
    private Color? _pageBackgroundColor;
    private string _dividerStyle = "line";
    public Color? PageBackgroundColor { get => _pageBackgroundColor; set { if (_pageBackgroundColor == value) return; _pageBackgroundColor = value; QueuePageAppearance(); } }
    public string DividerStyle { get => _dividerStyle; set { if (_dividerStyle == value) return; _dividerStyle = value; QueuePageAppearance(); } }
    internal bool IsDarkPage
    {
        get { var color = PageBackgroundColor ?? Ui.Surface.Color; return color.R * .299 + color.G * .587 + color.B * .114 < 140; }
    }
    internal IBrush PageColor(string light, string dark) => Brush.Parse(IsDarkPage ? dark : light);
    internal IBrush PageMuted => PageColor("#7B828B", "#A5AFBE");
    internal IBrush PageLine => PageColor("#E4E8EC", "#3E4856");
    private HashSet<string>? _referenceIds;
    public event EventHandler? SelectionChanged;
    public event Action<string>? Notice;
    internal bool IsInteracting => _pointerSelecting || _dragSource != null || _commands.IsVisible || References?.IsVisible == true;
    internal BlockDropTarget? DropTarget { get; private set; }
    internal Guid? HoveredBlockId { get; private set; }
    private readonly Grid _layout = new();
    private readonly Border _preedit;
    private readonly TextBlock _preeditText = new() { FontSize = 15, Foreground = Ui.Ink, TextDecorations = TextDecorations.Underline };
    private readonly DispatcherTimer _dragScroll = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private bool _syncing;
    private bool _editing;
    private bool _queuedSync;
    private bool _pointerSelecting;
    private ScrollViewer? _scrollViewer;
    private Guid? _dragSource;
    private Point _dragStart;
    private Point _dragPointer;
    private bool _dragging;
    private IPointer? _capturedPointer;

    public BlockEditor(DocumentSession session, bool compact = false, bool preview = false)
    {
        Session = session;
        IsCompact = compact;
        IsDragPreview = preview;
        if (preview) { IsHitTestVisible = false; Focusable = false; Surface.IsReadOnly = true; Surface.TextArea.Focusable = false; }
        AutomationProperties.SetName(Surface, "笔记正文");
        AutomationProperties.SetName(Surface.TextArea, "笔记正文");
        Surface.Background = Brushes.Transparent;
        Surface.Classes.Add("noteSurface");
        Surface.Foreground = Ui.Ink;
        Surface.FontFamily = new("Microsoft YaHei UI, Segoe UI, Noto Sans CJK SC, sans-serif");
        Surface.FontSize = 15;
        Surface.WordWrap = true;
        Surface.ShowLineNumbers = false;
        Surface.BorderThickness = new(0);
        Surface.Padding = new(0);
        Surface.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Surface.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Surface.Options.EnableHyperlinks = false;
        Surface.Options.EnableEmailHyperlinks = false;
        // A plaintext drag would bypass the block tree and lose the toggle wrapper.
        // All block movement goes through the captured handle + DocumentSession.Move transaction.
        Surface.Options.EnableTextDragDrop = false;
        Surface.Options.AllowScrollBelowDocument = !compact;
        Surface.Options.EnableVirtualSpace = false;
        Surface.Options.HighlightCurrentLine = false;
        Surface.TemplateApplied += (_, e) => _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer");
        Surface.Options.LineHeightFactor = 1.2;
        Surface.Options.InheritWordWrapIndentation = false;
        Surface.Options.ShowBoxForControlCharacters = false;
        Surface.TextArea.SelectionBrush = Ui.Chrome("#CBDDEA");
        Surface.TextArea.SelectionBorder = null;
        Surface.Document.UndoStack.SizeLimit = 0;
        Surface.TextArea.ReadOnlySectionProvider = new AtomicReadOnlyProvider(this);
        Surface.TextArea.TextView.ElementGenerators.Insert(0, new BlockPrefixGenerator(this));
        Surface.TextArea.TextView.ElementGenerators.Insert(1, new AssetElementGenerator(this));
        Surface.TextArea.TextView.ElementGenerators.Insert(2, new LayoutElementGenerator(this));
        Surface.TextArea.TextView.SizeChanged += (_, _) => QueueLayoutWidths();
        PropertyChanged += (_, e) => { if (e.Property == ThemeVariantScope.ActualThemeVariantProperty) QueuePageAppearance(); };
        Surface.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextEditor.FontSizeProperty || e.Property == TextEditor.FontFamilyProperty || e.Property == TextEditor.ForegroundProperty) QueuePageAppearance();
        };
        Surface.Options.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Surface.Options.LineHeightFactor)) QueuePageAppearance(); };
        BlockTextFormatter.Attach(Surface.TextArea.TextView, this);
        Surface.TextArea.TextView.LineTransformers.Add(new BlockStyleTransformer(this));
        Surface.TextArea.TextView.BackgroundRenderers.Add(new BlockBackgroundRenderer(this));
        _layout.Children.Add(Surface);
        if (!preview) InitializeCommands();
        _preedit = new Border { Child = _preeditText, Background = Ui.Surface, IsVisible = false, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        _layout.Children.Add(_preedit);
        Content = _layout;

        InputClient = new(Surface);
        InitializeParagraphComments();
        if (!preview) BlockCaretGeometry.Attach(Surface);
        Formatting = new(this);
        if (!preview) _layout.Children.Add(Formatting);
        References = new(this);
        if (!preview) _layout.Children.Add(References);
        _ghost.Opacity = .85;
        _ghost.BoxShadow = Ui.FloatingShadow;
        if (!preview) { AutomationProperties.SetAutomationId(_ghost, "DragGhost"); _layout.Children.Add(_ghost); }
        Surface.TextArea.AddHandler(InputElement.TextInputMethodClientRequestedEvent, (_, e) => { if (!OwnsInput(e.Source)) return; e.Client = InputClient; e.Handled = true; }, RoutingStrategies.Bubble, handledEventsToo: true);
        InputClient.PreeditChanged += (_, _) => UpdatePreedit();
        Surface.TextArea.TextView.VisualLinesChanged += (_, _) => { if (InputClient.IsComposing) UpdatePreedit(); };
        Surface.TextArea.TextView.ScrollOffsetChanged += (_, _) => { if (InputClient.IsComposing) UpdatePreedit(); };
        Surface.TextArea.GotFocus += (_, e) => { if (OwnsInput(e.Source)) Activate(); };
        Surface.AddHandler(TextInputEvent, HandleAtomicText, RoutingStrategies.Tunnel);
        Surface.TextArea.TextEntered += (_, _) => { InputClient.SetPreeditText(null); UpdateCommands(); };
        Surface.Document.Changed += DocumentChanged;
        Surface.TextArea.SelectionChanged += (_, _) => ObserveSelection();
        Surface.TextArea.Caret.PositionChanged += (_, _) => { ObserveSelection(); UpdateCommands(); };
        Surface.AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
        Surface.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!OwnsInput(e.Source)) return;
            foreach (var table in _layouts.Values.OfType<NativeTableView>()) table.ClearRangeSelection();
            Activate();
            CommentPointerPressed(e);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed
                && e.GetPosition(Surface.TextArea.TextView).X >= TextStart(Session.Projection.At(Surface.CaretOffset))
                && Surface.GetPositionFromPoint(e.GetPosition(Surface)) is { } position
                && ActivateReference(Surface.Document.GetOffset(position.Line, position.Column)))
            { e.Handled = true; return; }
            if (!_syncing) Session.BreakTypingGroup();
            _pointerSelecting = e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed;
            Formatting.Reset();
            DismissCommands();
            References.Dismiss();
        }, RoutingStrategies.Tunnel);
        Surface.AddHandler(PointerMovedEvent, DragMoved, RoutingStrategies.Tunnel);
        Surface.AddHandler(PointerReleasedEvent, (sender, e) =>
        {
            if (!OwnsInput(e.Source) && _dragSource == null) return;
            CommentPointerReleased(e);
            DragReleased(sender, e);
            _pointerSelecting = false;
            Formatting.QueueRefresh();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Surface.PointerCaptureLost += (_, _) => { CancelDrag(); _pointerSelecting = false; Formatting.QueueRefresh(); };
        Surface.PointerExited += (_, e) => { if (_dragSource == null && !new Rect(Bounds.Size).Contains(e.GetPosition(this))) SetHoveredBlock(null); };
        Surface.LostFocus += (_, _) => { if (!_commands.IsPointerOver) DismissCommands(); };
        Session.Changed += SessionChanged;
        _dragScroll.Tick += (_, _) => ScrollDrag();
        DetachedFromVisualTree += (_, _) =>
        {
            _detaching = true;
            try { CancelDrag(); HideCommands(); InputClient.Cancel(); ClearAssetCache(); }
            finally { _detaching = false; }
        };
        SyncSurface();
    }

    public void Load(DocumentSession session)
    {
        _activeComment = null; _commentBlock = null; _commentPress = null;
        _commentMarkers.Children.Clear(); _commentButtons.Clear();
        ClearLayouts();
        ClearAssetCache();
        References.Reset();
        Formatting.Reset();
        _pointerSelecting = false;
        InputClient.Cancel();
        CancelDrag();
        DismissCommands();
        Session.Changed -= SessionChanged;
        Session = session;
        Session.Changed += SessionChanged;
        SyncSurface();
        Surface.ScrollToHome();
    }

    public void FocusText()
    {
        if (TryFocusLayoutSelection()) return;
        Activate();
        Surface.TextArea.Focus();
    }
    internal void OpenAsset(NoteNode node) => AssetInvoked?.Invoke(node);
    internal Bitmap? AssetImage(string id)
    {
        if (_assetImages.TryGetValue(id, out var bitmap)) return bitmap;
        if (ResolveAsset?.Invoke(id) is not { } path) return null;
        try
        {
            if (_assetImages.Count >= 32)
            {
                var visible = Surface.TextArea.TextView.GetVisualDescendants().OfType<Image>().Select(image => image.Source).ToHashSet();
                foreach (var pair in _assetImages.Where(pair => !visible.Contains(pair.Value)).ToArray()) { _assetImages.Remove(pair.Key); pair.Value.Dispose(); }
            }
            using var source = File.OpenRead(path); bitmap = Bitmap.DecodeToWidth(source, 640);
            _assetImages[id] = bitmap; return bitmap;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }
    private void ClearAssetCache() { foreach (var bitmap in _assetImages.Values) bitmap.Dispose(); _assetImages.Clear(); }
    public void SetReferenceCatalogue(IReadOnlyList<DocumentInfo> documents, IReadOnlyList<TagInfo> tags)
    {
        ReferenceDocuments = documents; ReferenceTags = tags;
        _referenceIds = documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var layout in _layouts.Values) layout.UpdateCatalogue(documents, tags);
        Surface.TextArea.TextView.Redraw();
    }
    internal bool IsKnownReference(string id) => _referenceIds == null || _referenceIds.Contains(id);
    private bool ActivateReference(int offset, bool includeEnd = false)
    {
        if (InputClient.IsComposing || !IsEnabled || Surface.Document.TextLength != Session.Projection.Text.Length) return false;
        var row = Session.Projection.At(offset);
        if (NoteReferences.At(row.Node, offset - row.Start, includeEnd) is not { } reference) return false;
        References.Dismiss(); Formatting.Dismiss(); ReferenceInvoked?.Invoke(reference);
        return true;
    }
    internal void ShowNotice(string message) => Notice?.Invoke(message);
    public void Fold(Guid id) { if (!IsEnabled || InputClient.IsComposing) return; Session.Toggle(id); FocusText(); }
    public void ApplyFormat(NoteMark? mark, bool remove = false)
    {
        if (!IsEnabled || InputClient.IsComposing) return;
        ObserveSelection();
        Session.Format(Surface.SelectionStart, Surface.SelectionLength, mark, remove);
        FocusText();
    }

    private void SessionChanged(object? sender, EventArgs args)
    {
        RefreshCommentHighlights();
        if (_dragSource != null) CancelDrag();
        if (!_editing && _commands.IsVisible) HideCommands();
        if (_editing)
        {
            Surface.TextArea.TextView.Redraw();
            if (Surface.Document.Text != Session.Projection.Text && !_queuedSync)
            {
                _queuedSync = true;
                Dispatcher.UIThread.Post(() => { _queuedSync = false; SyncSurface(); }, DispatcherPriority.Input);
            }
        }
        else SyncSurface();
    }

    private void DocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_syncing) return;
        _editing = true;
        try { Session.Edit(e.Offset, e.RemovalLength, e.InsertedText.Text); }
        catch (InvalidOperationException ex)
        {
            Notice?.Invoke(ex.Message);
            Dispatcher.UIThread.Post(() => SyncSurface());
        }
        finally { _editing = false; }
    }

    public void SyncSurface()
    {
        var readOnly = IsDragPreview || Session.IsReadOnly;
        if (Surface.IsReadOnly != readOnly)
        {
            Surface.IsReadOnly = readOnly;
            Surface.TextArea.ReadOnlySectionProvider = new AtomicReadOnlyProvider(this);
            if (readOnly) { InputClient.Cancel(); DismissCommands(); Formatting.Dismiss(); References.Dismiss(); }
        }
        RefreshLayouts();
        _syncing = true;
        try
        {
            var desired = Session.Projection.Text;
            var current = Surface.Document.Text;
            if (desired != current)
            {
                var start = 0;
                while (start < Math.Min(current.Length, desired.Length) && current[start] == desired[start]) start++;
                var end = 0;
                while (end < Math.Min(current.Length, desired.Length) - start && current[^(end + 1)] == desired[^(end + 1)]) end++;
                Surface.Document.Replace(start, current.Length - start - end, desired.Substring(start, desired.Length - start - end));
            }
            var anchor = Session.Projection.Offset(Session.Selection.Anchor);
            var caret = Session.Projection.Offset(Session.Selection.Caret);
            Surface.TextArea.Selection = AvaloniaEdit.Editing.Selection.Create(Surface.TextArea, anchor, caret);
            Surface.CaretOffset = caret;
            Surface.TextArea.TextView.Redraw();
        }
        finally { _syncing = false; }
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ObserveSelection()
    {
        if (_syncing || _editing || _queuedSync || Surface.Document.TextLength != Session.Projection.Text.Length) return;
        if (_activeChild != null && !Surface.TextArea.IsFocused) return;
        var start = Surface.SelectionStart;
        var end = start + Surface.SelectionLength;
        Session.Selection = Surface.CaretOffset == start ? Session.Projection.Selection(end, start) : Session.Projection.Selection(start, end);
        UpdateToggleFeedback();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleKey(object? sender, KeyEventArgs e)
    {
        if (!OwnsInput(e.Source)) return;
        if (Session.IsReadOnly) return; // AvaloniaEdit still handles navigation, selection and copying.
        if (InputClient.IsComposing)
        {
            // Returning alone lets AvaloniaEdit's default Enter/Tab/Delete handler edit the document.
            // IME owns these keys until the committed TextInput event arrives.
            if (e.Key is Key.Enter or Key.Tab or Key.Back or Key.Delete or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End || e.KeyModifiers.HasFlag(KeyModifiers.Control))
                e.Handled = true;
            return;
        }
        if (_dragSource != null && e.Key == Key.Escape) { CancelDrag(); e.Handled = true; return; }
        if (References.HandleKey(e) || _commands.HandleKey(e)) return;
        if (HandleLayoutKey(e)) return;
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (control && shift && e.Key == Key.K) { References.Open(); e.Handled = true; return; }
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && ActivateReference(Surface.CaretOffset, true)) { e.Handled = true; return; }
        if (e.Key == Key.Escape) { CancelDrag(); DismissCommands(); Formatting.Dismiss(); e.Handled = true; return; }
        if (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { Formatting.FocusToolbar(); e.Handled = true; return; }
        if (control)
        {
            if (e.Key == Key.Z) { if (shift) Session.Redo(); else Session.Undo(); FocusHistorySelection(); e.Handled = true; return; }
            if (e.Key == Key.Y) { Session.Redo(); FocusHistorySelection(); e.Handled = true; return; }
            if (e.Key == Key.B) { ApplyFormat(new("bold")); e.Handled = true; return; }
            if (e.Key == Key.I) { ApplyFormat(new("italic")); e.Handled = true; return; }
            if (e.Key == Key.U) { ApplyFormat(new("underline")); e.Handled = true; return; }
            if (e.Key == Key.K) { Formatting.OpenLink(); e.Handled = true; return; }
            if (e.Key == Key.D7 && shift) { Session.ConvertBlock(Surface.CaretOffset, "toggleBlock"); e.Handled = true; return; }
            if (e.Key == Key.T && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { Session.ToggleAll(); e.Handled = true; return; }
        }
        if (e.Key == Key.Enter)
        {
            try { Session.ReplaceSelectionWithEnter(Surface.SelectionStart, Surface.SelectionLength, control, shift); }
            catch (InvalidOperationException ex) { ShowNotice(ex.Message); }
            Surface.ScrollTo(Surface.TextArea.Caret.Line, Surface.TextArea.Caret.Column);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Tab)
        {
            var row = Session.Projection.At(Surface.CaretOffset);
            if (shift) Session.Outdent(row.Block.Id); else Session.Indent(row.Block.Id);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back && Surface.SelectionLength == 0 && !control)
        {
            if (Session.BackspaceAtStart(Surface.CaretOffset)) { SyncSurface(); e.Handled = true; return; }
        }
        if (e.Key == Key.Delete && Surface.SelectionLength == 0 && !control && Session.ExitEmptyList(Surface.CaretOffset))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && !control && Surface.SelectionLength == 0)
        {
            var row = Session.Projection.At(Surface.CaretOffset);
            var prefix = row.Text[..Math.Clamp(Surface.CaretOffset - row.Start, 0, row.Text.Length)];
            var command = prefix switch
            {
                "+" => ("toggleBlock", 1), "#" => ("heading", 1), "##" => ("heading", 2), "###" => ("heading", 3),
                "-" or "*" => ("bulletList", 1), "1." => ("orderedList", 1), "[]" or "[ ]" => ("taskList", 1), ">" => ("blockquote", 1),
                _ => ("", 1)
            };
            if (command.Item1.Length > 0) { Session.ConvertBlock(Surface.CaretOffset, command.Item1, command.Item2, prefix.Length); e.Handled = true; return; }
        }
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageDown or Key.PageUp) Session.BreakTypingGroup();
    }

    private Point CaretPoint()
    {
        var view = Surface.TextArea.TextView;
        if (!view.VisualLinesValid) return new(48, 0);
        var point = view.GetVisualPosition(Surface.TextArea.Caret.Position, VisualYPosition.LineBottom) - view.ScrollOffset;
        return view.TranslatePoint(point, _layout) ?? point;
    }

    private void UpdatePreedit()
    {
        _preedit.IsVisible = InputClient.IsComposing;
        _preeditText.Text = InputClient.Preedit;
        if (InputClient.IsComposing)
        {
            var properties = BlockCaretGeometry.GetTextProperties(Surface);
            _preeditText.FontFamily = properties?.Typeface.FontFamily ?? Surface.FontFamily;
            _preeditText.FontSize = properties?.FontRenderingEmSize ?? Surface.FontSize;
            _preeditText.FontWeight = properties?.Typeface.Weight ?? FontWeight.Normal;
            _preeditText.FontStyle = properties?.Typeface.Style ?? FontStyle.Normal;
            _preeditText.Foreground = properties?.ForegroundBrush ?? Surface.Foreground;
            _preedit.Background = PageBackgroundColor is { } color ? new SolidColorBrush(color) : Ui.Surface;
            var point = Surface.TextArea.TextView.TranslatePoint(InputClient.CursorRectangle.TopLeft, _layout) ?? default;
            _preedit.Margin = new(Math.Max(0, point.X), Math.Max(0, point.Y), 0, 0);
            DismissCommands();
        }
    }

    internal void BeginBlockDrag(BlockRow row, PointerPressedEventArgs e)
    {
        if (Session.IsReadOnly) return;
        if (!e.GetCurrentPoint(Surface).Properties.IsLeftButtonPressed || InputClient.IsComposing || !IsEffectivelyEnabled) return;
        DismissCommands(); Formatting.Dismiss(); References.Dismiss();
        var nodeIds = NoteTree.Descendants(row.Block).Select(node => node.Id).ToHashSet();
        _dragRows = Session.Projection.Rows.Where(candidate => nodeIds.Contains(candidate.Node.Id)).ToArray();
        if (_dragRows.Count == 0) return;
        _dragDepth = row.Depth;
        _dragSource = row.Block.Id;
        SetHoveredBlock(row.Block.Id);
        _dragStart = _dragPointer = e.GetPosition(Surface.TextArea.TextView);
        _dragging = false;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(Surface);
        e.Handled = true;
    }

    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (!OwnsInput(e.Source) && _dragSource == null) return;
        if (_dragSource == null) { UpdateHover(e.GetPosition(Surface.TextArea.TextView)); return; }
        _dragPointer = e.GetPosition(Surface.TextArea.TextView);
        if (!_dragging && Math.Sqrt(Math.Pow(_dragPointer.X - _dragStart.X, 2) + Math.Pow(_dragPointer.Y - _dragStart.Y, 2)) < 5) return;
        if (!_dragging)
        {
            _ghostPreview = new(this, _dragRows[0], _dragRows);
            _ghost.Child = _ghostPreview;
            _ghost.Width = _ghostPreview.Width + 14;
            _ghost.Height = _ghostPreview.Height + 14;
            _ghost.Background = PageBackgroundColor is { } color ? new SolidColorBrush(color) : Ui.Surface;
            _ghost.BorderBrush = PageLine;
            _ghostGrabOffset = new(_ghostPreview.SourceLeft - _dragStart.X - 7, _ghostPreview.SourceTop - _dragStart.Y - 7);
            MountOverlay(_ghost);
            _ghost.IsVisible = true;
            _dragging = true;
            _dragScroll.Start();
        }
        var point = Surface.TextArea.TextView.TranslatePoint(_dragPointer, OverlayOwner._layout) ?? _dragPointer;
        _ghost.Margin = new(Math.Max(0, point.X + _ghostGrabOffset.X),
            Math.Max(0, point.Y + _ghostGrabOffset.Y), 0, 0);
        UpdateDrop();
        e.Handled = true;
    }

    private void UpdateHover(Point point)
    {
        var view = Surface.TextArea.TextView;
        if (!view.VisualLinesValid || point.X < 0 || point.X > view.Bounds.Width || point.Y < 0 || point.Y > view.Bounds.Height)
        {
            SetHoveredBlock(null);
            return;
        }
        var y = point.Y + view.ScrollOffset.Y;
        var line = view.VisualLines.FirstOrDefault(l => y >= l.VisualTop && y < l.VisualTop + l.Height);
        SetHoveredBlock(line == null ? null : Session.Projection.At(line.FirstDocumentLine.Offset).Block.Id);
    }

    private void SetHoveredBlock(Guid? id)
    {
        if (HoveredBlockId == id) return;
        HoveredBlockId = id;
        QueueCommentMarkers();
        foreach (var grip in Surface.TextArea.TextView.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("blockGrip")))
        {
            var visible = id != null && grip.Tag is Guid key && key == id;
            grip.Opacity = visible ? 1 : 0;
            grip.IsHitTestVisible = visible;
        }
        UpdateToggleFeedback();
        Surface.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void UpdateToggleFeedback()
    {
        var current = Session.Projection.Find(Session.Selection.Caret.NodeId)?.Block.Id;
        foreach (var arrow in Surface.TextArea.TextView.GetVisualDescendants().OfType<ToggleDisclosureButton>())
            arrow.SetEmphasized(arrow.Tag is Guid id && (id == current || id == HoveredBlockId));
    }

    private void UpdateDrop()
    {
        var view = Surface.TextArea.TextView;
        if (!view.VisualLinesValid || view.VisualLines.Count == 0 || _dragSource is not { } sourceId) { DropTarget = null; return; }
        if (_dragPointer.X < 0 || _dragPointer.X > view.Bounds.Width || _dragPointer.Y < 0 || _dragPointer.Y > view.Bounds.Height)
        {
            DropTarget = null;
            view.InvalidateLayer(KnownLayer.Background);
            return;
        }
        var y = _dragPointer.Y + view.ScrollOffset.Y;
        var line = view.VisualLines.FirstOrDefault(l => y < l.VisualTop + l.Height) ?? view.VisualLines[^1];
        var row = Session.Projection.At(line.FirstDocumentLine.Offset);
        var relative = (y - line.VisualTop) / line.Height;
        // Match the level change to the horizontal drag from the actual grab point.
        // Absolute text coordinates overshoot by a level because a grip sits left of its text.
        var depth = Math.Max(0, _dragDepth + (int)Math.Round((_dragPointer.X - _dragStart.X) / BlockLayout.Indent, MidpointRounding.AwayFromZero));
        DropTarget = Session.ResolveBlockDrop(sourceId, row.Index, relative >= .5, depth,
            inside: row.IsToggle && relative is >= .2 and <= .8);
        view.InvalidateLayer(KnownLayer.Background);
    }

    internal double DropIndicatorLeft(BlockDropTarget drop)
    {
        var destination = _dragRows[0] with { Depth = drop.Depth };
        return Math.Max(4, BlockLayout.TextInset + drop.Depth * BlockLayout.Indent - PrefixInset(destination));
    }

    private void ScrollDrag()
    {
        var view = Surface.TextArea.TextView;
        if (_scrollViewer == null) return;
        // AvaloniaEdit 11.4.1's ScrollToVerticalOffset is a stub; use its native scrolling contract.
        if (_dragPointer.Y < 35) _scrollViewer.Offset = new(view.HorizontalOffset, Math.Max(0, view.VerticalOffset - 14));
        else if (_dragPointer.Y > view.Bounds.Height - 35) _scrollViewer.Offset = new(view.HorizontalOffset, Math.Min(Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height), view.VerticalOffset + 14));
        else return;
        UpdateDrop();
    }

    private void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragSource is not { } source) return;
        var dragging = _dragging;
        if (dragging) { _dragPointer = e.GetPosition(Surface.TextArea.TextView); UpdateDrop(); }
        var drop = DropTarget;
        CancelDrag();
        if (dragging && drop is { } target) Session.Move(source, target.Target, target.Placement);
        else if (!dragging) OpenBlockMenu(source);
        e.Handled = true;
    }

    private void CancelDrag()
    {
        _dragScroll.Stop();
        _dragSource = null;
        _ghost.IsVisible = false;
        _ghost.Child = null;
        _ghostPreview?.Dispose(); _ghostPreview = null; _dragRows = [];
        MountOverlay(_ghost, restore: true);
        _dragging = false;
        SetHoveredBlock(null);
        DropTarget = null;
        var pointer = _capturedPointer;
        _capturedPointer = null;
        pointer?.Capture(null);
        Surface.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private void OpenBlockMenu(Guid id)
    {
        var row = Session.Projection.Rows.FirstOrDefault(r => r.Block.Id == id);
        if (row == null) return;
        var session = Session;
        var root = session.Root;
        MenuItem Item(string label, Action action, bool focusText = true)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                if (!IsEffectivelyEnabled || OverlayOwner.IsAnyComposing || !ReferenceEquals(session, Session) || !ReferenceEquals(root, Session.Root)) return;
                action(); if (focusText) FocusText();
            };
            return item;
        }
        var items = new List<Control>();
        var comment = Item(Comments.InBlock(row.Node.Id).IsEmpty ? "添加段落评论" : "查看段落评论", () => OpenParagraphComments(row.Node.Id), focusText: false);
        AutomationProperties.SetAutomationId(comment, "BlockMenuComment");
        items.Add(comment); items.Add(new Separator());
        if (!row.IsAtomic)
        {
            items.Add(Item(row.IsToggle ? "取消折叠，保留内容" : "转换为折叠块", () => Session.ConvertBlock(row.Start, row.IsToggle ? "paragraph" : "toggleBlock")));
            items.Add(Item("增加一级缩进", () => Session.Indent(id)));
            items.Add(Item("减少一级缩进", () => Session.Outdent(id)));
        }
        else if ((LayoutBlocks.IsEditableTable(row.Node) || LayoutBlocks.IsEditableColumns(row.Node))
                 && NoteTree.Descendants(row.Node).FirstOrDefault(node => node.IsTextBlock) is { } first)
        {
            items.Add(Item(row.Node.Type == "table" ? "编辑表格" : "编辑分栏", () => NavigateTo(first.Id)));
            if (row.Node.Type == "columnList") items.Add(Item("取消分栏，保留全部内容", () => Session.UnwrapColumns(id)));
        }
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Item("创建块副本", () => Session.DuplicateBlock(id)));
        items.Add(Item("删除此块", () => Session.DeleteBlock(id)));
        var menu = new ContextMenu { ItemsSource = items };
        Surface.ContextMenu?.Close();
        Surface.ContextMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(Surface.ContextMenu, menu)) Surface.ContextMenu = null; };
        menu.Open(Surface);
    }

    private sealed class AtomicReadOnlyProvider(BlockEditor editor) : IReadOnlySectionProvider
    {
        public bool CanInsert(int offset) => !editor.Session.IsReadOnly && !editor.IsDragPreview && !editor.Session.Projection.At(offset).IsAtomic;
        public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
        {
            if (editor.Session.IsReadOnly || editor.IsDragPreview || editor.Session.Projection.Rows.Any(row => row.IsAtomic && row.End > segment.Offset && row.Start < segment.EndOffset)) return [];
            return [segment];
        }
    }
}
