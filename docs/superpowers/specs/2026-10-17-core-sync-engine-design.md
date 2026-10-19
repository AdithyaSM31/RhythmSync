# RhythmSync — Sub-project 1: Core Sync Engine — Design

**Date:** 2026-10-17
**Status:** Draft, awaiting review
**Stack:** C# / .NET 10 (LTS), WPF, NAudio

## 1. Purpose

RhythmSync is a Windows app that lets several PCs on the same Wi-Fi play the host PC's
audio in tight sync, so the PCs act as one multi-speaker system.

The full vision has four sub-projects, each with its own spec → plan → build cycle:

| # | Sub-project | Depends on |
|---|-------------|-----------|
| **1** | **Core sync engine** (this spec) | — |
| 2 | EQ (per-device / global) | 1 (`DspChain`) |
| 3 | Auto-sync calibration with a microphone | 1 (per-device offset) |
| 4 | Spatial audio / room layout of speakers | 1, 3 (channel routing, offset, gain) |

### 1.1 Agreed requirements

- **Use case:** music. Latency of ~200 ms between source and sound is acceptable.
- **Source:** all host system audio (any app), captured via WASAPI loopback.
- **Scale:** 2–4 PCs including the host.
- **Placement:** same room → PCs must play within **≤ 2 ms** of each other or it sounds like an echo.
- **Platform:** Windows 10/11 only. LAN only; no internet/cloud.

### 1.2 Success criteria (v1 is done when)

1. 2–4 PCs join a session with a pairing code and are playing within **10 s**.
2. Audio from any app on the host plays on every PC, including the host itself (via VB-CABLE).
3. After a one-time manual offset tuning, inter-PC alignment stays **≤ 2 ms for 1 hour**, measured acoustically.
4. **No audible clicks or dropouts in 1 hour** on normal home Wi-Fi (< 1% packet loss).
5. Recovers unaided from a client output-device change and a ≤ 5 s host Wi-Fi drop.

### 1.3 Out of scope for v1

EQ, mic calibration, spatial/room map, channel routing (every PC plays full stereo), encryption,
compression (Opus), multicast, non-Windows clients, low-latency/video mode, Bluetooth speakers
(their variable 100–300 ms latency breaks same-room sync; they are allowed but not supported).

## 2. Key decisions

| Decision | Choice | Why |
|----------|--------|-----|
| Sync approach | Custom timestamped UDP stream + NTP-style software clock sync + resampling drift correction | Full control, reaches same-room target; alternatives (Snapcast wrap, plain TCP) rejected — Linux-first / no clock sync |
| Host echo problem | **Require VB-CABLE** (free virtual audio driver) set as Windows default output on the host. Without it the host is sender-only and its speakers must be muted | Otherwise the host hears the original undelayed audio plus our delayed copy. Writing our own driver is out of scope |
| Wire format | Uncompressed 48 kHz / 16-bit / stereo PCM, 5 ms frames | 960-byte payload fits one UDP packet; ~1.7 Mbps per client is trivial at this scale |
| Transport | Unicast UDP per client | 2–4 clients; avoids router multicast problems |
| Target latency | 200 ms default, host-wide, adjustable 100–500 ms | Absorbs Wi-Fi jitter with margin |

## 3. Architecture

### 3.1 Solution layout

```
RhythmSync.sln
src/
  RhythmSync.Core/              net10.0 — no Windows APIs; all logic, fully unit-testable
    Protocol/                   packet types + binary serializer, control messages
    Clock/                      ISessionClock, ClockEstimator, ClockSyncServer, ClockSyncClient
    Audio/                      IAudioCapture, IAudioOutput, IAudioProcessor, DspChain,
                                GainProcessor, CubicResampler, Concealer
    Host/                       CaptureTimestamper, Streamer, HostSession
    Client/                     JitterBuffer, PlaybackScheduler, ClientSession
    Net/                        INetworkTransport, UdpTransport, Discovery, ControlChannel
  RhythmSync.Platform.Windows/  net10.0-windows — WASAPI implementations via NAudio
                                WasapiLoopbackCapture, WasapiRenderOutput, SilenceKeeper,
                                DeviceWatcher, FirewallSetup, MmcssThread
  RhythmSync.App/               net10.0-windows — WPF + tray icon; Host and Client modes
tests/
  RhythmSync.Tests/             xUnit; Sim/ holds SimNetwork, DriftingClock, FakeOutput, FakeCapture
```

`Core` depends on nothing platform-specific. `Platform.Windows` implements `Core`'s interfaces.
`App` wires them together.

### 3.2 Components

| Unit | Runs on | Responsibility | Depends on |
|------|---------|----------------|-----------|
| `ISessionClock` | Both | Monotonic clock in 100 ns ticks: QPC converted to 100 ns, the same epoch and unit WASAPI reports in `qpcPosition`. On the host this *is* session time | — |
| `LoopbackCapture` | Host | Captures the default render endpoint (= VB-CABLE) via WASAPI loopback; converts to 48 kHz stereo float; reports each buffer with its device sample position + QPC time | NAudio |
| `SilenceKeeper` | Host | Plays continuous silence into the captured endpoint so loopback keeps delivering buffers when nothing is playing (keeps the timeline continuous) | NAudio |
| `CaptureTimestamper` | Host | Fits `sessionTime = f(sampleIndex)` over a 30 s sliding window of (position, QPC) pairs; stamps each 5 ms frame `playAt = f(firstSample) + targetLatency` | `ISessionClock` |
| `Streamer` | Host | Converts to 16-bit with TPDF dither, packs frames, sends to every client in `Ready` state | `INetworkTransport` |
| `ClockSyncServer` | Host | Answers pings with host receive/send timestamps | `ISessionClock` |
| `ClockSyncClient` | Client | Sends pings (50 ms interval for first 2 s, then 250 ms); feeds `ClockEstimator` | — |
| `ClockEstimator` | Client | Maps client time ↔ host time: keeps 60 s window, takes the min-RTT sample of each time bucket, RTT-weighted least-squares fit `host = a + b·client`; exposes offset, drift (ppm), fit residual, standard error | — (pure) |
| `JitterBuffer` | Client | Stores frames by `seq`; reorders; reports missing/late frames; serves samples by host-time position | — (pure) |
| `PlaybackScheduler` | Client | On each output callback: computes when the next written sample will be heard (in host time), compares with where the stream should be, drives the resampler ratio (PI) or hard-resyncs | `ClockEstimator`, `JitterBuffer`, `CubicResampler`, `Concealer` |
| `CubicResampler` | Client | 4-point cubic Hermite fractional resampler, ratio adjustable per callback within ±500 ppm | — (pure) |
| `Concealer` | Client | Repeat-with-crossfade for single lost frames; fade-out/fade-in around longer gaps and resyncs | — (pure) |
| `DspChain` | Client | Ordered list of `IAudioProcessor`. v1 contains `GainProcessor` only. EQ plugs in here (sub-project 2) | — |
| `WasapiRenderOutput` | Client | Event-driven shared-mode WASAPI render on an MMCSS "Pro Audio" thread; exposes `IAudioClock` position+QPC so the scheduler knows which sample is at the device at what time | NAudio |
| `DeviceWatcher` | Both | Detects default-device changes / unplug; triggers output rebuild (client) or VB-CABLE warning (host) | NAudio |
| `Discovery` | Both | Host broadcasts beacon every 1 s; client lists hosts heard in last 3 s | `INetworkTransport` |
| `ControlChannel` | Both | TCP; length-prefixed JSON messages (§4.3): join, heartbeat, per-device settings | — |
| `HostSession` / `ClientSession` | Both | Orchestrate lifecycle and state machines; the host also runs an in-process `ClientSession` (with an identity clock estimator, offset 0) for its own speakers | all above |

The **per-device delay offset** is not a DSP stage: it is added to the target host time inside
`PlaybackScheduler`, so it can be negative (play earlier) within the latency budget. The effective offset is clamped to
`≥ −(targetLatencyMs − 50)` so a client never asks for audio that has not arrived yet. Calibration
(sub-project 3) will write this same value.

An offset change made by the user (a `SetDeviceSettings` from the host's slider, ±1 ms buttons or typed value) that moves
the effective offset by more than **0.25 ms** forces one hard resync on the next render (§5.4 step 5: fade out, jump, fade in),
so it is heard at once; smaller changes are slewed by the PI loop. Automatic drift correction and latency re-clamps never
force a resync (a latency step is handled as in §6). The host UI sends one change per drag (the slider commits 300 ms after
the last movement), so tuning by ear does not cause a resync per slider step.

## 4. Protocol

All binary fields little-endian. Times are `i64` in 100 ns ticks of host session time unless noted.

### 4.1 UDP packets (common header)

| Field | Type | Notes |
|-------|------|-------|
| magic | u16 | `0x5253` ("RS") |
| version | u8 | `1` |
| type | u8 | 1 = Audio, 2 = Ping, 3 = Pong, 4 = Beacon |
| sessionId | u32 | random per host session; packets with wrong id are dropped |

**Audio (type 1)** — `seq u32`, `playAt i64`, `frameCount u16` (= 240), `pcm` (240 × 2 ch × i16 = 960 B).
Total 982 B (22 B header + 960 B PCM).

**Ping (type 2)** — `pingId u32`, `t1 i64` (client clock).
**Pong (type 3)** — `pingId u32`, `t1 i64` (echoed), `t2 i64`, `t3 i64` (host receive/send).

**Beacon (type 4)** — `controlPort u16`, `protocolVersion u8`, `nameLen u8`, `name` (UTF-8, ≤ 64 B).

Malformed, truncated, wrong-magic or wrong-version packets are dropped and counted.

### 4.2 Ports (defaults, configurable)

UDP 47800 discovery · TCP 47801 control · UDP 47802 audio · UDP 47803 clock.

### 4.3 Control messages (TCP, `u32 length` + UTF-8 JSON, `type` field discriminates)

| Message | Direction | Payload |
|---------|-----------|---------|
| `Hello` | C→H | deviceId (GUID, persisted), deviceName, pairingCode, protocolVersion, audioPort (client's UDP port for audio) |
| `Welcome` | H→C | sessionId, sampleRate 48000, channels 2, frameSamples 240, targetLatencyMs, audioPort, clockPort |
| `Reject` | H→C | reason: `BadCode` \| `LockedOut` \| `VersionMismatch` \| `SessionFull` (max 8) |
| `Ready` | C→H | — (sent once clock estimate converges) |
| `Heartbeat` | both | stats: lossPct, latePct, driftPpm, fitResidualMs, syncErrorMs (C→H only) |
| `SetDeviceSettings` | H→C | volume (0–1), offsetMs (−100…+100) |
| `SetLatency` | H→C | targetLatencyMs |
| `Bye` | both | — |

## 5. Data flow

### 5.1 Join

1. Host starts → creates sessionId and 4-digit pairing code (shown in UI) → starts beacons.
2. Client lists hosts → user selects one and enters code → `Hello`.
3. Host validates → `Welcome` (or `Reject`; 3 bad codes from one deviceId → 30 s lockout).
4. Client starts fast clock sync. When the clock estimate is converged (≥ 20 selected samples and standard error < 0.5 ms, §5.2) → `Ready`.
5. Host adds client to `Streamer`. Client buffers until the first frame's `playAt` is reached, then plays.

### 5.2 Clock sync

For each ping: `offset = ((t2 − t1) + (t3 − t4)) / 2`, `rtt = (t4 − t1) − (t3 − t2)`.
`ClockEstimator` keeps a 60 s window, selects samples (see below), and fits
`hostTime = a + b · clientTime`. `b − 1` is relative drift (typically 10–100 ppm).
Converged = ≥ 20 selected samples and standard error of the fitted host time at the latest sample < 0.5 ms; selection = per-time-bucket min-RTT, fit = RTT-weighted least squares.

### 5.3 Host audio path

```
apps ─► VB-CABLE (default output) ─► LoopbackCapture ─► CaptureTimestamper ─► Streamer ─► UDP
                                                              │
                         in-process ClientSession ◄───────────┘ (same frames, local)
                                    └─► real speakers (user-selected; must not be VB-CABLE)
```

`CaptureTimestamper` uses the fitted capture-rate line, not callback arrival times, so `playAt`
advances exactly with the capture device's real sample clock and long sessions never drift the
buffer. Non-48 kHz capture formats are resampled to 48 kHz; >2 channels are downmixed to stereo.

### 5.4 Client playback loop

On each WASAPI render event (10 ms period):

1. Read device clock: sample `p` is at the device at client time `q`.
2. Next sample to write has index `w`; it will be heard at `q + (w − p) / 48000` (client time).
3. Convert to host time with `ClockEstimator`, then add per-device `offsetMs`.
4. The stream position that *should* be heard then is derived from frame `playAt`s; the position
   the resampler will actually emit is known. `error = should − actual` (ms).
5. If `|error| > 20 ms` → hard resync: fade out 5 ms, jump read position, fade in 5 ms.
   Otherwise → PI controller sets resampler ratio, clamped to ±500 ppm.
6. Pull samples through `CubicResampler` ← `JitterBuffer` (+ `Concealer` for gaps) → `DspChain` → device.

Steady-state target: |error| < 0.5 ms. Remaining per-device acoustic latency (DAC, speaker)
is covered by `offsetMs`.

### 5.5 Real-time hygiene

Audio threads registered with MMCSS ("Pro Audio"). No allocations in the audio path
(`ArrayPool`, `Span<T>`, preallocated buffers). `GCSettings.LatencyMode = SustainedLowLatency`.

## 6. Error handling

| Situation | Behaviour |
|-----------|-----------|
| One frame lost | `Concealer` repeats previous 5 ms with crossfade |
| Gap > 50 ms | Fade to silence; fade in when data resumes |
| Frame arrives after its `playAt` | Dropped, counted. > 5% late over 10 s → UI: "Wi-Fi congested – try higher latency" |
| Clock estimate standard error > 1 ms, or no new samples for 10 s | Keep playing on last drift estimate; state `Degraded` |
| 3 missed heartbeats (host gone) | Fade out; auto-reconnect for 60 s reusing deviceId (no code re-entry); then `Disconnected` |
| 3 missed heartbeats (client gone) | Host stops streaming to it, marks offline |
| `SetLatency` received | Frames' `playAt` jump → handled as hard resync |
| Client output device changes | Rebuild `WasapiRenderOutput`, hard resync |
| Host default output no longer VB-CABLE | Warning banner with button opening Sound settings; streaming continues |
| Host local speaker device == captured device | Refuse to start local playback; explain |
| VB-CABLE not installed | Host can run sender-only; UI explains and links to install guide |
| Network profile is Public | Warning; firewall rule is private-only |
| Malformed packet | Dropped, counted |

States shown per client: `Connecting → Syncing → InSync ⇄ Degraded → Disconnected`.

## 7. Configuration & UI (v1)

**First run:** add Windows Firewall rules (private profile only) for the app (requires one UAC prompt).

**Host screen:** session name, pairing code, latency slider (100–500 ms, default 200), VB-CABLE status,
local speaker device picker, client list with state, live sync error (ms), volume, offset slider (±100 ms).

**Client screen:** discovered hosts, code entry, state, own volume and offset.

**Tray icon:** show/hide, current state, quit.

**Stats panel (toggle):** loss %, late %, drift ppm, fit residual, sync error.

**Storage:** `%AppData%\RhythmSync\settings.json` (deviceId, name, last host, per-device volume/offset
keyed by deviceId); logs via Serilog rolling file in `%AppData%\RhythmSync\logs`.

**Known limitation:** traffic is unencrypted (home LAN, music only).

## 8. Testing

All hardware/network dependencies are behind interfaces (`ISessionClock`, `IAudioCapture`,
`IAudioOutput`, `INetworkTransport`) so `Core` is tested without hardware.

### 8.1 Unit tests (xUnit)

- **ClockEstimator:** synthetic pings with known offset and drift plus heavy-tailed, asymmetric delay
  → offset error < 0.2 ms for every seed; drift error < 8 ppm (p95 < 4 ppm) over 50 seeds — drift only matters for extrapolation, and 8 ppm × a 10 s gap = 0.08 ms.
- **PlaybackScheduler + PI loop:** `FakeOutput` consuming at +100 ppm → converges within 10 s,
  then |error| < 0.5 ms.
- **JitterBuffer:** reordering, duplicates, loss, late frames, gaps > 50 ms.
- **Concealer / resync:** output has no sample-to-sample jump above a threshold.
- **CubicResampler:** ratio 1.0 is transparent; ±500 ppm keeps a 1 kHz tone's THD+N below −80 dB.
- **CaptureTimestamper:** capture clock drifting vs session clock → `playAt` spacing tracks it.
- **Protocol:** serializer round-trips; truncated/garbage input rejected without exceptions escaping.
- **Control flow:** bad code, lockout, version mismatch, reconnect without code.

### 8.2 Virtual session integration test

Host + 3 clients in one process over `SimNetwork` (configurable jitter, loss, reordering), each with
its own `DriftingClock` (±100 ppm) and `FakeOutput`. Simulate 10 minutes faster than real time,
including a 2 s total outage. Assert pairwise output alignment ≤ 1 ms at p99 outside resync windows,
and recovery to `InSync` within 10 s of the outage ending.

### 8.3 Localhost smoke test

Same scenario with real `UdpTransport` on 127.0.0.1, 30 s, one client.

### 8.4 Hardware acceptance (manual checklist, `docs/acceptance.md`)

Two laptops side by side; host plays a click track; phone records from the midpoint; measure click-onset
gap in Audacity. Tune offset once; re-measure at 0, 30, 60 min → each ≤ 2 ms. Also: 1 h listen for
clicks; unplug headphones on a client; toggle host Wi-Fi off for 5 s.

## 9. Extension points for later sub-projects

- **EQ (2):** add an `IAudioProcessor` to `DspChain`; settings carried by `SetDeviceSettings`. Built: see the [EQ design](2026-10-18-eq-design.md).
- **Calibration (3):** writes per-device `offsetMs` (and gain); needs a "play test signal" control message.
- **Spatial (4):** adds a channel-routing stage before `DspChain` and room positions in host settings;
  uses offset + gain per device.
