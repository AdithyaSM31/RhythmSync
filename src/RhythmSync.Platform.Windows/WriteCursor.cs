namespace RhythmSync.Platform.Windows;

/// <summary>
/// Tracks the device frame at which the next rendered buffer starts. In steady state this is the frames written so
/// far, which leads the device (playback) position by the endpoint latency. After an underrun the device clock
/// overtakes the counter; the cursor then re-bases to <c>devicePosition + lastGoodLead</c> rather than to the device
/// position itself, so the scheduler keeps seeing the true write lead instead of a permanent zero.
/// Single-threaded: used only from the render thread.
/// </summary>
internal sealed class WriteCursor
{
    private long _written;
    private long _lastGoodLead;

    /// <param name="written">Frames already committed to the device (the silence priming the buffer).</param>
    /// <param name="initialLead">Lead assumed until a real one has been observed.</param>
    public WriteCursor(long written, long initialLead)
    {
        _written = written;
        _lastGoodLead = initialLead;
    }

    /// <summary>Device frame at which the buffer about to be rendered starts, given the current device position.</summary>
    public long Begin(long devicePosition)
    {
        long lead = _written - devicePosition;
        if (lead >= 0)
        {
            if (lead > 0) _lastGoodLead = lead;
            return _written;
        }
        return devicePosition + _lastGoodLead;      // underrun: device ran past everything we wrote
    }

    /// <summary>Records that <paramref name="frames"/> frames starting at <paramref name="writePosition"/> were committed to the device.</summary>
    public void Commit(long writePosition, int frames) => _written = writePosition + frames;
}
