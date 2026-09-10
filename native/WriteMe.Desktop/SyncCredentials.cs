using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.Desktop;

public sealed record SavedSyncConnection(string Endpoint, string Username, SyncLoginResult Login);

// Note: 令牌只以当前 Windows 用户 DPAPI 密文持久化，备份不包含凭据 — 见 .agents/notes/implemented/architecture/2026-09-08-sync-docker-crdt.md
public static class SyncCredentials
{
    private static byte[] Entropy(NoteStore store) => SHA256.HashData(Encoding.UTF8.GetBytes("WriteME.Sync|" + Path.GetFullPath(store.DatabasePath).ToUpperInvariant()));
    public static SavedSyncConnection? Load(NoteStore store)
    {
        if (!OperatingSystem.IsWindows() || store.Setting("sync_credentials") is not { Length: > 0 } saved) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved), Entropy(store), DataProtectionScope.CurrentUser);
            try
            {
                var connection = JsonSerializer.Deserialize<SavedSyncConnection>(bytes, SyncProtocol.Json);
                if (connection?.Login == null || connection.Login.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return null;
                _ = SyncProtocol.Endpoint(connection.Endpoint); return connection;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException) { return null; }
    }
    public static bool Save(NoteStore store, SavedSyncConnection connection)
    {
        store.SetSetting("sync_endpoint", connection.Endpoint); store.SetSetting("sync_username", connection.Username);
        if (!OperatingSystem.IsWindows()) { Clear(store); return false; }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(connection, SyncProtocol.Json);
        try { store.SetSetting("sync_credentials", Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(store), DataProtectionScope.CurrentUser))); return true; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void Clear(NoteStore store) => store.SetSetting("sync_credentials", "");
}
