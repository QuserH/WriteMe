using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using WriteMe.Core;

namespace WriteMe.Desktop;

// Note: 页面样式与本机偏好分离，真实能力接入 Craft 四入口 — 见 .agents/notes/implemented/feature/2026-09-10-page-assets-and-portability.md
public sealed partial class MainWindow
{
    private PageAppearance _pageAppearance = new();
    private AppPreferences _preferences = new();
    private Bitmap? _coverBitmap;
    private string? _coverPath;

    private void InitializeWorkspaceUi()
    {
        _preferences = _store.Preferences();
        ApplyPreferences();
        _editor.ResolveAsset = _store.AssetPath;
        _editor.AssetInvoked += async node => await RunUiAsync(() => OpenAssetAsync(node));
        _tools.ConfigureExtendedPanels(BuildPagePanel, BuildInfoPanel, image => _ = RunUiAsync(() => InsertAssetsAsync(image)));
        ApplyPageAppearance();
        PropertyChanged += (_, args) =>
        {
            if (args.Property == ActualThemeVariantProperty) Dispatcher.UIThread.Post(() => { if (!_closed) ApplyPageAppearance(); }, DispatcherPriority.Background);
        };
    }
    private void ApplyPreferences()
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = _preferences.Theme switch { "dark" => ThemeVariant.Dark, "system" => ThemeVariant.Default, _ => ThemeVariant.Light };
            Ui.ApplyDarkTheme(application.ActualThemeVariant == ThemeVariant.Dark);
        }
        _editor.GhostOpacity = _preferences.GhostOpacity;
        ApplyPageAppearance();
    }
    internal static FontFamily PageFont(string name) => new(name switch
    {
        "serif" => "SimSun, Noto Serif CJK SC, Georgia, serif", "mono" => "Cascadia Code, Consolas, Microsoft YaHei UI, monospace",
        "round" => "Microsoft YaHei UI, YouYuan, Segoe UI, sans-serif", _ => "Microsoft YaHei UI, Segoe UI, Noto Sans CJK SC, sans-serif"
    });
    private void ApplyPageAppearance()
    {
        _pageAppearance = _store.Appearance(_active.Id);
        _editor.PageBackgroundColor = _pageAppearance.Background == null ? null : Color.Parse(_pageAppearance.Background);
        _editor.DividerStyle = _pageAppearance.Divider;
        _editor.Surface.FontFamily = PageFont(_pageAppearance.Font); _editor.Surface.FontSize = _pageAppearance.FontSize;
        _editor.Surface.Options.LineHeightFactor = _pageAppearance.LineHeight;
        _title.FontFamily = PageFont(_pageAppearance.Font);
        var ink = PageInk(_pageAppearance);
        _editor.Surface.Foreground = ink; _title.Foreground = ink;
        var divider = this.FindControl<PageDivider>("PageTitleDivider")!; divider.Stroke = _editor.PageLine; divider.Variant = _pageAppearance.Divider; divider.InvalidateVisual();
        ApplyCover();
        this.FindControl<Border>("PageBackground")!.Background = _pageAppearance.Background == null ? Ui.Surface : Brush.Parse(_pageAppearance.Background);
        this.FindControl<Grid>("EditorHost")!.MaxWidth = _pageAppearance.Width;
        this.FindControl<Grid>("TitleRegion")!.MaxWidth = _pageAppearance.Width;
        _editor.RefreshPageAppearance();
    }
    private void ApplyCover()
    {
        var path = _pageAppearance.CoverAssetId is { } id ? _store.AssetPath(id) : null;
        if (path != _coverPath)
        {
            this.FindControl<Image>("CoverImage")!.Source = null; _coverBitmap?.Dispose(); _coverBitmap = null; _coverPath = path;
            if (path != null)
            {
                try { using var stream = File.OpenRead(path); _coverBitmap = Bitmap.DecodeToWidth(stream, 1200); }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { _status.Text = "封面无法显示，可以重新选择图片"; }
            }
        }
        this.FindControl<Border>("CoverFrame")!.IsVisible = _pageAppearance.CoverAssetId != null;
        this.FindControl<Image>("CoverImage")!.Source = _coverBitmap;
    }
    internal static IBrush PageInk(PageAppearance appearance)
    {
        if (appearance.TextColor is { } color) return Brush.Parse(color);
        if (appearance.Background == null) return Ui.Ink;
        var background = Color.Parse(appearance.Background);
        return background.R * .299 + background.G * .587 + background.B * .114 < 140 ? Brushes.White : Brush.Parse("#1F2225");
    }
    private static TextBlock PanelHeading(string title) => new() { Text = title, Foreground = Ui.Muted, FontSize = 11, Margin = new(0, 13, 0, 6) };
    private static Button PanelAction(string label, Action action, string? id = null)
    {
        var button = NavigationButton(label, "", action, automationId: id); button.Padding = new(0, 7); return button;
    }

    private Control BuildPagePanel()
    {
        var documentId = _active.Id;
        var panel = new StackPanel { Margin = new(14, 0, 14, 18), Spacing = 8 };
        var preview = new Border { Background = _pageAppearance.Background == null ? Ui.Surface : Brush.Parse(_pageAppearance.Background), BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(11), Height = 122, Padding = new(22), Margin = new(18, 5) };
        var sample = new StackPanel { Spacing = 8 };
        sample.Children.Add(new TextBlock { Text = "写下你的想法", FontWeight = FontWeight.SemiBold, FontSize = 17, Foreground = PageInk(_pageAppearance) });
        sample.Children.Add(new Border { Height = 2, Background = Ui.Line, Margin = new(0, 3, 24, 0) });
        sample.Children.Add(new Border { Height = 2, Background = Ui.Line, Margin = new(0, 3, 44, 0) });
        preview.Child = sample; panel.Children.Add(preview);
        void Save(PageAppearance next)
        {
            if (_active.Id != documentId || ActiveHasConflict) return;
            _store.SetAppearance(documentId, next); ApplyPageAppearance();
            preview.Background = _pageAppearance.Background == null ? Ui.Surface : Brush.Parse(_pageAppearance.Background);
            foreach (var label in sample.Children.OfType<TextBlock>()) { label.Foreground = PageInk(_pageAppearance); label.FontFamily = PageFont(_pageAppearance.Font); }
        }
        panel.Children.Add(PanelHeading("颜色"));
        var backgrounds = new UniformGrid { Columns = 6, Rows = 1 };
        foreach (var (name, color) in new (string, string?)[] { ("默认", null), ("纸白", "#FFFDF5"), ("浅绿", "#F0F7F1"), ("浅蓝", "#F0F5FC"), ("浅粉", "#FCF1F4"), ("墨色", "#252A31") })
        {
            var button = new Button { Width = 28, Height = 28, CornerRadius = new(14), Background = color == null ? Ui.Surface : Brush.Parse(color), BorderBrush = Ui.Line, BorderThickness = new(1), Padding = new(0), Content = color == null ? "A" : null };
            AutomationProperties.SetAutomationId(button, "PageBackground_" + name); ToolTip.SetTip(button, name);
            button.Click += (_, _) => Save(_pageAppearance with { Background = color }); backgrounds.Children.Add(button);
        }
        panel.Children.Add(backgrounds);
        ColorInput("文档颜色", _pageAppearance.Background, "PageBackgroundHex", value => Save(_pageAppearance with { Background = value }));
        ColorInput("文本颜色", _pageAppearance.TextColor, "PageTextHex", value => Save(_pageAppearance with { TextColor = value }));
        void ColorInput(string label, string? value, string id, Action<string?> update)
        {
            var row = new Grid { ColumnDefinitions = new("68,*,Auto"), Margin = new(0, 3) };
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
            var input = new TextBox { Text = value ?? "", Watermark = "默认 / #HEX", FontSize = 11, MinHeight = 28, Padding = new(7, 4) };
            AutomationProperties.SetAutomationId(input, id); Grid.SetColumn(input, 1); row.Children.Add(input);
            var apply = new Button { Content = "应用", FontSize = 11, Padding = new(7, 5), Margin = new(5, 0, 0, 0) };
            void Apply()
            {
                if (input.GetVisualDescendants().OfType<TextPresenter>().Any(presenter => !string.IsNullOrEmpty(presenter.PreeditText))) return;
                var color = string.IsNullOrWhiteSpace(input.Text) ? null : TextColor.Normalize(input.Text);
                if (!string.IsNullOrWhiteSpace(input.Text) && color == null) { _status.Text = "请输入 #426BB3 或 #abc；留空恢复默认"; return; }
                update(color);
            }
            apply.Click += (_, _) => Apply(); input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Apply(); e.Handled = true; } };
            Grid.SetColumn(apply, 2); row.Children.Add(apply); panel.Children.Add(row);
        }
        panel.Children.Add(PanelHeading("封面"));
        panel.Children.Add(PanelAction(_pageAppearance.CoverAssetId == null ? "添加封面图片…" : "更换封面图片…", () => _ = RunUiAsync(() => SetCoverAsync(documentId)), "ChooseCover"));
        if (_pageAppearance.CoverAssetId != null) panel.Children.Add(PanelAction("移除封面", () => { Save(_pageAppearance with { CoverAssetId = null }); _tools.RefreshExtendedPanel(true); }, "RemoveCover"));
        panel.Children.Add(PanelHeading("分割线"));
        var separators = new ComboBox { ItemsSource = new[] { "细线", "点线", "隐藏" }, SelectedIndex = _pageAppearance.Divider switch { "dotted" => 1, "none" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 };
        AutomationProperties.SetAutomationId(separators, "PageDividerStyle");
        separators.SelectionChanged += (_, _) => Save(_pageAppearance with { Divider = separators.SelectedIndex switch { 1 => "dotted", 2 => "none", _ => "line" } }); panel.Children.Add(separators);
        panel.Children.Add(PanelHeading("字体"));
        var fonts = new UniformGrid { Columns = 4, Rows = 1 };
        foreach (var (name, value) in new[] { ("默认", "sans"), ("衬线", "serif"), ("等宽", "mono"), ("圆体", "round") })
        {
            var button = new Button { Content = name, FontSize = 11, Padding = new(4, 8), HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Classes.Add("quiet"); button.Classes.Set("active", value == _pageAppearance.Font); AutomationProperties.SetAutomationId(button, "PageFont_" + value);
            button.Click += (_, _) => { Save(_pageAppearance with { Font = value }); foreach (var other in fonts.Children.OfType<Button>()) other.Classes.Set("active", other == button); };
            fonts.Children.Add(button);
        }
        panel.Children.Add(fonts);
        SliderRow("字号", "PageFontSize", 12, 24, 1, _pageAppearance.FontSize, value => Save(_pageAppearance with { FontSize = value }), value => value.ToString("0") + " px");
        SliderRow("行距", "PageLineHeight", 1, 2, .05, _pageAppearance.LineHeight, value => Save(_pageAppearance with { LineHeight = value }), value => value.ToString("0.##"));
        panel.Children.Add(PanelHeading("高级"));
        var wide = new CheckBox { Content = "宽页", IsChecked = _pageAppearance.Width > 900 };
        AutomationProperties.SetAutomationId(wide, "PageWide"); wide.IsCheckedChanged += (_, _) => Save(_pageAppearance with { Width = wide.IsChecked == true ? 1200 : 900 }); panel.Children.Add(wide);
        panel.Children.Add(PanelAction("恢复默认页面样式", () => { Save(new()); _tools.RefreshExtendedPanel(true); }, "ResetPageAppearance"));
        return panel;

        void SliderRow(string name, string id, double min, double max, double tick, double value, Action<double> apply, Func<double, string> display)
        {
            var title = new TextBlock { Text = name + "  " + display(value), FontSize = 12, Margin = new(0, 6, 0, 0) }; panel.Children.Add(title);
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = tick, IsSnapToTickEnabled = true, Value = value };
            AutomationProperties.SetAutomationId(slider, id); slider.ValueChanged += (_, _) => { title.Text = name + "  " + display(slider.Value); apply(slider.Value); }; panel.Children.Add(slider);
        }
    }

    private Control BuildInfoPanel()
    {
        var panel = new StackPanel { Margin = new(14, 0, 14, 18), Spacing = 6 };
        var doc = _store.Get(_active.Id); var location = _store.Location(doc.Id); var root = _editor.Session.Root;
        panel.Children.Add(PanelHeading("属性"));
        void Info(string name, string value)
        {
            var row = new Grid { ColumnDefinitions = new("64,*"), Margin = new(0, 6) };
            row.Children.Add(new TextBlock { Text = name, FontSize = 11, Foreground = Ui.Muted });
            var text = new TextBlock { Text = value, FontSize = 11, TextWrapping = TextWrapping.Wrap }; Grid.SetColumn(text, 1); row.Children.Add(text); panel.Children.Add(row);
        }
        Info("已创建", DateTimeOffset.FromUnixTimeMilliseconds(doc.CreatedAt).LocalDateTime.ToString("yyyy/M/d HH:mm"));
        Info("已更新", DateTimeOffset.FromUnixTimeMilliseconds(doc.UpdatedAt).LocalDateTime.ToString("yyyy/M/d HH:mm"));
        Info("位置", Places(location.SpaceId).FirstOrDefault(place => place.FolderId == location.FolderId)?.Label ?? "未分类");
        panel.Children.Add(PanelHeading("统计"));
        var text = DocumentText.Plain(root);
        var characters = text.EnumerateRunes().Count(rune => !System.Text.Rune.IsWhiteSpace(rune));
        Info("字符", characters.ToString("N0")); Info("区块", NoteTree.Descendants(root).Count(node => node.IsTextBlock || node.Type is "image" or "attachment" or "horizontalRule").ToString());
        Info("阅读时间", Math.Max(1, (int)Math.Ceiling(characters / 300d)) + " 分钟");
        Info("附件", DocumentAssets.Read(root, _pageAppearance).Count().ToString());
        panel.Children.Add(PanelHeading("动作"));
        panel.Children.Add(PanelAction("演示文档", Present, "PresentDocument"));
        panel.Children.Add(PanelAction("移动到…", () => _ = RunUiAsync(MoveActiveAsync), "MoveDocument"));
        panel.Children.Add(PanelAction("创建副本", () => _ = RunUiAsync(DuplicateAsync), "DuplicateDocument"));
        panel.Children.Add(PanelAction(doc.IsFavorite ? "取消收藏" : "收藏文档", () => { ToggleFavorite(); _tools.RefreshExtendedPanel(); }, "InfoFavorite"));
        panel.Children.Add(PanelAction("查看本地备份", () => _ = RunUiAsync(RestoreRevisionAsync), "DocumentRevisions"));
        panel.Children.Add(PanelAction("移至回收站", () => _ = RunUiAsync(DeleteAsync), "TrashDocument"));
        AddSyncInfo(panel);
        return panel;
    }

    private async Task SettingsAsync()
    {
        var content = new StackPanel { Spacing = 12, Margin = new(24) };
        content.Children.Add(new TextBlock { Text = "设置", FontSize = 23, FontWeight = FontWeight.SemiBold });
        content.Children.Add(PanelHeading("外观"));
        var theme = new ComboBox { ItemsSource = new[] { "浅色", "深色", "跟随系统" }, SelectedIndex = _preferences.Theme switch { "dark" => 1, "system" => 2, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(theme, "SettingsTheme"); theme.SelectionChanged += (_, _) =>
        {
            _preferences = _preferences with { Theme = theme.SelectedIndex switch { 1 => "dark", 2 => "system", _ => "light" } };
            _store.SetPreferences(_preferences); ApplyPreferences();
        };
        content.Children.Add(theme);
        content.Children.Add(PanelHeading("拖动"));
        var label = new TextBlock { Text = "幽灵块不透明度  " + _preferences.GhostOpacity.ToString("P0"), FontSize = 12 }; content.Children.Add(label);
        var slider = new Slider { Minimum = .4, Maximum = 1, TickFrequency = .05, IsSnapToTickEnabled = true, Value = _preferences.GhostOpacity };
        AutomationProperties.SetAutomationId(slider, "GhostOpacity"); content.Children.Add(slider);
        var preview = new Border { Child = new TextBlock { Text = "拖动时的预览效果", FontSize = 14 }, Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new(1), CornerRadius = new(8), Padding = new(16), Opacity = _preferences.GhostOpacity };
        content.Children.Add(new Border { Child = preview, Background = Ui.Subtle, Padding = new(15), CornerRadius = new(10) });
        slider.ValueChanged += (_, _) =>
        {
            _preferences = _preferences with { GhostOpacity = slider.Value }; _store.SetPreferences(_preferences); _editor.GhostOpacity = slider.Value;
            preview.Opacity = slider.Value; label.Text = "幽灵块不透明度  " + slider.Value.ToString("P0");
        };
        content.Children.Add(PanelHeading("每日笔记"));
        var date = new TextBox { Text = DateTime.Today.ToString("yyyy-MM-dd"), Watermark = "yyyy-MM-dd" }; AutomationProperties.SetAutomationId(date, "DailyNoteDate"); content.Children.Add(date);
        var daily = new Button { Content = "打开这一天的笔记" }; content.Children.Add(daily);
        AddSyncSettings(content);
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var dialog = Dialog("WriteME 设置", scroll, 520, Math.Min(680, Math.Max(490, Bounds.Height - 60)));
        daily.Click += async (_, _) =>
        {
            if (!DateOnly.TryParseExact(date.Text, "yyyy-MM-dd", out var parsed)) { _status.Text = "请输入 yyyy-MM-dd 格式的日期"; return; }
            dialog.Close(); await RunUiAsync(() => OpenDailyAsync(parsed));
        };
        var close = new Button { Content = "完成", HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 14, 0, 0) }; close.Click += (_, _) => dialog.Close(); content.Children.Add(close);
        await dialog.ShowDialog(this);
    }
    partial void AddSyncSettings(StackPanel panel);
    partial void AddSyncInfo(StackPanel panel);
    partial void AddLibraryExtraActions(StackPanel panel)
    {
        panel.Children.Add(NavigationButton("每日笔记", "▦", () => _ = RunUiAsync(() => OpenDailyAsync(DateOnly.FromDateTime(DateTime.Now))), automationId: "LibraryDaily"));
    }
    private async Task OpenDailyAsync(DateOnly date)
    {
        if (!await SaveAsync()) return;
        var document = _store.DailyNote(date, _spaceId); _libraryMode = "all"; _folderId = null; _tagFilter = null; ReloadDocuments(); await SwitchAsync(document.Id); _editor.FocusText();
    }
}
