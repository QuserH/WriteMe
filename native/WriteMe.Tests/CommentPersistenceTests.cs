using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class CommentPersistenceTests
{
    [Fact]
    public void CommentsSurviveSqliteVersionsJsonZipAndTrashWhileMarkdownExportsBodyOnly()
    {
        using var directory = new TestDirectory(); string id; string expected; Guid discussion;
        using (var store = new NoteStore(directory.Path))
        {
            var child = NoteNode.Paragraph("原文与批注一起携带"); var session = new DocumentSession(new("doc") { Content = [NoteNode.Toggle("引用", child).WithAttr("collapsed", false)] });
            var row = session.Projection.Find(child.Id)!;
            discussion = session.AddComment("完整保存这条讨论", NoteComments.Capture(session, row.Start, row.Text.Length));
            var thread = NoteComments.For(session.Root).Find(discussion)!; session.ReplyComment(thread, "已补充证据");
            var reply = NoteComments.For(session.Root).Find(discussion)!.Messages[1];
            session.ReplyComment(NoteComments.For(session.Root).Find(discussion)!, "回复这条证据", reply.Id);
            var paragraphDiscussion = session.AddComment("针对整段内容的讨论", NoteComments.CaptureBlock(session, child.Id));
            id = store.Create("带批注的文档", session.Root).Id;
            var before = NoteJson.Serialize(session.Root);
            session.AddComment("文档层面的评论"); store.Save(id, "带批注的文档", session.Root);
            Assert.Equal(before, Assert.Single(store.Revisions(id)).Content);
            expected = NoteJson.Serialize(session.Root);
            store.SetTrashed(id, true); Assert.Equal(expected, store.Get(id).Content);
            store.SetTrashed(id, false); Assert.Equal(expected, store.Get(id).Content);
            using var backup = new MemoryStream(); store.ExportBackup(backup); backup.Position = 0;
            using var restored = new NoteStore(Path.Combine(directory.Path, "restored"));
            var copy = Assert.Single(restored.ImportBackup(backup)); Assert.NotEqual(id, copy);
            Assert.Equal(expected, restored.Get(copy).Content);
            var parsed = NoteJson.ParseStrict(restored.Get(copy).Content);
            Assert.Equal(3, NoteComments.For(parsed).Threads.Length); Assert.Equal(reply.Id, NoteComments.For(parsed).Find(discussion)!.Messages[2].ReplyTo);
            Assert.True(NoteComments.For(parsed).Find(paragraphDiscussion)!.WholeBlock); Assert.Single(NoteComments.For(parsed).Spans(paragraphDiscussion));
            Assert.Single(NoteComments.For(parsed).Spans(discussion));
            var duplicated = store.Create("文档副本", parsed); Assert.Equal(expected, duplicated.Content);
            var markdown = NoteMarkdown.Export(parsed);
            Assert.Contains("原文与批注一起携带", markdown);
            Assert.DoesNotContain("完整保存这条讨论", markdown); Assert.DoesNotContain("\"comment\"", markdown);
            Assert.DoesNotContain(NoteComments.BlockAttribute, markdown);
            Assert.Empty(NoteComments.For(NoteMarkdown.Parse(markdown)).Threads);
            restored.Delete(copy); Assert.Throws<KeyNotFoundException>(() => restored.Get(copy)); Assert.Empty(restored.Revisions(copy));
        }
        using var reopened = new NoteStore(directory.Path);
        var saved = NoteJson.ParseStrict(reopened.Get(id).Content); Assert.Equal(expected, NoteJson.Serialize(saved));
        Assert.Equal(3, NoteComments.For(saved).Find(discussion)!.Messages.Length);
        Assert.Single(NoteComments.For(saved).Spans(discussion));
    }

    [Fact]
    public async Task HttpSyncTransfersAnnotationsAndPreservesCommentOnlyConflicts()
    {
        using var directory = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(directory.Path, "server"));
        using var first = new NoteStore(Path.Combine(directory.Path, "a")); using var second = new NoteStore(Path.Combine(directory.Path, "b"));
        using var a = await host.Client(); using var b = await host.Client();
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph("共同原文不变")] });
        var threadId = session.AddComment("待讨论", NoteComments.Capture(session, 0, 4));
        var paragraphId = session.AddComment("段落讨论", NoteComments.CaptureBlock(session, session.Root.Content[0].Id));
        session.ReplyComment(NoteComments.For(session.Root).Find(paragraphId)!, "第一层回复");
        var parentReply = NoteComments.For(session.Root).Find(paragraphId)!.Messages[^1].Id;
        session.ReplyComment(NoteComments.For(session.Root).Find(paragraphId)!, "回复的回复", parentReply);
        var id = first.Create("只改变评论", session.Root).Id;
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second);
        var peer = new DocumentSession(NoteJson.ParseStrict(second.Get(id).Content));
        Assert.Equal(NoteJson.Serialize(session.Root), NoteJson.Serialize(peer.Root)); Assert.Single(NoteComments.For(peer.Root).Spans(threadId));
        Assert.True(NoteComments.For(peer.Root).Find(paragraphId)!.WholeBlock); Assert.Single(NoteComments.For(peer.Root).Spans(paragraphId));
        session.ReplyComment(NoteComments.For(session.Root).Find(threadId)!, "设备 A 的离线回复"); first.Save(id, "只改变评论", session.Root);
        peer.ReplyComment(NoteComments.For(peer.Root).Find(threadId)!, "设备 B 的离线回复"); second.Save(id, "只改变评论", peer.Root);
        await a.SynchronizeAsync(first); await b.SynchronizeAsync(second); await a.SynchronizeAsync(first);
        var conflict = Assert.Single(first.SyncConflicts()).Entity; Assert.Equal(2, conflict.Versions.Length);
        var previews = conflict.Versions.Select(version => NoteComments.For(NoteJson.ParseStrict(SyncProtocol.Read<SyncDocumentPayload>(version.Payload!).Document.Content)).Summary()).ToArray();
        Assert.Contains(previews, text => text.Contains("设备 A 的离线回复")); Assert.Contains(previews, text => text.Contains("设备 B 的离线回复"));
        Assert.Throws<InvalidOperationException>(() => first.Save(id, "不得静默覆盖", peer.Root));
        var choice = conflict.Versions.Single(version => SyncProtocol.Read<SyncDocumentPayload>(version.Payload!).Document.Content.Contains("设备 B 的离线回复"));
        first.ResolveSyncConflict(conflict, choice.Id); await a.SynchronizeAsync(first); await b.SynchronizeAsync(second);
        Assert.Empty(first.SyncConflicts()); Assert.Equal(first.Get(id), second.Get(id));
        Assert.Contains(first.Revisions(id), revision => revision.Content.Contains("设备 A 的离线回复"));
        var merged = NoteJson.ParseStrict(second.Get(id).Content); Assert.Single(NoteComments.For(merged).Spans(threadId));
        Assert.Contains("设备 B 的离线回复", NoteComments.For(merged).Summary());
        Assert.Single(NoteComments.For(merged).Spans(paragraphId));
        Assert.Equal(parentReply, NoteComments.For(merged).Find(paragraphId)!.Messages[^1].ReplyTo);
    }
}
