<p align="center">
  <img src="src/RhythmSync.App/Assets/RhythmSync-256.png" width="128" height="128" alt="RhythmSync logo: a violet-to-teal sync wave on a dark rounded tile">
</p>

<h1 align="center">RhythmSync</h1>

<p align="center"><b>One PC's audio, every PC's speakers, in sync.</b></p>

<p align="center"><a href="https://github.com/AdithyaSM31/RhythmSync/releases/latest">Download the latest RhythmSync.exe</a> (64-bit Windows 10/11, no .NET install needed)</p>

RhythmSync plays one Windows PC's audio on several PCs on the same Wi-Fi, in sync. The host captures its system audio (any app) with WASAPI loopback and streams it over UDP with timestamps. Each client syncs its clock to the host and plays every frame at the same moment, so PCs in one room act as a multi-speaker system. The goal is under 2 ms between PCs.

This is the core sync engine (sub-project 1) with a per-device EQ (sub-project 2). Microphone calibration and room layout are not built yet.

## Requirements

- Windows 10 or 11, all PCs on the same LAN (Wi-Fi network profile set to Private).
- .NET 10 SDK to build and run from source.
- Host only: [VB-CABLE](https://vb-audio.com/Cable/) (free virtual audio device), so the host can play in sync with the clients. Without it the host is sender-only and its own speakers must be muted.

## Build, test, run

```
dotnet build -c Release
dotnet run --project src/RhythmSync.App
```

Solution: `RhythmSync.sln`.

| Project | Contents |
|---------|----------|
| `src/RhythmSync.Core` | Protocol, clock sync, jitter buffer, scheduler, host/client sessions, settings. No Windows APIs. |
| `src/RhythmSync.Platform.Windows` | WASAPI loopback capture and render output, device watching, host audio checks. |
| `src/RhythmSync.App` | WPF window (Host, Join and Settings pages), tray icon and tray flyout. |
| `tests/RhythmSync.Tests`, `tests/RhythmSync.Platform.Windows.Tests`, `tests/RhythmSync.App.Tests` | xUnit tests for each of the above. |

Tests:

```
dotnet test --filter "Category!=Hardware&Category!=Smoke"   # default suite
dotnet test --filter "Category=Smoke"                       # 30 s real-time UDP session on localhost
dotnet test --filter "Category=Hardware"                    # needs real audio devices
```

On first run the app adds a Windows Firewall rule (inbound, this program, Private profile only) by running `netsh` elevated, which shows a UAC prompt. The prompt comes back on each launch until you accept it once. To skip it, for example if you manage the firewall yourself:

```
dotnet run --project src/RhythmSync.App -- --skip-firewall
```

Without a rule, other PCs may not be able to reach the host.

## Host setup

1. Install VB-CABLE and reboot if the installer asks.
2. In Windows sound settings, set **CABLE Input** as the default playback device.
3. In RhythmSync, open the **Host** page, choose your real speakers or headphones under "Play on this PC through", and click **Start hosting**. CABLE Input is not in that list. You can change the speakers on the running Host screen too.
4. The Host screen shows a 4-digit pairing code. The banner on that screen tells you if VB-CABLE is missing or is not the default device, and has an "Open sound settings" button.

When VB-CABLE is installed, the host always captures the CABLE Input device itself (by its id), whatever the default device is. It plays on this PC's speakers only when CABLE Input is the default device and real speakers are chosen: then everything this PC plays goes into the cable, is streamed, and comes back out of the speakers through the same synchronised path as on the clients, so the host's speakers are delayed by the target latency like everyone else's. If CABLE Input is not the default, the host still streams whatever plays into the cable but does not play locally, and the banner says so. Without VB-CABLE the host captures the default device and only sends (mute its speakers).

Changing the default playback device while hosting does not restart the session: the banner updates and local playback starts or stops within about a second. Only losing the captured device (for example disabling or removing CABLE Input) restarts the host session; clients reconnect on their own after a few seconds of silence.

## Joining as a client

Open the **Join** page, pick the host from the list, enter the 4-digit code and click **Join**. A client should be playing within about 10 seconds. A PC that is hosting cannot join another host; stop hosting first.

The target latency (default 200 ms, 100 to 500 ms) is set on the host. Raise it on a weak Wi-Fi connection. Each client's volume and offset (plus or minus 100 ms) are set on the host, in that client's row; the host remembers them per PC and sends them every time the PC joins. The Join page shows them read-only ("Set on the host").

Only one RhythmSync runs per Windows user. Starting it again brings the running window to the front.

## Tray icon and Settings

Left-click the tray icon to open the flyout: the app's status and quick controls (stop hosting, leave, each client's volume). Click it again, press Esc or click elsewhere to close it. Right-click gives the Show/Hide and Quit menu; double-click shows or hides the window. Closing the window hides it to the tray.

The **Settings** page has this PC's name (used the next time it joins a host), **Start with Windows** (a per-user entry that starts the app with `--minimized`), **Start minimized**, and buttons that open the log and settings folders. The `--minimized` switch starts the app with only the tray icon and no window:

```
dotnet run --project src/RhythmSync.App -- --minimized
```

## Tuning the offset

The app does not yet measure how long each PC's output path takes (sound card, DAC, speakers, Bluetooth). PCs with different hardware will be a few milliseconds apart or more. Tune once by ear or with a recording: play a click track, record from the midpoint between two PCs, and adjust the offset in that client's row on the host until its clicks line up with the host's. Drag the slider (it applies when you release it), press −1 or +1 for single-millisecond steps, or type an exact value such as −13 or 12.5 in the box and press Enter. The host's own speakers have a fixed offset of 0 and no slider, so tune each client against the host. Automatic calibration is planned as sub-project 3.

Any offset change you make (more than 0.25 ms) takes effect at once, with one short resync (a brief fade out and in) on that client, so you hear each step as soon as you make it. Latency changes work differently: a step of more than 20 ms resyncs once, a smaller one (a single 10 ms key press on the latency slider) settles gradually, at up to 0.5 ms per second, over about 20 seconds.

## Matching speakers with EQ

Laptop speakers, a soundbar and a pair of desk speakers rarely sound alike. Each PC has its own ten-band EQ so you can make them match. It is on the **Host** page: every connected PC's card has an **EQ** line, and there is a **This PC** card at the top of the list for the host's own speakers. Click the **EQ** line to open it (it starts collapsed). The EQ is set on the host only; a client's Join page shows it read-only ("EQ: Laptop speakers", "EQ: Flat" or "EQ: Custom").

- **Bands.** Ten sliders from 31 Hz to 16 kHz, each from -12 to +12 dB in 0.5 dB steps. Drag, or use the arrow keys (0.5 dB) and PageUp or PageDown (3 dB). The audio follows as you move; there are no clicks.
- **Presets.** Flat, Laptop speakers, Bass boost and Treble cut. Moving a slider off a preset shows Custom. **Flat** puts every band back to 0 dB.
- **Preamp.** When you boost a band, RhythmSync lowers that PC's overall level a little so the boost cannot clip. The readout next to **Flat** shows how much (for example "Preamp -3.9 dB"). A boosted PC is therefore a bit quieter than a flat one, and you can raise its volume on the host if you want. While you drag a boosted band down, that PC plays a little quieter until about a quarter of a second after you let go. That is the preamp's safety hold, and it is deliberate.
- **Kept per PC.** The host remembers each PC's curve, sends it every time that PC joins, and restores its own curve when it starts. **This PC** shows "Not playing on this PC" while the host is not playing on its own speakers; you can still edit it, and the curve is used once local playback starts.

EQ shifts timing slightly at the bands it changes. A boost delays the sound near that band's centre and a cut advances it. Above 1 kHz the shift is under 1 ms, so it does not matter for staying in sync. It grows toward the bass: at 31 Hz a full +12 dB boost delays the sound by about 21 ms, which is still less than one cycle of the wave. The planned microphone calibration (sub-project 3) will measure each PC with its EQ applied, so the offset it sets will absorb what is left.

## Ports

| Port | Use |
|------|-----|
| UDP 47800 | Host discovery beacons |
| TCP 47801 | Control channel |
| UDP 47802 | Audio |
| UDP 47803 | Clock sync |

If 47802 or 47803 is taken, the audio and clock ports fall back to ephemeral ports (on the host, and for the client's audio port too). If 47801 is taken, hosting does not start and the Host page says so.

## Files

- Settings: `%AppData%\RhythmSync\settings.json` (device id, name, last host and pairing code, per-device volume, offset and EQ).
- Logs: `%AppData%\RhythmSync\logs\` (daily rolling files, 7 kept).

## The logo

The "sync wave" logo is `src/RhythmSync.App/Assets/RhythmSync.svg` (master artwork). `RhythmSync.ico` (frames 16, 20, 24, 32, 40, 48, 64 and 256 px, PNG-compressed) is the exe, window, title-bar and tray icon, and `RhythmSync-256.png` is for docs. Both are generated from the same geometry by a small off-screen WPF tool; after changing the artwork, regenerate them with:

```
dotnet run --project tools/IconGen
```

## Known limitations

- Traffic is not encrypted. Anyone on the LAN can listen to the stream.
- Windows only.
- Bluetooth speakers are not supported for same-room sync; their latency is large and varies.
- The pairing code is 4 digits. Three wrong codes from one device id lock that device out for 30 seconds, and more than 10 wrong codes within 30 seconds from any devices lock out every join for 30 seconds. That slows guessing to about 11 codes per 30 seconds, so finding the code takes hours, but someone on the LAN can still find it eventually, and can keep honest PCs from joining while they try.
- If the captured device (CABLE Input, or the default device without VB-CABLE) is lost while hosting, the host restarts its session; clients reconnect after a few seconds of silence.
- Each PC's output latency is not measured automatically; tune the offset on the host.
- Maximum 8 clients per host. The design target is 2 to 4 PCs.

## Acceptance testing

The hardware procedure, the checks that cannot run in the automated suite and the visual walkthrough of the window, flyout and Settings page are in [docs/acceptance.md](docs/acceptance.md). The design is in `docs/superpowers/specs/2026-10-17-core-sync-engine-design.md`.
