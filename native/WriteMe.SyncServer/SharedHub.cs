using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.SyncServer;

public sealed class SharedHub(SyncRepository repository)
{
    private sealed class Room
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ConcurrentDictionary<string, Connection> Connections { get; } = new();
        public int Leases { get; set; }
    }
    private sealed class Connection(WebSocket socket, string token, SharedPeer peer, string workspace, string document)
    {
        public WebSocket Socket { get; } = socket;
        public string Token { get; } = token;
        public SharedPeer Peer { get; set; } = peer;
        public string Workspace { get; } = workspace;
        public string Document { get; } = document;
        public SemaphoreSlim Sending { get; } = new(1, 1);
        public async Task Send(SharedWireMessage message, CancellationToken cancellation)
        {
            await Sending.WaitAsync(cancellation);
            try { if (Socket.State == WebSocketState.Open) await Socket.SendAsync(SharedProtocol.Encode(message), WebSocketMessageType.Text, true, cancellation); }
            finally { Sending.Release(); }
        }
    }
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly object _roomMembership = new();
    public async Task Connect(HttpContext context, string document, CancellationToken cancellation)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var account = (string)context.Items["account"]!; var token = (string)context.Items["token"]!;
        var data = repository.SharedDocument(account, document); var profile = repository.Profile(account);
        var connectionId = Guid.NewGuid().ToString("N");
        var colors = new[] { "#627caa", "#7776ad", "#b47b50", "#4e87a3", "#ad6e83" };
        var color = colors[Convert.ToInt32(account[..2], 16) % colors.Length];
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        Room room;
        lock (_roomMembership) { room = _rooms.GetOrAdd(document, _ => new()); room.Leases++; }
        var connection = new Connection(socket, token, new(connectionId, account, profile.DisplayName, profile.PublicId ?? "", color, Avatar: profile.Avatar), data.Document.WorkspaceId, document);
        room.Connections[connectionId] = connection;
        try
        {
            await Presence(room, cancellation);
            var buffer = new byte[65536]; var activity = DateTime.UtcNow; var count = 0;
            while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
            {
                using var body = new MemoryStream(); WebSocketReceiveResult received;
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellation); idle.CancelAfter(TimeSpan.FromSeconds(60));
                do
                {
                    received = await socket.ReceiveAsync(buffer, idle.Token); if (received.MessageType == WebSocketMessageType.Close) return;
                    if (received.MessageType != WebSocketMessageType.Text || body.Length + received.Count > SharedProtocol.MaximumWireBytes) throw new InvalidDataException("协同消息过大或类型无效");
                    body.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                if ((DateTime.UtcNow - activity).TotalSeconds >= 1) { activity = DateTime.UtcNow; count = 0; }
                if (++count > 100) throw new InvalidDataException("操作过于频繁，请重新连接");
                if (repository.Authenticate(token) != account) throw new SharedAccessException("登录已失效，请重新登录", 401);
                var message = SharedProtocol.Decode(body.ToArray());
                await room.Gate.WaitAsync(cancellation);
                try
                {
                    var current = repository.SharedDocument(account, document);
                    if (message.Type == "hello")
                    {
                        if (message.Vector == null || message.Vector.Length > 512000) throw new InvalidDataException("状态向量缺失");
                        using var replica = new SharedDocumentReplica(current.State);
                        await connection.Send(new("sync", replica.Difference(message.Vector), replica.StateVector()), cancellation);
                    }
                    else if (message.Type == "update")
                    {
                        if (message.Update == null || message.Vector == null || message.Id is not { Length: > 0 and <= 100 }) throw new InvalidDataException("更新信息缺失");
                        if (current.SyncPaused) { await connection.Send(new("permissions"), cancellation); continue; }
                        try
                        {
                            var accepted = repository.ApplySharedUpdate(account, document, message.Update, message.Vector);
                            using var replica = new SharedDocumentReplica(accepted.State); var vector = replica.StateVector();
                            await Broadcast(room, new("update", message.Update, vector), cancellation, connectionId);
                            await connection.Send(new("ack", Vector: vector, Id: message.Id), cancellation);
                        }
                        catch (SharedAccessException e) when (e.Message == "resync")
                        {
                            using var replica = new SharedDocumentReplica(current.State);
                            await connection.Send(new("sync", replica.Difference(message.Vector), replica.StateVector()), cancellation);
                        }
                        catch (SharedAccessException e) when (e.Status == 423)
                        {
                            // An administrator can pause between reading permissions and committing.
                            await connection.Send(new("permissions"), cancellation);
                        }
                    }
                    else if (message.Type is "presence" or "ping")
                    {
                        if (message.Type == "presence")
                        {
                            connection.Peer = connection.Peer with { BlockId = message.BlockId != null && Guid.TryParse(message.BlockId, out _) ? message.BlockId : null };
                            await Presence(room, cancellation);
                        }
                        else await connection.Send(new("pong"), cancellation);
                    }
                    else throw new InvalidDataException("未知协同消息");
                }
                finally { room.Gate.Release(); }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or JsonException or ArgumentException or SharedAccessException)
        {
            if (e is not (WebSocketException or OperationCanceledException))
            {
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await connection.Send(new("error", Error: e.Message), timeout.Token); }
                catch (Exception sendError) when (sendError is WebSocketException or OperationCanceledException) { }
            }
        }
        finally
        {
            room.Connections.TryRemove(connectionId, out _);
            lock (_roomMembership) { if (--room.Leases == 0) _rooms.TryRemove(document, out _); }
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await Presence(room, timeout.Token); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            if (socket.State == WebSocketState.Open) { try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "连接已结束", timeout.Token); } catch (Exception e) when (e is WebSocketException or OperationCanceledException) { } }
        }
    }
    private bool CanRead(Connection connection)
    {
        try { return repository.Authenticate(connection.Token) == connection.Peer.AccountId && repository.SharedDocument(connection.Peer.AccountId, connection.Document).Document.WorkspaceId == connection.Workspace; }
        catch (SharedAccessException) { return false; }
    }
    private Task Presence(Room room, CancellationToken cancellation)
    {
        var peers = room.Connections.Values.Where(CanRead).Select(connection =>
        {
            var profile = repository.Profile(connection.Peer.AccountId);
            return connection.Peer = connection.Peer with { DisplayName = profile.DisplayName, PublicId = profile.PublicId ?? "", Avatar = profile.Avatar };
        }).ToArray();
        return Broadcast(room, new("peers", Peers: peers), cancellation);
    }
    public Dictionary<string, int> AccountConnections() => _rooms.Values.SelectMany(room => room.Connections.Values).Where(CanRead)
        .GroupBy(connection => connection.Peer.AccountId).ToDictionary(group => group.Key, group => group.Count());
    private async Task Broadcast(Room room, SharedWireMessage message, CancellationToken cancellation, string? except = null)
    {
        foreach (var connection in room.Connections.Values.Where(connection => connection.Peer.ConnectionId != except))
        {
            if (!CanRead(connection))
            {
                room.Connections.TryRemove(connection.Peer.ConnectionId, out _);
                try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await connection.Send(new("error", Error: "访问权限已改变，本机草稿已保留"), timeout.Token); }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
                connection.Socket.Abort(); continue;
            }
            try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(5)); await connection.Send(message, timeout.Token); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { connection.Socket.Abort(); }
        }
    }
    public async Task Recheck(string? account = null, string? workspace = null, string? document = null)
    {
        foreach (var (id, room) in _rooms)
        {
            if (document != null && id != document) continue;
            await room.Gate.WaitAsync();
            try
            {
                foreach (var connection in room.Connections.Values.Where(connection => (account == null || connection.Peer.AccountId == account) && (workspace == null || connection.Workspace == workspace)))
                {
                    try
                    {
                        if (repository.Authenticate(connection.Token) == null) throw new SharedAccessException("登录已失效，请重新登录");
                        _ = repository.SharedDocument(connection.Peer.AccountId, id);
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await connection.Send(new("permissions"), timeout.Token);
                    }
                    catch (Exception e) when (e is SharedAccessException or OperationCanceledException or WebSocketException)
                    {
                        // Remove the revoked connection before broadcasting presence; otherwise that
                        // broadcast aborts the socket and discards the error and close frame below.
                        room.Connections.TryRemove(connection.Peer.ConnectionId, out _);
                        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await connection.Send(new("error", Error: "账号、成员权限或文档状态已改变，请重新打开工作区"), timeout.Token); }
                        catch (Exception sendError) when (sendError is WebSocketException or OperationCanceledException) { }
                        try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "权限已改变", timeout.Token); }
                        catch (Exception closeError) when (closeError is WebSocketException or OperationCanceledException) { connection.Socket.Abort(); }
                    }
                }
                using var presenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await Presence(room, presenceTimeout.Token);
            }
            finally { room.Gate.Release(); }
        }
    }
}
