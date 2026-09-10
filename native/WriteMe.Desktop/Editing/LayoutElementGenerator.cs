using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 原生复合块缓存控件以保留焦点和中文候选，见 .agents/notes/implemented/feature/2026-09-11-native-tables-columns-and-editing.md
internal sealed class LayoutElementGenerator(BlockEditor owner) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var row = owner.Session.Projection.At(startOffset);
        return row.Start >= startOffset && row.Node.Type is "table" or "columnList" ? row.Start : -1;
    }
    public override VisualLineElement? ConstructElement(int offset)
    {
        var row = owner.Session.Projection.At(offset);
        if (row.Start != offset || row.Node.Type is not ("table" or "columnList")) return null;
        var width = Math.Max(100, owner.Surface.TextArea.TextView.Bounds.Width - owner.TextStart(row) - 16);
        var view = owner.LayoutView(row.Node, width);
        TextBlock.SetBaselineOffset(view, 19);
        return new InlineObjectElement(1, view);
    }
}

internal abstract class NativeLayoutView : Border, IDisposable
{
    protected readonly BlockEditor Owner;
    protected readonly DocumentSession Session;
    protected readonly Guid BlockId;
    protected NoteNode? Node;
    protected bool Disposed;
    private readonly List<Button> _actions = [];
    private (FontFamily Font, double Size, double LineHeight, Color? Ink, Color? Background, bool Dark, string Divider, double Ghost)? _appearance;
    protected NativeLayoutView(BlockEditor owner, Guid id)
    {
        Owner = owner; Session = owner.Session; BlockId = id;
        Margin = new(0, 5, 0, 14);
        HorizontalAlignment = HorizontalAlignment.Left;
        AutomationProperties.SetAutomationId(this, "Layout_" + id);
    }
    public abstract void Update(NoteNode node, double width);
    public abstract bool FocusPoint(TextPoint point);
    public abstract void UpdateCatalogue(IReadOnlyList<DocumentInfo> documents, IReadOnlyList<TagInfo> tags);
    public abstract void Dispose();
    protected bool RefreshOwnerAppearance()
    {
        var appearance = (Owner.Surface.FontFamily, Owner.Surface.FontSize, Owner.Surface.Options.LineHeightFactor,
            (Owner.Surface.Foreground as ISolidColorBrush)?.Color, Owner.PageBackgroundColor, Owner.IsDarkPage, Owner.DividerStyle, Owner.GhostOpacity);
        if (_appearance is { } previous && previous.Equals(appearance)) return false;
        _appearance = appearance;
        TextElement.SetForeground(this, Owner.PageMuted);
        foreach (var action in _actions) action.Foreground = Owner.PageMuted;
        return true;
    }
    protected bool CanEdit => !Disposed && Owner.IsEffectivelyEnabled && !Owner.IsAnyComposing && ReferenceEquals(Session, Owner.Session) && NoteTree.Find(Session.Root, BlockId) != null;
    protected Button Action(string label, SidebarSymbol symbol, string id, Action action)
    {
        var button = new Button { Content = new SidebarGlyph(symbol, 15), Width = 28, Height = 27, Padding = new(0), Focusable = false };
        button.Classes.Add("quiet");
        ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label); AutomationProperties.SetAutomationId(button, id + "_" + BlockId);
        button.Click += (_, _) => { if (CanEdit) action(); };
        _actions.Add(button);
        return button;
    }
    protected void ShowUnsupported(string text)
    {
        Child = new Border { Padding = new(16), Background = Owner.PageColor("#F5F6F8", "#303740"), CornerRadius = new(8), Child = new TextBlock
        { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Owner.PageMuted } };
    }
}
