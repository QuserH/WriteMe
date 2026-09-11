using System.Text.Json;
using WriteMe.Core;
using WriteMe.SyncServer;
using Xunit;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;

namespace WriteMe.Tests;

public sealed class SharedValidationTests
{
    private const string Password = "test-only-password-26";
    [Theory]
    [InlineData("message-key")]
    [InlineData("hidden-attrs")]
    [InlineData("text-format")]
    public async Task MalformedStoredFieldsAreRejectedWithoutChangingDurableState(string corruption)
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository;
        var login = r.Login(new("owner", Password))!; var owner = r.SetupProfile(login.AccountId, new("owner", "管理员", Password));
        var space = r.CreateWorkspace(owner.Id, "校验"); var data = r.CreateSharedDocument(owner.Id, space.Id, "保留原文");
        using (var editor = new SharedEditingSession(new(data.State), owner.DisplayName, owner.Id))
        { editor.Session.Edit(0, 0, "原文"); editor.Session.AddComment("评论"); data = r.ApplySharedUpdate(owner.Id, data.Document.Id, editor.Replica.State()); }
        using var snapshot = new SharedDocumentReplica(data.State); var root = snapshot.Read().Root; var block = root.Content[0].Id.ToString("D"); var thread = NoteComments.For(root).Threads[0];
        using var wire = new Doc(new DocOptions { Encoding = DocEncoding.Utf16, Id = 3456789012 });
        var blocks = wire.Map("blocks"); var threads = wire.Map("threads");
        using (var transaction = wire.WriteTransaction())
        {
            transaction.ApplyV1(data.State); var map = blocks.Get(transaction, block)!.Map;
            if (corruption == "message-key")
            {
                var messages = threads.Get(transaction, thread.Id.ToString("D"))!.Map.Get(transaction, "messages")!.Map;
                using var message = Input.String(messages.Get(transaction, thread.Messages[0].Id.ToString("D"))!.String);
                messages.Remove(transaction, thread.Messages[0].Id.ToString("D")); messages.Insert(transaction, Guid.NewGuid().ToString("D"), message);
            }
            else if (corruption == "hidden-attrs")
            {
                using var deleted = Input.String("true"); map.Insert(transaction, "deleted", deleted);
                using var invalid = Input.String("{broken-json"); map.Get(transaction, "attrs")!.Map.Insert(transaction, "color", invalid);
            }
            else
            {
                using var value = Input.String("{}"); using var attrs = Input.Object(new Dictionary<string, Input> { ["m:bold"] = value });
                map.Get(transaction, "text")!.Text.Format(transaction, 0, 1, attrs);
            }
        }
        byte[] invalidState; using (var transaction = wire.ReadTransaction()) invalidState = transaction.StateDiffV1(null);
        Assert.Throws<InvalidDataException>(() => r.ApplySharedUpdate(owner.Id, data.Document.Id, invalidState));
        Assert.Equal(data.State, r.SharedDocument(owner.Id, data.Document.Id).State);
    }
    [Fact]
    public async Task CommentUndoCanRestorePeersOnlyInTheirOriginalDiscussion()
    {
        using var temp = new TestDirectory(); await using var host = await SyncTests.Host.Start(temp.Path); var r = host.Repository;
        var login = r.Login(new("owner", Password))!; var owner = r.SetupProfile(login.AccountId, new("owner", "管理员", Password));
        var member = r.AddAccount(owner.Id, new("member", Password)); r.SetupProfile(member.Id, new("member", "成员", Password));
        var space = r.CreateWorkspace(owner.Id, "讨论"); r.SetMember(owner.Id, space.Id, new("member")); var data = r.CreateSharedDocument(owner.Id, space.Id, "回帖");
        using var a = new SharedEditingSession(new(data.State), "成员", member.Id); a.Session.AddComment("我的讨论"); data = r.ApplySharedUpdate(member.Id, data.Document.Id, a.Replica.State());
        using (var b = new SharedEditingSession(new(data.State), owner.DisplayName, owner.Id))
        { b.Session.ReplyComment(NoteComments.For(b.Session.Root).Threads[0], "另一账号的回复"); data = r.ApplySharedUpdate(owner.Id, data.Document.Id, b.Replica.State()); }
        a.Apply(data.State); var original = NoteComments.For(a.Session.Root).Threads[0]; a.Session.DeleteComment(original);
        data = r.ApplySharedUpdate(member.Id, data.Document.Id, a.Replica.State());
        using (var changed = new SharedDocumentReplica(data.State))
        {
            var root = changed.Read().Root; var catalog = JsonSerializer.SerializeToElement(new { version = 1, threads = new[] { original with { Id = Guid.NewGuid() } } }, SyncProtocol.Json);
            changed.Write(root with { Attrs = root.Attrs.SetItem(NoteComments.Attribute, catalog) });
            Assert.Throws<SharedAccessException>(() => r.ApplySharedUpdate(member.Id, data.Document.Id, changed.State()));
        }
        a.Session.Undo(); data = r.ApplySharedUpdate(member.Id, data.Document.Id, a.Replica.State());
        using var saved = new SharedDocumentReplica(data.State); var restored = NoteComments.For(saved.Read().Root).Threads[0];
        Assert.Equal(original.Id, restored.Id); Assert.Equal(original.Messages.ToArray(), restored.Messages.ToArray());
    }
}
