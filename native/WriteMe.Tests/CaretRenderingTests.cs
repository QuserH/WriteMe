using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using WriteMe.Core;
using WriteMe.Desktop.Editing;
using Xunit;

namespace WriteMe.Tests;

public sealed class CaretRenderingTests
{
    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); using var first = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs(); using var settled = window.CaptureRenderedFrame();
    }

    private static PixelRect CaretPixels(BlockEditor editor)
    {
        var view = editor.Surface.TextArea.TextView;
        var layer = view.Layers.Single(control => control.GetType().FullName == "AvaloniaEdit.Editing.CaretLayer");
        var size = new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height));
        using var image = new RenderTargetBitmap(size);
        image.Render(layer);
        using var pixels = new WriteableBitmap(size, new(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = pixels.Lock();
        image.CopyPixels(buffer, AlphaFormat.Premul);
        var bytes = new byte[buffer.RowBytes * size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var left = size.Width; var top = size.Height; var right = -1; var bottom = -1;
        for (var y = 0; y < size.Height; y++)
            for (var x = 0; x < size.Width; x++)
                if (bytes[y * buffer.RowBytes + x * 4 + 3] > 64)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        Assert.True(right >= left && bottom >= top, "The focused native caret must be visible.");
        return new(left, top, right - left + 1, bottom - top + 1);
    }

    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WRITEME_QA_ARTIFACTS") is not { Length: > 0 } directory) return;
        Directory.CreateDirectory(directory); using var image = window.CaptureRenderedFrame(); image?.Save(Path.Combine(directory, name));
    }

    [AvaloniaTheory]
    [InlineData("paragraph", 14, 25)]
    [InlineData("heading", 26, 42)]
    [InlineData("codeBlock", 14, 25)]
    [InlineData("empty", 14, 25)]
    public void NativeCaretIsThinAndFitsTheTextInsteadOfBlockSpacing(string kind, int minimum, int maximum)
    {
        var node = NoteNode.Paragraph(kind == "empty" ? "" : "正在输入中文，记录一个想法。");
        if (kind is "heading" or "codeBlock") node = node.WithAttr("level", 1) with { Type = kind };
        var editor = new BlockEditor(new(new("doc") { Content = [node, NoteNode.Paragraph("下一段内容")] }));
        var window = new Window { Width = 640, Height = 230, Content = new Grid { Margin = new(30), Children = { editor } } };
        window.Show(); editor.FocusText(); Frame(window);
        try
        {
            double? textHeight = null;
            foreach (var factor in new[] { 1.2, 2.4 })
            {
                editor.Surface.Options.LineHeightFactor = factor; Frame(window);
                editor.Surface.CaretOffset = kind == "empty" ? 0 : 2;
                editor.Surface.TextArea.Caret.Show(); Frame(window);
                var pixels = CaretPixels(editor);
                Assert.InRange(pixels.Width, 1, 2); Assert.InRange(pixels.Height, minimum, maximum);
                var anchor = editor.InputClient.CursorRectangle;
                Assert.InRange(Math.Abs(anchor.Y - pixels.Y), 0, 1);
                Assert.InRange(Math.Abs(anchor.Height - pixels.Height), 0, 1);
                if (textHeight != null) Assert.InRange(Math.Abs(textHeight.Value - anchor.Height), 0, 1);
                textHeight = anchor.Height;
                if (factor == 1.2) Save(window, "caret-" + kind + ".png");
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void WrappedCaretAndImeAnchorFollowTheSameTextLineAndKeepTheDocumentUntouched()
    {
        var text = string.Concat(Enumerable.Repeat("中文输入与自动换行要跟随文字。", 8));
        var editor = new BlockEditor(new(new("doc") { Content = [NoteNode.Toggle(text)] }));
        var window = new Window { Width = 420, Height = 360, Content = editor };
        window.Show(); editor.FocusText(); Frame(window);
        try
        {
            var view = editor.Surface.TextArea.TextView;
            var line = view.VisualLines[0]; var continuation = line.TextLines[1];
            var offset = line.GetRelativeOffset(continuation.FirstTextSourceIndex);
            editor.Surface.CaretOffset = offset; editor.Surface.TextArea.Caret.Show(); Frame(window);
            var anchor = editor.InputClient.CursorRectangle; var pixels = CaretPixels(editor);
            Assert.Equal(76, anchor.X, 2);
            Assert.InRange(Math.Abs(pixels.Y - anchor.Y), 0, 1);
            Assert.True(anchor.Y >= line.GetTextLineVisualYPosition(continuation, VisualYPosition.LineTop));
            var before = NoteJson.Serialize(editor.Session.Root);
            editor.InputClient.SetPreeditText("zhong"); Frame(window);
            Assert.Equal(anchor, editor.InputClient.CursorRectangle); Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
            var preedit = editor.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Text == "zhong");
            Assert.InRange(Math.Abs(preedit.TranslatePoint(default, view)!.Value.Y - anchor.Y), 0, 1);
            Save(window, "caret-wrapped-ime.png");
            editor.Surface.FontSize = 20; Frame(window);
            Assert.Equal(20, preedit.FontSize);
            Assert.InRange(Math.Abs(preedit.TranslatePoint(default, view)!.Value.Y - editor.InputClient.CursorRectangle.Y), 0, 1);
            editor.InputClient.Cancel(); window.KeyTextInput("中"); Frame(window);
            Assert.Equal(text.Insert(offset, "中"), editor.Session.Projection.Rows[0].Text);
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.None, "");
            window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.None, ""); Frame(window);
            Assert.Equal(before, NoteJson.Serialize(editor.Session.Root));
        }
        finally { window.Close(); }
    }
}
