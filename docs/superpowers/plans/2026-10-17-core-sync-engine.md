# RhythmSync Core Sync Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows app where one host PC streams its system audio over Wi-Fi to 2–4 PCs that play it within ≤ 2 ms of each other in the same room.

**Architecture:** Platform-free `RhythmSync.Core` holds the protocol, clock sync, jitter buffer, playback scheduler and sessions, all tested with a simulated network and drifting clocks. `RhythmSync.Platform.Windows` implements WASAPI capture/render via NAudio. `RhythmSync.App` is a WPF + tray front end. The host stamps 5 ms PCM frames with `playAt` (host time + 200 ms); clients map host time to their clock (NTP-style estimator) and steer a cubic resampler with a PI loop.

**Tech Stack:** .NET 10, C#, WPF (+ WinForms `NotifyIcon`), NAudio 2.2.1, CommunityToolkit.Mvvm, Serilog + Serilog.Sinks.File, xUnit.

**Spec:** `docs/superpowers/specs/2026-10-17-core-sync-engine-design.md`

## Global Constraints

- Target frameworks: `net10.0` for `RhythmSync.Core` and `RhythmSync.Tests`; `net10.0-windows` for `Platform.Windows`, `App`, and their test projects.
- Root `Directory.Build.props`: `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, `TreatWarningsAsErrors=true`.
- `RhythmSync.Core` references no Windows-only API and no NuGet package except what the BCL ships (System.Text.Json is in the BCL).
- Audio format (constants in `AudioFormat`): 48 000 Hz, 2 channels, 240 samples/frame (5 ms), 16-bit PCM on the wire, 960-byte payload.
- All times are `long` 100 ns ticks. Session clock = QPC converted to 100 ns (same epoch/unit as WASAPI `qpcPosition`).
- Wire: little-endian, magic `0x5253`, version `1`, audio packet 982 bytes. Ports default UDP 47800 discovery, TCP 47801 control, UDP 47802 audio, UDP 47803 clock.
- Target latency default 200 ms, range 100–500 ms. Per-device offset range ±100 ms, effective offset clamped `≥ −(targetLatencyMs − 50)`. Max 8 clients.
- Audio-path code (render/capture callbacks, scheduler, resampler, concealer, jitter buffer reads, packet (de)serialization) does not allocate per call: preallocated buffers, `Span<T>`, `ArrayPool`.
- Tests needing real audio hardware carry `[Trait("Category","Hardware")]`; real-time 30 s tests carry `[Trait("Category","Smoke")]`. Default run: `dotnet test --filter "Category!=Hardware&Category!=Smoke"`.
- Commit after every task; message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Huge absolute clock values** (host booted 30 days ago, client 60 days ago) → clock estimator stays within 0.2 ms; no precision loss from squaring raw ticks. Test in Task 3.
2. **Client output device runs at 44.1 kHz** (common on laptops) → scheduler uses nominal step 48000/44100 and still holds < 0.5 ms. Test in Task 7.
3. **Host changes latency mid-song** → `playAt` jumps 100 ms; client does exactly one hard resync with no click and re-converges. Test in Task 7.
4. **Stale/foreign packets** (old session on same port, seq far in the future) → dropped without flushing the buffer. Tests in Tasks 4 and 9.
5. **Offset slider at −100 ms with 100 ms latency** → clamped to −50 ms, never asks for audio that has not arrived. Test in Task 7.

---

## File Structure

```
RhythmSync.sln
Directory.Build.props
src/RhythmSync.Core/
  AudioFormat.cs
  Protocol/  PacketType.cs PacketSerializer.cs BeaconPacket.cs ControlMessages.cs ControlCodec.cs
  Clock/     ISessionClock.cs StopwatchClock.cs IClockMapping.cs IdentityClockMapping.cs ClockEstimator.cs
             ClockSyncServer.cs ClockSyncClient.cs SyncState.cs
  Audio/     IAudioOutput.cs IAudioCapture.cs CubicResampler.cs Concealer.cs IAudioProcessor.cs
             GainProcessor.cs DspChain.cs
  Client/    JitterBuffer.cs PlaybackScheduler.cs ClientPipeline.cs ClientSession.cs StatsWindow.cs
  Host/      CaptureTimestamper.cs FrameAssembler.cs IFrameSink.cs Streamer.cs HostPipeline.cs HostSession.cs
  Net/       IDatagramChannel.cs UdpDatagramChannel.cs DiscoveryBroadcaster.cs DiscoveryListener.cs
  Config/    AppSettings.cs SettingsStore.cs
src/RhythmSync.Platform.Windows/
  WasapiRenderOutput.cs WasapiOutputFactory.cs WasapiLoopbackSource.cs SilenceKeeper.cs
  DeviceCatalog.cs DeviceWatcher.cs Mmcss.cs ChannelMapper.cs Downmixer.cs HostAudioCheck.cs
src/RhythmSync.App/
  App.xaml(.cs) MainWindow.xaml(.cs) Views/ ViewModels/ Services/ (TrayIcon, FirewallSetup, NetworkProfile, SessionTicker)
tests/RhythmSync.Tests/                 Protocol/ Clock/ Audio/ Client/ Host/ Net/ Config/ Integration/ Sim/
tests/RhythmSync.Platform.Windows.Tests/
tests/RhythmSync.App.Tests/
docs/acceptance.md  README.md
```

---

### Task 1: Solution scaffold and UDP packet serializer

**Files:**
- Create: `RhythmSync.sln`, `Directory.Build.props`, `src/RhythmSync.Core/RhythmSync.Core.csproj`, `src/RhythmSync.Core/AudioFormat.cs`, `src/RhythmSync.Core/Protocol/PacketType.cs`, `src/RhythmSync.Core/Protocol/BeaconPacket.cs`, `src/RhythmSync.Core/Protocol/PacketSerializer.cs`
- Test: `tests/RhythmSync.Tests/Protocol/PacketSerializerTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class AudioFormat {
  public const int SampleRate = 48000, Channels = 2, FrameSamples = 240, FrameFloats = 480, PcmBytes = 960;
  public const long TicksPerSecond = 10_000_000, FrameTicks = 50_000;
}
public enum PacketType : byte { Audio = 1, Ping = 2, Pong = 3, Beacon = 4 }
public readonly record struct PingPacket(uint SessionId, uint PingId, long T1);
public readonly record struct PongPacket(uint SessionId, uint PingId, long T1, long T2, long T3);
public sealed record BeaconPacket(uint SessionId, ushort ControlPort, byte ProtocolVersion, string Name);
public static class PacketSerializer {
  public const ushort Magic = 0x5253; public const byte Version = 1;
  public const int AudioPacketBytes = 982, PingBytes = 20, PongBytes = 36, MaxBeaconNameBytes = 64;
  public static int WriteAudio(Span<byte> dst, uint sessionId, uint seq, long playAt, ReadOnlySpan<short> pcm);
  public static int WritePing(Span<byte> dst, in PingPacket p);
  public static int WritePong(Span<byte> dst, in PongPacket p);
  public static int WriteBeacon(Span<byte> dst, BeaconPacket b);   // ArgumentException if UTF-8 name > 64 bytes
  public static bool TryReadHeader(ReadOnlySpan<byte> src, out PacketType type, out uint sessionId);
  public static bool TryReadAudio(ReadOnlySpan<byte> src, out uint seq, out long playAt, Span<short> pcmDst); // pcmDst.Length == 480
  public static bool TryReadPing(ReadOnlySpan<byte> src, out PingPacket p);
  public static bool TryReadPong(ReadOnlySpan<byte> src, out PongPacket p);
  public static bool TryReadBeacon(ReadOnlySpan<byte> src, out BeaconPacket? b);
}
```

- [ ] **Step 1: Scaffold**

```bash
dotnet new sln -n RhythmSync
dotnet new classlib -n RhythmSync.Core -o src/RhythmSync.Core -f net10.0
dotnet new xunit -n RhythmSync.Tests -o tests/RhythmSync.Tests -f net10.0
dotnet sln add src/RhythmSync.Core tests/RhythmSync.Tests
dotnet add tests/RhythmSync.Tests reference src/RhythmSync.Core
```
Delete template `Class1.cs`/`UnitTest1.cs`. Add `Directory.Build.props` with the Global Constraints properties. Add `AudioFormat.cs`.

- [ ] **Step 2: Write the failing tests** in `PacketSerializerTests`

```csharp
[Fact] public void Audio_round_trips() {
  var pcm = Enumerable.Range(0, 480).Select(i => (short)(i * 37 - 8000)).ToArray();
  var buf = new byte[1500];
  Assert.Equal(982, PacketSerializer.WriteAudio(buf, 0xA1B2C3D4, 77, 123_456_789_012, pcm));
  Assert.True(PacketSerializer.TryReadHeader(buf.AsSpan(0, 982), out var t, out var sid));
  Assert.Equal(PacketType.Audio, t); Assert.Equal(0xA1B2C3D4u, sid);
  var outPcm = new short[480];
  Assert.True(PacketSerializer.TryReadAudio(buf.AsSpan(0, 982), out var seq, out var playAt, outPcm));
  Assert.Equal(77u, seq); Assert.Equal(123_456_789_012, playAt); Assert.Equal(pcm, outPcm);
}
// Ping_round_trips (20 bytes), Pong_round_trips (36 bytes), Beacon_round_trips(name "Living Room PC")
[Fact] public void Beacon_name_over_64_bytes_throws() => Assert.Throws<ArgumentException>(() =>
  PacketSerializer.WriteBeacon(new byte[200], new BeaconPacket(1, 47801, 1, new string('é', 33))));
[Theory] // each must return false, never throw
[InlineData("bad-magic")] [InlineData("version-2")] [InlineData("truncated-981")] [InlineData("framecount-239")] [InlineData("empty")]
public void Malformed_audio_rejected(string kind) { /* build a valid packet, corrupt per kind, assert TryReadHeader||TryReadAudio false */ }
[Fact] public void Random_garbage_never_throws() {
  var rng = new Random(1234); var pcm = new short[480];
  for (int i = 0; i < 10_000; i++) { var b = new byte[rng.Next(0, 1201)]; rng.NextBytes(b);
    PacketSerializer.TryReadHeader(b, out _, out _); PacketSerializer.TryReadAudio(b, out _, out _, pcm);
    PacketSerializer.TryReadPing(b, out _); PacketSerializer.TryReadPong(b, out _); PacketSerializer.TryReadBeacon(b, out _); }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/RhythmSync.Tests --filter "FullyQualifiedName~PacketSerializerTests"`
Expected: build FAIL — `PacketSerializer` not defined.

- [ ] **Step 4: Implement** `PacketType`, `BeaconPacket`, `PacketSerializer` with `BinaryPrimitives` little-endian. Header layout per spec §4.1: magic u16, version u8, type u8, sessionId u32 (8 bytes). Audio body: seq u32, playAt i64, frameCount u16 (must equal 240), 480 × i16. `TryRead*` check length, magic, version and type first.

- [ ] **Step 5: Run tests to verify they pass** — same command. Expected: PASS.

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(core): solution scaffold and UDP packet serializer"`

---

### Task 2: Control message codec

**Files:**
- Create: `src/RhythmSync.Core/Protocol/ControlMessages.cs`, `src/RhythmSync.Core/Protocol/ControlCodec.cs`
- Test: `tests/RhythmSync.Tests/Protocol/ControlCodecTests.cs`

**Interfaces:**
- Produces:
```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "Hello")] /* …one per derived type, discriminator = type name */
public abstract record ControlMessage;
public sealed record Hello(Guid DeviceId, string DeviceName, string PairingCode, int ProtocolVersion, int AudioPort) : ControlMessage;
public sealed record Welcome(uint SessionId, int SampleRate, int Channels, int FrameSamples, int TargetLatencyMs, int AudioPort, int ClockPort) : ControlMessage;
public enum RejectReason { BadCode, LockedOut, VersionMismatch, SessionFull }   // serialized as string
public sealed record Reject(RejectReason Reason) : ControlMessage;
public sealed record Ready : ControlMessage;
public sealed record HeartbeatStats(double LossPct, double LatePct, double DriftPpm, double FitResidualMs, double SyncErrorMs, SyncState State);
public sealed record Heartbeat(HeartbeatStats? Stats) : ControlMessage;
public sealed record SetDeviceSettings(float Volume, double OffsetMs) : ControlMessage;
public sealed record SetLatency(int TargetLatencyMs) : ControlMessage;
public sealed record Bye : ControlMessage;
public static class ControlCodec {
  public const int MaxMessageBytes = 65536;
  public static Task WriteAsync(Stream s, ControlMessage m, CancellationToken ct);
  public static Task<ControlMessage?> ReadAsync(Stream s, CancellationToken ct); // null on clean EOF before a length prefix
}
public enum SyncState { Connecting, Syncing, InSync, Degraded, Disconnected }  // put in Clock/SyncState.cs now; Task 9 adds the evaluator
```

- [ ] **Step 1: Write the failing tests**

```csharp
public static TheoryData<ControlMessage> All => new() {
  new Hello(Guid.NewGuid(), "Desk", "0420", 1, 47802), new Welcome(7, 48000, 2, 240, 200, 47802, 47803),
  new Reject(RejectReason.LockedOut), new Ready(), new Heartbeat(new HeartbeatStats(0.1, 0, 42.5, 0.2, -0.3, SyncState.InSync)),
  new Heartbeat(null), new SetDeviceSettings(0.5f, -12.5), new SetLatency(300), new Bye() };
[Theory, MemberData(nameof(All))] public async Task Round_trips(ControlMessage m) { /* write to MemoryStream, rewind, read, Assert.Equal(m, read) */ }
[Fact] public async Task Json_uses_type_discriminator_and_string_enums() // UTF-8 body contains "\"type\":\"Reject\"" and "\"LockedOut\""
[Fact] public async Task Empty_stream_returns_null()
[Fact] public async Task Oversize_length_throws_InvalidData()        // prefix 70_000
[Fact] public async Task Truncated_body_throws_InvalidData()         // prefix 50, body 10 bytes
[Fact] public async Task Unknown_type_throws_InvalidData()           // body {"type":"Nope"}
```
(Hello and Ready records with value equality: `Ready` has no members, so equality holds by type.)

- [ ] **Step 2: Run tests to verify they fail** — `dotnet test tests/RhythmSync.Tests --filter "FullyQualifiedName~ControlCodecTests"`. Expected: build FAIL.
- [ ] **Step 3: Implement** — frame = `u32` little-endian length + UTF-8 JSON; `JsonSerializerOptions` with `JsonStringEnumConverter`, camelCase not required. Wrap `JsonException`/`NotSupportedException` and short reads in `InvalidDataException`.
- [ ] **Step 4: Run tests to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `feat(core): length-prefixed JSON control messages`

---

### Task 3: Session clock and clock estimator

**Files:**
- Create: `src/RhythmSync.Core/Clock/ISessionClock.cs`, `StopwatchClock.cs`, `IClockMapping.cs`, `IdentityClockMapping.cs`, `ClockEstimator.cs`
- Test: `tests/RhythmSync.Tests/Clock/ClockEstimatorTests.cs`, `tests/RhythmSync.Tests/Sim/SyntheticPings.cs`

**Interfaces:**
- Produces:
```csharp
public interface ISessionClock { long NowTicks { get; } }
public sealed class StopwatchClock : ISessionClock   // NowTicks = QPC * 10_000_000 / Stopwatch.Frequency, computed without overflow (Int128 or split div/mod)
public interface IClockMapping {
  bool IsConverged { get; } double DriftPpm { get; } double ResidualMs { get; } double StdErrorMs { get; }  // StdErrorMs: weighted SE of fitted host time at latest sample (ruling R7)
  long? LastSampleClientTicks { get; } bool EverConverged { get; }
  long ClientToHost(long clientTicks);
}
public sealed class IdentityClockMapping : IClockMapping   // converged, drift 0, residual 0, std error 0, ClientToHost(x) == x
public sealed class ClockEstimator : IClockMapping {
  public ClockEstimator(long windowTicks = 600_000_000, double keepFraction = 0.2, int minSamples = 20, double convergedResidualMs = 0.5);
  public void AddSample(long t1, long t2, long t3, long t4);
  public int SampleCount { get; }
}
```

- [ ] **Step 1: Write the test helper** `SyntheticPings` (tests/Sim): generates `(t1,t2,t3,t4)` for a true mapping `host(c) = offset + c·(1 + driftPpm·1e-6)`, client send interval, seeded `Random`. Each direction's delay = 0.5 ms + Exp(mean: up 2 ms, down 1 ms) + with probability 0.1 an extra U(20, 100) ms. Host turnaround 0.05 ms. Supports a drift change at a given client time.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void Recovers_offset_and_drift() // offset 3.7 s, +80 ppm, 120 s at 250 ms, seed 42
{ /* feed all; Assert.InRange(est.ClientToHost(lastT4) - truth.Host(lastT4), -2000, 2000); Assert.InRange(est.DriftPpm, 78, 82); */ }
[Fact] public void Converges_after_2s_of_fast_pings()   // 40 samples at 50 ms, drift 0 → IsConverged, ResidualMs < 0.5, EverConverged
[Fact] public void Not_converged_below_20_samples()      // 19 samples → !IsConverged
[Fact] public void Large_absolute_times_keep_precision() // client base = 60 days of ticks, offset = −30 days, +80 ppm → same 2000-tick and ±2 ppm bounds
[Fact] public void Window_forgets_old_drift()           // +80 ppm for 60 s then −40 ppm; after 130 s total, DriftPpm in [−42, −38]
[Fact] public void Identity_maps_one_to_one()
```

- [ ] **Step 3: Run to verify failure** — `--filter "FullyQualifiedName~ClockEstimatorTests"`. Expected: build FAIL.

- [ ] **Step 4: Implement** `StopwatchClock`, `IdentityClockMapping`, `ClockEstimator`. Algorithm (not determined by the signature):

```text
on AddSample(t1,t2,t3,t4):
  rtt = (t4 - t1) - (t3 - t2);  offset = ((t2 - t1) + (t3 - t4)) / 2
  c = t1 + (t4 - t1)/2;  h = c + offset            // one (client, host) point
  store {c, h, rtt}; drop samples with c < t4 - window
  k = max(ceil(keepFraction * n), min(n, minSamples)); selected = k smallest by rtt
  fit h = a + b·c by least squares over selected, using doubles of (c - c0) and (h - h0)
      where c0/h0 = first selected sample  (avoids squaring 1e13-scale ticks)
  residualMs = RMS(h - fit(c)) / 10_000
  IsConverged = n >= minSamples && residualMs < convergedResidualMs; EverConverged |= IsConverged
  DriftPpm = (b - 1) * 1e6; LastSampleClientTicks = t4
ClientToHost(x) = h0 + a' + b·(x - c0)   (round to long)
```

- [ ] **Step 5: Run to verify pass.** Expected: PASS.
- [ ] **Step 6: Commit** — `feat(core): session clock and min-RTT clock estimator`

---

### Task 4: Jitter buffer

**Files:**
- Create: `src/RhythmSync.Core/Client/JitterBuffer.cs`
- Test: `tests/RhythmSync.Tests/Client/JitterBufferTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class JitterBuffer {
  public JitterBuffer(int capacityFrames = 256);
  public bool Insert(uint seq, long playAt, ReadOnlySpan<short> pcm);   // false for duplicate, late or out-of-range
  public bool TryReadFrame(uint seq, Span<float> dst);                  // dst.Length == 480; sample / 32768f
  public bool TryMapHostTimeToPosition(long hostTicks, out double streamSample); // false only if no reference frame ever seen
  public void MarkConsumedThrough(uint seq);                            // frees frames <= seq
  public long Received { get; } public long Duplicates { get; } public long Late { get; } public long Rejected { get; }
}
```
Rules: stream sample of frame `seq` start = `(long)seq * 240` (u32 wrap after 248 days is unsupported). Anchor = `consumedThrough + 1` if anything consumed, else oldest present seq. Reject (count `Rejected`) if `seq >= anchor + capacity`. `seq <= consumedThrough` → count `Late`. Mapping: take the latest frame `f` with `playAt_f <= T` among present frames plus the retained last-consumed `(seq, playAt)`; interpolate to the next present frame `g > f`, else extrapolate at 48 kHz nominal; if `T` precedes every reference, extrapolate backward from the earliest.

- [ ] **Step 1: Write the failing tests** (helper `Frame(seq)` = 480 shorts all `seq*100`; `PlayAt(seq) = 1_000_000 + seq*50_000`)

```csharp
[Fact] public void In_order_insert_and_read()          // 0..9; TryReadFrame(5) → all 500/32768f
[Fact] public void Out_of_order_frames_all_readable()  // insert 3,1,2
[Fact] public void Duplicate_is_counted_and_refused()  // Insert(4) twice → second false, Duplicates == 1
[Fact] public void Late_frame_is_refused()             // MarkConsumedThrough(10); Insert(8) false; Late == 1; TryReadFrame(8) false
[Fact] public void Far_future_seq_is_rejected_without_flushing() // 0..9 present; Insert(100_000) false; Rejected == 1; frames 0..9 readable
[Fact] public void Missing_frame_read_returns_false()
[Fact] public void Maps_host_time_inside_frames()      // T = PlayAt(4) + 25_000 → 1080 ± 1e-6
[Fact] public void Extrapolates_past_newest_and_before_oldest() // T = PlayAt(9)+100_000 → 9*240+480; T = PlayAt(0)-50_000 → -240
[Fact] public void Maps_using_consumed_reference()     // insert 0..9, MarkConsumedThrough(9), T = PlayAt(9)+50_000 → 2400
```

- [ ] **Step 2: Run to verify failure** — `--filter "FullyQualifiedName~JitterBufferTests"`.
- [ ] **Step 3: Implement** with a preallocated ring of `capacity` slots (`short[480]`, `playAt`, `seq`, `present`). No allocation in `Insert`/`TryReadFrame`/`TryMapHostTimeToPosition`. Guard with a `lock` (network thread inserts, audio thread reads; critical sections are short).
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): jitter buffer with host-time mapping`

---

### Task 5: Cubic resampler

**Files:**
- Create: `src/RhythmSync.Core/Audio/CubicResampler.cs`
- Test: `tests/RhythmSync.Tests/Audio/CubicResamplerTests.cs`

**Interfaces:**
- Produces:
```csharp
public static class CubicResampler {
  // input: interleaved stereo; position: fractional frame index into input, >= 1; step: input frames per output frame
  // returns position after the last output frame; ArgumentException if input has fewer than RequiredInputFrames frames
  public static double Process(ReadOnlySpan<float> input, double position, double step, Span<float> output);
  public static int RequiredInputFrames(double position, double step, int outFrames); // floor(position + (outFrames-1)*step) + 3
}
```
Interpolation per channel, Catmull-Rom: with `x = frac`, `y(x) = p1 + 0.5x(p2 − p0 + x(2p0 − 5p1 + 4p2 − p3 + x(3(p1 − p2) + p3 − p0)))` where `p0..p3` are frames `i−1..i+2`, `i = floor(position)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Unity_step_at_integer_position_is_identity()  // random input 600 frames; position 1.0, step 1.0, 500 out → out[k] == in[k+1] (1e-6)
[Fact] public void Returns_advanced_position()                    // position 1.25, step 1.0005, 480 out → 1.25 + 480*1.0005 (1e-9)
[Fact] public void Throws_when_input_too_short()
[Theory] [InlineData(1.0005)] [InlineData(0.9995)]
public void Tone_quality_at_500ppm(double step)
{ /* 1 kHz sine amp 0.5 at 48 kHz, 48_000 outputs; least-squares fit a sine at 1000*step Hz (amplitude+phase) to each channel;
     THD+N = 20*log10(rms(residual)/rms(signal)); Assert.True(thdn < -80) */ }
```
If the −80 dB tone test fails, report it — do not loosen the bound (it is a spec requirement).

- [ ] **Step 2: Run to verify failure.** — `--filter "FullyQualifiedName~CubicResamplerTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): cubic Hermite fractional resampler`

---

### Task 6: Concealer and DSP chain

**Files:**
- Create: `src/RhythmSync.Core/Audio/Concealer.cs`, `IAudioProcessor.cs`, `GainProcessor.cs`, `DspChain.cs`
- Test: `tests/RhythmSync.Tests/Audio/ConcealerTests.cs`, `tests/RhythmSync.Tests/Audio/DspChainTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class Concealer {
  public const int SpliceFrames = 48, MaxConcealFrames = 10;
  public void Process(Span<float> frame, bool present);   // frame = 480 floats; fills/mutates in place
  public void Reset();                                     // next present frame fades in 0→1 across all 240 sample frames
  public long ConcealedFrames { get; }
  public static void Ramp(Span<float> interleavedStereo, float fromGain, float toGain);
}
public interface IAudioProcessor { void Process(Span<float> interleaved, int channels); }
public sealed class GainProcessor : IAudioProcessor { public float Gain { get; set; } }  // thread-safe set; ramps old→new across one buffer
public sealed class DspChain : IAudioProcessor { public void Add(IAudioProcessor p); }  // runs in insertion order
```
Concealer rules: keeps the last good frame and the last output sample per channel. Missing frame number `m` (1-based within a gap): output = last good frame × gain, gain falling linearly from `1 − (m−1)/10` to `1 − m/10` across the frame; `m > 10` → silence. Every splice (start of a repeat, first good frame after a gap) blends from the last output sample over 48 sample frames: `out[i] = (1−w)·last + w·new[i]`, `w = i/48`. First good frame after `m > 10` or after `Reset()` fades in 0→1 over the whole frame instead.

- [ ] **Step 1: Write the failing tests** (440 Hz sine, amp 0.5, 48 kHz, cut into frames; `MaxDelta` = largest |Δ| between consecutive samples per channel across all output)

```csharp
[Fact] public void Present_frames_pass_through_unchanged()
[Fact] public void Single_loss_has_no_click()      // drop frame 5 of 20 → MaxDelta < 0.06; frame 5 RMS > 0.5 × normal RMS; ConcealedFrames == 1
[Fact] public void Long_gap_fades_to_silence()     // drop 5..20 of 30 → frames 15..20 all 0; MaxDelta < 0.06 incl. resume at 21
[Fact] public void Reset_fades_in()                // Reset(); constant 0.5 frame → first sample 0, last ≥ 0.49, non-decreasing
[Fact] public void Gain_ramps_then_holds()         // Gain 1→0.5 on constant 1.0 × 480 frames: first ≈ 1.0, last == 0.5; next buffer all 0.5
[Fact] public void Chain_applies_in_order()        // two GainProcessor(0.5) → 0.25
```

- [ ] **Step 2: Run to verify failure.** — `--filter "FullyQualifiedName~ConcealerTests|FullyQualifiedName~DspChainTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): packet-loss concealment and DSP chain`

---

### Task 7: Playback scheduler

**Files:**
- Create: `src/RhythmSync.Core/Audio/IAudioOutput.cs`, `src/RhythmSync.Core/Client/PlaybackScheduler.cs`
- Create (test infra): `tests/RhythmSync.Tests/Sim/SimScheduler.cs`, `Sim/DriftingClock.cs`, `Sim/ManualClock.cs`, `Sim/FakeOutput.cs`
- Test: `tests/RhythmSync.Tests/Client/PlaybackSchedulerTests.cs`

**Interfaces:**
- Consumes: `JitterBuffer` (Task 4), `IClockMapping`/`IdentityClockMapping` (Task 3), `CubicResampler` (Task 5), `Concealer`, `DspChain` (Task 6).
- Produces:
```csharp
public readonly record struct RenderTiming(long DevicePositionFrames, long DevicePositionClientTicks, long WritePositionFrames);
public interface IRenderCallback { void Render(Span<float> stereo, in RenderTiming timing); } // stereo interleaved, frames = Length/2
public interface IAudioOutput : IDisposable { int SampleRate { get; } void Start(IRenderCallback cb); void Stop(); event Action? DeviceInvalidated; }
public interface IAudioOutputFactory { IAudioOutput Create(); }
public enum PlaybackState { WaitingForData, Playing }
public readonly record struct PlaybackStats(double SyncErrorMs, double RatioPpm, long Resyncs, long ConcealedFrames, long LateFrames);
public sealed class PlaybackScheduler : IRenderCallback {
  public const double HardResyncMs = 20, MaxCorrectionPpm = 500;
  public PlaybackScheduler(JitterBuffer buffer, IClockMapping clock, int deviceSampleRate, DspChain dsp);
  public void SetOffset(double offsetMs, int targetLatencyMs); // clamp to [max(-100, -(targetLatencyMs-50)), 100]
  public double EffectiveOffsetMs { get; }
  public PlaybackState State { get; }
  public double LastRenderStreamPosition { get; }   // stream sample at the first sample of the last buffer; NaN while waiting
  public PlaybackStats GetStats();
}
// test infra
public sealed class SimScheduler { public long Now { get; } public void Schedule(long atTicks, Action a); public void RunUntil(long ticks); }
public sealed class DriftingClock : ISessionClock { public DriftingClock(SimScheduler s, long offsetTicks, double driftPpm); public long ToTrue(long clockTicks); }
public sealed class ManualClock : ISessionClock { public long NowTicks { get; set; } public void Advance(TimeSpan d); }
public sealed class FakeOutput : IAudioOutput {
  public FakeOutput(SimScheduler s, DriftingClock clientClock, int sampleRate, double deviceDriftPpm, int periodFrames);
  public List<(long TrueTicksHeard, double StreamPos)> Heard { get; }   // one entry per callback (first sample)
  public List<float> Samples { get; }                                   // all output, for click checks
  public Action? BeforeEachCallback { get; set; }
}
```
`FakeOutput` consumes at `sampleRate·(1+deviceDrift)` frames per *true* second; prefills one period (write position = device position + period); each callback reports `RenderTiming` with `DevicePositionClientTicks` read from `clientClock`; the true time a sample at write index `w` is heard = callback true time + `(w − p)/(rate·(1+drift))`.

- [ ] **Step 1: Write test infra** (`SimScheduler` = priority queue of `(time, seq, action)`; `DriftingClock.NowTicks = offset + Now·(1+drift·1e-6)`), then the failing tests. Common setup: `IdentityClockMapping`, client clock offset 0/drift 0, frames inserted from `BeforeEachCallback` while `playAt <= now + 300 ms`, `playAt(seq) = t0 + 200 ms + seq·50_000`, content 440 Hz sine amp 0.5. *Truth* for an entry = `(hostTimeHeard − offsetTicks − playAt(0))·48000/1e7`.

```csharp
[Fact] public void Outputs_silence_while_waiting()                  // no frames → all zeros, State == WaitingForData
[Fact] public void Starts_on_time()                                 // first non-NaN entry within 24 samples of truth
[Fact] public void Converges_with_100ppm_device_drift()             // 30 s, 48 kHz, period 480; entries after 10 s: |pos − truth| < 24; Resyncs == 0
[Fact] public void Converges_on_44100_device()                      // same with sampleRate 44100, period 441
[Fact] public void Offset_shifts_playback()                         // SetOffset(10, 200) → same bound vs truth with 10 ms offset
[Fact] public void Offset_is_clamped()                              // SetOffset(-100,100) → −50; SetOffset(150,200) → 100; SetOffset(-100,200) → −100
[Fact] public void Latency_jump_resyncs_once_without_click()        // at 10 s, playAt of later frames += 100 ms → Resyncs == 1; MaxDelta(Samples) < 0.06; within bound again after 20 s
[Fact] public void Missing_frames_are_concealed()                   // skip every 50th frame → ConcealedFrames > 0; bound still holds after 10 s
```

- [ ] **Step 2: Run to verify failure.** — `--filter "FullyQualifiedName~PlaybackSchedulerTests"`

- [ ] **Step 3: Implement `PlaybackScheduler`.** Gains start at `Kp = 1e-5` per stream-sample of error and `Ki = 2e-8` per sample per callback; tune only these two if convergence tests fail. Preallocate staging for 1 s of input. Render loop:

```text
Render(buf, timing):
  n = buf.frames; nominal = 48000 / deviceRate
  heardClient = timing.DevicePositionClientTicks + (timing.WritePositionFrames - timing.DevicePositionFrames) * 1e7 / deviceRate
  hostT = clock.ClientToHost(heardClient) - offsetTicks
  if !buffer.TryMapHostTimeToPosition(hostT, out should) or should < earliest available sample:
      fill silence; LastRenderStreamPosition = NaN; return
  if State == WaitingForData: readPos = should; concealer.Reset(); processedThrough = floor(should/240) - 1; State = Playing
  err = should - readPos                          // stream samples; + means we are behind
  if |err| > HardResyncMs * 48:
      render up to 240 frames of current audio with Ramp(1→0), silence for the rest
      readPos = should + n * nominal; processedThrough = floor(readPos/240) - 1; concealer.Reset(); Resyncs++; integral = 0
  else:
      integral += err; corr = clamp(Kp*err + Ki*integral, ±500e-6)
      step = nominal * (1 + corr)
      ensure frames through floor((readPos + n*step)/240) + 1 processed (TryReadFrame → concealer.Process → ring)
      copy needed range into staging; readPos = base + CubicResampler.Process(staging, readPos - base, step, buf)
  dsp.Process(buf, 2); buffer.MarkConsumedThrough(floor(readPos/240) - 2)
  record SyncErrorMs = err/48, RatioPpm = corr*1e6, LastRenderStreamPosition = position at buffer start
```

- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): playback scheduler with PI drift correction`

---

### Task 8: Capture timestamper and frame assembler

**Files:**
- Create: `src/RhythmSync.Core/Host/CaptureTimestamper.cs`, `src/RhythmSync.Core/Host/IFrameSink.cs`, `src/RhythmSync.Core/Host/FrameAssembler.cs`
- Test: `tests/RhythmSync.Tests/Host/CaptureTimestamperTests.cs`, `tests/RhythmSync.Tests/Host/FrameAssemblerTests.cs`

**Interfaces:**
- Consumes: `CubicResampler` (Task 5).
- Produces:
```csharp
public sealed class CaptureTimestamper {
  public CaptureTimestamper(int nominalRate, long windowTicks = 300_000_000);
  public void AddObservation(long devicePositionFrames, long sessionTicks);
  public long SessionTimeOf(double devicePositionFrames);   // before 2 observations: first observation + nominal rate
  public double MeasuredRate { get; }
}
public interface IFrameSink { void OnFrame(uint seq, long playAt, ReadOnlySpan<short> pcm); }
public sealed class FrameAssembler {
  public FrameAssembler(int deviceSampleRate, CaptureTimestamper timestamper, IFrameSink sink, int targetLatencyMs, int ditherSeed = 0);
  public int TargetLatencyMs { get; set; }
  public void Push(ReadOnlySpan<float> interleavedStereo, long devicePositionOfFirstFrame);
}
```
Timestamper: least-squares line `session = a + b·position` over the window (center values like Task 3). Assembler: resample device→48 kHz with fixed step `deviceRate/48000` (keep 3 frames of history across pushes); the 48 kHz output frame `k` starts at device position `origin + k·step`; `playAt = SessionTimeOf(thatPosition) + TargetLatencyMs·10_000`; convert to i16 with TPDF dither `(r1 − r2)` LSB, round, clamp to ±32767; emit every 240 frames with consecutive `seq` from 0.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Tracks_drifting_capture_clock()     // device +150 ppm vs session, observation every 10 ms with uniform ±2 ms jitter, 60 s
                                                       // |SessionTimeOf(lastPos) − truth| < 1000 ticks; |MeasuredRate − 48007.2| < 1
[Fact] public void Emits_240_frame_packets_with_consecutive_seq() // push 240_000 device frames in chunks 137, 441, 1000 repeating → 1000 frames, seq 0..999,
                                                       // playAt spacing within 50_000 ± 10, playAt(0) == SessionTimeOf(0) + 2_000_000
[Fact] public void Resamples_44100_input()             // 2 s at 44 100 → 400 ± 2 frames
[Fact] public void Scales_dithers_and_clamps()         // constant 0.5 → every sample in 16384 ± 2; constant 1.5 → 32767
[Fact] public void Latency_change_shifts_playAt()      // TargetLatencyMs = 300 mid-stream → next frame playAt jumps by +1_000_000 ± 10 relative to spacing
```

- [ ] **Step 2: Run to verify failure.** — `--filter "FullyQualifiedName~CaptureTimestamperTests|FullyQualifiedName~FrameAssemblerTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): capture timestamping and frame assembly`

---

### Task 9: Datagram channels, clock sync, streamer and pipelines

**Files:**
- Create: `src/RhythmSync.Core/Net/IDatagramChannel.cs`, `src/RhythmSync.Core/Audio/IAudioCapture.cs`, `src/RhythmSync.Core/Clock/ClockSyncServer.cs`, `ClockSyncClient.cs`, `SyncState.cs` (add evaluator), `src/RhythmSync.Core/Host/Streamer.cs`, `HostPipeline.cs`, `src/RhythmSync.Core/Client/ClientPipeline.cs`
- Create (test infra): `tests/RhythmSync.Tests/Sim/SimNetwork.cs`
- Test: `tests/RhythmSync.Tests/Clock/ClockSyncTests.cs`, `tests/RhythmSync.Tests/Host/StreamerTests.cs`, `tests/RhythmSync.Tests/Client/ClientPipelineTests.cs`, `tests/RhythmSync.Tests/Clock/SyncStateEvaluatorTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 3, 4, 6, 7, 8.
- Produces:
```csharp
public delegate void DatagramHandler(ReadOnlySpan<byte> data, IPEndPoint from);
public interface IDatagramChannel : IDisposable { IPEndPoint LocalEndPoint { get; } void Send(ReadOnlySpan<byte> datagram, IPEndPoint to); void Start(DatagramHandler handler); }
public interface IDatagramChannelFactory { IDatagramChannel Create(IPEndPoint bind, bool broadcast = false); }
public delegate void CaptureHandler(ReadOnlySpan<float> interleavedStereo, long devicePositionFrames, long qpcTicks);
public interface IAudioCapture : IDisposable { int SampleRate { get; } void Start(CaptureHandler h); void Stop(); event Action? DeviceInvalidated; }
public sealed class ClockSyncServer : IDisposable { public ClockSyncServer(IDatagramChannel ch, ISessionClock clock, uint sessionId); public void Start(); }
public sealed class ClockSyncClient : IDisposable {
  public ClockSyncClient(IDatagramChannel ch, ISessionClock clock, IPEndPoint server, uint sessionId, ClockEstimator estimator);
  public void Start(); public void Tick();   // ping every 50 ms for 2 s after Start, then every 250 ms
  public long PingsSent { get; }
}
public static class SyncStateEvaluator {
  // !EverConverged → Syncing; StdErrorMs > 1 or (LastSampleClientTicks != null && now − LastSampleClientTicks > 10 s) → Degraded; else InSync
  public static SyncState Evaluate(IClockMapping m, long nowClientTicks);
}
public sealed class Streamer : IFrameSink {
  public Streamer(IDatagramChannel audio, uint sessionId);
  public void AddClient(Guid deviceId, IPEndPoint audioEndPoint); public void RemoveClient(Guid deviceId);
  public IFrameSink? LocalSink { get; set; }
}
public sealed class HostPipeline : IDisposable {   // owns a started ClockSyncServer on the clock channel
  public HostPipeline(uint sessionId, IDatagramChannel audio, IDatagramChannel clock, ISessionClock sessionClock, int captureSampleRate, int targetLatencyMs);
  public Streamer Streamer { get; } public FrameAssembler Assembler { get; }
  public void OnCaptured(ReadOnlySpan<float> stereo, long devicePositionFrames, long qpcTicks); // AddObservation + Push
}
public sealed class ClientPipeline : IFrameSink {
  public ClientPipeline(uint sessionId, IClockMapping clock, int deviceSampleRate);
  public JitterBuffer Buffer { get; } public DspChain Dsp { get; } public GainProcessor Gain { get; }
  public PlaybackScheduler Scheduler { get; }
  public void HandleAudioDatagram(ReadOnlySpan<byte> datagram);   // checks header + sessionId, inserts
  public void ReplaceOutputRate(int deviceSampleRate);             // new scheduler on same buffer/dsp (device change)
}
public sealed class SimNetwork : IDatagramChannelFactory {
  public SimNetwork(SimScheduler s, int seed, SimLinkProfile profile);
  public void SetOutage(long fromTrueTicks, long toTrueTicks);
}
public sealed record SimLinkProfile(double BaseDelayMs = 0.5, double JitterMeanMs = 1.5, double TailProbability = 0.02,
  double TailMinMs = 20, double TailMaxMs = 100, double LossProbability = 0.005);
```

- [ ] **Step 1: Write `SimNetwork`** (copies datagram bytes, delivers through `SimScheduler` after sampled delay; drops on loss/outage; reordering emerges from random delays), then the failing tests:

```csharp
[Fact] public void Clock_sync_converges_over_sim_network()  // host DriftingClock(0,0); client DriftingClock(12 s, +60 ppm); Tick every 10 ms
                                                            // after 3 s IsConverged; after 60 s |ClientToHost(c.Now) − host.Now| < 2000 ticks
[Fact] public void Ping_rate_is_fast_then_slow()            // PingsSent at 2 s ∈ [39, 41]; additional in next 10 s ∈ [39, 41]
[Fact] public void Pings_and_pongs_with_wrong_session_are_ignored()
[Fact] public void Streamer_fans_out_identical_frames()     // 2 clients + LocalSink get the same seq/playAt/pcm; after RemoveClient no more sends to it
[Fact] public void Client_pipeline_drops_wrong_session_and_garbage() // Received unchanged
[Theory] /* SyncStateEvaluator: never converged → Syncing; converged → InSync; std error 1.5 ms → Degraded; last sample 11 s ago → Degraded */
```

- [ ] **Step 2: Run to verify failure.** — `--filter "FullyQualifiedName~ClockSyncTests|FullyQualifiedName~StreamerTests|FullyQualifiedName~ClientPipelineTests|FullyQualifiedName~SyncStateEvaluatorTests"`
- [ ] **Step 3: Implement.** Streamer serializes once per frame into a preallocated 982-byte buffer; client list is an immutable array swapped under a lock. ClockSyncServer sets `t2` on receive and `t3` immediately before send.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): clock sync protocol, streamer and pipelines`

---

### Task 10: Virtual session integration test

**Files:**
- Create: `tests/RhythmSync.Tests/Sim/FakeCapture.cs`, `tests/RhythmSync.Tests/Integration/VirtualSessionTests.cs`
- Modify: any Core file only to fix defects this test exposes (each fix gets a focused unit test in that file's test class).

**Interfaces:**
- Consumes: `HostPipeline`, `ClientPipeline`, `ClockSyncServer/Client`, `ClockEstimator`, `SyncStateEvaluator`, `SimNetwork`, `SimScheduler`, `DriftingClock`, `FakeOutput`.
- Produces: `FakeCapture(SimScheduler s, DriftingClock deviceClock, ISessionClock hostClock, int rate, CaptureHandler sink)` — every 10 ms pushes a 440 Hz sine with device position and host QPC (+ uniform ±1 ms jitter).

- [ ] **Step 1: Write the failing test**

```csharp
[Fact] public void Four_outputs_stay_aligned_for_10_minutes_with_outage()
{
  // host clock true; capture device +30 ppm; local client: IdentityClockMapping, 48 kHz device 0 ppm
  // remote clients (client clock offset, client drift, device rate, device drift):
  //   (4 s, +100 ppm, 48000, +50) (−9 s, −80 ppm, 44100, −100) (30 min, +40 ppm, 48000, 0)
  // clock sync ticks every 10 ms; outage 300 s..302 s; run 600 s
  // for every pair, interpolate Heard (true time → stream pos) on a 10 ms grid from 15 s on,
  //   excluding [300 s, 312 s]: p99 |Δpos| <= 48 samples
  // every remote client SyncStateEvaluator == InSync by 312 s
}
```

- [ ] **Step 2: Run** — `--filter "FullyQualifiedName~VirtualSessionTests"`. Expected: FAIL first (missing `FakeCapture`), then either PASS or real defects.
- [ ] **Step 3: Implement `FakeCapture`; fix any exposed defects** (root-cause with superpowers:systematic-debugging; add a unit test per fix).
- [ ] **Step 4: Run full default suite** — `dotnet test --filter "Category!=Hardware&Category!=Smoke"`. Expected: all PASS, test run < 2 min.
- [ ] **Step 5: Commit** — `test: virtual 4-output session with drift and outage`

---

### Task 11: UDP transport, discovery and localhost smoke test

**Files:**
- Create: `src/RhythmSync.Core/Net/UdpDatagramChannel.cs`, `DiscoveryBroadcaster.cs`, `DiscoveryListener.cs`
- Test: `tests/RhythmSync.Tests/Net/DiscoveryTests.cs`, `tests/RhythmSync.Tests/Integration/LocalhostSmokeTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class UdpDatagramChannel : IDatagramChannel { public UdpDatagramChannel(IPEndPoint bind, bool broadcast = false); }
public sealed class UdpDatagramChannelFactory : IDatagramChannelFactory
public sealed class DiscoveryBroadcaster { public DiscoveryBroadcaster(IDatagramChannel ch, IPEndPoint target, ISessionClock clock, Func<BeaconPacket> beacon); public void Tick(); } // sends once per second
public sealed record DiscoveredHost(uint SessionId, string Name, IPEndPoint ControlEndPoint, long LastSeenTicks);
public sealed class DiscoveryListener : IDisposable {
  public DiscoveryListener(IDatagramChannel ch, ISessionClock clock); public void Start();
  public IReadOnlyList<DiscoveredHost> Hosts { get; }   // heard within last 3 s; control endpoint = sender IP + beacon ControlPort
  public event Action? Changed;
}
```
`UdpDatagramChannel`: dedicated receive thread, 2048-byte buffer, disables Windows `SIO_UDP_CONNRESET` (`socket.IOControl(-1744830452, [0,0,0,0], null)`) so ICMP port-unreachable does not kill the loop; `SocketException` on receive is logged and the loop continues.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Listener_sees_and_expires_hosts()       // SimNetwork + ManualClock: beacon at t=0 → 1 host; t=3.1 s without beacons → 0; Changed raised both times
[Fact] public void Broadcaster_sends_once_per_second()     // Tick every 100 ms for 5 s → 5 beacons
[Fact, Trait("Category","Smoke")]
public void Localhost_session_plays_in_sync_for_30_s()
{ // real UdpDatagramChannel on 127.0.0.1 ephemeral ports, StopwatchClock, HostPipeline + one ClientPipeline + ClockSyncClient;
  // a background loop every 10 ms pushes 480 sine frames to OnCaptured and calls Render on a 480-frame buffer with timing from StopwatchClock
  // after 30 s: SyncStateEvaluator == InSync; Buffer.Late / Buffer.Received < 0.01; |SyncErrorMs| < 2
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/RhythmSync.Tests --filter "FullyQualifiedName~DiscoveryTests|FullyQualifiedName~LocalhostSmokeTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass** (same filter; the smoke test takes ~30 s).
- [ ] **Step 5: Commit** — `feat(core): UDP transport and LAN discovery`

---

### Task 12: Host session (control plane)

**Files:**
- Create: `src/RhythmSync.Core/Host/HostSession.cs`
- Test: `tests/RhythmSync.Tests/Host/HostSessionTests.cs`

**Interfaces:**
- Consumes: `ControlCodec`/messages (Task 2), `HostPipeline`/`Streamer` (Task 9), `ISessionClock`.
- Produces:
```csharp
public sealed record HostSessionOptions(string Name, int ControlPort = 47801, int AudioPort = 47802, int ClockPort = 47803,
  int TargetLatencyMs = 200, int MaxClients = 8, string? PairingCode = null);  // null → random 4 digits
public sealed record ConnectedClient(Guid DeviceId, string Name, IPEndPoint AudioEndPoint, bool Ready, HeartbeatStats? LastStats, float Volume, double OffsetMs);
public interface IHostControl { Task SetLatencyAsync(int ms); Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs); }
public sealed class HostSession : IHostControl, IAsyncDisposable {
  public const int ProtocolVersion = 1;
  public HostSession(HostSessionOptions options, ISessionClock clock, HostPipeline pipeline);
  public uint SessionId { get; } public string PairingCode { get; } public int TargetLatencyMs { get; }
  public int ControlPort { get; }   // actual bound port (options may pass 0 in tests)
  public Task StartAsync(CancellationToken ct);
  public IReadOnlyList<ConnectedClient> Clients { get; } public event Action? ClientsChanged;
  public Task SetDeviceSettingsAsync(Guid deviceId, float volume, double offsetMs);
  public Task SetLatencyAsync(int ms);   // clamp 100..500; sets Assembler.TargetLatencyMs; sends SetLatency to all
  public void Tick();                    // send Heartbeat every 1 s; drop clients silent > 3 s (RemoveClient, close socket)
}
```
Rules: wrong `ProtocolVersion` → `Reject(VersionMismatch)`; client count at max → `SessionFull`; wrong code → `BadCode`, and after 3 bad codes from one `DeviceId` that device gets `LockedOut` for 30 s (by `clock`) even with the right code. A `Hello` from an already-connected `DeviceId` replaces the old connection (reconnect). On `Ready` → `Streamer.AddClient(deviceId, new IPEndPoint(remoteIp, hello.AudioPort))`.

- [ ] **Step 1: Write the failing tests** (real TCP loopback, `ControlPort = 0`, raw `TcpClient` + `ControlCodec` as the client, `ManualClock`, `HostPipeline` over `SimNetwork` channels)

```csharp
[Fact] public async Task Join_returns_welcome_and_lists_client()   // code "1234" → Welcome(SessionId, 48000, 2, 240, 200, 47802, 47803); Clients.Count == 1
[Fact] public async Task Ready_adds_client_to_streamer()
[Fact] public async Task Bad_code_rejected()
[Fact] public async Task Lockout_after_three_bad_codes_expires_after_30s() // 4th with right code → LockedOut; clock +31 s → Welcome
[Fact] public async Task Version_mismatch_rejected()
[Fact] public async Task Session_full_rejected()                    // MaxClients = 1
[Fact] public async Task Silent_client_dropped_after_3s()           // Tick at +3.1 s → Clients empty, ClientsChanged raised
[Fact] public async Task Device_settings_and_latency_are_sent()     // SetDeviceSettingsAsync → client reads SetDeviceSettings(0.5, 10); SetLatencyAsync(900) → SetLatency(500); SetLatencyAsync(50) → 100
[Fact] public async Task Same_device_reconnect_replaces_connection()
```

- [ ] **Step 2: Run to verify failure** — `--filter "FullyQualifiedName~HostSessionTests"`
- [ ] **Step 3: Implement** (one `Task` per connection reading messages; writes serialized by a per-connection `SemaphoreSlim`).
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit** — `feat(core): host session control plane`

---

### Task 13: Client session and settings store

**Files:**
- Create: `src/RhythmSync.Core/Client/ClientSession.cs`, `src/RhythmSync.Core/Client/StatsWindow.cs`, `src/RhythmSync.Core/Config/AppSettings.cs`, `src/RhythmSync.Core/Config/SettingsStore.cs`
- Test: `tests/RhythmSync.Tests/Client/ClientSessionTests.cs`, `tests/RhythmSync.Tests/Client/StatsWindowTests.cs`, `tests/RhythmSync.Tests/Config/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `HostSession` (Task 12, used as the real counterpart in tests), `ClientPipeline`, `ClockSyncClient`, `ClockEstimator`, `SyncStateEvaluator`, `IAudioOutputFactory`, `IDatagramChannelFactory`.
- Produces:
```csharp
public sealed record ClientSessionOptions(Guid DeviceId, string DeviceName, int AudioPort = 47802);
public enum JoinOutcome { Joined, Rejected, Unreachable }
public sealed record JoinResult(JoinOutcome Outcome, RejectReason? Reason = null);
public sealed class ClientSession : IAsyncDisposable {
  public ClientSession(ClientSessionOptions o, ISessionClock clock, IDatagramChannelFactory channels, IAudioOutputFactory outputs);
  public Task<JoinResult> JoinAsync(IPEndPoint control, string pairingCode, CancellationToken ct);
  public SyncState State { get; } public event Action? StateChanged;
  public float Volume { get; set; } public double OffsetMs { get; set; }   // local edits; host SetDeviceSettings overwrites
  public PlaybackStats? Stats { get; }
  public void Tick();       // clock-sync tick; Ready once converged; Heartbeat(StatsWindow) every 1 s; host silent > 3 s → reconnect loop
  public Task LeaveAsync(); // sends Bye
}
public sealed class StatsWindow {   // trailing 10 s of counter deltas
  public void Add(long nowTicks, long received, long late, long concealed);
  public double LatePct { get; } public double LossPct { get; }  // late/received; concealed/(received + concealed)
}
public sealed record DeviceSettings(float Volume, double OffsetMs);
public sealed record AppSettings(Guid DeviceId, string DeviceName, string? LastHostName, string? LastPairingCode,
  int TargetLatencyMs, string? LocalSpeakerDeviceId, bool FirewallConfigured, Dictionary<Guid, DeviceSettings> Devices);
public sealed class SettingsStore {
  public SettingsStore(string directory);   // App passes %AppData%\RhythmSync
  public AppSettings Load();                // missing → defaults (new Guid, Environment.MachineName, 200 ms); corrupt → rename to settings.json.bak, defaults
  public void Save(AppSettings s);          // write settings.json.tmp then File.Move(overwrite: true)
}
```
Reconnect: on host silence > 3 s → scheduler starves (concealer fades), `State = Connecting`, retry `JoinAsync` with the remembered code every 2 s; after 60 s without success → `Disconnected`. On `IAudioOutput.DeviceInvalidated` → dispose output, `outputs.Create()`, `pipeline.ReplaceOutputRate(newRate)`, restart.

- [ ] **Step 1: Write the failing tests** (real `HostSession` on loopback TCP + `UdpDatagramChannelFactory` on 127.0.0.1, `StopwatchClock`; fake `IAudioOutputFactory` returning a no-op output with a settable `SampleRate` and a `RaiseInvalidated()` method)

```csharp
[Fact] public async Task Join_reaches_InSync()                 // tick every 10 ms; InSync within 5 s; host Clients[0].Ready
[Fact] public async Task Rejection_is_reported()              // wrong code → JoinResult(Rejected, BadCode)
[Fact] public async Task Unreachable_host_is_reported()       // closed port → Unreachable
[Fact] public async Task Host_settings_apply_locally()        // host SetDeviceSettingsAsync(0.5, 10) → Volume 0.5, OffsetMs 10 within 1 s
[Fact] public async Task Reconnects_after_host_restart()      // dispose host, start new HostSession same port + code → State passes Connecting, back to InSync within 15 s
[Fact] public async Task Gives_up_after_60s()                 // ManualClock variant: host gone, advance 61 s with Ticks → Disconnected
[Fact] public async Task Output_device_change_rebuilds_output() // RaiseInvalidated with new SampleRate 44100 → factory called twice; scheduler uses 44100
[Fact] public void StatsWindow_uses_trailing_10s()            // late burst at t=0, clean 11 s after → LatePct == 0
[Fact] public void Settings_round_trip_and_keep_device_id()
[Fact] public void Corrupt_settings_backed_up_and_defaulted()
```

- [ ] **Step 2: Run to verify failure** — `--filter "FullyQualifiedName~ClientSessionTests|FullyQualifiedName~StatsWindowTests|FullyQualifiedName~SettingsStoreTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass**, then the full default suite.
- [ ] **Step 5: Commit** — `feat(core): client session, stats window and settings store`

---

### Task 14: WASAPI render output

**Files:**
- Create: `src/RhythmSync.Platform.Windows/RhythmSync.Platform.Windows.csproj`, `WasapiRenderOutput.cs`, `WasapiOutputFactory.cs`, `DeviceCatalog.cs`, `DeviceWatcher.cs`, `Mmcss.cs`, `ChannelMapper.cs`
- Test: `tests/RhythmSync.Platform.Windows.Tests/` (`ChannelMapperTests.cs`, `WasapiRenderOutputTests.cs`)

**Interfaces:**
- Consumes: `IAudioOutput`, `IRenderCallback`, `RenderTiming`, `IAudioOutputFactory` (Task 7).
- Produces:
```csharp
public sealed record AudioDeviceInfo(string Id, string Name);
public static class DeviceCatalog {
  public static IReadOnlyList<AudioDeviceInfo> RenderDevices();
  public static string? DefaultRenderId();
  public static AudioDeviceInfo? FindVbCableRender();   // name contains "CABLE Input"
}
public sealed class WasapiRenderOutput : IAudioOutput { public WasapiRenderOutput(string? deviceId /* null = default */); }
public sealed class WasapiOutputFactory : IAudioOutputFactory { public WasapiOutputFactory(Func<string?> deviceId); }
public sealed class DeviceWatcher : IDisposable { public event Action? DefaultRenderChanged; public event Action<string>? DeviceRemoved; }
public static class Mmcss { public static IDisposable JoinProAudio(); }   // AvSetMmThreadCharacteristics("Pro Audio") / AvRevert…
public static class ChannelMapper { public static void StereoToDevice(ReadOnlySpan<float> stereo, Span<float> dst, int deviceChannels); } // L,R to ch0,ch1; others 0; mono = (L+R)/2
```
Implementation notes: NAudio 2.2.1 `AudioClient` in shared, event-driven mode, 10 ms buffer, device mix format (float). Render thread joins MMCSS. Each event: padding → frames to write; `AudioClockClient.GetPosition(out pos, out qpc)`; `DevicePositionFrames = pos·mixRate/AudioClockClient.Frequency`; `qpc` is already 100 ns (matches `StopwatchClock`). `AUDCLNT_E_DEVICE_INVALIDATED` from any call, or `DeviceWatcher` default-change for a default-device output, raises `DeviceInvalidated` once.

- [ ] **Step 1: Scaffold** — `dotnet new classlib -n RhythmSync.Platform.Windows -o src/RhythmSync.Platform.Windows -f net10.0-windows`, `dotnet add … package NAudio --version 2.2.1`, reference Core; xunit test project `tests/RhythmSync.Platform.Windows.Tests` (`net10.0-windows`); add both to the sln.
- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void Stereo_to_six_channels_puts_LR_first_and_zeros_rest()
[Fact] public void Stereo_to_mono_averages()
[Fact, Trait("Category","Hardware")]
public void Renders_and_reports_sane_timing()  // default device, 2 s of silence; positions non-decreasing; |DevicePositionClientTicks − StopwatchClock.NowTicks| < 500_000; WritePosition ≥ DevicePosition
```
- [ ] **Step 3: Run to verify failure** — `dotnet test tests/RhythmSync.Platform.Windows.Tests`
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run to verify pass** — including hardware: `dotnet test tests/RhythmSync.Platform.Windows.Tests --filter "Category=Hardware"` on a PC with speakers.
- [ ] **Step 6: Commit** — `feat(windows): event-driven WASAPI render output`

---

### Task 15: WASAPI loopback capture and host audio checks

**Files:**
- Create: `src/RhythmSync.Platform.Windows/WasapiLoopbackSource.cs`, `SilenceKeeper.cs`, `Downmixer.cs`, `HostAudioCheck.cs`
- Test: `tests/RhythmSync.Platform.Windows.Tests/DownmixerTests.cs`, `HostAudioCheckTests.cs`, `WasapiLoopbackSourceTests.cs`

**Interfaces:**
- Consumes: `WasapiRenderOutput`, `DeviceCatalog`, `Mmcss` (Task 14); `IAudioCapture`, `CaptureHandler`, `HostPipeline.OnCaptured` (Task 9).
- Produces:
```csharp
public sealed class WasapiLoopbackSource : IAudioCapture { public WasapiLoopbackSource(string? renderDeviceId /* null = default */); }
public sealed class SilenceKeeper : IDisposable { public SilenceKeeper(string? renderDeviceId); public void Start(); } // renders zeros so loopback never stalls
public static class Downmixer { public static void ToStereo(ReadOnlySpan<float> src, int channels, Span<float> dst); }
  // 1 ch → duplicate; 2 → copy; 6 (FL FR C LFE SL SR) → L = FL + .707C + .707SL, R = FR + .707C + .707SR (LFE dropped); other → ch0/ch1
public enum HostAudioStatus { Ok, VbCableMissing, VbCableNotDefault, LocalSpeakerIsCaptured }
public static class HostAudioCheck { public static HostAudioStatus Evaluate(string? defaultRenderId, string? vbCableId, string? localSpeakerId); }
```
Loopback uses NAudio `AudioClient` with `AudioClientStreamFlags.Loopback | EventCallback` on the render device and `AudioCaptureClient.GetBuffer(out frames, out flags, out devicePosition, out qpcPosition)`; `Silent` flag → zeros; downmix to stereo; capture thread joins MMCSS.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Downmix_5_1_follows_ITU_coefficients()
[Fact] public void Downmix_mono_duplicates()
[Theory] // (default, vbCable, local) → status
[InlineData(null, null, "spk", HostAudioStatus.VbCableMissing)]
[InlineData("spk", "cab", "spk", HostAudioStatus.VbCableNotDefault)]
[InlineData("cab", "cab", "cab", HostAudioStatus.LocalSpeakerIsCaptured)]
[InlineData("cab", "cab", "spk", HostAudioStatus.Ok)]
public void Host_audio_status(string? def, string? cab, string? local, HostAudioStatus expected)
[Fact, Trait("Category","Hardware")]
public void Loopback_delivers_continuously_with_silence_keeper() // default device, SilenceKeeper running, 2 s → ≥ 180 callbacks, device positions strictly increasing, |qpc − StopwatchClock.NowTicks| < 500_000
```
- [ ] **Step 2: Run to verify failure.** — `dotnet test tests/RhythmSync.Platform.Windows.Tests --filter "FullyQualifiedName~Downmixer|FullyQualifiedName~HostAudioCheck"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass** (+ hardware filter on a real PC).
- [ ] **Step 5: Commit** — `feat(windows): loopback capture, silence keeper, host audio checks`

---

### Task 16: App shell and client mode

**Files:**
- Create: `src/RhythmSync.App/` (`RhythmSync.App.csproj` with `UseWPF` + `UseWindowsForms`, `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `Views/ClientView.xaml`, `ViewModels/ClientViewModel.cs`, `Services/TrayIcon.cs`, `Services/FirewallSetup.cs`, `Services/NetworkProfile.cs`, `Services/SessionTicker.cs`)
- Test: `tests/RhythmSync.App.Tests/ClientViewModelTests.cs`

**Interfaces:**
- Consumes: `ClientSession`, `DiscoveryListener`, `SettingsStore` (Core); `WasapiOutputFactory` (Task 14); `UdpDatagramChannelFactory`, `StopwatchClock`.
- Produces:
```csharp
public partial class ClientViewModel : ObservableObject {   // CommunityToolkit.Mvvm
  public ClientViewModel(Func<IReadOnlyList<DiscoveredHost>> hosts, Func<IPEndPoint, string, Task<JoinResult>> join);
  public ObservableCollection<DiscoveredHost> Hosts { get; } public DiscoveredHost? SelectedHost { get; set; }
  public string Code { get; set; } public IAsyncRelayCommand JoinCommand { get; }   // enabled iff SelectedHost != null && Code is 4 digits
  public string? ErrorText { get; }  public SyncState State { get; set; } public float Volume { get; set; } public double OffsetMs { get; set; }
  public static string RejectText(RejectReason r);
}
public sealed class SessionTicker : IDisposable { public SessionTicker(Action tick); }  // PeriodicTimer 10 ms on a background task
public static class FirewallSetup { public static bool EnsureRules(string exePath); }   // one elevated `netsh advfirewall firewall add rule name="RhythmSync" dir=in action=allow program="<exe>" profile=private` (Verb=runas)
public static class NetworkProfile { public static bool AnyConnectedPublic(); }       // COM NetworkListManager {DCB00C01-570F-4A9B-8D69-199FDBA5723B}, GetNetworks(Connected), category 0 = Public
```
Exact copy: `RejectText` → BadCode "Wrong code", LockedOut "Too many attempts – wait 30 s", VersionMismatch "Host runs a different RhythmSync version", SessionFull "Session is full"; Unreachable → "Can't reach host". Public-network banner: "This Wi-Fi is set to Public – set it to Private so other PCs can connect." Startup: Serilog rolling daily file `%AppData%\RhythmSync\logs\rhythmsync-.log` (7 retained), `GCSettings.LatencyMode = SustainedLowLatency`, settings load, first-run firewall (store `FirewallConfigured`), tray icon (Show/Hide, status text, Quit), closing the window hides to tray.

- [ ] **Step 1: Scaffold** the WPF project and `tests/RhythmSync.App.Tests` (`net10.0-windows`), add packages `CommunityToolkit.Mvvm`, `Serilog`, `Serilog.Sinks.File`; add to sln.
- [ ] **Step 2: Write the failing tests**

```csharp
[Fact] public void Join_disabled_until_host_and_four_digits()   // "12a4", "123" → disabled; "1234" + host → enabled
[Theory] /* each RejectReason → exact RejectText copy above */
[Fact] public async Task Rejected_join_sets_error_text()        // join returns Rejected(BadCode) → ErrorText "Wrong code"
[Fact] public async Task Unreachable_sets_error_text()          // → "Can't reach host"
```
- [ ] **Step 3: Run to verify failure** — `dotnet test tests/RhythmSync.App.Tests`
- [ ] **Step 4: Implement** VM, views, services and composition root.
- [ ] **Step 5: Run to verify pass**; then `dotnet run --project src/RhythmSync.App` and confirm the window, tray icon and host list appear.
- [ ] **Step 6: Commit** — `feat(app): WPF shell, tray and client mode`

---

### Task 17: App host mode and stats panel

**Files:**
- Create: `src/RhythmSync.App/Views/HostView.xaml`, `ViewModels/HostViewModel.cs`, `ViewModels/ClientRowViewModel.cs`, `Views/StatsPanel.xaml`
- Modify: `src/RhythmSync.App/MainWindow.xaml` (Host tab), `App.xaml.cs` (host composition)
- Test: `tests/RhythmSync.App.Tests/HostViewModelTests.cs`

**Interfaces:**
- Consumes: `HostSession`/`IHostControl` (Task 12), `HostPipeline`, `DiscoveryBroadcaster`, `ClientPipeline` + `IdentityClockMapping` (local playback via `Streamer.LocalSink`), `WasapiLoopbackSource`, `SilenceKeeper`, `WasapiRenderOutput`, `DeviceCatalog`, `DeviceWatcher`, `HostAudioCheck`, `SettingsStore`/`DeviceSettings` (Task 13).
- Produces:
```csharp
public partial class HostViewModel : ObservableObject {
  public HostViewModel(IHostControl host, Func<HostAudioStatus> audioStatus,
                       Func<Guid, DeviceSettings?> storedSettings, Action<Guid, DeviceSettings> saveSettings);
  public void RefreshAudioStatus();                        // called on DeviceWatcher.DefaultRenderChanged
  public string SessionName { get; set; } public string PairingCode { get; }
  public int LatencyMs { get; set; }                       // 100..500 step 10; change → SetLatencyAsync
  public string? AudioBanner { get; }                      // from HostAudioStatus
  public IRelayCommand OpenSoundSettingsCommand { get; }   // Process.Start("ms-settings:sound")
  public ObservableCollection<ClientRowViewModel> Clients { get; }
  public bool ShowStats { get; set; }
  public void UpdateClients(IReadOnlyList<ConnectedClient> clients);
}
public partial class ClientRowViewModel : ObservableObject {
  public Guid DeviceId { get; } public string Name { get; } public SyncState State { get; } public double SyncErrorMs { get; }
  public float Volume { get; set; } public double OffsetMs { get; set; }   // −100..100; change → SetDeviceSettingsAsync
  public bool Congested { get; }                                           // LatePct > 5
  public double LossPct, LatePct, DriftPpm, FitResidualMs { get; }
}
```
Exact banner copy: VbCableMissing "VB-CABLE not found – this PC will only send audio. Mute its speakers or install VB-CABLE."; VbCableNotDefault "Set 'CABLE Input' as the default playback device so this PC plays in sync."; LocalSpeakerIsCaptured "Pick real speakers for local playback – not CABLE Input."; Ok → null. Congested row text: "Wi-Fi congested – try higher latency". Host local playback is not started when status is `LocalSpeakerIsCaptured` or `VbCableMissing`.

- [ ] **Step 1: Write the failing tests** (fake `IHostControl` recording calls)

```csharp
[Theory] /* each HostAudioStatus → exact AudioBanner */
[Fact] public async Task Latency_change_is_sent()            // LatencyMs = 300 → SetLatencyAsync(300)
[Fact] public async Task Row_offset_change_is_sent()         // row.OffsetMs = 12.5 → SetDeviceSettingsAsync(id, volume, 12.5)
[Fact] public void Congested_when_late_over_5_percent()       // LastStats.LatePct 6 → Congested
[Fact] public void UpdateClients_adds_updates_and_removes_rows()
[Fact] public async Task New_client_gets_stored_settings()   // storedSettings(id) = (0.7, −5) → new row sends SetDeviceSettingsAsync(id, 0.7, −5)
[Fact] public async Task Row_change_is_saved()               // row.Volume = 0.3 → saveSettings(id, (0.3, offset))
[Fact] public void Refresh_updates_banner()                  // status func flips Ok → VbCableNotDefault; RefreshAudioStatus() → banner text set
```
- [ ] **Step 2: Run to verify failure** — `dotnet test tests/RhythmSync.App.Tests --filter "FullyQualifiedName~HostViewModelTests"`
- [ ] **Step 3: Implement** VM, views and host composition (capture → `HostPipeline.OnCaptured`; `SessionTicker` drives `HostSession.Tick` and `DiscoveryBroadcaster.Tick`).
- [ ] **Step 4: Run to verify pass**; then run the app as Host on one PC and Client on another (or the same PC with two instances on different audio ports) and confirm the client reaches *In sync*.
- [ ] **Step 5: Commit** — `feat(app): host mode, client controls and stats panel`

---

### Task 18: Acceptance checklist and README

**Files:**
- Create: `docs/acceptance.md`, `README.md`

- [ ] **Step 1: Write `README.md`** — what RhythmSync does, requirements (Windows 10/11, .NET 10 SDK, VB-CABLE on the host), build (`dotnet build`), test (`dotnet test --filter "Category!=Hardware&Category!=Smoke"`), run, host setup (install VB-CABLE, set *CABLE Input* as default output, choose local speakers), firewall/Private-network note, known limitations (unencrypted, no Bluetooth speakers, Windows only).
- [ ] **Step 2: Write `docs/acceptance.md`** — the spec §8.4 procedure as checkboxes, one per success criterion in spec §1.2: join ≤ 10 s; any app's audio on all PCs incl. host; click-track alignment ≤ 2 ms at 0/30/60 min (phone at midpoint, Audacity onset gap) after one offset tuning; 1 h listen with no clicks/dropouts; headphone unplug recovery; 5 s host Wi-Fi off recovery. Each row has a "measured" column.
- [ ] **Step 3: Verify** — `dotnet build -c Release` succeeds with 0 warnings; `dotnet test --filter "Category!=Hardware&Category!=Smoke"` all pass; `dotnet test --filter "Category=Smoke"` passes.
- [ ] **Step 4: Commit** — `docs: README and hardware acceptance checklist`
