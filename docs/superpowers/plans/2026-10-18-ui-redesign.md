# RhythmSync "Dark studio" UI Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restyle the RhythmSync WPF app into the approved "Dark studio" design: a sidebar window, a tray flyout and a Settings page, without changing app behaviour.

**Architecture:**
- A hand-built XAML design system (tokens plus per-control styles in `Themes/`), merged once in `App.xaml`.
- Small reusable controls in `Controls/`.
- The existing view models stay the logic layer. New logic lives in small, pure, unit-tested classes: `CodeEntry`, `StatusKinds`, `FlyoutPlacement`, `FlyoutToggle`, `SettingsViewModel`, `TrayFlyoutViewModel`.
- XAML correctness is checked by STA "markup smoke" tests that construct each view with the theme and fail on any binding error or missing resource.

**Tech Stack:** .NET 10, WPF (+ WinForms `NotifyIcon`), CommunityToolkit.Mvvm 8.4.2, xUnit. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-10-18-ui-redesign-design.md`. Approved mockups: `docs/superpowers/specs/2026-10-18-ui-redesign-mockups/` (`design-system.html`, `screens.html`, `flyout.html`). Executors read the spec, and open the mockups in a browser for layout.

## Global Constraints

- Scope: `src/RhythmSync.App`, `tests/RhythmSync.App.Tests`, and only `AppSettings.StartMinimized` (default `false`, backward-compatible JSON) in `src/RhythmSync.Core/Config/AppSettings.cs`. No other Core or Platform changes.
- No new NuGet packages. No third-party UI library.
- Every existing UI string constant and view-model behaviour stays as is. New copy is exactly as written in the spec (§3.1, §4, §5).
- Token values are exactly spec §2.1 (hex colours, radii 12/9/6/12, spacing 4/8/12/16/24, type ramp, motion 120/150/1600 ms). Fonts: `Segoe UI Variable Text, Segoe UI`; `Cascadia Mono, Consolas`; `Segoe Fluent Icons, Segoe MDL2 Assets`.
- When `SystemParameters.ClientAreaAnimation` is false, there is no pulse and no slide.
- SAFETY: never launch the GUI app, never write the registry from tests, never run netsh or elevation. The GUI walkthrough is the user's manual step.
- Build: `dotnet build -c Release`, 0 warnings. Default tests: `dotnet test --filter "Category!=Hardware&Category!=Smoke"`. All existing tests must still pass.
- Commit after every task; each message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- UI smoke tests live in the xUnit collection `"Ui"` (`[Collection("Ui")]`, `DisableParallelization = true`) and run on the shared STA dispatcher from Task 1.

## Review Focus

1. **Tray click race:** clicking the tray icon while the flyout is open must close it and keep it closed. The flyout's own `Deactivated` fires first, so a naive toggle reopens it. Test: `FlyoutToggle` (Task 8).
2. **Long names in a small window** (min 760×520, 64-character session, host or PC names): names must ellipsize, not push controls off screen. Test: a markup assertion that name `TextBlock`s use `TextTrimming="CharacterEllipsis"`, and a smoke test at 760×520 with 64-character names (Tasks 5 and 6).
3. **Eight connected PCs:** the host page scrolls, and the flyout height is capped to the working area minus margins and scrolls. Test: `FlyoutPlacement` with a tall flyout, and a flyout smoke test with 8 clients (Task 8).
4. **Executable path with spaces** for Start with Windows: the Run value must be quoted, `"C:\Program Files\RhythmSync\RhythmSync.App.exe" --minimized`. Test: `RegistryStartupRegistration.RunValue` (Task 7).
5. **Pasted or odd input into the code boxes** ("47-29", "１２３４" full-width digits, letters): only ASCII 0-9, first 4 kept. Test: `CodeEntry` (Task 3).

---

## File Structure

```
src/RhythmSync.App/
  Themes/     Tokens.xaml Studio.xaml Buttons.xaml Slider.xaml ToggleSwitch.xaml TextBox.xaml ComboBox.xaml
              ScrollBar.xaml NavItem.xaml Card.xaml FocusVisual.xaml StatusChip.xaml Banner.xaml
  Controls/   StatusKind.cs StatusKinds.cs StatusChip.cs Banner.cs CodeEntry.cs PinBox.xaml(.cs)
  Shell/      TitleBar.xaml(.cs) WindowChromeHelper.cs
  Flyout/     FlyoutPlacement.cs FlyoutToggle.cs TrayFlyoutViewModel.cs TrayFlyoutWindow.xaml(.cs)
  Views/      SettingsView.xaml(.cs)  (+ restyled HostTabView, HostView, ClientView, StatsPanel)
  ViewModels/ Page.cs SettingsViewModel.cs  (+ MainViewModel: SelectedPage, FooterText; ClientViewModel: JoinedHostName)
  Services/   IStartupRegistration.cs RegistryStartupRegistration.cs  (+ TrayIcon: left-click; StartupOptions: --minimized)
  App.xaml (merge Studio.xaml), App.xaml.cs (composition), MainWindow.xaml(.cs)
tests/RhythmSync.App.Tests/
  Ui/UiDispatcher.cs Ui/BindingErrorRecorder.cs Ui/UiCollection.cs
  ThemeTests CodeEntryTests StatusKindsTests ControlSmokeTests ShellTests HostPagesSmokeTests JoinPageSmokeTests
  SettingsViewModelTests StartupRegistrationTests SettingsSmokeTests FlyoutPlacementTests FlyoutToggleTests
  TrayFlyoutViewModelTests FlyoutSmokeTests
docs/acceptance.md (visual walkthrough section)
```

---

### Task 1: Design tokens, control styles and the UI test harness

**Files:**
- Create: `src/RhythmSync.App/Themes/{Tokens,Studio,Buttons,Slider,ToggleSwitch,TextBox,ComboBox,ScrollBar,NavItem,Card,FocusVisual}.xaml`
- Modify: `src/RhythmSync.App/App.xaml` (merge `Themes/Studio.xaml`)
- Create: `tests/RhythmSync.App.Tests/Ui/{UiDispatcher,BindingErrorRecorder,UiCollection}.cs`
- Test: `tests/RhythmSync.App.Tests/ThemeTests.cs`

**Interfaces:**
- Produces: the resource keys `BgBrush SurfaceBrush CardBrush CardBorderBrush RaisedBrush RaisedBorderBrush TrackBrush TextBrush TextMutedBrush AccentBrush AccentSoftBrush TealBrush WarningBrush DangerBrush AccentGradientBrush` (plus a matching `…Color` for each), `RadiusCard RadiusControl RadiusSmall RadiusPill` (`CornerRadius`), `FontUi FontMono FontIcons` (`FontFamily`), text styles `DisplayText TitleText SubtitleText BodyText CaptionText StatText`, button styles `PrimaryButton SecondaryButton GhostButton DangerButton`, `ToggleSwitch` (CheckBox), `NavItem` (RadioButton), `CardBorder` (Border), and `StudioFocusVisual`. Implicit styles for `Button` (= SecondaryButton), `Slider`, `TextBox`, `ComboBox`, `ScrollBar` and `Window` (Bg background, FontUi, Text foreground).
- Produces (tests): `UiDispatcher.Run(Action body)` runs `body` on one shared STA thread, where a WPF `Application` exists (`ShutdownMode=OnExplicitShutdown`) with `pack://application:,,,/RhythmSync.App;component/Themes/Studio.xaml` merged. `UiDispatcher.Render(FrameworkElement e, double width = 960, double height = 640)` sets the size, calls `Measure`/`Arrange`/`UpdateLayout`, then waits for `DispatcherPriority.ApplicationIdle`. `BindingErrorRecorder` (IDisposable) attaches a `TraceListener` to `PresentationTraceSources.DataBindingSource` (level Warning) and exposes `IReadOnlyList<string> Errors`. `[CollectionDefinition("Ui", DisableParallelization = true)]`.

- [ ] **Step 1: Write the failing tests** (`ThemeTests`, `[Collection("Ui")]`)

```csharp
[Fact] public void Every_token_and_style_key_resolves()   // FindResource for every key listed in Produces → not null
[Fact] public void Token_colours_match_the_spec()         // e.g. ((SolidColorBrush)Find("BgBrush")).Color == #0E0F13, Accent #8B7CFF, Teal #4FD1C5, Warning #F5B94A, Danger #FF6B7A, TextMuted #8A8F9C
[Fact] public void Implicit_styles_apply_to_stock_controls() // new Button/Slider/TextBox/ComboBox rendered → Style resolves to the theme's (Button.Style == null but Background == RaisedBrush after Render)
[Fact] public void Styled_controls_render_without_binding_errors() // StackPanel with one of each styled control (incl. disabled, checked ToggleSwitch, selected NavItem) → recorder.Errors empty
```

- [ ] **Step 2: Run to verify failure:** `dotnet test tests/RhythmSync.App.Tests --filter "FullyQualifiedName~ThemeTests"`. Expected: build FAIL (no `UiDispatcher`) or resource-not-found.
- [ ] **Step 3: Implement the harness and the dictionaries** per spec §2.1/§2.2. Each control template defines its own visual states (hover 120 ms colour animation, pressed, disabled at 40% opacity, focus via `StudioFocusVisual`). Slider fill = `AccentGradientBrush` up to the thumb (template `PART_Track` with `DecreaseRepeatButton` showing the gradient).
- [ ] **Step 4: Run to verify pass**, then the full default suite.
- [ ] **Step 5: Commit**: `feat(app): dark studio design tokens, control styles and UI test harness`

---

### Task 2: StatusChip and Banner components

**Files:**
- Create: `src/RhythmSync.App/Controls/{StatusKind,StatusKinds,StatusChip,Banner}.cs`, `src/RhythmSync.App/Themes/{StatusChip,Banner}.xaml` (merged in `Studio.xaml`)
- Test: `tests/RhythmSync.App.Tests/StatusKindsTests.cs`, `tests/RhythmSync.App.Tests/ControlSmokeTests.cs`

**Interfaces:**
- Consumes: Task 1 tokens and harness; `RhythmSync.Core.Clock.SyncState { Connecting, Syncing, InSync, Degraded, Disconnected }`.
- Produces:
```csharp
public enum StatusKind { InSync, Hosting, Syncing, Degraded, Connecting, NotConnected }
public static class StatusKinds { public static StatusKind From(SyncState? state, bool hosting); }
public sealed class StatusChip : Control { DP string Text; DP StatusKind Kind; }        // template in Themes/StatusChip.xaml
public enum BannerSeverity { Warning, Info }
public sealed class Banner : Control { DP BannerSeverity Severity; DP string Message; DP string? ActionText; DP ICommand? ActionCommand; }
```
`From` rules: hosting → `Hosting`. Otherwise `InSync`→InSync, `Syncing`→Syncing, `Degraded`→Degraded, `Connecting`→Connecting, `Disconnected` or null → NotConnected.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] // StatusKindsTests
[InlineData(SyncState.InSync, false, StatusKind.InSync)] [InlineData(SyncState.Syncing, false, StatusKind.Syncing)]
[InlineData(SyncState.Degraded, false, StatusKind.Degraded)] [InlineData(SyncState.Connecting, false, StatusKind.Connecting)]
[InlineData(SyncState.Disconnected, false, StatusKind.NotConnected)] [InlineData(null, false, StatusKind.NotConnected)]
[InlineData(SyncState.Disconnected, true, StatusKind.Hosting)]
public void Maps_state_to_kind(SyncState? s, bool hosting, StatusKind expected)
// ControlSmokeTests [Collection("Ui")]
[Fact] public void StatusChip_renders_every_kind_without_errors()   // each Kind, Text "In sync" → Errors empty; the dot brush for InSync == TealBrush, Degraded == WarningBrush, NotConnected == DangerBrush
[Fact] public void Syncing_pulses_only_when_animations_are_enabled() // expose `internal static Func<bool> AnimationsEnabled` (default reads SystemParameters.ClientAreaAnimation); false → no running Storyboard on the dot
[Fact] public void Banner_hides_action_when_text_is_empty()         // ActionText null → action button Visibility Collapsed; "Open sound settings" → Visible and clicking executes ActionCommand
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~StatusKindsTests|FullyQualifiedName~ControlSmokeTests"`
- [ ] **Step 3: Implement** per spec §2.3. The chip is a pill with a 7 px dot; the glow is a `DropShadowEffect` (depth 0, blur 8) in the status colour for InSync and Hosting; the Syncing pulse is a 1.6 s opacity 0.5↔1 forever storyboard started from code when `AnimationsEnabled()` is true. The banner uses a Warning tint fill, a 3 px left bar and a warning glyph from `FontIcons`.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit**: `feat(app): status chip and banner components`

---

### Task 3: Pairing-code entry (CodeEntry + PinBox)

**Files:**
- Create: `src/RhythmSync.App/Controls/CodeEntry.cs`, `src/RhythmSync.App/Controls/PinBox.xaml(.cs)`
- Test: `tests/RhythmSync.App.Tests/CodeEntryTests.cs`, add cases to `ControlSmokeTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed class CodeEntry {                         // pure; 4 slots
  public const int Length = 4;
  public string Code { get; }                           // digits typed so far, ≤ 4, no gaps
  public int Focus { get; }                             // index of the slot that receives the next keystroke (0..3)
  public void Type(char c);                             // ASCII '0'..'9' only, else ignored; fills Focus slot, advances
  public void Backspace();                              // clears current slot if filled, else moves back one and clears it
  public void Paste(string text);                       // keeps the first 4 ASCII digits found, replaces Code, Focus = min(len, 3)
  public void Set(string? code);                        // from binding; same filtering as Paste
  public bool IsComplete => Code.Length == Length;
}
public partial class PinBox : UserControl { DP string Code (BindsTwoWayByDefault); routed event Submitted; }
```
PinBox shows 4 boxes (40×48, `RadiusControl`, Card fill, the focused box gets an Accent border and ring). Keyboard and paste go through `CodeEntry`. Enter raises `Submitted` only when `IsComplete`. Each box's `AutomationProperties.Name` = `Pairing code digit {n}`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Accepts_only_ascii_digits()             // Type('4'),'a','７'(U+FF17),'2' → Code "42"
[Fact] public void Typing_advances_focus()                 // 4 digits → Code "4729", Focus 3, IsComplete
[Fact] public void Backspace_on_empty_slot_moves_back()    // "47", Backspace → "4"; Backspace → ""
[Theory] [InlineData("47-29","4729")] [InlineData("123456","1234")] [InlineData("ab","")] [InlineData("１２３４","")]
public void Paste_keeps_first_four_ascii_digits(string pasted, string expected)
[Fact] public void Set_from_binding_filters_like_paste()   // Set("9x9") → "99"
// ControlSmokeTests
[Fact] public void PinBox_round_trips_code_with_a_view_model() // bind Code to an INotifyPropertyChanged fake; set VM "4729" → boxes show 4,7,2,9; simulate typing via CodeEntry path → VM updated; Errors empty
[Fact] public void PinBox_raises_submitted_only_when_complete()
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~CodeEntryTests|FullyQualifiedName~ControlSmokeTests"`
- [ ] **Step 3: Implement** `CodeEntry` and then `PinBox`. PinBox handles `PreviewTextInput`, `PreviewKeyDown` (Back, Left/Right, Enter) and `DataObject.Pasting`, and moves keyboard focus to box `Focus`.
- [ ] **Step 4: Run to verify pass.**
- [ ] **Step 5: Commit**: `feat(app): four-box pairing code entry`

---

### Task 4: Shell: custom title bar, sidebar navigation and footer

**Files:**
- Create: `src/RhythmSync.App/Shell/TitleBar.xaml(.cs)`, `src/RhythmSync.App/Shell/WindowChromeHelper.cs`, `src/RhythmSync.App/ViewModels/Page.cs`
- Modify: `src/RhythmSync.App/MainWindow.xaml(.cs)`, `src/RhythmSync.App/ViewModels/MainViewModel.cs`, `src/RhythmSync.App/ViewModels/ClientViewModel.cs` (add `JoinedHostName`), `src/RhythmSync.App/App.xaml.cs` (initial page)
- Test: `tests/RhythmSync.App.Tests/ShellTests.cs`, `tests/RhythmSync.App.Tests/MainWindowSmokeTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2; existing `MainViewModel(ClientViewModel client, Action<Action>? post = null, HostTabViewModel? host = null)`, `HostTabViewModel.IsHosting/Current`, `HostViewModel.Clients` (`ClientRowViewModel.State`), `ClientViewModel.State/StatusText`.
- Produces:
```csharp
public enum Page { Host, Join, Settings }
// MainViewModel additions
public Page SelectedPage { get; set; }                                   // observable
public string FooterText { get; }                                        // observable, recomputed on host/client/rows changes
public static Page InitialPage(string? lastHostName) => lastHostName is null ? Page.Host : Page.Join;
public const string FooterNotConnected = "Not connected";
public const string FooterHostingWaiting = "Hosting · waiting for PCs";
// FooterText rules (spec §3.1): hosting & 0 clients → FooterHostingWaiting; hosting & n>0 → $"Hosting · {inSync} of {n} in sync";
//   client State != Disconnected && JoinedHostName != null → $"{client.StatusText} · {JoinedHostName}"; else FooterNotConnected
// ClientViewModel addition
public string? JoinedHostName { get; }   // set to SelectedHost.Name when a join succeeds; null after Leave or when State becomes Disconnected
// WindowChromeHelper
public static class WindowChromeHelper { public static void Attach(Window w, FrameworkElement maximizeButton); } // WM_NCHITTEST → HTMAXBUTTON over the button; DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE=20, 1), failures ignored
```
MainWindow:
- `WindowChrome` with caption 32, resize border 6 and `UseAeroCaptionButtons=false`.
- Default 960×640, minimum 760×520.
- Title bar: app glyph, "RhythmSync", and 46 px-wide minimize/maximize/close icon buttons (close hover = Danger tint), each with `AutomationProperties.Name`.
- Sidebar 200 px, `SurfaceBrush`, with `NavItem` RadioButtons bound to `SelectedPage`. Glyphs: Host `E7F5`, Join `E71B`, Settings `E713` pinned to the bottom. The footer `CardBorder` shows `FooterText`.
- Content: a `ContentControl` that swaps the Host page (existing `HostTabView`), the Join page (existing `ClientView` with the two banners from spec §3, visible as today) and the Settings page (a placeholder `TextBlock` "Settings" until Task 7).
- Close still hides to the tray.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] [InlineData(null, Page.Host)] [InlineData("Living Room", Page.Join)]
public void Initial_page_follows_last_host(string? last, Page expected)
[Fact] public void Footer_not_connected_by_default()                      // == "Not connected"
[Fact] public void Footer_while_hosting_without_clients()                 // fake runtime hosting, 0 rows → "Hosting · waiting for PCs"
[Fact] public void Footer_while_hosting_counts_in_sync_rows()             // 3 rows, 2 InSync → "Hosting · 2 of 3 in sync"; a row flips to Degraded → "Hosting · 1 of 3 in sync"
[Fact] public void Footer_when_joined()                                    // successful join to host "Living Room", State InSync → "In sync · Living Room"; after Leave → "Not connected"
// separate class MainWindowSmokeTests marked [Collection("Ui")] (collections are class-level):
[Fact] public void MainWindow_renders_each_page_without_errors() // construct MainWindow (not shown), DataContext = MainViewModel with fakes, for each Page: Render → Errors empty, no exceptions
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~ShellTests"`
- [ ] **Step 3: Implement**, following `screens.html` for layout. `App.xaml.cs` sets `SelectedPage = MainViewModel.InitialPage(settings.LastHostName)`.
- [ ] **Step 4: Run to verify pass**, plus the full default suite (existing `MainViewModelTests` and `ViewMarkupTests` stay green).
- [ ] **Step 5: Commit**: `feat(app): custom title bar, sidebar navigation and status footer`

---

### Task 5: Host pages restyle

**Files:**
- Modify: `src/RhythmSync.App/Views/HostTabView.xaml`, `src/RhythmSync.App/Views/HostView.xaml`, `src/RhythmSync.App/Views/StatsPanel.xaml`
- Test: `tests/RhythmSync.App.Tests/HostPagesSmokeTests.cs` (+ the existing `ViewMarkupTests` must pass unchanged)

**Interfaces:**
- Consumes: Tasks 1–2 styles and components; the existing bindings in these views (keep every binding path and every `Delay=300`). `StatusKinds.From` for each row (bind `Kind` through a small `IValueConverter` `SyncStateToKindConverter` in `Controls/`, created in this task).
- Layout per spec §4.1/§4.2 and `screens.html`:
  - **Idle:** a centred 360 px `CardBorder` with the spec copy and a full-width `PrimaryButton` "Start hosting". `ErrorText` in Danger.
  - **Running:**
    - header (session name `TitleText` + `StatusChip` Hosting "Hosting" + `DangerButton` "Stop hosting");
    - `Banner` (AudioBanner, "Open sound settings");
    - code card (`DisplayText` code + "Share this with the other PCs");
    - settings card (latency, helper text, speaker picker, session name);
    - "Connected PCs" + count + "Show stats" `ToggleSwitch`;
    - empty state;
    - one `CardBorder` per client (name `SubtitleText`, chip, SyncErrorText in `StatText`, CongestedText in Warning, Volume and Offset sliders, then `StatsPanel` as a 5-column grid with values in `StatText` when ShowStats is on).
  - The page scrolls vertically when content exceeds the window.
- Every name `TextBlock` (session name, client name) has `TextTrimming="CharacterEllipsis"`.

- [ ] **Step 1: Write the failing tests** (`[Collection("Ui")]`)

```csharp
[Fact] public void Idle_host_page_renders_without_errors()
[Fact] public void Running_host_page_with_clients_and_stats_renders_without_errors() // 2 rows (InSync, Degraded+congested), ShowStats true, banner visible
[Fact] public void Running_host_page_with_eight_clients_scrolls()    // 8 rows at 960×640 → a ScrollViewer with ScrollableHeight > 0
[Fact] public void Long_names_ellipsize_at_minimum_size()           // 64-char session and client names, Render(…, 760, 520) → those TextBlocks have TextTrimming CharacterEllipsis and ActualWidth ≤ their container's ActualWidth
[Fact] public void Host_markup_uses_the_design_components()         // parse XAML text: "Stop hosting" button Style = DangerButton; "Start hosting" = PrimaryButton; contains local:StatusChip and local:Banner
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~HostPagesSmokeTests|FullyQualifiedName~ViewMarkupTests"`
- [ ] **Step 3: Implement** the restyle (XAML only, plus the converter).
- [ ] **Step 4: Run to verify pass**, plus the full default suite.
- [ ] **Step 5: Commit**: `feat(app): restyle host pages`

---

### Task 6: Join page restyle

**Files:**
- Modify: `src/RhythmSync.App/Views/ClientView.xaml(.cs)` (wire `PinBox.Submitted` → `JoinCommand`)
- Test: `tests/RhythmSync.App.Tests/JoinPageSmokeTests.cs` (+ the existing `ViewMarkupTests`, including `The_client_tab_shows_volume_and_offset_without_sliders`)

**Interfaces:**
- Consumes: Tasks 1–3; existing `ClientViewModel` bindings; `JoinedHostName` (Task 4).
- Layout per spec §4.3:
  - "Join a host" (`TitleText`);
  - "Hosts on this network" (`CaptionText`);
  - host list as `CardBorder` items (selected: Accent border and ring), with `HostListMessage` as the empty or error text;
  - "Code" + `PinBox` (`Code="{Binding Code}"`) + `PrimaryButton` "Join";
  - `ErrorText` in Danger;
  - a status card (visible when `IsConnected`): `Joined · {JoinedHostName}` (`SubtitleText`), `StatusChip` (StatusText, kind from State), `SecondaryButton` "Leave", `StatusMessage` in Warning, a `StatText` line of `SyncErrorText` · `DriftText` · `CountersText`, and `VolumeText` · `OffsetText` + `SetOnHostText` (italic caption).
- Host and client name `TextBlock`s get `TextTrimming="CharacterEllipsis"`.

- [ ] **Step 1: Write the failing tests** (`[Collection("Ui")]`)

```csharp
[Fact] public void Join_page_empty_renders_without_errors()        // no hosts → HostListMessage "Looking for hosts…" visible
[Fact] public void Join_page_with_hosts_renders_without_errors()
[Fact] public void Join_page_joined_renders_without_errors()       // joined state with stats
[Fact] public void Enter_in_pin_box_runs_join_when_enabled()       // host selected, code complete → PinBox Submitted → JoinCommand executed (fake join called once)
[Fact] public void Long_host_names_ellipsize_at_minimum_size()      // 64-char host name at 760×520
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~JoinPageSmokeTests|FullyQualifiedName~ViewMarkupTests"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass**, plus the full default suite.
- [ ] **Step 5: Commit**: `feat(app): restyle join page with pairing-code boxes`

---

### Task 7: Settings page, start with Windows and start minimized

**Files:**
- Modify: `src/RhythmSync.Core/Config/AppSettings.cs` (+ `bool StartMinimized = false`, last positional parameter with a default)
- Create: `src/RhythmSync.App/Services/IStartupRegistration.cs`, `src/RhythmSync.App/Services/RegistryStartupRegistration.cs`, `src/RhythmSync.App/ViewModels/SettingsViewModel.cs`, `src/RhythmSync.App/Views/SettingsView.xaml(.cs)`
- Modify: `src/RhythmSync.App/StartupOptions.cs` (+ `--minimized`), `src/RhythmSync.App/App.xaml.cs` (Settings page; don't show the window when minimized), `MainWindow.xaml` (replace the Settings placeholder)
- Test: `tests/RhythmSync.App.Tests/SettingsViewModelTests.cs`, `StartupRegistrationTests.cs`, `SettingsSmokeTests.cs`, extend `StartupOptionsTests.cs`; extend the Core `SettingsStoreTests` with old JSON without the field → `StartMinimized == false`

**Interfaces:**
- Consumes: `SettingsPersister.Update(Func<AppSettings, AppSettings> change, bool immediate = false)`, `AppSettings` record (`DeviceName`, …).
- Produces:
```csharp
public interface IStartupRegistration { bool IsEnabled { get; } void Enable(); void Disable(); }   // Enable/Disable may throw
public sealed class RegistryStartupRegistration : IStartupRegistration {
  public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  public const string ValueName = "RhythmSync";
  public RegistryStartupRegistration(string exePath);
  public static string RunValue(string exePath) => $"\"{exePath}\" {StartupOptions.MinimizedSwitch}";
}
public sealed record StartupOptions(bool SkipFirewall, bool Minimized = false) { public const string MinimizedSwitch = "--minimized"; }
public sealed partial class SettingsViewModel : ObservableObject {
  public const int MaxNameLength = 64;
  public const string NameError = "Enter a name (up to 64 characters).";
  public const string StartupError = "Couldn't change the startup setting.";
  public SettingsViewModel(SettingsPersister settings, IStartupRegistration startup, string version, Action<string> openFolder, string dataDir);
  public string DeviceName { get; set; }              // edited text
  public IRelayCommand CommitNameCommand { get; }       // trim; 1..64 → persist + clear NameErrorText; else NameErrorText = NameError, nothing saved
  public string? NameErrorText { get; }
  public bool StartWithWindows { get; set; }          // initial = startup.IsEnabled; set → Enable/Disable; on exception revert (raise PropertyChanged) and StartupErrorText = StartupError
  public string? StartupErrorText { get; }
  public bool StartMinimized { get; set; }            // persists AppSettings.StartMinimized
  public string VersionText { get; }                  // $"RhythmSync {version}"
  public IRelayCommand OpenLogFolderCommand { get; }  // openFolder(Path.Combine(dataDir, "logs"))
  public IRelayCommand OpenSettingsFolderCommand { get; } // openFolder(dataDir)
}
public static bool App.ShouldShowWindowAtStartup(StartupOptions o, AppSettings s) => !(o.Minimized || s.StartMinimized); // internal static, tested
```
SettingsView per spec §4.4 and `screens.html`: a `CardBorder` with three rows (label `SubtitleText`, caption, control on the right, errors in Danger), then an About card. The name TextBox commits on Enter and on lost focus. `App.xaml.cs` builds the version from `AssemblyInformationalVersionAttribute`, and `openFolder` = `Process.Start("explorer.exe", $"\"{path}\"")`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Theory] [InlineData("")] [InlineData("   ")] [InlineData(null)]                         // 65 chars as a separate Fact
public void Invalid_name_is_not_saved(string? name)                   // NameErrorText == NameError; persisted DeviceName unchanged
[Fact] public void Valid_name_is_trimmed_and_saved()                  // "  Kitchen  " → persisted "Kitchen", NameErrorText null
[Fact] public void Start_with_windows_reads_and_writes_registration() // fake IsEnabled true → StartWithWindows true; set false → fake.Disable called
[Fact] public void Failed_registration_reverts_toggle_and_shows_error() // fake.Enable throws → StartWithWindows false, StartupErrorText == StartupError
[Fact] public void Start_minimized_is_persisted()
[Fact] public void Folder_buttons_open_logs_and_data_folders()        // openFolder receives dataDir\logs and dataDir
[Fact] public void Run_value_quotes_paths_with_spaces()              // RunValue(@"C:\Program Files\RhythmSync\RhythmSync.App.exe") == "\"C:\\Program Files\\RhythmSync\\RhythmSync.App.exe\" --minimized"
[Theory] [InlineData(false,false,true)] [InlineData(true,false,false)] [InlineData(false,true,false)]
public void Window_shown_unless_minimized(bool flag, bool setting, bool shown)
[Fact] public void Parses_minimized_switch()                          // StartupOptions.Parse(["--minimized"]).Minimized
// SettingsSmokeTests (class marked [Collection("Ui")]):
[Fact] public void Settings_page_renders_without_errors()
```
`RegistryStartupRegistration` is NOT exercised against the real registry in tests; only `RunValue` is tested.

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~Settings|FullyQualifiedName~StartupRegistration|FullyQualifiedName~StartupOptions"`
- [ ] **Step 3: Implement.** `RegistryStartupRegistration` uses `Registry.CurrentUser.OpenSubKey(RunKey, writable: true)`, and `IsEnabled` = the value exists.
- [ ] **Step 4: Run to verify pass**, plus the full default suite.
- [ ] **Step 5: Commit**: `feat(app): settings page with start-with-Windows and start-minimized`

---

### Task 8: Tray flyout

**Files:**
- Create: `src/RhythmSync.App/Flyout/{FlyoutPlacement,FlyoutToggle,TrayFlyoutViewModel}.cs`, `src/RhythmSync.App/Flyout/TrayFlyoutWindow.xaml(.cs)`
- Modify: `src/RhythmSync.App/Services/TrayIcon.cs` (left-click event; right-click keeps the menu), `src/RhythmSync.App/App.xaml.cs` (compose the flyout)
- Test: `tests/RhythmSync.App.Tests/{FlyoutPlacementTests,FlyoutToggleTests,TrayFlyoutViewModelTests,FlyoutSmokeTests}.cs`

**Interfaces:**
- Consumes: `HostTabViewModel` (`IsHosting`, `Current`, `StopCommand`), `HostViewModel` (`SessionName`, `PairingCode`, `Clients`), `ClientRowViewModel` (`Name`, `State`, `SyncErrorText`, `Volume` setter, `VolumeText`), `ClientViewModel` (`State`, `StatusText`, `JoinedHostName`, `SyncErrorText`, `DriftText`, `VolumeText`, `OffsetText`, `LeaveCommand`), `StatusChip`/`StatusKinds` (Task 2).
- Produces:
```csharp
public static class FlyoutPlacement {
  // all DIPs; returns the top-left point and the clamped height
  public static (Point TopLeft, double Height) Compute(Rect workArea, Rect screenBounds, Size flyout, double margin = 12);
}
// Taskbar edge = the side where workArea is inset from screenBounds (bottom if none). The flyout sits in the working-area
// corner on that edge, nearest the right side for bottom and top, nearest the bottom for left and right;
// Height = min(flyout.Height, workArea.Height − 2·margin); the result always lies fully inside workArea.
public sealed class FlyoutToggle {
  public const double ReopenGuardMs = 300;
  public bool IsOpen { get; }
  public bool OnTrayClick(DateTime now);      // returns true if the flyout should open now; closes if open
  public void OnDeactivated(DateTime now);    // marks closed at now
  public void OnClosedByAction();             // Esc / Open RhythmSync / Stop / Leave
}
// OnTrayClick: if IsOpen → close, return false; else if (now − lastDeactivated) < ReopenGuardMs → return false; else open, return true.
public enum FlyoutMode { Idle, Hosting, Joined }
public sealed partial class TrayFlyoutViewModel : ObservableObject {
  public const string IdleText = "Host this PC's audio, or join a host from the app.";
  public const string WaitingText = "Waiting for PCs to join…";
  public TrayFlyoutViewModel(HostTabViewModel host, ClientViewModel client, Action openMainWindow, Action<Action>? post = null);
  public FlyoutMode Mode { get; }
  public string Title { get; }                // Hosting: session name; Joined: JoinedHostName; Idle: "RhythmSync"
  public string? PairingCode { get; }         // Hosting only
  public ObservableCollection<ClientRowViewModel> Clients { get; } // Hosting: mirrors host.Current.Clients (same instances, so the volume slider uses the existing row → host path)
  public IRelayCommand StopHostingCommand { get; } // host.StopCommand
  public IRelayCommand LeaveCommand { get; }       // client.LeaveCommand
  public IRelayCommand OpenCommand { get; }        // openMainWindow()
  public event Action? CloseRequested;             // raised after Open, Stop or Leave
}
```
TrayFlyoutWindow per spec §5 and `flyout.html`:
- Borderless, `Topmost`, `ShowInTaskbar=false`, `AllowsTransparency=true`, 320 px wide, `MaxHeight` from `Compute`, inner `ScrollViewer`.
- Slide and fade in over 150 ms when animations are enabled; Esc closes.
- On `Deactivated` → `FlyoutToggle.OnDeactivated` + Hide.
- App composition:
  - `TrayIcon.LeftClick` → if `toggle.OnTrayClick(DateTime.UtcNow)`, compute placement on the monitor under the cursor (convert screen pixels to DIPs with the flyout's DPI via `VisualTreeHelper.GetDpi` after `SourceInitialized`; if no monitor is found, use the primary screen), Show + Activate. Otherwise Hide.
  - All in try/catch with a log, never throwing into the tray handler.
- `TrayIcon` keeps its constructor and adds `public event Action? LeftClick` (raised from `NotifyIcon.MouseClick` when `Button == Left`).

- [ ] **Step 1: Write the failing tests**

```csharp
// FlyoutPlacementTests
[Fact] public void Bottom_taskbar_puts_flyout_bottom_right()   // screen 0,0,1920,1080; work 0,0,1920,1032; flyout 320×400 → TopLeft (1920−12−320, 1032−12−400)
[Fact] public void Top_taskbar_puts_flyout_top_right()         // work 0,48,1920,1032 → (1588, 60)
[Fact] public void Left_taskbar_puts_flyout_bottom_left()      // work 48,0,1872,1080 → (60, 1080−12−400)
[Fact] public void Right_taskbar_puts_flyout_bottom_right_of_work_area() // work 0,0,1872,1080 → (1872−12−320, 668)
[Fact] public void Tall_flyout_is_capped_to_work_area()        // flyout 320×2000, work height 1032 → Height 1008, TopLeft.Y 12
[Fact] public void Second_monitor_with_negative_origin()      // screen −1920,0,1920,1080 work −1920,0,1920,1040 → result inside work area
// FlyoutToggleTests
[Fact] public void Click_opens_when_closed()
[Fact] public void Click_closes_when_open()
[Fact] public void Click_right_after_deactivation_does_not_reopen() // OnDeactivated(t), OnTrayClick(t+100ms) → false, IsOpen false
[Fact] public void Click_after_guard_reopens()                     // OnTrayClick(t+400ms) → true
// TrayFlyoutViewModelTests
[Fact] public void Idle_mode_when_neither_hosting_nor_joined()     // Mode Idle, Title "RhythmSync"
[Fact] public void Hosting_mode_mirrors_session_code_and_rows()     // Title == session name, PairingCode, Clients same instances; new row added on host → appears
[Fact] public void Hosting_mode_with_no_clients_shows_waiting()     // Clients empty (view shows WaitingText)
[Fact] public void Joined_mode_shows_host_name()                    // Title == JoinedHostName
[Fact] public void Flyout_volume_change_reaches_the_host_path()     // set Clients[0].Volume = 0.5f → fake IHostControl.SetDeviceSettingsAsync(id, 0.5, offset) called (existing row behaviour); NaN ignored
[Fact] public void Actions_invoke_commands_and_request_close()      // Open → openMainWindow called + CloseRequested; Stop → host stop invoked; Leave → client leave invoked
// FlyoutSmokeTests [Collection("Ui")]
[Fact] public void Flyout_renders_each_mode_without_errors()       // Idle, Hosting (2 rows), Joined
[Fact] public void Flyout_with_eight_clients_scrolls_within_max_height() // 8 rows, MaxHeight 500 → ScrollViewer ScrollableHeight > 0
```

- [ ] **Step 2: Run to verify failure:** `--filter "FullyQualifiedName~Flyout"`
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify pass**, plus the full default suite.
- [ ] **Step 5: Commit**: `feat(app): tray flyout with status and quick controls`

---

### Task 9: Acceptance walkthrough and final verification

**Files:**
- Modify: `docs/acceptance.md` (new section "UI walkthrough (dark studio)"), `README.md` (one line: the tray icon's left-click opens the flyout; the Settings page; the `--minimized` switch)

- [ ] **Step 1: Write the walkthrough rows** from spec §8.3, each with a pass box:
  - each page matches its mockup (with the file names);
  - hover, press and focus states;
  - the flyout over a bottom taskbar and, where practical, a top or side one;
  - 150% scaling;
  - snap layouts on maximize;
  - the dark title bar;
  - Esc and click-away close the flyout, and a tray click while it is open closes it and doesn't reopen;
  - `--minimized` starts to the tray;
  - Start with Windows survives a sign-out/sign-in and turning it off removes the Run value (`reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v RhythmSync`);
  - 8 connected PCs scroll on the host page and in the flyout;
  - 64-character names ellipsize.
- [ ] **Step 2: Verify**: `dotnet build -c Release` gives 0 warnings; the full default suite passes; `dotnet test --filter "Category=Smoke"` passes. Do not launch the GUI.
- [ ] **Step 3: Commit**: `docs: UI walkthrough for the dark studio redesign`
