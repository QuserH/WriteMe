using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WriteMe.Tests;

public sealed class SharedWindowTests
{
    private const string Password = "test-only-password-26";
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Frame(Window window) { Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window); var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static async Task Wait(Func<bool> condition)
    { for (var i = 0; i < 400 && !condition(); i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); } Assert.True(condition()); }
    private static bool Has(Visual root, string id) => root.GetVisualDescendants().OfType<Control>().Any(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Image(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path); Frame(window); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(path, name));
    }
    private static string SourceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "package.json"))) return directory.FullName;
        throw new InvalidOperationException("找不到 WriteME 源码目录");
    }
    [AvaloniaFact]
    public async Task NativeProfileDesignerAndSixCharacterPasswordKeepTheActiveEditorAndSession()
    {
        using var temporary = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temporary.Path, "server")); var repository = host.Repository;
        var initial = repository.Login(new("owner", Password))!; var owner = repository.SetupProfile(initial.AccountId, new("profile-owner", "林然", Password));
        var workspace = repository.CreateWorkspace(owner.Id, "头像与个人资料"); var document = repository.CreateSharedDocument(owner.Id, workspace.Id, "编辑中的文档");
        using var local = new NoteStore(Path.Combine(temporary.Path, "device")); var window = new SharedWorkspaceWindow(local); window.Show(); Frame(window);
        try
        {
            Find<TextBox>(window, "SharedEndpoint").Text = host.Endpoint.AbsoluteUri; Find<TextBox>(window, "SharedUsername").Text = "owner"; Find<TextBox>(window, "SharedPassword").Text = Password;
            Click(window, Find<Button>(window, "SharedLogin")); await Wait(() => Has(window, "SharedDocument_" + document.Document.Id));
            Click(window, Find<Button>(window, "SharedDocument_" + document.Document.Id)); await Wait(() => Has(window, "SharedBlockEditor"));
            var editor = Find<BlockEditor>(window, "SharedBlockEditor"); editor.FocusText(); window.KeyTextInput("修改个人资料时，继续保留正在编辑的内容。"); Frame(window);
            await Wait(() => Find<TextBlock>(window, "SharedSaveStatus").Text == "所有更改已保存");
            var session = editor.Session; var credential = SharedCredentials.Load(local)!;
            Click(window, Find<Button>(window, "SharedProfileSettingsButton")); await Wait(() => window.OwnedWindows.Count == 1);
            var settings = window.OwnedWindows.Single(); Frame(settings);
            Find<TextBox>(settings, "SharedAvatarText").Text = "林";
            Click(settings, Find<Button>(settings, "SharedAvatarColor_8170AE"));
            Find<TextBox>(settings, "SharedEditDisplayName").Text = "林然的工作台";
            Find<TextBox>(settings, "SharedEditPublicId").Text = "new-profile-id";
            Click(settings, Find<Button>(settings, "SharedSaveProfile"));
            await Wait(() => repository.Profile(owner.Id).DisplayName == "林然的工作台");
            await Wait(() => Find<Button>(settings, "SharedSaveProfile").IsEnabled);
            Assert.Equal("new-profile-id", repository.Profile(owner.Id).PublicId); Assert.Equal("#8170AE", repository.Profile(owner.Id).Avatar!.Color); Assert.Equal("林", repository.Profile(owner.Id).Avatar!.Text);
            Image(settings, "native-profile-designer.png");
            Click(settings, Find<Button>(settings, "SharedPasswordTab"));
            Find<TextBox>(settings, "SharedCurrentPassword").Text = Password; Find<TextBox>(settings, "SharedChangePassword").Text = "654321"; Find<TextBox>(settings, "SharedConfirmPassword").Text = "654321";
            Click(settings, Find<Button>(settings, "SharedSavePassword"));
            await Wait(() => string.IsNullOrEmpty(Find<TextBox>(settings, "SharedCurrentPassword").Text));
            Assert.Null(repository.Authenticate(initial.Token)); Assert.Equal(owner.Id, repository.Authenticate(credential.Login.Token));
            Assert.Null(repository.Login(new("owner", Password))); Assert.NotNull(repository.Login(new("owner", "654321")));
            Image(settings, "native-profile-password.png"); settings.Close(); Frame(window);
            Assert.Same(session, editor.Session); Assert.Contains("修改个人资料时", editor.Session.Projection.Text);
            editor.Session.AddComment("修改资料后继续评论", NoteComments.CaptureBlock(editor.Session, editor.Session.Projection.Rows[0].Node.Id)); Frame(window);
            await Wait(() => Find<TextBlock>(window, "SharedSaveStatus").Text == "所有更改已保存");
            using var saved = new SharedDocumentReplica(repository.SharedDocument(owner.Id, document.Document.Id).State);
            Assert.Equal("林然的工作台", Assert.Single(NoteComments.For(saved.Read().Root).Threads).Messages[0].Author);
            editor.OpenParagraphComments(editor.Session.Projection.Rows[0].Node.Id); Frame(window); Image(window, "native-profile-paragraph.png");
        }
        finally { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); await Wait(() => !window.IsVisible); }
    }
    private sealed class BrowserPeer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _errors;
        public BrowserPeer(string root)
        {
            var start = new ProcessStartInfo("node") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("tests/shared/native-peer.mjs"); _process = Process.Start(start)!; _errors = _process.StandardError.ReadToEndAsync();
        }
        public async Task<JsonElement> Send(object command)
        {
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, SyncProtocol.Json)); await _process.StandardInput.FlushAsync();
            var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(35));
            if (line == null) throw new InvalidOperationException("浏览器协同验证异常退出：" + await _errors);
            using var result = JsonDocument.Parse(line); Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.TryGetProperty("error", out var error) ? error.GetString() : line);
            return result.RootElement.Clone();
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!_process.HasExited) { await Send(new { operation = "quit" }); await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
            catch (TimeoutException) { /* The owned browser process is terminated below; do not mask an earlier assertion. */ }
            finally { if (!_process.HasExited) _process.Kill(entireProcessTree: true); _process.Dispose(); }
        }
    }
    [AvaloniaFact]
    [Trait("Category", "SharedBrowser")]
    public async Task BrowserAndNativeEditorConvergeUnicodeFormattingUndoRepliesAndReconnect()
    {
        var root = SourceRoot(); Assert.True(File.Exists(Path.Combine(root, "dist", "index.html")), "混合端回归需要先运行 npm run build 和 npx playwright install chromium");
        using var temporary = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temporary.Path, "server"), Path.Combine(root, "dist")); var r = host.Repository;
        var login = r.Login(new("owner", Password))!; var owner = r.SetupProfile(login.AccountId, new("native", "原生用户", Password));
        var peer = r.AddAccount(owner.Id, new("browser", Password)); r.SetupProfile(peer.Id, new("browser", "网页用户", Password));
        var space = r.CreateWorkspace(owner.Id, "双端共同编辑"); r.SetMember(owner.Id, space.Id, new("browser")); var data = r.CreateSharedDocument(owner.Id, space.Id, "双端协同回归");
        using (var seed = new SharedEditingSession(new(data.State), owner.DisplayName, owner.Id))
        { seed.Session.Edit(0, 0, "你好，共同编辑🙂", false); seed.Session.AddComment("从原生段落开始讨论", NoteComments.CaptureBlock(seed.Session, seed.Session.Projection.Rows[0].Node.Id)); r.ApplySharedUpdate(owner.Id, data.Document.Id, seed.Replica.State()); }
        using var local = new NoteStore(Path.Combine(temporary.Path, "native")); var window = new SharedWorkspaceWindow(local); window.Show(); Frame(window);
        await using var browser = new BrowserPeer(root);
        try
        {
            Find<TextBox>(window, "SharedEndpoint").Text = host.Endpoint.AbsoluteUri; Find<TextBox>(window, "SharedUsername").Text = "owner"; Find<TextBox>(window, "SharedPassword").Text = Password;
            Click(window, Find<Button>(window, "SharedLogin")); await Wait(() => Has(window, "SharedDocument_" + data.Document.Id));
            Click(window, Find<Button>(window, "SharedDocument_" + data.Document.Id)); await Wait(() => Has(window, "SharedBlockEditor"));
            await Wait(() => Find<TextBlock>(window, "SharedSaveStatus").Text == "所有更改已保存");
            await browser.Send(new { operation = "open", endpoint = host.Endpoint.AbsoluteUri, username = "browser", password = Password, title = data.Document.Title });
            var editor = Find<BlockEditor>(window, "SharedBlockEditor");
            await browser.Send(new { operation = "offline", value = true });
            await browser.Send(new { operation = "insert", offset = 2, text = "网页🙂", italic = true });
            editor.FocusText(); editor.Surface.CaretOffset = 2; window.KeyTextInput("桌面"); Frame(window); editor.Session.Format(2, 2, new("bold"));
            await browser.Send(new { operation = "offline", value = false });
            await browser.Send(new { operation = "expect", contains = new[] { "网页🙂", "桌面", "共同编辑🙂" }, bold = "桌面", italic = "网页🙂" });
            await Wait(() => editor.Session.Projection.Text.Contains("网页🙂"));
            Assert.Contains(NoteTree.Descendants(editor.Session.Root), node => node.Text.Contains("网页") && node.Marks.Any(mark => mark.Type == "italic"));
            await browser.Send(new { operation = "undo" }); await browser.Send(new { operation = "expect", contains = new[] { "桌面" }, absent = new[] { "网页🙂" } });
            await Wait(() => !editor.Session.Projection.Text.Contains("网页🙂"));
            await browser.Send(new { operation = "reply", parent = "从原生段落开始讨论", text = "网页回复原生段落" });
            await Wait(() => NoteComments.For(editor.Session.Root).Threads[0].Messages.Length == 2);
            var thread = NoteComments.For(editor.Session.Root).Threads[0]; editor.Session.ReplyComment(thread, "原生再回复网页的回复", thread.Messages[1].Id);
            await browser.Send(new { operation = "comments", contains = new[] { "网页回复原生段落", "原生再回复网页的回复" } });
            await browser.Send(new { operation = "offline", value = true }); await browser.Send(new { operation = "insert", end = true, text = "离线草稿恢复" });
            await browser.Send(new { operation = "offline", value = false }); await browser.Send(new { operation = "expect", contains = new[] { "离线草稿恢复" } });
            await Wait(() => editor.Session.Projection.Text.Contains("离线草稿恢复"));
            await browser.Send(new { operation = "reload" }); await browser.Send(new { operation = "expect", contains = new[] { "桌面", "离线草稿恢复" } });
            Click(window, Find<Button>(window, "SharedComments")); Image(window, "shared-mixed-native.png");
            await Wait(() => Find<TextBlock>(window, "SharedSaveStatus").Text == "所有更改已保存");
            using var saved = new SharedDocumentReplica(r.SharedDocument(owner.Id, data.Document.Id).State);
            Assert.Equal(editor.Session.Projection.Text, new DocumentProjection(saved.Read().Root).Text);
            Assert.Equal(3, NoteComments.For(saved.Read().Root).Threads[0].Messages.Length);
        }
        finally { window.Close(); await Wait(() => !window.IsVisible); }
    }
    [AvaloniaFact]
    public async Task TwoNativeAccountsEditAndReplyThroughSharedWindowsWithoutUploadingPersonalNotes()
    {
        using var temporary = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temporary.Path, "server")); var r = host.Repository;
        var admin = r.Login(new("owner", Password))!; var profile = r.SetupProfile(admin.AccountId, new("linran", "林然", Password));
        var member = r.AddAccount(profile.Id, new("partner", Password)); r.SetupProfile(member.Id, new("xuzhou", "许舟", Password));
        var workspace = r.CreateWorkspace(profile.Id, "产品设计 · 共同工作"); r.SetMember(profile.Id, workspace.Id, new("xuzhou")); var document = r.CreateSharedDocument(profile.Id, workspace.Id, "九月 · 产品工作手记");
        using var personal = new NoteStore(Path.Combine(temporary.Path, "personal")); personal.Create("不上传的个人笔记", new("doc") { Content = [NoteNode.Paragraph("私人的内容")] });
        using var otherStore = new NoteStore(Path.Combine(temporary.Path, "other"));
        var first = new SharedWorkspaceWindow(personal); var second = new SharedWorkspaceWindow(otherStore); first.Show(); second.Show(); Frame(first); Frame(second);
        try
        {
            Image(first, "shared-native-login.png");
            foreach (var (window, name) in new[] { (first, "owner"), (second, "partner") })
            {
                Find<TextBox>(window, "SharedEndpoint").Text = host.Endpoint.AbsoluteUri; Find<TextBox>(window, "SharedUsername").Text = name; Find<TextBox>(window, "SharedPassword").Text = Password;
                Click(window, Find<Button>(window, "SharedLogin")); await Wait(() => Has(window, "SharedDocument_" + document.Document.Id));
                Click(window, Find<Button>(window, "SharedDocument_" + document.Document.Id)); await Wait(() => Has(window, "SharedBlockEditor"));
                await Wait(() => Find<TextBlock>(window, "SharedSaveStatus").Text == "所有更改已保存");
            }
            var a = Find<BlockEditor>(first, "SharedBlockEditor"); var b = Find<BlockEditor>(second, "SharedBlockEditor");
            a.FocusText(); first.KeyTextInput("把零散的想法，整理成共同的下一步。"); Frame(first);
            await Wait(() => b.Session.Projection.Text.Contains("共同的下一步"));
            b.FocusText(); b.Surface.CaretOffset = b.Surface.Document.TextLength; second.KeyTextInput(" 网页与桌面继续同一份工作。"); Frame(second);
            await Wait(() => a.Session.Projection.Text.Contains("同一份工作"));
            var anchor = NoteComments.CaptureBlock(a.Session, a.Session.Projection.Rows[0].Node.Id);
            var threadId = a.Session.AddComment("这段思路可以再补一个具体例子。", anchor);
            await Wait(() => NoteComments.For(b.Session.Root).Find(threadId) != null);
            b.Session.ReplyComment(NoteComments.For(b.Session.Root).Find(threadId)!, "好，我来补上用户场景。");
            await Wait(() => NoteComments.For(a.Session.Root).Find(threadId)?.Messages.Length == 2);
            a.Session.ReplyComment(NoteComments.For(a.Session.Root).Find(threadId)!, "收到，我们接着这条回复讨论。", NoteComments.For(a.Session.Root).Find(threadId)!.Messages[1].Id);
            await Wait(() => NoteComments.For(b.Session.Root).Find(threadId)?.Messages.Length == 3);
            Click(first, Find<Button>(first, "SharedComments")); Frame(first); Image(first, "shared-native-editor.png");
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(first); Image(first, "shared-native-dark.png"); Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            first.Width = 900; Frame(first); Image(first, "shared-native-narrow.png");
            Assert.Single(personal.List()); Assert.Empty(r.Exchange(profile.Id, new(0, [])).Entities); Assert.Null(SyncCredentials.Load(personal)); Assert.NotNull(SharedCredentials.Load(personal));
            await Wait(() => Find<TextBlock>(first, "SharedSaveStatus").Text == "所有更改已保存");
            r.SetMember(profile.Id, workspace.Id, new("xuzhou", "viewer"));
            await ((WriteMe.SyncServer.SharedHub)host.App.Services.GetService(typeof(WriteMe.SyncServer.SharedHub))!).Recheck(workspace: workspace.Id);
            await Wait(() => b.Session.IsReadOnly && b.Surface.IsReadOnly);
            var readonlyText = b.Session.Projection.Text; b.FocusText(); b.Surface.Select(0, 2); second.KeyTextInput("不可写入"); Frame(second);
            Assert.Equal(readonlyText, b.Session.Projection.Text); Assert.True(b.Surface.SelectionLength > 0);
            Assert.False(Find<Button>(second, "SharedUndo").IsEnabled); Assert.False(Find<Button>(second, "SharedDeleteDocument").IsEnabled);
            Click(second, Find<Button>(second, "SharedComments"));
            Click(second, Find<Button>(second, "CommentExpand_" + threadId));
            Assert.Contains(second.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.Text == "好，我来补上用户场景。");
            Click(second, Find<Button>(second, "CommentParent_" + NoteComments.For(b.Session.Root).Find(threadId)!.Messages[2].Id));
            Assert.True(Find<TextBox>(second, "CommentInput").IsReadOnly);
        }
        finally
        {
            first.Close(); second.Close(); await Wait(() => !first.IsVisible && !second.IsVisible);
        }
    }
}
