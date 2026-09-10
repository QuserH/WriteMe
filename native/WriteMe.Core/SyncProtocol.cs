using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace WriteMe.Core;

public sealed record SyncVersion(string Id, Dictionary<string, long> Clock, string? Payload);
public sealed record SyncEntity(string Key, SyncVersion[] Versions);
public sealed record SyncDocumentPayload(PortableDocument Document, AssetInfo[] Assets);
public sealed record SyncRequest(long Cursor, SyncEntity[] Entities);
public sealed record SyncResponse(long Cursor, bool HasMore, SyncEntity[] Entities);
public sealed record SyncLogin(string Username, string Password);
public sealed record SyncLoginResult(string Token, string AccountId, long ExpiresAt);
public sealed record SyncConflict(string Title, SyncEntity Entity);

// Note: 多值寄存器 CRDT 保留并发版本，禁止时间戳覆盖 — 见 .agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md
public static class SyncProtocol
{
    public const int MaximumBatchBytes = 24 * 1024 * 1024;
    public const int MaximumEntityBytes = 16 * 1024 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 256, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 128 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static (string Kind, string Id) Subject(string key)
    {
        var parts = key.Split('/');
        if (parts.Length != 2 || parts[0] is not ("document" or "space" or "folder") || !ValidId(parts[1])) throw new InvalidDataException("无效的同步对象标识");
        return (parts[0], parts[1]);
    }
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw new InvalidDataException("同步内容为空");
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static void Validate(SyncEntity entity, bool allowEmpty = false)
    {
        if (entity == null || string.IsNullOrEmpty(entity.Key) || entity.Versions == null || entity.Versions.Length > 32 || !allowEmpty && entity.Versions.Length == 0)
            throw new InvalidDataException("无效的同步版本集合");
        var (kind, id) = Subject(entity.Key);
        var bytes = 0L; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in entity.Versions)
        {
            if (version == null || version.Id == null || version.Clock is not { Count: > 0 and <= 256 } || !seen.Add(version.Id)) throw new InvalidDataException("同步版本缺少唯一标识或时钟");
            var separator = version.Id.IndexOf(':');
            if (separator < 1 || !ValidId(version.Id[..separator]) || !long.TryParse(version.Id[(separator + 1)..], out var counter)
                || !version.Clock.TryGetValue(version.Id[..separator], out var own) || own != counter)
                throw new InvalidDataException("同步版本标识与时钟不符");
            foreach (var (actor, tick) in version.Clock) if (!ValidId(actor) || tick is <= 0 or > 9_000_000_000_000_000) throw new InvalidDataException("同步时钟无效");
            if (version.Payload == null) continue;
            bytes += Encoding.UTF8.GetByteCount(version.Payload);
            if (bytes > MaximumEntityBytes) throw new InvalidDataException("并发版本超过同步大小限制");
            ValidatePayload(kind, id, version.Payload);
        }
        if (JsonSerializer.SerializeToUtf8Bytes(entity, Json).Length > MaximumBatchBytes - 131072)
            throw new InvalidDataException("同步版本编码后超过单批大小限制，请缩小文档");
    }

    private static void ValidatePayload(string kind, string id, string payload)
    {
        switch (kind)
        {
            case "space":
                var space = Read<SpaceInfo>(payload);
                if (space.Id != id || !ValidName(space.Name)) throw new InvalidDataException("同步空间无效");
                break;
            case "folder":
                var folder = Read<FolderInfo>(payload);
                if (folder.Id != id || !ValidId(folder.SpaceId) || folder.ParentId != null && !ValidId(folder.ParentId) || !ValidName(folder.Name)) throw new InvalidDataException("同步文件夹无效");
                break;
            case "document":
                var content = Read<SyncDocumentPayload>(payload); var document = content.Document;
                if (document == null || document.Id != id || document.Title == null || document.Title.Length > 65536 || !ValidId(document.SpaceId)
                    || document.FolderId != null && !ValidId(document.FolderId) || content.Assets == null || content.Assets.Length > 10000
                    || document.CreatedAt is < -62135596800000 or > 253402300799999 || document.UpdatedAt is < -62135596800000 or > 253402300799999
                    || document.DailyDate != null && !DateOnly.TryParseExact(document.DailyDate, "yyyy-MM-dd", out _))
                    throw new InvalidDataException("同步笔记元信息无效");
                if (document.Content == null || document.Appearance == null) throw new InvalidDataException("同步笔记缺少内容");
                var root = NoteJson.ParseStrict(document.Content); NoteStore.ValidateAppearance(document.Appearance);
                var assets = new HashSet<string>();
                foreach (var asset in content.Assets)
                {
                    if (asset == null || asset.Id == null || !NoteStore.IsAssetId(asset.Id) || !assets.Add(asset.Id) || asset.Size is < 0 or > NoteStore.MaximumAssetSize
                        || asset.Name is not { Length: > 0 and <= 240 } || asset.Name.Any(character => char.IsControl(character) || character is '/' or '\\')
                        || asset.MediaType is not { Length: > 0 and <= 100 }) throw new InvalidDataException("同步附件清单无效");
                }
                if (DocumentAssets.Read(root, document.Appearance).Any(asset => !assets.Contains(asset))) throw new InvalidDataException("同步笔记缺少附件清单");
                break;
        }
    }
    private static bool ValidName(string? name) => name is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    private static bool After(SyncVersion a, SyncVersion b) => b.Clock.All(pair => a.Clock.GetValueOrDefault(pair.Key) >= pair.Value)
        && a.Clock.Any(pair => pair.Value > b.Clock.GetValueOrDefault(pair.Key));

    public static SyncEntity Merge(SyncEntity a, SyncEntity b)
    {
        if (a.Key != b.Key) throw new ArgumentException("只能合并同一同步对象");
        Validate(a, true); Validate(b, true);
        var versions = new Dictionary<string, SyncVersion>(StringComparer.Ordinal);
        foreach (var version in a.Versions.Concat(b.Versions))
        {
            if (versions.TryGetValue(version.Id, out var previous) && (previous.Payload != version.Payload || previous.Clock.Count != version.Clock.Count || previous.Clock.Any(pair => version.Clock.GetValueOrDefault(pair.Key) != pair.Value)))
                throw new InvalidDataException("同一个同步版本的内容不一致");
            versions[version.Id] = version;
        }
        var frontier = versions.Values.Where(version => !versions.Values.Any(other => other.Id != version.Id && After(other, version))).OrderBy(version => version.Id, StringComparer.Ordinal).ToArray();
        var merged = new SyncEntity(a.Key, frontier); Validate(merged, true); return merged;
    }
    public static SyncVersion? Preferred(SyncEntity entity) => entity.Versions.Where(version => version.Payload != null).OrderByDescending(version => version.Id, StringComparer.Ordinal).FirstOrDefault()
        ?? entity.Versions.OrderByDescending(version => version.Id, StringComparer.Ordinal).FirstOrDefault();
    public static bool HasConflict(SyncEntity entity) => entity.Versions.Select(version => version.Payload).Distinct(StringComparer.Ordinal).Skip(1).Any();
    public static string Canonical(SyncEntity entity) => Encode(entity with
    {
        Versions = entity.Versions.OrderBy(version => version.Id, StringComparer.Ordinal).Select(version => version with { Clock = version.Clock.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }).ToArray()
    });

    public static IEnumerable<AssetInfo> Assets(IEnumerable<SyncEntity> entities) => entities.Where(entity => Subject(entity.Key).Kind == "document")
        .SelectMany(entity => entity.Versions).Where(version => version.Payload != null).SelectMany(version => Read<SyncDocumentPayload>(version.Payload!).Assets).DistinctBy(asset => asset.Id);

    public static Uri Endpoint(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) throw new ArgumentException("服务器地址需要 HTTPS；本机 localhost 可使用 HTTP");
        return uri;
    }

    public static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, CancellationToken cancellation = default)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("同步请求或响应过大");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
        return output.ToArray();
    }
}
