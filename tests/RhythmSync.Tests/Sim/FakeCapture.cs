using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;

namespace RhythmSync.Tests.Sim;

/// <summary>
/// Simulated loopback capture device. Its stream position advances at <c>rate</c> frames per second of
/// <c>deviceClock</c> (position 0 at <see cref="Start"/>). Every 10 ms of device time it delivers the chunk just
/// captured, a 440 Hz sine at amplitude 0.5 on both channels, with the position of the chunk's first frame and the
/// host session-clock time at which that frame was captured, plus uniform ±1 ms jitter (as a real driver's QPC stamp).
/// </summary>
public sealed class FakeCapture
{
    private const double ToneHz = 440;
    private const double Amplitude = 0.5;
    private const double JitterTicks = AudioFormat.TicksPerSecond / 1000.0;

    private readonly SimScheduler _sim;
    private readonly DriftingClock _deviceClock;
    private readonly ISessionClock _hostClock;
    private readonly int _rate;
    private readonly CaptureHandler _sink;
    private readonly int _periodFrames;
    private readonly float[] _buffer;
    private readonly Random _jitter;
    private long _startDeviceTicks;
    private long _position;
    private int _generation;

    public FakeCapture(SimScheduler s, DriftingClock deviceClock, ISessionClock hostClock, int rate, CaptureHandler sink, int seed = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rate, 100);
        _sim = s ?? throw new ArgumentNullException(nameof(s));
        _deviceClock = deviceClock ?? throw new ArgumentNullException(nameof(deviceClock));
        _hostClock = hostClock ?? throw new ArgumentNullException(nameof(hostClock));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _rate = rate;
        _periodFrames = rate / 100;
        _buffer = new float[_periodFrames * AudioFormat.Channels];
        _jitter = new Random(seed);
    }

    /// <summary>Device position of the next frame to be delivered (frames delivered so far).</summary>
    public long Position => _position;

    /// <summary>
    /// Ground truth: the (fractional) device position captured at true time <paramref name="trueTicks"/>, from this
    /// device's clock and its position origin at <see cref="Start"/>.
    /// </summary>
    public double DevicePositionAt(long trueTicks) =>
        (_deviceClock.ToClock(trueTicks) - _startDeviceTicks) * (double)_rate / AudioFormat.TicksPerSecond;

    /// <summary>Starts capturing now at device position 0; the first chunk is delivered 10 ms of device time later.</summary>
    public void Start()
    {
        _startDeviceTicks = _deviceClock.NowTicks;
        _position = 0;
        int generation = ++_generation;
        _sim.Schedule(TrueTimeOf(_periodFrames), () => Deliver(generation));
    }

    public void Stop() => _generation++;

    /// <summary>True time at which the device captures frame <paramref name="position"/>.</summary>
    private long TrueTimeOf(long position) =>
        _deviceClock.ToTrue(_startDeviceTicks + (long)Math.Round(position * (double)AudioFormat.TicksPerSecond / _rate));

    private void Deliver(int generation)
    {
        if (generation != _generation) return;

        for (int i = 0; i < _periodFrames; i++)
        {
            long frame = _position + i;
            float v = (float)(Amplitude * Math.Sin(2 * Math.PI * ToneHz * (frame % _rate) / _rate));
            _buffer[2 * i] = v;
            _buffer[2 * i + 1] = v;
        }

        // Host clock reading when the chunk's first frame was captured (the host clock barely drifts over 10 ms).
        long age = _sim.Now - TrueTimeOf(_position);
        long jitter = (long)Math.Round((2 * _jitter.NextDouble() - 1) * JitterTicks);
        _sink(_buffer, _position, _hostClock.NowTicks - age + jitter);

        _position += _periodFrames;
        _sim.Schedule(TrueTimeOf(_position + _periodFrames), () => Deliver(generation));
    }
}
