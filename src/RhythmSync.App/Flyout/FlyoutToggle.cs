namespace RhythmSync.App.Flyout;

/// <summary>
/// Whether a tray click opens or closes the flyout. Clicking the tray icon while the flyout is open first deactivates it
/// (the click lands on the taskbar), which closes it; the click itself then arrives a moment later and must not open it
/// again. So a click within <see cref="ReopenGuardMs"/> of a deactivation is ignored. Times are passed in; there is no clock here.
/// </summary>
public sealed class FlyoutToggle
{
    public const double ReopenGuardMs = 300;

    private DateTime? _lastDeactivated;

    public bool IsOpen { get; private set; }

    /// <summary>Returns true if the flyout should open now. Closes it if it is open.</summary>
    public bool OnTrayClick(DateTime now)
    {
        if (IsOpen)
        {
            IsOpen = false;
            return false;
        }

        // A clock that went back (now before the deactivation) does not count as "right after".
        if (_lastDeactivated is { } last && now >= last && (now - last).TotalMilliseconds < ReopenGuardMs)
            return false;

        IsOpen = true;
        return true;
    }

    /// <summary>The flyout lost activation (a click elsewhere) and closed at <paramref name="now"/>.</summary>
    public void OnDeactivated(DateTime now)
    {
        IsOpen = false;
        _lastDeactivated = now;
    }

    /// <summary>The flyout closed itself: Esc, Open RhythmSync, Stop hosting or Leave.</summary>
    public void OnClosedByAction() => IsOpen = false;
}
