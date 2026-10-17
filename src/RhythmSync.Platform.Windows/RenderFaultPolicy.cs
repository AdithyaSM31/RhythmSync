namespace RhythmSync.Platform.Windows;

/// <summary>
/// Decides how the render loop reacts to WASAPI exceptions and guarantees <c>DeviceInvalidated</c> is claimed at most once.
/// </summary>
internal sealed class RenderFaultPolicy
{
    public const int MaxConsecutiveErrors = 20;

    private int _consecutive;       // render thread only
    private int _claimed;

    /// <summary>AUDCLNT_E_DEVICE_INVALIDATED, _RESOURCES_INVALIDATED, _SERVICE_NOT_RUNNING, HRESULT_FROM_WIN32(ERROR_NOT_FOUND).</summary>
    public static bool IsDeviceGone(int hr) =>
        hr == unchecked((int)0x88890004) || hr == unchecked((int)0x88890026) ||
        hr == unchecked((int)0x88890010) || hr == unchecked((int)0x80070490);

    /// <summary>A period completed; resets the consecutive-error count.</summary>
    public void Succeeded() => _consecutive = 0;

    /// <summary>
    /// A WASAPI call threw. Returns true when the loop must stop and report the device as invalidated: the exception is a
    /// device-gone HRESULT, or this is the <see cref="MaxConsecutiveErrors"/>th failure in a row.
    /// </summary>
    public bool ShouldInvalidate(Exception ex)
    {
        if (ex is System.Runtime.InteropServices.COMException com && IsDeviceGone(com.HResult)) return true;
        return ++_consecutive >= MaxConsecutiveErrors;
    }

    /// <summary>True for exactly one caller, ever.</summary>
    public bool TryClaimInvalidation() => Interlocked.Exchange(ref _claimed, 1) == 0;
}
