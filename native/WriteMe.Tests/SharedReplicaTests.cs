using WriteMe.Core;
using Xunit;

namespace WriteMe.Tests;

public sealed class SharedReplicaTests
{
    private static byte[] Seed(string text = "你好🙂")
    {
        using var replica = new SharedDocumentReplica(); replica.Write(new("doc") { Content = [NoteNode.Paragraph(text)] }, "共享笔记", false); return replica.State();
    }
    private static string Text(SharedDocumentReplica replica) => DocumentText.Plain(replica.Read().Root);
    [Fact]
    public void ConcurrentUnicodeFormattingUndoAndRestartPreservePeerInput()
    {
        var state = Seed(); using var a = new SharedEditingSession(new(state), "甲", "account-a"); using var b = new SharedEditingSession(new(state), "乙", "account-b");
        var old = a.Replica.StateVector(); a.Session.Edit(2, 0, "桌面", false); a.Session.Format(2, 2, new("bold"));
        b.Session.Edit(2, 0, "网页", false); b.Session.Format(2, 2, new("italic"));
        var aUpdate = a.Replica.Difference(old); var bUpdate = b.Replica.Difference(old); a.Apply(bUpdate); b.Apply(aUpdate);
        Assert.Equal(Text(a.Replica), Text(b.Replica)); Assert.Contains("桌面", Text(a.Replica)); Assert.Contains("网页", Text(a.Replica)); Assert.Contains("🙂", Text(a.Replica));
        a.Session.Undo(); a.Session.Undo(); b.Apply(a.Replica.Difference(b.Replica.StateVector()));
        Assert.DoesNotContain("桌面", Text(a.Replica)); Assert.Contains("网页", Text(a.Replica)); Assert.Equal(Text(a.Replica), Text(b.Replica));
        using var reopened = new SharedDocumentReplica(a.Replica.State()); Assert.Equal(Text(a.Replica), Text(reopened));
        a.Apply(bUpdate); Assert.Equal(Text(a.Replica), Text(reopened));
    }
    [Fact]
    public void ConcurrentRepliesKeepIdentityAndReplyToReply()
    {
        using var seed = new SharedEditingSession(new(Seed()), "甲", "a"); var threadId = seed.Session.AddComment("初始评论");
        using var a = new SharedEditingSession(new(seed.Replica.State()), "甲", "a"); using var b = new SharedEditingSession(new(seed.Replica.State()), "乙", "b");
        a.Session.ReplyComment(NoteComments.For(a.Session.Root).Find(threadId)!, "第一条回复"); b.Session.ReplyComment(NoteComments.For(b.Session.Root).Find(threadId)!, "第二条回复");
        a.Apply(b.Replica.State()); b.Apply(a.Replica.State());
        var thread = NoteComments.For(b.Session.Root).Find(threadId)!; Assert.Equal(3, thread.Messages.Length);
        var aReply = thread.Messages.Single(message => message.Text == "第一条回复"); b.Session.ReplyComment(thread, "回复你的回复", aReply.Id); a.Apply(b.Replica.State());
        Assert.Equal(aReply.Id, NoteComments.For(a.Session.Root).Find(threadId)!.Messages[^1].ReplyTo);
        Assert.Throws<InvalidOperationException>(() => b.Session.EditComment(NoteComments.For(b.Session.Root).Find(threadId)!, aReply.Id, "冒充"));
        Assert.Equal("b", NoteComments.For(a.Session.Root).Find(threadId)!.Messages[^1].AuthorId);
    }
    [Fact]
    public void ConcurrentMoveAndTypingKeepTheSameBlockText()
    {
        var paragraph = NoteNode.Paragraph("内部"); var toggle = NoteNode.Toggle("父级", paragraph);
        using var seed = new SharedDocumentReplica(); seed.Write(new("doc") { Content = [toggle, NoteNode.Paragraph("末尾")] }, "结构", false);
        using var a = new SharedDocumentReplica(seed.State()); using var b = new SharedDocumentReplica(seed.State());
        var aRoot = a.Read().Root; aRoot = NoteTree.Replace(aRoot, paragraph.Id); aRoot = aRoot with { Content = aRoot.Content.Add(paragraph) }; a.Write(aRoot);
        b.Write(NoteTree.Update(b.Read().Root, paragraph.Id, node => RichText.Splice(node, 2, 0, "新增", [])));
        a.Apply(b.State()); b.Apply(a.State());
        Assert.Equal(a.Read().Root.Id, b.Read().Root.Id); Assert.Equal("内部新增", RichText.Plain(a.Read().Root.Content[^1]));
        Assert.Equal("doc", NoteTree.Parent(a.Read().Root, paragraph.Id)!.Type); Assert.Equal(Text(a), Text(b));
    }
    [Fact]
    public void InvalidLocalWriteIsAtomicAndEmptyFallbackIdentityIsStable()
    {
        using var replica = new SharedDocumentReplica(Seed()); var before = replica.State(); var node = NoteNode.Paragraph("重复");
        Assert.Throws<InvalidDataException>(() => replica.Write(new("doc") { Content = [node, node] })); Assert.Equal(before, replica.State());
        Assert.Throws<InvalidDataException>(() => replica.Write(replica.Read().Root, new string('a', 501))); Assert.Equal(before, replica.State());
        replica.Write(new("doc") { Content = [] }); using var peer = new SharedDocumentReplica(replica.State());
        Assert.Equal(replica.Read().Root.Content[0].Id, peer.Read().Root.Content[0].Id);
    }
    [Fact]
    public void RelativeCaretTracksRemoteInsertionAndSharedScopesUseRootHistory()
    {
        using var a = new SharedEditingSession(new(Seed("正文")), "甲", "a"); using var b = new SharedEditingSession(new(a.Replica.State()), "乙", "b");
        a.Session.Selection = EditorSelection.At(a.Session.Projection.Rows[0].Node.Id, 2); b.Session.Edit(0, 0, "共同", false); a.Apply(b.Replica.State());
        Assert.Equal(4, a.Session.Selection.Caret.Offset); Assert.False(a.Session.CanUndo);
        a.Session.Edit(4, 0, "输入", false); a.Session.Undo(); Assert.Equal("共同正文", Text(a.Replica));
    }
    [Fact]
    public void MiddleCaretTracksUnicodeInsertionDeletionAndLocalUndo()
    {
        using var a = new SharedEditingSession(new(Seed("甲乙🙂丙丁")), "甲", "a"); using var b = new SharedEditingSession(new(a.Replica.State()), "乙", "b");
        var id = a.Session.Projection.Rows[0].Node.Id; a.Session.Selection = EditorSelection.At(id, 4);
        b.Session.Edit(1, 0, "远端", false); a.Apply(b.Replica.State());
        Assert.Equal(6, a.Session.Selection.Caret.Offset); Assert.Equal("甲远端乙🙂丙丁", Text(a.Replica));
        a.Session.Edit(6, 0, "本机", false); b.Apply(a.Replica.State());
        b.Session.Edit(0, 1, "", false); a.Apply(b.Replica.State());
        Assert.Equal(7, a.Session.Selection.Caret.Offset); a.Session.Undo();
        Assert.Equal("远端乙🙂丙丁", Text(a.Replica));
    }
    [Fact]
    public void ViewerFoldingAndCommentNavigationAreLocalAndCannotChangeSharedContent()
    {
        var paragraph = NoteNode.Paragraph("折叠中的内容"); var toggle = NoteNode.Toggle("标题", paragraph).WithAttr("collapsed", true);
        using var replica = new SharedDocumentReplica(); replica.Write(new("doc") { Content = [toggle] }, "阅读", false);
        using var viewer = new SharedEditingSession(new(replica.State()), "读者", "viewer"); var state = viewer.Replica.State(); var changes = 0;
        viewer.LocalUpdate += _ => changes++; viewer.Session.IsReadOnly = true;
        Assert.Single(viewer.Session.Projection.Rows); viewer.Session.Toggle(toggle.Id);
        Assert.Equal(2, viewer.Session.Projection.Rows.Length); Assert.True(NoteTree.Find(viewer.Session.Root, toggle.Id)!.Bool("collapsed"));
        viewer.Session.Edit(0, 0, "禁止修改"); viewer.SetTitle("禁止重命名"); Assert.Equal("阅读", viewer.Title);
        viewer.Session.SetAllCollapsed(true); Assert.Single(viewer.Session.Projection.Rows); Assert.True(viewer.Session.Reveal(paragraph.Id));
        Assert.Equal(paragraph.Id, viewer.Session.Selection.Caret.NodeId); Assert.Equal(2, viewer.Session.Projection.Rows.Length);
        Assert.Equal(0, changes); Assert.Equal(state, viewer.Replica.State()); Assert.False(viewer.Session.CanUndo);
        viewer.Session.IsReadOnly = false; Assert.Single(viewer.Session.Projection.Rows);
    }
    [Theory]
    [InlineData("http://192.168.1.109:18789", "http://192.168.1.109:18789/")]
    [InlineData("http://10.0.0.2", "http://10.0.0.2/")]
    [InlineData("http://172.16.0.2", "http://172.16.0.2/")]
    [InlineData("http://[fd00::2]:8787", "http://[fd00::2]:8787/")]
    [InlineData("https://notes.example.com", "https://notes.example.com/")]
    public void SharedEndpointAllowsTheSelectedLanDeployment(string input, string expected) => Assert.Equal(expected, SharedProtocol.Endpoint(input).AbsoluteUri);
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("http://172.32.0.1")]
    [InlineData("http://192.168.1.109/?password=x")]
    [InlineData("https://user:secret@notes.example.com")]
    public void SharedEndpointRejectsPublicHttpAndCredentialUrls(string input) => Assert.Throws<ArgumentException>(() => SharedProtocol.Endpoint(input));
}
