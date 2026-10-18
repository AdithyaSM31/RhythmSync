# RhythmSync UI redesign: "Dark studio": Design

**Date:** 2026-10-18
**Status:** Draft, awaiting review
**Scope:** `src/RhythmSync.App` and its tests, plus one backward-compatible field in `RhythmSync.Core/Config/AppSettings.cs` (`StartMinimized`). There are no other Core or Platform.Windows changes.
**Approved mockups:** `docs/superpowers/specs/2026-10-18-ui-redesign-mockups/` (`design-system.html`, `screens.html`, `flyout.html`). They are HTML fragments and the source of truth for look and layout.

## 1. Purpose

Make RhythmSync look and feel like a polished, modern audio tool: minimal, dark, consistent. Today it is about 250 lines of default-styled WPF.

### 1.1 Agreed requirements

- **Visual direction:** "Dark studio". Near-black surfaces, a violet→teal accent, soft glows on live status, rounded cards, and generous spacing. Always dark; there is no light theme.
- **Layout:** a resizable **sidebar window** (Host · Join · Settings) plus a **tray flyout** for quick control.
- **Flyout content:** status plus quick controls (§5).
- **Settings page:** app basics only (§4.4).
- **Approach:** a hand-built XAML design system, with no third-party UI library.
- **Parity:** every existing control, message and behaviour is kept. View-model logic and existing UI strings don't change unless this spec says so.

### 1.2 Success criteria

1. Every screen in the mockups is implemented with the shared design system. No view uses default WPF control chrome.
2. All existing App, Core and Platform tests still pass, and the build has 0 warnings.
3. A markup smoke test constructs every window, page and the flyout with the theme merged, with **zero WPF binding errors and zero missing resources**.
4. Everything is reachable by keyboard, with a visible focus ring, and Esc closes the flyout.
5. The manual visual walkthrough (§8.3) passes on Windows 11 at 100% and 150% scaling.

### 1.3 Out of scope

A light theme, audio level meters, localization, EQ controls (sub-project 2 will build on these components), remembering the window size and position, and Windows 10-specific visual tweaks (Windows 10 gets the same UI, with MDL2 icon and font fallbacks).

## 2. Design system (`src/RhythmSync.App/Themes/`)

### 2.1 Tokens (`Tokens.xaml`)

Each colour has a `Color` resource and a `SolidColorBrush`. Keys are given without the `Brush`/`Color` suffix.

| Key | Value | Use |
|-----|-------|-----|
| `Bg` | `#0E0F13` | Window background, title bar |
| `Surface` | `#14161C` | Sidebar, flyout background (`#12141A` for the flyout) |
| `Card` | `#161921` | Cards, list items |
| `CardBorder` | `#232733` | 1 px hairlines |
| `Raised` | `#1D2029` | Secondary button, selected nav item, inactive chip |
| `RaisedBorder` | `#2A2E3B` | Secondary button and input borders |
| `Track` | `#262A36` | Slider and toggle track |
| `Text` | `#E8E9EE` | Primary text |
| `TextMuted` | `#8A8F9C` | Captions and secondary text (≈ 6:1 contrast on Bg) |
| `Accent` | `#8B7CFF` | Violet: primary actions, focus, selection |
| `AccentSoft` | `#B4ABFF` | Accent text on dark (nav, ghost buttons) |
| `Teal` | `#4FD1C5` | "In sync" and hosting status |
| `Warning` | `#F5B94A` | Degraded, banners, congestion |
| `Danger` | `#FF6B7A` | Stop hosting, not connected, errors |
| `AccentGradient` | linear 0→1: `#8B7CFF` → `#4FD1C5` | Slider fill, toggle on |

Tinted fills use the status colour at 12–14% alpha, with borders at 25–30% alpha (see the mockups).

- **Radii:** `RadiusCard` 12, `RadiusControl` 9, `RadiusSmall` 6, `RadiusPill` 12.
- **Spacing:** 4 / 8 / 12 / 16 / 24.
- **Type:**
  - `FontUi` = `Segoe UI Variable Text, Segoe UI`
  - `FontMono` = `Cascadia Mono, Consolas`
  - `FontIcons` = `Segoe Fluent Icons, Segoe MDL2 Assets`
  - Ramp:

    | Style | Size and weight | Extras |
    |---|---|---|
    | `Display` | 30 SemiBold | letter spacing ≈ 8 px, glow `DropShadowEffect` (Accent, blur 18, opacity 0.45, depth 0) |
    | `Title` | 20 SemiBold | |
    | `Subtitle` | 14 SemiBold | |
    | `Body` | 13 | |
    | `Caption` | 11, `TextMuted` | |
    | `Stat` | 12, `FontMono` | |

- **Motion:**
  - Hover and press colour transitions: 120 ms.
  - Flyout: slide up 12 px plus fade over 150 ms, cubic ease-out.
  - "Syncing" chip pulse: opacity 0.5↔1 over 1.6 s, looping.
  - When `SystemParameters.ClientAreaAnimation` is false, the pulse and slide are turned off.

### 2.2 Control styles (one dictionary each, merged by `Studio.xaml`, which is merged by `App.xaml`)

- **Buttons (`Buttons.xaml`):**
  - Base: `RadiusControl`, padding 8×16, SemiBold 13.
  - `PrimaryButton`: Accent fill, Bg text, accent glow shadow.
  - `SecondaryButton`: Raised fill, RaisedBorder.
  - `GhostButton`: transparent, AccentSoft text.
  - `DangerButton`: Danger tint fill, border and text.
  - All buttons:
    - hover lightens 6%;
    - pressed darkens 6%;
    - disabled sits at 40% opacity.
  - Implicit `Button` style = `SecondaryButton`.
- **Slider (`Slider.xaml`):** a 4 px Track with AccentGradient fill up to the thumb. The thumb is a 16 px white circle with a 4 px Accent ring at 25% alpha on hover and focus. Implicit style.
- **ToggleSwitch (`ToggleSwitch.xaml`):** a keyed `CheckBox` style. The track is 38×20; off is Track with a muted knob, on is AccentGradient with a white knob. The knob slides over 120 ms.
- **Inputs (`TextBox.xaml`, `ComboBox.xaml`):**
  - Base: Bg fill, RaisedBorder, `RadiusControl`, padding 6×9.
  - On focus the border turns Accent with a 3 px Accent ring at 20% alpha.
  - The ComboBox popup is a Card with an item hover of Raised.
  - Implicit styles.
- **`ScrollBar.xaml`:** a thin 6 px thumb in RaisedBorder, with no arrows. Implicit.
- **`NavItem.xaml`:** a keyed `RadioButton` style with an icon glyph and label. When selected it uses a Raised fill, AccentSoft text and a 3 px Accent inset bar on the left.
- **`Card.xaml`:** a keyed `Border` style with Card fill, a 1 px CardBorder and `RadiusCard`.
- **`FocusVisual.xaml`:** a 2 px Accent rounded rectangle with a 2 px offset, applied to every focusable style.

### 2.3 Components (`src/RhythmSync.App/Controls/`)

- **`StatusChip`** (a custom `Control` with a template in `Themes/StatusChip.xaml`):
  - Properties: `Text` (string) and `Kind` (enum `StatusKind { InSync, Hosting, Syncing, Degraded, Connecting, NotConnected }`).
  - Rendering: a pill with a 7 px dot. InSync and Hosting are Teal with a glow, Syncing is Accent with the pulse, Degraded is Warning, Connecting is muted, and NotConnected is Danger.
  - A pure static `StatusKinds.From(SyncState?, bool hosting)` maps app state to a `Kind`.
- **`Banner`** (a `ContentControl`):
  - Properties: `Severity` (Warning, Info), `Message`, `ActionText` and `ActionCommand`. When `ActionText` is empty the action is hidden.
  - Rendering: a Warning-tint fill with a 3 px left bar, an icon glyph, the message, and an optional ghost action button.
- **`PinBox`** (a `UserControl`):
  - Four 1-digit boxes bound two-way to a `string Code` dependency property, so it drops into the existing `ClientViewModel.Code`.
  - Its behaviour comes from a pure `CodeEntry` class:
    - digits 0-9 only (other characters ignored);
    - typing advances to the next box;
    - Backspace on an empty box moves back and clears it;
    - pasting takes the first 4 digits from any text ("47-29" → `4729`);
    - Enter raises a routed `Submitted` event, which the Join page wires to `JoinCommand`.
  - `AutomationProperties.Name` = "Pairing code digit N".

## 3. Shell (`MainWindow`)

- **Window:** default 960×640, minimum 760×520, `Bg` background.
- **Title bar and chrome:**
  - Custom chrome via `WindowChrome`: caption height 32, resize border 6, `UseAeroCaptionButtons=false`.
  - A dark title bar draws the app icon glyph, "RhythmSync", and minimize/maximize/close buttons (icon buttons, 46 px wide; close hovers Danger).
  - Windows 11 snap layouts: handle `WM_NCHITTEST` so the maximize button returns `HTMAXBUTTON`.
  - Set `DWMWA_USE_IMMERSIVE_DARK_MODE` and keep the system rounded corners.
  - Close still hides to the tray, as today.
- **Sidebar:**
  - 200 px, `Surface` background with a right hairline.
  - Nav items: Host (Speakers glyph `E7F5`), Join (Link glyph `E71B`), and Settings (Setting glyph `E713`) pinned to the bottom. All three glyphs exist in both Segoe Fluent Icons and Segoe MDL2 Assets.
  - A footer card shows the app status (§3.1).
- **Navigation:** `MainViewModel` gains `SelectedPage` (enum `Page { Host, Join, Settings }`; at startup it is Join when `AppSettings.LastHostName` is non-null, otherwise Host) and `FooterText`. Pages are swapped in a `ContentControl` via DataTemplates.
- **Banners at the top of the Join page:** the existing public-network warning (`MainViewModel.PublicNetworkWarningText`) and the existing hosting notice "This PC is hosting – stop hosting to join another host." (shown while hosting). While hosting, the Join page content is disabled (existing `ClientEnabled`).

### 3.1 Footer text (new copy)

- Hosting with no clients: `Hosting · waiting for PCs`
- Hosting with clients: `Hosting · {inSync} of {n} in sync`
- Joined: `{client StatusText} · {host name}`, for example `In sync · Living Room`
- Otherwise: `Not connected`

## 4. Pages

### 4.1 Host, not started (`HostTabView`, restyled)

A centred 360 px Card holds:
- the accent icon;
- **"Share this PC's audio"** (Title) and **"Other PCs on your Wi-Fi can join with the pairing code and play in sync."** (Caption);
- "Session name" (TextBox);
- "Play on this PC through" (ComboBox);
- a full-width PrimaryButton **"Start hosting"**;
- the existing `ErrorText` in Danger below it.

### 4.2 Host, running (`HostView` with the running part of `HostTabView`)

- **Header row:** the session name (Title), StatusChip(Hosting, "Hosting"), and on the right a DangerButton **"Stop hosting"**.
- **Banner:** the existing `AudioBanner`, with the action **"Open sound settings"** (`OpenSoundSettingsCommand`).
- **Two cards side by side:**
  - **Code card:** "Pairing code" (Caption), the code in `Display`, and "Share this with the other PCs" (Caption, new).
  - **Settings card:**
    - "Latency": slider, `LatencyText`, and the existing `Delay=300` binding;
    - the existing helper text "Higher latency is more robust on a weak Wi-Fi connection.";
    - "Play on this PC through" (ComboBox, the running speaker picker).
  - "Session name" stays editable in the settings card (existing binding).
- **"Connected PCs" header:** the client count, and on the right "Show stats" as a ToggleSwitch.
- **Empty state:** "Waiting for PCs to join…" (Caption, centred in a Card).
- **One Card per client:**
  - Row 1: the name (Subtitle), StatusChip (`StatusText`, kind from the client state), and the sync error (Stat). `CongestedText` appears right-aligned in Warning.
  - Row 2: "Volume" slider plus `VolumeText`, and "Offset" slider plus `OffsetText`.
  - When stats are on, a hairline separator, then a 5-column grid using `StatsPanel`'s existing labels and values ("Sync error", "Packet loss", "Late packets", "Clock drift", "Clock fit residual"), with the values in `Stat`.

### 4.3 Join (`ClientView`, restyled)

- **Header:** "Join a host" (Title, new).
- **"Hosts on this network"** (Caption), then a list of host Cards, each with an icon, name (Subtitle) and IP (Caption). The selected card gets an Accent border and ring. `HostListMessage` appears as the empty or error text.
- **"Code"** label, then `PinBox` (bound to `Code`), then PrimaryButton **"Join"** (`JoinCommand`). `ErrorText` appears in Danger.
- **Status card**, visible when joined or joining:
  - "Joined · {host}" (Subtitle) with a StatusChip (`StatusText`);
  - a SecondaryButton "Leave";
  - `StatusMessage` in Warning;
  - a Stat line: `SyncErrorText` · `DriftText` · `CountersText`;
  - `VolumeText` · `OffsetText` with "Set on the host" (Caption, italic).

### 4.4 Settings (new `SettingsView` + `SettingsViewModel`)

A Card with three setting rows (label, caption, control on the right):

| Label | Caption | Control | Behaviour |
|-------|---------|---------|-----------|
| This PC's name | Shown to the host and other PCs | TextBox | Trimmed, 1–64 characters. Saved to `AppSettings.DeviceName` on lost focus or Enter, and used in the next `Hello`. Invalid → inline "Enter a name (up to 64 characters)." and the value is not saved. |
| Start with Windows | Launch RhythmSync when you sign in | ToggleSwitch | Reads and writes through `IStartupRegistration`. On failure the toggle reverts and shows "Couldn't change the startup setting." |
| Start minimized | Open straight to the tray | ToggleSwitch | Saved to the new `AppSettings.StartMinimized` (default false). |

An **About** Card:
- "RhythmSync {version}" (from `AssemblyInformationalVersion`);
- SecondaryButtons **"Open log folder"** and **"Open settings folder"** (Explorer on `%AppData%\RhythmSync\logs` and `%AppData%\RhythmSync`).

**Startup integration:**
- The `IStartupRegistration` implementation writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `RhythmSync` = `"<exe path>" --minimized`. The toggle's initial state is whether that value exists.
- `StartupOptions` gains `--minimized`.
- When `--minimized` or `StartMinimized` is set, the window isn't shown at startup (the tray icon still appears).
- Tests use a fake `IStartupRegistration` and never touch the registry.

## 5. Tray flyout (`TrayFlyoutWindow` + `TrayFlyoutViewModel`)

- **Window:**
  - Borderless, `Topmost`, `ShowInTaskbar=false`, 320 px wide with auto height.
  - `Surface` (`#12141A`) with a `CardBorder` hairline, radius 14 and a soft drop shadow.
  - Content is laid out as in `flyout.html`.
- **Opening and closing:**
  - Left-click on the tray icon toggles it; a second click closes it.
  - Right-click keeps the existing context menu (Show/Hide, Quit).
  - It closes on `Deactivated` (clicking elsewhere) or Esc.
  - Only one instance at a time.
- **Placement:** a pure `FlyoutPlacement.Compute(Rect workArea, Rect screenBounds, Size flyoutSize, double margin = 12)` puts the flyout in the working-area corner nearest the taskbar (bottom, top, left or right edge), on the monitor that contains the cursor at click time. Values are in device-independent pixels (the caller converts with the window's DPI). If no monitor is found, it uses the primary screen.
- **Content by state:**

  | State | Content |
  |---|---|
  | **Hosting** | Header: session name, StatusChip(Hosting), and the code (`Display` at 20 px). One item per client: status dot, name, sync error, and a volume slider. The slider calls the same host per-device setting path as the main window's row and saves the same way; it is clamped and NaN-guarded like the row. With no clients, "Waiting for PCs to join…". Buttons: DangerButton **"Stop hosting"**, SecondaryButton **"Open RhythmSync"**. |
  | **Joined (client)** | Header: host name and StatusChip(`StatusText`). An item with `SyncErrorText` · `DriftText` and `VolumeText` · `OffsetText` · "Set on the host". Buttons: **"Leave"**, **"Open RhythmSync"**. |
  | **Idle** | Header: "RhythmSync" and StatusChip(NotConnected, "Not connected"). "Host this PC's audio, or join a host from the app." PrimaryButton **"Open RhythmSync"**. |

- "Open RhythmSync" shows and activates the main window and closes the flyout. Stop hosting and Leave call the existing commands.
- `TrayFlyoutViewModel` exposes no new behaviour. It projects the existing `HostViewModel`/`HostRuntime` and `ClientViewModel` state and reuses their commands. Cross-thread updates go through the existing `post` dispatcher pattern.

## 6. Accessibility and resilience

- Every icon-only button has `AutomationProperties.Name`. Tab order follows the visual order. The focus ring is always visible on keyboard focus.
- Colour is never the only signal: every status chip has text.
- Missing icon glyphs fall back to the MDL2 font. A missing `Cascadia Mono` falls back to Consolas.
- The flyout never throws into the tray click handler (exceptions are caught and logged). A failed DWM call (dark mode, older Windows) is ignored.

## 7. Files

```
src/RhythmSync.App/
  Themes/   Tokens.xaml Studio.xaml Buttons.xaml Slider.xaml ToggleSwitch.xaml TextBox.xaml ComboBox.xaml
            ScrollBar.xaml NavItem.xaml Card.xaml FocusVisual.xaml StatusChip.xaml Banner.xaml
  Controls/ StatusChip.cs StatusKinds.cs Banner.cs PinBox.xaml(.cs) CodeEntry.cs
  Shell/    TitleBar.xaml(.cs) WindowChromeHelper.cs (WM_NCHITTEST, DWM dark mode)
  Flyout/   TrayFlyoutWindow.xaml(.cs) TrayFlyoutViewModel.cs FlyoutPlacement.cs
  Views/    SettingsView.xaml(.cs) (+ restyled HostTabView, HostView, ClientView, StatsPanel)
  ViewModels/ SettingsViewModel.cs (+ MainViewModel: SelectedPage, FooterText)
  Services/ IStartupRegistration.cs RegistryStartupRegistration.cs (+ TrayIcon: LeftClick event; StartupOptions: --minimized)
src/RhythmSync.Core/Config/AppSettings.cs   (+ StartMinimized, default false; JSON stays backward compatible)
tests/RhythmSync.App.Tests/  CodeEntryTests FlyoutPlacementTests SettingsViewModelTests TrayFlyoutViewModelTests
                             StatusKindsTests ShellNavigationTests MarkupSmokeTests
```

`AppSettings` is the only change outside the App. It is backward compatible: missing JSON field → false.

## 8. Testing

### 8.1 Unit tests (no UI)

- **`CodeEntry`:**
  - digits only;
  - auto-advance;
  - backspace moves back;
  - paste "47-29" → `4729`;
  - paste "123456" → `1234`;
  - Enter raises Submitted only when 4 digits are present.
- **`FlyoutPlacement`:**
  - taskbar on each of the four edges (bottom, top, left, right);
  - two monitors with different working areas;
  - the result always lies fully inside the working area;
  - 150% DPI input in DIPs.
- **`SettingsViewModel`:**
  - name trimming and validation: empty, 65 characters, whitespace only;
  - a valid name is saved;
  - the startup toggle calls the fake registration, and a fake failure reverts the toggle and sets the error;
  - Start minimized is saved.
- **`TrayFlyoutViewModel`:**
  - hosting, joined and idle each expose the right sections and texts;
  - the volume slider routes through the host settings path with clamping and NaN-guarding;
  - Stop hosting, Leave and Open invoke the right commands.
- **`StatusKinds.From`:** every `SyncState` value, plus hosting.
- **Shell:**
  - default page selection;
  - `FooterText` for every case in §3.1.
- **Startup options:** `--minimized` parsing, and that the window isn't shown when it or `StartMinimized` is set (tested through the composition seam, not by launching).

### 8.2 Markup smoke tests (STA thread, no window shown)

- With the `Studio.xaml` theme merged, construct `MainWindow` for each page, `HostTabView` (idle and hosting), `HostView` (with clients, stats on), `ClientView` (empty, list, joined), `SettingsView` and `TrayFlyoutWindow` (each state). Use real view models with the existing fakes.
- For each one, call `Measure`/`Arrange` and run the dispatcher until idle.
- Assert that a `TraceListener` on `PresentationTraceSources.DataBindingSource` recorded **zero binding errors**, and that no `XamlParseException` or `ResourceReferenceKeyNotFoundException` occurred.
- Existing `ViewMarkupTests` assertions (for example, the Client tab has no sliders for volume and offset) must still pass.

### 8.3 Manual visual walkthrough (added to `docs/acceptance.md`)

The user does these checks; agents don't launch the GUI:
- each page matches the mockups;
- hover, press and focus states;
- the flyout opens over the taskbar on each edge (where practical) and at 150% scaling;
- snap layouts on maximize;
- the dark title bar;
- Esc and click-away close the flyout;
- `--minimized` and Start with Windows actually start the app to the tray after signing in;
- with "Start with Windows" off, the registry value is gone.

## 9. Interaction with other work

- Sub-project 2 (EQ) is being designed in a separate session. Its UI should use these tokens and components (Card, Slider, ToggleSwitch, StatusChip). No code overlap exists until EQ is implemented.
- No protocol, Core timing or Platform audio behaviour changes.
