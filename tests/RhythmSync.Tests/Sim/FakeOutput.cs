using RhythmSync.Core;
using RhythmSync.Core.Audio;

namespace RhythmSync.Tests.Sim;

/// <summary>
/// Simulated output device. It consumes <c>sampleRate·(1 + deviceDriftPpm·1e-6)</c> frames per true second,
/// starts with one period of silence queued (write position = device position + period) and calls the render
/// callback once per period. Each callback reports the device position with the client clock's reading at that
/// moment. A sample at write index <c>w</c> is heard at callback true time + <c>(w − p) / (rate·(1 + drift))</c>.
/// </summary>
public sealed class FakeOutput : IAudioOutput
{
    private readonly SimScheduler _sim;
    private readonly DriftingClock _clientClock;
    private readonly double _trueTicksPerFrame;
    private readonly int _period;
    private readonly float[] _buffer;
    private IRenderCallback? _callback;
    private long _startTicks;
    private long _callbackIndex;
    private int _generation;

    public FakeOutput(SimScheduler s, DriftingClock clientClock, int sampleRate, double deviceDriftPpm, int periodFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodFrames, 1);
        _sim = s ?? throw new ArgumentNullException(nameof(s));
        _clientClock = clientClock ?? throw new ArgumentNullException(nameof(clientClock));
        SampleRate = sampleRate;
        _period = periodFrames;
        _trueTicksPerFrame = AudioFormat.TicksPerSecond / (sampleRate * (1 + deviceDriftPpm * 1e-6));
        _buffer = new float[periodFrames * AudioFormat.Channels];
    }

    public int SampleRate { get; }

    /// <summary>One entry per callback: true time the buffer's first sample is heard, and the probe value read after Render.</summary>
    public List<(long TrueTicksHeard, double StreamPos)> Heard { get; } = new();

    /// <summary>Every sample sent to the device (interleaved stereo), including the initial period of silence.</summary>
    public List<float> Samples { get; } = new();

    /// <summary>Runs at the start of each callback, before Render (e.g. to deliver packets).</summary>
    public Action? BeforeEachCallback { get; set; }

    /// <summary>Read right after each Render returns and recorded as <c>StreamPos</c> (NaN when null).</summary>
    public Func<double>? StreamPositionProbe { get; set; }

    public event Action? DeviceInvalidated;

    public void Start(IRenderCallback cb)
    {
        _callback = cb ?? throw new ArgumentNullException(nameof(cb));
        _startTicks = _sim.Now;
        _callbackIndex = 0;
        int generation = ++_generation;
        for (int i = 0; i < _period * AudioFormat.Channels; i++) Samples.Add(0f);
        _sim.Schedule(_startTicks, () => Callback(generation));
    }

    public void Stop()
    {
        _generation++;
        _callback = null;
    }

    /// <summary>Simulates the device disappearing.</summary>
    public void RaiseDeviceInvalidated() => DeviceInvalidated?.Invoke();

    public void Dispose() => Stop();

    private long CallbackTime(long index) => _startTicks + (long)Math.Round(index * _period * _trueTicksPerFrame);

    private void Callback(int generation)
    {
        if (generation != _generation || _callback is null) return;

        long devicePosition = _callbackIndex * _period;
        long writePosition = devicePosition + _period;
        var timing = new RenderTiming(devicePosition, _clientClock.NowTicks, writePosition);

        BeforeEachCallback?.Invoke();
        Array.Clear(_buffer);
        _callback.Render(_buffer, timing);
        double probe = StreamPositionProbe?.Invoke() ?? double.NaN;

        long heard = _sim.Now + (long)Math.Round((writePosition - devicePosition) * _trueTicksPerFrame);
        Heard.Add((heard, probe));
        Samples.AddRange(_buffer);

        _callbackIndex++;
        _sim.Schedule(CallbackTime(_callbackIndex), () => Callback(generation));
    }
}
