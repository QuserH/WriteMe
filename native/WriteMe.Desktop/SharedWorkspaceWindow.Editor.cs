using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

public sealed partial class SharedWorkspaceWindow
{
    private SharedEditingSession? _shared;
    private SharedDocumentInfo? _sharedInfo;
    private SharedSocket? _socket;
    private BlockEditor? _editor;
    private EditorSidebar? _tools;
    private CommentsPane? _comments;
    private TextBox? _documentTitle;
    private TextBlock? _saveStatus;
    private TextBlock? _peers;
    private Grid? _documentLayout;
    private Button? _recoverDraft;
    private Button? _undoShared;
    private Button? _redoShared;
    private Button? _trashShared;
    private string _role = "viewer";
    private bool _rejected;
    private byte[]? _serverVector;
    private long _revision;
    private long _sequence;
    private readonly Dictionary<string, long> _sent = [];
    private readonly Queue<byte[]> _remoteUpdates = new();
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _remoteTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _editorTimers;

    private async Task OpenDocumentAsync(string id, bool useDraft = true)
    {
        if (useDraft && _sharedInfo?.Id == id) return; if (!await SaveDraftAsync()) return;
        var version = ++_loadingVersion; var api = _api!; var profile = _profile!;
        var data = await api.Request<SharedDocumentData>("shared/" + id, cancellation: _lifetime.Token);
        if (_closing || version != _loadingVersion) return;
        await StopDocumentAsync();
        var replica = new SharedDocumentReplica(data.State);
        try { if (useDraft && _drafts.Load(api.Endpoint.AbsoluteUri, profile.Id, id) is { } saved) { replica.Apply(saved); _ = replica.Read(); } }
        catch { replica.Dispose(); throw; }
        _shared = new(replica, profile.DisplayName, profile.Id, data.Role == "owner"); _sharedInfo = data.Document; _role = data.Role; _rejected = false; _serverVector = null; _revision = _sequence = 0; _sent.Clear();
        _shared.Session.IsReadOnly = !CanWriteShared;
        if (!useDraft) SaveDraft();
        BuildDocument(); _shared.LocalUpdate += LocalUpdate; _shared.Refreshed += (_, _) => RefreshSharedUi();
        _socket = new(api, id); var session = _shared; var socket = _socket;
        _socket.Start(async () => await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (_shared != session || _closing) return; _serverVector = null; await socket.Send(new("hello", Vector: session.Replica.StateVector()));
        }), async message => await Dispatcher.UIThread.InvokeAsync(async () => { if (_shared == session && !_closing) await ReceiveAsync(message); }),
        async status => await Dispatcher.UIThread.InvokeAsync(() => { if (_shared == session && !_closing && !_rejected) { _serverVector = null; if (_saveStatus != null) _saveStatus.Text = status; } }));
        InitializeEditorTimers(); _heartbeat.Start(); _remoteTimer.Start(); RefreshDocumentItems(); RefreshSharedUi();
    }
    private void InitializeEditorTimers()
    {
        if (_editorTimers) return; _editorTimers = true;
        _flushTimer.Tick += async (_, _) => { _flushTimer.Stop(); await RunAsync(FlushAsync); };
        _remoteTimer.Tick += (_, _) =>
        {
            if (_remoteUpdates.Count == 0 || _editor?.IsAnyComposing == true || _comments?.IsComposing == true || _documentTitle != null && Composing(_documentTitle) || _shared == null) return;
            try { while (_remoteUpdates.TryDequeue(out var update)) _shared.Apply(update); SaveDraft(); }
            catch (Exception e) when (IsConnectionError(e)) { Reject(e.Message); }
        };
        _heartbeat.Tick += async (_, _) => { if (_socket != null) await _socket.Send(new("ping")); };
    }
    private void BuildDocument()
    {
        if (_shared == null) return;
        _heading.Text = _workspace?.Name; _editor = new(_shared.Session); _tools = new(_editor) { Margin = new(8, 4, 12, 14), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        _documentLayout = new Grid { ColumnDefinitions = new("*,56"), RowDefinitions = new("*") };
        _headerActions.Children.Clear(); var toolbar = _headerActions;
        _peers = Label("正在连接…", 11, Ui.Muted); _peers.Margin = new(0, 0, 16, 0); toolbar.Children.Add(_peers);
        toolbar.Children.Add(ActionButton("共享", "SharedShare", MembersAsync));
        toolbar.Children.Add(ActionButton("评论", "SharedComments", () => { OpenSharedComments(); return Task.CompletedTask; }));
        _undoShared = ActionButton("↶", "SharedUndo", () => { if (CanWriteShared) _shared?.Undo(); return Task.CompletedTask; }); toolbar.Children.Add(_undoShared);
        _redoShared = ActionButton("↷", "SharedRedo", () => { if (CanWriteShared) _shared?.Redo(); return Task.CompletedTask; }); toolbar.Children.Add(_redoShared);
        toolbar.Children.Add(ActionButton("导出", "SharedExport", ExportSharedAsync));
        _recoverDraft = ActionButton("重新打开", "SharedRecoverDraft", RecoverDraftAsync); _recoverDraft.IsVisible = false; toolbar.Children.Add(_recoverDraft);
        var trash = ActionButton("回收站", "SharedDeleteDocument", async () =>
        {
            if (!CanWriteShared || _sharedInfo == null) return; var id = _sharedInfo.Id;
            if (!await ConfirmAsync("将文档移至回收站？", "这篇文档会从所有成员的列表移除，可以在回收站恢复。", "移至回收站")) return;
            if (_sharedInfo?.Id != id || !CanWriteShared) return;
            await _api!.Request<object>($"shared/{id}", "DELETE", cancellation: _lifetime.Token); await StopDocumentAsync(); await RefreshDocumentsAsync(); ShowHome();
        }); trash.IsEnabled = CanWriteShared; _trashShared = trash; toolbar.Children.Add(trash);
        var paper = new Grid { RowDefinitions = new("Auto,*,Auto"), Margin = new(6, 0, 6, 4) };
        var heading = new StackPanel { Margin = new(64, 50, 36, 22), Spacing = 18 };
        _saveStatus = Label("正在连接…", 10, Ui.Muted); AutomationProperties.SetAutomationId(_saveStatus, "SharedSaveStatus");
        _documentTitle = new TextBox { Text = _shared.Title, Watermark = "无标题", FontSize = 31, FontWeight = FontWeight.SemiBold, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0), MaxLength = 500, Classes = { "clean" } };
        AutomationProperties.SetAutomationId(_documentTitle, "SharedDocumentTitle");
        _documentTitle.TextChanged += (_, _) => { if (_shared != null && CanWriteShared && (_documentTitle.Text ?? "") != _shared.Title) _shared.SetTitle(_documentTitle.Text ?? ""); };
        heading.Children.Add(_documentTitle); heading.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(0, 2, 0, 0) }); paper.Children.Add(heading);
        _editor.Margin = new(16, 5, 30, 8); AutomationProperties.SetAutomationId(_editor, "SharedBlockEditor"); Grid.SetRow(_editor, 1); paper.Children.Add(_editor);
        var footer = _saveStatus; footer.Margin = new(32, 10, 32, 18); Grid.SetRow(footer, 2); paper.Children.Add(footer);
        var card = new Border { Child = paper, Background = Ui.Surface, CornerRadius = new(22), BorderBrush = Ui.Line, BorderThickness = new(1), Margin = new(6, 0, 4, 8) };
        Grid.SetRow(card, 0); _documentLayout.Children.Add(card); Grid.SetColumn(_tools, 1); Grid.SetRow(_tools, 0); _documentLayout.Children.Add(_tools);
        _comments = new(_editor) { Margin = new(8, 0, 8, 8), IsVisible = false }; _comments.Bind(_sharedInfo!.Id, _shared.Session); Grid.SetRow(_comments, 0); Grid.SetColumn(_comments, 1); _documentLayout.Children.Add(_comments);
        _comments.CloseRequested += (_, _) => { _comments.IsVisible = false; _tools.IsVisible = true; AdjustSharedSidebars(); };
        _comments.OverviewRequested += (_, _) => { _comments.ClearContext(); AdjustSharedSidebars(); };
        _editor.CommentRequested += anchor => { OpenSharedComments(); _comments.Compose(anchor); };
        _editor.CommentInvoked += id => { OpenSharedComments(); _comments.ShowThread(id); };
        _editor.ParagraphCommentsRequested += anchor => { OpenSharedComments(); _comments.ShowParagraph(anchor); };
        _editor.SelectionChanged += async (_, _) => { if (_socket != null && _shared != null) await _socket.Send(new("presence", BlockId: _shared.Session.Selection.Caret.NodeId.ToString("D"))); };
        _tools.PanelChanged += (_, _) => AdjustSharedSidebars(); _screen.Content = _documentLayout;
        _documentLayout.AddHandler(KeyDownEvent, (_, e) => { if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key == Key.M) { e.Handled = true; if (CanWriteShared) _editor.RequestComment(); } }, RoutingStrategies.Tunnel);
        _tools.IsEnabled = CanWriteShared; _documentTitle.IsReadOnly = !CanWriteShared;
        _documentLayout.SizeChanged += (_, _) => AdjustSharedSidebars(); AdjustSharedSidebars();
    }
    private bool CanWriteShared => !_rejected && SharedProtocol.CanWrite(_role);
    private void OpenSharedComments()
    {
        if (_comments == null || _tools == null) return; _comments.ClearContext(); _comments.IsVisible = true; _comments.Refresh(); _tools.Close(); _tools.IsVisible = false; AdjustSharedSidebars();
    }
    private void AdjustSharedSidebars()
    {
        if (_documentLayout == null || _tools == null) return;
        var comments = _comments?.IsVisible == true;
        if (_peers != null) _peers.IsVisible = Bounds.Width >= 1150;
        _documentLayout.ColumnDefinitions[1].Width = new(comments ? 340 : _tools.IsOpen ? 300 : 56);
        _tools.MaxHeight = Math.Max(180, Bounds.Height - 120); _tools.Height = _tools.IsOpen ? _tools.MaxHeight : double.NaN;
        _shell.ColumnDefinitions[0].Width = new(Bounds.Width < 1100 && (comments || _tools.IsOpen) ? 0 : 284);
    }
    private void LocalUpdate(byte[] update)
    {
        if (_shared == null) return; _revision++;
        try { SaveDraft(); } catch (Exception e) when (IsConnectionError(e)) { Notice("本机草稿保存失败，内容仍在窗口中：" + e.Message); }
        if (_saveStatus != null) _saveStatus.Text = _serverVector == null ? "离线 · 修改保存在此设备" : "正在保存…";
        _flushTimer.Stop(); _flushTimer.Start();
    }
    private void SaveDraft()
    {
        if (_shared != null && _sharedInfo != null && _profile != null && _api != null) _drafts.Save(_api.Endpoint.AbsoluteUri, _profile.Id, _sharedInfo.Id, _shared.Replica.State());
    }
    private Task<bool> SaveDraftAsync()
    {
        try { SaveDraft(); return Task.FromResult(true); } catch (Exception e) when (IsConnectionError(e)) { Notice("无法保存本机草稿，窗口将保持打开：" + e.Message); return Task.FromResult(false); }
    }
    private async Task FlushAsync()
    {
        if (_shared == null || _socket == null || _serverVector == null || !CanWriteShared) return;
        SaveDraft(); var id = (++_sequence).ToString(System.Globalization.CultureInfo.InvariantCulture); _sent[id] = _revision;
        if (!await _socket.Send(new("update", _shared.Replica.Difference(_serverVector), _shared.Replica.StateVector(), id))) { _serverVector = null; if (_saveStatus != null) _saveStatus.Text = "离线 · 修改保存在此设备"; }
    }
    private async Task ReceiveAsync(SharedWireMessage message)
    {
        if (_shared == null) return;
        try
        {
            switch (message.Type)
            {
                case "sync":
                    if (message.Update != null) QueueSharedUpdate(message.Update); _serverVector = message.Vector; _sent.Clear();
                    if (CanWriteShared) await FlushAsync(); else if (_saveStatus != null) _saveStatus.Text = "已连接 · 仅阅读";
                    break;
                case "update": if (message.Update != null) QueueSharedUpdate(message.Update); break;
                case "ack":
                    _serverVector = message.Vector;
                    if (message.Id != null && _sent.Remove(message.Id, out var revision) && revision == _revision && _saveStatus != null) _saveStatus.Text = "所有更改已保存";
                    break;
                case "peers": if (_peers != null) _peers.Text = string.Join(" · ", (message.Peers ?? []).DistinctBy(peer => peer.AccountId).Select(peer => peer.DisplayName)) + "  在线"; break;
                case "permissions":
                    if (_sharedInfo != null) { var data = await _api!.Request<SharedDocumentData>("shared/" + _sharedInfo.Id, cancellation: _lifetime.Token); _role = data.Role; _shared.Session.CanModerateComments = _role == "owner"; RefreshSharedUi(); }
                    break;
                case "error":
                    Reject(message.Error ?? "无法保存到服务器"); break;
            }
        }
        catch (Exception e) when (IsConnectionError(e)) { Reject(e.Message); }
    }
    private void Reject(string message)
    {
        _rejected = true; _socket?.Stop(); _heartbeat.Stop(); _flushTimer.Stop(); _remoteUpdates.Clear();
        Notice(message + "。本机草稿已保留，可使用导出保留副本。");
        if (_saveStatus != null) _saveStatus.Text = "尚未保存到服务器 · 草稿保留在本机";
        RefreshSharedUi();
    }
    private void QueueSharedUpdate(byte[] update)
    {
        if (_shared == null) return;
        if (_editor?.IsAnyComposing == true || _comments?.IsComposing == true || _documentTitle != null && Composing(_documentTitle)) _remoteUpdates.Enqueue(update);
        else { _shared.Apply(update); SaveDraft(); }
    }
    private void RefreshSharedUi()
    {
        if (_shared == null) return;
        if (_documentTitle != null && _documentTitle.Text != _shared.Title) _documentTitle.Text = _shared.Title;
        if (_documentTitle != null) _documentTitle.IsReadOnly = !CanWriteShared;
        _shared.Session.IsReadOnly = !CanWriteShared;
        if (_tools != null) _tools.IsEnabled = CanWriteShared; _comments?.Refresh();
        if (_undoShared != null) _undoShared.IsEnabled = _shared.CanUndo;
        if (_redoShared != null) _redoShared.IsEnabled = _shared.CanRedo;
        if (_trashShared != null) _trashShared.IsEnabled = CanWriteShared;
        if (_recoverDraft != null) _recoverDraft.IsVisible = _rejected;
        _heading.Text = (_workspace?.Name ?? "共享工作区") + "  /  " + Display(_shared.Title);
    }
    private async Task StopDocumentAsync()
    {
        _flushTimer.Stop(); _remoteTimer.Stop(); _heartbeat.Stop(); _remoteUpdates.Clear();
        if (_shared != null) _shared.LocalUpdate -= LocalUpdate;
        var socket = _socket; _socket = null; if (socket != null) await socket.DisposeAsync();
        _comments?.Release(); _screen.Content = null; _shared?.Dispose(); _shared = null; _sharedInfo = null; _editor = null; _comments = null; _tools = null; _documentLayout = null; _documentTitle = null; _serverVector = null;
        _recoverDraft = _undoShared = _redoShared = _trashShared = null;
    }
    private async Task ExportSharedAsync() => _ = await ExportSharedDraftAsync();
    private async Task<bool> ExportSharedDraftAsync()
    {
        if (_shared == null) return false; var json = NoteJson.Serialize(_shared.Session.Root);
        var file = await StorageProvider.SaveFilePickerAsync(new() { Title = "导出共享文档", SuggestedFileName = "WriteME-共享文档.json", DefaultExtension = "json" }); if (file == null) return false;
        await using var output = await file.OpenWriteAsync(); output.SetLength(0); await using var writer = new StreamWriter(output); await writer.WriteAsync(json); Notice("文档和评论已导出");
        return true;
    }
    private async Task RecoverDraftAsync()
    {
        if (_sharedInfo == null || !_rejected) return; var id = _sharedInfo.Id;
        if (!await ConfirmAsync("重新打开服务器版本？", "先把当前草稿和评论导出为副本，再读取服务器内容。取消保存会保留当前文档。", "导出草稿并重新打开")) return;
        if (await ExportSharedDraftAsync() && _sharedInfo?.Id == id) await OpenDocumentAsync(id, useDraft: false);
    }
}
