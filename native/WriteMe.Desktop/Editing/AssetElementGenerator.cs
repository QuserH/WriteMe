using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace WriteMe.Desktop.Editing;

// Note: 附件引用本机内容寻址副本，原生预览不加载网页 — 见 .agents/notes/implemented/feature/2026-09-10-page-assets-and-portability.md
internal sealed class AssetElementGenerator(BlockEditor owner) : VisualLineElementGenerator
{
    public override int GetFirstInterestedOffset(int startOffset)
    {
        var row = owner.Session.Projection.At(startOffset);
        return row.Start >= startOffset && row.Node.Type is "image" or "attachment" ? row.Start : -1;
    }
    public override VisualLineElement? ConstructElement(int offset)
    {
        var row = owner.Session.Projection.At(offset);
        if (row.Start != offset || row.Node.Type is not ("image" or "attachment")) return null;
        var node = row.Node; var id = node.String("assetId");
        var panel = new StackPanel { Spacing = 8 };
        var width = Math.Max(120, Math.Min(560, owner.Surface.TextArea.TextView.Bounds.Width - BlockLayout.TextStart(row) - 20));
        if (node.Type == "image" && id != null && owner.AssetImage(id) is { } bitmap)
            panel.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = width - 24, MaxHeight = 270, HorizontalAlignment = HorizontalAlignment.Left });
        else panel.Children.Add(new SidebarGlyph(node.Type == "image" ? SidebarSymbol.Image : SidebarSymbol.Attachment, 23));
        panel.Children.Add(new TextBlock { Text = node.String("name") ?? node.String("alt") ?? "附件", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        if (id == null || owner.ResolveAsset?.Invoke(id) == null)
            panel.Children.Add(new TextBlock { Text = id == null ? "外部图片 · 点击查看来源" : "附件尚未下载", FontSize = 10, Foreground = Ui.Muted });
        var button = new Button
        {
            Content = panel, Width = width, Background = Ui.Chrome("#F5F6F8"), BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(10),
            Padding = new(12), HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false, Margin = new(0, 5, 0, 8)
        };
        TextBlock.SetBaselineOffset(button, 17);
        AutomationProperties.SetName(button, (node.Type == "image" ? "图片：" : "附件：") + (node.String("name") ?? "附件"));
        AutomationProperties.SetAutomationId(button, "Asset_" + node.Id);
        button.Click += (_, _) => owner.OpenAsset(node);
        return new InlineObjectElement(1, button);
    }
}
