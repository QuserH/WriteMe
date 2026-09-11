using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.Desktop;

public static class SharedCredentials
{
    private static byte[] Entropy(NoteStore store) => SHA256.HashData(Encoding.UTF8.GetBytes("WriteME.Shared|" + Path.GetFullPath(store.DatabasePath).ToUpperInvariant()));
    public static SavedSyncConnection? Load(NoteStore store)
    {
        if (!OperatingSystem.IsWindows() || store.Setting("shared_credentials") is not { Length: > 0 } saved) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(saved), Entropy(store), DataProtectionScope.CurrentUser);
            try { var connection = JsonSerializer.Deserialize<SavedSyncConnection>(bytes, SyncProtocol.Json); if (connection?.Login == null || connection.Login.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) return null; _ = SharedProtocol.Endpoint(connection.Endpoint); return connection; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException or ArgumentException) { return null; }
    }
    public static void Save(NoteStore store, SavedSyncConnection connection)
    {
        store.SetSetting("shared_endpoint", connection.Endpoint); store.SetSetting("shared_username", connection.Username);
        if (!OperatingSystem.IsWindows()) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(connection, SyncProtocol.Json);
        try { store.SetSetting("shared_credentials", Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy(store), DataProtectionScope.CurrentUser))); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static void Clear(NoteStore store) => store.SetSetting("shared_credentials", "");
}
