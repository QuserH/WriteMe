using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class StoreTests
{
    [Fact]
    public void OnlineBackupImportsWalDataAndNeverWritesIntoTheLegacyDatabase()
    {
        using var temporary = new TestDirectory();
        using var legacy = new NoteStore(Path.Combine(temporary.Path, "legacy"));
        var original = legacy.Create("原版标题", WelcomeDocument.Create());
        var before = legacy.Get(original.Id);
        using (var native = new NoteStore(Path.Combine(temporary.Path, "native"), legacy.DatabasePath))
        {
            Assert.True(native.ImportedLegacy);
            Assert.Equal(before, native.Get(before.Id));
            native.Save(before.Id, "原生版修改", NoteNode.EmptyDocument());
            Assert.Equal(before, legacy.Get(before.Id));
        }
        using var reopen = new NoteStore(Path.Combine(temporary.Path, "native"), legacy.DatabasePath);
        Assert.False(reopen.ImportedLegacy);
        Assert.Equal("原生版修改", reopen.Get(before.Id).Title);
        Assert.Equal(before, legacy.Get(before.Id));
    }

    [Fact]
    public void SaveReopenKeepsEveryFoldMarkAndEmptyParagraph()
    {
        using var temporary = new TestDirectory();
        string id;
        string content;
        using (var store = new NoteStore(temporary.Path))
        {
            var doc = store.Create("中文🙂", WelcomeDocument.Create());
            var session = new DocumentSession(NoteJson.Parse(doc.Content));
            session.ToggleAll();
            session.Format(0, 2, NoteMark.With("link", "href", "https://example.com"));
            content = NoteJson.Serialize(session.Root);
            id = doc.Id;
            store.Save(doc.Id, doc.Title, session.Root);
        }
        using var reopen = new NoteStore(temporary.Path);
        Assert.Equal(content, reopen.Get(id).Content);
        Assert.Equal("中文🙂", reopen.Get(id).Title);
    }

    [Fact]
    public void MissingDocumentSaveFailsInsteadOfSilentlyClaimingSuccess()
    {
        using var temporary = new TestDirectory();
        using var store = new NoteStore(temporary.Path);
        Assert.Throws<IOException>(() => store.Save("missing", "标题", NoteNode.EmptyDocument()));
        Assert.Empty(store.List());
    }
}

internal sealed class TestDirectory : IDisposable
{
    private static readonly string TestRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "writeme-native-tests"));
    public string Path { get; } = System.IO.Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        var full = System.IO.Path.GetFullPath(Path);
        if (!full.StartsWith(TestRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("测试清理路径越界");
        Directory.Delete(full, true);
    }
}
