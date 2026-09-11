namespace WriteMe.Core;

public enum SlashOperation { Block, Table, Columns, Alignment, Indent, Outdent, Format, TextColor }

public sealed record SlashAction(SlashOperation Operation, string Value = "", int Level = 1);

public sealed partial class DocumentSession
{
    // Note: 分类导航不改正文，确认叶项才原子移除查询并执行 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
    public bool CanApplySlash(Guid nodeId, SlashAction action)
    {
        if (!IsScopeAttached || Projection.Find(nodeId) is not { IsAtomic: false } row || row.Node.Type == "codeBlock") return false;
        if (action.Operation is SlashOperation.Indent or SlashOperation.Outdent)
        {
            var parent = NoteTree.Parent(Root, row.Block.Id);
            if (parent == null) return false;
            if (action.Operation == SlashOperation.Outdent)
                return parent.Type == "toggleBlock" && CanMove(row.Block.Id, parent.Id, DropPlacement.After);
            var index = parent.Content.FindIndex(node => node.Id == row.Block.Id);
            return index > 0 && CanMove(row.Block.Id, parent.Content[index - 1].Id, DropPlacement.Inside, append: true);
        }
        return action.Operation switch
        {
            SlashOperation.Block => action.Value is "paragraph" or "heading" or "toggleBlock" or "bulletList" or "orderedList" or "taskList" or "blockquote" or "codeBlock" or "horizontalRule",
            SlashOperation.Table => action.Level is >= 2 and <= 9,
            SlashOperation.Columns => action.Level is 2 or 3,
            SlashOperation.Alignment => action.Value is "left" or "center" or "right" or "justify",
            SlashOperation.Format => action.Value is "bold" or "italic" or "underline" or "strike" or "code" or "clear",
            SlashOperation.TextColor => action.Value.Length == 0 || TextColor.Normalize(action.Value) != null,
            _ => false
        };
    }

    public bool ApplySlash(Guid nodeId, string prefix, SlashAction action)
    {
        if (!CanApplySlash(nodeId, action) || prefix.Length == 0 || prefix[0] != '/' || Projection.Find(nodeId) is not { } row
            || !row.Text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var offset = row.Start + prefix.Length;
        if (action.Operation == SlashOperation.Block) ConvertBlock(offset, action.Value, action.Level, prefix.Length);
        else if (action.Operation is SlashOperation.Table or SlashOperation.Columns)
            InsertLayoutCommand(offset, prefix.Length, action.Operation == SlashOperation.Table ? "table" : "columnList", action.Level, action.Level);
        else
        {
            // Work against a detached draft, then publish one before/after pair to the root history.
            // The draft never notifies editors or forwards an intermediate selection to a cell owner.
            using var draft = new DocumentSession(Root) { Selection = Selection };
            draft.Edit(row.Start, prefix.Length, "", false);
            var remaining = draft.Projection.Find(nodeId)!;
            switch (action.Operation)
            {
                case SlashOperation.Alignment: draft.SetAlignment(remaining.Start, 0, action.Value); break;
                case SlashOperation.Indent: draft.Indent(remaining.Block.Id); break;
                case SlashOperation.Outdent: draft.Outdent(remaining.Block.Id); break;
                case SlashOperation.Format: draft.Format(remaining.Start, remaining.Text.Length, action.Value == "clear" ? null : new(action.Value)); break;
                case SlashOperation.TextColor: draft.SetTextColor(remaining.Start, remaining.Text.Length, action.Value.Length == 0 ? null : action.Value); break;
            }
            Commit(draft.Root, draft.Selection);
            TypingMarks = draft.TypingMarks;
        }
        return true;
    }
}
