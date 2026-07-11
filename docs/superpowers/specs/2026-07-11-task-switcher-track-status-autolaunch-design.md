# Task Switcher, Track Status Box, and Auto-Launch — Design Spec

**Date:** 2026-07-11
**Status:** Draft
**Type:** Feature work (3 Tier-1 items from competitive analysis)
**Author:** EyeCtrl Team
**Reference:** [`docs/COMPETITIVE_FEATURE_ANALYSIS.md`](../../COMPETITIVE_FEATURE_ANALYSIS.md) — gap analysis vs. OptiKey and Tobii Dynavox Computer Control (TD Control)

## Problem

A competitive review of OptiKey (open-source) and Tobii Dynavox Computer Control (TD Control v1.80.1, installed on this machine) surfaced three Tier-1 gaps in EyeCtrl — capabilities both competitors have that EyeCtrl lacks, all low-to-moderate effort, all on-mission for a computer-control + eye-care tool:

1. **Auto-launch is a no-op.** `GeneralSettings.StartOnBoot` is defined and toggled in two UI surfaces, but no code anywhere in `src/` writes the registry Run key, creates a Startup-folder shortcut, or otherwise registers the app for Windows startup. The flag round-trips through JSON and the UI only. Verified by grepping `StartOnBoot`, `Registry`, `IShellLink`, `.lnk`, `Software\Microsoft\Windows\CurrentVersion\Run` across the tree — zero consumers.

2. **No window/app switching.** EyeCtrl has no taskbar access, no Alt-Tab equivalent, no app switcher. The toolbar exposes exactly five buttons (Pause, SelectionMode, Keyboard, Calibrate, Settings). Switching between applications requires gaze-clicking inside the Windows taskbar — a small-target exercise that defeats the purpose of a gaze-control tool.

3. **No track-status feedback.** When tracking is poor (user out of the track box, head too far/close, eyes lost), EyeCtrl gives no diagnostic. The user just sees a cursor that behaves erratically. TD Control's Track Status box shows eye position in the track box, distance-from-screen quality, and presence — the canonical reference for this.

EyeCtrl's differentiators (eye-care subsystem, ONNX SmallLM prediction, UIA snap-to-control, SDK for cooperating apps) are unaffected and not in scope.

## Goals

- **Feature 1 (Auto-launch):** toggling `StartOnBoot` actually registers/unregisters the app with Windows startup. Survives the single-file-publish extraction-path quirk. Reconciles on every launch.
- **Feature 2 (Task Switcher):** a fullscreen, gaze-dwellable overlay listing open windows; selecting one brings it to the front. Triggered from the main toolbar (a new 6th button). Out of the way when not in use (dismissed after a selection or cancel).
- **Feature 3 (Track Status box):** a small, click-through, always-on-top corner overlay showing eye position in the track box (both eyes), user-present/away, distance-from-screen quality (green/amber/red), and live sample rate (Hz). Hidden when the provider has no track-status capability (mouse-as-gaze) or the tracker is disconnected.

## Non-Goals

- **No `InteractionAction` enum changes.** An earlier draft proposed adding window-management actions (Show Desktop, Alt+Tab, Minimize/Maximize/Close) to the action menu. **Removed by decision (2026-07-11)** — the Task Switcher alone covers the app-switching need, and dropping the enum changes avoids coupling to the drift-guard tests (`ActionMenuViewModelSymbolTests` asserts the glyph map ≡ all enum values; `InteractionActionEnumTests` hardcodes `PinNote == 9`). The action menu, `ActionMenuSettings`, and `GetActionSymbol` stay untouched.
- **No live window thumbnails in the Task Switcher (v1).** v1 ships icon + title tiles. Live DWM thumbnails (`DwmRegisterThumbnail` / `PrintWindow`) are deferred to a follow-up — a conscious scope line, not a regression.
- **No targeted window-under-gaze actions** (minimize/maximize/close the window at the gaze point). Removed with the action-menu changes. The Task Switcher only switches foreground focus.
- **No multi-monitor.** The Task Switcher covers the primary screen; gaze-driven cursor positioning remains single-primary-monitor (documented limitation in `Win32InputSimulator`).
- **No new `InteractionState`.** The Task Switcher follows the Keyboard toggle pattern (a toolbar message → overlay show/hide) rather than adding a dedicated state machine state. A state will be added only if the underlying gaze pipeline misbehaves behind the fullscreen cover (flagged in Risks).
- **No AAC / TTS / communication boards, localization, webcam gaze, or non-Tobii trackers.** These are Tier-3 items from the competitive analysis, explicitly declined.
- **No installer.** Startup registration is the app's runtime responsibility (there is no Inno Setup / WiX / MSI in the repo).

## Terminology

- **Track box** — the physical volume in front of the eye tracker within which the user's eyes are tracked. Tobii reports eye position normalized to this box (0..1 on each axis).
- **Run key** — `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`, the standard per-user startup-registration registry location.
- **Click-through overlay** — a `Topmost` WPF window with `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE` (via `OverlayWindow.IsClickThrough = true`) that displays information without intercepting gaze/mouse input. The Track Status box is click-through; the Task Switcher is **not** (it must receive dwell).
- **Capabilities** — a flag enum on `IGazeProvider` describing which optional data streams a provider can supply (eye position, head pose, user presence). The mouse provider reports `None`.

## Design

The three features are independent. Implementation is sequenced into three checkpoints (A: auto-launch, B: track status, C: task switcher), each building and testing green before the next.

### Feature 1 — Auto-Launch (Registry Run Key)

#### Approach

A new `IStartupRegistrar` seam in Core (mirroring `IInputSimulator` / `IMouseCoordinateMapper`), implemented in Platform with `Microsoft.Win32.Registry`. The toggle writes/removes a `Run`-key entry; on every app startup the registration is reconciled against the persisted setting.

The single-file publish model (`dotnet publish ... -p:PublishSingleFile=true`) extracts the bundle to a **temp directory at each launch**. `Environment.ProcessPath` returns the **stable** bundle apphost path (the `.exe` the user launched); `Assembly.Location` is the trap — it points into the **varying** temp extraction dir. So the registration stores `Environment.ProcessPath`, never `Assembly.Location`.

#### Reconcile-on-startup

`App.OnStartup` calls `registrar.SetStartOnBoot(settings.General.StartOnBoot)` **after `BuildServiceProvider` (line 77)** — the registrar is resolved from the DI container, so the call must be at line ≥ 80 (the first `GetRequiredService` resolve), e.g. co-located with the existing service resolves around line 84. This is idempotent: if enabled, it overwrites the entry with the current `Environment.ProcessPath`; if disabled, it removes any stale entry. Reconcile-on-startup is kept because the exe path can change between versions/installs (e.g. the user moves or reinstalls the app) — the reconcile refreshes the registry entry to match the current path each launch.

Mid-session toggle: subscribe `settings.General.PropertyChanged` for the `StartOnBoot` property → call `registrar.SetStartOnBoot(newValue)` so the change takes effect immediately, not just on next launch.

#### File layout

| File | Status | Purpose |
|------|--------|---------|
| `src/EyeCtrl.Core/Interfaces/IStartupRegistrar.cs` | NEW | `void SetStartOnBoot(bool enabled); bool IsRegistered { get; }` — Core seam (testable, framework-agnostic) |
| `src/EyeCtrl.Platform/Services/StartupCommandValue.cs` | NEW | **Pure** helper `string BuildCommandValue(string exePath)` → quoted path. The testable unit; registry I/O itself is not unit-tested (mirrors `IMouseCoordinateMapper` pattern) |
| `src/EyeCtrl.Platform/Services/WindowsStartupRegistrar.cs` | NEW | `IStartupRegistrar` impl using `Microsoft.Win32.Registry`. Key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value name `"EyeCtrl"`, data = `BuildCommandValue(Environment.ProcessPath)`. `SetStartOnBoot(true)` writes/overwrites; `false` deletes (ignores missing-key). `IsRegistered` reads the value |
| `src/EyeCtrl.Platform/EyeCtrl.Platform.csproj` | UNCHANGED | `Microsoft.Win32.Registry` is already transitively available via `UseWPF`/WindowsDesktop SDK on `net10.0-windows` — **no `PackageReference` needed**. (Caveat: `EyeCtrl.Core` targets plain `net10.0`; registry code stays in Platform, not Core.) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | MODIFIED | Register `IStartupRegistrar` → `WindowsStartupRegistrar` as a singleton (next to `IInputSimulator`, ~line 66) |
| `src/EyeCtrl.App/App.xaml.cs` | MODIFIED | `OnStartup`: reconcile `SetStartOnBoot(settings.General.StartOnBoot)` after `BuildServiceProvider` (line 77), co-located with existing service resolves (~line 84). Subscribe `settings.General.PropertyChanged` for live toggle |

#### Tests

| File | Purpose |
|------|---------|
| `tests/EyeCtrl.Platform.Tests/Services/StartupCommandValueTests.cs` | `BuildCommandValue` quoting / spaces / empty path via NFluent |
| `tests/EyeCtrl.Core.Tests/Settings/GeneralSettingsDefaultsTests.cs` (NEW) | `StartOnBoot` default `false`, `StartPaused` default, `GazeProviderType` default |

No registry-writing test (would hit the real HKCU hive; the repo has no registry-abstraction or mock framework today).

### Feature 2 — Task Switcher (fullscreen overlay, toolbar-triggered)

#### Approach

A new 6th toolbar button (`TaskSwitcher`, glyph `🪟` U+1FA9F) toggles a fullscreen modal overlay that lists open windows as large dwellable tiles. Selecting a tile brings that window to the front and dismisses the overlay. A Cancel/Back target dismisses without switching.

> **Glyph note:** `🪟` is non-BMP (SMP), but this is not a first — `PinNote` (📌, U+1F4CC via surrogate pair `\uD83D\uDCCC`) already establishes a working non-BMP precedent in `ActionMenuViewModel.GetActionSymbol`. Whatever font fallback handles `📌` handles `🪟`. If a pure-BMP toolbar-consistent alternative is preferred, `\u229E` (⊞) or `\u25A4` (▤) are fallbacks; otherwise keep `🪟`.

This follows the **Keyboard toggle pattern** (a toolbar button sends a message; `EventCoordinator` shows/hides an overlay) rather than the action-menu pattern. It touches the existing action-dispatch chain not at all — no `InteractionAction` changes, no `HandleActionRequested` cases, no drift-guard test updates.

#### `IWindowManager` (slimmed)

Only two methods:

```csharp
public interface IWindowManager
{
    IReadOnlyList<AppWindow> EnumerateOpenWindows();
    void BringToFront(IntPtr hwnd);
}
```

`AppWindow` is a record `(IntPtr Hwnd, string Title, uint ProcessId, string ProcessName)`.

`Win32WindowManager` enumerates via `EnumWindows` + `GetWindowText` + `GetWindowThreadProcessId` + `Process.GetProcessById`, filtering:
- visible (`IsWindowVisible`),
- top-level (no owner — `GetWindow(GW_OWNER) == IntPtr.Zero`),
- **non-tool-window (`(GetWindowLong(GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0`)** — this is the key filter that excludes EyeCtrl's own overlays,
- has a non-empty title.

> **Filter on `WS_EX_TOOLWINDOW`, not `WS_EX_TRANSPARENT`.** `OverlayWindow.SetOverlayInteraction` (`OverlayWindow.cs:42`) applies `WS_EX_TOOLWINDOW` **unconditionally** to every overlay, while `WS_EX_TRANSPARENT` is conditional on the `clickThrough` arg. The Task Switcher itself is non-click-through (it must receive dwell), so it has `WS_EX_TOOLWINDOW` but **not** `WS_EX_TRANSPARENT`. Filtering on `WS_EX_TRANSPARENT` would fail to exclude the switcher's own window (self-inclusion bug) and any future non-click-through overlay. All 19 existing overlay subclasses extend `OverlayWindow`, so they all inherit `WS_EX_TOOLWINDOW` and are excluded by this predicate. **The Task Switcher window must subclass `OverlayWindow`** to inherit this style.

`BringToFront` calls `SetForegroundWindow`, with `AllowSetForegroundWindow(ASFW_ANY)` first internally (a known Win32 focus-steal guardrail — if a foreground window owned by another process refuses the switch, `ASFW_ANY` permits it). Both P/Invokes live in the Platform-layer `Win32WindowManager` (or `NativeMethods`), **not** in `EventCoordinator` — matching the dominant pattern where Win32 lives in Platform (`Win32InputSimulator`, `MouseCoordinateMapper`, `ControlFinder`). `EventCoordinator` itself is P/Invoke-free. A few in-file exceptions exist elsewhere (`ExternalDwellService` uses `WindowFromPoint`/`GetAncestor`; `GlassBackdropWindow` uses `user32`/`dwmapi`; `App.xaml.cs` uses `GetDpiForSystem`) — the placement of `Win32WindowManager` in `Platform/Services/` follows the dominant convention, not the exceptions. The coordinator calls `_context.WindowManager.BringToFront(hwnd)`.

#### Overlay

`TaskSwitcherWindow` is fullscreen (`SystemParameters.PrimaryScreenWidth/Height`), `Topmost`, `WS_EX_NOACTIVATE` (no focus steal), **not** click-through (must receive dwell input, like the keyboard). A grid of large tiles; each tile shows the process icon + window title.

`TaskSwitcherViewModel` extends `DwellOverlayViewModelBase` (the established base for dwell-driven overlays). It builds tiles from `IWindowManager.EnumerateOpenWindows()` and registers each as a `DwellTarget`. On activation it sends a `TaskSwitcherSelectionMessage(IntPtr hwnd)`; the VM also exposes a Cancel/Back target.

> **Dwell time:** Task Switcher tiles are functionally closest to toolbar buttons (large targets, comparable misclick cost). **Reuse `DwellSettings.ToolbarButtonDwellMs` (default 700 ms, range 200–2000)** as the per-target dwell passed to each `DwellTarget` — the same field `ToolbarViewModel.cs:55` uses. The VM constructor passes `settings.Dwell.DwellCooldownMs` (the global re-arm cooldown, default 400 ms) to the base, matching `ToolbarViewModel`. No new `DwellSettings` field is needed unless independent tunability is later desired.

`IAppIconProvider` + `Win32AppIconProvider` resolve a per-process icon via `System.Drawing.Icon.ExtractAssociatedIcon(process.MainModule.FileName)` → `ImageSource`. (Note: `EyeCtrl.App` already has `UseWindowsForms`, so `System.Drawing` is available.)

#### File layout

| File | Status | Purpose |
|------|--------|---------|
| `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` | NEW | `EnumerateOpenWindows()` + `BringToFront(hwnd)` |
| `src/EyeCtrl.Core/Models/AppWindow.cs` | NEW | `record AppWindow(IntPtr Hwnd, string Title, uint ProcessId, string ProcessName)` |
| `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | NEW | `IWindowManager` impl via `EnumWindows` etc. |
| `src/EyeCtrl.Platform/Input/NativeMethods.cs` | MODIFIED | Add P/Invokes: `EnumWindows` (+ `EnumWindowsProc` delegate), `GetWindowText`/`GetWindowTextLength`, `GetWindowThreadProcessId`, `IsWindowVisible`, `GetWindowLong`/`GetWindow` (owner/style), `SetForegroundWindow`, `AllowSetForegroundWindow`. Constants: `GW_OWNER`, `GWL_EXSTYLE`, `WS_EX_TOOLWINDOW`, `ASFW_ANY` |
| `src/EyeCtrl.Core/Interfaces/IAppIconProvider.cs` | NEW | `ImageSource? GetIcon(uint processId)` |
| `src/EyeCtrl.Platform/Services/Win32AppIconProvider.cs` | NEW | `IAppIconProvider` impl via `Icon.ExtractAssociatedIcon(process.MainModule.FileName)`. **Must wrap in try/catch** — `Process.MainModule` throws `Win32Exception` (Access Denied) for elevated or other-user processes; fall back to a default icon so one inaccessible window doesn't blank the whole switcher |
| `src/EyeCtrl.ViewModels/TaskSwitcherViewModel.cs` | NEW | `DwellOverlayViewModelBase` subclass; builds tiles from `EnumerateOpenWindows()`; activation → `TaskSwitcherSelectionMessage` |
| `src/EyeCtrl.ViewModels/TaskSwitcherTileViewModel.cs` | NEW | `{ IntPtr Hwnd, string Title, ImageSource? Icon, PhysicalPoint Position, double DwellProgress }` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | MODIFIED | Add `TaskSwitcherToggledMessage` (toolbar → coordinator) and `TaskSwitcherSelectionMessage(IntPtr Hwnd)` (tile → coordinator) |
| `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` | MODIFIED | Add `("TaskSwitcher", "🪟")` to the button array (line 38); add `case "TaskSwitcher": Messenger.Send(new TaskSwitcherToggledMessage()); break;` to `OnTargetActivated` (line 98) |
| `src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml` + `.xaml.cs` | NEW | Fullscreen, topmost, `WS_EX_NOACTIVATE`, **not** click-through; grid of tiles |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` | MODIFIED | Add `TaskSwitcherWindow TaskSwitcher { get; }` property |
| `src/EyeCtrl.App/Services/OverlayManager.cs` | MODIFIED | Instantiate `TaskSwitcher` in `Initialize` |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` (`OverlayViewModels` record) | MODIFIED | Add `TaskSwitcherViewModel TaskSwitcher` field |
| `src/EyeCtrl.App/App.xaml.cs` | MODIFIED | Add `TaskSwitcherViewModel` to `ResolveViewModels` + the `OverlayViewModels` construction; add `TaskSwitcherVM` + `IWindowManager` to the `EventCoordinationContext` construction (~line 122) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | MODIFIED | Register `IWindowManager` → `Win32WindowManager`, `IAppIconProvider` → `Win32AppIconProvider`, and `TaskSwitcherViewModel` as singletons (VM next to the other VM registrations, ~line 132–152) |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | MODIFIED | Handle `TaskSwitcherToggledMessage` (populate VM from `WindowManager.EnumerateOpenWindows()`, show overlay) and `TaskSwitcherSelectionMessage` (`WindowManager.BringToFront(hwnd)`, hide overlay). Mirror the keyboard toggle handling |

#### Tests

| File | Purpose |
|------|---------|
| `tests/EyeCtrl.ViewModels.Tests/TaskSwitcherViewModelTests.cs` | Tile-building from a `FakeWindowManager` (private nested fake per repo convention); activation sends the correct message; cancel target works |
| `tests/EyeCtrl.Platform.Tests/Services/Win32WindowManagerTests.cs` | If pure filtering logic is extractable, test it; otherwise smoke-test `EnumerateOpenWindows` returns non-null list (no mocking lib; real Win32 call) |

### Feature 3 — Track Status Box (eye position + presence + distance)

#### Approach

Extend `IGazeProvider` with an optional `TrackStatusReceived` event and a `Capabilities` flag enum, both as **default interface members** so the 8 existing test/benchmark fakes (`FakeGazeProvider` / `TestGazeProvider` / `CountingGazeProvider` / `FakeGazeProviderForTests` / `StubGazeProvider` across 7 test/benchmark files) compile unchanged. The Tobii provider overrides them; the mouse provider relies on the defaults. A small click-through corner overlay reads the latest cached sample on a `DispatcherTimer` (~15 Hz) and renders the track-box dots, presence, and distance quality.

> **Why default interface members (DIMs):** `IGazeProvider` has 10 implementers (2 production + 8 test/benchmark doubles). Adding abstract members would break all 10. The codebase already uses DIMs for exactly this purpose — `IInputSimulator` (`IInputSimulator.cs:53,58`) defines `SendKeyPress(…, bool isExtendedKey)` and `SendModifiedKeyPress(…, bool isExtendedKey)` with default implementations, and the 6 test/benchmark `IInputSimulator` fakes do **not** implement those overloads; they compile via the DIMs. The same pattern applies here.

The data payload is a **new** `TrackStatusSample` struct — separate from `GazePoint` so the hot gaze path is not widened.

#### Data layer

```csharp
public readonly record struct TrackStatusSample(
    EyeSample Left, EyeSample Right,    // normalized XYZ in track box + validity
    double HeadDistanceMm, bool HeadDistanceValid,
    bool UserPresent,
    long TimestampMicroseconds);

public readonly record struct EyeSample(double X, double Y, double Z, bool IsValid);

[Flags]
public enum GazeProviderCapabilities { None = 0, EyePosition = 1, HeadPose = 2, UserPresence = 4 }
```

> **`EyeSample.IsValid` semantics (impl-time pin):** Tobii's `tobii_eye_position_normalized_t` exposes **separate validity bits per coordinate** (`left_x_validity`, `left_y_validity`, `left_z_validity`, …). The `EyeSample` record flattens these to a single `IsValid` bool. At impl time, set `IsValid = (all three coordinate validities are `TOBII_VALIDITY_VALID`)` — this matches the spec's "XY in track box" dot-rendering usage, so a dot is only drawn when both X and Y (and Z) are reliable. Otherwise dot rendering would mark a left eye valid when only its `z` coordinate is unreliable.

`IGazeProvider` gains (as default interface members):
```csharp
GazeProviderCapabilities Capabilities => GazeProviderCapabilities.None;
event Action<TrackStatusSample>? TrackStatusReceived { add { } remove { } }
```

#### Tobii provider extension

Behind `#if TOBII_AVAILABLE`, `TobiiStreamEngineProvider` subscribes to:
- `tobii_eye_position_normalized_subscribe` → left/right eye XYZ (0..1) in the track box,
- `tobii_head_pose_subscribe` → `position_xyz.z` is head distance from the tracker (mm),
- `tobii_user_presence_subscribe` → `PRESENT` / `AWAY`.

The three callback delegates are rooted (like the existing `_gazeCallback`) to prevent GC; unsubscribed in `CleanupDevice`. `Capabilities = EyePosition | HeadPose | UserPresence`.

**Highest-uncertainty item in the whole spec:** verify at implementation time that EyeCtrl's referenced `Tobii.StreamEngine.Net.dll` `Interop` surface exposes these subscribe methods + structs at compile time under `TOBII_AVAILABLE`. The SDK DLLs are present on this machine (`C:\Program Files\Tobii\Tobii EyeX\`), so `TOBII_AVAILABLE` is defined. If the wrapper lacks the methods, fall back to `[LibraryImport]` P-Invoke signatures against the stream-engine C API directly. Flagged, not blocking.

**Threading & caching:** the callbacks fire on the Tobii poll thread. **The cache lives inside `TobiiStreamEngineProvider`** (not a separate App-layer mediator) — adjacent to the callback that writes it. The pattern mirrors `samples/StreamEngineGazeTracker/MainWindow.xaml.cs:24-28,103-131`: a high-frequency callback writes a locked field + sets a `_hasNewSample` flag; the `DispatcherTimer` (~15 Hz, UI thread) reads it. The provider exposes the cached sample via a thread-safe accessor (e.g. `bool TryGetLatestStatus(out TrackStatusSample)`); the VM/overlay reads only from the timer, never from the callback. `MouseGazeProvider` has no track-status concept and relies on the DIM defaults (`Capabilities = None`, no event).

> **Lock choice (impl-time decision):** the existing `_lock` field guards device lifecycle and is held during `tobii_device_process_callbacks` (which fires the callbacks), so reusing it for the cache write extends an already-held critical section. Two options: (a) reuse `_lock` — the UI-thread `DispatcherTimer` reader accepts brief blocking on `_lock` (acceptable at 15 Hz); or (b) use a dedicated `volatile` field in a sealed wrapper class for single-word atomicity (avoids contention with the device-lifecycle lock, but `record struct` can't be `volatile` directly so it needs a wrapper). The choice doesn't change correctness — it's a perf/clarity tradeoff resolved at impl time.

#### Overlay

`TrackStatusWindow` extends `OverlayWindow`, small (~140×100 DIP), docked to a screen corner (default top-right), **click-through** (`IsClickThrough = true`, informational only), `Topmost`. Renders:
- a track-box rectangle with two eye dots (left/right eye position from `EyeSample.X/Y`),
- a presence indicator (green dot = present, dimmed = away),
- a distance/quality bar (green = optimal ~45–75 cm, amber = marginal, red = too close/far — thresholds TBD against PCEye 5 real data at impl time),
- a Hz readout (rolling 1-second callback count, mirroring the `StreamEngineGazeTracker` sample's rate counter).

A `DispatcherTimer` at ~66 ms (≈15 Hz) **owned by the `TrackStatusViewModel`** (not the window) pulls the cached sample via `TryGetLatestStatus` and raises PropertyChanged. Keeping the timer in the VM (not the window) makes the polling logic testable without a live WPF window. This avoids repainting at the full ~60–120 Hz sample rate.

**Degradation:** when `Capabilities == None` (mouse provider) or the tracker is disconnected, the overlay hides itself (or shows a "no tracker" placeholder). Gating in `EventCoordinator` mirrors `ShowBackdropIfEnabled` (line 567): `if (!settings.TrackStatus.Enabled || provider.Capabilities == None || !provider.IsConnected) return;`.

#### File layout

| File | Status | Purpose |
|------|--------|---------|
| `src/EyeCtrl.Core/Models/TrackStatusSample.cs` | NEW | `TrackStatusSample` + `EyeSample` record structs |
| `src/EyeCtrl.Core/Enums/GazeProviderCapabilities.cs` | NEW | `[Flags]` enum: `None, EyePosition, HeadPose, UserPresence` |
| `src/EyeCtrl.Core/Interfaces/IGazeProvider.cs` | MODIFIED | Add `TrackStatusReceived` event + `Capabilities` property as **default interface members** (mirroring `IInputSimulator.cs:53,58`) — `Capabilities => GazeProviderCapabilities.None`, `TrackStatusReceived { add{} remove{} }`. The 8 test/benchmark fakes compile unchanged. |
| `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs` | UNCHANGED | Relies on the DIM defaults (`Capabilities = None`, no event). No code change needed — the DIM handles it. |
| `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` | MODIFIED (behind `#if TOBII_AVAILABLE`) | Override `Capabilities` (`EyePosition\|HeadPose\|UserPresence`); subscribe to 3 new streams; root callbacks; **cache latest sample under `_lock` with `_hasNewSample` flag** (mirrors `samples/StreamEngineGazeTracker`); expose `TryGetLatestStatus(out TrackStatusSample)`; unsubscribe in `CleanupDevice` |
| `src/EyeCtrl.Core/Settings/TrackStatusSettings.cs` | NEW | `partial class : ObservableObject, IValidatable` with `bool Enabled` (+ optional `string Edge` default "TopRight") |
| `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | MODIFIED | Add `[ObservableProperty] private TrackStatusSettings _trackStatus = new();` + include in `EnumerateSubSettings()` |
| `src/EyeCtrl.ViewModels/TrackStatusViewModel.cs` | NEW | Plain `ObservableObject`; exposes eye-dot positions, presence, distance+quality color, Hz. **Owns the `DispatcherTimer` (~15 Hz)** that polls `gazeProvider.TryGetLatestStatus(...)` and raises PropertyChanged — timer ownership in the VM (not the window) for testability |
| `src/EyeCtrl.App/Overlays/TrackStatusWindow.xaml` + `.xaml.cs` | NEW | `OverlayWindow` subclass; click-through; track-box + dots + presence + distance bar + Hz. **No timer here** — the VM owns it |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | MODIFIED | Add a "Show track status" toggle (mirror the Backdrop toggle pattern) |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` + `OverlayManager.cs` | MODIFIED | Add `TrackStatusWindow TrackStatus` property + instantiate in `Initialize` |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` (`OverlayViewModels` record) | MODIFIED | Add `TrackStatusViewModel TrackStatus` field |
| `src/EyeCtrl.App/App.xaml.cs` | MODIFIED | Add `TrackStatusViewModel` to `ResolveViewModels` + `OverlayViewModels`; add `TrackStatusVM` to the `EventCoordinationContext` construction (~line 122). (**Checkpoint B adds only `TrackStatusVM` here** — `IWindowManager` is added in Checkpoint C.) |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | MODIFIED | Register `TrackStatusViewModel` as a singleton (next to the other VM registrations, ~line 132–152). No window-manager or icon-provider registration in this checkpoint — those are Feature 2 / Checkpoint C |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | MODIFIED | Gate show/hide on `settings.TrackStatus.Enabled` + capabilities + connection (mirror `ShowBackdropIfEnabled`, line 567); react to `settings.TrackStatus.PropertyChanged` |

#### Tests

| File | Purpose |
|------|---------|
| `tests/EyeCtrl.Core.Tests/Settings/TrackStatusSettingsDefaultsTests.cs` | `Enabled` default, validation |
| `tests/EyeCtrl.ViewModels.Tests/TrackStatusViewModelTests.cs` | VM mapping from a fake `TrackStatusSample` (eye-dot positions, distance-quality color thresholds, presence) using a private nested fake provider |

Tobii-provider changes are compile-verified only under `TOBII_AVAILABLE`; CI builds without the SDK exercise the mouse-provider degradation path (`Capabilities == None` → overlay hidden).

## Wiring summary (app-wide)

The three features share the same wiring touch-points. The **end state** after all three checkpoints:

1. **`AppBootstrapper.BuildServiceProvider`** — register new singletons: `IStartupRegistrar` (Checkpoint A), `TrackStatusViewModel` (Checkpoint B), `IWindowManager`, `IAppIconProvider`, `TaskSwitcherViewModel` (Checkpoint C). Every VM must be registered here because `ResolveViewModels` resolves them via `GetRequiredService<XxxViewModel>()` (lines 216–232) — an unregistered VM throws at startup.
2. **`OverlayViewModels` record** — add `TrackStatusViewModel TrackStatus` (Checkpoint B) and `TaskSwitcherViewModel TaskSwitcher` (Checkpoint C) fields.
3. **`EventCoordinationContext` record** (`IEventCoordinator.cs:51-80`) — add positional parameters: `TrackStatusViewModel TrackStatusVM` (Checkpoint B), then `TaskSwitcherViewModel TaskSwitcherVM` + `IWindowManager WindowManager` (Checkpoint C). This is the 20-field positional record that bundles the coordinator's dependencies; the construction site at `App.xaml.cs:122` must be updated in lockstep **in each checkpoint** as fields are added. (`IInputSimulator` is already a field there at line 78 — `IWindowManager` is the direct analog.)
4. **`App.ResolveViewModels` / `AppBootstrapper.ResolveViewModels`** — resolve the new VMs in each checkpoint.
5. **`IOverlayManager` / `OverlayManager`** — add `TrackStatus` (Checkpoint B) and `TaskSwitcher` (Checkpoint C) window properties + instantiation in `Initialize`.
6. **`EventCoordinator`** — handle the new messages (`TaskSwitcherToggled`, `TaskSwitcherSelection` in Checkpoint C) and the new settings gate (`TrackStatus.Enabled` in Checkpoint B).

> **Sequencing note:** the `EventCoordinationContext` positional record and its `App.xaml.cs:122` construction site change in **both** Checkpoint B and C. Checkpoint B adds only `TrackStatusVM` (no `IWindowManager` — that type doesn't exist yet). Checkpoint C adds `TaskSwitcherVM` + `IWindowManager`. This is the critical ordering constraint: Checkpoint B must not reference Feature 2 types.

Auto-launch is the exception: it has no overlay and no `EventCoordinationContext` change, just the registrar + `App.OnStartup` reconcile + `settings.General.PropertyChanged`.

## Risks

- **Tobii interop surface (Feature 3, highest uncertainty):** the `Tobii.StreamEngine.Net.dll` wrapper must expose `tobii_eye_position_normalized_subscribe`, `tobii_head_pose_subscribe`, `tobii_user_presence_subscribe` and their structs at compile time. Verify first thing in Checkpoint B. Fallback: P-Invoke the C API directly.
- **Fullscreen overlay vs. underlying gaze pipeline (Feature 2):** the Task Switcher covers the whole screen while the gaze pipeline keeps moving the cursor and detecting fixations behind it. The Keyboard toggle coexists fine (edge-docked, not fullscreen). If the fullscreen cover causes stray fixations or cursor jumps, add a dedicated `InteractionState.TaskSwitcher` that suppresses the pipeline while active. Flagged — follow the Keyboard pattern first, add a state only if needed.
- **Focus stealing (Feature 2):** `SetForegroundWindow` can be refused when the foreground window belongs to another process. Mitigated by `AllowSetForegroundWindow(ASFW_ANY)` called inside `Win32WindowManager.BringToFront` (Platform layer) before the switch — the coordinator calls `_context.WindowManager.BringToFront(hwnd)`, never the raw P/Invoke. Will verify at impl time on a real multi-app scenario.
- **Distance thresholds (Feature 3):** the green/amber/red distance bands are TBD against real PCEye 5 data. PCEye 5 optimal range is ~45–75 cm per Tobii docs; will calibrate against actual `head_pose.position_xyz.z` values at impl time.
- **Single-file publish + registry path (Feature 1):** the registered exe path must be `Environment.ProcessPath` (the **stable** bundle apphost path the user launched), **not `Assembly.Location`** (which points inside the **varying** temp extraction dir and would register the wrong path). `Environment.ProcessPath` does not vary across single-file launches, so reconcile-on-startup is not needed for extraction-path stability — it's kept because the exe path can change between versions/installs (the user moves or reinstalls the app). No installer exists to clean up the entry on uninstall — toggling the setting off is the only removal path (documented in the toggle's UI label).
- **Overlay self-inclusion in Task Switcher (Feature 2):** the window enumerator must filter on `WS_EX_TOOLWINDOW` (unconditional on all EyeCtrl overlays via `OverlayWindow.cs:42`), **not** `WS_EX_TRANSPARENT` (conditional — the Task Switcher itself is non-click-through and would not have it, causing a self-inclusion bug). The Task Switcher window must subclass `OverlayWindow` to inherit `WS_EX_TOOLWINDOW`. Verified: all 19 existing overlay subclasses extend `OverlayWindow`.
- **Elevated/other-user processes in Task Switcher (Feature 2):** `EnumWindows` surfaces windows from all processes, including elevated ones. `IAppIconProvider.GetIcon` reads `process.MainModule.FileName`, which throws `Win32Exception` (Access Denied) when the target is elevated or owned by another user. The impl must wrap this in try/catch and fall back to a default/null icon so one inaccessible window doesn't crash the switcher population.

## Sequencing

1. **Checkpoint A — Feature 1 (auto-launch).** Small, isolated. `IStartupRegistrar` + `WindowsStartupRegistrar` + `App.OnStartup` reconcile. No overlay, no `EventCoordinationContext` change. Build + test green.
2. **Checkpoint B — Feature 3 (track status).** `IGazeProvider` DIMs + `TrackStatusSample` + Tobii streams + `TrackStatusViewModel`/`Window` + `TrackStatusSettings`. **`EventCoordinationContext` gains only `TrackStatusVM`** — no `IWindowManager` (that type is created in C). Register `TrackStatusViewModel` in `AppBootstrapper`. Build + test green. (Tobii-provider changes compile-verified under `TOBII_AVAILABLE`; CI exercises the mouse-provider path.)
3. **Checkpoint C — Feature 2 (task switcher).** Toolbar button + `IWindowManager` + `IAppIconProvider` + fullscreen overlay. `EventCoordinationContext` gains `TaskSwitcherVM` + `IWindowManager`. Register `IWindowManager`, `IAppIconProvider`, `TaskSwitcherViewModel` in `AppBootstrapper`. Build + test green.

> **Critical ordering constraint:** Checkpoint B must not reference Feature 2 types (`IWindowManager`, `Win32WindowManager`, `IAppIconProvider`, `Win32AppIconProvider`, `TaskSwitcherViewModel`). The `EventCoordinationContext` positional record and its `App.xaml.cs:122` construction site change in **both** B and C — B adds `TrackStatusVM`, C adds `TaskSwitcherVM` + `IWindowManager`.

## Test conventions

- **NFluent 3.1.0** for all assertions (e.g. `Check.That(list).HasSize(n)`).
- **No mocking library** — private nested fake classes (e.g. `FakeWindowManager`, `FakeGazeProvider`).
- Test classes named `XxxTests` with underscore-separated PascalCase methods.
- Namespaces mirror source namespace.
