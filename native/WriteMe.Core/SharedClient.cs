using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WriteMe.Core;

public sealed class SharedClientException(string message, int status) : IOException(message) { public int Status { get; } = status; }

public sealed class SharedApiClient : IDisposable
{
    private readonly HttpClient _http;
    public Uri Endpoint { get; }
    public SyncLoginResult Login { get; }
    public SharedApiClient(string endpoint, SyncLoginResult login)
    {
        Endpoint = SharedProtocol.Endpoint(endpoint); ValidateLogin(login); Login = login;
        _http = CreateHttp(); _http.BaseAddress = Endpoint; _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
    }
    private static HttpClient CreateHttp() => new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(30) };
    private static void ValidateLogin(SyncLoginResult login)
    {
        if (!SyncProtocol.ValidId(login.AccountId) || login.Token is not { Length: >= 32 and <= 128 } || login.Token.Any(char.IsControl) || login.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            throw new InvalidDataException("服务器返回了无效登录信息");
    }
    public static async Task<SyncLoginResult> LoginAsync(string endpoint, string username, string password, CancellationToken cancellation = default)
    {
        using var http = CreateHttp();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(SharedProtocol.Endpoint(endpoint), "api/login")) { Content = JsonContent.Create(new SyncLogin(username.Trim(), password), options: SyncProtocol.Json) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode) throw new SharedClientException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "账号或密码不正确" : "无法登录，请检查服务器地址后重试", (int)response.StatusCode);
        var bytes = await SyncProtocol.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cancellation), 8192, cancellation);
        var login = JsonSerializer.Deserialize<SyncLoginResult>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException("登录响应为空"); ValidateLogin(login); return login;
    }
    public async Task<T> Request<T>(string path, string method = "GET", object? body = null, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "api/" + path);
        if (body != null) request.Content = JsonContent.Create(body, options: SyncProtocol.Json);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (!response.IsSuccessStatusCode)
        {
            string? error = null;
            try { using var json = JsonDocument.Parse(await SyncProtocol.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cancellation), 32768, cancellation)); if (json.RootElement.TryGetProperty("error", out var value)) error = value.GetString(); }
            catch (JsonException) { }
            throw new SharedClientException(error ?? (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "登录已失效，请重新登录" : "无法完成操作，请检查连接后重试"), (int)response.StatusCode);
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        var bytes = await SyncProtocol.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(cancellation), SharedProtocol.MaximumWireBytes, cancellation);
        return JsonSerializer.Deserialize<T>(bytes, SyncProtocol.Json) ?? throw new InvalidDataException("服务器返回为空");
    }
    public void Dispose() => _http.Dispose();
}

// Note: 双端同一 WebSocket 协议、确认后已保存、本机草稿与账号隔离 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed class SharedSocket(SharedApiClient api, string document) : IAsyncDisposable
{
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    public void Start(Func<Task> connected, Func<SharedWireMessage, Task> received, Func<string, Task> status)
    {
        if (_running != null) throw new InvalidOperationException("连接已启动");
        _running = Run(connected, received, status);
    }
    private async Task Run(Func<Task> connected, Func<SharedWireMessage, Task> received, Func<string, Task> status)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + api.Login.Token); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _socket = socket;
            try
            {
                await status("正在连接…"); var uri = new UriBuilder(new Uri(api.Endpoint, $"api/shared/{document}/connect")) { Scheme = api.Endpoint.Scheme == "https" ? "wss" : "ws" }.Uri;
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(uri, connectTimeout.Token); await connected();
                var buffer = new byte[65536];
                while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                {
                    using var stream = new MemoryStream(); WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, _stop.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > SharedProtocol.MaximumWireBytes) throw new InvalidDataException("协同消息无效");
                        stream.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    await received(SharedProtocol.Decode(stream.ToArray()));
                }
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException) { }
            finally { if (ReferenceEquals(_socket, socket)) _socket = null; }
            if (_stop.IsCancellationRequested) break;
            await status("离线 · 修改保存在此设备");
            try { await Task.Delay(2500, _stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task<bool> Send(SharedWireMessage message)
    {
        if (_stop.IsCancellationRequested) return false;
        try
        {
            await _sendGate.WaitAsync(_stop.Token);
            try
            {
                if (_socket is not { State: WebSocketState.Open } socket) return false;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.SendAsync(SharedProtocol.Encode(message), WebSocketMessageType.Text, true, timeout.Token); return true;
            }
            finally { _sendGate.Release(); }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { return false; }
    }
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync(); _socket?.Abort(); if (_running != null) await _running;
        _stop.Dispose(); _sendGate.Dispose();
    }
    public void Stop() { _stop.Cancel(); _socket?.Abort(); }
}

public sealed class SharedDraftStore : IDisposable
{
    private readonly SqliteConnection _database;
    public SharedDraftStore(string directory)
    {
        Directory.CreateDirectory(directory); _database = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "shared-drafts.db"), Pooling = false }.ToString()); _database.Open();
        using var command = _database.CreateCommand(); command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS drafts(target TEXT NOT NULL,account TEXT NOT NULL,document TEXT NOT NULL,state BLOB NOT NULL,PRIMARY KEY(target,account,document))"; command.ExecuteNonQuery();
    }
    private static string Target(string endpoint) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SharedProtocol.Endpoint(endpoint).AbsoluteUri)));
    public byte[]? Load(string endpoint, string account, string document)
    {
        using var command = _database.CreateCommand(); command.CommandText = "SELECT state FROM drafts WHERE target=$target AND account=$account AND document=$document";
        command.Parameters.AddWithValue("$target", Target(endpoint)); command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$document", document); return command.ExecuteScalar() as byte[];
    }
    public void Save(string endpoint, string account, string document, byte[] state)
    {
        if (state.Length > SharedDocumentReplica.MaximumStateBytes) throw new InvalidDataException("本地协同草稿超过 16 MB");
        using var command = _database.CreateCommand(); command.CommandText = "INSERT INTO drafts(target,account,document,state) VALUES($target,$account,$document,$state) ON CONFLICT(target,account,document) DO UPDATE SET state=excluded.state";
        command.Parameters.AddWithValue("$target", Target(endpoint)); command.Parameters.AddWithValue("$account", account); command.Parameters.AddWithValue("$document", document); command.Parameters.AddWithValue("$state", state); command.ExecuteNonQuery();
    }
    public void Dispose() => _database.Dispose();
}
