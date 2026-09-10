using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;
using WriteMe.Desktop;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class LayoutWorkspaceTests
{
    private static NoteNode Heading(string text, int level = 2) => NoteNode.Paragraph(text).WithAttr("level", level) with { Type = "heading" };
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Frame(Window window) { Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); using var second = window.CaptureRenderedFrame(); }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window); var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Frame(window);
    }
    private static async Task Wait(Func<bool> done) { for (var i = 0; i < 180 && !done(); i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); } Assert.True(done()); }
    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } path) return;
        Directory.CreateDirectory(path); Frame(window); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(path, name + ".png"));
    }
    private static async Task Close(MainWindow window) { foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close(); await Wait(() => !window.IsVisible); }

    [AvaloniaFact]
    public async Task LayoutWorkspaceRendersSearchesNestedTextSavesAndRestoresTrashedNotes()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var columns = LayoutBlocks.Columns();
        columns = columns with { Content = [columns.Content[0] with { Content = [Heading("灵感记录", 3), NoteNode.Paragraph("留心生活中的小事。\u2028散步时，也许就会有新的答案。") ] }, columns.Content[1] with { Content = [Heading("下一步", 3), NoteNode.Paragraph("把设计草稿整理成可分享的故事。") ] }] };
        var table = LayoutBlocks.Table(4, 3);
        var session = new DocumentSession(new("doc") { Content = [NoteNode.Paragraph("把零散的想法整理在一起，给重要的事情留一点空间。"), Heading("本周，专注三件事"), table, columns] });
        session.PasteTableCells(table.Id, 0, 0, "工作事项\t进展\t下一步\n阅读与摘录\t进行中\t整理本周书单\n产品设计\t初稿完成\t细化每一次交互\n周末计划\t待安排\t去山间走走");
        store.Create("周末的慢生活", new("doc") { Content = [Heading("给自己留白"), NoteNode.Paragraph("一杯咖啡，一本书。把时间留给喜欢的人和事。 #生活")] });
        store.Create("阅读是一场远行", new("doc") { Content = [Heading("正在阅读"), NoteNode.Paragraph("记录那些值得反复回看的句子，让它们与自己的经历相遇。 #阅读")] });
        var document = store.Create("让想法，慢慢成形", session.Root);
        store.SetFavorite(document.Id, true);
        using (var cover = new RenderTargetBitmap(new(700, 210)))
        {
            using (var drawing = cover.CreateDrawingContext())
            {
                drawing.FillRectangle(Brush.Parse("#DCE9E4"), new Rect(0, 0, 700, 210));
                drawing.DrawEllipse(Brush.Parse("#B4CABC"), null, new(520, 260), 320, 240);
                drawing.DrawEllipse(Brush.Parse("#7B9C8B"), null, new(170, 360), 320, 280);
                drawing.DrawEllipse(Brush.Parse("#F5EDDA"), null, new(560, 45), 24, 24);
            }
            using var bytes = new MemoryStream(); cover.Save(bytes); bytes.Position = 0;
            var asset = store.ImportAsset(bytes, "山间.png"); store.SetAppearance(document.Id, new(CoverAssetId: asset.Id));
        }
        var window = new MainWindow(temporary.Path, false) { Width = 1320, Height = 920 }; window.Show(); Frame(window);
        try
        {
            var editor = window.GetVisualDescendants().OfType<BlockEditor>().First();
            Save(window, "workspace-layout-light");
            var tools = window.GetVisualDescendants().OfType<EditorSidebar>().Single();
            window.Width = 1493; tools.Open(EditorPanel.Style); Frame(window); Save(window, "workspace-layout-inspector");
            var paper = window.FindControl<Border>("PageBackground")!;
            Assert.True(paper.TranslatePoint(new(paper.Bounds.Width, 0), window)!.Value.X < tools.TranslatePoint(new(0, 0), window)!.Value.X);
            tools.Close(); window.Width = 1320; Frame(window);
            Click(window, Find<Button>(window, "ShowSpaceSidebar")); Click(window, Find<Button>(window, "LibraryAll"));
            await Wait(() => Find<Grid>(window, "LibraryOverview").IsVisible); Frame(window); Save(window, "workspace-overview");
            var cards = window.GetVisualDescendants().OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button)?.StartsWith("DocumentCard_") == true).ToArray();
            Assert.Equal(3, cards.Length); Assert.All(cards, card => Assert.True(card.Bounds.Width > 230));
            Find<ComboBox>(window, "OverviewSort").SelectedIndex = 2; Frame(window);
            var sortedTitles = window.GetVisualDescendants().OfType<Button>().Where(button => AutomationProperties.GetAutomationId(button)?.StartsWith("DocumentCard_") == true).Select(AutomationProperties.GetName).ToArray();
            Assert.Equal(sortedTitles.Order(StringComparer.CurrentCultureIgnoreCase), sortedTitles);
            Click(window, Find<Button>(window, "OverviewListMode")); Save(window, "workspace-list");
            Assert.True(Find<Button>(window, "DocumentCard_" + document.Id).Bounds.Width > 600);
            Click(window, Find<Button>(window, "DocumentCard_" + document.Id));
            await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == document.Title);
            window.FindControl<TextBox>("SearchBox")!.Text = "设计草稿"; Frame(window);
            var results = window.FindControl<ListBox>("DocumentList")!;
            var item = results.GetVisualDescendants().OfType<ListBoxItem>().First(); Click(window, item); await Wait(() => editor.ActiveEditor.Surface.SelectedText == "设计草稿");
            window.KeyTextInput("写作提纲"); Frame(window);
            editor.ActiveEditor.FocusText();
            window.FindControl<TextBox>("SearchBox")!.Text = ""; Frame(window);
            Click(window, Find<Button>(window, "ShowDocumentSidebar"));
            tools.Open(EditorPanel.Info); Frame(window);
            Click(window, Find<Button>(window, "TrashDocument"));
            await Wait(() => store.Location(document.Id).IsDeleted); Frame(window);
            Assert.True(Find<Button>(window, "UndoTrash").IsEffectivelyVisible);
            Click(window, Find<Button>(window, "UndoTrash")); await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == document.Title);
            Assert.False(store.Location(document.Id).IsDeleted); Assert.Contains("写作提纲", store.Get(document.Id).Content);
            tools.Close();
            store.SetPreferences(new("dark")); Application.Current!.RequestedThemeVariant = ThemeVariant.Dark; Frame(window); Save(window, "workspace-layout-dark");
            window.Width = 820; window.Height = 640; tools.Open(EditorPanel.Style); Frame(window); Save(window, "workspace-layout-narrow");
        }
        finally { await Close(window); Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
        using var reopened = new NoteStore(temporary.Path);
        var saved = NoteJson.Parse(reopened.Get(document.Id).Content);
        Assert.Contains(NoteTree.Descendants(saved), node => node.Type == "table"); Assert.Contains(NoteTree.Descendants(saved), node => node.Type == "columnList");
        Assert.Contains("写作提纲", DocumentText.Plain(saved));
    }
}
