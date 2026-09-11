using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.SyncServer;
using Xunit;

namespace WriteMe.Tests;

public sealed class SyncTests
{
    private const string Password = "test-only-password-26";
    private static NoteNode Doc(string text) => new("doc") { Content = [NoteNode.Paragraph(text)] };
    private static string Text(NoteStore store, string id) => DocumentText.Plain(NoteJson.Parse(store.Get(id).Content));
    private static SyncEntity Version(string actor, long counter, string name, Dictionary<string, long>? clock = null) =>
        new("space/project", [new(actor + ":" + counter, clock ?? new() { [actor] = counter }, SyncProtocol.Encode(new SpaceInfo("project", name)))]);

    internal sealed class Host : IAsyncDisposable
    {
        public WebApplication App { get; }
        public Uri Endpoint { get; private set; } = null!;
        public SyncRepository Repository => App.Services.GetRequiredService<SyncRepository>();
        private Host(string path, string? webRoot) => App = SyncServerHost.Build(new(path, "owner", Password, Quiet: true, WebRoot: webRoot));
        public static async Task<Host> Start(string path, string? webRoot = null)
        {
            var host = new Host(path, webRoot); await host.App.StartAsync();
            host.Endpoint = new(host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/"); return host;
        }
        public async Task<SyncClient> Client(string username = "owner", string password = Password) => new(Endpoint, await SyncClient.LoginAsync(Endpoint.AbsoluteUri, username, password));
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }

    [Fact]
    public void RegisterMergeIsCommutativeAssociativeIdempotentAndKeepsCausalFrontiers()
    {
        var a = Version("alpha", 1, "A"); var b = Version("beta", 1, "B"); var c = Version("gamma", 1, "C");
        string Canon(SyncEntity entity) => SyncProtocol.Canonical(entity);
        Assert.Equal(Canon(SyncProtocol.Merge(a, b)), Canon(SyncProtocol.Merge(b, a)));
        Assert.Equal(Canon(SyncProtocol.Merge(SyncProtocol.Merge(a, b), c)), Canon(SyncProtocol.Merge(a, SyncProtocol.Merge(b, c))));
        var combined = SyncProtocol.Merge(SyncProtocol.Merge(a, b), c);
        Assert.Equal(Canon(combined), Canon(SyncProtocol.Merge(combined, combined))); Assert.Equal(3, combined.Versions.Length);
        var resolved = Version("alpha", 2, "合并", new() { ["alpha"] = 2, ["beta"] = 1, ["gamma"] = 1 });
        Assert.Equal(Canon(resolved), Canon(SyncProtocol.Merge(combined, resolved)));
        Assert.Equal(Canon(resolved), Canon(SyncProtocol.Merge(resolved, a)));
        Assert.Throws<InvalidDataException>(() => SyncProtocol.Merge(a, Version("alpha", 1, "篡改同一个版本")));
    }

    [Fact]
    public async Task HttpExchangeTransfersFullLibraryAssetsReferencesAndAppearance()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b"));
        using var a = await host.Client(); using var b = await host.Client();
        var space = first.CreateSpace("研究"); var folder = first.CreateFolder(space.Id, "资料");
        using var input = new MemoryStream("附件内容：中文和 emoji 🌿"u8.ToArray()); var asset = first.ImportAsset(input, "资料.txt");
        using var coverStream = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+lmioAAAAASUVORK5CYII=")); var cover = first.ImportAsset(coverStream, "封面.png");
        var target = first.Create("目标", spaceId: space.Id, folderId: folder.Id);
        var session = new DocumentSession(Doc("目标 #研究")); session.InsertNoteLink(0, 2, target.Id, "目标", true); session.InsertAsset(session.Projection.Text.Length, asset, false);
        var document = first.Create("文档", session.Root, space.Id, folder.Id);
        first.SetFavorite(document.Id, true); first.SetAppearance(document.Id, new("serif", 18, 1200, "#FFFDF5", CoverAssetId: cover.Id, Divider: "dotted")); first.SetPreferences(new("dark", .55));
        var pushed = await a.SynchronizeAsync(first); Assert.Equal(2, pushed.UploadedAssets);
        var pulled = await b.SynchronizeAsync(second); await a.SynchronizeAsync(first);
        Assert.Equal(2, pulled.DownloadedAssets); Assert.Equal(first.Get(document.Id), second.Get(document.Id)); Assert.NotNull(second.AssetPath(cover.Id));
        Assert.Equal(folder.Id, second.Location(document.Id).FolderId); Assert.Equal("研究", second.Spaces().Single(item => item.Id == space.Id).Name);
        Assert.Equal(first.Appearance(document.Id), second.Appearance(document.Id)); Assert.Equal(new(), second.Preferences());
        Assert.Equal("研究", Assert.Single(second.Tags()).Name); Assert.Single(second.Backlinks(target.Id));
        Assert.Equal(File.ReadAllBytes(first.AssetPath(asset.Id)!), File.ReadAllBytes(second.AssetPath(asset.Id)!));
        Assert.Equal(0, first.PendingSyncCount); Assert.Equal(0, second.PendingSyncCount);
        Assert.Empty(first.SyncConflicts()); Assert.Empty(second.SyncConflicts());
        second.SetTrashed(document.Id, true); await b.SynchronizeAsync(second); await a.SynchronizeAsync(first);
        Assert.True(first.Location(document.Id).IsDeleted); Assert.Empty(first.Backlinks(target.Id));
        second.SetTrashed(document.Id, false); await b.SynchronizeAsync(second); await a.SynchronizeAsync(first); Assert.Single(first.Backlinks(target.Id));
    }

    [Fact]
    public async Task ThreeOfflineEditsArePreservedAndStaleConflictChoicesCannotOverwriteNewVersions()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b")); using var third = new NoteStore(Path.Combine(temporary.Path, "c"));
        using var a = await host.Client(); using var b = await host.Client(); using var c = await host.Client();
        var id = first.Create("共同文档", Doc("原文")).Id; await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); await c.SynchronizeAsync(third);
        first.Save(id, "版本 A", Doc("离线 A")); second.Save(id, "版本 B", Doc("离线 B")); third.Save(id, "版本 C", Doc("离线 C"));
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); await a.SynchronizeAsync(first);
        var stale = Assert.Single(first.SyncConflicts()).Entity; Assert.Equal(2, stale.Versions.Length);
        var before = first.Get(id); Assert.Throws<InvalidOperationException>(() => first.Save(id, "不得覆盖", Doc("冲突待处理"))); Assert.Equal(before, first.Get(id));
        Assert.Throws<InvalidOperationException>(() => first.SetFavorite(id, true));
        await c.SynchronizeAsync(third); await a.SynchronizeAsync(first); await b.SynchronizeAsync(second);
        var conflict = Assert.Single(first.SyncConflicts()).Entity; Assert.Equal(3, conflict.Versions.Length);
        Assert.Throws<InvalidOperationException>(() => first.ResolveSyncConflict(stale, stale.Versions[0].Id));
        var chosen = conflict.Versions.Single(version => SyncProtocol.Read<SyncDocumentPayload>(version.Payload!).Document.Title == "版本 B");
        first.ResolveSyncConflict(conflict, chosen.Id); Assert.Equal("离线 B", Text(first, id)); Assert.Empty(first.SyncConflicts());
        Assert.Contains(first.Revisions(id), revision => DocumentText.Plain(NoteJson.Parse(revision.Content)) == "离线 A");
        Assert.Contains(first.Revisions(id), revision => DocumentText.Plain(NoteJson.Parse(revision.Content)) == "离线 C");
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); await c.SynchronizeAsync(third);
        Assert.Equal(first.Get(id), second.Get(id)); Assert.Equal(first.Get(id), third.Get(id));
        Assert.Equal(SyncProtocol.Canonical(first.SyncState("document/" + id)!), SyncProtocol.Canonical(third.SyncState("document/" + id)!));
        var resolved = first.SyncState("document/" + id)!;
        first.ApplySync(a.Target, new(first.SyncCursor(a.Target), false, [stale])); Assert.Equal(SyncProtocol.Canonical(resolved), SyncProtocol.Canonical(first.SyncState(resolved.Key)!));
    }

    [Fact]
    public async Task OfflineDeleteVersusEditRemainsVisibleUntilExplicitChoice()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b"));
        using var a = await host.Client(); using var b = await host.Client();
        var id = first.Create("文档", Doc("原文")).Id; await a.SynchronizeAsync(first); await b.SynchronizeAsync(second);
        first.Delete(id); second.Save(id, "保留的离线编辑", Doc("仍然在这里"));
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); await a.SynchronizeAsync(first);
        Assert.Equal("仍然在这里", Text(first, id)); var conflict = Assert.Single(first.SyncConflicts()).Entity;
        Assert.Contains(conflict.Versions, version => version.Payload == null);
        first.ResolveSyncConflict(conflict, conflict.Versions.Single(version => version.Payload != null).Id);
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); Assert.Empty(second.SyncConflicts()); Assert.Equal("仍然在这里", Text(second, id));
    }

    [Fact]
    public async Task EditDuringRequestIsSavedBeforeApplyingOldAcknowledgement()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        using var store = new NoteStore(Path.Combine(temporary.Path, "client")); using var peer = new NoteStore(Path.Combine(temporary.Path, "peer"));
        using var client = await host.Client(); using var other = await host.Client();
        var id = store.Create("标题", Doc("发送时的内容")).Id; var committed = false;
        await client.SynchronizeAsync(store, (response, _) =>
        {
            if (!committed) { store.Save(id, "新标题", Doc("请求期间又输入了中文")); committed = true; }
            return Task.FromResult(store.ApplySync(client.Target, response));
        });
        await other.SynchronizeAsync(peer); Assert.Equal("请求期间又输入了中文", Text(peer, id)); Assert.Equal(0, store.PendingSyncCount); Assert.Empty(store.SyncConflicts());
    }

    [Fact]
    public async Task InvalidBatchRollsBackEarlierEntitiesAndCursor()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        var account = host.Repository.Authenticate((await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "owner", Password)).Token)!;
        var original = Version("actor", 1, "已存在"); host.Repository.Exchange(account, new(0, [original]));
        var valid = new SyncEntity("space/other", [new("writer:1", new() { ["writer"] = 1 }, SyncProtocol.Encode(new SpaceInfo("other", "不得留下")))]);
        Assert.Throws<InvalidDataException>(() => host.Repository.Exchange(account, new(0, [valid, Version("actor", 1, "伪造同一版本")])));
        var result = host.Repository.Exchange(account, new(0, [])); Assert.Single(result.Entities); Assert.Equal(original.Key, result.Entities[0].Key);
        using var store = new NoteStore(Path.Combine(temporary.Path, "client")); store.ApplySync("test", new(1, false, [original]));
        Assert.Throws<InvalidDataException>(() => store.ApplySync("test", new(2, false, [valid, Version("actor", 1, "冲突标识")])));
        Assert.Null(store.SyncState(valid.Key)); Assert.Equal(1, store.SyncCursor("test"));
    }

    [Fact]
    public async Task AccountsAndAssetsAreIsolatedCredentialsHashedAndLogoutRevokesToken()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        host.Repository.CreateAccount("other", Password);
        var owner = await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "owner", Password); var other = await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "other", Password);
        using var store = new NoteStore(Path.Combine(temporary.Path, "client"));
        using var input = new MemoryStream("private contents"u8.ToArray()); var asset = store.ImportAsset(input, "private.txt");
        store.Create("私有资料", new("doc") { Content = [DocumentAssets.Node(asset, false)] });
        using var client = new SyncClient(host.Endpoint, owner); await client.SynchronizeAsync(store);
        using var http = new HttpClient { BaseAddress = host.Endpoint }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Token);
        using var denied = await http.GetAsync("api/assets/" + asset.Id); Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var exchange = await http.PostAsJsonAsync("api/sync", new SyncRequest(0, []), SyncProtocol.Json);
        var empty = await exchange.Content.ReadFromJsonAsync<SyncResponse>(SyncProtocol.Json); Assert.Empty(empty!.Entities);
        using var invalid = await http.PutAsync("api/assets/" + new string('A', 64), new ByteArrayContent("wrong hash"u8.ToArray())); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Null(host.Repository.AssetPath(other.AccountId, new string('A', 64)));
        await Assert.ThrowsAsync<IOException>(() => SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "owner", "bad-password"));
        await client.LogoutAsync(); Assert.Null(host.Repository.Authenticate(owner.Token)); Assert.NotNull(host.Repository.Authenticate(other.Token));
        using var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(host.Repository.DataDirectory, "sync.db"), Pooling = false }.ToString()); database.Open();
        using var command = database.CreateCommand(); command.CommandText = "SELECT length(salt),length(password_hash) FROM accounts LIMIT 1";
        using var reader = command.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(32, reader.GetInt32(0)); Assert.Equal(32, reader.GetInt32(1));
    }

    [Theory]
    [InlineData("{\"cursor\":0,\"entities\":[null]}")]
    [InlineData("{\"cursor\":0,\"entities\":[{\"key\":\"space/x\",\"versions\":[null]}]}")]
    [InlineData("{\"cursor\":-1,\"entities\":[]}")]
    public async Task MalformedNetworkBatchesReturnControlledBadRequest(string json)
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        var login = await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "owner", Password);
        using var http = new HttpClient { BaseAddress = host.Endpoint }; http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"); using var response = await http.PostAsync("api/sync", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PaginationAndRestartPreserveFolderBindingsAndAccountData()
    {
        using var temporary = new TestDirectory(); var directory = Path.Combine(temporary.Path, "server");
        using var first = new NoteStore(Path.Combine(temporary.Path, "a")); var space = first.CreateSpace("分页空间"); var parent = first.CreateFolder(space.Id, "父级"); var folder = first.CreateFolder(space.Id, "子级", parent.Id);
        for (var i = 0; i < 127; i++) first.Create("笔记 " + i, Doc("正文 " + i), space.Id, folder.Id);
        await using (var host = await Host.Start(directory)) { using var client = await host.Client(); await client.SynchronizeAsync(first); }
        await using var restarted = await Host.Start(directory); using var second = new NoteStore(Path.Combine(temporary.Path, "b")); using var other = await restarted.Client();
        await other.SynchronizeAsync(second); Assert.Equal(127, second.List().Count); Assert.All(second.List(), document => Assert.Equal(folder.Id, second.Location(document.Id).FolderId));
        Assert.Equal(parent.Id, second.Folders().Single(item => item.Id == folder.Id).ParentId); Assert.Equal(0, second.PendingSyncCount);
        var noChange = await other.SynchronizeAsync(second); Assert.Equal(0, noChange.Received); Assert.Equal(0, noChange.Sent);
    }

    [Fact]
    public void LegacyCloneGetsANewActorAndIndependentCounters()
    {
        using var temporary = new TestDirectory(); using var original = new NoteStore(Path.Combine(temporary.Path, "source"));
        var doc = original.Create("标题", Doc("原文")); original.SetSetting("sync_credentials", "must-not-copy");
        using var clone = new NoteStore(Path.Combine(temporary.Path, "clone"), original.DatabasePath);
        Assert.NotEqual(original.DeviceId, clone.DeviceId); Assert.Equal("", clone.Setting("sync_credentials")); Assert.True(clone.PendingSyncCount > 0);
        original.Save(doc.Id, "原库修改", Doc("A")); clone.Save(doc.Id, "副本修改", Doc("B"));
        var merged = SyncProtocol.Merge(original.SyncState("document/" + doc.Id)!, clone.SyncState("document/" + doc.Id)!); Assert.Equal(2, merged.Versions.Length);
    }

    [Fact]
    public void ConcurrentFolderMovesBreakCyclesDeterministicallyAndDailyNotesKeepBothCopies()
    {
        using var temporary = new TestDirectory(); using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b"));
        var a = first.CreateFolder("personal", "A"); var b = first.CreateFolder("personal", "B");
        second.ApplySync("test", new(1, false, first.PendingSync().ToArray()));
        first.MoveFolder(a.Id, b.Id); second.MoveFolder(b.Id, a.Id);
        var aMove = first.SyncState("folder/" + a.Id)!; var bMove = second.SyncState("folder/" + b.Id)!;
        first.ApplySync("test", new(2, false, [bMove])); second.ApplySync("test", new(2, false, [aMove]));
        Assert.Equal(first.Folders(), second.Folders()); Assert.Single(first.Folders(), folder => folder.ParentId == null);
        var date = new DateOnly(2026, 9, 10); var one = first.DailyNote(date); var two = second.DailyNote(date);
        first.ApplySync("test", new(3, false, [second.SyncState("document/" + two.Id)!])); second.ApplySync("test", new(3, false, [first.SyncState("document/" + one.Id)!]));
        Assert.Equal(2, first.List().Count); Assert.Equal(first.DailyNote(date).Id, second.DailyNote(date).Id);
    }

    [Fact]
    public async Task NetworkFailureAndCancellationKeepTheEntirePendingBatch()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path); var document = store.Create("离线文档", Doc("内容仍在本机"));
        var pending = store.PendingSyncCount; var login = new SyncLoginResult(new string('a', 43), "account", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds());
        using var client = new SyncClient(new("http://127.0.0.1:1/"), login);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SynchronizeAsync(store)); Assert.Equal(pending, store.PendingSyncCount); Assert.Equal(document, store.Get(document.Id));
        using var canceled = new CancellationTokenSource(); await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SynchronizeAsync(store, cancellation: canceled.Token)); Assert.Equal(pending, store.PendingSyncCount);
    }

    [Fact]
    public async Task MissingAssetsRetryAndCorruptDownloadsNeverBecomeLocalFiles()
    {
        using var temporary = new TestDirectory(); await using var host = await Host.Start(Path.Combine(temporary.Path, "server"));
        using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b"));
        var bytes = "arrives later"u8.ToArray(); var asset = new AssetInfo(Convert.ToHexString(SHA256.HashData(bytes)), "later.txt", "text/plain", bytes.Length);
        first.Create("占位附件", new("doc") { Content = [DocumentAssets.Node(asset, false)] });
        var login = await SyncClient.LoginAsync(host.Endpoint.AbsoluteUri, "owner", Password); using var a = new SyncClient(host.Endpoint, login); using var b = await host.Client();
        await a.SynchronizeAsync(first); var partial = await b.SynchronizeAsync(second); Assert.Equal(1, partial.MissingAssets); Assert.Null(second.AssetPath(asset.Id));
        using (var source = new MemoryStream(bytes)) await host.Repository.SaveAssetAsync(login.AccountId, asset.Id, source, default);
        var completed = await b.SynchronizeAsync(second); Assert.Equal(1, completed.DownloadedAssets); Assert.Equal(0, completed.MissingAssets);
        using var corrupt = new MemoryStream("bad"u8.ToArray()); var invalid = new AssetInfo(new string('F', 64), "bad.txt", "text/plain", 3);
        await Assert.ThrowsAsync<InvalidDataException>(() => first.ReceiveAssetAsync(invalid, corrupt)); Assert.Null(first.AssetPath(invalid.Id));
        Assert.DoesNotContain(Directory.GetFiles(first.AssetDirectory), path => Path.GetFileName(path).StartsWith('.'));
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/?token=secret")]
    [InlineData("file:///tmp/x")]
    public void EndpointRejectsInsecureOrCredentialBearingAddresses(string address) => Assert.Throws<ArgumentException>(() => SyncProtocol.Endpoint(address));

    [Fact]
    public void CredentialsAreEncryptedForThisLibraryAndExcludedFromPortableBackup()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(Path.Combine(temporary.Path, "client"));
        var login = new SyncLoginResult("0123456789-test-token-that-must-stay-private", "account", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds());
        var connection = new SavedSyncConnection("https://sync.example.com/", "owner", login); var saved = SyncCredentials.Save(store, connection);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(saved); Assert.Equal(connection, SyncCredentials.Load(store)); Assert.DoesNotContain(login.Token, store.Setting("sync_credentials")!);
            using var another = new NoteStore(Path.Combine(temporary.Path, "other")); another.SetSetting("sync_credentials", store.Setting("sync_credentials")!); Assert.Null(SyncCredentials.Load(another));
        }
        else { Assert.False(saved); Assert.Null(SyncCredentials.Load(store)); }
        using var output = new MemoryStream(); store.ExportBackup(output); output.Position = 0;
        using var zip = new System.IO.Compression.ZipArchive(output); using var stream = zip.GetEntry("manifest.json")!.Open(); using var reader = new StreamReader(stream);
        var manifest = reader.ReadToEnd(); Assert.DoesNotContain(login.Token, manifest); Assert.DoesNotContain("sync_credentials", manifest);
        SyncCredentials.Clear(store); Assert.Null(SyncCredentials.Load(store));
    }
}
