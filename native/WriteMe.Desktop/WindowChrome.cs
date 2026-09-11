using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace WriteMe.Desktop;

// Note: 顶栏与系统窗口边缘合并，保留原生移动/缩放与关闭保存 — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
internal static class WindowChrome
{
    public static void Attach(Window window, Control header, Panel buttons, Border frame)
    {
        var maximizeGlyph = new CaptionGlyph("maximize");
        foreach (var (name, symbol, action) in new (string, string, Action)[]
        {
            ("最小化", "minimize", () => window.WindowState = WindowState.Minimized),
            ("最大化或还原", "maximize", () => Toggle(window)),
            ("关闭窗口", "close", window.Close)
        })
        {
            var button = new Button { Width = 44, Height = 38, Padding = new(0), CornerRadius = new(0),
                Content = symbol == "maximize" ? maximizeGlyph : new CaptionGlyph(symbol), Classes = { "windowCaption" } };
            if (symbol == "close") button.Classes.Add("windowClose");
            AutomationProperties.SetAutomationId(button, "Window" + symbol); AutomationProperties.SetName(button, name); ToolTip.SetTip(button, name);
            button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        header.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || e.Source is not Visual source) return;
            if (source is Button or TextBox || source.GetVisualAncestors().Any(ancestor => ancestor is Button or TextBox or MenuBase)) return;
            if (e.ClickCount == 2) Toggle(window); else window.BeginMoveDrag(e);
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        void Update()
        {
            var maximized = window.WindowState == WindowState.Maximized;
            frame.CornerRadius = new(maximized ? 0 : 10); frame.BorderThickness = new(maximized ? 0 : 1);
            maximizeGlyph.Restore = maximized; maximizeGlyph.InvalidateVisual();
        }
        window.PropertyChanged += (_, e) => { if (e.Property == Window.WindowStateProperty) Update(); };
        Update();
    }
    private static void Toggle(Window window) => window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private sealed class CaptionGlyph(string symbol) : Control
    {
        public bool Restore { get; set; }
        public CaptionGlyph() : this("close") { }
        public override void Render(DrawingContext context)
        {
            base.Render(context); var pen = new Pen(GetValue(TextBlock.ForegroundProperty) ?? Ui.Ink, 1);
            var x = Bounds.Width / 2; var y = Bounds.Height / 2;
            if (symbol == "minimize") context.DrawLine(pen, new(x - 5, y + 2), new(x + 5, y + 2));
            else if (symbol == "close") { context.DrawLine(pen, new(x - 4, y - 4), new(x + 4, y + 4)); context.DrawLine(pen, new(x + 4, y - 4), new(x - 4, y + 4)); }
            else if (Restore)
            {
                context.DrawRectangle(null, pen, new Rect(x - 5, y - 2, 8, 8));
                context.DrawLine(pen, new(x - 2, y - 5), new(x + 6, y - 5)); context.DrawLine(pen, new(x + 6, y - 5), new(x + 6, y + 3));
            }
            else context.DrawRectangle(null, pen, new Rect(x - 4.5, y - 4.5, 9, 9));
        }
        protected override Size MeasureOverride(Size availableSize) => new(16, 16);
    }
}
