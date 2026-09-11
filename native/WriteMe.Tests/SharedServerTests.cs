using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using WriteMe.Core;
using WriteMe.SyncServer;
using Xunit;

namespace WriteMe.Tests;

public sealed class SharedServerTests
{
    private const string Password = "test-only-password-26";
    private static SharedProfile Setup(SyncRepository r, string username, string id, string display)
    {
        var login = r.Login(new(username, Password))!; return r.SetupProfile(login.AccountId, new(id, display, Password));
    }
    [Fact]
    public async Task CompletingFirstLoginRevokesOtherTemporarySessionsAndRejectsNullFields()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository;
        var current = r.Login(new("owner", Password))!; var temporary = r.Login(new("owner", Password))!;
        using var http = new HttpClient { BaseAddress = host.Endpoint }; http.DefaultRequestHeaders.Authorization = new("Bearer", current.Token);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/profile", new { publicId = (string?)null, displayName = "管理员", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("api/profile", new SharedProfileSetup("owner", "管理员", Password))).StatusCode);
        Assert.Equal(current.AccountId, r.Authenticate(current.Token)); Assert.Null(r.Authenticate(temporary.Token));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/admin/accounts", new { username = (string?)null, password = (string?)null })).StatusCode);
    }
    [Fact]
    public async Task AccountsMembershipAndCommentAuthAreEnforcedOverRealHttp()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository;
        var owner = Setup(r, "owner", "owner", "管理员"); var member = r.AddAccount(owner.Id, new("member", Password)); Setup(r, "member", "member", "成员");
        using var http = new HttpClient { BaseAddress = host.Endpoint }; var memberLogin = await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "member", Password); http.DefaultRequestHeaders.Authorization = new("Bearer", memberLogin.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("api/admin/accounts")).StatusCode);
        var workspace = r.CreateWorkspace(owner.Id, "项目组"); var doc = r.CreateSharedDocument(owner.Id, workspace.Id, "计划");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/shared/" + doc.Document.Id)).StatusCode);
        r.SetMember(owner.Id, workspace.Id, new("member")); Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("api/shared/" + doc.Document.Id)).StatusCode);
        using var a = new SharedEditingSession(new(doc.State), owner.DisplayName, owner.Id); a.Session.AddComment("管理员的段落评论");
        doc = r.ApplySharedUpdate(owner.Id, doc.Document.Id, a.Replica.State());
        using var forged = new SharedEditingSession(new(doc.State), "管理员", owner.Id); forged.Session.AddComment("伪造管理员身份");
        Assert.Throws<SharedAccessException>(() => r.ApplySharedUpdate(member.Id, doc.Document.Id, forged.Replica.State()));
        using var b = new SharedEditingSession(new(doc.State), "成员", member.Id); b.Session.ReplyComment(NoteComments.For(b.Session.Root).Threads[0], "我的回复");
        doc = r.ApplySharedUpdate(member.Id, doc.Document.Id, b.Replica.State()); using var verified = new SharedDocumentReplica(doc.State); Assert.Equal(2, NoteComments.For(verified.Read().Root).Threads[0].Messages.Length);
        r.SetMember(owner.Id, workspace.Id, new("member", "viewer")); b.Session.Edit(0, 0, "越权"); Assert.Throws<SharedAccessException>(() => r.ApplySharedUpdate(member.Id, doc.Document.Id, b.Replica.State()));
        r.ChangeAccount(owner.Id, member.Id, new(Disabled: true)); Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("api/me")).StatusCode);
    }
    [Fact]
    public async Task WebSocketEditsAreDurableAndRevocationClosesAccess()
    {
        using var temp = new TestDirectory(); string documentId; byte[]? saved;
        await using (var host = await SyncTests.Host.Start(temp.Path))
        {
            var r = host.Repository; var owner = Setup(r, "owner", "owner", "管理员"); var workspace = r.CreateWorkspace(owner.Id, "共同工作"); var doc = r.CreateSharedDocument(owner.Id, workspace.Id, "实时"); documentId = doc.Document.Id;
            var login = r.Login(new("owner", Password))!; using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + login.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var uri = new UriBuilder(host.Endpoint) { Scheme = "ws", Path = "api/shared/" + documentId + "/connect" }.Uri; await socket.ConnectAsync(uri, timeout.Token);
            using var local = new SharedEditingSession(new(doc.State), "管理员", owner.Id); local.Session.Edit(0, 0, "两台设备共同编辑🙂", false);
            await socket.SendAsync(SharedProtocol.Encode(new("update", local.Replica.State(), local.Replica.StateVector(), "one")), WebSocketMessageType.Text, true, timeout.Token);
            SharedWireMessage response; var buffer = new byte[1024 * 1024];
            do { var read = await socket.ReceiveAsync(buffer, timeout.Token); response = SharedProtocol.Decode(buffer[..read.Count]); } while (response.Type == "peers");
            Assert.Equal("ack", response.Type); Assert.Equal("one", response.Id); saved = r.SharedDocument(owner.Id, documentId).State;
            r.Logout(login.Token); await host.App.Services.GetService(typeof(SharedHub))!.As<SharedHub>().Recheck(account: owner.Id);
            var error = await socket.ReceiveAsync(buffer, timeout.Token); Assert.Contains("error", System.Text.Encoding.UTF8.GetString(buffer, 0, error.Count));
        }
        await using var restarted = await SyncTests.Host.Start(temp.Path); var account = restarted.Repository.Login(new("owner", Password))!.AccountId;
        using var replica = new SharedDocumentReplica(restarted.Repository.SharedDocument(account, documentId).State); Assert.Contains("共同编辑", DocumentText.Plain(replica.Read().Root)); Assert.NotNull(saved);
    }
}

internal static class SharedTestCast { public static T As<T>(this object value) => (T)value; }
