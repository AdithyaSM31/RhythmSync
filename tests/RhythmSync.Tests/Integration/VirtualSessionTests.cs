using System.Net;
using System.Text;
using RhythmSync.Core;
using RhythmSync.Core.Client;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Host;
using RhythmSync.Tests.Sim;
using Xunit.Abstractions;

namespace RhythmSync.Tests.Integration;

/// <summary>
/// End-to-end proof on a simulated timeline: one host (capture → frames → UDP fan-out + local speaker) and four outputs
/// (the host's own speaker and three remote clients with their own clocks, devices and rates) over a lossy, jittery
/// simulated Wi-Fi with one 2 s outage. Truth is each device's (true time heard, stream position) per callback.
/// </summary>
public class VirtualSessionTests
{
    private const long Second = AudioFormat.TicksPerSecond;
    private const long Ms = Second / 1000;
    private const uint SessionId = 0x5E55_0010;
    private const int TargetLatencyMs = 200;
    private const double MaxPairErrorSamples = 48;   // 1 ms at 48 kHz

    private static readonly long RunEnd = 600 * Second;
    private static readonly long OutageStart = 300 * Second, OutageEnd = 302 * Second;
    private static readonly long MeasureFrom = 15 * Second;
    private static readonly long ExcludeFrom = 300 * Second, ExcludeTo = 312 * Second;
    private static readonly long Grid = 10 * Ms;

    private static readonly IPEndPoint HostAudio = new(IPAddress.Parse("10.0.0.1"), 47802);
    private static readonly IPEndPoint HostClock = new(IPAddress.Parse("10.0.0.1"), 47803);

    private readonly ITestOutputHelper _log;

    public VirtualSessionTests(ITestOutputHelper log) => _log = log;

    private sealed record RemoteSpec(string Name, long ClockOffsetTicks, double ClockDriftPpm, int DeviceRate, double DeviceDriftPpm);

    /// <summary>One speaker: its clock, pipeline and device; <c>Estimator</c> is null for the host's own output.</summary>
    private sealed record Output(string Name, DriftingClock Clock, ClientPipeline Pipeline, FakeOutput Device, ClockEstimator? Estimator);

    [Fact]
    public void Four_outputs_stay_aligned_for_10_minutes_with_outage()
    {
        var sim = new SimScheduler();
        var net = new SimNetwork(sim, seed: 10, new SimLinkProfile());
        net.SetOutage(OutageStart, OutageEnd);

        // Host: session clock is true time; the capture device runs +30 ppm fast.
        var hostClock = new DriftingClock(sim, 0, 0);
        using var host = new HostPipeline(SessionId, net.Create(HostAudio), net.Create(HostClock), hostClock, 48000, TargetLatencyMs);

        var outputs = new List<Output>();

        // The host's own speaker: identity mapping, 48 kHz device at 0 ppm, frames handed over directly.
        var local = new ClientPipeline(SessionId, new IdentityClockMapping(), 48000);
        host.Streamer.LocalSink = local;
        outputs.Add(new Output("host", hostClock, local, new FakeOutput(sim, hostClock, 48000, 0, 480), null));

        RemoteSpec[] remotes =
        [
            new("A", 4 * Second, +100, 48000, +50),
            new("B", -9 * Second, -80, 44100, -100),
            new("C", 30 * 60 * Second, +40, 48000, 0),
        ];
        for (int i = 0; i < remotes.Length; i++)
        {
            var r = remotes[i];
            var address = IPAddress.Parse($"10.0.0.{i + 2}");
            var clientClock = new DriftingClock(sim, r.ClockOffsetTicks, r.ClockDriftPpm);
            var estimator = new ClockEstimator();
            var pipeline = new ClientPipeline(SessionId, estimator, r.DeviceRate);

            var audioEndPoint = new IPEndPoint(address, 47802);
            var audio = net.Create(audioEndPoint);
            audio.Start((data, _) => pipeline.HandleAudioDatagram(data));
            host.Streamer.AddClient(Guid.NewGuid(), audioEndPoint);

            var sync = new ClockSyncClient(net.Create(new IPEndPoint(address, 47803)), clientClock, HostClock, SessionId, estimator);
            sync.Start();
            Repeat(sim, 10 * Ms, sync.Tick);

            outputs.Add(new Output(r.Name, clientClock, pipeline, new FakeOutput(sim, clientClock, r.DeviceRate, r.DeviceDriftPpm, r.DeviceRate / 100), estimator));
        }

        foreach (var o in outputs)
        {
            o.Device.RecordSamples = false;   // 10 minutes of 4 devices would be ~1 GB of floats
            o.Device.StreamPositionProbe = () => o.Pipeline.Scheduler.LastRenderStreamPosition;
            o.Device.Start(o.Pipeline.Scheduler);
        }

        var capture = new FakeCapture(sim, new DriftingClock(sim, 0, +30), hostClock, 48000, host.OnCaptured);
        capture.Start();

        // Once a second: record sync-state and resync transitions for the diagnostics.
        var timeline = new StringBuilder();
        var lastState = new Dictionary<string, SyncState>();
        var lastResyncs = new Dictionary<string, long>();
        Repeat(sim, Second, () =>
        {
            foreach (var o in outputs)
            {
                if (o.Estimator is { } e)
                {
                    var state = SyncStateEvaluator.Evaluate(e, o.Clock.NowTicks);
                    if (!lastState.TryGetValue(o.Name, out var prev) || prev != state)
                        timeline.AppendLine($"  {sim.Now / (double)Second,6:F0} s  {o.Name}: {state}");
                    lastState[o.Name] = state;
                }
                long resyncs = o.Pipeline.Scheduler.GetStats().Resyncs;
                if (lastResyncs.GetValueOrDefault(o.Name) != resyncs)
                    timeline.AppendLine($"  {sim.Now / (double)Second,6:F0} s  {o.Name}: resyncs → {resyncs}");
                lastResyncs[o.Name] = resyncs;
            }
        });

        sim.RunUntil(OutageStart - Second);
        var concealedBefore = outputs.ToDictionary(o => o.Name, o => o.Pipeline.Scheduler.GetStats().ConcealedFrames);
        long droppedBefore = net.Dropped;

        sim.RunUntil(ExcludeTo);
        var concealedAfter = outputs.ToDictionary(o => o.Name, o => o.Pipeline.Scheduler.GetStats().ConcealedFrames);
        long droppedAcrossOutage = net.Dropped - droppedBefore;
        var statesAt312 = outputs.Where(o => o.Estimator is not null)
            .ToDictionary(o => o.Name, o => SyncStateEvaluator.Evaluate(o.Estimator!, o.Clock.NowTicks));

        sim.RunUntil(RunEnd);

        // Diagnostics.
        var report = new StringBuilder();
        report.AppendLine($"network: sent {net.Sent}, dropped {net.Dropped} ({droppedAcrossOutage} from {OutageStart / Second - 1} s to {ExcludeTo / Second} s)");
        foreach (var o in outputs)
        {
            var s = o.Pipeline.Scheduler.GetStats();
            report.AppendLine($"{o.Name}: resyncs {s.Resyncs}, concealed {s.ConcealedFrames} (outage +{concealedAfter[o.Name] - concealedBefore[o.Name]}), " +
                              $"late {s.LateFrames}, ratio {s.RatioPpm:F1} ppm, sync error {s.SyncErrorMs:F3} ms" +
                              (o.Estimator is { } e ? $", clock drift {e.DriftPpm:F2} ppm, std error {e.StdErrorMs:F3} ms" : ""));
        }

        var series = outputs.Select(o => Resample(o.Device.Heard)).ToArray();
        var failures = new List<string>();
        for (int i = 0; i < outputs.Count; i++)
        {
            for (int j = i + 1; j < outputs.Count; j++)
            {
                var errors = PairErrors(series[i], series[j]);
                string pair = $"{outputs[i].Name}–{outputs[j].Name}";
                if (errors.Length == 0)
                {
                    failures.Add($"{pair}: no comparable points");
                    report.AppendLine($"{pair}: no comparable points");
                    continue;
                }
                double p50 = Percentile(errors, 0.50), p99 = Percentile(errors, 0.99), max = errors[^1];
                report.AppendLine($"{pair}: n {errors.Length}, |Δpos| p50 {p50 / 48:F3} ms, p99 {p99 / 48:F3} ms, max {max / 48:F3} ms");
                if (p99 > MaxPairErrorSamples) failures.Add($"{pair}: p99 {p99:F1} samples > {MaxPairErrorSamples}");
            }
        }
        report.AppendLine("timeline:").Append(timeline);
        _log.WriteLine(report.ToString());

        // The outage really dropped traffic: every remote client concealed the missing 2 s of audio.
        Assert.True(droppedAcrossOutage > 1000, $"outage dropped only {droppedAcrossOutage} datagrams\n{report}");
        foreach (var o in outputs.Where(o => o.Estimator is not null))
        {
            long concealed = concealedAfter[o.Name] - concealedBefore[o.Name];
            Assert.True(concealed >= 300, $"{o.Name} concealed only {concealed} frames across the outage\n{report}");
        }

        Assert.All(statesAt312, kv => Assert.True(kv.Value == SyncState.InSync, $"{kv.Key} is {kv.Value} at 312 s\n{report}"));
        Assert.True(failures.Count == 0, string.Join("\n", failures) + "\n" + report);
    }

    /// <summary>Runs <paramref name="action"/> now and then every <paramref name="interval"/> of true time.</summary>
    private static void Repeat(SimScheduler sim, long interval, Action action)
    {
        void Run(long at) => sim.Schedule(at, () =>
        {
            action();
            Run(at + interval);
        });
        Run(sim.Now);
    }

    /// <summary>
    /// Stream position heard at each grid time (from <see cref="MeasureFrom"/>, every <see cref="Grid"/>), linearly
    /// interpolated between callbacks; NaN where either neighbouring callback was silent or the grid is not covered.
    /// </summary>
    private static double[] Resample(List<(long TrueTicksHeard, double StreamPos)> heard)
    {
        int points = (int)((RunEnd - MeasureFrom) / Grid);
        var result = new double[points];
        int k = 0;
        for (int p = 0; p < points; p++)
        {
            long t = MeasureFrom + p * Grid;
            while (k + 1 < heard.Count && heard[k + 1].TrueTicksHeard <= t) k++;
            if (k + 1 >= heard.Count || heard[k].TrueTicksHeard > t)
            {
                result[p] = double.NaN;
                continue;
            }
            var (t0, p0) = heard[k];
            var (t1, p1) = heard[k + 1];
            result[p] = p0 + (p1 - p0) * (t - t0) / (t1 - t0);   // NaN propagates
        }
        return result;
    }

    /// <summary>Sorted |Δpos| (stream samples) over grid points outside the excluded window where both are defined.</summary>
    private static double[] PairErrors(double[] a, double[] b)
    {
        var errors = new List<double>(a.Length);
        for (int p = 0; p < a.Length; p++)
        {
            long t = MeasureFrom + p * Grid;
            if (t >= ExcludeFrom && t <= ExcludeTo) continue;
            if (double.IsNaN(a[p]) || double.IsNaN(b[p])) continue;
            errors.Add(Math.Abs(a[p] - b[p]));
        }
        errors.Sort();
        return errors.ToArray();
    }

    private static double Percentile(double[] sorted, double q) =>
        sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
}
