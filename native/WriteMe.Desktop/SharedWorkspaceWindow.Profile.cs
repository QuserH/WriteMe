using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using WriteMe.Core;

namespace WriteMe.Desktop;

public sealed partial class SharedWorkspaceWindow
{
    private SharedAvatars? _avatars;
    private SharedMember[] _people = [];
    private Control AccountAvatar(string account, string name, SharedAvatar? avatar, double size = 30)
        => (_avatars ??= new(_api!, _lifetime.Token)).Create(account, name, avatar, size);
    private Control MessageAvatar(CommentMessage message, double size = 26)
    {
        var person = _people.FirstOrDefault(person => person.AccountId == message.AuthorId);
        var own = _profile is { } profile && message.AuthorId == profile.Id ? profile : null;
        return AccountAvatar(message.AuthorId ?? "", own?.DisplayName ?? person?.DisplayName ?? message.Author, own?.Avatar ?? person?.Avatar, size);
    }
    private string MessageAuthor(CommentMessage message) => _profile is { } profile && message.AuthorId == profile.Id ? profile.DisplayName : _people.FirstOrDefault(person => person.AccountId == message.AuthorId)?.DisplayName ?? message.Author;
    private void ApplyProfile(SharedProfile profile)
    {
        if (_closed || _closing || profile.Id != _profile?.Id) return;
        _profile = profile; _syncPaused = profile.SyncPaused;
        if (_connection != null) { _connection = _connection with { Username = profile.Username }; SharedCredentials.Save(_localStore, _connection); }
        if (_shared != null) _shared.Session.CommentAuthor = profile.DisplayName;
        RefreshNavigation(); RefreshSharedUi(); _comments?.Refresh(true); _editor?.RefreshAccountComments(); if (_shared == null) ShowHome();
    }
    private async Task RefreshPeopleAsync()
    {
        var api = _api; var workspace = _workspace; if (api == null || _closing) return;
        var profile = await api.Request<SharedProfile>("me", cancellation: _lifetime.Token);
        if (_closing || _api != api) return;
        if (profile != _profile) ApplyProfile(profile);
        if (workspace == null) return;
        var people = await api.Request<SharedMember[]>("workspaces/" + workspace.Id + "/members", cancellation: _lifetime.Token);
        if (_closing || _workspace?.Id != workspace.Id || _api != api || _people.SequenceEqual(people)) return;
        _people = people; _comments?.Refresh(true); _editor?.RefreshAccountComments();
    }
    private async Task ProfileSettingsAsync()
    {
        if (_profile == null || _api == null) return;
        var profile = _profile; var api = _api; var closed = false;
        var layout = new Grid { RowDefinitions = new("46,*") };
        var frame = new Border { Child = layout, Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(10), ClipToBounds = true };
        var dialog = new Window { Title = "个人设置", Width = 500, Height = 720, MinWidth = 440, MinHeight = 540, Background = Ui.Surface,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = frame, ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome, ExtendClientAreaTitleBarHeightHint = 0 };
        AutomationProperties.SetAutomationId(dialog, "SharedProfileSettings");
        var header = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(22, 0, 0, 0) }; header.Children.Add(Label("个人设置", 13, Ui.Muted));
        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top }; Grid.SetColumn(captions, 1); header.Children.Add(captions); layout.Children.Add(header); WindowChrome.Attach(dialog, header, captions, frame);
        var body = new StackPanel { Margin = new(32, 20, 32, 30), Spacing = 12 }; body.Children.Add(Label("你的个人名片", 24, Ui.Ink, true)); body.Children.Add(Label("让伙伴通过头像、名字和 ID 认出你。", 12, Ui.Muted));
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; body.Children.Add(new Border { Child = tabs, BorderBrush = Ui.Line, BorderThickness = new(0, 0, 0, 1), Margin = new(0, 6, 0, 12) });
        var content = new ContentControl(); body.Children.Add(content);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        var details = new StackPanel(); var credentials = new StackPanel(); content.Content = details;
        var tabProfile = ActionButton("头像与资料", "SharedProfileTab", () => { content.Content = details; return Task.CompletedTask; });
        var tabPassword = ActionButton("修改密码", "SharedPasswordTab", () => { content.Content = credentials; return Task.CompletedTask; });
        tabProfile.Classes.Add("sharedProfileTab"); tabPassword.Classes.Add("sharedProfileTab");
        void RefreshTabs()
        {
            tabProfile.Classes.Remove("selected"); tabPassword.Classes.Remove("selected");
            (ReferenceEquals(content.Content, details) ? tabProfile : tabPassword).Classes.Add("selected");
        }
        tabProfile.Click += (_, _) => RefreshTabs(); tabPassword.Click += (_, _) => RefreshTabs(); RefreshTabs();
        tabs.Children.Add(tabProfile); tabs.Children.Add(tabPassword);
        var color = profile.Avatar?.Color ?? "#6C82AD"; var removeImage = false; Bitmap? picture = null;
        var crop = new AvatarCrop(); AutomationProperties.SetAutomationId(crop, "SharedAvatarCrop");
        var preview = new ContentControl { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Left };
        var avatarText = Input("SharedAvatarText", "文字、字母或表情", profile.Avatar?.Text); avatarText.MaxLength = 8;
        void Preview()
        {
            preview.Content = picture != null ? crop : AccountAvatar(profile.Id, profile.DisplayName, new(color, avatarText.Text ?? "", removeImage ? null : profile.Avatar?.ImageVersion), 100);
        }
        Preview(); avatarText.TextChanged += (_, _) => Preview();
        var avatarRow = new Grid { ColumnDefinitions = new("120,*"), Margin = new(0, 4, 0, 18) }; avatarRow.Children.Add(preview);
        var actions = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 1); avatarRow.Children.Add(actions);
        var notice = Label("", 12, Ui.Chrome("#547AB7")); notice.Margin = new(0, 5, 0, 16);
        var zoom = new Slider { Minimum = 1, Maximum = 3, Value = 1, IsVisible = false, Margin = new(0, 0, 0, 12) }; AutomationProperties.SetAutomationId(zoom, "SharedAvatarZoom");
        zoom.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) crop.Zoom = zoom.Value; };
        actions.Children.Add(ActionButton("上传头像", "SharedUploadAvatar", async () =>
        {
            try
            {
                var files = await dialog.StorageProvider.OpenFilePickerAsync(new() { Title = "选择头像", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"] }] });
                if (closed || files.Count == 0) return;
                await using var file = await files[0].OpenReadAsync(); var bytes = await SyncProtocol.ReadLimitedAsync(file, 10 * 1024 * 1024, _lifetime.Token);
                if (closed) return; using var memory = new MemoryStream(bytes); var bitmap = new Bitmap(memory);
                if ((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > 32_000_000) { bitmap.Dispose(); throw new ArgumentException("图片尺寸过大，请缩小后再上传"); }
                crop.Picture = bitmap; picture?.Dispose(); picture = bitmap; zoom.Value = 1; crop.Zoom = 1; zoom.IsVisible = true; removeImage = false; Preview(); crop.InvalidateVisual(); notice.Text = "拖动头像调整位置，下方滑块可以缩放。";
            }
            catch (Exception e) when (IsConnectionError(e)) { if (!closed) notice.Text = e.Message; }
        }));
        actions.Children.Add(ActionButton("使用文字头像", "SharedTextAvatar", () => { removeImage = true; crop.Picture = null; picture?.Dispose(); picture = null; zoom.IsVisible = false; Preview(); return Task.CompletedTask; }));
        details.Children.Add(avatarRow); details.Children.Add(zoom); details.Children.Add(Field("头像文字", avatarText));
        var colors = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 19) };
        foreach (var swatch in new[] { "#6C82AD", "#497BE0", "#8170AE", "#B17391", "#B7845D", "#9C795E", "#59677C", "#343A43" })
        {
            var choice = new Button { Width = 27, Height = 27, Padding = new(0), Background = Brush.Parse(swatch), CornerRadius = new(14), Margin = new(0, 0, 12, 0), BorderThickness = new(2), BorderBrush = Brushes.White };
            AutomationProperties.SetAutomationId(choice, "SharedAvatarColor_" + swatch[1..]); ToolTip.SetTip(choice, "头像颜色 " + swatch); choice.Click += (_, _) => { color = swatch; Preview(); }; colors.Children.Add(choice);
        }
        details.Children.Add(colors);
        var display = Input("SharedEditDisplayName", "你的名字", profile.DisplayName); display.MaxLength = 40;
        var publicId = Input("SharedEditPublicId", "个人 ID", profile.PublicId); publicId.MaxLength = 32;
        details.Children.Add(Field("你的名字", display)); details.Children.Add(Field("个人 ID", publicId, "伙伴使用这个 ID 将你加入工作区")); details.Children.Add(notice);
        var save = ActionButton("保存个人资料", "SharedSaveProfile", async () =>
        {
            if (Composing(details)) return;
            try
            {
                var change = new SharedProfileChange(publicId.Text ?? "", display.Text ?? "", new(color, avatarText.Text ?? "", picture == null ? null : crop.Export(), removeImage));
                var next = await api.Request<SharedProfile>("profile", "PATCH", change, _lifetime.Token);
                if (_api == api) ApplyProfile(next); if (!closed) { profile = next; Preview(); notice.Text = "个人资料已保存"; }
            }
            catch (Exception e) when (IsConnectionError(e)) { if (!closed) notice.Text = e.Message; }
        }, true); save.HorizontalAlignment = HorizontalAlignment.Stretch; details.Children.Add(save);
        credentials.Children.Add(Label("登录账号  " + profile.Username, 13, Ui.Muted));
        var current = Input("SharedCurrentPassword", "当前密码", password: true);
        var password = Input("SharedChangePassword", "新密码", password: true);
        var confirm = Input("SharedConfirmPassword", "再次输入新密码", password: true);
        credentials.Children.Add(new Border { Height = 18 }); credentials.Children.Add(Field("当前密码", current)); credentials.Children.Add(Field("新密码", password, "至少 6 个字符")); credentials.Children.Add(Field("确认新密码", confirm));
        var passwordNotice = Label("", 12, Ui.Chrome("#547AB7")); passwordNotice.Margin = new(0, 0, 0, 14); credentials.Children.Add(passwordNotice);
        credentials.Children.Add(ActionButton("更新密码", "SharedSavePassword", async () =>
        {
            if (Composing(credentials)) return;
            try
            {
                AccountProfiles.ValidatePassword(password.Text);
                if (password.Text != confirm.Text) throw new ArgumentException("两次输入的新密码不一致");
                var next = await api.Request<SharedProfile>("profile/password", "POST", new SharedPasswordChange(current.Text ?? "", password.Text ?? ""), _lifetime.Token);
                if (_api == api) ApplyProfile(next);
                if (!closed) { current.Text = password.Text = confirm.Text = ""; passwordNotice.Text = "密码已更新，其他设备需要重新登录"; }
            }
            catch (Exception e) when (IsConnectionError(e)) { if (!closed) passwordNotice.Text = e.Message; }
        }, true));
        dialog.Closed += (_, _) => { closed = true; crop.Picture = null; picture?.Dispose(); picture = null; };
        await dialog.ShowDialog(this);
    }
}
