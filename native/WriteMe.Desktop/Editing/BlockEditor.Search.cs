using Avalonia.Media;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed partial class BlockEditor
{
    private DocumentSearchResult? _searchHighlights;
    private DocumentSearchMatch? _selectedSearchMatch;
    private Dictionary<Guid, DocumentSearchMatch[]> _searchByBlock = [];

    internal void SetSearchHighlights(DocumentSearchResult? result, DocumentSearchMatch? selected = null)
    {
        var owner = OverlayOwner;
        if (result != null && !ReferenceEquals(result.Root, owner.Session.Root)) result = null;
        if (ReferenceEquals(owner._searchHighlights, result) && owner._selectedSearchMatch == selected) return;
        if (!ReferenceEquals(owner._searchHighlights, result))
            owner._searchByBlock = result?.Matches.GroupBy(match => match.NodeId).ToDictionary(group => group.Key, group => group.ToArray()) ?? [];
        owner._searchHighlights = result; owner._selectedSearchMatch = selected;
        owner.Surface.TextArea.TextView.Redraw();
        foreach (var child in owner.GetVisualDescendants().OfType<BlockEditor>()) child.Surface.TextArea.TextView.Redraw();
        foreach (var table in owner.GetVisualDescendants().OfType<NativeTableView>()) table.RefreshDecorations();
    }

    internal IEnumerable<DocumentSearchMatch> SearchMatches(Guid nodeId)
    {
        var owner = OverlayOwner;
        return ReferenceEquals(owner._searchHighlights?.Root, Session.HistoryOwner.Root)
            ? owner._searchByBlock.GetValueOrDefault(nodeId) ?? [] : [];
    }

    internal IBrush SearchColor(DocumentSearchMatch match) => PageColor(OverlayOwner._selectedSearchMatch == match ? "#FFD888" : "#FFF0C2", "#635031");

    internal IEnumerable<(string Text, IBrush? Background)> SearchSegments(Guid nodeId, string text, int offset)
    {
        var position = 0;
        foreach (var match in SearchMatches(nodeId))
        {
            var start = Math.Max(0, match.Start - offset); var end = Math.Min(text.Length, match.Start + match.Length - offset);
            if (start >= end) continue;
            if (start > position) yield return (text[position..start], null);
            yield return (text[start..end], SearchColor(match)); position = end;
        }
        if (position < text.Length) yield return (text[position..], null);
    }
}
