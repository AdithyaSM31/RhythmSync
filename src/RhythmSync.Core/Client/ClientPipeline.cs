using RhythmSync.Core.Audio;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Core.Client;

/// <summary>
/// Client media path for one host session: audio datagrams (or frames handed over directly, for the host's own
/// speaker) → <see cref="JitterBuffer"/> → <see cref="PlaybackScheduler"/> → <see cref="DspChain"/> holding a
/// <see cref="GainProcessor"/>. The session id is fixed: a new host session needs a new pipeline (the scheduler and
/// buffer cannot be reset). <see cref="HandleAudioDatagram"/> is called from one network thread and does not allocate.
/// </summary>
public sealed class ClientPipeline : IFrameSink
{
    /// <summary>Largest target latency; clamping against it leaves an already-effective offset unchanged.</summary>
    private const int MaxTargetLatencyMs = 500;

    private readonly uint _sessionId;
    private readonly IClockMapping _clock;
    private readonly short[] _pcm = new short[AudioFormat.FrameFloats];
    private volatile PlaybackScheduler _scheduler;

    public ClientPipeline(uint sessionId, IClockMapping clock, int deviceSampleRate)
    {
        _sessionId = sessionId;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        Buffer = new JitterBuffer();
        Gain = new GainProcessor();
        Dsp = new DspChain();
        Dsp.Add(Gain);
        _scheduler = new PlaybackScheduler(Buffer, clock, deviceSampleRate, Dsp);
    }

    public JitterBuffer Buffer { get; }
    public DspChain Dsp { get; }
    public GainProcessor Gain { get; }

    /// <summary>The render callback for the current output device; replaced by <see cref="ReplaceOutputRate"/>.</summary>
    public PlaybackScheduler Scheduler => _scheduler;

    /// <summary>
    /// Inserts an audio datagram into the buffer. Anything that is not a well-formed audio packet of this session
    /// (garbage, other packet types, wrong length, stale/foreign session) is dropped before touching the buffer, so it
    /// can neither anchor the buffer nor be counted in its statistics.
    /// </summary>
    public void HandleAudioDatagram(ReadOnlySpan<byte> datagram)
    {
        if (!PacketSerializer.TryReadHeader(datagram, out var type, out uint sessionId)
            || type != PacketType.Audio
            || sessionId != _sessionId
            || !PacketSerializer.TryReadAudio(datagram, out uint seq, out long playAt, _pcm))
            return;

        Buffer.Insert(seq, playAt, _pcm);
    }

    /// <summary>Local delivery (host's own speaker via <see cref="Streamer.LocalSink"/>).</summary>
    public void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm) => Buffer.Insert(seq, playAt, pcm);

    /// <summary>
    /// Creates a scheduler for a new output device rate on the same buffer, DSP chain and gain, keeping the current
    /// offset. Call while no output is rendering with the old scheduler, then start the new output with <see cref="Scheduler"/>.
    /// </summary>
    public void ReplaceOutputRate(int deviceSampleRate)
    {
        var next = new PlaybackScheduler(Buffer, _clock, deviceSampleRate, Dsp);
        next.ApplyLatency(_scheduler.EffectiveOffsetMs, MaxTargetLatencyMs);
        _scheduler = next;
    }
}
