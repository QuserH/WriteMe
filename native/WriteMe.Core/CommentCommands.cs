using System.Collections.Immutable;

namespace WriteMe.Core;

public sealed partial class DocumentSession
{
    private static string CommentText(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("请先输入评论内容。");
        if (text.Length > NoteComments.MaxMessageLength) throw new InvalidOperationException($"每条评论最多 {NoteComments.MaxMessageLength:N0} 字。");
        return text;
    }

    private NoteComments EditableComments()
    {
        if (!IsScopeAttached) throw new InvalidOperationException("原编辑区域已被删除。");
        var index = NoteComments.For(HistoryOwner.Root);
        if (!index.CanEdit) throw new InvalidOperationException(index.Error);
        return index;
    }

    public Guid AddComment(string text, CommentAnchor? anchor = null)
    {
        var index = EditableComments();
        text = CommentText(text);
        if (index.Threads.Length >= NoteComments.MaxThreads || index.Threads.Sum(thread => thread.Messages.Length) >= NoteComments.MaxMessages)
            throw new InvalidOperationException("本篇评论已达到数量上限。");
        var owner = HistoryOwner;
        var root = owner.Root;
        var ranges = anchor == null ? [] : NoteComments.Resolve(root, anchor);
        if (anchor != null && ranges.IsEmpty) throw new InvalidOperationException(anchor.WholeBlock ? "评论的段落已删除，请重新选择段落或改为文档评论；草稿已保留。" : "引用的文字已变化，请重新选取或改为文档评论；草稿已保留。");
        var id = Guid.NewGuid();
        foreach (var range in ranges) root = NoteTree.Update(root, range.NodeId, node => anchor!.WholeBlock
            ? NoteComments.WithBlockIds(node, NoteComments.BlockIds(node).Add(id)) : NoteComments.MarkRange(node, range.Start, range.Length, id));
        var quote = anchor?.WholeBlock == true ? NoteComments.BlockQuote(NoteTree.Find(root, ranges[0].NodeId)!) : anchor?.Quote ?? "";
        var thread = new CommentThread(id, quote, anchor != null, [new(Guid.NewGuid(), "我", text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())], anchor?.WholeBlock == true);
        owner.BreakTypingGroup();
        owner.Commit(NoteComments.Write(root, index.Threads.Add(thread)));
        return id;
    }

    private void ChangeComment(CommentThread expected, Func<CommentThread, CommentThread?> change)
    {
        var index = EditableComments();
        if (index.Find(expected.Id) is not { } current || !NoteComments.Equivalent(current, expected))
            throw new InvalidOperationException("这条讨论已变化，请查看最新内容后重试；草稿已保留。");
        var updated = change(current);
        if (updated != null && NoteComments.Equivalent(current, updated)) return;
        var threads = updated == null ? index.Threads.Remove(current) : index.Threads.Replace(current, updated);
        var owner = HistoryOwner;
        var root = updated == null ? NoteComments.RemoveMarks(owner.Root, current.Id) : owner.Root;
        owner.BreakTypingGroup();
        owner.Commit(NoteComments.Write(root, threads));
    }

    public void ReplyComment(CommentThread expected, string text, Guid? replyTo = null)
    {
        text = CommentText(text);
        ChangeComment(expected, thread =>
        {
            var parent = thread.Messages.FirstOrDefault(message => message.Id == (replyTo ?? thread.Messages[0].Id));
            if (parent == null || parent.Deleted) throw new InvalidOperationException("要回复的消息已删除，草稿已保留。");
            if (NoteComments.For(HistoryOwner.Root).Threads.Sum(item => item.Messages.Length) >= NoteComments.MaxMessages)
                throw new InvalidOperationException("本篇评论已达到数量上限。");
            return thread with { Messages = thread.Messages.Add(new(Guid.NewGuid(), "我", text, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ReplyTo: parent.Id)) };
        });
    }

    public void EditComment(CommentThread expected, Guid messageId, string text)
    {
        text = CommentText(text);
        ChangeComment(expected, thread =>
        {
            var message = thread.Messages.FirstOrDefault(message => message.Id == messageId) ?? throw new InvalidOperationException("这条评论已删除。");
            if (message.Deleted) throw new InvalidOperationException("这条回复已删除，不能修改。");
            return message.Text == text ? thread : thread with { Messages = thread.Messages.Replace(message, message with { Text = text, EditedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) };
        });
    }

    public void DeleteCommentReply(CommentThread expected, Guid messageId) => ChangeComment(expected, thread =>
    {
        var message = thread.Messages.FirstOrDefault(message => message.Id == messageId) ?? throw new InvalidOperationException("这条回复已删除。");
        if (thread.Messages[0].Id == messageId) throw new InvalidOperationException("首条评论属于整条讨论，请使用删除讨论。");
        return message.Deleted ? thread : thread with { Messages = thread.Messages.Replace(message, message with { Text = "", Deleted = true, EditedAt = null }) };
    });

    public void DeleteComment(CommentThread expected) => ChangeComment(expected, _ => null);
}
