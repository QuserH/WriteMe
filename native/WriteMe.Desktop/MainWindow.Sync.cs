using System.Net.Http;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer _syncTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource _syncCancellation = new();
    private SavedSyncConnection? _syncConnection;
    private Task? _syncWork;
    private bool _syncApplying;
    private bool _closingPending;
    private bool _closed;
    private bool _syncConnecting;
    private bool _syncDisconnecting;
    private string _syncStatus = "尚未连接同步服务器 · 笔记已保存在本机";
    private event Action? SyncStatusChanged;

    private void InitializeSyncUi()
    {
        _syncConnection = SyncCredentials.Load(_store);
        if (_syncConnection != null) _syncStatus = "已连接 " + _syncConnection.Username + " · 等待同步";
        this.FindControl<Button>("SyncButton")!.Click += async (_, _) =>
        {
            if (_store.SyncConflicts().Count > 0) await RunUiAsync(ResolveConflictsAsync);
            else if (_syncConnection == null) await SettingsAsync();
            else await StartSyncAsync();
        };
        _syncTimer.Tick += async (_, _) => await StartSyncAsync();
        Opened += async (_, _) => { _syncTimer.Start(); await StartSyncAsync(); };
        UpdateSyncStatus(); UpdateEditorAvailability();
    }

    private bool ActiveHasConflict => _store.SyncState("document/" + _active.Id) is { } state && SyncProtocol.HasConflict(state);
    private void UpdateEditorAvailability()
    {
        var editable = !_syncApplying && !_switching && !_closingPending && !ActiveHasConflict;
        _editor.IsEnabled = editable; _title.IsReadOnly = !editable; _outline.IsEnabled = editable;
        _comments.Refresh(); UpdateCommentButtons();
        UpdateHistoryButtons();
    }
    private void UpdateSyncStatus(string? value = null)
    {
        if (_closed) return;
        if (value != null) _syncStatus = value;
        var conflicts = _store.SyncConflicts().Count;
        var button = this.FindControl<Button>("SyncButton")!;
        button.Content = conflicts > 0 ? "冲突 " + conflicts : _syncWork is { IsCompleted: false } ? "同步中…" : "同步";
        ToolTip.SetTip(button, _syncStatus); SyncStatusChanged?.Invoke();
    }

    private Task StartSyncAsync()
    {
        if (_closed || _closingPending || _syncConnection == null || _syncConnecting || _syncDisconnecting) return Task.CompletedTask;
        if (_syncWork is { IsCompleted: false }) return _syncWork;
        _syncWork = RunSyncAsync(_syncConnection, _syncCancellation.Token); UpdateSyncStatus(); return _syncWork;
    }
    private async Task RunSyncAsync(SavedSyncConnection connection, CancellationToken cancellation)
    {
        try
        {
            if (!await SaveAsync()) return;
            UpdateSyncStatus("正在同步…");
            using var client = new SyncClient(SyncProtocol.Endpoint(connection.Endpoint), connection.Login);
            var result = await Task.Run(() => client.SynchronizeAsync(_store,
                async (response, token) => await Dispatcher.UIThread.InvokeAsync(() => ApplyRemoteAsync(client.Target, response, token)), cancellation), cancellation);
            if (result.DownloadedAssets > 0) { _editor.Surface.TextArea.TextView.Redraw(); ApplyCover(); }
            var message = result.Conflicts > 0 ? result.Conflicts + " 项冲突待选择 · 点击同步查看版本"
                : result.MissingAssets > 0 ? result.MissingAssets + " 个附件尚不可用，下次同步继续重试"
                : "已同步 · " + DateTime.Now.ToString("HH:mm:ss");
            UpdateSyncStatus(message);
            if (result.Conflicts > 0) _status.Text = message;
        }
        catch (OperationCanceledException) { if (!_closingPending) UpdateSyncStatus(cancellation.IsCancellationRequested ? "同步已取消，修改仍保存在本机" : "同步超时，稍后自动重试"); }
        catch (SyncAuthenticationException ex) { _syncConnection = null; SyncCredentials.Clear(_store); UpdateSyncStatus(ex.Message); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        { UpdateSyncStatus("同步未完成，稍后重试：" + ex.Message); }
        finally
        {
            if (!_closed) Dispatcher.UIThread.Post(() => { if (!_closed) { UpdateSyncStatus(); _tools.RefreshExtendedPanel(); } });
        }
    }

    private async Task<IReadOnlySet<string>> ApplyRemoteAsync(string target, SyncResponse response, CancellationToken cancellation)
    {
        while (_switching || _editor.IsAnyComposing || _comments.IsComposing || _title.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText)))
            await Task.Delay(60, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var enabled = IsEnabled; _syncApplying = true; IsEnabled = false; UpdateEditorAvailability();
        try
        {
            if (!await SaveAsync()) throw new IOException("当前草稿尚未保存，本轮同步稍后重试");
            await _saveGate.WaitAsync(cancellation);
            try
            {
                var changed = await Task.Run(() => _store.ApplySync(target, response), cancellation);
                if (changed.Count > 0) RefreshAfterSync(changed); return changed;
            }
            finally { _saveGate.Release(); }
        }
        finally { _syncApplying = false; IsEnabled = enabled; UpdateEditorAvailability(); }
    }

    private void RefreshAfterSync(IReadOnlySet<string> changed)
    {
        var documents = _store.List();
        if (documents.Count == 0) { _store.Create(); documents = _store.List(); }
        var nextId = documents.Any(document => document.Id == _active.Id) ? _active.Id : documents[0].Id;
        var next = _store.Get(nextId); var sameDocument = nextId == _active.Id;
        var contentChanged = !sameDocument || NoteJson.Serialize(NoteJson.Parse(next.Content)) != NoteJson.Serialize(_editor.Session.Root);
        var caret = _editor.Surface.CaretOffset;
        if (contentChanged)
        {
            _editor.Session.Changed -= ContentChanged;
            _editor.Load(new(NoteJson.Parse(next.Content)));
            _editor.Session.Changed += ContentChanged;
            if (sameDocument) _editor.Surface.CaretOffset = Math.Min(caret, _editor.Surface.Document.TextLength);
        }
        _active = next; _editVersion = _savedVersion = 0;
        ReloadDocuments(); ShowActive(!sameDocument || _pageAppearance != _store.Appearance(nextId));
        if (contentChanged && sameDocument) _status.Text = "已接收其他设备的内容，原文可在本地备份中查看";
        if (changed.Count > 0) _editor.Surface.TextArea.TextView.Redraw();
        UpdateEditorAvailability();
    }

    private async Task StopSyncAsync()
    {
        _syncTimer.Stop(); await _syncCancellation.CancelAsync();
        if (_syncWork is { } work) await work;
    }
    private async Task DisconnectSyncAsync()
    {
        if (_syncDisconnecting) return;
        _syncDisconnecting = true;
        try
        {
        var connection = _syncConnection; _syncConnection = null;
        await StopSyncAsync(); SyncCredentials.Clear(_store);
        _syncCancellation.Dispose(); _syncCancellation = new();
        if (!_closingPending) _syncTimer.Start();
        UpdateSyncStatus("已退出同步 · 所有本机笔记仍然保留");
        if (connection == null) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_syncCancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var client = new SyncClient(SyncProtocol.Endpoint(connection.Endpoint), connection.Login);
            await client.LogoutAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        { UpdateSyncStatus("已退出本机登录；服务器暂不可达，旧会话将在到期后失效"); }
        }
        finally { _syncDisconnecting = false; UpdateSyncStatus(); }
    }

    partial void AddSyncSettings(StackPanel panel)
    {
        panel.Children.Add(PanelHeading("同步"));
        panel.Children.Add(new TextBlock { Text = "连接自己的 WriteME 服务器，让本机资料库与同一账号的设备同步。断网时可以继续编辑。", FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Text = _syncStatus, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "SyncStatus"); panel.Children.Add(status);
        var endpoint = new TextBox { Watermark = "https://notes.example.com", Text = _store.Setting("sync_endpoint"), MaxLength = 2048 };
        var username = new TextBox { Watermark = "账号", Text = _store.Setting("sync_username"), MaxLength = 100 };
        var password = new TextBox { Watermark = "密码", PasswordChar = '●', MaxLength = 1024 };
        AutomationProperties.SetAutomationId(endpoint, "SyncEndpoint"); AutomationProperties.SetAutomationId(username, "SyncUsername"); AutomationProperties.SetAutomationId(password, "SyncPassword");
        panel.Children.Add(endpoint); panel.Children.Add(username); panel.Children.Add(password);
        var login = new Button { Content = "登录并同步此资料库", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(login, "SyncLogin"); panel.Children.Add(login);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var sync = new Button { Content = "立即同步" }; var logout = new Button { Content = "退出登录" }; var conflicts = new Button { Content = "查看冲突" };
        AutomationProperties.SetAutomationId(sync, "SyncNow"); AutomationProperties.SetAutomationId(logout, "SyncLogout"); AutomationProperties.SetAutomationId(conflicts, "SyncConflicts");
        actions.Children.Add(sync); actions.Children.Add(logout); actions.Children.Add(conflicts); panel.Children.Add(actions);
        void Refresh()
        {
            if (_closed) return;
            status.Text = _syncStatus; sync.IsEnabled = _syncConnection != null && !_syncConnecting; logout.IsEnabled = _syncConnection != null && !_syncConnecting;
            login.IsEnabled = !_syncConnecting && !_syncDisconnecting; conflicts.IsEnabled = _store.SyncConflicts().Count > 0;
        }
        SyncStatusChanged += Refresh; panel.DetachedFromVisualTree += (_, _) => SyncStatusChanged -= Refresh; Refresh();
        sync.Click += async (_, _) => await StartSyncAsync(); logout.Click += async (_, _) => await DisconnectSyncAsync();
        conflicts.Click += async (_, _) => await RunUiAsync(ResolveConflictsAsync);
        login.Click += async (_, _) =>
        {
            if (_syncConnecting || password.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText))) return;
            _syncConnecting = true; Refresh();
            try
            {
                var uri = SyncProtocol.Endpoint(endpoint.Text ?? ""); var user = username.Text?.Trim() ?? ""; var secret = password.Text ?? "";
                password.Text = "";
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_syncCancellation.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var credentials = await SyncClient.LoginAsync(uri.AbsoluteUri, user, secret, timeout.Token);
                if (_closed || _closingPending) return;
                await StopSyncAsync(); _syncCancellation.Dispose(); _syncCancellation = new();
                _syncConnection = new(uri.AbsoluteUri, user, credentials);
                var persisted = SyncCredentials.Save(_store, _syncConnection);
                using (var client = new SyncClient(uri, credentials)) _store.ResetSyncCursor(client.Target);
                UpdateSyncStatus(persisted ? "登录成功，正在同步资料库" : "本次登录有效，关闭应用后需重新登录"); _syncTimer.Start();
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ArgumentException or JsonException or System.Security.Cryptography.CryptographicException)
            { UpdateSyncStatus(ex is OperationCanceledException ? "登录超时，请检查服务器地址" : ex.Message); }
            finally { _syncConnecting = false; Refresh(); }
            await StartSyncAsync();
        };
    }
    partial void AddSyncInfo(StackPanel panel)
    {
        panel.Children.Add(PanelHeading("同步"));
        panel.Children.Add(new TextBlock { Text = _syncStatus, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Muted });
        panel.Children.Add(PanelAction("同步设置", () => _ = SettingsAsync(), "InfoSyncSettings"));
        if (_store.SyncConflicts().Count > 0) panel.Children.Add(PanelAction("查看并选择冲突版本", () => _ = RunUiAsync(ResolveConflictsAsync), "InfoResolveConflicts"));
    }

    private async Task ResolveConflictsAsync()
    {
        if (!await SaveAsync()) return;
        var conflicts = _store.SyncConflicts();
        if (conflicts.Count == 0) { UpdateSyncStatus("当前没有待处理的同步冲突"); return; }
        var selected = conflicts.Count == 1 ? conflicts[0] : await ChooseAsync("选择需要处理的同步冲突", conflicts, conflict => conflict.Title);
        if (selected == null) return;
        var content = new StackPanel { Margin = new(24), Spacing = 14 };
        content.Children.Add(new TextBlock { Text = selected.Title, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "这些版本在离线期间分别修改。请先查看内容，再选择一个继续编辑；需要保留的其他版本可以另存副本。", FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap });
        var dialog = Dialog("处理同步冲突", new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, 660, Math.Min(680, Math.Max(480, Bounds.Height - 60)));
        var kind = SyncProtocol.Subject(selected.Entity.Key).Kind;
        foreach (var version in selected.Entity.Versions)
        {
            var card = new StackPanel { Spacing = 10 };
            SyncDocumentPayload? payload = kind == "document" && version.Payload != null ? SyncProtocol.Read<SyncDocumentPayload>(version.Payload) : null;
            var heading = version.Payload == null ? "已删除的版本" : payload != null ? NoteReferences.DisplayTitle(payload.Document.Title)
                : kind == "space" ? SyncProtocol.Read<SpaceInfo>(version.Payload).Name : SyncProtocol.Read<FolderInfo>(version.Payload).Name;
            card.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            card.Children.Add(new TextBlock { Text = "设备 " + version.Id.Split(':')[0][..Math.Min(8, version.Id.IndexOf(':'))] + (payload == null ? "" : " · " + DateTimeOffset.FromUnixTimeMilliseconds(payload.Document.UpdatedAt).LocalDateTime.ToString("M/d HH:mm")), FontSize = 10, Foreground = Ui.Muted });
            if (payload != null)
            {
                var versionRoot = NoteJson.ParseStrict(payload.Document.Content);
                var preview = new TextBox { Text = DocumentText.Plain(versionRoot), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 85, MaxHeight = 180, FontSize = 12 };
                ScrollViewer.SetVerticalScrollBarVisibility(preview, ScrollBarVisibility.Auto); card.Children.Add(preview);
                var comments = new TextBox { Text = NoteComments.For(versionRoot).Summary(), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 180, FontSize = 12 };
                AutomationProperties.SetName(comments, "此版本的评论");
                ScrollViewer.SetVerticalScrollBarVisibility(comments, ScrollBarVisibility.Auto); card.Children.Add(comments);
            }
            else if (version.Payload == null) card.Children.Add(new TextBlock { Text = "选择此版本会删除该项目。其他版本请先另存副本。", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var keep = new Button { Content = version.Payload == null ? "选择删除" : "保留这个版本" }; AutomationProperties.SetAutomationId(keep, "ResolveVersion_" + version.Id);
            keep.Click += async (_, _) => await RunUiAsync(async () =>
            {
                _store.ResolveSyncConflict(selected.Entity, version.Id); dialog.Close();
                RefreshAfterSync(new HashSet<string> { selected.Entity.Key }); UpdateSyncStatus("已选择版本，等待同步"); await StartSyncAsync();
            }); actions.Children.Add(keep);
            if (payload != null)
            {
                var copy = new Button { Content = "另存为副本" }; actions.Children.Add(copy);
                copy.Click += async (_, _) => await RunUiAsync(() =>
                {
                    var document = _store.Create(NoteReferences.DisplayTitle(payload.Document.Title) + " · 冲突副本", NoteJson.ParseStrict(payload.Document.Content));
                    _store.SetAppearance(document.Id, payload.Document.Appearance); copy.IsEnabled = false; copy.Content = "副本已保存在我的空间"; ReloadDocuments(); return Task.CompletedTask;
                });
            }
            card.Children.Add(actions); content.Children.Add(new Border { Child = card, Padding = new(16), BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(12) });
        }
        var close = new Button { Content = "稍后处理", HorizontalAlignment = HorizontalAlignment.Right }; close.Click += (_, _) => dialog.Close(); content.Children.Add(close);
        await dialog.ShowDialog(this);
    }
}
