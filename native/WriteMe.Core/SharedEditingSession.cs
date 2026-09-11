namespace WriteMe.Core;

internal interface IDocumentHistory
{
    bool CanUndo { get; }
    bool CanRedo { get; }
    void Record(NoteNode root, bool separate);
    void BreakGroup();
    void Undo();
    void Redo();
}

// Note: 协同撤销只移除本地事务，远端输入通过相对位置保留光标 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
public sealed class SharedEditingSession : IDocumentHistory, IDisposable
{
    public SharedDocumentReplica Replica { get; }
    public DocumentSession Session { get; }
    public string Title { get; private set; }
    public event Action<byte[]>? LocalUpdate;
    public event EventHandler? Refreshed;

    public SharedEditingSession(SharedDocumentReplica replica, string author, string accountId, bool moderate = false)
    {
        Replica = replica; var snapshot = replica.Read(); Title = snapshot.Title;
        Session = new(snapshot.Root) { CommentAuthor = author, CommentAccountId = accountId, CanModerateComments = moderate };
        Session.AttachHistory(this);
    }

    public bool CanUndo => !Session.IsReadOnly && Replica.CanUndo;
    public bool CanRedo => !Session.IsReadOnly && Replica.CanRedo;
    void IDocumentHistory.Record(NoteNode root, bool separate)
    {
        if (separate) Replica.BreakHistory();
        var update = Replica.Write(root); if (update.Length > 0) LocalUpdate?.Invoke(update);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void SetTitle(string title)
    {
        if (Session.IsReadOnly || title == Title) return;
        var update = Replica.Write(Session.Root, title); Title = title;
        if (update.Length > 0) LocalUpdate?.Invoke(update);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void BreakGroup() => Replica.BreakHistory();
    public void Apply(byte[] update) => Change(() => { Replica.Apply(update); return []; });
    public void Undo() { if (!Session.IsReadOnly) Change(Replica.Undo); }
    public void Redo() { if (!Session.IsReadOnly) Change(Replica.Redo); }
    private void Change(Func<byte[]> action)
    {
        var selection = Session.Selection;
        var anchor = Replica.CapturePosition(selection.Anchor); var caret = Replica.CapturePosition(selection.Caret);
        var update = action(); var snapshot = Replica.Read(); Title = snapshot.Title;
        Session.AcceptSharedRoot(snapshot.Root, new(Replica.RestorePosition(selection.Anchor, anchor), Replica.RestorePosition(selection.Caret, caret)));
        if (update.Length > 0) LocalUpdate?.Invoke(update);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }
    public void Dispose() { Session.AttachHistory(null); Replica.Dispose(); }
}

public sealed partial class DocumentSession
{
    private IDocumentHistory? _sharedHistory;
    public string CommentAuthor { get; set; } = "我";
    public string? CommentAccountId { get; set; }
    public bool CanModerateComments { get; set; }
    public bool CanEditComment(CommentMessage message) => !IsReadOnly && (HistoryOwner.CommentAccountId == null || message.AuthorId == HistoryOwner.CommentAccountId);
    public bool CanDeleteComment(CommentMessage message) => !IsReadOnly && (CanEditComment(message) || HistoryOwner.CanModerateComments);
    internal void AttachHistory(IDocumentHistory? history)
    { _sharedHistory = history; _undo.Clear(); _redo.Clear(); _lastGroup = null; }
    internal void AcceptSharedRoot(NoteNode root, EditorSelection selection)
    {
        Root = NoteTree.Normalize(root); Projection = Project(); Selection = ResolveSelection(selection);
        Revision++; BreakTypingGroup(); Changed?.Invoke(this, EventArgs.Empty);
    }
}
