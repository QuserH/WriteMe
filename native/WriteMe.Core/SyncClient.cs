using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WriteMe.Core;

public sealed class SyncAuthenticationException() : IOException("登录已失效，请重新登录同步服务器");
public sealed record SyncRunResult(int Sent, int Received, int UploadedAssets, int DownloadedAssets, int MissingAssets, int Conflicts, IReadOnlySet<string> Changed);

// Note: 网络传输与本地提交分离，提交前保存当前草稿；失败保持 dirty — 见 .agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md
public sealed class SyncClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _token;
    public Uri Endpoint { get; }
    public string Target { get; }

    public SyncClient(Uri endpoint, SyncLoginResult login)
    {
        Endpoint = SyncProtocol.Endpoint(endpoint.AbsoluteUri);
        if (!SyncProtocol.ValidId(login.AccountId) || login.Token is not { Length: >= 32 and <= 128 } || login.Token.Any(char.IsControl))
            throw new InvalidDataException("登录响应无效");
        _token = login.Token; Target = Endpoint.AbsoluteUri + "|" + login.AccountId;
        _http = CreateHttp();
    }
    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = TimeSpan.FromSeconds(90) };

    public static async Task<SyncLoginResult> LoginAsync(string endpoint, string username, string password, CancellationToken cancellation = default)
    {
        var uri = SyncProtocol.Endpoint(endpoint);
        using var http = CreateHttp();
        using var request = JsonRequest(new(uri, "api/login"), new SyncLogin(username.Trim(), password));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException("账号或密码不正确");
        Check(response);
        var login = await ReadAsync<SyncLoginResult>(response, 8192, cancellation);
        if (!SyncProtocol.ValidId(login.AccountId) || login.Token is not { Length: >= 32 and <= 128 } || login.Token.Any(char.IsControl)
            || login.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new InvalidDataException("服务器返回了无效登录信息");
        return login;
    }

    public async Task LogoutAsync(CancellationToken cancellation = default)
    {
        using var request = Request(HttpMethod.Post, "api/logout");
        using var response = await _http.SendAsync(request, cancellation);
        if (response.StatusCode != HttpStatusCode.Unauthorized) Check(response);
    }

    public async Task<SyncRunResult> SynchronizeAsync(NoteStore store,
        Func<SyncResponse, CancellationToken, Task<IReadOnlySet<string>>>? apply = null, CancellationToken cancellation = default)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var uploaded = new HashSet<string>(StringComparer.Ordinal); var downloaded = new HashSet<string>(StringComparer.Ordinal);
        var visitedUploads = new HashSet<string>(StringComparer.Ordinal); var visitedDownloads = new HashSet<string>(StringComparer.Ordinal);
        var missing = new HashSet<string>(StringComparer.Ordinal); var sent = 0; var received = 0;
        for (var batch = 0; ; batch++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (batch >= 500) throw new IOException("本轮同步已处理 500 批，剩余数据将在下次同步继续");
            var pending = store.PendingSync().ToArray();
            foreach (var asset in SyncProtocol.Assets(pending))
            {
                if (!visitedUploads.Add(asset.Id)) continue;
                using var head = Request(HttpMethod.Head, "api/assets/" + asset.Id);
                using var exists = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, cancellation);
                if (exists.IsSuccessStatusCode) continue;
                if (exists.StatusCode != HttpStatusCode.NotFound) Check(exists);
                if (store.AssetPath(asset.Id) is not { } path) { missing.Add(asset.Id); continue; }
                await using var source = File.OpenRead(path);
                if (source.Length != asset.Size) throw new InvalidDataException("本地附件大小不符：" + asset.Name);
                using var upload = Request(HttpMethod.Put, "api/assets/" + asset.Id); upload.Content = new StreamContent(source);
                upload.Content.Headers.ContentType = new("application/octet-stream");
                using var result = await _http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, cancellation); Check(result); uploaded.Add(asset.Id);
            }
            var cursor = store.SyncCursor(Target);
            using var request = JsonRequest(new(Endpoint, "api/sync"), new SyncRequest(cursor, pending)); Authorize(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); Check(response);
            var state = await ReadAsync<SyncResponse>(response, SyncProtocol.MaximumBatchBytes, cancellation);
            if (state.Cursor < 0 || state.Entities == null || state.Entities.Length > 1000 || state.Entities.Any(entity => entity == null)
                || state.Entities.Select(entity => entity.Key).Distinct(StringComparer.Ordinal).Count() != state.Entities.Length)
                throw new InvalidDataException("同步响应无效");
            foreach (var entity in state.Entities) SyncProtocol.Validate(entity);
            foreach (var asset in SyncProtocol.Assets(state.Entities)) await DownloadAsync(asset);
            cancellation.ThrowIfCancellationRequested();
            var applied = apply == null ? store.ApplySync(Target, state) : await apply(state, cancellation);
            changed.UnionWith(applied); sent += pending.Length; received += state.Entities.Length;
            if (!state.HasMore && store.PendingSyncCount == 0) break;
            if (state.HasMore && state.Cursor == cursor && state.Entities.Length == 0) throw new InvalidDataException("服务器同步游标未前进");
        }
        // Retry files that were temporarily unavailable in an earlier successful metadata exchange.
        foreach (var asset in store.MissingSyncAssets()) await DownloadAsync(asset);
        missing.RemoveWhere(id => store.AssetPath(id) != null);
        return new(sent, received, uploaded.Count, downloaded.Count, missing.Count, store.SyncConflicts().Count, changed);

        async Task DownloadAsync(AssetInfo asset)
        {
            if (store.AssetPath(asset.Id) != null || !visitedDownloads.Add(asset.Id)) return;
            using var download = Request(HttpMethod.Get, "api/assets/" + asset.Id);
            using var response = await _http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound) { missing.Add(asset.Id); return; }
            Check(response);
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("服务器附件大小不符");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await store.ReceiveAssetAsync(asset, source, cancellation); downloaded.Add(asset.Id); missing.Remove(asset.Id);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Endpoint, path)); Authorize(request); return request;
    }
    private void Authorize(HttpRequestMessage request) => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    private static HttpRequestMessage JsonRequest<T>(Uri endpoint, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SyncProtocol.Json);
        if (bytes.Length > SyncProtocol.MaximumBatchBytes) throw new InvalidDataException("同步批次过大");
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/json"); return request;
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("服务器响应过大");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        var bytes = await SyncProtocol.ReadLimitedAsync(stream, limit, cancellation);
        return JsonSerializer.Deserialize<T>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException("服务器响应为空");
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new SyncAuthenticationException();
        if ((int)response.StatusCode is >= 300 and < 400) throw new IOException("同步地址发生重定向，请直接填写最终 HTTPS 地址");
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new IOException("登录尝试过于频繁，请稍后重试");
        if (!response.IsSuccessStatusCode) throw new IOException("同步服务器请求失败（HTTP " + (int)response.StatusCode + "）");
    }
    public void Dispose() => _http.Dispose();
}
