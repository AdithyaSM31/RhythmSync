using System.Net;
using RhythmSync.Core;
using RhythmSync.Core.Audio;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Core.Net;
using Xunit.Abstractions;

namespace RhythmSync.Tests.Integration;

/// <summary>Real sockets, real threads, real time: one host and one client over 127.0.0.1.</summary>
public class LocalhostSmokeTests
{
    private const uint SessionId = 0x5E55_0011;
    private const int Rate = AudioFormat.SampleRate;
    private const int ChunkFrames = 480;                                              // 10 ms
    private const long ChunkTicks = AudioFormat.TicksPerSecond * ChunkFrames / Rate;
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly ITestOutputHelper _log;

    public LocalhostSmokeTests(ITestOutputHelper log) => _log = log;

    [Fact, Trait("Category", "Smoke")]
    public void Localhost_session_plays_in_sync_for_30_s()
    {
        var clock = new StopwatchClock();
        var loopback = new IPEndPoint(IPAddress.Loopback, 0);

        // Host: audio channel (fan-out) and clock channel (ping server) on ephemeral ports.
        var hostAudio = new UdpDatagramChannel(loopback);
        var hostClock = new UdpDatagramChannel(loopback);
        using var host = new HostPipeline(SessionId, hostAudio, hostClock, clock, Rate, targetLatencyMs: 200);

        // Client: audio channel feeding the pipeline, clock channel pinging the host.
        var estimator = new ClockEstimator();
        var pipeline = new ClientPipeline(SessionId, estimator, Rate);
        using var clientAudio = new UdpDatagramChannel(loopback);
        clientAudio.Start((data, _) => pipeline.HandleAudioDatagram(data));
        host.Streamer.AddClient(Guid.NewGuid(), clientAudio.LocalEndPoint);
        using var sync = new ClockSyncClient(new UdpDatagramChannel(loopback), clock, hostClock.LocalEndPoint, SessionId, estimator);
        sync.Start();

        // One loop plays the part of both the capture device and the render device. Each is driven by chunk index so
        // that its timestamps are exact, however late a Sleep returns: chunk k covers device time start + k · 10 ms.
        Exception? failure = null;
        using var stop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                RunDevices(clock, host, pipeline, sync, stop.Token);
            }
            catch (Exception e)
            {
                failure = e;
            }
        }) { IsBackground = true, Name = "smoke devices" };

        thread.Start();
        Thread.Sleep(Duration);
        stop.Cancel();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "device loop did not stop");
        Assert.Null(failure);

        var state = SyncStateEvaluator.Evaluate(estimator, clock.NowTicks);
        var stats = pipeline.Scheduler.GetStats();
        long received = pipeline.Buffer.Received, late = pipeline.Buffer.Late;
        string report = $"state {state}, received {received}, late {late}, duplicates {pipeline.Buffer.Duplicates}, " +
                        $"rejected {pipeline.Buffer.Rejected}, resyncs {stats.Resyncs}, concealed {stats.ConcealedFrames}, " +
                        $"sync error {stats.SyncErrorMs:F3} ms, ratio {stats.RatioPpm:F1} ppm, pings {sync.PingsSent}, " +
                        $"clock std error {estimator.StdErrorMs:F3} ms, drift {estimator.DriftPpm:F2} ppm, " +
                        $"playback {pipeline.Scheduler.State}";
        _log.WriteLine(report);

        Assert.True(received > 5000, $"only {received} frames arrived in 30 s\n{report}");
        Assert.Equal(SyncState.InSync, state);
        Assert.True((double)late / received < 0.01, report);
        Assert.True(Math.Abs(stats.SyncErrorMs) < 2, report);
        Assert.Equal(PlaybackState.Playing, pipeline.Scheduler.State);
    }

    private static void RunDevices(ISessionClock clock, HostPipeline host, ClientPipeline pipeline, ClockSyncClient sync, CancellationToken stop)
    {
        var capture = new float[ChunkFrames * AudioFormat.Channels];
        var render = new float[ChunkFrames * AudioFormat.Channels];
        long start = clock.NowTicks;
        long captureChunks = 0, renderChunks = 0;

        while (!stop.IsCancellationRequested)
        {
            long now = clock.NowTicks;

            // Capture: every chunk whose last frame has been captured by now (a 440 Hz tone, phase-continuous).
            while (start + (captureChunks + 1) * ChunkTicks <= now)
            {
                long firstFrame = captureChunks * ChunkFrames;
                for (int i = 0; i < ChunkFrames; i++)
                {
                    float v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * ((firstFrame + i) % Rate) / Rate));
                    capture[2 * i] = v;
                    capture[2 * i + 1] = v;
                }
                host.OnCaptured(capture, firstFrame, start + captureChunks * ChunkTicks);
                captureChunks++;
            }

            // Render: the device asks for the next 480 frames once its clock has reached that chunk's start.
            while (start + renderChunks * ChunkTicks <= now)
            {
                long devicePosition = renderChunks * ChunkFrames;
                var timing = new RenderTiming(devicePosition, start + renderChunks * ChunkTicks, devicePosition + ChunkFrames);
                pipeline.Scheduler.Render(render, timing);
                renderChunks++;
            }

            sync.Tick();
            Thread.Sleep(10);
        }
    }
}
