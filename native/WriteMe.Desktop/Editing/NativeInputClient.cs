using Avalonia;
using Avalonia.Input.TextInput;
using AvaloniaEdit;

namespace WriteMe.Desktop.Editing;

// Preedit stays outside the saved tree. Enter/Tab must not execute block commands while composing.
public sealed class NativeInputClient : TextInputMethodClient
{
    private readonly TextEditor _editor;
    public string Preedit { get; private set; } = "";
    public bool IsComposing => Preedit.Length > 0;
    public event EventHandler? PreeditChanged;

    public NativeInputClient(TextEditor editor)
    {
        _editor = editor;
        editor.TextArea.Caret.PositionChanged += (_, _) => Refresh();
        editor.TextArea.SelectionChanged += (_, _) => Refresh();
        editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => RaiseCursorRectangleChanged();
        editor.TextArea.TextView.VisualLinesChanged += (_, _) => RaiseCursorRectangleChanged();
    }

    public override Visual TextViewVisual => _editor.TextArea.TextView;
    public override bool SupportsPreedit => true;
    public override bool SupportsSurroundingText => true;
    private int SurroundingStart => _editor.Document.GetLineByOffset(_editor.SelectionStart).Offset;
    private int SurroundingEnd => _editor.Document.GetLineByOffset(_editor.SelectionStart + _editor.SelectionLength).EndOffset;
    public override string SurroundingText => _editor.Document.GetText(SurroundingStart, SurroundingEnd - SurroundingStart);
    public override Rect CursorRectangle => BlockCaretGeometry.GetRectangle(_editor);
    public override TextSelection Selection
    {
        get => new(_editor.SelectionStart - SurroundingStart, _editor.SelectionStart + _editor.SelectionLength - SurroundingStart);
        set
        {
            var start = SurroundingStart;
            var length = SurroundingEnd - start;
            var a = Math.Clamp(value.Start, 0, length);
            var b = Math.Clamp(value.End, 0, length);
            _editor.Select(start + Math.Min(a, b), Math.Abs(a - b));
            _editor.CaretOffset = start + b;
        }
    }
    public override void SetPreeditText(string? preeditText)
    {
        Preedit = preeditText ?? "";
        PreeditChanged?.Invoke(this, EventArgs.Empty);
        RaiseCursorRectangleChanged();
    }
    public void Cancel() { SetPreeditText(null); RequestReset(); }
    public void Refresh()
    {
        RaiseCursorRectangleChanged();
        RaiseSurroundingTextChanged();
        RaiseSelectionChanged();
    }
    public override void ExecuteContextMenuAction(ContextMenuAction action)
    {
        switch (action)
        {
            case ContextMenuAction.Copy: _editor.Copy(); break;
            case ContextMenuAction.Cut: _editor.Cut(); break;
            case ContextMenuAction.Paste: _editor.Paste(); break;
            case ContextMenuAction.SelectAll: _editor.SelectAll(); break;
        }
    }
}
