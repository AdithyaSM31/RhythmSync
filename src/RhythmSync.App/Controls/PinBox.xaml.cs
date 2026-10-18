using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace RhythmSync.App.Controls;

/// <summary>
/// Four single-digit boxes bound two-way to <see cref="Code"/>. All behaviour comes from <see cref="CodeEntry"/>;
/// the event handlers only translate WPF input into its calls and refresh the boxes afterwards.
/// </summary>
public partial class PinBox : UserControl
{
    public static readonly DependencyProperty CodeProperty = DependencyProperty.Register(
        nameof(Code), typeof(string), typeof(PinBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((PinBox)d).OnCodeChanged((string?)e.NewValue),
            (_, value) => value ?? string.Empty)
        {
            DefaultUpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });

    public static readonly RoutedEvent SubmittedEvent = EventManager.RegisterRoutedEvent(
        nameof(Submitted), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(PinBox));

    private readonly CodeEntry _entry = new();
    private readonly TextBox[] _boxes;
    private bool _syncing;

    public PinBox()
    {
        InitializeComponent();
        _boxes = [Box0, Box1, Box2, Box3];

        for (int i = 0; i < _boxes.Length; i++)
        {
            var index = i;
            var box = _boxes[i];
            box.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;   // a click picks the slot itself; the box must not place its own caret
                FocusSlot(index);
            };
            box.GotKeyboardFocus += (_, _) => OnBoxFocused(index);
            box.TextChanged += (_, _) => Refresh();   // undoes any edit that bypassed CodeEntry (cut, drop, IME)
            DataObject.AddPastingHandler(box, OnPasting);
        }

        PreviewTextInput += (_, e) => e.Handled = HandleText(e.Text) || e.Handled;
        PreviewKeyDown += (_, e) => e.Handled = HandleKey(e.Key) || e.Handled;
        Refresh();
    }

    /// <summary>Raised when Enter is pressed with all four digits entered.</summary>
    public event RoutedEventHandler Submitted
    {
        add => AddHandler(SubmittedEvent, value);
        remove => RemoveHandler(SubmittedEvent, value);
    }

    public string Code
    {
        get => (string)GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    /// <summary>Index of the box that takes the next keystroke.</summary>
    internal int FocusedIndex => _entry.Focus;

    /// <summary>Applies typed text. Always handled, so the boxes never edit themselves.</summary>
    internal bool HandleText(string? text)
    {
        foreach (var c in text ?? string.Empty)
            _entry.Type(c);
        Commit();
        return true;
    }

    /// <summary>Applies a key press; returns whether it was consumed.</summary>
    internal bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Back:
                _entry.Backspace();
                break;
            case Key.Left:
                _entry.MoveLeft();
                break;
            case Key.Right:
                _entry.MoveRight();
                break;
            case Key.Delete:   // not an editing key here; swallowed so a box cannot delete its own text
                return true;
            case Key.Enter when _entry.IsComplete:
                RaiseEvent(new RoutedEventArgs(SubmittedEvent, this));
                return true;
            default:
                return false;
        }

        Commit();
        return true;
    }

    internal void HandlePaste(string? text)
    {
        _entry.Paste(text);
        Commit();
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetData(DataFormats.UnicodeText) is string text)
            HandlePaste(text);
        else if (e.DataObject.GetDataPresent(DataFormats.Text) && e.DataObject.GetData(DataFormats.Text) is string ansi)
            HandlePaste(ansi);
    }

    private void OnCodeChanged(string? code)
    {
        if (_syncing)
            return;

        _entry.Set(code);
        Refresh();
        if (_entry.Code != code)
        {
            // The incoming value was filtered, so write the cleaned code back. A binding ignores a source update made
            // while it is still transferring the incoming value, hence the deferral; skip it if the code moved on since.
            Dispatcher.BeginInvoke(() =>
            {
                if (Code == code)
                    Publish();
            });
        }
    }

    /// <summary>Called after the user changed the entry: refresh the boxes, push the code to the binding, follow focus.</summary>
    private void Commit()
    {
        Refresh();
        Publish();
        MoveKeyboardFocus();
    }

    private void Publish()
    {
        _syncing = true;
        try
        {
            SetCurrentValue(CodeProperty, _entry.Code);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Refresh()
    {
        var code = _entry.Code;
        for (int i = 0; i < _boxes.Length; i++)
        {
            var digit = i < code.Length ? code[i].ToString() : string.Empty;
            if (_boxes[i].Text != digit)
                _boxes[i].Text = digit;
            _boxes[i].IsTabStop = i == _entry.Focus;   // one tab stop, on the slot that takes the next keystroke
        }
    }

    /// <summary>Moves keyboard focus to the active box, but only if focus is already inside (a binding update must not steal it).</summary>
    private void MoveKeyboardFocus()
    {
        if (IsKeyboardFocusWithin)
            _boxes[_entry.Focus].Focus();
    }

    private void FocusSlot(int index)
    {
        _entry.FocusAt(index);
        Refresh();
        _boxes[_entry.Focus].Focus();
    }

    private void OnBoxFocused(int index)
    {
        if (index == _entry.Focus)
            return;

        _entry.FocusAt(index);
        Refresh();
        if (_entry.Focus != index)   // focus landed past the first empty slot
            _boxes[_entry.Focus].Focus();
    }
}
