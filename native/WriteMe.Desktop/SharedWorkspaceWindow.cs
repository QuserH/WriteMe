using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop.Editing;

namespace WriteMe.Desktop;

// Note: 独立共享模式不上传个人资料，账号登录与正文保持原生绘制 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed partial class SharedWorkspaceWindow : Window
{
    private readonly NoteStore _localStore;
    private readonly SharedDraftStore _drafts;
    private readonly Grid _shell = new() { ColumnDefinitions = new("284,*"), RowDefinitions = new("56,*") };
    private readonly StackPanel _navigation = new() { Spacing = 5, Margin = new(14, 12, 14, 0) };
    private readonly StackPanel _documentItems = new() { Spacing = 4, Margin = new(14, 0) };
    private readonly StackPanel _accountArea = new() { Spacing = 6, Margin = new(14, 10, 14, 16) };
    private readonly StackPanel _headerActions = new() { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
    private string _searchText = "";
    private readonly ContentControl _screen = new();
    private readonly TextBlock _heading = Label("共享工作区", 12, Ui.Muted);
    private readonly TextBlock _banner = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Chrome("#A36345"), Margin = new(22, 10), IsVisible = false };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private SharedApiClient? _api;
    private SharedProfile? _profile;
    private SavedSyncConnection? _connection;
    private SharedWorkspace[] _workspaces = [];
    private SharedWorkspace? _workspace;
    private SharedDocumentInfo[] _documents = [];
    private bool _trash;
    private bool _closing;
    private bool _allowClose;
    private bool _closed;
    private int _loadingVersion;
    private bool _refreshing;

    public SharedWorkspaceWindow(NoteStore store)
    {
        _localStore = store; _drafts = new(Path.GetDirectoryName(store.DatabasePath)!);
        Title = "WriteME · 共享工作区"; Width = 1240; Height = 820; MinWidth = 820; MinHeight = 590; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendClientAreaToDecorationsHint = true; ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome; ExtendClientAreaTitleBarHeightHint = 0;
        Background = Ui.Shell; _shell.Background = Ui.Shell;
        var frame = new Border { Child = _shell, CornerRadius = new(10), BorderThickness = new(1), BorderBrush = Ui.Chrome("#E6E8ED"), Background = Ui.Shell, ClipToBounds = true }; Content = frame;
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto,Auto"), Margin = new(20, 0, 0, 0) };
        header.Children.Add(Label("WriteME", 16, Ui.Chrome("#1A1C1E"), true)); _heading.VerticalAlignment = VerticalAlignment.Center; _heading.HorizontalAlignment = HorizontalAlignment.Center; Grid.SetColumn(_heading, 1); header.Children.Add(_heading);
        Grid.SetColumn(_headerActions, 2); header.Children.Add(_headerActions);
        var back = ActionButton("本机笔记", "SharedBackToPersonal", async () => { if (await SaveDraftAsync()) Close(); }); Grid.SetColumn(back, 3); back.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(back);
        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new(12, 0, 0, 0) }; Grid.SetColumn(captions, 4); header.Children.Add(captions);
        Grid.SetColumnSpan(header, 2); _shell.Children.Add(header); WindowChrome.Attach(this, header, captions, frame);
        var sidebarContent = new Grid { RowDefinitions = new("Auto,*,Auto") }; sidebarContent.Children.Add(_navigation);
        var documentScroll = new ScrollViewer { Content = _documentItems, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(documentScroll, 1); sidebarContent.Children.Add(documentScroll);
        Grid.SetRow(_accountArea, 2); sidebarContent.Children.Add(_accountArea);
        var sidebar = new Border { Background = Ui.Shell, Child = sidebarContent };
        Grid.SetRow(sidebar, 1); _shell.Children.Add(sidebar);
        var main = new Grid { RowDefinitions = new("Auto,*") }; main.Children.Add(_banner); Grid.SetRow(_screen, 1); main.Children.Add(_screen); Grid.SetColumn(main, 1); Grid.SetRow(main, 1); _shell.Children.Add(main);
        _refreshTimer.Tick += async (_, _) => { if (_api != null && _workspace != null && !_refreshing && !_closed) { _refreshing = true; try { await RefreshDocumentsAsync(); } catch (Exception e) when (IsConnectionError(e)) { } finally { _refreshing = false; } } };
        Opened += async (_, _) => await RunAsync(InitializeAsync);
        Closing += async (_, e) =>
        {
            if (_allowClose) return; e.Cancel = true; if (_closing) return; _closing = true;
            if (!await SaveDraftAsync()) { _closing = false; return; }
            _loadingVersion++; _lifetime.Cancel(); _refreshTimer.Stop(); IsEnabled = false;
            await StopDocumentAsync(); _allowClose = true; Close();
        };
        Closed += (_, _) => { _closed = true; _api?.Dispose(); _drafts.Dispose(); _lifetime.Dispose(); _refreshTimer.Stop(); };
        ShowLogin();
    }
    private static TextBlock Label(string text, double size = 13, IBrush? foreground = null, bool bold = false) => new() { Text = text, FontSize = size, Foreground = foreground ?? Ui.Ink, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private Button ActionButton(string title, string id, Func<Task> action, bool primary = false)
    {
        var button = new Button { Content = title, Padding = new(12, 9), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, CornerRadius = new(7), Classes = { "quiet" } };
        if (primary) { button.Classes.Clear(); button.Background = Ui.Chrome("#2E6DE9"); button.Foreground = Ui.Surface; button.BorderThickness = new(0); button.CornerRadius = new(10); }
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, title); button.Click += async (_, _) => { if (!button.IsEnabled) return; button.IsEnabled = false; try { await RunAsync(action); } finally { button.IsEnabled = true; RefreshSharedUi(); } }; return button;
    }
    private Button NavigationButton(string title, string id, SidebarSymbol symbol, Func<Task> action)
    {
        var button = ActionButton(title, id, action); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new SidebarGlyph(symbol, 17) { VerticalAlignment = VerticalAlignment.Center }); content.Children.Add(Label(title, 14, Ui.Ink)); button.Content = content; return button;
    }
    private static TextBox Input(string id, string placeholder, string? value = null, bool password = false)
    {
        var input = new TextBox { Text = value, Watermark = placeholder, MinHeight = 42, FontSize = 13, CornerRadius = new(8), Padding = new(12, 10), Background = Ui.Surface, BorderBrush = Ui.Chrome("#E6E8ED"), MaxLength = password ? 1024 : 2048 };
        if (password) input.PasswordChar = '●'; AutomationProperties.SetAutomationId(input, id); AutomationProperties.SetName(input, placeholder); return input;
    }
    private static StackPanel Field(string title, Control input, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 9, Margin = new(0, 0, 0, 17) }; panel.Children.Add(Label(title, 12, Ui.Chrome("#555B64"))); panel.Children.Add(input); if (hint != null) panel.Children.Add(Label(hint, 10, Ui.Muted)); return panel;
    }
    private static bool Composing(Control control) => control.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText));
    private static bool IsConnectionError(Exception exception) => exception is IOException or HttpRequestException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or System.Security.Cryptography.CryptographicException;
    private async Task RunAsync(Func<Task> action)
    {
        try { Notice(null); await action(); }
        catch (Exception e) when (IsConnectionError(e)) { if (!_closed && !_closing) Notice(e is OperationCanceledException ? "连接超时，请检查服务器地址" : e.Message); }
    }
    private void Notice(string? text) { if (_closed) return; _banner.Text = text; _banner.IsVisible = !string.IsNullOrEmpty(text); }
    private async Task InitializeAsync()
    {
        _connection = SharedCredentials.Load(_localStore); if (_connection == null) return;
        _api = new(_connection.Endpoint, _connection.Login);
        try { _profile = await _api.Request<SharedProfile>("me", cancellation: _lifetime.Token); if (_profile.NeedsSetup) ShowProfileSetup(); else await EnterAsync(); }
        catch (SharedClientException e) when (e.Status == 401) { SharedCredentials.Clear(_localStore); _api.Dispose(); _api = null; ShowLogin(); }
    }
    private void ShowLogin()
    {
        _shell.ColumnDefinitions[0].Width = new(0); _heading.Text = "共享工作区"; _headerActions.Children.Clear();
        var form = new StackPanel { Width = 360, Spacing = 0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(24) };
        var mark = new Border { Width = 50, Height = 50, CornerRadius = new(15), Background = Ui.Ink, Margin = new(0, 0, 0, 24), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = "w·", FontSize = 34, FontFamily = new("Georgia"), Foreground = Ui.Surface, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        form.Children.Add(mark);
        form.Children.Add(new TextBlock { Text = "登录你的工作区", FontSize = 27, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 12) });
        form.Children.Add(new TextBlock { Text = "在网页与桌面，继续同一份工作。", FontSize = 14, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 30) });
        var endpoint = Input("SharedEndpoint", "http://192.168.1.109:18789", _localStore.Setting("shared_endpoint"));
        var username = Input("SharedUsername", "管理员为你开通的账号", _localStore.Setting("shared_username")); var password = Input("SharedPassword", "输入密码", password: true);
        form.Children.Add(Field("服务器地址", endpoint)); form.Children.Add(Field("登录账号", username)); form.Children.Add(Field("密码", password));
        async Task Login()
        {
            if (Composing(form)) return; var address = SharedProtocol.Endpoint(endpoint.Text ?? "").AbsoluteUri; var user = username.Text?.Trim() ?? ""; var secret = password.Text ?? ""; password.Text = "";
            var login = await SharedApiClient.LoginAsync(address, user, secret, _lifetime.Token); if (_closing) return;
            _api?.Dispose(); _api = new(address, login); _connection = new(address, user, login); SharedCredentials.Save(_localStore, _connection);
            _profile = await _api.Request<SharedProfile>("me", cancellation: _lifetime.Token); if (_profile.NeedsSetup) ShowProfileSetup(); else await EnterAsync();
        }
        var loginButton = ActionButton("登录  →", "SharedLogin", Login, true); loginButton.HorizontalAlignment = HorizontalAlignment.Stretch; form.Children.Add(loginButton);
        password.KeyDown += (_, e) => { if (e.Key == Key.Enter && !Composing(password)) { e.Handled = true; loginButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); } };
        form.Children.Add(new TextBlock { Text = "账号由管理员开通。网页与桌面使用同一账号。", Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 22, 0, 0) });
        _screen.Content = new ScrollViewer { Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private void ShowProfileSetup()
    {
        var form = new StackPanel { Width = 380, VerticalAlignment = VerticalAlignment.Center, Spacing = 8 };
        form.Children.Add(Label("设置你的个人名片", 25, null, true)); form.Children.Add(new TextBlock { Text = "伙伴通过 ID 找到你，评论中会显示你的名字。", Foreground = Ui.Muted, FontSize = 12, Margin = new(0, 5, 0, 24) });
        var display = Input("SharedDisplayName", "你的名字", _profile?.DisplayName); var publicId = Input("SharedPublicId", "your_name", _profile?.PublicId); var password = Input("SharedNewPassword", "设置仅自己知道的密码", password: true);
        form.Children.Add(Field("你的名字", display)); form.Children.Add(Field("个人 ID", publicId, "3–32 位字母、数字、下划线或短横线")); form.Children.Add(Field("设置新密码", password, "至少 12 个字符，可使用一句容易记住的短语"));
        var submit = ActionButton("进入工作区  →", "SharedCompleteProfile", async () =>
        {
            if (Composing(form)) return; _profile = await _api!.Request<SharedProfile>("profile", "POST", new SharedProfileSetup(publicId.Text ?? "", display.Text ?? "", password.Text ?? ""), _lifetime.Token); password.Text = ""; await EnterAsync();
        }, true); submit.HorizontalAlignment = HorizontalAlignment.Stretch; form.Children.Add(submit); _screen.Content = form;
    }
    private async Task EnterAsync()
    {
        _workspaces = await _api!.Request<SharedWorkspace[]>("workspaces", cancellation: _lifetime.Token); if (_closing) return;
        _workspace = _workspaces.FirstOrDefault(item => item.Id == _localStore.Setting("shared_workspace")) ?? _workspaces.FirstOrDefault();
        _shell.ColumnDefinitions[0].Width = new(284); _refreshTimer.Start(); RefreshNavigation();
        if (_workspace != null) await RefreshDocumentsAsync(); ShowHome();
    }
    private void RefreshNavigation()
    {
        _navigation.Children.Clear(); _accountArea.Children.Clear();
        var picker = new ComboBox { ItemsSource = _workspaces.Select(item => item.Name).ToArray(), SelectedIndex = _workspace == null ? -1 : Array.FindIndex(_workspaces, item => item.Id == _workspace.Id), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "选择工作区", FontSize = 13, FontWeight = FontWeight.Medium, MinHeight = 38, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(8, 6) };
        AutomationProperties.SetAutomationId(picker, "SharedWorkspacePicker");
        picker.SelectionChanged += async (_, _) => { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < _workspaces.Length && _workspace?.Id != _workspaces[picker.SelectedIndex].Id) await RunAsync(async () => { if (!await SaveDraftAsync()) return; await StopDocumentAsync(); _workspace = _workspaces[picker.SelectedIndex]; _localStore.SetSetting("shared_workspace", _workspace.Id); _trash = false; await RefreshDocumentsAsync(); RefreshNavigation(); ShowHome(); }); };
        var spaceRow = new Grid { ColumnDefinitions = new("28,*,Auto"), Margin = new(4, 0, 0, 12) };
        spaceRow.Children.Add(new Border { Width = 26, Height = 26, CornerRadius = new(7), Background = Ui.Chrome("#DCE7FF"), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = System.Globalization.StringInfo.GetNextTextElement(_workspace?.Name ?? "工作"), FontSize = 12, Foreground = Ui.Chrome("#365E9D"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        Grid.SetColumn(picker, 1); spaceRow.Children.Add(picker);
        var addSpace = ActionButton("＋", "SharedCreateWorkspace", CreateWorkspaceAsync); ToolTip.SetTip(addSpace, "创建工作区"); Grid.SetColumn(addSpace, 2); spaceRow.Children.Add(addSpace); _navigation.Children.Add(spaceRow);
        var search = new TextBox { Text = _searchText, Watermark = "搜索文档", FontSize = 13, Padding = new(10, 8), Background = Brushes.Transparent, BorderThickness = new(0), CornerRadius = new(8) };
        AutomationProperties.SetAutomationId(search, "SharedSearch"); search.TextChanged += (_, _) => { _searchText = search.Text ?? ""; RefreshDocumentItems(); }; _navigation.Children.Add(search);
        if (_workspace != null && _workspace.Role != "viewer") _navigation.Children.Add(NavigationButton("新建文档", "SharedNewDocument", SidebarSymbol.Plus, CreateDocumentAsync));
        _navigation.Children.Add(NavigationButton("所有文档", "SharedDocuments", SidebarSymbol.Document, async () => { if (!await SaveDraftAsync()) return; await StopDocumentAsync(); _trash = false; await RefreshDocumentsAsync(); ShowHome(); }));
        if (_workspace != null) _navigation.Children.Add(NavigationButton("工作区成员", "SharedMembers", SidebarSymbol.Folder, MembersAsync));
        var row = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(7, 24, 0, 6) }; row.Children.Add(Label(_trash ? "回收站" : "工作区文档", 10, Ui.Muted));
        if (_workspace != null && SharedProtocol.CanWrite(_workspace.Role)) { var add = ActionButton("＋", "SharedAddDocument", CreateDocumentAsync); Grid.SetColumn(add, 1); row.Children.Add(add); }
        _navigation.Children.Add(row); RefreshDocumentItems();
        _accountArea.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new(0, 25, 0, 12) });
        _accountArea.Children.Add(NavigationButton("回收站", "SharedTrash", SidebarSymbol.Trash, async () => { if (!await SaveDraftAsync()) return; await StopDocumentAsync(); _trash = true; await RefreshDocumentsAsync(); RefreshNavigation(); ShowHome(); }));
        var account = new Grid { ColumnDefinitions = new("42,*,Auto"), Margin = new(4, 10, 0, 0) };
        account.Children.Add(new Border { Width = 31, Height = 31, CornerRadius = new(16), Background = Ui.Chrome("#ECEFFD"), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = System.Globalization.StringInfo.GetNextTextElement(_profile?.DisplayName ?? "我"), FontSize = 12, Foreground = Ui.Chrome("#607DB3"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var identity = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center }; identity.Children.Add(Label(_profile?.DisplayName ?? "", 13)); identity.Children.Add(Label("@" + _profile?.PublicId, 10, Ui.Muted)); Grid.SetColumn(identity, 1); account.Children.Add(identity);
        var logout = ActionButton("退出", "SharedLogout", async () =>
        {
            if (!await SaveDraftAsync()) return; await StopDocumentAsync(); await _api!.Request<object>("logout", "POST", cancellation: _lifetime.Token);
            SharedCredentials.Clear(_localStore); _api.Dispose(); _api = null; _profile = null; _connection = null; _refreshTimer.Stop(); ShowLogin();
        }); ToolTip.SetTip(logout, "退出登录"); Grid.SetColumn(logout, 2); account.Children.Add(logout); _accountArea.Children.Add(account);
    }
    private async Task RefreshDocumentsAsync()
    {
        var workspace = _workspace; var trash = _trash; if (workspace == null || _api == null) return;
        var documents = await _api.Request<SharedDocumentInfo[]>($"workspaces/{workspace.Id}/documents" + (trash ? "?trash=true" : ""), cancellation: _lifetime.Token);
        if (_closing || _workspace?.Id != workspace.Id || _trash != trash) return;
        _documents = documents; RefreshDocumentItems();
    }
    private void RefreshDocumentItems()
    {
        _documentItems.Children.Clear();
        foreach (var document in _documents.Where(document => Display(document.Title).Contains(_searchText, StringComparison.OrdinalIgnoreCase)))
        {
            var button = ActionButton(_trash ? Display(document.Title) + " · 恢复" : Display(document.Id == _sharedInfo?.Id && _shared != null ? _shared.Title : document.Title), "SharedDocument_" + document.Id, async () =>
            {
                if (_trash) { await _api!.Request<object>($"shared/{document.Id}/restore", "POST", cancellation: _lifetime.Token); await RefreshDocumentsAsync(); }
                else await OpenDocumentAsync(document.Id);
            }); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            content.Children.Add(new SidebarGlyph(SidebarSymbol.Document, 15) { VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = button.Content as string, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 185, VerticalAlignment = VerticalAlignment.Center }); button.Content = content;
            if (_sharedInfo?.Id == document.Id) { button.Classes.Add("libraryNav"); button.Classes.Add("active"); button.Foreground = Ui.Chrome("#365E9D"); }
            if (_trash && _workspace?.Role == "viewer") button.IsEnabled = false;
            _documentItems.Children.Add(button);
        }
        if (_documents.Length == 0) _documentItems.Children.Add(new TextBlock { Text = _trash ? "回收站是空的" : "从第一篇文档开始", Foreground = Ui.Muted, FontSize = 11, Margin = new(8, 10) });
    }
    private static string Display(string title) => string.IsNullOrWhiteSpace(title) ? "无标题" : title;
    private void ShowHome()
    {
        _heading.Text = _workspace?.Name ?? "我的工作台"; _headerActions.Children.Clear();
        var panel = new StackPanel { Margin = new(64, 58), Spacing = 20 };
        panel.Children.Add(Label(_workspace?.Name ?? "我的工作台", 12, Ui.Muted)); panel.Children.Add(Label(_trash ? "回收站" : _workspace == null ? "你好，" + _profile?.DisplayName : "所有文档", 31, Ui.Ink, true));
        panel.Children.Add(Label(_workspace == null ? "创建一个工作区，或请伙伴用你的个人 ID 添加你。" : "你的文档，以及正在一起完成的工作。", 13, Ui.Muted));
        var create = ActionButton(_workspace == null ? "＋ 创建工作区" : "＋ 新建文档", "SharedHomeCreate", _workspace == null ? CreateWorkspaceAsync : CreateDocumentAsync, true); create.HorizontalAlignment = HorizontalAlignment.Left; create.IsEnabled = _workspace?.Role != "viewer"; panel.Children.Add(create);
        var cards = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 18, 0, 0) };
        foreach (var doc in _documents.Where(_ => !_trash))
        {
            var body = new StackPanel { Spacing = 18, Margin = new(20) }; body.Children.Add(new SidebarGlyph(SidebarSymbol.Document, 22) { HorizontalAlignment = HorizontalAlignment.Left }); body.Children.Add(Label(Display(doc.Title), 15, null, true)); body.Children.Add(Label(DateTimeOffset.FromUnixTimeMilliseconds(doc.UpdatedAt).LocalDateTime.ToString("M月d日") + "更新", 10, Ui.Muted));
            var card = ActionButton(Display(doc.Title), "SharedCard_" + doc.Id, () => OpenDocumentAsync(doc.Id)); card.Content = body; card.Width = 226; card.MinHeight = 158; card.Padding = new(0); card.Margin = new(0, 0, 16, 16); card.Background = Ui.Surface; card.BorderBrush = Ui.Line; card.BorderThickness = new(1); card.CornerRadius = new(12); card.HorizontalContentAlignment = HorizontalAlignment.Stretch; cards.Children.Add(card);
        }
        panel.Children.Add(cards); _screen.Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    private async Task<string[]?> FormAsync(string title, string description, params (string Label, string Placeholder, bool Password)[] fields)
    {
        var body = new StackPanel { Margin = new(28), Spacing = 6 }; body.Children.Add(Label(title, 22, null, true)); body.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 23) });
        var inputs = fields.Select((field, i) => Input("SharedForm" + i, field.Placeholder, password: field.Password)).ToArray(); for (var i = 0; i < inputs.Length; i++) body.Children.Add(Field(fields[i].Label, inputs[i]));
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body, Background = Ui.Surface };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        actions.Children.Add(ActionButton("取消", "SharedFormCancel", () => { dialog.Close(); return Task.CompletedTask; }));
        actions.Children.Add(ActionButton("确认", "SharedFormSubmit", () => { if (!Composing(body) && inputs.All(input => !string.IsNullOrWhiteSpace(input.Text))) dialog.Close(inputs.Select(input => input.Text!).ToArray()); return Task.CompletedTask; }, true)); body.Children.Add(actions);
        dialog.Opened += (_, _) => inputs.FirstOrDefault()?.Focus(); return await dialog.ShowDialog<string[]?>(this);
    }
    private async Task<bool> ConfirmAsync(string title, string description, string accept)
    {
        var body = new StackPanel { Margin = new(28), Spacing = 18 };
        body.Children.Add(Label(title, 21, null, true)); body.Children.Add(Label(description, 13, Ui.Muted));
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = body, Background = Ui.Surface };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = ActionButton("取消", "SharedConfirmCancel", () => { dialog.Close(false); return Task.CompletedTask; }); actions.Children.Add(cancel);
        actions.Children.Add(ActionButton(accept, "SharedConfirmAccept", () => { dialog.Close(true); return Task.CompletedTask; }, true)); body.Children.Add(actions);
        dialog.Opened += (_, _) => cancel.Focus(); return await dialog.ShowDialog<bool>(this);
    }
    private async Task CreateWorkspaceAsync()
    {
        var values = await FormAsync("创建工作区", "用项目、团队或一件正在做的事来命名。", ("工作区名称", "例如：产品设计小组", false)); if (values == null) return;
        if (!await SaveDraftAsync()) return; var created = await _api!.Request<SharedWorkspace>("workspaces", "POST", new SharedWorkspaceInput(values[0]), _lifetime.Token);
        await StopDocumentAsync(); _workspaces = [.. _workspaces, created]; _workspace = created; _documents = []; _trash = false; RefreshNavigation(); ShowHome();
    }
    private async Task CreateDocumentAsync()
    {
        if (_workspace == null) return; if (!await SaveDraftAsync()) return;
        var doc = await _api!.Request<SharedDocumentData>($"workspaces/{_workspace.Id}/documents", "POST", new SharedDocumentInput(""), _lifetime.Token); _trash = false; await RefreshDocumentsAsync(); await OpenDocumentAsync(doc.Document.Id);
    }
    private async Task MembersAsync()
    {
        if (_workspace == null) return; var workspace = _workspace;
        var panel = new StackPanel { Spacing = 14, Margin = new(26) }; panel.Children.Add(Label("工作区成员", 23, null, true)); panel.Children.Add(Label(workspace.Name, 12, Ui.Muted));
        var list = new StackPanel { Spacing = 8 }; var notice = Label("", 11, Ui.Chrome("#A36345"));
        async Task Refresh()
        {
            list.Children.Clear(); foreach (var member in await _api!.Request<SharedMember[]>($"workspaces/{workspace.Id}/members", cancellation: _lifetime.Token))
            {
                var row = new Grid { ColumnDefinitions = new("*,Auto,Auto"), Margin = new(0, 6) }; var name = new StackPanel { Spacing = 5 }; name.Children.Add(Label(member.DisplayName, 13, null, true)); name.Children.Add(Label("@" + member.PublicId, 10, Ui.Muted)); row.Children.Add(name);
                var role = new ComboBox { ItemsSource = new[] { "所有者", "可编辑", "仅阅读" }, SelectedIndex = Array.IndexOf(new[] { "owner", "editor", "viewer" }, member.Role), FontSize = 11, IsEnabled = workspace.Role == "owner", MinWidth = 92 };
                Grid.SetColumn(role, 1); row.Children.Add(role);
                role.SelectionChanged += async (_, _) => { if (role.SelectedIndex < 0) return; try { await _api.Request<object>($"workspaces/{workspace.Id}/members", "PUT", new SharedMemberInput(member.PublicId, new[] { "owner", "editor", "viewer" }[role.SelectedIndex]), _lifetime.Token); notice.Text = "权限已更新"; } catch (Exception e) when (IsConnectionError(e)) { notice.Text = e.Message; } };
                if (workspace.Role == "owner") { var remove = ActionButton("移除", "SharedRemoveMember_" + member.AccountId, async () => { try { await _api.Request<object>($"workspaces/{workspace.Id}/members/{member.AccountId}", "DELETE", cancellation: _lifetime.Token); await Refresh(); } catch (Exception e) when (IsConnectionError(e)) { notice.Text = e.Message; } }); Grid.SetColumn(remove, 2); row.Children.Add(remove); }
                list.Children.Add(row);
            }
        }
        if (workspace.Role == "owner")
        {
            var id = Input("SharedInviteId", "@ 对方的个人 ID"); var role = new ComboBox { ItemsSource = new[] { "可编辑和评论", "仅阅读", "所有者" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            panel.Children.Add(Field("添加成员", id)); panel.Children.Add(role);
            panel.Children.Add(ActionButton("添加成员", "SharedInviteMember", async () => { try { await _api!.Request<object>($"workspaces/{workspace.Id}/members", "PUT", new SharedMemberInput(id.Text ?? "", new[] { "editor", "viewer", "owner" }[role.SelectedIndex]), _lifetime.Token); id.Text = ""; await Refresh(); } catch (Exception e) when (IsConnectionError(e)) { notice.Text = e.Message; } }, true));
        }
        panel.Children.Add(notice); panel.Children.Add(list); await Refresh();
        var dialog = new Window { Title = "工作区成员", Width = 510, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        await dialog.ShowDialog(this);
    }
}
