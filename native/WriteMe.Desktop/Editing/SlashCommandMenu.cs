using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using WriteMe.Core;

namespace WriteMe.Desktop.Editing;

// Note: 同位置分类/二级菜单、保留输入焦点与查询事务 — 见 .agents/notes/implemented/architecture/2026-09-09-native-block-editor.md
internal sealed class SlashCommandMenu : Border
{
    private sealed record Entry(string Id, string Name, SidebarSymbol Symbol, string Aliases = "", SlashAction? Action = null, Entry[]? Children = null, string? Color = null);
    private static Entry Block(string name, string kind, SidebarSymbol symbol, string aliases = "", int level = 1)
        => new(kind + (kind == "heading" ? level : ""), name, symbol, aliases, new(SlashOperation.Block, kind, level));
    private static readonly Entry[] Catalogue =
    [
        new("lists", "列表", SidebarSymbol.BulletList, "list liebiao", Children:
        [
            Block("待办列表", "taskList", SidebarSymbol.Task, "todo task daiban 待办事项"),
            Block("折叠列表", "toggleBlock", SidebarSymbol.Toggle, "toggle zhedie 折叠块"),
            Block("无序列表", "bulletList", SidebarSymbol.BulletList, "bullet list wuxu"),
            Block("编号列表", "orderedList", SidebarSymbol.OrderedList, "ordered numbered list youxu 有序列表"),
            Block("取消列表格式", "paragraph", SidebarSymbol.Clear, "取消列表 remove list") with { Id = "clearList" }
        ]),
        new("styles", "文本样式", SidebarSymbol.Format, "style text biaoti", Children:
        [
            Block("一级标题", "heading", SidebarSymbol.Heading1, "h1 heading biaoti", 1),
            Block("二级标题", "heading", SidebarSymbol.Heading2, "h2 heading biaoti", 2),
            Block("三级标题", "heading", SidebarSymbol.Heading3, "h3 heading biaoti", 3),
            Block("正文", "paragraph", SidebarSymbol.Text, "text paragraph zhengwen")
        ]),
        new("decoration", "装饰", SidebarSymbol.Quote, "decoration zhuangshi", Children:
        [
            Block("引用", "blockquote", SidebarSymbol.Quote, "quote yinyong"),
            Block("代码块", "codeBlock", SidebarSymbol.Code, "code daima")
        ]),
        new("colors", "颜色", SidebarSymbol.Paint, "color yanse", Children:
        [
            new("colorDefault", "默认文字颜色", SidebarSymbol.NoColor, "default color 恢复默认", new(SlashOperation.TextColor)),
            .. Ui.TextColors.Select(color => new Entry("color" + color.Color[1..], color.Name, SidebarSymbol.Text,
                "color 文字颜色", new(SlashOperation.TextColor, color.Color), Color: color.Color))
        ]),
        new("indentation", "缩进", SidebarSymbol.Indent, "indent suojin", Children:
        [
            new("indent", "增加缩进", SidebarSymbol.Indent, "indent tab 增加层级", new(SlashOperation.Indent)),
            new("outdent", "减少缩进", SidebarSymbol.Outdent, "outdent shift tab 移出层级", new(SlashOperation.Outdent))
        ]),
        new("alignment", "对齐", SidebarSymbol.AlignLeft, "alignment duiqi", Children:
        [
            new("alignLeft", "左对齐", SidebarSymbol.AlignLeft, "align left zuo", new(SlashOperation.Alignment, "left")),
            new("alignCenter", "居中", SidebarSymbol.AlignCenter, "align center juzhong", new(SlashOperation.Alignment, "center")),
            new("alignRight", "右对齐", SidebarSymbol.AlignRight, "align right you", new(SlashOperation.Alignment, "right")),
            new("alignJustify", "两端对齐", SidebarSymbol.AlignJustify, "align justify liangduan", new(SlashOperation.Alignment, "justify"))
        ]),
        new("formats", "文字格式", SidebarSymbol.Bold, "format geshi", Children:
        [
            new("bold", "粗体", SidebarSymbol.Bold, "bold cuti", new(SlashOperation.Format, "bold")),
            new("italic", "斜体", SidebarSymbol.Italic, "italic xieti", new(SlashOperation.Format, "italic")),
            new("underline", "下划线", SidebarSymbol.Underline, "underline xiahuaxian", new(SlashOperation.Format, "underline")),
            new("strike", "删除线", SidebarSymbol.Strike, "strike shanchuxian", new(SlashOperation.Format, "strike")),
            new("inlineCode", "行内代码", SidebarSymbol.Code, "inline code", new(SlashOperation.Format, "code")),
            new("clearFormat", "清除文字格式", SidebarSymbol.Clear, "clear format qingchu", new(SlashOperation.Format, "clear"))
        ]),
        new("tables", "插入表格", SidebarSymbol.Table, "table grid biaoge", Children:
            Enumerable.Range(2, 8).Select(size => new Entry("table" + size, $"{size} × {size}", SidebarSymbol.Table,
                $"{size}x{size} 表格 table grid biaoge", new(SlashOperation.Table, Level: size))).ToArray()),
        new("columns", "分栏", SidebarSymbol.Columns, "columns layout fenlan", Children:
        [
            new("columns2", "两栏布局", SidebarSymbol.Columns, "columns layout fenlan lianglan", new(SlashOperation.Columns, Level: 2)),
            new("columns3", "三栏布局", SidebarSymbol.Columns, "columns layout fenlan sanlan", new(SlashOperation.Columns, Level: 3))
        ]),
        Block("插入分隔符", "horizontalRule", SidebarSymbol.Divider, "divider rule fengexian 分割线")
    ];

    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private readonly Button _back;
    private readonly List<(Entry Entry, Button Button)> _buttons = [];
    private Func<SlashAction, bool>? _canApply;
    private Entry? _category;
    private string _query = "";
    private int _selected = -1;
    private int _returnIndex;
    private Point? _lastPointer;
    public event Action<SlashAction>? Invoked;
    public event Action? Dismissed;
    public event Action? PageChanged;

    public SlashCommandMenu()
    {
        IsVisible = false; Width = 232; Padding = new(8); CornerRadius = new(18);
        Background = Ui.Chrome("#FAFAFA"); BorderBrush = Ui.Chrome("#EDEEEF"); BorderThickness = new(1);
        BoxShadow = Ui.FloatingShadow;
        HorizontalAlignment = HorizontalAlignment.Left; VerticalAlignment = VerticalAlignment.Top; ZIndex = 80;
        AutomationProperties.SetAutomationId(this, "SlashMenu");
        var content = new Grid { RowDefinitions = new("Auto,*") };
        _back = new Button { Height = 28, Padding = new(8, 0), Focusable = false, IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        _back.Classes.Add("slashBack"); AutomationProperties.SetAutomationId(_back, "SlashBack");
        _back.Click += (_, _) => Back(); content.Children.Add(_back);
        _scroll = new ScrollViewer { Content = _rows, MaxHeight = 272, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(_scroll, 1); content.Children.Add(_scroll); Child = content;
    }

    public void ShowQuery(string query, Func<SlashAction, bool> canApply, bool restart)
    {
        _canApply = canApply;
        if (restart || query != _query) { _category = null; _selected = -1; }
        var rebuild = restart || query != _query || !IsVisible;
        _query = query;
        IsVisible = true;
        if (rebuild) Rebuild();
    }

    public void Reset() { IsVisible = false; _category = null; _selected = -1; _query = ""; _canApply = null; }

    private void Rebuild()
    {
        _buttons.Clear(); _rows.Children.Clear(); _scroll.Offset = default;
        _back.IsVisible = _category != null;
        _back.Content = _category == null ? "" : "‹  " + _category.Name;
        AutomationProperties.SetName(_back, "返回上一级");
        AutomationProperties.SetName(this, _category == null ? "插入或设置格式" : _category.Name);
        IEnumerable<Entry> entries = _category?.Children ?? Catalogue;
        if (_category == null && _query.Length > 0)
        {
            bool Match(Entry entry) => (entry.Name + " " + entry.Aliases).Contains(_query, StringComparison.OrdinalIgnoreCase);
            entries = Catalogue.Where(entry => entry.Children != null && Match(entry))
                .Concat(Catalogue.SelectMany(entry => entry.Children ?? [entry]).Where(Match));
        }
        foreach (var entry in entries)
        {
            var row = new Grid { ColumnDefinitions = new("18,*,14"), ColumnSpacing = 8 };
            var icon = new SidebarGlyph(entry.Symbol, 15) { VerticalAlignment = VerticalAlignment.Center };
            TextElement.SetForeground(icon, entry.Color == null ? Ui.Chrome("#686C71") : Brush.Parse(entry.Color));
            row.Children.Add(icon);
            var label = new TextBlock { Text = entry.Name, FontSize = 13, FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 1); row.Children.Add(label);
            if (entry.Children != null)
            {
                var arrow = new SidebarGlyph(SidebarSymbol.ChevronRight, 12) { VerticalAlignment = VerticalAlignment.Center };
                TextElement.SetForeground(arrow, Ui.Chrome("#9EA2A7")); Grid.SetColumn(arrow, 2); row.Children.Add(arrow);
            }
            var button = new Button { Content = row, Height = 32, MinHeight = 0, Padding = new(8, 6), Focusable = false,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsEnabled = entry.Action == null || _canApply?.Invoke(entry.Action) == true };
            button.Classes.Add("slashItem");
            AutomationProperties.SetName(button, entry.Name + (entry.Children != null ? "，子菜单" : ""));
            AutomationProperties.SetAutomationId(button, "Slash_" + entry.Id);
            var index = _buttons.Count;
            button.PointerMoved += (_, args) =>
            {
                var point = args.GetPosition(this);
                if (_lastPointer != point) { _lastPointer = point; if (button.IsEnabled) Select(index, false); }
            };
            button.Click += (_, _) =>
            {
                if (!IsVisible || index >= _buttons.Count || !ReferenceEquals(_buttons[index].Button, button)) return;
                Select(index, false); Activate();
            };
            _buttons.Add((entry, button)); _rows.Children.Add(button);
        }
        if (_buttons.Count == 0)
            _rows.Children.Add(new TextBlock { Text = "没有匹配的命令", FontSize = 13, Foreground = Ui.Muted, Margin = new(8, 12) });
        Select(_buttons.FindIndex(pair => pair.Button.IsEnabled), false);
        PageChanged?.Invoke();
    }

    private void Select(int index, bool scroll)
    {
        if (index < 0 || index >= _buttons.Count) { _selected = -1; return; }
        for (var i = 0; i < _buttons.Count; i++) _buttons[i].Button.Classes.Set("selected", i == index);
        _selected = index;
        if (scroll) _buttons[index].Button.BringIntoView();
    }

    private void Activate()
    {
        if (_selected < 0 || _selected >= _buttons.Count || !_buttons[_selected].Button.IsEnabled) return;
        var entry = _buttons[_selected].Entry;
        if (entry.Children != null) { _returnIndex = _selected; _category = entry; Rebuild(); }
        else if (entry.Action != null) Invoked?.Invoke(entry.Action);
    }

    private bool Back()
    {
        if (_category == null) return false;
        _category = null; Rebuild(); Select(_returnIndex, true); return true;
    }

    public bool HandleKey(KeyEventArgs args)
    {
        if (!IsVisible || args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Alt)) return false;
        switch (args.Key)
        {
            case Key.Up:
            case Key.Down:
                for (var step = 1; step <= _buttons.Count; step++)
                {
                    var index = (_selected + (args.Key == Key.Down ? step : -step) + _buttons.Count) % _buttons.Count;
                    if (_buttons[index].Button.IsEnabled) { Select(index, true); break; }
                }
                break;
            case Key.Home: Select(_buttons.FindIndex(pair => pair.Button.IsEnabled), true); break;
            case Key.End: Select(_buttons.FindLastIndex(pair => pair.Button.IsEnabled), true); break;
            case Key.Right:
                if (_selected >= 0 && _buttons[_selected].Entry.Children != null) Activate();
                else return false;
                break;
            case Key.Left: if (!Back()) { Dismissed?.Invoke(); return false; } break;
            case Key.Escape: if (!Back()) Dismissed?.Invoke(); break;
            case Key.Enter: Activate(); break;
            case Key.Tab: if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) { if (!Back()) Dismissed?.Invoke(); } else Activate(); break;
            default: return false;
        }
        args.Handled = true; return true;
    }
}
