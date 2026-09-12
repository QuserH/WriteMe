using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using WriteMe.Core;

namespace WriteMe.Desktop;

// Note: 原生头像按认证账号加载，缓存随共享窗口释放，头像不进入文档 CRDT — 见 .agents/notes/implemented/architecture/2026-09-12-shared-workspaces-and-realtime.md
internal sealed class SharedAvatars(SharedApiClient api, CancellationToken cancellation) : IDisposable
{
    private readonly Dictionary<string, Task<Bitmap?>> _images = [];
    private bool _disposed;
    public Control Create(string account, string name, SharedAvatar? avatar, double size = 30)
    {
        var ink = Color.TryParse(avatar?.Color, out var color) ? color : Color.Parse("#6C82AD");
        var label = string.IsNullOrEmpty(avatar?.Text) ? System.Globalization.StringInfo.GetNextTextElement(string.IsNullOrEmpty(name) ? "W" : name) : avatar.Text;
        var border = new Border { Width = size, Height = size, CornerRadius = new(size / 2), Clip = new EllipseGeometry(new Rect(0, 0, size, size)),
            Background = new SolidColorBrush(ink, .11), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = label, FontSize = size * .37, Foreground = new SolidColorBrush(ink), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        if (avatar?.ImageVersion is { } version && NoteStore.IsAssetId(version) && SyncProtocol.ValidId(account))
        {
            var key = account + ":" + version;
            if (!_images.TryGetValue(key, out var task)) _images[key] = task = Load(account, version);
            _ = Show();
            async Task Show() { var image = await task; if (!_disposed && image != null) border.Child = new Image { Source = image, Stretch = Stretch.UniformToFill }; }
        }
        return border;
    }
    private async Task<Bitmap?> Load(string account, string version)
    {
        try
        {
            var bytes = await api.Avatar(account, version, cancellation); if (_disposed) return null;
            using var stream = new MemoryStream(bytes); var bitmap = new Bitmap(stream);
            if (_disposed) { bitmap.Dispose(); return null; } return bitmap;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or ArgumentException or InvalidOperationException or OperationCanceledException) { return null; }
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var image in _images.Values.Where(task => task.IsCompletedSuccessfully)) image.Result?.Dispose();
        _images.Clear();
    }
}

internal sealed class AvatarCrop : Control
{
    private Bitmap? _picture;
    public Bitmap? Picture { get => _picture; set { _picture = value; _offset = default; _drag = null; InvalidateVisual(); } }
    private double _zoom = 1;
    public double Zoom { get => _zoom; set { _zoom = Math.Clamp(value, 1, 3); InvalidateVisual(); } }
    private Vector _offset;
    private (Point Point, Vector Offset)? _drag;
    public AvatarCrop()
    {
        Width = Height = 100; Cursor = new(StandardCursorType.SizeAll); Clip = new EllipseGeometry(new Rect(0, 0, 100, 100));
        PointerPressed += (_, e) => { if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; _drag = (e.GetPosition(this), _offset); e.Pointer.Capture(this); e.Handled = true; };
        PointerMoved += (_, e) =>
        {
            if (_drag is not { } drag || Picture == null) return; var shift = e.GetPosition(this) - drag.Point;
            var scale = 100 / Math.Min(Picture.Size.Width, Picture.Size.Height) * Zoom;
            _offset = new(Math.Clamp(drag.Offset.X + shift.X / Math.Max(1, (Picture.Size.Width * scale - 100) / 2), -1, 1),
                Math.Clamp(drag.Offset.Y + shift.Y / Math.Max(1, (Picture.Size.Height * scale - 100) / 2), -1, 1)); InvalidateVisual();
        };
        PointerReleased += (_, e) => { _drag = null; e.Pointer.Capture(null); };
        PointerCaptureLost += (_, _) => _drag = null;
    }
    private Rect Source()
    {
        var size = Picture!.Size; var side = Math.Min(size.Width, size.Height) / Zoom;
        return new((size.Width - side) * (1 - _offset.X) / 2, (size.Height - side) * (1 - _offset.Y) / 2, side, side);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Picture != null) context.DrawImage(Picture, Source(), new Rect(Bounds.Size));
    }
    public string Export()
    {
        if (Picture == null) throw new InvalidOperationException("请先选择头像图片");
        using var image = new RenderTargetBitmap(new PixelSize(128, 128), new Vector(96, 96));
        using (var context = image.CreateDrawingContext()) context.DrawImage(Picture, Source(), new Rect(0, 0, 128, 128));
        using var bytes = new MemoryStream(); image.Save(bytes); return "data:image/png;base64," + Convert.ToBase64String(bytes.ToArray());
    }
}
