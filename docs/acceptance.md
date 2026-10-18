# Hardware acceptance checklist

Manual checks for the core sync engine (spec section 8.4 and section 1.2). Fill in the "measured" column as you go. Record the date, the PCs, the Wi-Fi setup and the build (commit SHA) at the top of a copy of this file.

- Date:
- Build (git SHA):
- Host PC / output device:
- Client PC(s) / output device(s):
- Wi-Fi (router, band, signal):
- Target latency:

## Setup

- Host: VB-CABLE installed, **CABLE Input** is the default playback device, real speakers chosen on the Host tab.
- Two laptops side by side, 1 to 2 m apart. Phone or recorder at the midpoint between them.
- Click-track audio file (one short click per second or similar) played from any app on the host.
- Audacity (or any editor with sample-accurate zoom) to measure the gap between click onsets on the left and right laptop in the recording. At 48 kHz one sample is 0.02 ms; a 2 ms gap is 96 samples.
- Windows network profile set to Private on all PCs.

## A. Success criteria (spec 1.2)

| # | Check | Pass criterion | Pass | Measured |
|---|-------|----------------|------|----------|
| A1 | Join time | Each client, from clicking Join to audio playing, takes 10 s or less. Try with 2, 3 and 4 PCs if available. | [ ] | |
| A2 | Any app's audio on every PC | Play audio from at least two different apps (for example a browser and a media player) on the host; it plays on every client and on the host's own speakers. | [ ] | |
| A3 | Alignment after one offset tuning | Tune each client's offset once, against the host, so the clicks line up. Then re-measure the click-onset gap in the recording at 0, 30 and 60 min; each is 2 ms or less. Do not touch the offset between measurements. | [ ] | 0 min: <br> 30 min: <br> 60 min: |
| A4 | 1 h listen, no clicks or dropouts | Listen for a full hour of music on normal home Wi-Fi with no audible clicks or dropouts. Note any that you hear and the stats shown (loss %, late %). | [ ] | |
| A5 | Client output device change | While playing, unplug headphones on a client (or change its default output). Audio resumes on its own without restarting the app, and the client shows "In sync" within 10 s of the change. A click-track recording taken afterwards is again within 2 ms of the other PCs. | [ ] | Time to "In sync": <br> Gap after: |
| A6 | Host Wi-Fi drop | Switch the host's Wi-Fi off for 5 s, then back on. Without re-entering the code, each client shows "In sync" within 10 s after the Wi-Fi is back, and a click-track recording taken afterwards is again within 2 ms. | [ ] | Time to "In sync": <br> Gap after: |

## B. Checks that need the GUI

The GUI was not launched during development, so none of these has been run. Do them first, before the timing measurements.

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| B1 | App window opens | `dotnet run --project src/RhythmSync.App` shows the window with Client and Host tabs and a tray icon. On first run the firewall prompt (UAC) appears; accept it. If you decline it, it appears again on the next launch, until it is accepted once. | [ ] | |
| B2 | Tray menu | Right-click the tray icon: Show/Hide toggles the window, Quit exits the app. | [ ] | |
| B3 | Hide to tray | Closing the window hides it to the tray; the app keeps running (and keeps hosting or playing if a session is active). | [ ] | |
| B4 | Host tab banners | With VB-CABLE missing, the banner says the PC will only send audio. With VB-CABLE installed but not the default device, the banner asks to set CABLE Input as default. Choosing CABLE Input as the local speakers shows the "pick real speakers" banner and local playback does not start. | [ ] | |
| B5 | Open sound settings | The "Open sound settings" button opens Windows sound settings. | [ ] | |
| B6 | Client tab: host list and join | A running host appears in "Hosts on this network" within a few seconds. Selecting it, entering the code and clicking Join starts playback. A wrong code shows "Wrong code". After three wrong codes the next attempt, even with the right code, shows "Too many attempts – wait 30 s"; the right code works again after 30 s. | [ ] | |
| B7 | Stats panel before first heartbeat | On the host, with "Show stats" ticked, watch the new client's row: its stats show dashes (not zeros or garbage) until the first heartbeat arrives, then real values. On the joining PC's Client tab, before playback starts, the status reads "Connecting…" or "Syncing…" and the lines read "Sync error: —", "Drift correction: —" and "Resyncs: — · Concealed frames: — · Late frames: —", then fill in with numbers once playing. | [ ] | |
| B8 | Client tab disabled while hosting | While hosting, the Client tab is disabled with the message to stop hosting before joining another host. It is usable again after Stop hosting. | [ ] | |
| B9 | Second instance | With the app already hosting, start a second instance (`dotnet run --project src/RhythmSync.App -- --skip-firewall`) and click Start hosting: it reports that port 47801 is in use ("Is RhythmSync already hosting?"). The second instance's Client tab reports it cannot listen for hosts ("is RhythmSync already running?"). | [ ] | |
| B10 | Clean Quit | Quit from the tray while hosting and while joined. The process exits, and `%AppData%\RhythmSync\logs\` contains no "step failed" lines (this matches both "Host teardown step failed" and "Shutdown step failed") and no Error-level lines from that run. | [ ] | |

## C. Fault and change handling

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| C1 | Host default-device change while hosting | With at least one client connected and playing, change the host's default playback device in Windows. The host restarts its session; the client reconnects (it retries every 2 s) and plays in sync again, with the silence lasting under about 10 s (host restart plus client reconnect). Also confirms the host rebinds TCP 47801 after the restart (no "port in use" error). | [ ] | Silence length (expected under ~10 s): |
| C2 | Latency change mid-song | While music plays, move the host latency slider to a new value. Each client does one short resync (a brief fade, no click) and returns to in-sync. Known issue: dragging the slider currently sends several changes, so expect more than one resync while dragging; set the value with a single click or key press to see exactly one. | [ ] | Resyncs per client: |
| C3 | Endpoint latency per PC (acoustic) | Compare each PC's output path by recording (setup above). Wired speakers vs laptop speakers vs Bluetooth can differ by tens of ms. The host's own speakers have a fixed offset of 0 and no slider, so tune each client against the host: use the offset slider on the Client tab (or on that client's row on the host) to bring each client within 2 ms of the host. Note the offset each client needed. | [ ] | PC / offset: |

## Notes

- The offset range is plus or minus 100 ms, and the effective offset is clamped to no less than minus (target latency − 50) ms, i.e. −150 ms at 200 ms latency, so very negative offsets need a higher latency.
- The sync error shown in the app is the scheduler's error against the clock estimate. It does not include each PC's output-device latency, which is why the acoustic measurement (A3, C3) is the real test.
- Bluetooth speakers are not supported for same-room sync; C3 is there to show how far off they are.
- Logs: `%AppData%\RhythmSync\logs\`. Attach the relevant log if a check fails.
