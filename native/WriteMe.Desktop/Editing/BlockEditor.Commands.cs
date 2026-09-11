using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

public sealed partial class BlockEditor
{
    private sealed record CommandContext(DocumentSession Session, long Revision, Guid NodeId, int Start, string Prefix, EditorSelection Selection);
    private readonly SlashCommandMenu _commands = new();
    private CommandContext? _commandContext;
    private string? _dismissedQuery;
    private TopLevel? _commandTopLevel;

    private void InitializeCommands()
    {
        _layout.Children.Add(_commands);
        _commands.Invoked += ApplyCommand;
        _commands.Dismissed += DismissCommands;
        _commands.PageChanged += PositionCommands;
        SizeChanged += (_, _) => PositionCommands();
        Surface.TextArea.TextView.ScrollOffsetChanged += (_, _) => PositionCommands();
        Surface.TextArea.TextView.VisualLinesChanged += (_, _) => PositionCommands();
    }

    private void UpdateCommands()
    {
        if (_syncing || _editing) return;
        if (!IsEffectivelyEnabled || InputClient.IsComposing || Surface.SelectionLength != 0 || Surface.Document.Text != Session.Projection.Text)
        { HideCommands(); return; }
        var row = Session.Projection.At(Surface.CaretOffset);
        var length = Math.Clamp(Surface.CaretOffset - row.Start, 0, row.Text.Length);
        var prefix = row.Text[..length];
        if (!prefix.StartsWith('/') || prefix.Length > 48 || row.Node.Type == "codeBlock" || row.IsAtomic)
        { HideCommands(); _dismissedQuery = null; return; }
        if (_dismissedQuery == $"{row.Node.Id}:{prefix}") return;
        var restart = _commandContext == null || !ReferenceEquals(_commandContext.Session, Session) || _commandContext.NodeId != row.Node.Id;
        _commandContext = new(Session, Session.Revision, row.Node.Id, row.Start, prefix, Session.Selection);
        MountOverlay(_commands);
        _commands.ShowQuery(prefix[1..].Trim(), action => Session.CanApplySlash(row.Node.Id, action), restart);
        if (_commandTopLevel == null)
        {
            _commandTopLevel = TopLevel.GetTopLevel(this);
            _commandTopLevel?.AddHandler(PointerPressedEvent, CommandsOutsidePointer, RoutingStrategies.Tunnel);
        }
        PositionCommands();
    }

    private void PositionCommands()
    {
        if (!_commands.IsVisible || Bounds.Width <= 0 || Bounds.Height <= 0 || !Surface.TextArea.TextView.VisualLinesValid) return;
        var host = OverlayOwner;
        _commands.Width = Math.Min(232, host.Bounds.Width);
        _commands.MaxHeight = Math.Max(32, host.Bounds.Height - 8);
        _commands.Margin = new(0);
        _commands.Measure(new(host.Bounds.Width, _commands.MaxHeight));
        var point = _layout.TranslatePoint(CaretPoint(), host._layout) ?? CaretPoint();
        var height = _commands.DesiredSize.Height;
        var below = point.Y + 6;
        var top = below + height <= host.Bounds.Height ? below : point.Y - Surface.FontSize * Surface.Options.LineHeightFactor - height - 6;
        _commands.Margin = new(Math.Clamp(point.X, 0, Math.Max(0, host.Bounds.Width - _commands.Width - 4)),
            Math.Clamp(top, 0, Math.Max(0, host.Bounds.Height - height - 4)), 0, 0);
    }

    private void CommandsOutsidePointer(object? sender, PointerPressedEventArgs args)
    {
        if (args.Source is Visual visual && (visual == _commands || visual.GetVisualAncestors().Contains(_commands))) return;
        DismissCommands();
    }

    private void HideCommands()
    {
        _commands.Reset(); _commandContext = null;
        MountOverlay(_commands, restore: true);
        _commandTopLevel?.RemoveHandler(PointerPressedEvent, CommandsOutsidePointer);
        _commandTopLevel = null;
    }

    private void DismissCommands()
    {
        if (_commandContext is { } context) _dismissedQuery = $"{context.NodeId}:{context.Prefix}";
        HideCommands();
    }

    private void ApplyCommand(SlashAction action)
    {
        var context = _commandContext;
        if (!_commands.IsVisible || context == null || !IsEffectivelyEnabled || InputClient.IsComposing
            || !ReferenceEquals(context.Session, Session) || context.Revision != Session.Revision
            || context.Selection != Session.Selection || Surface.SelectionLength != 0
            || Surface.CaretOffset != context.Start + context.Prefix.Length || Surface.Document.Text != Session.Projection.Text)
        { DismissCommands(); return; }
        DismissCommands();
        Session.ApplySlash(context.NodeId, context.Prefix, action);
        FocusText();
    }
}
