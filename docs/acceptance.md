# Hardware acceptance checklist

Manual checks for the core sync engine (spec section 8.4 and section 1.2). Fill in the "measured" column as you go. Record the date, the PCs, the Wi-Fi setup and the build (commit SHA) at the top of a copy of this file.

- Date:
- Build (git SHA):
- Host PC / output device:
- Client PC(s) / output device(s):
- Wi-Fi (router, band, signal):
- Target latency:

## Setup

- Host: VB-CABLE installed, **CABLE Input** is the default playback device, real speakers chosen on the Host page.
- Two laptops side by side, 1 to 2 m apart. Phone or recorder at the midpoint between them.
- Click-track audio file (one short click per second or similar) played from any app on the host.
- Audacity (or any editor with sample-accurate zoom) to measure the gap between click onsets on the left and right laptop in the recording. At 48 kHz one sample is 0.02 ms; a 2 ms gap is 96 samples.
- Windows network profile set to Private on all PCs.

## A. Success criteria (spec 1.2)

| # | Check | Pass criterion | Pass | Measured |
|---|-------|----------------|------|----------|
| A1 | Join time | Each client, from clicking Join to audio playing, takes 10 s or less. Try with 2, 3 and 4 PCs if available. | [ ] | |
| A2 | Any app's audio on every PC | Play audio from at least two different apps (for example a browser and a media player) on the host; it plays on every client and on the host's own speakers. | [ ] | |
| A3 | Alignment after one offset tuning | Tune each client's offset once, against the host, so the clicks line up (on the host, in that client's row; see C3). Then re-measure the click-onset gap in the recording at 0, 30 and 60 min; each is 2 ms or less. Do not touch the offset or the latency between measurements. If you nudged an offset by 2 ms or less, wait about 5 s before recording: small changes are slewed in, not applied at once. | [ ] | 0 min: <br> 30 min: <br> 60 min: |
| A4 | 1 h listen, no clicks or dropouts | Listen for a full hour of music on normal home Wi-Fi with no audible clicks or dropouts. Note any that you hear and the stats shown (loss %, late %). | [ ] | |
| A5 | Client output device change | While playing, unplug headphones on a client (or change its default output). Audio resumes on its own without restarting the app, and the client shows "In sync" within 10 s of the change. A click-track recording taken afterwards is again within 2 ms of the other PCs. | [ ] | Time to "In sync": <br> Gap after: |
| A6 | Host Wi-Fi drop | Switch the host's Wi-Fi off for 5 s, then back on. Without re-entering the code, each client shows "In sync" within 10 s after the Wi-Fi is back, and a click-track recording taken afterwards is again within 2 ms. | [ ] | Time to "In sync": <br> Gap after: |

## B. Checks that need the GUI

The GUI was not launched during development, so none of these has been run. Do them first, before the timing measurements.

Since the dark studio redesign the Client tab is the **Join** page and the Host tab is the **Host** page, both in the sidebar beside **Settings**; the rows below keep the old names. Section D covers how the new window looks and behaves.

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| B1 | App window opens | `dotnet run --project src/RhythmSync.App` shows the window with the sidebar (Host, Join, Settings) and a tray icon (see also D1.1). On first run the firewall prompt (UAC) appears; accept it. If you decline it, it appears again on the next launch, until it is accepted once. | [ ] | |
| B2 | Tray menu | Right-click the tray icon: Show/Hide toggles the window, Quit exits the app. | [ ] | |
| B3 | Hide to tray | Closing the window hides it to the tray; the app keeps running (and keeps hosting or playing if a session is active). | [ ] | |
| B4 | Host tab banners and local playback | With VB-CABLE missing, the banner says the PC will only send audio, and the host does not play on its own speakers. With VB-CABLE installed but not the default device, the banner asks to set CABLE Input as default and the host does not play locally (no echo: what plays on the speakers is not captured and played again); clients still get whatever plays into CABLE Input. Now set CABLE Input as the default while hosting: within about a second the banner goes away and local playback starts, without the session restarting (clients keep playing, the pairing code stays). CABLE Input is never offered under "Play on this PC through". | [ ] | |
| B5 | Open sound settings | The "Open sound settings" button opens Windows sound settings. | [ ] | |
| B6 | Client tab: host list and join | A running host appears in "Hosts on this network" within a few seconds. Selecting it, entering the code and clicking Join starts playback. A wrong code shows "Wrong code". After three wrong codes the next attempt, even with the right code, shows "Too many attempts – wait 30 s"; the right code works again after 30 s. (More than 10 wrong codes within 30 s from any PCs lock out every PC the same way.) | [ ] | |
| B7 | Stats panel before first heartbeat | On the host, with "Show stats" ticked, watch the new client's row: its stats show dashes (not zeros or garbage) until the first heartbeat arrives, then real values. On the joining PC's Client tab, before playback starts, the status reads "Connecting…" or "Syncing…" and the lines read "Sync error: —", "Drift correction: —" and "Resyncs: — · Concealed frames: — · Late frames: —", then fill in with numbers once playing. | [ ] | |
| B8 | Join page disabled while hosting | While hosting, the Join nav item stays enabled, but the content of the Join page is disabled behind the banner "This PC is hosting – stop hosting to join another host.". The page is usable again, and the banner gone, after Stop hosting. | [ ] | |
| B9 | Second launch | With the app already running (hosting, or hidden to the tray), start it again (`dotnet run --project src/RhythmSync.App -- --skip-firewall`). No second window or tray icon appears; the running instance's window is shown and brought to the front, and the second process exits at once. Hosting or playback in the running instance is not interrupted. | [ ] | |
| B10 | Clean Quit | Quit from the tray while hosting and while joined. The process exits, and `%AppData%\RhythmSync\logs\` contains no "step failed" lines (this matches both "Host teardown step failed" and "Shutdown step failed") and no Error-level lines from that run. | [ ] | |
| B11 | Client volume and offset are read-only | On a joined client, the Client tab shows "Volume" and "Offset" as text with "Set on the host" and no sliders. Change that client's volume and offset in its row on the host: the Client tab shows the new values within about a second, and the volume change is audible. | [ ] | |
| B12 | Speakers while hosting | While hosting, change "Play on this PC through" on the running Host screen: local playback moves to the new speakers within about a second, the session does not restart and clients keep playing. | [ ] | |

## C. Fault and change handling

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| C1 | Host default-device change, and loss of the cable, while hosting | With at least one client connected and playing, change the host's default playback device in Windows from CABLE Input to the real speakers and back. The session does not restart: the clients keep playing without a gap (they get silence while nothing plays into the cable), the pairing code stays, and the banner and local playback follow the default within about a second. Then disable CABLE Input (Sound control panel, Playback tab, right-click, Disable): the host restarts its session as a sender only (banner "VB-CABLE not found"); each client reconnects (it retries every 2 s) and plays again, with the silence lasting under about 10 s (host restart plus client reconnect), and the host rebinds TCP 47801 after the restart (no "port in use" error). Re-enable CABLE Input and stop and start hosting to capture it again. | [ ] | Silence length after disabling (expected under ~10 s): |
| C2 | Latency change mid-song | While music plays, drag the host latency slider to a value more than 20 ms away (for example 50 ms) and let go. The change is sent once, about 300 ms after the slider stops, and each client does exactly one short resync (a brief fade, no click) and returns to in-sync. A step of 20 ms or less (a single 10 ms key press) does not resync: it settles gradually, in about 20 s for 10 ms. | [ ] | Resyncs per client: |
| C3 | Endpoint latency per PC (acoustic) | Compare each PC's output path by recording (setup above). Wired speakers vs laptop speakers vs Bluetooth can differ by tens of ms. The host's own speakers have a fixed offset of 0 and no slider, so tune each client against the host: use the offset slider in that client's row on the host (the Client tab only shows it) to bring each client within 2 ms of the host. Each change of more than 2 ms is applied at once with one brief fade on that client; nudges of 2 ms or less are slewed in over a few seconds without a fade. Note the offset each client needed. | [ ] | PC / offset: |

## D. UI walkthrough (dark studio)

Visual checks for the redesigned window, tray flyout and Settings page (design: `docs/superpowers/specs/2026-10-18-ui-redesign-design.md`, section 8.3). The GUI was not launched during development, so none of these has been run. Do them on Windows 11 at 100% scaling unless a row says otherwise.

Mockups to compare against, in `docs/superpowers/specs/2026-10-18-ui-redesign-mockups/`:

- `design-system.html`: colours, type, buttons, slider and toggle, status chips, banner, pairing-code entry, navigation and card.
- `screens.html`: 1 Host running, 2 Host not started, 3 Join (before and after joining), 4 Settings.
- `flyout.html`: the tray flyout and its window behaviour.

Useful while testing: `dotnet run --project src/RhythmSync.App -- --skip-firewall` skips the UAC prompt; `--minimized` starts to the tray with no window. Settings are in `%AppData%\RhythmSync\settings.json`, logs in `%AppData%\RhythmSync\logs\`.

### D1. Window and pages

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| D1.1 | Window shell | The window has a custom dark title bar (no white or light system bar, also when the window is inactive), a sidebar with Host, Join and Settings, and a status footer in the sidebar. The window cannot be made smaller than 760 x 520. The footer reads "Not connected" when idle, "Hosting · waiting for PCs" when hosting with nobody joined, "Hosting · N of M in sync" once PCs have joined, and "<status> · <host name>" (for example "In sync · Living room") while joined to a host. | [ ] | |
| D1.2 | Host page, not started | Matches mockup 2 in `screens.html`. | [ ] | |
| D1.3 | Host page, running | Matches mockup 1 in `screens.html`: pairing code, banner (when VB-CABLE is missing or not the default device), client rows with status chips, volume and offset sliders, Show stats toggle. | [ ] | |
| D1.4 | Join page | Matches mockup 3 in `screens.html`, before and after joining: host list, four pairing-code boxes (digits only, auto-advance, backspace moves back, pasting "47-29" gives 4729), status chip once joined. | [ ] | |
| D1.5 | Settings page | Matches mockup 4 in `screens.html`: This PC's name, Start with Windows, Start minimized, and an About card with the version, "Open log folder" and "Open settings folder". Both buttons open Explorer at the right folder. | [ ] | |
| D1.6 | Components | Buttons, slider, toggles, status chips, banner and cards match `design-system.html`. | [ ] | |
| D1.7 | No black text on dark | On every page, in the flyout and in every state (idle, hosting, joined, disabled controls, error messages, empty lists, combo box drop-downs, text boxes, tooltips), text is light or coloured, never black on a dark background. Host names in the Join list use the normal text colour. | [ ] | |
| D1.8 | Hover, press and focus | Every button, sidebar item, toggle, slider, combo box, text box and list row shows a visible change on hover and on press. Tab through each page: every control shows a focus ring, and the ring on a host card in the Join list (keyboard focus) is not clipped on any edge. | [ ] | |
| D1.9a | Long session name (host) | On the Host page set a 64-character Session name (the box stops at 64). It ellipsizes with "…" without widening or breaking the layout in the Host page header, in the Join page host list on a client, in the "Joined · …" card on the client after joining, and in the title of the flyout (on the host, and on the client while joined). | [ ] | |
| D1.9b | Long PC name (client) | On a client PC set a 64-character name in Settings (see D4.3), leave the host and join again. The name ellipsizes in the client's row on the host page and in the host flyout's rows, without widening or breaking the layout. | [ ] | |
| D1.10 | 8 connected PCs, host page | With 8 clients connected, the host page scrolls vertically; every row can be reached and nothing overlaps the sidebar. | [ ] | Clients tested: |
| D1.11 | Minimum size | At 760 x 520 every page is usable: nothing is cut off without a scrollbar (Settings scrolls at this height). | [ ] | |

### D2. Title bar, snap layouts and scaling

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| D2.1 | Title bar buttons | Minimize, Maximize/Restore and Close work. Minimize and Close show hover and press states (Close turns red on hover). Maximize/Restore shows a hover state only (Windows owns that area for the snap layouts) and acts when the mouse button is released. Dragging the title bar moves the window; double-clicking it maximizes and restores. | [ ] | |
| D2.2 | Snap layouts | Hover the Maximize button for about a second: the Windows 11 snap layouts flyout appears. Choosing a zone snaps the window. A plain click on Maximize maximizes, and a second click restores. | [ ] | |
| D2.3 | Maximize hover does not stick | The Maximize button shows its hover look while the pointer is over it (and while the snap layouts are open). Move the pointer off the button and out of the window: the button goes back to normal and stays that way. | [ ] | |
| D2.4 | Drag onto or off Maximize | Press the mouse button on the page content, drag over the Maximize button and release there: the window does NOT maximize (a release only clicks Maximize after a press on it). The other way round: press on the Maximize button, drag off it and release elsewhere: the window does NOT maximize either. Double-click the Maximize button: the window maximizes (or restores) once, not twice. | [ ] | |
| D2.5 | Dark frame | The window border and shadow are dark and no light line shows around the title bar, when the window is active, inactive, maximized and snapped. | [ ] | |
| D2.6 | 150% scaling | Set the display scale to 150% (Settings, System, Display, Scale), signing out and in if Windows asks. Repeat D1.1 to D1.8, D2.1 to D2.5 and D3.1 to D3.3: text is crisp, nothing is clipped or overlaps, the flyout still sits in the corner by the taskbar with its gap, and the snap layouts still appear over the Maximize button. Put the scale back afterwards. | [ ] | |

### D3. Tray icon and flyout

Tray icon: a left-click opens the flyout and a second click closes it. Right-click keeps the menu (Show/Hide, a status line, Quit). Double-click shows or hides the main window and closes the flyout that its first click opened, so the flyout never stays open over the window (it may flash briefly).

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| D3.1 | Flyout, three modes | Left-click the tray icon in each state and compare with `flyout.html`. Idle: the idle text and "Open RhythmSync". Hosting: the title and pairing code, a row per connected PC with a status dot, its sync error and a volume slider (or the waiting text when nobody has joined), "Stop hosting" and "Open RhythmSync". Joined: the sync error and drift, and the volume and offset as text ("Set on the host"), "Leave" and "Open RhythmSync". Each button works and closes the flyout (Stop hosting stops hosting, Leave leaves, Open RhythmSync shows and focuses the main window). A volume slider on a client row changes that PC's volume (audible). | [ ] | |
| D3.2 | Over a bottom taskbar | With the taskbar at the bottom, the flyout opens in the bottom-right corner just above the taskbar, fully on screen, with a short slide-and-fade entrance (no animation if Windows animations are off). | [ ] | |
| D3.3 | Other edges, other monitor | Where practical: with the taskbar at the top, left or right (Windows 11 only offers the bottom by default; skip an edge you cannot set), the flyout sits against that corner and inside the working area. With two monitors it opens on the monitor under the cursor, by that monitor's taskbar. | [ ] | Edges tried: |
| D3.4 | Esc and click-away | With the flyout open, Esc closes it. Open it again and click on the desktop or another window: it closes. Alt+F4 on the flyout only hides it; the app keeps running. | [ ] | |
| D3.5 | Tray click while open | With the flyout open, click the tray icon: it closes and does not reopen (a click within 300 ms of the click-away is ignored). Click the tray icon again more than 300 ms later: it opens. | [ ] | |
| D3.6 | Flyout grows and scrolls | With the flyout open while hosting, a PC joining or leaving adds or removes its row and the flyout stays against the taskbar. With 8 connected PCs the client list scrolls inside the flyout, the buttons stay visible, and the flyout never grows past the working area. | [ ] | Clients tested: |
| D3.7 | Tray menu and double-click | Right-click shows Show/Hide, the status line and Quit; Show/Hide and Quit work (Quit exits cleanly, as in B10). Double-click on the icon shows or hides the window, and no flyout is left open over it. | [ ] | |

### D4. Settings, startup and `--minimized`

| # | Check | Pass criterion | Pass | Measured / notes |
|---|-------|----------------|------|------------------|
| D4.1 | `--minimized` | With no instance running, start the app with `--minimized`: no window appears, the tray icon does, and a left-click on it opens the flyout and "Open RhythmSync" shows the window. | [ ] | |
| D4.2 | Start minimized | In Settings turn Start minimized on, quit, and start the app normally: it starts to the tray with no window. Turn it off: the next start shows the window. | [ ] | |
| D4.3 | Rename, leave, rejoin | In Settings change This PC's name on a client PC (press Enter, or click away). An empty name, or one over 64 characters, shows "Enter a name (up to 64 characters)." and is not saved. A valid name is saved (see `settings.json`). The name applies on the next join: leave the host, join again, and the host's client row shows the new name. | [ ] | |
| D4.4 | Start with Windows, on | Turn Start with Windows on, then run `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v RhythmSync`: the value exists and reads `"<path to the exe>" --minimized`, with the path in quotes. | [ ] | Value seen: |
| D4.5 | Start with Windows, sign-out/in | With it on, sign out of Windows and sign in again: the app starts by itself, to the tray with no window, and the tray icon works. | [ ] | |
| D4.6 | Start with Windows, off | Turn Start with Windows off. The same `reg query` now reports "ERROR: The system was unable to find the specified registry key or value.", and after the next sign-in the app does not start. | [ ] | |

## Notes

- The offset range is plus or minus 100 ms, and the effective offset is never below max(−100, −(target latency − 50)) ms: −100 ms at the default 200 ms latency. The latency rule only binds below 150 ms latency (for example −50 ms at 100 ms), so very negative offsets need a latency of at least 150 ms.
- The sync error shown in the app is the scheduler's error against the clock estimate. It does not include each PC's output-device latency, which is why the acoustic measurement (A3, C3) is the real test.
- Bluetooth speakers are not supported for same-room sync; C3 is there to show how far off they are.
- Logs: `%AppData%\RhythmSync\logs\`. Attach the relevant log if a check fails.
