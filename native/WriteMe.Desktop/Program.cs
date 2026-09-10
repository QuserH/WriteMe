using Avalonia;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WriteMe.Core;

namespace WriteMe.Desktop;

internal static class Program
{
    public static string DataDirectory { get; private set; } = NoteStore.DefaultDirectory;
    public static bool ImportLegacy { get; private set; } = true;

    [STAThread]
    public static int Main(string[] args)
    {
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && dataIndex + 1 < args.Length)
        {
            DataDirectory = Path.GetFullPath(args[dataIndex + 1]);
            ImportLegacy = false;
        }
        if (args.Contains("--smoke-test", StringComparer.Ordinal)) return SmokeTest();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(DataDirectory).ToUpperInvariant())))[..24];
        using var mutex = new Mutex(true, "WriteME.Native." + identity, out var first);
        if (!first) return 0;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception ex)
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "startup-error.log"), $"{DateTimeOffset.Now:O}\n{ex}\n");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static int SmokeTest()
    {
        if (ImportLegacy || Directory.Exists(DataDirectory) && Directory.EnumerateFileSystemEntries(DataDirectory).Any())
        {
            Console.Error.WriteLine("安装检查需要使用 --data-dir 指定一个空目录"); return 2;
        }
        using (var store = new NoteStore(DataDirectory))
        {
            var root = NoteMarkdown.Parse("# 安装检查\n\n中文保存与检索 #验证"); var document = store.Create("WriteME 安装检查", root);
            store.Save(document.Id, "WriteME 安装检查完成", root);
            if (store.Query(new(Text: "中文保存")).Count != 1 || store.Tags().Count != 1 || store.SyncState("document/" + document.Id) == null)
                throw new InvalidOperationException("本地存储检查失败");
            File.WriteAllText(Path.Combine(DataDirectory, "smoke-test.json"), JsonSerializer.Serialize(new
            {
                status = "ok", runtime = Environment.Version.ToString(), sqlite = true, fullTextSearch = true,
                documentTransactions = true, webView = false, directory = DataDirectory
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
