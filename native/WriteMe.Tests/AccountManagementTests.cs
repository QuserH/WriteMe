using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using WriteMe.Core;
using WriteMe.SyncServer;
using Xunit;

namespace WriteMe.Tests;

public sealed class AccountManagementTests
{
    private const string Password = "test-only-password-26";
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+lmioAAAAASUVORK5CYII=";
    private static SharedProfile Owner(SyncRepository repository)
    {
        var account = repository.Login(new("owner", Password))!;
        return repository.SetupProfile(account.AccountId, new("owner", "林然", Password));
    }
    private static SharedProfile Member(SyncRepository repository, string owner, string username)
    {
        var account = repository.AddAccount(owner, new(username, Password));
        return repository.SetupProfile(account.Id, new(username, username, Password));
    }
    private static HttpClient Http(SyncTests.Host host, SyncLoginResult login)
    {
        var http = new HttpClient { BaseAddress = host.Endpoint }; http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token); return http;
    }

    [Fact]
    public async Task AdministratorRenamesAndChangesToSixCharactersWithoutLosingSessionOrData()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository; var owner = Owner(r);
        var workspace = r.CreateWorkspace(owner.Id, "仍属于我的工作区"); var document = r.CreateSharedDocument(owner.Id, workspace.Id, "保留的文档");
        var current = r.Login(new("owner", Password))!; var other = r.Login(new("owner", Password))!;
        using var http = Http(host, current);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("api/admin/accounts/" + owner.Id, new SharedAccountChange(Password: "12345"))).StatusCode);
        var response = await http.PatchAsJsonAsync("api/admin/accounts/" + owner.Id, new SharedAccountChange(Password: "new123", Username: "new-admin", DisplayName: "管理员新名字"));
        response.EnsureSuccessStatusCode(); var updated = (await response.Content.ReadFromJsonAsync<SharedProfile>())!;
        Assert.Equal(owner.Id, updated.Id); Assert.False(updated.NeedsSetup); Assert.Equal("new-admin", updated.Username); Assert.Equal("管理员新名字", updated.DisplayName);
        Assert.Equal(owner.Id, r.Authenticate(current.Token)); Assert.Null(r.Authenticate(other.Token));
        Assert.Null(r.Login(new("owner", Password))); Assert.Null(r.Login(new("new-admin", Password))); Assert.NotNull(r.Login(new("new-admin", "new123")));
        Assert.Equal(workspace.Id, Assert.Single(await http.GetFromJsonAsync<SharedWorkspace[]>("api/workspaces") ?? []).Id);
        Assert.Equal(document.Document.Id, Assert.Single(await http.GetFromJsonAsync<SharedDocumentInfo[]>("api/workspaces/" + workspace.Id + "/documents") ?? []).Id);
        var second = await http.PostAsJsonAsync("api/admin/accounts", new SharedAccountInput("six-character-user", "654321")); second.EnsureSuccessStatusCode();
        var member = (await second.Content.ReadFromJsonAsync<SharedProfile>())!;
        using var memberHttp = Http(host, r.Login(new("six-character-user", "654321"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await memberHttp.PostAsJsonAsync("api/profile", new SharedProfileSetup("six-user", "六位密码", "12345"))).StatusCode);
        (await memberHttp.PostAsJsonAsync("api/profile", new SharedProfileSetup("six-user", "六位密码", "123456"))).EnsureSuccessStatusCode();
        Assert.False(r.Profile(member.Id).NeedsSetup);
    }

    [Fact]
    public async Task OwnPasswordRequiresCurrentSecretAndRevokesOnlyOtherSessions()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository; var owner = Owner(r);
        var member = Member(r, owner.Id, "member"); var current = r.Login(new("member", Password))!; var other = r.Login(new("member", Password))!;
        var administrator = r.Login(new("owner", Password))!; using var http = Http(host, current);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("api/profile/password", new SharedPasswordChange("incorrect", "654321"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/profile/password", new SharedPasswordChange(Password, "12345"))).StatusCode);
        (await http.PostAsJsonAsync("api/profile/password", new SharedPasswordChange(Password, "654321"))).EnsureSuccessStatusCode();
        Assert.Equal(member.Id, r.Authenticate(current.Token)); Assert.Null(r.Authenticate(other.Token)); Assert.Equal(owner.Id, r.Authenticate(administrator.Token));
        Assert.Null(r.Login(new("member", Password))); Assert.NotNull(r.Login(new("member", "654321")));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/me")).StatusCode);
    }

    [Fact]
    public async Task AvatarAccessIsScopedAndRenamingAcceptsOfflineCommentsWithThePreviousName()
    {
        using var temp = new TestDirectory(); string ownerId, version, documentId;
        await using (var host = await SyncTests.Host.Start(temp.Path))
        {
            var r = host.Repository; var owner = Owner(r); ownerId = owner.Id; var peer = Member(r, owner.Id, "peer"); var outsider = Member(r, owner.Id, "outside");
            var workspace = r.CreateWorkspace(owner.Id, "头像可见范围"); r.SetMember(owner.Id, workspace.Id, new("peer"));
            var data = r.CreateSharedDocument(owner.Id, workspace.Id, "离线评论"); documentId = data.Document.Id;
            using var offline = new SharedEditingSession(new(data.State), owner.DisplayName, owner.Id); offline.Session.AddComment("改名之前离线写的评论");
            using var http = Http(host, r.Login(new("owner", Password))!);
            var response = await http.PatchAsJsonAsync("api/profile", new SharedProfileChange("owner", "新的名字", new("#497BE0", "林", "data:image/png;base64," + Png)));
            response.EnsureSuccessStatusCode(); var profile = (await response.Content.ReadFromJsonAsync<SharedProfile>())!; version = profile.Avatar!.ImageVersion!;
            Assert.Equal(64, version.Length); Assert.Equal(owner.Id, profile.Id);
            var path = "api/avatars/" + owner.Id + "/" + version;
            using var memberHttp = Http(host, r.Login(new("peer", Password))!); using var strangerHttp = Http(host, r.Login(new("outside", Password))!);
            Assert.Equal(Convert.FromBase64String(Png), await memberHttp.GetByteArrayAsync(path));
            Assert.Equal(HttpStatusCode.NotFound, (await strangerHttp.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PatchAsJsonAsync("api/profile", new SharedProfileChange("owner", "名字", new("#497BE0", "", "data:image/svg+xml;base64,PHN2Zy8+")))).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PatchAsJsonAsync("api/profile", new SharedProfileChange(peer.PublicId!, "冲突名字", new("#343A43", "X", RemoveImage: true)))).StatusCode);
            Assert.Equal(version, r.Profile(owner.Id).Avatar!.ImageVersion); Assert.Equal("新的名字", r.Profile(owner.Id).DisplayName);
            data = r.ApplySharedUpdate(owner.Id, data.Document.Id, offline.Replica.State());
            using var saved = new SharedDocumentReplica(data.State); Assert.Equal("林然", Assert.Single(NoteComments.For(saved.Read().Root).Threads).Messages[0].Author);
            using var forged = new SharedEditingSession(new(data.State), "从未使用的名字", owner.Id); forged.Session.AddComment("伪造名字");
            Assert.Throws<SharedAccessException>(() => r.ApplySharedUpdate(owner.Id, data.Document.Id, forged.Replica.State()));
            Assert.Equal(HttpStatusCode.Forbidden, (await memberHttp.GetAsync("api/admin/accounts/usage")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await memberHttp.GetAsync("api/admin/accounts/" + outsider.Id + "/data")).StatusCode);
        }
        await using var restarted = await SyncTests.Host.Start(temp.Path);
        Assert.Equal(version, restarted.Repository.Profile(ownerId).Avatar!.ImageVersion);
        Assert.Equal(Convert.FromBase64String(Png), restarted.Repository.AvatarImage(ownerId, ownerId, version));
        using var read = new SharedDocumentReplica(restarted.Repository.SharedDocument(ownerId, documentId).State);
        Assert.Equal("改名之前离线写的评论", Assert.Single(NoteComments.For(read.Read().Root).Threads).Messages[0].Text);
    }

    [Fact]
    public async Task AdminUsageIncludesRealPersonalSharedAndAssetDataAndDeviceRevocationIsScoped()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temp.Path, "server")); var r = host.Repository; var owner = Owner(r);
        var member = Member(r, owner.Id, "member"); var otherAccount = r.Login(new("member", Password))!;
        var current = r.Login(new("owner", Password), "Windows · Chrome")!; var android = r.Login(new("owner", Password), "Android · Chrome")!;
        using var http = Http(host, current);
        using var store = new NoteStore(Path.Combine(temp.Path, "device"));
        store.Create("真实的个人文档", new("doc") { Content = [NoteNode.Paragraph("可同步的内容")] });
        using var input = new MemoryStream("附件内容"u8.ToArray()); var asset = store.ImportAsset(input, "资料.txt");
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph("附件")] }); session.InsertAsset(0, asset, false); store.Create("带附件的文档", session.Root);
        using var client = new SyncClient(host.Endpoint, current); await client.SynchronizeAsync(store);
        var space = r.CreateWorkspace(owner.Id, "共同工作"); r.SetMember(owner.Id, space.Id, new("member"));
        var visible = r.CreateSharedDocument(owner.Id, space.Id, "可见文档"); var deleted = r.CreateSharedDocument(owner.Id, space.Id, "回收站文档"); r.TrashSharedDocument(owner.Id, deleted.Document.Id, true);
        var data = (await http.GetFromJsonAsync<SharedAccountData>("api/admin/accounts/" + owner.Id + "/data"))!;
        Assert.Equal(2, data.Usage.PersonalDocuments); Assert.Equal(1, data.Usage.SharedDocuments); Assert.Equal(1, data.AssetCount); Assert.True(data.AssetBytes > 0);
        Assert.True(data.Usage.PersonalBytes > 0); Assert.NotNull(data.Usage.LastSyncAt); Assert.NotNull(data.Usage.LastLoginAt);
        Assert.Contains(data.PersonalDocuments, document => document.Title == "真实的个人文档"); var workspace = Assert.Single(data.Workspaces); Assert.Equal(1, workspace.DeletedDocuments); Assert.Equal(1, workspace.Documents);
        Assert.Equal(visible.State.Length + deleted.State.Length, workspace.Bytes);
        Assert.Single(data.Sessions, session => session.Current); var mobile = Assert.Single(data.Sessions, session => session.Device == "Android · Chrome");
        var json = JsonSerializer.Serialize(data, SyncProtocol.Json); Assert.DoesNotContain(current.Token, json); Assert.DoesNotContain(android.Token, json); Assert.DoesNotContain("tokenHash", json);
        (await http.DeleteAsync("api/admin/accounts/" + owner.Id + "/sessions/" + mobile.Id)).EnsureSuccessStatusCode();
        Assert.Null(r.Authenticate(android.Token)); Assert.Equal(owner.Id, r.Authenticate(current.Token)); Assert.Equal(member.Id, r.Authenticate(otherAccount.Token));
        (await http.PostAsync("api/admin/accounts/" + owner.Id + "/sessions/revoke", null)).EnsureSuccessStatusCode(); Assert.Equal(owner.Id, r.Authenticate(current.Token));
        Assert.Equal(1, (await http.GetFromJsonAsync<SharedAccountData>("api/admin/accounts/" + owner.Id + "/data"))!.Usage.ActiveSessions);
    }

    [Fact]
    public async Task PausingPersonalSyncRejectsDocumentsAndAssetsWithoutAcknowledgingTheLocalBatch()
    {
        using var temporary = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temporary.Path, "server")); var repository = host.Repository;
        var owner = Owner(repository); var current = repository.Login(new("owner", Password))!; using var http = Http(host, current);
        using var store = new NoteStore(Path.Combine(temporary.Path, "device")); store.Create("尚未发送的个人笔记", new("doc") { Content = [NoteNode.Paragraph("草稿要保留")] });
        using var source = new MemoryStream("附件上传也受暂停控制"u8.ToArray()); var asset = store.ImportAsset(source, "附件.txt");
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph("附件")] }); session.InsertAsset(0, asset, false); store.Create("带附件的草稿", session.Root);
        var pending = store.PendingSyncCount;
        (await http.PatchAsJsonAsync("api/admin/accounts/" + owner.Id, new SharedAccountChange(SyncPaused: true))).EnsureSuccessStatusCode();
        Assert.Equal((HttpStatusCode)423, (await http.PostAsJsonAsync("api/sync", new SyncRequest(0, store.PendingSync().ToArray()), SyncProtocol.Json)).StatusCode);
        using var content = new ByteArrayContent("附件上传也受暂停控制"u8.ToArray());
        Assert.Equal((HttpStatusCode)423, (await http.PutAsync("api/assets/" + asset.Id, content)).StatusCode);
        var read = await http.PostAsJsonAsync("api/sync", new SyncRequest(0, []), SyncProtocol.Json); read.EnsureSuccessStatusCode();
        Assert.Empty((await read.Content.ReadFromJsonAsync<SyncResponse>(SyncProtocol.Json))!.Entities);
        Assert.Equal(pending, store.PendingSyncCount); Assert.Null(repository.AssetPath(owner.Id, asset.Id));
        (await http.PatchAsJsonAsync("api/admin/accounts/" + owner.Id, new SharedAccountChange(SyncPaused: false))).EnsureSuccessStatusCode();
        using var client = new SyncClient(host.Endpoint, current); await client.SynchronizeAsync(store);
        Assert.Equal(0, store.PendingSyncCount); Assert.NotNull(repository.AssetPath(owner.Id, asset.Id)); Assert.Equal(2, repository.AccountData(owner.Id, owner.Id, current.Token).Usage.PersonalDocuments);
    }

    private static async Task<SharedWireMessage> Receive(ClientWebSocket socket, string type, CancellationToken cancellation)
    {
        var buffer = new byte[SharedProtocol.MaximumWireBytes];
        while (true)
        {
            using var body = new MemoryStream(); WebSocketReceiveResult response;
            do { response = await socket.ReceiveAsync(buffer, cancellation); Assert.Equal(WebSocketMessageType.Text, response.MessageType); body.Write(buffer, 0, response.Count); } while (!response.EndOfMessage);
            var message = SharedProtocol.Decode(body.ToArray()); if (message.Type == type) return message;
            Assert.NotEqual("error", message.Type);
        }
    }
    [Fact]
    public async Task PausingWritesRetainsSocketAndPendingDataThenResumesAndRevokesTheChosenDevice()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository; var owner = Owner(r); var member = Member(r, owner.Id, "mobile");
        var workspace = r.CreateWorkspace(owner.Id, "暂停恢复"); r.SetMember(owner.Id, workspace.Id, new("mobile")); var data = r.CreateSharedDocument(owner.Id, workspace.Id, "草稿");
        using var http = Http(host, r.Login(new("owner", Password))!); var login = r.Login(new("mobile", Password), "Android · Edge")!;
        using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + login.Token); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await socket.ConnectAsync(new UriBuilder(host.Endpoint) { Scheme = "ws", Path = "api/shared/" + data.Document.Id + "/connect" }.Uri, timeout.Token);
        await Receive(socket, "peers", timeout.Token);
        Assert.Equal(1, (await http.GetFromJsonAsync<SharedAccountData>("api/admin/accounts/" + member.Id + "/data"))!.Usage.OnlineConnections);
        (await http.PatchAsJsonAsync("api/admin/accounts/" + member.Id, new SharedAccountChange(SyncPaused: true))).EnsureSuccessStatusCode();
        await Receive(socket, "permissions", timeout.Token);
        using var pending = new SharedEditingSession(new(data.State), member.DisplayName, member.Id); pending.Session.Edit(0, 0, "暂停期间仍保留的草稿");
        await socket.SendAsync(SharedProtocol.Encode(new("update", pending.Replica.State(), pending.Replica.StateVector(), "paused")), WebSocketMessageType.Text, true, timeout.Token);
        await Receive(socket, "permissions", timeout.Token);
        Assert.Equal(data.State, r.SharedDocument(owner.Id, data.Document.Id).State); Assert.Equal(WebSocketState.Open, socket.State);
        using var memberHttp = Http(host, login);
        Assert.Equal((HttpStatusCode)423, (await memberHttp.PostAsJsonAsync("api/workspaces/" + workspace.Id + "/documents", new SharedDocumentInput("不能新建"))).StatusCode);
        (await http.PatchAsJsonAsync("api/admin/accounts/" + member.Id, new SharedAccountChange(SyncPaused: false))).EnsureSuccessStatusCode(); await Receive(socket, "permissions", timeout.Token);
        await socket.SendAsync(SharedProtocol.Encode(new("update", pending.Replica.State(), pending.Replica.StateVector(), "resumed")), WebSocketMessageType.Text, true, timeout.Token);
        Assert.Equal("resumed", (await Receive(socket, "ack", timeout.Token)).Id);
        using var saved = new SharedDocumentReplica(r.SharedDocument(owner.Id, data.Document.Id).State); Assert.Contains("暂停期间仍保留的草稿", DocumentText.Plain(saved.Read().Root));
        var device = Assert.Single((await http.GetFromJsonAsync<SharedAccountData>("api/admin/accounts/" + member.Id + "/data"))!.Sessions);
        (await http.DeleteAsync("api/admin/accounts/" + member.Id + "/sessions/" + device.Id)).EnsureSuccessStatusCode();
        await Receive(socket, "error", timeout.Token); Assert.Null(r.Authenticate(login.Token));
    }
}
