using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
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
using Xunit.Abstractions;

namespace WriteMe.Tests;

public sealed class WorkspaceInteractionTests(ITestOutputHelper output)
{
    private static NoteNode Doc(params NoteNode[] nodes) => new("doc") { Content = [.. nodes] };
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>().Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs(); using var second = window.CaptureRenderedFrame();
    }
    private static void Key(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, ""); if (window.IsVisible) { window.KeyRelease(key, modifiers, PhysicalKey.None, ""); Frame(window); }
    }
    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); Frame(window);
        var point = control.TranslatePoint(new(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); Frame(window); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); if (window.IsVisible) Frame(window);
    }
    private static void SaveImage(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); Frame(window); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(directory, name));
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 160 && !condition(); i++) { await Task.Delay(15); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private static async Task Close(MainWindow window)
    {
        foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();
        if (window.IsVisible) { window.Close(); await Wait(() => !window.IsVisible); }
    }
    private static (Window Window, BlockEditor Editor) OpenEditor(params NoteNode[] nodes)
    {
        var editor = new BlockEditor(new(Doc(nodes))); var window = new Window { Content = editor, Width = 780, Height = 560 };
        window.Show(); editor.FocusText(); Frame(window); return (window, editor);
    }

    [AvaloniaFact]
    public async Task CardOverviewFiltersOpensAndKeepsTheExistingEditorSession()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        var folder = store.CreateFolder("personal", "工作与创作");
        var first = store.Create("把灵感整理成文章", Doc(NoteNode.Paragraph("一个清晰的开头，一条值得追问的线索。 #写作")), folderId: folder.Id);
        var second = store.Create("本周阅读计划", Doc(NoteNode.Paragraph("保持好奇，慢慢读完一本好书。 #阅读")));
        store.Create("日常小记", Doc(NoteNode.Paragraph("下过雨后的街道，和一杯热咖啡。"))); store.SetFavorite(first.Id, true);
        var window = new MainWindow(temporary.Path, false); window.Show(); Frame(window);
        try
        {
            Click(window, Find<Button>(window, "ShowSpaceSidebar")); Click(window, Find<Button>(window, "LibraryAll"));
            await Wait(() => Find<Grid>(window, "LibraryOverview").IsVisible); Frame(window);
            Assert.Equal(3, window.GetVisualDescendants().OfType<Button>().Count(button => AutomationProperties.GetAutomationId(button)?.StartsWith("DocumentCard_") == true));
            SaveImage(window, "library-overview.png"); Click(window, Find<Button>(window, "DocumentCard_" + second.Id));
            await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == second.Title); Assert.False(Find<Grid>(window, "LibraryOverview").IsVisible);
            var session = window.GetVisualDescendants().OfType<BlockEditor>().Single().Session;
            Click(window, Find<Button>(window, "LibraryFavorites")); await Wait(() => Find<Grid>(window, "LibraryOverview").IsVisible);
            Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button)?.StartsWith("DocumentCard_") == true);
            Click(window, Find<Button>(window, "ShowDocumentSidebar")); Assert.Same(session, window.GetVisualDescendants().OfType<BlockEditor>().Single().Session);
            Assert.False(Find<Grid>(window, "LibraryOverview").IsVisible);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task DesktopLoginPreservesMetadataHistoryAndResolvesARealServerConflict()
    {
        using var temporary = new TestDirectory(); await using var host = await SyncTests.Host.Start(Path.Combine(temporary.Path, "server"));
        using var probe = new NoteStore(Path.Combine(temporary.Path, "desktop")); using var peer = new NoteStore(Path.Combine(temporary.Path, "peer"));
        var document = probe.Create("同步练习", Doc(NoteNode.Paragraph("原始内容")));
        var window = new MainWindow(Path.Combine(temporary.Path, "desktop"), false); window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
        var stage = "登录";
        try
        {
            editor.FocusText(); window.KeyTextInput("本机输入，"); Frame(window); var session = editor.Session;
            Click(window, Find<Button>(window, "SyncButton")); await Wait(() => window.OwnedWindows.Count == 1);
            var settings = window.OwnedWindows.Single(); Frame(settings);
            Find<TextBox>(settings, "SyncEndpoint").Text = host.Endpoint.AbsoluteUri; Find<TextBox>(settings, "SyncUsername").Text = "owner"; Find<TextBox>(settings, "SyncPassword").Text = "test-only-password-26";
            Click(settings, Find<Button>(settings, "SyncLogin"));
            await Wait(() => Find<TextBlock>(settings, "SyncStatus").Text?.StartsWith("已同步") == true);
            Assert.Equal("", Find<TextBox>(settings, "SyncPassword").Text); Assert.NotNull(SyncCredentials.Load(probe)); SaveImage(settings, "sync-settings.png");
            settings.Close(); Frame(window);
            stage = "元信息同步";
            using var remote = await host.Client(); await remote.SynchronizeAsync(peer);
            peer.SetFavorite(document.Id, true); await remote.SynchronizeAsync(peer); Click(window, Find<Button>(window, "SyncButton"));
            await Wait(() => probe.Get(document.Id).IsFavorite && editor.IsEnabled && Find<Button>(window, "SyncButton").Content?.ToString() == "同步");
            Assert.Same(session, editor.Session); Assert.True(editor.Session.CanUndo);
            stage = "并发编辑";
            peer.Save(document.Id, "另一台设备的版本", Doc(NoteNode.Paragraph("远端离线写下的想法"))); await remote.SynchronizeAsync(peer);
            editor.FocusText(); editor.Surface.CaretOffset = editor.Surface.Document.TextLength; window.KeyTextInput("，本机同时继续编辑。"); Frame(window);
            Click(window, Find<Button>(window, "SyncButton")); await Wait(() => probe.SyncConflicts().Count == 1 && !editor.IsEnabled);
            stage = "选择冲突版本";
            Click(window, Find<Button>(window, "SyncButton")); await Wait(() => window.OwnedWindows.Count == 1);
            var conflict = window.OwnedWindows.Single(); Frame(conflict); SaveImage(conflict, "sync-conflict.png");
            var selected = probe.SyncConflicts()[0].Entity.Versions.Single(version => SyncProtocol.Read<SyncDocumentPayload>(version.Payload!).Document.Title == "另一台设备的版本");
            Click(conflict, Find<Button>(conflict, "ResolveVersion_" + selected.Id));
            await Wait(() => probe.SyncConflicts().Count == 0 && editor.IsEnabled && probe.PendingSyncCount == 0);
            Assert.Equal("远端离线写下的想法", editor.Session.Projection.Text); Assert.False(editor.Session.CanUndo);
            Assert.Contains(probe.Revisions(document.Id), revision => revision.Content.Contains("本机同时继续编辑"));
        }
        catch (Exception ex)
        {
            output.WriteLine(stage + ": " + ex);
            output.WriteLine("windowEnabled=" + window.IsEnabled + "; editorEnabled=" + editor.IsEnabled + "; pending=" + probe.PendingSyncCount + "; conflicts=" + probe.SyncConflicts().Count);
            output.WriteLine("status=" + window.FindControl<TextBlock>("StatusLabel")!.Text + "; sync=" + ToolTip.GetTip(Find<Button>(window, "SyncButton")));
            throw;
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task CoverAndDividerRenderFromPortableMetadataAndCanBeRemoved()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        using var bitmap = new RenderTargetBitmap(new(800, 220));
        using (var draw = bitmap.CreateDrawingContext())
        {
            draw.FillRectangle(Brush.Parse("#D8E7E0"), new Rect(0, 0, 800, 220)); draw.FillRectangle(Brush.Parse("#5E8D7B"), new Rect(40, 50, 240, 140));
            draw.FillRectangle(Brush.Parse("#F3F7ED"), new Rect(315, 60, 420, 30)); draw.FillRectangle(Brush.Parse("#EBCBA9"), new Rect(315, 115, 270, 20));
        }
        using var png = new MemoryStream(); bitmap.Save(png); png.Position = 0; var asset = store.ImportAsset(png, "封面.png");
        var doc = store.Create("为想法留一页空间", Doc(NoteNode.Paragraph("把零散的片段，整理成值得回看的记录。"), NoteNode.Toggle("本周计划", NoteNode.Paragraph("阅读、写作、散步。"))));
        store.SetAppearance(doc.Id, new(CoverAssetId: asset.Id, Divider: "dotted"));
        using var backup = new MemoryStream(); store.ExportBackup(backup); backup.Position = 0;
        using var restored = new NoteStore(Path.Combine(temporary.Path, "restored")); var imported = Assert.Single(restored.ImportBackup(backup)); Assert.Equal(asset.Id, restored.Appearance(imported).CoverAssetId); Assert.NotNull(restored.AssetPath(asset.Id));
        var window = new MainWindow(temporary.Path, false); window.Show(); Frame(window);
        try
        {
            Assert.True(window.FindControl<Border>("CoverFrame")!.IsVisible); Assert.NotNull(window.FindControl<Image>("CoverImage")!.Source);
            Assert.Equal("dotted", window.FindControl<PageDivider>("PageTitleDivider")!.Variant); SaveImage(window, "page-cover.png");
            Click(window, Find<Button>(window, "SidebarPage")); Click(window, Find<Button>(window, "RemoveCover"));
            Assert.False(window.FindControl<Border>("CoverFrame")!.IsVisible); Assert.Null(store.Appearance(doc.Id).CoverAssetId);
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public void NativeReferenceCompletionInsertsStableLinkAndSharesUndoWithText()
    {
        var (window, editor) = OpenEditor(NoteNode.Paragraph());
        try
        {
            editor.SetReferenceCatalogue([new("target", "目标笔记", 1, 2)], [new("阅读", "阅读", 2)]);
            window.KeyTextInput("[[目"); Frame(window); Assert.True(editor.References.IsVisible);
            Assert.Equal("target", Assert.Single(Find<ListBox>(editor, "ReferenceChoices").ItemsSource!.Cast<ReferenceChoice>()).Id);
            SaveImage(window, "reference-completion.png"); Key(window, Avalonia.Input.Key.Enter);
            var link = Assert.Single(NoteReferences.Read(editor.Session.Root)); Assert.Equal("target", link.Span.Target); Assert.Equal("目标笔记 ", editor.Surface.Text);
            ReferenceSpan? activated = null; editor.ReferenceInvoked += span => activated = span;
            editor.Surface.CaretOffset = 2; Key(window, Avalonia.Input.Key.Enter, RawInputModifiers.Alt); Assert.Equal("target", activated?.Target);
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control); Assert.Empty(NoteReferences.Read(editor.Session.Root)); Assert.Equal("[[目", editor.Surface.Text);
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control); Assert.Single(NoteReferences.Read(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ReferenceDraftProtectsChinesePreeditAndCannotApplyAfterDocumentSwitch()
    {
        var (window, editor) = OpenEditor(NoteNode.Paragraph("选中文字"));
        try
        {
            editor.SetReferenceCatalogue([new("target", "目标", 1, 2)], []);
            editor.Surface.Select(0, 4); Frame(window); Key(window, Avalonia.Input.Key.K, RawInputModifiers.Control | RawInputModifiers.Shift);
            var search = Find<TextBox>(editor, "ReferenceSearch"); search.Text = "目标"; Frame(window);
            var presenter = search.GetVisualDescendants().OfType<TextPresenter>().Single(); presenter.PreeditText = "mubiao";
            Key(window, Avalonia.Input.Key.Enter); Assert.Empty(NoteReferences.Read(editor.Session.Root)); Assert.True(editor.References.IsVisible);
            presenter.PreeditText = null; Key(window, Avalonia.Input.Key.Enter); Assert.Equal("target", Assert.Single(NoteReferences.Read(editor.Session.Root)).Span.Target);
            editor.Surface.Select(0, 4); editor.References.Open(); Frame(window);
            var oldSearch = Find<TextBox>(editor, "ReferenceSearch");
            editor.Load(new(Doc(NoteNode.Paragraph("另一篇文档")))); oldSearch.Text = "目标";
            var key = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter }; editor.References.HandleKey(key);
            Assert.Equal("另一篇文档", editor.Surface.Text); Assert.Empty(NoteReferences.Read(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TagCompletionRetainsReadableTextAndTitleShortcutStillWorks()
    {
        var (window, editor) = OpenEditor(NoteNode.Paragraph());
        try
        {
            editor.SetReferenceCatalogue([], [new("阅读", "阅读", 3)]);
            window.KeyTextInput("#阅"); Frame(window); Assert.True(editor.References.IsVisible); Key(window, Avalonia.Input.Key.Enter);
            Assert.Equal("#阅读 ", editor.Surface.Text); Assert.Equal("阅读", Assert.Single(NoteReferences.Read(editor.Session.Root)).Span.Label);
            editor.Load(new(Doc(NoteNode.Paragraph()))); editor.FocusText(); window.KeyTextInput("#"); Key(window, Avalonia.Input.Key.Space);
            Assert.Equal("heading", editor.Session.Root.Content[0].Type);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FavoritesBacklinksAndHiddenSearchWorkThroughTheNativeNavigation()
    {
        using var temporary = new TestDirectory(); using var probe = new NoteStore(temporary.Path);
        var target = probe.Create("读书笔记");
        var link = NoteNode.Paragraph() with { Content = [new("text") { Text = "读书笔记", Marks = [NoteMark.With("noteLink", "documentId", target.Id)] }] };
        var source = probe.Create("写作工作台", Doc(NoteNode.Paragraph("把想法留在这里 #阅读"), NoteNode.Toggle("待展开的想法", link, NoteNode.Paragraph("中文隐藏检索正文")).WithAttr("collapsed", true)));
        probe.Save(source.Id, source.Title, NoteJson.Parse(source.Content));
        var window = new MainWindow(temporary.Path, false); window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single();
        try
        {
            Click(window, Find<Button>(window, "FavoriteDocument")); Assert.True(probe.Get(source.Id).IsFavorite);
            Click(window, Find<Button>(window, "ShowSpaceSidebar")); Click(window, Find<Button>(window, "LibraryFavorites"));
            Assert.Equal(source.Id, Assert.Single(window.FindControl<ListBox>("DocumentList")!.ItemsSource!.Cast<DocumentItem>()).Id);
            Click(window, Find<Button>(window, "LibraryAll"));
            var search = window.FindControl<TextBox>("SearchBox")!; search.Text = "隐藏检索"; Frame(window);
            var list = window.FindControl<ListBox>("DocumentList")!; var result = Assert.Single(list.ItemsSource!.Cast<DocumentItem>()); Assert.Equal(source.Id, result.Id);
            Click(window, list.GetVisualDescendants().OfType<ListBoxItem>().Single());
            await Wait(() => editor.Surface.SelectedText == "隐藏检索"); Assert.False(editor.Session.Root.Content[1].Bool("collapsed"));
            SaveImage(window, "library-search.png");
            search.Text = "读书笔记"; Frame(window);
            list.SelectedItem = list.ItemsSource!.Cast<DocumentItem>().Single(item => item.Id == target.Id);
            await Wait(() => window.FindControl<TextBox>("TitleBox")!.Text == target.Title);
            Click(window, Find<Button>(window, "ShowDocumentSidebar")); SaveImage(window, "document-backlinks.png");
            Click(window, Find<Button>(window, "Backlink_" + source.Id));
            await Wait(() => editor.Surface.SelectedText == "读书笔记"); Assert.Equal(source.Title, window.FindControl<TextBox>("TitleBox")!.Text);
            await Close(window); Assert.True(probe.Get(source.Id).IsFavorite); Assert.Single(probe.Backlinks(target.Id));
        }
        finally { await Close(window); }
    }

    [AvaloniaFact]
    public async Task PageStyleSettingsThemeAndDailyNotesPersistAndAllFourPanelsAreFunctional()
    {
        using var temporary = new TestDirectory(); using var probe = new NoteStore(temporary.Path);
        var document = probe.Create("整理一段好时光", Doc(NoteNode.Paragraph("用中文写下一点灵感，让每一个想法都有自己的位置。"), NoteNode.Toggle("今天要完成的事", NoteNode.Paragraph("读一章书，整理一页笔记。"))));
        var window = new MainWindow(temporary.Path, false); window.Show(); Frame(window);
        var editor = window.GetVisualDescendants().OfType<BlockEditor>().Single(); var tools = window.GetVisualDescendants().OfType<EditorSidebar>().Single();
        try
        {
            Click(window, Find<Button>(tools, "SidebarPage")); Assert.Equal(EditorPanel.Page, tools.ActivePanel);
            Click(window, Find<Button>(tools, "PageFont_serif")); Click(window, Find<Button>(tools, "PageBackground_浅绿"));
            Find<Slider>(tools, "PageFontSize").Value = 18; Find<Slider>(tools, "PageLineHeight").Value = 1.5; Find<CheckBox>(tools, "PageWide").IsChecked = true; Frame(window);
            Assert.Equal("serif", probe.Appearance(document.Id).Font); Assert.Equal(18, editor.Surface.FontSize); Assert.Equal(1.5, editor.Surface.Options.LineHeightFactor);
            Assert.Equal(1200, window.FindControl<Grid>("EditorHost")!.MaxWidth); SaveImage(window, "page-appearance.png");
            var draft = Find<TextBox>(tools, "PageBackgroundHex"); draft.Text = "#abc";
            editor.Session.Edit(0, 0, "保存后仍保留草稿：", false); Key(window, Avalonia.Input.Key.S, RawInputModifiers.Control);
            await Wait(() => probe.Get(document.Id).Content.Contains("保存后仍保留草稿")); Assert.Equal("#abc", draft.Text); Assert.Same(draft, Find<TextBox>(tools, "PageBackgroundHex"));
            Click(window, Find<Button>(tools, "SidebarInfo")); Assert.NotNull(Find<Button>(tools, "PresentDocument")); Assert.NotNull(Find<Button>(tools, "InfoSyncSettings")); SaveImage(window, "page-info.png");
            Key(window, Avalonia.Input.Key.OemComma, RawInputModifiers.Control); await Wait(() => window.OwnedWindows.Count == 1);
            var settings = window.OwnedWindows.Single(); Frame(settings);
            Find<Slider>(settings, "GhostOpacity").Value = .55; Find<ComboBox>(settings, "SettingsTheme").SelectedIndex = 1; Frame(settings);
            Assert.Equal(.55, editor.GhostOpacity, 3); Assert.Equal(new("dark", .55), probe.Preferences()); Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
            SaveImage(settings, "settings-dark.png"); settings.Close(); Frame(window); SaveImage(window, "workspace-dark.png");
            var expected = probe.Appearance(document.Id); await Close(window);
            var reopened = new MainWindow(temporary.Path, false); reopened.Show(); Frame(reopened);
            try { Assert.Equal(expected, probe.Appearance(document.Id)); Assert.Equal(.55, reopened.GetVisualDescendants().OfType<BlockEditor>().Single().GhostOpacity, 3); }
            finally { await Close(reopened); }
        }
        finally { await Close(window); Application.Current!.RequestedThemeVariant = ThemeVariant.Light; }
    }

    [AvaloniaFact]
    public void ImageAndAttachmentAreNativeAtomicBlocksWithSingleUndoAndValidHitAreas()
    {
        using var temporary = new TestDirectory(); using var store = new NoteStore(temporary.Path);
        using var bitmap = new RenderTargetBitmap(new(640, 220));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            drawing.FillRectangle(Brush.Parse("#D9EAE2"), new Rect(0, 0, 640, 220)); drawing.FillRectangle(Brush.Parse("#4D806F"), new Rect(45, 45, 180, 130));
            drawing.FillRectangle(Brush.Parse("#B8D2E4"), new Rect(250, 75, 330, 28)); drawing.FillRectangle(Brush.Parse("#EEF5F1"), new Rect(250, 122, 240, 18));
        }
        using var source = new MemoryStream(); bitmap.Save(source); source.Position = 0; var image = store.ImportAsset(source, "原生图片示例.png");
        var (window, editor) = OpenEditor(NoteNode.Paragraph("图片与附件")); editor.ResolveAsset = store.AssetPath;
        try
        {
            var before = NoteJson.Serialize(editor.Session.Root); Assert.True(editor.Session.InsertAsset(5, image, true)); Frame(window);
            Assert.Contains(editor.Session.Projection.Rows, row => row.IsAtomic && row.Node.Type == "image");
            var nativeImage = editor.Surface.GetVisualDescendants().OfType<Image>().Single(); Assert.True(nativeImage.Bounds.Width > 100); Assert.True(nativeImage.Bounds.Height > 20);
            var node = editor.Session.Root.Content.Single(node => node.Type == "image"); var button = Find<Button>(editor, "Asset_" + node.Id);
            NoteNode? activated = null; editor.AssetInvoked += asset => activated = asset; Click(window, button); Assert.Same(node, activated);
            SaveImage(window, "native-image.png"); Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control); Assert.Contains(editor.Session.Root.Content, child => child.Type == "image");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void DragGhostUsesTheConfiguredOpacityAndEscapeLeavesDocumentUntouched()
    {
        var (window, editor) = OpenEditor(NoteNode.Toggle("拖动这个想法", NoteNode.Paragraph("保留全部子内容")), NoteNode.Paragraph("目标位置"));
        try
        {
            editor.GhostOpacity = .4; var before = NoteJson.Serialize(editor.Session.Root);
            var grip = editor.GetVisualDescendants().OfType<Button>().First(button => button.Classes.Contains("blockGrip"));
            var point = grip.TranslatePoint(new(grip.Bounds.Width / 2, grip.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); Frame(window); window.MouseDown(point, MouseButton.Left); Frame(window);
            window.MouseMove(new(point.X + 140, point.Y + 80), RawInputModifiers.LeftMouseButton); Frame(window);
            var ghost = Find<Border>(editor, "DragGhost"); Assert.True(ghost.IsVisible); Assert.Equal(.4, ghost.Opacity, 3); Assert.False(ghost.IsHitTestVisible); SaveImage(window, "drag-ghost-opacity.png");
            Key(window, Avalonia.Input.Key.Escape); window.MouseUp(new(point.X + 140, point.Y + 80), MouseButton.Left); Frame(window);
            Assert.False(ghost.IsVisible); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void PresentationPreservesInlineFormattingAndKeyboardPagingWithoutEditingTheSource()
    {
        var paragraph = NoteNode.Paragraph() with { Content = [new("text") { Text = "大胆写下想法", Marks = [new("bold")] }, new("text") { Text = "，再慢慢整理。" }] };
        var root = Doc(NoteNode.Paragraph("第一部分") with { Type = "heading" }, paragraph, new("horizontalRule"), NoteNode.Paragraph("第二部分") with { Type = "heading" }, NoteNode.Paragraph("让表达更清晰。"));
        var before = NoteJson.Serialize(root); var window = new PresentationWindow("我的演示", root, new("serif", Background: "#252A31"), _ => null); window.Show(); Frame(window);
        try
        {
            Assert.Equal("1 / 2", Find<TextBlock>(window, "PresentationPosition").Text);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>().SelectMany(block => block.Inlines?.OfType<Run>() ?? []), run => run.Text == "大胆写下想法" && run.FontWeight == FontWeight.Bold);
            Assert.Equal(Colors.White, ((ISolidColorBrush)window.Foreground!).Color); SaveImage(window, "native-presentation.png");
            Key(window, Avalonia.Input.Key.Right); Assert.Equal("2 / 2", Find<TextBlock>(window, "PresentationPosition").Text);
            Key(window, Avalonia.Input.Key.Home); Assert.Equal("1 / 2", Find<TextBlock>(window, "PresentationPosition").Text); Assert.Equal(before, NoteJson.Serialize(root));
            Key(window, Avalonia.Input.Key.Escape); Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }
}
