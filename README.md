# RhythmSync

RhythmSync plays one Windows PC's audio on several PCs on the same Wi-Fi, in sync. The host captures its system audio (any app) with WASAPI loopback and streams it over UDP with timestamps. Each client syncs its clock to the host and plays every frame at the same moment, so PCs in one room act as a multi-speaker system. The goal is under 2 ms between PCs.

This is the core sync engine (sub-project 1). EQ, microphone calibration and room layout are not built yet.

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
| `src/RhythmSync.App` | WPF window and tray icon (Client and Host tabs). |
| `tests/RhythmSync.Tests`, `tests/RhythmSync.Platform.Windows.Tests`, `tests/RhythmSync.App.Tests` | xUnit tests for each of the above. |

Tests:

```
dotnet test --filter "Category!=Hardware&Category!=Smoke"   # default suite
dotnet test --filter "Category=Smoke"                       # 30 s real-time UDP session on localhost
dotnet test --filter "Category=Hardware"                    # needs real audio devices
```

On first run the app adds a Windows Firewall rule (inbound, this program, Private profile only) by running `netsh` elevated, which shows one UAC prompt. To skip it, for example if you manage the firewall yourself:

```
dotnet run --project src/RhythmSync.App -- --skip-firewall
```

Without a rule, other PCs may not be able to reach the host.

## Host setup

1. Install VB-CABLE and reboot if the installer asks.
2. In Windows sound settings, set **CABLE Input** as the default playback device.
3. In RhythmSync, open the **Host** tab, choose your real speakers or headphones under "Play on this PC through" (not CABLE Input), and click **Start hosting**.
4. The Host screen shows a 4-digit pairing code. The banner on that screen tells you if VB-CABLE is missing or is not the default device, and has an "Open sound settings" button.

The host plays its own audio through the same synchronised path as the clients, so the host's speakers are delayed by the target latency like everyone else's.

## Joining as a client

Open the **Client** tab, pick the host from the list, enter the 4-digit code and click **Join**. A client should be playing within about 10 seconds. The tab is disabled while the PC is hosting.

The target latency (default 200 ms, 100 to 500 ms) is set on the host. Raise it on a weak Wi-Fi connection. Each client has its own volume and an offset slider (plus or minus 100 ms).

## Tuning the offset

The app does not yet measure how long each PC's output path takes (sound card, DAC, speakers, Bluetooth). PCs with different hardware will be a few milliseconds apart or more. Tune once by ear or with a recording: play a click track, record from the midpoint between two PCs, and move the offset slider on one PC until the clicks line up. Automatic calibration is planned as sub-project 3.

## Ports

| Port | Use |
|------|-----|
| UDP 47800 | Host discovery beacons |
| TCP 47801 | Control channel |
| UDP 47802 | Audio |
| UDP 47803 | Clock sync |

If 47802 or 47803 is taken, the audio and clock ports fall back to ephemeral ports. If 47801 is taken, hosting does not start and the Host tab says so.

## Files

- Settings: `%AppData%\RhythmSync\settings.json` (device id, name, last host and pairing code, per-device volume and offset).
- Logs: `%AppData%\RhythmSync\logs\` (daily rolling files, 7 kept).

## Known limitations

- Traffic is not encrypted. Anyone on the LAN can listen to the stream.
- Windows only.
- Bluetooth speakers are not supported for same-room sync; their latency is large and varies.
- The pairing code is 4 digits. Three wrong codes from one device id lock it out for 30 seconds, but an attacker on the LAN can change device ids and keep guessing.
- If the host's default playback device changes while hosting, the host restarts its session; clients reconnect after a few seconds of silence.
- Each PC's output latency is not measured automatically; use the offset slider.
- Maximum 8 clients per host. The design target is 2 to 4 PCs.

## Acceptance testing

The hardware procedure and the checks that cannot run in the automated suite are in [docs/acceptance.md](docs/acceptance.md). The design is in `docs/superpowers/specs/2026-10-17-core-sync-engine-design.md`.
