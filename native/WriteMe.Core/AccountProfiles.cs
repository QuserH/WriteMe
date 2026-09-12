using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace WriteMe.Core;

// Note: 头像、六位密码与管理员数据管理共用账号身份，不改写文档作者 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed record SharedAvatar(string Color = "#6C82AD", string Text = "", string? ImageVersion = null);
public sealed record SharedAvatarChange(string Color, string Text, string? ImageData = null, bool RemoveImage = false);
public sealed record SharedProfileChange(string PublicId, string DisplayName, SharedAvatarChange? Avatar = null);
public sealed record SharedPasswordChange(string CurrentPassword, string Password);
public sealed record SharedAccountUsage(SharedProfile Profile, int PersonalDocuments, int Conflicts, long PersonalBytes,
    int SharedDocuments, long SharedBytes, int Workspaces, int ActiveSessions, long? LastLoginAt, long? LastSyncAt, int OnlineConnections = 0);
public sealed record SharedSessionInfo(string Id, string Device, long? CreatedAt, long? LastSeenAt, long ExpiresAt, bool Current);
public sealed record SharedWorkspaceUsage(string Id, string Name, string Role, int Documents, int DeletedDocuments, long Bytes, long? UpdatedAt);
public sealed record SharedStoredDocument(string Id, string Title, int Versions, long Bytes, bool Deleted);
public sealed record SharedAccountData(SharedAccountUsage Usage, SharedWorkspaceUsage[] Workspaces, SharedStoredDocument[] PersonalDocuments,
    SharedSessionInfo[] Sessions, int AssetCount, long AssetBytes);

public static class AccountProfiles
{
    public const int MinimumPasswordLength = 6;
    public const int MaximumAvatarBytes = 128 * 1024;
    public static void ValidatePassword(string? value)
    {
        if (value is not { Length: >= MinimumPasswordLength and <= 1024 }) throw new ArgumentException("密码需要 6–1024 个字符");
    }
    public static string PublicId(string? value)
    {
        var id = (value ?? "").Trim().TrimStart('@').ToLowerInvariant();
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9_-]{2,31}$", RegexOptions.CultureInvariant)) throw new ArgumentException("ID 使用 3–32 位字母、数字、下划线或短横线");
        return id;
    }
    public static byte[]? ValidateAvatar(SharedAvatarChange value)
    {
        if (!Regex.IsMatch(value.Color ?? "", "^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant) || value.Text is null || value.Text.Length > 8 || value.Text.Any(char.IsControl))
            throw new ArgumentException("头像文字或颜色无效");
        if (value.ImageData == null) return null;
        const string prefix = "data:image/png;base64,";
        if (!value.ImageData.StartsWith(prefix, StringComparison.Ordinal) || value.ImageData.Length > MaximumAvatarBytes * 4 / 3 + 32)
            throw new ArgumentException("头像需要是裁剪后的 PNG 图片");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(value.ImageData[prefix.Length..]); }
        catch (FormatException) { throw new ArgumentException("头像图片无效"); }
        if (bytes.Length is < 33 or > MaximumAvatarBytes || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8, 4)) != 13)
            throw new ArgumentException("头像图片无效");
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)); var height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 256 || height != width) throw new ArgumentException("头像应为不超过 256 像素的正方形图片");
        return bytes;
    }
}
