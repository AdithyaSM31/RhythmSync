using RhythmSync.Core.Clock;
using RhythmSync.Core.Net;

namespace RhythmSync.Core.Host;

/// <summary>
/// Host media path: captured audio → <see cref="CaptureTimestamper"/> + <see cref="FrameAssembler"/> →
/// <see cref="Streamer"/> (UDP fan-out on the audio channel), plus a started <see cref="ClockSyncServer"/> on the
/// clock channel. Owns both channels and disposes them.
/// </summary>
public sealed class HostPipeline : IDisposable
{
    private readonly IDatagramChannel _audio;
    private readonly ClockSyncServer _clockServer;
    private readonly CaptureTimestamper _timestamper;
    private bool _disposed;

    public HostPipeline(uint sessionId, IDatagramChannel audio, IDatagramChannel clock, ISessionClock sessionClock,
                        int captureSampleRate, int targetLatencyMs)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sessionClock);

        _audio = audio;
        SessionId = sessionId;
        _timestamper = new CaptureTimestamper(captureSampleRate);
        Streamer = new Streamer(audio, sessionId);
        Assembler = new FrameAssembler(captureSampleRate, _timestamper, Streamer, targetLatencyMs);
        _clockServer = new ClockSyncServer(clock, sessionClock, sessionId);
        _clockServer.Start();
    }

    /// <summary>The session id stamped on every audio packet and answered by the clock server.</summary>
    public uint SessionId { get; }

    public Streamer Streamer { get; }

    public FrameAssembler Assembler { get; }

    /// <summary>
    /// Capture callback (<see cref="Audio.CaptureHandler"/>-compatible): records that device frame
    /// <paramref name="devicePositionFrames"/> was captured at <paramref name="qpcTicks"/>, then assembles and sends frames.
    /// </summary>
    public void OnCaptured(ReadOnlySpan<float> stereo, long devicePositionFrames, long qpcTicks)
    {
        _timestamper.AddObservation(devicePositionFrames, qpcTicks);
        Assembler.Push(stereo, devicePositionFrames);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _clockServer.Dispose();
        _audio.Dispose();
    }
}
