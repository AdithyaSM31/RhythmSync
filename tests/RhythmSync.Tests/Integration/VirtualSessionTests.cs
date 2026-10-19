using System.Net;
using System.Text;
using RhythmSync.Core;
using RhythmSync.Core.Audio.Eq;
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
    private const double MaxErrorSamples = 48;       // 1 ms at 48 kHz
    private const double MinCoverage = 0.99;         // share of measured grid points that must be audible

    private static readonly long RunEnd = 600 * Second;
    private static readonly long OutageStart = 300 * Second, OutageEnd = 302 * Second;
    private static readonly long MeasureFrom = 15 * Second;
    private static readonly long ExcludeFrom = 300 * Second, ExcludeTo = 312 * Second;
    private static readonly long Grid = 10 * Ms;

    private static readonly IPEndPoint HostAudio = new(IPAddress.Parse("10.0.0.1"), 47802);
    private static readonly IPEndPoint HostClock = new(IPAddress.Parse("10.0.0.1"), 47803);

    private readonly ITestOutputHelper _log;

    public VirtualSessionTests(ITestOutputHelper log) => _log = log;

    private sealed record RemoteSpec(string Name, long ClockOffsetTicks, double ClockDriftPpm, int DeviceRate, double DeviceDriftPpm,
                                     int PeriodFrames);

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
            // Device periods differ (10.7 / 20 / 10 ms) so that a mishandled write-ahead cannot cancel between outputs.
            new("A", 4 * Second, +100, 48000, +50, 512),
            new("B", -9 * Second, -80, 44100, -100, 882),
            new("C", 30 * 60 * Second, +40, 48000, 0, 480),
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

            outputs.Add(new Output(r.Name, clientClock, pipeline, new FakeOutput(sim, clientClock, r.DeviceRate, r.DeviceDriftPpm, r.PeriodFrames), estimator));
        }

        foreach (var o in outputs)
        {
            o.Device.RecordSamples = false;   // 10 minutes of 4 devices would be ~1 GB of floats
            o.Device.StreamPositionProbe = () => o.Pipeline.Scheduler.LastRenderStreamPosition;
            o.Device.Start(o.Pipeline.Scheduler);
        }

        const int CaptureRate = 48000;
        var captureClock = new DriftingClock(sim, 0, +30);
        var capture = new FakeCapture(sim, captureClock, hostClock, CaptureRate, host.OnCaptured);
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
        int expectedPoints = Enumerable.Range(0, series[0].Length).Count(p => IsMeasured(p));
        var failures = new List<string>();

        void Check(string label, double[] errors)
        {
            if (errors.Length < MinCoverage * expectedPoints)
                failures.Add($"{label}: only {errors.Length} of {expectedPoints} grid points comparable");
            if (errors.Length == 0)
            {
                report.AppendLine($"{label}: no comparable points");
                return;
            }
            double p50 = Percentile(errors, 0.50), p99 = Percentile(errors, 0.99), max = errors[^1];
            report.AppendLine($"{label}: n {errors.Length}/{expectedPoints}, |Δpos| p50 {p50 / 48:F3} ms, p99 {p99 / 48:F3} ms, max {max / 48:F3} ms");
            if (p99 > MaxErrorSamples) failures.Add($"{label}: p99 {p99:F1} samples > {MaxErrorSamples}");
        }

        // Absolute: the stream position whose audio the capture device recorded at true time t − target latency
        // (FrameAssembler: stream sample k ↔ device position origin + k · captureRate / 48000, origin = position 0).
        double[] truth = new double[series[0].Length];
        for (int p = 0; p < truth.Length; p++)
            truth[p] = capture.DevicePositionAt(GridTime(p) - TargetLatencyMs * Ms) * AudioFormat.SampleRate / CaptureRate;
        for (int i = 0; i < outputs.Count; i++)
        {
            int silent = Enumerable.Range(0, series[i].Length).Count(p => IsMeasured(p) && double.IsNaN(series[i][p]));
            report.AppendLine($"{outputs[i].Name}: {silent} silent grid points");
            Check($"{outputs[i].Name} vs truth", Errors(series[i], truth));
        }

        // Pairwise.
        for (int i = 0; i < outputs.Count; i++)
            for (int j = i + 1; j < outputs.Count; j++)
                Check($"{outputs[i].Name}–{outputs[j].Name}", Errors(series[i], series[j]));

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

    [Fact]
    public void An_eq_on_one_remote_changes_only_its_level_and_not_its_timing()
    {
        const int Rate = 48000;
        long runEnd = 60 * Second;
        var gains = EqGains.Flat.WithBand(4, 12);

        var sim = new SimScheduler();
        var net = new SimNetwork(sim, seed: 10, new SimLinkProfile());

        var hostClock = new DriftingClock(sim, 0, 0);
        using var host = new HostPipeline(SessionId, net.Create(HostAudio), net.Create(HostClock), hostClock, Rate, TargetLatencyMs);

        var outputs = new List<Output>();
        var local = new ClientPipeline(SessionId, new IdentityClockMapping(), Rate);
        host.Streamer.LocalSink = local;
        outputs.Add(new Output("host", hostClock, local, new FakeOutput(sim, hostClock, Rate, 0, 480), null));

        // Same remotes as the 10-minute test; only A gets an EQ.
        RemoteSpec[] remotes =
        [
            new("A", 4 * Second, +100, 48000, +50, 512),
            new("B", -9 * Second, -80, 44100, -100, 882),
            new("C", 30 * 60 * Second, +40, 48000, 0, 480),
        ];
        for (int i = 0; i < remotes.Length; i++)
        {
            var r = remotes[i];
            var address = IPAddress.Parse($"10.0.0.{i + 2}");
            var clientClock = new DriftingClock(sim, r.ClockOffsetTicks, r.ClockDriftPpm);
            var estimator = new ClockEstimator();
            var pipeline = new ClientPipeline(SessionId, estimator, r.DeviceRate);
            if (r.Name == "A") pipeline.Eq.SetGains(gains);

            var audioEndPoint = new IPEndPoint(address, 47802);
            net.Create(audioEndPoint).Start((data, _) => pipeline.HandleAudioDatagram(data));
            host.Streamer.AddClient(Guid.NewGuid(), audioEndPoint);

            var sync = new ClockSyncClient(net.Create(new IPEndPoint(address, 47803)), clientClock, HostClock, SessionId, estimator);
            sync.Start();
            Repeat(sim, 10 * Ms, sync.Tick);

            outputs.Add(new Output(r.Name, clientClock, pipeline, new FakeOutput(sim, clientClock, r.DeviceRate, r.DeviceDriftPpm, r.PeriodFrames), estimator));
        }

        foreach (var o in outputs)
        {
            o.Device.RecordSamples = o.Name != "B";   // the level checks use host, A and C
            o.Device.StreamPositionProbe = () => o.Pipeline.Scheduler.LastRenderStreamPosition;
            o.Device.Start(o.Pipeline.Scheduler);
        }

        var captureClock = new DriftingClock(sim, 0, +30);
        new FakeCapture(sim, captureClock, hostClock, Rate, host.OnCaptured).Start();

        sim.RunUntil(runEnd);

        // Level: RMS of the 440 Hz tone over the last 10 s of each 48 kHz device's interleaved output. 1200 frames
        // are exactly 11 periods of 440 Hz at 48 kHz, so 10 s (400 x 1200 frames) is a whole number of periods.
        double LevelDb(string name)
        {
            var samples = outputs.Single(x => x.Name == name).Device.Samples;
            const int Frames = 400 * 1200;
            int first = samples.Count - Frames * AudioFormat.Channels;
            double worst = double.NegativeInfinity, best = double.PositiveInfinity;
            for (int ch = 0; ch < AudioFormat.Channels; ch++)
            {
                double sum = 0;
                for (int n = 0; n < Frames; n++)
                {
                    double v = samples[first + n * AudioFormat.Channels + ch];
                    sum += v * v;
                }
                double db = 10 * Math.Log10(sum / Frames);
                worst = Math.Max(worst, db);
                best = Math.Min(best, db);
            }
            Assert.True(worst - best < 0.05, $"{name}: channels differ by {worst - best:F3} dB");
            return best;
        }

        double hostDb = LevelDb("host"), aDb = LevelDb("A"), cDb = LevelDb("C");
        double expectedA = EqCurve.ResponseDb(gains, 48000, 440) + EqCurve.PreampDb(gains, 48000);

        // Alignment, on the same stream-position measure as the 10-minute test.
        var series = outputs.Select(o => Resample(o.Device.Heard)).ToArray();
        var report = new StringBuilder();
        report.AppendLine($"level: host {hostDb:F3} dB, A {aDb:F3} dB (A-host {aDb - hostDb:F3}, expected {expectedA:F3}), C {cDb:F3} dB (C-host {cDb - hostDb:F3})");
        var failures = new List<string>();
        for (int i = 0; i < outputs.Count; i++)
            for (int j = i + 1; j < outputs.Count; j++)
            {
                var errors = Errors(series[i], series[j]);
                if (errors.Length == 0)
                {
                    failures.Add($"{outputs[i].Name}–{outputs[j].Name}: no comparable points");
                    continue;
                }
                double p99 = Percentile(errors, 0.99);
                report.AppendLine($"{outputs[i].Name}–{outputs[j].Name}: n {errors.Length}, p99 {p99 / 48:F3} ms ({p99:F1} samples)");
                if (p99 > MaxErrorSamples) failures.Add($"{outputs[i].Name}–{outputs[j].Name}: p99 {p99:F1} samples > {MaxErrorSamples}");
            }
        _log.WriteLine(report.ToString());

        Assert.True(Math.Abs(aDb - hostDb - expectedA) <= 0.5, $"A − host should be {expectedA:F3} dB{Environment.NewLine}{report}");
        Assert.True(Math.Abs(cDb - hostDb) <= 0.1, $"C − host should be 0 dB{Environment.NewLine}{report}");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures) + Environment.NewLine + report);
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
            long t = GridTime(p);
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

    private static long GridTime(int p) => MeasureFrom + p * Grid;

    /// <summary>Grid point <paramref name="p"/> lies outside the excluded outage window.</summary>
    private static bool IsMeasured(int p) => GridTime(p) < ExcludeFrom || GridTime(p) > ExcludeTo;

    /// <summary>Sorted |Δpos| (stream samples) over measured grid points where both are defined.</summary>
    private static double[] Errors(double[] a, double[] b)
    {
        var errors = new List<double>(a.Length);
        for (int p = 0; p < a.Length; p++)
        {
            if (!IsMeasured(p) || double.IsNaN(a[p]) || double.IsNaN(b[p])) continue;
            errors.Add(Math.Abs(a[p] - b[p]));
        }
        errors.Sort();
        return errors.ToArray();
    }

    private static double Percentile(double[] sorted, double q) =>
        sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
}
