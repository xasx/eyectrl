# Quick Actions Toolbar — Design Spec

**Date:** 2026-07-13
**Status:** Draft
**Type:** Feature work
**Author:** EyeCtrl Team
**Reference:** brainstorming session 2026-07-13

## Problem

EyeCtrl's toolbar has 6 buttons (Pause, SelectionMode, TaskSwitcher, Keyboard, Calibrate, Settings) — all system-level toggles. There is no way for a user to fire a custom action from a dwell surface. Repeated operations (copy/paste, launching an app, inserting a canned phrase) require either the on-screen keyboard (slow for multi-key chords) or the Windows taskbar/desktop (small-target defeats gaze control).

TD Control ships a configurable "Quick Access" panel; OptiKey supports user-extensible XML keyboards. EyeCtrl has no equivalent — every task goes through the full keyboard or OS UI.

## Goals

- A dedicated grid of user-defined action buttons, separate from the main toolbar and the radial action menu.
- Summoned on demand via a hot-edge gaze gesture (a separate screen edge from the main toolbar's docked edge), plus a toolbar-button fallback for mouse mode.
- Six action types: keyboard shortcuts, canned text, EXE app launch, Windows Store app launch, URL launch, media controls.
- All one-shot — dwell fires, grid auto-dismisses.
- Configured via JSON file (`actions.json`) with a full CRUD editor in the Settings window.
- Auto-dismiss after action fires or 5s of gaze away from the grid. Suppressed during active interaction (fixation, action menu, scroll, drag, pause, keyboard open, settings open, calibration, task switcher).

## Non-Goals

- **No repeat-click / continuous mode.** This is a system mode, not an action. It belongs on the main toolbar (alongside Pause and SelectionMode) or as its own feature. The toolbar is exclusively one-shot actions.
- **No action menu integration.** The action menu (radial) handles precise cursor manipulation (click, scroll, drag). Quick actions handle user-defined shortcuts — different surface, different purpose.
- **No multi-monitor, no conditions/scripting, no category tabs in the grid, no drag-to-reorder tiles.**
- **No cloud sync, no import/export beyond the JSON file.**
- **No changes to `InteractionAction` enum or `InteractionManager` state machine.**

## Terminology

- **Hot-edge** — looking past a screen edge with a valid gaze coordinate. Proven pattern from `ToolbarAutoHideController` (PR #61).
- **Summon edge** — the screen edge configured for quick actions summon (default: Left). Must differ from the main toolbar's `DockedEdge` (default: Top per [ToolbarSettings.cs:11](src/EyeCtrl.Core/Settings/ToolbarSettings.cs:11)).
- **AUMID** — AppUserModelID, the string identifier for Windows Store / UWP apps (e.g. `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`).
- **One-shot action** — fires once on dwell, then the grid dismisses. No toggles, no modes.

## Design

### Action Types

| Type | Payload | Execution |
|------|---------|-----------|
| `KeyboardShortcut` | Hotkey chord string e.g. `"Ctrl+C"`, `"Win+D"`, `"Alt+Tab"` | `IInputSimulator.SendModifiedKeyPress` (wired to Platform) |
| `CannedText` | Text string | `IInputSimulator.SendText` |
| `LaunchExe` | Full path to `.exe` or `.lnk` | `Process.Start(new ProcessStartInfo(payload) { UseShellExecute = true })` |
| `LaunchWindowsApp` | AUMID string | `IWindowsAppLauncher.ActivateApplication(aumid)` — COM `IApplicationActivationManager` in Platform |
| `LaunchUrl` | URL string | `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` |
| `MediaControl` | Enum: `PlayPause`, `Stop`, `NextTrack`, `PreviousTrack`, `VolumeUp`, `VolumeDown`, `VolumeMute` | `IInputSimulator.SendKeyPress(vkCode, isExtendedKey: true)` with the matching `VK_MEDIA_*` / `VK_VOLUME_*` virtual key |

`Process.Start` lives in `EyeCtrl.App` (it already references `System.Diagnostics.Process`). `IApplicationActivationManager` COM interop lives in `EyeCtrl.Platform` (where all Win32/COM lives). A new `IWindowsAppLauncher` interface in Core provides the seam.

### Data Model

```csharp
// src/EyeCtrl.Core/Models/QuickAction.cs
namespace EyeCtrl.Core.Models;

public enum QuickActionType
{
    KeyboardShortcut,
    CannedText,
    LaunchExe,
    LaunchWindowsApp,
    LaunchUrl,
    MediaControl
}

public class QuickAction
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public QuickActionType Type { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
```

### Settings

```csharp
// src/EyeCtrl.Core/Settings/QuickActionsSettings.cs
namespace EyeCtrl.Core.Settings;

public sealed partial class QuickActionsSettings : ObservableObject, IValidatable
{
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _summonEdge = "Left";
    [ObservableProperty] private int _summonThresholdMs = 400;
    [ObservableProperty] private int _dismissTimeoutMs = 5000;
    [ObservableProperty] private int _gridColumns = 3;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (SummonThresholdMs < 200 || SummonThresholdMs > 2000)
            errors.Add("SummonThresholdMs must be between 200 and 2000 ms.");
        if (DismissTimeoutMs < 1000 || DismissTimeoutMs > 15000)
            errors.Add("DismissTimeoutMs must be between 1000 and 15000 ms.");
        if (GridColumns < 2 || GridColumns > 5)
            errors.Add("GridColumns must be between 2 and 5.");
        if (!new[] { "Top", "Bottom", "Left", "Right" }.Contains(SummonEdge))
            errors.Add($"SummonEdge '{SummonEdge}' is not a valid edge.");
        // Note: edge conflict with ToolbarSettings.DockedEdge is NOT validated here
        // — it requires cross-settings knowledge resolved at EventCoordinator setup time.
        return errors;
    }
}
```

Per house convention ([ToolbarSettings.cs:20](src/EyeCtrl.Core/Settings/ToolbarSettings.cs:20)), setters should also clamp via `partial void OnSummonThresholdMsChanging(int value)` → `SetProperty(ref _summonThresholdMs, Math.Clamp(value, 200, 2000))`, etc. The exact clamping ranges match the Validate thresholds.

`EyeCtrlSettings` gains `[ObservableProperty] private QuickActionsSettings _quickActions = new();` + inclusion in `EnumerateSubSettings()`. Note: `EyeCtrlSettingsEnumerationTests` enumerates all sub-settings via reflection ([EyeCtrlSettings.cs:52](src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs:52)) — adding a property to `EnumerateSubSettings()` will change the test's expected count; update the assertion.

### Summon Controller

`QuickActionsSummonController` — a state machine on the raw `GazeSampled` stream, mirroring `ToolbarAutoHideController`:

```
States: Idle / Summoned

Idle → Summoned:
  - GazeSampled is VALID
  - Gaze coordinate is past the summon edge
  - Continuous for >= SummonThresholdMs (default 400ms)
  - InteractionManager.CurrentState == FreeLook
  - Quick Actions not suspended (keyboard/settings/calibration/task-switcher closed)

Summoned → Idle:
  - Action fires (dwell target activated on a tile)
  - OR 5s (DismissTimeoutMs) with no gaze on the grid
  - OR close button dwelled
  - OR any suspension condition becomes true
```

The controller:
- Subscribes to `GazeEngine.GazeSampled` (raw, not filtered `CursorMoved` — matching `ToolbarAutoHideController`)
- Uses `volatile` fields for cross-thread state (gaze-thread reads, UI-thread writes)
- Anchors use `long.MinValue` as "unseeded" sentinel (not `0`)
- On Idle→Summoned: **does not** call `Messenger.Send` from `Feed()` (that runs on the gaze/poll thread and would synchronously invoke `QuickActionsGridViewModel.Receive` → WPF mutation off the UI thread → cross-thread exception). Instead, takes a `Action<Action>? marshal` callback (e.g. `Dispatcher.BeginInvoke`) in the constructor and calls `marshal(() => _messenger.Send(new QuickActionsToggledMessage()))`. This mirrors `ToolbarAutoHideController`'s `ToolbarVisibilityAdapter` → `_context.Dispatcher` hop ([EventCoordinator.cs:460](src/EyeCtrl.App/Services/EventCoordinator.cs:460), :918).
- Disabled in mouse mode (`App.ActiveProviderType == Mouse`)
- Added to `EventCoordinator` via a `SetupQuickActionsSummon()` method, following `SetupToolbarAutoHide`'s pattern
- Exposes `EnterSummoned()` / `ResetToIdle()` so `EventCoordinator` can synchronize the controller's internal state when the grid is shown/dismissed via other paths (toolbar button, close button, auto-dismiss). This prevents the controller from trying to summon an already-visible grid (which would toggle it off). Implementation: `Volatile.Write(ref _state, State.Summoned)` / `Volatile.Write(ref _state, State.Idle)` + seed/unseed anchors — lightweight, no message round-trip. **Unlike `ToolbarAutoHideController`** (which only drives visibility from the gaze thread), the summon controller must also be drivable from the UI side, so it exposes these explicit state-restore hooks — a deliberate extension, not a mirror.
- Accepts a `Func<bool>? calibrationActive` gate (same pattern as `ToolbarAutoHideController`'s ctor parameter, [EventCoordinator.cs:458](src/EyeCtrl.App/Services/EventCoordinator.cs:458)) — checked inside `Feed()` before summoning. This avoids adding a `SetSuspended` call path for calibration, which the toolbar controller handles via the gate, not via `SetSuspended`.

`SetSuspended(bool)` is called when keyboard/settings/task-switcher open/close. (Calibration uses the Func gate — see above.) Suspension wiring in `EventCoordinator`:

### Grid Overlay

`QuickActionsGridWindow` extends `OverlayWindow`:
- **Not** click-through (must receive dwell, like Keyboard and Task Switcher)
- `WS_EX_NOACTIVATE` (does not steal focus — launched apps handle their own activation)
- `Topmost`
- Anchored to the summon edge, vertically centered on the work area
- Tile size: 120×120 DIP, 12px gap
- Grid layout: configurable columns (default 3), rows auto-computed
- Each tile: icon (from action type) + label, dwell progress arc
- Close button at top-right corner

**Icons per action type:**
| Type | Default icon |
|------|-------------|
| `KeyboardShortcut` | ⌨ U+2328 |
| `CannedText` | 📝 emoticon → fallback `T` (matching keyboard conventions) |
| `LaunchExe` | ⊞ U+229E |
| `LaunchWindowsApp` | 🪟 U+1FA9F (same non-BMP pattern as TaskSwitcher) |
| `LaunchUrl` | 🌐 emoticon → fallback `@` |
| `MediaControl` | Media-specific icons (▶⏸⏭⏮🔊🔉🔇) based on payload |

### QuickActionsGridViewModel

Extends `DwellOverlayViewModelBase` (same base as `TaskSwitcherViewModel`, `ToolbarViewModel`), implements `IRecipient<QuickActionsToggledMessage>`:

```csharp
public partial class QuickActionsGridViewModel : DwellOverlayViewModelBase,
    IRecipient<QuickActionsToggledMessage>
{
    [ObservableProperty] private bool _isVisible;
    private readonly EyeCtrlSettings _settings;
    private readonly IQuickActionsStore _store;

    public ObservableCollection<QuickActionTileViewModel> Tiles { get; } = new();

    public QuickActionsGridViewModel(IMessenger messenger, EyeCtrlSettings settings,
        IQuickActionsStore store)
        : base(messenger, settings.Dwell.DwellCooldownMs)
    {
        _settings = settings;
        _store = store;
        // base.MessengerViewModelBase sets IsActive=true, which auto-registers
        // every IRecipient<T>. No explicit RegisterAll call — it would
        // double-register and throw in WeakReferenceMessenger.
    }

    public void Receive(QuickActionsToggledMessage message)
    {
        if (IsVisible) { HideOverlay(); return; }
        Reload();
        ShowOverlay();
    }

    /// <summary>
    /// Called by the View once a tile's container is laid out, with the tile's
    /// real screen center. Creates the dwell target here (AddTarget replaces by id)
    /// so the detector only ever hit-tests correctly-positioned targets — mirrors
    /// <c>TaskSwitcherViewModel.UpdateTilePosition</c> and avoids the (0,0) race
    /// (targets are NOT created up front in Reload).
    /// </summary>
    public void UpdateTilePosition(string tileId, PhysicalPoint screenCenter)
    {
        DwellDetector.AddTarget(new DwellTarget(
            tileId, screenCenter,
            DpiHelper.LogicalToPhysicalX(UIConstants.ButtonRadius),
            _settings.Dwell.ToolbarButtonDwellMs));
    }

    public void Reload()
    {
        var actions = _store.LoadAll().Where(a => a.Enabled).ToList();
        Tiles.Clear();
        DwellDetector.ClearTargets();
        foreach (var action in actions)
        {
            // Dwell targets are NOT created here. The View calls UpdateTilePosition
            // after layout with each tile's real screen center, which creates the
            // target at its true position. Creating all targets up front at a shared
            // (0,0) placeholder would let gaze accumulate dwell on the overlapping
            // origin targets during the layout gap (the TaskSwitcher (0,0) race).
            Tiles.Add(new QuickActionTileViewModel(action));
        }
    }

    private void ShowOverlay() { IsVisible = true; }
    private void HideOverlay()
    {
        IsVisible = false;
        DwellDetector.ClearTargets();
    }

    protected override void OnTargetActivated(string targetId)
    {
        var tile = Tiles.First(t => t.Action.Id == targetId);
        Messenger.Send(new QuickActionSelectionMessage(tile.Action));
        HideOverlay(); // auto-dismiss after action fires
    }

    protected override void OnTargetProgress(string targetId, double progress)
    {
        var tile = Tiles.FirstOrDefault(t => t.Action.Id == targetId);
        if (tile != null) tile.DwellProgress = progress;
    }
}
```

This mirrors `TaskSwitcherViewModel` exactly: the VM owns show/hide logic via `IRecipient<QuickActionsToggledMessage>`. `EventCoordinator` only needs to handle `QuickActionSelectionMessage` (execute the action + dismiss), matching how `TaskSwitcherSelectionMessage` is handled.

### Messages

```csharp
// New in InteractionMessages.cs or a dedicated QuickActionsMessages.cs
public sealed class QuickActionsToggledMessage;
// No parameters — mirrors KeyboardToggledMessage / TaskSwitcherToggledMessage.
// Both the toolbar button and the summon controller send this; the coordinator
// handler toggles visibility (visible → hide, hidden → show).
// The summon controller only sends when transitioning Idle → Summoned (i.e.
// when not already visible), so the toggle always resolves to "show."

public sealed class QuickActionSelectionMessage(QuickAction action)
{
    public QuickAction Action { get; } = action;
}
```

### EventCoordinator Wiring

`EventCoordinator` handles:

1. **`QuickActionSelectionMessage`** — fire the action, then hide the grid. (The `QuickActionsToggledMessage` is handled by `QuickActionsGridViewModel` itself via `IRecipient`, matching `TaskSwitcherViewModel`'s pattern — the VM owns its own show/hide.)

```csharp
case var msg when msg is QuickActionSelectionMessage sel:
    ExecuteQuickAction(sel.Action);
    _context.Messenger.Send(new QuickActionsToggledMessage()); // auto-dismiss via VM
    break;
```

2. **`ExecuteQuickAction(QuickAction)`** — the dispatch switch:

| Type | Executes |
|------|----------|
| `KeyboardShortcut` | Parse chord string → `InputSimulator.SendModifiedKeyPress` |
| `CannedText` | `InputSimulator.SendText(action.Payload)` |
| `LaunchExe` | `Process.Start(new ProcessStartInfo(action.Payload) { UseShellExecute = true })` |
| `LaunchWindowsApp` | `_windowsAppLauncher.ActivateApplication(action.Payload)` |
| `LaunchUrl` | `Process.Start(new ProcessStartInfo(action.Payload) { UseShellExecute = true })` |
| `MediaControl` | Map enum → `VK_MEDIA_*` / `VK_VOLUME_*` → `SendKeyPress(vk, isExtendedKey: true)` |

All six cases are wrapped in `try/catch` — a bad path, invalid AUMID, or unreachable URL throws `Win32Exception`, which on a daily-driver is a crash if unhandled. Catch + log via Serilog; no user-visible dialog for v1.

Chord parsing: `KeyboardShortcut` payload is a simple format: `"Modifier+Key"` e.g. `"Ctrl+C"`, `"Ctrl+Shift+Esc"`. Reuse existing name→key mapping from `EventCoordinator.HandleSpecialKey` / `CharToVirtualKey` ([EventCoordinator.cs:1015](src/EyeCtrl.App/Services/EventCoordinator.cs:1015)) and the `ModifierKey` / `ushort virtualKeyCode` split already used by `SendModifiedKeyPress` ([Win32InputSimulator.cs:144](src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:144)) — no new parser needed.

**Win-key caveat:** `Win+D`, `Win+E`, `Win+L` are historically flaky via `SendInput` alone — the OS layer intercepts some Win-key chords before they reach the WM_KEYDOWN queue. The `KEYEVENTF_LWIN` flag exists but is not a guarantee. Test `Win+D`/`Win+E`/`Win+L` end-to-end at Checkpoint C before claiming full support. If they fail, restrict the keyboard shortcut editor's Win-key presets to known-working chords, or flag the action type as "experimental" for Win-modified shortcuts.

3. **Suspension wiring** — `SetSuspended(bool)` is called from `EventCoordinator` at:
   - Settings shown ([839](src/EyeCtrl.App/Services/EventCoordinator.cs:839) suspend, **930** resume) — note 930 is the normal-close resume, not 873 (construction-failure).
   - Keyboard toggled ([1163](src/EyeCtrl.App/Services/EventCoordinator.cs:1163)).
   - **Calibration** is handled via a `Func<bool> calibrationActive` gate passed to the controller's constructor and checked inside `Feed()` — not `SetSuspended`. Same pattern as `ToolbarAutoHideController`'s ctor parameter ([EventCoordinator.cs:458](src/EyeCtrl.App/Services/EventCoordinator.cs:458)).
   - **Task-switcher** has no suspension wiring for the toolbar controller today. Decide whether to add it; if not, document that the quick-actions grid can appear while the task switcher is open.

### Configuration: JSON File

```jsonc
// %APPDATA%/EyeCtrl/actions.json
{
  "actions": [
    {
      "id": "a1b2c3d4",
      "type": "KeyboardShortcut",
      "label": "Copy",
      "payload": "Ctrl+C",
      "enabled": true
    },
    {
      "id": "e5f6g7h8",
      "type": "CannedText",
      "label": "Thanks",
      "payload": "Thanks, best regards.",
      "enabled": true
    },
    {
      "id": "i9j0k1l2",
      "type": "LaunchExe",
      "label": "Terminal",
      "payload": "C:\\Users\\andre\\AppData\\Local\\Microsoft\\WindowsApps\\wt.exe",
      "enabled": true
    },
    {
      "id": "m3n4o5p6",
      "type": "LaunchUrl",
      "label": "Gmail",
      "payload": "https://mail.google.com",
      "enabled": true
    },
    {
      "id": "q7r8s9t0",
      "type": "MediaControl",
      "label": "Pause",
      "payload": "PlayPause",
      "enabled": true
    }
  ]
}
```

### IQuickActionsStore

```csharp
// src/EyeCtrl.Core/Interfaces/IQuickActionsStore.cs
namespace EyeCtrl.Core.Interfaces;

public interface IQuickActionsStore
{
    IReadOnlyList<QuickAction> LoadAll();
    void Save(IReadOnlyList<QuickAction> actions);
    QuickAction? FindById(string id);
}
```

Implementation: `QuickActionsStore` in `EyeCtrl.App/Services/` — reads/writes `actions.json` via `System.Text.Json`. Path resolved via `Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "/EyeCtrl/actions.json"`.

### Settings Tab Editor

The Settings window gains a 14th tab: **"Quick Actions"** (symbol: ⚡ U+26A1).

The tab does NOT use the standard `SettingItemViewModel` list (which supports Slider/Combo/Toggle/ColorPicker only). Instead, it renders a custom list of action cards, each with:
- Label + type badge
- Toggle (enabled/disabled)
- Edit button → opens inline editor
- Delete button

**"Add Action" button** → prompts for action type (6-option dropdown using the existing Combo setting pattern) → opens the type-specific editor.

**Per-type editors:**

| Type | Editor fields |
|------|--------------|
| `KeyboardShortcut` | Label text field. Dropdown of common presets (Copy, Paste, Cut, Undo, Redo, Select All, Find, Save, New, Print, Alt+Tab, Win+D, Win+E, Win+L, etc.) OR custom text field for manual chord |
| `CannedText` | Label text field, `TextBox` for content |
| `LaunchExe` | Label text field, text field for path (no file picker — type or paste) |
| `LaunchWindowsApp` | Label text field. Text field for AUMID. Optional "Browse" that enumerates installed packages via `Windows.Management.Deployment.PackageManager.FindPackagesForUser`, then for each package calls `package.GetAppListEntries()` → `AppListEntry.AppUserModelId` to get the canonical AUMID — no hand-construction needed. User picks an app from the list, AUMID auto-fills. |
| `LaunchUrl` | Label text field, URL text field |
| `MediaControl` | Label text field, dropdown of media commands |

All editor fields use gaze-dwellable controls (Buttons, ComboBoxes, TextBoxes) via the existing `SettingsViewModel` / `SettingsWindowViewModel` dwell infrastructure. **There is no existing precedent for a non-`SettingItemViewModel` tab** — the existing tabs all bind to `List<SettingItemViewModel>` with Slider/Combo/Toggle/ColorPicker types. The Quick Actions tab is the first custom-card-layout tab and represents genuinely new UI (cards, per-type editors, confirm dialogs, AUMID browser). Checkpoint D should treat this as a first-class UI effort, not a "reuse existing pattern."

Delete prompts for confirmation ("Delete [label]?" with Yes/No dwell buttons).

### Edge Conflict Validation

`QuickActionsSettings.SummonEdge` must not equal the main toolbar's `DockedEdge`. Resolved entirely at `EventCoordinator` startup — no cross-settings validation in settings objects themselves (they have no reference to each other):

1. At `SetupQuickActionsSummon()` time: compute `actualSummonEdge` — if `quickActions.SummonEdge == toolbar.DockedEdge`, use the opposite edge (Right→Left, Left→Right, Top→Bottom, Bottom→Top) as a silent fallback. The configured value stays in settings unchanged (it may become valid if the user moves the main toolbar later).
2. Subscribe to `settings.Toolbar.PropertyChanged` for `DockedEdge`: if the user moves the main toolbar to the quick-actions edge, `QuickActionsSummonController.UpdateEdge()` re-evaluates and picks the opposite edge using the same fallback logic.

### Mouse-Mode Fallback

- In mouse mode (`App.ActiveProviderType == Mouse`), `QuickActionsSummonController` is disabled (never subscribes to `GazeSampled`).
- A 7th toolbar button ("QuickActions", symbol: ⚡ U+26A1) always summons the grid regardless of mode.
- `ToolbarViewModel` gains a 7th entry in the button array; `OnTargetActivated` dispatches `QuickActionsToggledMessage` (parameterless toggle, matching Keyboard/TaskSwitcher).

## Architecture Diagram

```
                    ┌─────────────────────────────────┐
                    │        EventCoordinator          │
                    │                                  │
                    │  SetupQuickActionsSummon()       │
                    │  Handle QuickActionsToggled      │
                    │  Handle QuickActionSelection     │
                    │  ExecuteQuickAction(qa)          │
                    │  Suspend/resume summon           │
                    └───┬──────────┬──────────────────┘
                        │          │
            ┌───────────┘          └──────────────┐
            ▼                                     ▼
┌───────────────────────┐         ┌──────────────────────────┐
│QuickActionsSummonCtrl │         │   QuickActionsGridWindow │
│ (hot-edge, gaze-thr)  │         │   (overlay, UI thread)   │
│                       │         │                          │
│ Feed(GazeSampled)     │         │   Grid of tiles           │
│ → QuickActionsToggled │         │   Dwell progress arcs     │
└───────────────────────┘         └──────┬───────────────────┘
                                         │
                               ┌─────────┘
                               ▼
                 ┌──────────────────────────┐
                 │ QuickActionsGridViewModel │
                 │ (DwellOverlayViewModelBase)│
                 │                          │
                 │ OnTargetActivated →       │
                 │   QuickActionSelectionMsg │
                 └──────┬───────────────────┘
                        │
               ┌────────┘
               ▼
    ┌─────────────────────┐
    │  IQuickActionsStore  │
    │  (JSON file on disk) │
    └─────────────────────┘
```

## File Layout

### New Files

| File | Layer | Purpose |
|------|-------|---------|
| `src/EyeCtrl.Core/Models/QuickAction.cs` | Core | `QuickAction` class + `QuickActionType` enum |
| `src/EyeCtrl.Core/Settings/QuickActionsSettings.cs` | Core | Settings sub-object |
| `src/EyeCtrl.Core/Interfaces/IQuickActionsStore.cs` | Core | Store seam |
| `src/EyeCtrl.Core/Interfaces/IWindowsAppLauncher.cs` | Core | `ActivateApplication(string aumid)` seam |
| `src/EyeCtrl.Platform/Services/WindowsAppLauncher.cs` | Platform | COM `IApplicationActivationManager` impl |
| `src/EyeCtrl.App/Services/QuickActionsStore.cs` | App | JSON file I/O |
| `src/EyeCtrl.App/Services/QuickActionsSummonController.cs` | App | Hot-edge state machine |
| `src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml` + `.xaml.cs` | App | Grid overlay window |
| `src/EyeCtrl.ViewModels/QuickActionsGridViewModel.cs` | ViewModels | Grid VM (extends `DwellOverlayViewModelBase`) |
| `src/EyeCtrl.ViewModels/QuickActionTileViewModel.cs` | ViewModels | Per-tile VM |

### Modified Files

| File | Change |
|------|--------|
| `src/EyeCtrl.Core/Settings/EyeCtrlSettings.cs` | Add `QuickActionsSettings` property + `EnumerateSubSettings()` entry |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` | Add `QuickActionsGridWindow QuickActionsGrid` property |
| `src/EyeCtrl.App/Services/IOverlayManager.cs` (`OverlayViewModels`) | Add `QuickActionsGridViewModel QuickActionsGrid` field |
| `src/EyeCtrl.App/Services/OverlayManager.cs` | Instantiate `QuickActionsGrid` in `Initialize` |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register DI: `QuickActionsStore`, `QuickActionsGridViewModel`, `IWindowsAppLauncher` |
| `src/EyeCtrl.App/App.xaml.cs` | Resolve VM + add to `OverlayViewModels` + `EventCoordinationContext` |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` (`EventCoordinationContext`) | Add `QuickActionsGridViewModel QuickActionsVM` + `IQuickActionsStore QuickActionsStore` + `IWindowsAppLauncher WindowsAppLauncher` |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | `SetupQuickActionsSummon()`, message handlers, suspension wiring, `ExecuteQuickAction()` |
| `src/EyeCtrl.ViewModels/ToolbarViewModel.cs` | 7th button entry + `case "QuickActions"` in `OnTargetActivated` |
| `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | Add `QuickActionsToggledMessage` + `QuickActionSelectionMessage` |
| `src/EyeCtrl.ViewModels/SettingsViewModel.cs` | Add "Quick Actions" tab + editor build logic |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml` + `.xaml.cs` | Render quick actions tab content |

## Sequencing

1. **Checkpoint A — Data layer.** `QuickAction` model, `QuickActionType` enum, `QuickActionsSettings`, `IQuickActionsStore` + `QuickActionsStore`. Build + test green.
2. **Checkpoint B — Summon controller + grid.** `QuickActionsSummonController`, `QuickActionsGridViewModel`, `QuickActionsGridWindow`. Wire into `EventCoordinator`. Summon/dismiss works. Build + test green.
3. **Checkpoint C — Execution.** `IWindowsAppLauncher` + `WindowsAppLauncher`. `ExecuteQuickAction()` switch in `EventCoordinator`. All 6 action types fire correctly. Build + test green.
4. **Checkpoint D — Settings editor + toolbar button.** Settings tab, toolbar button, JSON CRUD. Build + test green.

Checkpoint B depends on A (needs `QuickActionsSettings` for configuration). Checkpoint C depends on B (needs the grid to trigger actions). Checkpoint D depends on C (edits what C executes).

## Tests

| File | Purpose |
|------|---------|
| `tests/EyeCtrl.Core.Tests/Models/QuickActionTests.cs` | Construction, defaults, `Enabled` property |
| `tests/EyeCtrl.Core.Tests/Settings/QuickActionsSettingsDefaultsTests.cs` | Default values, validation edge cases |
| `tests/EyeCtrl.App.Tests/Services/QuickActionsSummonControllerTests.cs` | State machine: summon on valid past-edge, blink rejection, timeout dismiss, suspension, large-epoch regression — mirrors `ToolbarAutoHideControllerTests` structure. Uses `FakeGazeProvider` / synthetic stream. |
| `tests/EyeCtrl.App.Tests/Services/QuickActionsStoreTests.cs` | Round-trip load/save, empty file, missing file (returns empty), corrupted JSON (graceful fallback) |
| `tests/EyeCtrl.ViewModels.Tests/QuickActionsGridViewModelTests.cs` | Tile building from store, activation fires correct message, cancel/close target works |
| `tests/EyeCtrl.ViewModels.Tests/ToolbarViewModelQuickActionsTests.cs` | 7th button present, activation sends `QuickActionsToggledMessage` |
| `tests/EyeCtrl.Platform.Tests/Services/WindowsAppLauncherTests.cs` | Smoke-test `ActivateApplication` with a known Store app (e.g., Calculator); verify no crash on invalid AUMID |

No new test framework or pattern — all tests use NFluent + xUnit, no mocking library (private nested fakes for `FakeQuickActionsStore`, `FakeWindowsAppLauncher`).

## Risks

- **`IApplicationActivationManager` COM surface.** The `shobjidl_core.h` `IApplicationActivationManager` interface is well-documented and stable. Risk: low. Fallback: none needed — it's a standard COM interface. The interop uses `[ComImport]` + `[Guid]` attributes in Platform.
- **Chord parser edge cases.** Chord parsing splits the payload on `+` into `ModifierKey` values plus a terminal key, reusing the existing name→key mapping in `EventCoordinator.HandleSpecialKey` / `CharToVirtualKey` and the `ModifierKey` / `ushort virtualKeyCode` split already consumed by `SendModifiedKeyPress` (see [EventCoordinator Wiring](#eventcoordinator-wiring)) — no new parser. Modifier order (Ctrl+Shift vs Shift+Ctrl) doesn't matter to Win32. Keys like `+` itself or `-` are unlikely user inputs for a shortcut — if needed, a quoting convention (`"Ctrl+'+'"`) can be added later. Risk: low.
- **Settings tab complexity.** The 14th settings tab is the first tab that does NOT bind to a `List<SettingItemViewModel>` — every existing tab uses the Slider/Combo/Toggle/ColorPicker item types (see [Settings Tab Editor](#settings-tab-editor)). There is no precedent to reuse, so `SettingsWindow.xaml.cs` needs a new "Quick Actions" case in its tab-content switch plus genuinely new UI (action cards, per-type editors, confirm dialogs, the AUMID package browser). `SettingsViewModel.ShowTab()` and the dwell infrastructure carry over for the editor buttons, but the card layout itself is new. Risk: medium-high — treat this as a first-class UI effort at Checkpoint D. Mitigation: keep the editor simple; no drag-reorder, no inline validation beyond required fields.
- **Edge conflict with main toolbar.** If `ToolbarSettings.DockedEdge` changes mid-session (unlikely but possible), `QuickActionsSummonController.UpdateEdge()` must re-evaluate the conflict. Handled by the `actualSummonEdge` computation at setup + reacting to `ToolbarSettings.PropertyChanged`.
- **Overlay self-inclusion in Task Switcher.** The `QuickActionsGridWindow` extends `OverlayWindow`, which sets `WS_EX_TOOLWINDOW` at `OverlayWindow.cs:42`. The Task Switcher's `Win32WindowManager` filters on that flag — so the Quick Actions grid window is correctly excluded from the window list. Verified identical to the Task Switcher's own exclusion guarantee.

## Impact Summary (estimated)

Primary symbols introduced or touched — risk is LOW for new types, MEDIUM for `EventCoordinator`/`ToolbarViewModel` modifications:

| Symbol | Risk | Reason |
|--------|------|--------|
| `QuickAction`, `QuickActionType`, `QuickActionsSettings` | LOW | New leaf types, no dependents |
| `QuickActionsSummonController` | LOW | New, mirrors proven `ToolbarAutoHideController` |
| `QuickActionsGridWindow` / `QuickActionsGridViewModel` | LOW | New, follows `TaskSwitcherWindow` pattern |
| `EventCoordinator` (3 additions) | MEDIUM | Same file touched in every feature PR; additions are additive (new handler cases), not modifications of existing behavior |
| `ToolbarViewModel` (7th button) | LOW | Extends existing array + switch; 1 test assert change (6→7), test method rename (`InitializesWith6Buttons` → `InitializesWith7Buttons`) |
| `IOverlayManager` / `OverlayViewModels` | LOW | Pattern repeated for every overlay; positional record append |
| `EventCoordinationContext` | LOW | Same positional record pattern as all prior features |
| `SettingsViewModel` | MEDIUM | 14th tab + custom layout; existing tabs untouched |

No changes to `InteractionManager`, `InteractionAction` enum, `GazeEngine`, or `DwellDetector`. One drift-guard test is touched: `EyeCtrlSettingsEnumerationTests` asserts the sub-settings count, which increments by one when `QuickActions` is added to `EnumerateSubSettings()` (see [Settings](#settings)).
