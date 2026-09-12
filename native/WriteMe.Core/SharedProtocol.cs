using System.Text.Json;
using System.Net;

namespace WriteMe.Core;

public sealed record SharedProfile(string Id, string Username, string? PublicId, string DisplayName, bool IsAdmin, bool Disabled, bool NeedsSetup, SharedAvatar? Avatar = null, bool SyncPaused = false);
public sealed record SharedProfileSetup(string PublicId, string DisplayName, string Password);
public sealed record SharedAccountInput(string Username, string Password, bool IsAdmin = false);
public sealed record SharedAccountChange(bool? Disabled = null, bool? IsAdmin = null, string? Password = null, string? Username = null, string? DisplayName = null, bool? SyncPaused = null);
public sealed record SharedWorkspace(string Id, string Name, string Role, long CreatedAt);
public sealed record SharedWorkspaceInput(string Name);
public sealed record SharedMember(string AccountId, string PublicId, string DisplayName, string Role, SharedAvatar? Avatar = null);
public sealed record SharedMemberInput(string PublicId, string Role = "editor");
public sealed record SharedDocumentInfo(string Id, string WorkspaceId, string Title, long UpdatedAt, bool Deleted = false);
public sealed record SharedDocumentInput(string Title = "无标题");
public sealed record SharedDocumentData(SharedDocumentInfo Document, string Role, byte[] State, int Protocol = 1, bool SyncPaused = false);
public sealed record SharedPeer(string ConnectionId, string AccountId, string DisplayName, string PublicId, string Color, string? BlockId = null, SharedAvatar? Avatar = null);
public sealed record SharedWireMessage(string Type, byte[]? Update = null, byte[]? Vector = null, string? Id = null,
    string? Error = null, SharedPeer[]? Peers = null, string? BlockId = null);

public static class SharedProtocol
{
    public static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("服务器地址无效，请填写完整的 HTTP 或 HTTPS 地址");
        static bool Private(string host)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var bytes = ip.GetAddressBytes();
            return bytes.Length == 4 ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                : (bytes[0] & 0xfe) == 0xfc;
        }
        if (uri.Scheme != "https" && !(uri.Scheme == "http" && (uri.IsLoopback || Private(uri.Host))))
            throw new ArgumentException("公网服务器需要 HTTPS；局域网可以使用私有 IP 地址和 HTTP");
        return uri;
    }
    public const int Version = 1;
    public const int MaximumWireBytes = SharedDocumentReplica.MaximumStateBytes * 4 / 3 + 8192;
    public static bool CanWrite(string role) => role is "owner" or "editor";
    public static bool ValidRole(string role) => role is "owner" or "editor" or "viewer";
    public static byte[] Encode(SharedWireMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, SyncProtocol.Json);
    public static SharedWireMessage Decode(byte[] bytes) => JsonSerializer.Deserialize<SharedWireMessage>(bytes, SyncProtocol.Json)
        ?? throw new InvalidDataException("协同消息为空。");
    public static bool Covers(byte[] actual, byte[] expected)
    {
        static Dictionary<ulong, ulong> Read(byte[] bytes)
        {
            var position = 0;
            ulong Number()
            {
                ulong value = 0; var shift = 0;
                while (position < bytes.Length && shift < 63)
                { var b = bytes[position++]; value |= (ulong)(b & 127) << shift; if (b < 128) return value; shift += 7; }
                throw new InvalidDataException("状态向量无效");
            }
            var count = Number(); if (count > 50000) throw new InvalidDataException("状态向量过大");
            var result = new Dictionary<ulong, ulong>();
            for (ulong i = 0; i < count; i++) if (!result.TryAdd(Number(), Number())) throw new InvalidDataException("状态向量重复");
            if (position != bytes.Length) throw new InvalidDataException("状态向量无效"); return result;
        }
        var known = Read(actual); return Read(expected).All(pair => known.GetValueOrDefault(pair.Key) >= pair.Value);
    }
}
