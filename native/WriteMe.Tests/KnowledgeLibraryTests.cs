using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class KnowledgeLibraryTests
{
    private static NoteNode Doc(params NoteNode[] nodes) => new("doc") { Content = [.. nodes] };
    private static NoteNode Linked(string label, string target) => NoteNode.Paragraph() with { Content = [new("text") { Text = label, Marks = [NoteMark.With("noteLink", "documentId", target)] }] };

    [Fact]
    public void TagsRespectUnicodeBoundariesAndSkipCodeUrlsAndLinkedText()
    {
        var paragraph = NoteNode.Paragraph() with { Content = [new("text") { Text = "#标签 #Café #CAFE\u0301 x#忽略 https://site/#跳过 " }, new("text") { Text = "#代码", Marks = [new("code")] }, new("text") { Text = " #链接", Marks = [NoteMark.With("link", "href", "https://example.com")] }] };
        var refs = NoteReferences.Read(Doc(paragraph, NoteNode.Paragraph("#程序") with { Type = "codeBlock" }, NoteNode.Toggle("隐藏", NoteNode.Paragraph("#深处")).WithAttr("collapsed", true)));
        Assert.Equal(new[] { "标签", "Café", "CAFÉ", "深处" }, refs.Select(reference => reference.Span.Label));
        Assert.Equal(refs[1].Span.Target, refs[2].Span.Target);
        Assert.Null(NoteReferences.NormalizeTag(new string('x', 65)));
    }

    [Fact]
    public void NoteLinksKeepFormattingAndStopAtTheirEdgesWithSingleUndo()
    {
        var session = new DocumentSession(Doc(NoteNode.Paragraph("前目标后")));
        session.Format(1, 2, new("bold")); var before = NoteJson.Serialize(session.Root);
        Assert.True(session.InsertNoteLink(1, 2, "target", "别名", true));
        var linked = NoteJson.Serialize(session.Root);
        var span = Assert.Single(NoteReferences.Read(NoteJson.Parse(linked)));
        Assert.Equal("目标", span.Span.Label); Assert.Equal("target", span.Span.Target);
        Assert.Contains(session.Root.Content[0].Content[1].Marks, mark => mark.Type == "bold");
        session.Undo(); Assert.Equal(before, NoteJson.Serialize(session.Root)); session.Redo(); Assert.Equal(linked, NoteJson.Serialize(session.Root));
        session.Edit(3, 0, "追加", false);
        Assert.Equal("目标", Assert.Single(NoteReferences.Read(session.Root)).Span.Label);
        Assert.False(session.InsertNoteLink(-1, 0, "id", "无效"));
    }

    [Fact]
    public void ReferenceIndexesFavoriteAndDeletedTargetsSurviveReopen()
    {
        using var temporary = new TestDirectory(); string target; string source; long edited;
        using (var store = new NoteStore(temporary.Path))
        {
            target = store.Create("目标").Id;
            source = store.Create("来源", Doc(NoteNode.Toggle("父级", Linked("链接别名", target), NoteNode.Paragraph("#标签 #标签" )).WithAttr("collapsed", true))).Id;
            edited = store.Get(source).UpdatedAt; store.SetFavorite(source, true);
            Assert.Equal(edited, store.Get(source).UpdatedAt); Assert.Equal(1, Assert.Single(store.Tags()).Count);
            store.Save(target, "改名后的目标", NoteNode.EmptyDocument());
            Assert.Equal("改名后的目标", Assert.Single(store.OutgoingLinks(source)).Title);
            var backlink = Assert.Single(store.Backlinks(target)); Assert.Equal("0/1", backlink.Path);
            var session = new DocumentSession(NoteJson.Parse(store.Get(source).Content)); var node = NoteReferences.AtPath(session.Root, backlink.Path)!;
            Assert.True(session.Reveal(node.Id)); Assert.Contains("链接别名", session.Projection.Text);
        }
        using var reopened = new NoteStore(temporary.Path);
        Assert.True(reopened.Get(source).IsFavorite); Assert.Equal(edited, reopened.Get(source).UpdatedAt);
        reopened.Delete(target); Assert.False(Assert.Single(reopened.OutgoingLinks(source)).Exists);
        Assert.Single(NoteReferences.Read(NoteJson.Parse(reopened.Get(source).Content)), reference => reference.Span.Kind == ReferenceKind.Note);
        reopened.Delete(source); Assert.Empty(reopened.Tags());
    }

    [Fact]
    public void FailedReferenceIndexWriteRollsBackBodyTitleAndPreviousIndexes()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var doc = store.Create("原文", Doc(NoteNode.Paragraph("#原标签")));
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = store.DatabasePath, Pooling = false }.ToString()); database.Open();
        using var fail = database.CreateCommand(); fail.CommandText = "CREATE TRIGGER reject_link BEFORE INSERT ON document_links BEGIN SELECT RAISE(ABORT,'test rollback'); END"; fail.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Save(doc.Id, "错误标题", Doc(Linked("失败链接", "target"))));
        Assert.Equal(doc, store.Get(doc.Id)); Assert.Equal("原标签", Assert.Single(store.Tags()).Name);
        Assert.Single(store.Query(new(Text: "原标签"))); Assert.Empty(store.Query(new(Text: "错误标题")));
    }

    [Fact]
    public void LegacySchemaBuildsSearchAndReferenceIndexesWithoutChangingBody()
    {
        using var temporary = new TestDirectory(); var file = Path.Combine(temporary.Path, "writeme.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE documents(id TEXT PRIMARY KEY,title TEXT NOT NULL,content TEXT NOT NULL,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL); INSERT INTO documents VALUES('old','旧笔记','中文正文 #旧标签',1,2)"; command.ExecuteNonQuery();
        }
        using var store = new NoteStore(temporary.Path);
        Assert.Equal("中文正文 #旧标签", store.Get("old").Content); Assert.Equal("personal", store.Location("old").SpaceId);
        Assert.Equal("old", Assert.Single(store.Query(new(Text: "正文"))).Document.Id); Assert.Equal("旧标签", Assert.Single(store.Tags()).Name);
    }

    [Fact]
    public void FolderMovesRejectCyclesAndDeletingContainersPreservesDocuments()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var space = store.CreateSpace("项目"); var other = store.CreateSpace("其他");
        var parent = store.CreateFolder(space.Id, "父级"); var child = store.CreateFolder(space.Id, "子级", parent.Id);
        var unrelated = store.CreateFolder(other.Id, "外部");
        var doc = store.Create("文档", spaceId: space.Id, folderId: child.Id);
        Assert.Throws<ArgumentException>(() => store.MoveFolder(parent.Id, child.Id));
        Assert.Throws<ArgumentException>(() => store.MoveFolder(child.Id, unrelated.Id));
        Assert.Throws<ArgumentException>(() => store.MoveDocument(doc.Id, other.Id, child.Id));
        Assert.Single(store.Query(new(space.Id, parent.Id)));
        store.DeleteContainer(child.Id, false); Assert.Equal(parent.Id, store.Location(doc.Id).FolderId);
        store.DeleteContainer(space.Id, true); Assert.Equal("personal", store.Location(doc.Id).SpaceId); Assert.Null(store.Location(doc.Id).FolderId); Assert.Equal("文档", store.Get(doc.Id).Title);
        Assert.Throws<ArgumentException>(() => store.DeleteContainer("personal", true));
    }

    [Theory]
    [InlineData("中")]
    [InlineData("中文")]
    [InlineData("中文检索")]
    [InlineData("QUOTED")]
    [InlineData("\"引号\"")]
    public void FullTextSearchFindsHiddenTextAndReturnsRecoverablePaths(string query)
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var doc = store.Create("标题", Doc(NoteNode.Toggle("折叠", NoteNode.Paragraph("测试中文检索 quoted 与 \"引号\" 正文")).WithAttr("collapsed", true)));
        var result = Assert.Single(store.Query(new(Text: query))); Assert.Equal(doc.Id, result.Document.Id); Assert.Equal("0/1", result.Path);
        var block = NoteReferences.AtPath(NoteJson.Parse(doc.Content), result.Path!); Assert.NotNull(block);
        Assert.Equal(query, RichText.Plain(block!).Substring(result.Start, result.Length), ignoreCase: true);
        store.Save(doc.Id, doc.Title, Doc(NoteNode.Paragraph("替换正文"))); Assert.Empty(store.Query(new(Text: query)));
    }

    [Fact]
    public void SearchFiltersComposeAndRecentUsesOpenTimeInsteadOfAutosave()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var space = store.CreateSpace("工作"); var folder = store.CreateFolder(space.Id, "研究");
        var a = store.Create("A", Doc(NoteNode.Paragraph("共同正文 #项目")), space.Id, folder.Id);
        var b = store.Create("B", Doc(NoteNode.Paragraph("共同正文 #项目"))); store.SetFavorite(a.Id, true); store.MarkOpened(a.Id);
        store.Save(b.Id, "新标题", NoteNode.EmptyDocument());
        Assert.Equal(a.Id, Assert.Single(store.Query(new(space.Id, folder.Id, "favorites", "项目", "共同"))).Document.Id);
        Assert.Equal(a.Id, Assert.Single(store.Query(new(Mode: "recent"))).Document.Id);
        store.SetTrashed(a.Id, true); Assert.Empty(store.Query(new(Text: "共同"))); Assert.Single(store.Query(new(Mode: "trash")));
        store.SetTrashed(a.Id, false); Assert.Single(store.Query(new(Text: "共同")));
    }

    [Fact]
    public void MarkdownRoundtripKeepsCommonBlocksLinksAndCustomTogglePayloads()
    {
        var markdown = "# 标题\n\n正文 **粗体** 与 *斜体* 和 [目标](writeme://note/stable-id)\n\n- [x] 已完成\n- [ ] 待办\n\n```csharp\nvar a = 1;\n```\n\n> 引用";
        var root = NoteMarkdown.Parse(markdown);
        Assert.Contains(root.Content, node => node.Type == "taskList" && node.Content[0].Bool("checked"));
        Assert.Equal("stable-id", Assert.Single(NoteReferences.Read(root)).Span.Target);
        root = root with { Content = root.Content.Add(NoteNode.Toggle("折叠", NoteNode.Paragraph("子级")).WithAttr("collapsed", true)) };
        var restored = NoteMarkdown.Parse(NoteMarkdown.Export(root));
        Assert.Equal(DocumentText.Plain(root), DocumentText.Plain(restored));
        Assert.True(restored.Content.Last().Bool("collapsed"));
        Assert.Contains(NoteTree.Descendants(restored), node => node.Marks.Any(mark => mark.Type == "bold"));
    }

    [Fact]
    public void MarkdownAssetsImportFromSidecarsIncludingNestedCustomBlocks()
    {
        using var temporary = new TestDirectory();
        using var original = new NoteStore(Path.Combine(temporary.Path, "original"));
        using var target = new NoteStore(Path.Combine(temporary.Path, "target"));
        using var text = new MemoryStream("资料正文"u8.ToArray()); var attachment = original.ImportAsset(text, "资料.txt");
        using var image = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j/RsAAAAASUVORK5CYII="));
        var picture = original.ImportAsset(image, "预览.png");
        var root = Doc(DocumentAssets.Node(attachment, false), DocumentAssets.Node(picture, true),
            NoteNode.Toggle("隐藏素材", DocumentAssets.Node(attachment, false), DocumentAssets.Node(picture, true)).WithAttr("collapsed", true));
        var exportDirectory = Path.Combine(temporary.Path, "export"); Directory.CreateDirectory(Path.Combine(exportDirectory, "assets"));
        foreach (var id in DocumentAssets.Read(root)) File.Copy(original.AssetPath(id)!, Path.Combine(exportDirectory, "assets", id));

        var parsed = NoteMarkdown.Parse(NoteMarkdown.Export(root));
        Assert.Equal("attachment", parsed.Content[0].Type);
        var imported = target.ImportLocalAssets(parsed, exportDirectory);
        Assert.Equal(DocumentAssets.Read(root).Order(), DocumentAssets.Read(imported).Order());
        Assert.Equal("资料正文", File.ReadAllText(target.AssetPath(attachment.Id)!));
        Assert.Equal("预览.png", target.Asset(picture.Id)!.Name); Assert.Equal("image/png", target.Asset(picture.Id)!.MediaType);
        var toggle = imported.Content.Last(); Assert.True(toggle.Bool("collapsed"));
        Assert.Equal(attachment.Id, toggle.Content[1].String("assetId")); Assert.Equal(picture.Id, toggle.Content[2].String("assetId"));
    }

    [Fact]
    public void NestedSidecarImportVerifiesTheDeclaredContentHash()
    {
        using var temporary = new TestDirectory(); using var target = new NoteStore(Path.Combine(temporary.Path, "target"));
        var exportDirectory = Path.Combine(temporary.Path, "export"); Directory.CreateDirectory(Path.Combine(exportDirectory, "assets"));
        var id = new string('A', 64); File.WriteAllText(Path.Combine(exportDirectory, "assets", id), "损坏的文件");
        var root = Doc(NoteNode.Toggle("折叠", DocumentAssets.Node(new(id, "资料.txt", "text/plain", 0), false)));
        Assert.Throws<InvalidDataException>(() => target.ImportLocalAssets(root, exportDirectory));
        Assert.Null(target.Asset(id)); Assert.Empty(Directory.GetFiles(target.AssetDirectory));
    }

    [Fact]
    public void LocalImageImportDoesNotReadOutsideTheSelectedDocumentDirectory()
    {
        using var temporary = new TestDirectory(); using var target = new NoteStore(Path.Combine(temporary.Path, "target"));
        var exportDirectory = Path.Combine(temporary.Path, "export"); Directory.CreateDirectory(exportDirectory);
        var outside = Path.Combine(temporary.Path, "outside.png"); File.WriteAllBytes(outside, [1, 2, 3]);
        var root = Doc(new NoteNode("image").WithAttr("src", "../outside.png"), new NoteNode("image").WithAttr("src", "%2e%2e/outside.png"),
            new NoteNode("image").WithAttr("src", outside), new NoteNode("image").WithAttr("src", "https://example.com/image.png"));
        var imported = target.ImportLocalAssets(root, exportDirectory);
        Assert.Equal(NoteJson.Serialize(root), NoteJson.Serialize(imported)); Assert.Empty(Directory.GetFiles(target.AssetDirectory));
    }

    [Fact]
    public void BackupImportsCopiesWithRemappedLinksAppearanceAndContentAddressedAssets()
    {
        using var temporary = new TestDirectory(); using var original = new NoteStore(Path.Combine(temporary.Path, "original")); using var target = new NoteStore(Path.Combine(temporary.Path, "target"));
        var space = original.CreateSpace("项目"); var folder = original.CreateFolder(space.Id, "素材");
        using var bytes = new MemoryStream("附件原文"u8.ToArray()); var asset = original.ImportAsset(bytes, "参考.txt");
        var one = original.Create("目标", spaceId: space.Id, folderId: folder.Id);
        var two = original.Create("来源", Doc(Linked("稳定别名", one.Id), DocumentAssets.Node(asset, false)), space.Id, folder.Id);
        original.SetFavorite(two.Id, true); original.SetAppearance(two.Id, new("serif", 19, 1200, "#FFFDF5"));
        using var backup = new MemoryStream(); original.ExportBackup(backup); backup.Position = 0;
        var ids = target.ImportBackup(backup); Assert.Equal(2, ids.Count); Assert.DoesNotContain(one.Id, ids);
        var imported = target.List().Single(document => document.Title == "来源"); var importedTarget = target.List().Single(document => document.Title == "目标");
        Assert.True(imported.IsFavorite); Assert.Equal("serif", target.Appearance(imported.Id).Font);
        Assert.Equal(importedTarget.Id, Assert.Single(target.OutgoingLinks(imported.Id)).DocumentId);
        Assert.Equal("附件原文", File.ReadAllText(target.AssetPath(asset.Id)!)); Assert.Equal("素材", target.Folders().Single().Name);
        backup.Position = 0; target.ImportBackup(backup); Assert.Equal(4, target.List().Count);
    }

    [Fact]
    public void DamagedAssetIsRejectedBeforeAnyImportedDocumentsAreCreated()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        using var input = new MemoryStream("bad"u8.ToArray()); Assert.Throws<InvalidDataException>(() => store.ImportAsset(input, "asset.txt", new string('A', 64)));
        Assert.Null(store.AssetPath("../../outside")); Assert.Empty(store.List());
        Assert.Empty(Directory.GetFiles(store.AssetDirectory));
    }

    [Theory]
    [InlineData("{\"Version\":1,\"Spaces\":[],\"Folders\":[],\"Documents\":[],\"Assets\":[null]}")]
    [InlineData("{\"Version\":1,\"Spaces\":[null],\"Folders\":[],\"Documents\":[],\"Assets\":[]}")]
    [InlineData("{\"Version\":1,\"Spaces\":[],\"Folders\":[],\"Documents\":[null],\"Assets\":[]}")]
    [InlineData("{\"Version\":1,\"Spaces\":[],\"Folders\":[],\"Documents\":[],\"Assets\":[{\"Id\":\"invalid\",\"Name\":\"x\",\"Size\":-1}]}")]
    public void MalformedBackupManifestsCannotCreatePartialDocumentsOrAssets(string manifest)
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path); using var input = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open()); writer.Write(manifest); }
        input.Position = 0; Assert.Throws<InvalidDataException>(() => store.ImportBackup(input)); Assert.Empty(store.List()); Assert.Empty(Directory.GetFiles(store.AssetDirectory));
    }

    [Fact]
    public void NoOpSaveDoesNotCreateRevisionsOrReplaceAConflictFrontier()
    {
        using var temporary = new TestDirectory(); using var first = new NoteStore(Path.Combine(temporary.Path, "a")); using var second = new NoteStore(Path.Combine(temporary.Path, "b"));
        var document = first.Create("原文", Doc(NoteNode.Paragraph("内容"))); var before = first.SyncState("document/" + document.Id)!;
        first.Save(document.Id, document.Title, NoteJson.Parse(document.Content)); Assert.Equal(document, first.Get(document.Id)); Assert.Empty(first.Revisions(document.Id));
        Assert.Equal(SyncProtocol.Canonical(before), SyncProtocol.Canonical(first.SyncState(before.Key)!));
        second.ApplySync("test", new(1, false, [before])); first.Save(document.Id, "A", Doc(NoteNode.Paragraph("A"))); second.Save(document.Id, "B", Doc(NoteNode.Paragraph("B")));
        first.ApplySync("test", new(2, false, [second.SyncState(before.Key)!])); var conflict = first.SyncState(before.Key)!; var preferred = first.Get(document.Id);
        first.Save(document.Id, preferred.Title, NoteJson.Parse(preferred.Content)); Assert.Equal(SyncProtocol.Canonical(conflict), SyncProtocol.Canonical(first.SyncState(before.Key)!));
    }

    [Fact]
    public async Task DailyNoteIsUniqueAcrossConnectionsAndRestoresFromTrash()
    {
        using var temporary = new TestDirectory(); using var first = new NoteStore(temporary.Path); using var second = new NoteStore(temporary.Path);
        var date = new DateOnly(2026, 9, 10);
        var documents = await Task.WhenAll(Task.Run(() => first.DailyNote(date)), Task.Run(() => second.DailyNote(date)));
        Assert.Equal(documents[0].Id, documents[1].Id); Assert.Single(first.List());
        first.SetTrashed(documents[0].Id, true); Assert.Equal(documents[0].Id, second.DailyNote(date).Id); Assert.Single(first.List());
    }

    [Fact]
    public void PreferencesPageMetadataAndLocalRevisionsSurviveRestartWithoutChangingUndo()
    {
        using var temporary = new TestDirectory(); string id;
        using (var store = new NoteStore(temporary.Path))
        {
            id = store.Create("原始标题", Doc(NoteNode.Paragraph("原始内容"))).Id;
            store.SetPreferences(new("dark", .6)); store.SetAppearance(id, new("mono", 20, 1200));
            store.Save(id, "新标题", Doc(NoteNode.Paragraph("新内容")));
            Assert.Equal("原始内容", DocumentText.Plain(NoteJson.Parse(Assert.Single(store.Revisions(id)).Content)));
        }
        using var reopened = new NoteStore(temporary.Path);
        Assert.Equal(new("dark", .6), reopened.Preferences()); Assert.Equal("mono", reopened.Appearance(id).Font);
        var session = new DocumentSession(NoteJson.Parse(reopened.Get(id).Content)); session.RestoreSnapshot(NoteJson.Parse(reopened.Revisions(id)[0].Content));
        Assert.Equal("原始内容", session.Projection.Text); session.Undo(); Assert.Equal("新内容", session.Projection.Text);
    }
}
