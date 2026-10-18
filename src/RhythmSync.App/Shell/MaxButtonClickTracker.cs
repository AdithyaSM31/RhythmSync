namespace RhythmSync.App.Shell;

/// <summary>
/// Decides whether a non-client button-up over the Maximize button is a click. Pure: the window procedure in
/// <see cref="WindowChromeHelper"/> feeds it the messages. A click needs a press on the button first, so a release
/// after a press that started elsewhere (an orphan button-up) does nothing, and so does a release after the pointer
/// left the button. The double-click message is not a press: Windows sends down, up, double-click, up for a
/// double-click, and that toggles the window once.
/// </summary>
public sealed class MaxButtonClickTracker
{
    private bool _pressed;

    /// <summary><c>WM_NCLBUTTONDOWN</c> over the button.</summary>
    public void OnDown() => _pressed = true;

    /// <summary><c>WM_NCLBUTTONDBLCLK</c> over the button: ignored, so its button-up is not a second click.</summary>
    public void OnDoubleClick() => _pressed = false;

    /// <summary>The pointer left the button: <c>WM_NCMOUSELEAVE</c>, or a <c>WM_NCHITTEST</c> that misses it.</summary>
    public void OnLeave() => _pressed = false;

    /// <summary><c>WM_NCLBUTTONUP</c> over the button. Returns whether it completes a click.</summary>
    public bool OnUp()
    {
        bool click = _pressed;
        _pressed = false;
        return click;
    }
}
