# Quick Actions Settings Editor — Design Spec

**Date:** 2026-07-14
**Status:** Draft
**Type:** Feature work (follow-up to the Quick Actions toolbar)
**Author:** EyeCtrl Team
**Reference:** brainstorming session 2026-07-14; builds on `2026-07-13-quick-actions-toolbar-design.md`

## Problem

The Quick Actions toolbar (PR #63) ships without an authoring UI. The Settings → **Quick Actions** tab exposes only the five configuration controls (enable, summon edge, thresholds, grid columns). To create, remove, or disable an action the user must hand-edit `%APPDATA%/EyeCtrl/actions.json`. For an eye-tracking daily-driver that is not a usable workflow.

The core constraint: **the settings window has no gaze text-entry control.** The `DwellControl` set is Button / Toggle / Slider / ComboBox (a radio-style button group, not a popup) / ColorPicker / Label. The on-screen keyboard only sends keystrokes to the OS-focused window; it cannot capture text into a settings field. So free-text payloads (arbitrary canned text, URLs, file paths, custom chords) cannot be authored by gaze without a new keyboard-capture subsystem, which is out of scope here.

## Goals

- Manage every existing action by gaze: **enable/disable** and **delete** (with confirmation), regardless of type.
- **Create** the gaze-authorable action types without any typing:
  - `KeyboardShortcut` — from a fixed preset list.
  - `MediaControl` — from the seven transport commands.
  - `LaunchWindowsApp` — by picking an installed app (AUMID auto-filled).
- Every pick surface is a **paginated grid of large dwell buttons** — no dropdowns, no popups, no targeting inside a list control.
- Persist through the existing `IQuickActionsStore` (`actions.json`); the grid picks up changes on its next summon (it reloads on show).

## Non-Goals

- **No free-text authoring** — `CannedText`, `LaunchUrl`, and custom `LaunchExe` paths / custom chords are not creatable in the gaze UI. Existing entries of these types (authored in JSON) still appear in the list with enable/disable/delete. (Deferred: on-screen-keyboard text capture.)
- **No rename** (labels are auto-derived from the preset / app; renaming needs text entry).
- **No reorder** (matches the toolbar spec's non-goal).
- **No separate "edit"** — changing a preset action = delete + re-add.
- **No non-packaged desktop-app picker** — the app picker lists packaged apps only (see Risks). Desktop `.exe`/`.lnk` launches stay JSON-authored.
- **No app icons in the picker** for v1 (text labels only; `IAppIconProvider` resolves by process id, not AUMID — icon extraction is a later enhancement).
- **No new settings, messages, or changes to the runtime grid / summon controller.**

## Design

### Editor as an in-tab view state machine

`SettingsWindow` already rebuilds a tab's content on demand (`RebuildContent()`), and each `Build*Pages()` returns `List<List<Action>>` that the existing `pag_prev` / `pag_next` controls page through. The editor is driven by a private view-state field on `SettingsWindow`:

```
enum QaView { List, PickType, PickShortcut, PickMedia, PickApp }
private QaView _qaView = QaView.List;
```

`BuildQuickActionsPages()` switches on `_qaView` and delegates to a per-view builder. A dwell that changes the view sets `_qaView` and calls `RebuildContent()`. **On tab switch, `_qaView` resets to `List`** so the user never returns to a stale picker (reset in the existing tab-selection path).

| View | Content (all big `DwellButton`s / dwell controls) |
|------|---------------------------------------------------|
| `List` | The 5 config controls (unchanged), then one **row per action** — `DwellLabel` (type icon + label) · `DwellToggle` (enabled) · `DwellButton` "✕" (delete) — then a **"＋ Add action"** button. |
| `PickType` | Three buttons: **Keyboard Shortcut**, **Media Control**, **Launch App**; plus **Back**. |
| `PickShortcut` | One button per keyboard preset, auto-paginated; plus **Back**. |
| `PickMedia` | Seven buttons (one per transport command); plus **Back**. |
| `PickApp` | One button per installed app, paginated across as many pages as needed; plus **Back**. |

Dwelling a preset / media / app button creates the action, saves, sets `_qaView = List`, and rebuilds. Dwelling a type / Back button only swaps the view. Delete dwells a `DwellButton` that calls the existing `ShowModal(message, onYes)` (Yes/No dwell buttons) before removing.

Long lists (shortcuts ≈ 2 pages, apps N pages) reuse the existing pagination — the editor never renders a scroll region or a popup.

### Presets (Core)

A static `QuickActionPresets` holds the fixed tables (data only, no behavior):

**Keyboard shortcuts** — `(Label, Chord)`; every chord must parse via the same path `EventCoordinator.ExecuteKeyboardShortcut` uses (`KeyNameMapper` + `Win32InputSimulator.CharToVirtualKey` + `ModifierKey` split):

| Label | Chord | | Label | Chord |
|-------|-------|-|-------|-------|
| Copy | `Ctrl+C` | | Print | `Ctrl+P` |
| Paste | `Ctrl+V` | | Switch App | `Alt+Tab` |
| Cut | `Ctrl+X` | | Show Desktop | `Win+D` |
| Undo | `Ctrl+Z` | | Lock | `Win+L` |
| Redo | `Ctrl+Y` | | File Explorer | `Win+E` |
| Select All | `Ctrl+A` | | Save | `Ctrl+S` |
| Find | `Ctrl+F` | | New | `Ctrl+N` |

**Media controls** — `(Label, Command)` where `Command` is the `MediaControl` payload string: Play/Pause=`PlayPause`, Stop=`Stop`, Next=`NextTrack`, Previous=`PreviousTrack`, Volume Up=`VolumeUp`, Volume Down=`VolumeDown`, Mute=`VolumeMute`.

`Win+D` / `Win+E` / `Win+L` carry the known `SendInput` flakiness caveat from the toolbar spec — worth an end-to-end check but not gated on it.

### Installed-app enumeration (Core seam + Platform impl)

```csharp
// Core
public readonly record struct InstalledApp(string DisplayName, string Aumid);

public interface IInstalledAppProvider
{
    IReadOnlyList<InstalledApp> GetInstalledApps();
}
```

`Win32InstalledAppProvider` (Platform) enumerates the Shell **`AppsFolder`** (known folder `{4234d49b-0245-4df3-b780-3893943456e1}`) via Shell32 COM: bind the folder to an `IEnumShellItems`, and for each item read the display name (`SIGDN_NORMALDISPLAY`) and the AUMID (`PKEY_AppUserModel_ID` via `IShellItem2.GetString`). Results are **filtered to packaged AUMIDs** (those containing `!`, the `PackageFamilyName!AppId` form) so the existing `IApplicationActivationManager.ActivateApplication` launch path is reliable, then sorted by display name.

This is chosen over WinRT `PackageManager.FindPackagesForUser` because the Platform project targets plain `net10.0-windows` (no Windows SDK platform version), so WinRT projections are unavailable without a TFM change; the Shell COM route works on the current TFM and matches the existing Win32/COM style (`Win32WindowManager`).

`IInstalledAppProvider` is registered in `AppBootstrapper` and threaded to the editor VM through `EventCoordinationContext` (the editor VM is manually constructed in `EventCoordinator.ShowSettingsWindow`).

### Editor ViewModel (extend existing)

`SettingsQuickActionsEditorViewModel` (already present, currently unused) gains a constructor parameter `IInstalledAppProvider` and factory methods; its existing `Load` / `Save` / `ToggleEnabled` / `DeleteAction` are reused:

```csharp
public void AddKeyboardShortcut(string label, string chord); // Type=KeyboardShortcut, Payload=chord
public void AddMediaControl(string label, string command);   // Type=MediaControl,   Payload=command
public void AddApp(InstalledApp app);                        // Type=LaunchWindowsApp, Payload=Aumid, Label=DisplayName
public IReadOnlyList<InstalledApp> InstalledApps { get; }     // cached from the provider (loaded once)
```

Each `Add*` appends a `QuickAction` and calls `Save()`. `QuickAction` itself is unchanged (`Label` / `Type` / `Payload` / `Enabled` suffice).

### Data flow

```
Settings tab (SettingsWindow, _qaView) ──dwell──▶ SettingsQuickActionsEditorViewModel
        │  (RebuildContent per view)                     │  Add*/Toggle/Delete
        │                                                ▼
        │                                        IQuickActionsStore (actions.json)
        └── PickApp view ◀── InstalledApps ◀── IInstalledAppProvider (Win32, AppsFolder)

(next summon) QuickActionsGridViewModel.Reload() ── reads actions.json ──▶ tiles
```

The editor and the runtime grid are decoupled through the JSON store; no direct coupling, no live message.

## File Layout

### New files
| File | Layer | Purpose |
|------|-------|---------|
| `src/EyeCtrl.Core/Models/InstalledApp.cs` | Core | `InstalledApp` record struct |
| `src/EyeCtrl.Core/Interfaces/IInstalledAppProvider.cs` | Core | App-enumeration seam |
| `src/EyeCtrl.Core/QuickActions/QuickActionPresets.cs` | Core | Keyboard + media preset tables |
| `src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs` | Platform | Shell `AppsFolder` COM enumeration |
| `tests/EyeCtrl.Core.Tests/QuickActions/QuickActionPresetsTests.cs` | Test | Every preset chord maps to a non-zero VK |
| `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs` | Test | Add/toggle/delete behavior with fakes |
| `tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs` | Test | Smoke: returns packaged apps |

### Modified files
| File | Change |
|------|--------|
| `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs` | Add `IInstalledAppProvider` ctor param + `AddKeyboardShortcut`/`AddMediaControl`/`AddApp`/`InstalledApps` |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `IInstalledAppProvider → Win32InstalledAppProvider` |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Append `IInstalledAppProvider InstalledAppProvider` to `EventCoordinationContext` |
| `src/EyeCtrl.App/App.xaml.cs` | Pass the provider into the context (append at end) |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | Pass `_context.InstalledAppProvider` into the `new SettingsQuickActionsEditorViewModel(...)` in `ShowSettingsWindow` |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `_qaView` state + rewrite `BuildQuickActionsPages` into per-view builders; delete-confirm via `ShowModal`; reset `_qaView` on tab switch |
| `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` | Fake `IInstalledAppProvider` for the editor VM ctor |

## Testing

- **`SettingsQuickActionsEditorViewModelTests`** (fake store + fake app provider): `AddKeyboardShortcut` → one `KeyboardShortcut` action with the chord + label; `AddMediaControl` → `MediaControl` with the command; `AddApp` → `LaunchWindowsApp` with the AUMID and app name; `ToggleEnabled`/`DeleteAction` mutate and persist; `Load` reflects the store (including disabled actions).
- **`QuickActionPresetsTests`**: for every keyboard preset, the terminal key resolves to a non-zero VK via `KeyNameMapper.TryMap` or `Win32InputSimulator.CharToVirtualKey` — guarding against a preset that silently can't fire.
- **`Win32InstalledAppProviderTests`**: smoke — `GetInstalledApps()` returns a non-empty list on a normal Windows box and every entry has a non-empty `DisplayName` and an AUMID containing `!`. Marked so it degrades gracefully in headless CI if the shell namespace is unavailable.
- No new test framework or pattern — xUnit + NFluent + private nested fakes, matching the toolbar work.

## Risks

- **Shell `AppsFolder` COM interop** — moderate boilerplate (`IShellItem`/`IShellItem2`/`IEnumShellItems`/`PROPERTYKEY`), but a well-documented, stable surface; matches existing Platform COM. Risk: medium. Mitigation: filter to packaged AUMIDs for reliable activation; wrap enumeration in try/catch returning an empty list.
- **`SettingsWindow` view state** — adds statefulness to a tab. Mitigation: single `_qaView` field reset on tab switch; each view is a pure builder of dwell controls, so the drift-guard is just "reset on leave."
- **Win-key preset chords** (`Win+D/E/L`) — historically flaky via `SendInput`; inherited caveat, verified at test time on hardware.
- **App-list size** — many packaged apps → many pages. Acceptable (pagination exists); sorted by name to keep paging predictable.

## Impact Summary (estimated)

| Symbol | Risk | Reason |
|--------|------|--------|
| `InstalledApp`, `IInstalledAppProvider`, `QuickActionPresets` | LOW | New leaf types, no dependents |
| `Win32InstalledAppProvider` | MEDIUM | New COM interop; isolated behind the seam |
| `SettingsQuickActionsEditorViewModel` | LOW | Extends an existing, currently-unused VM |
| `SettingsWindow.BuildQuickActionsPages` | MEDIUM | Rewritten into a view-state machine; other tabs untouched |
| `EventCoordinationContext` / `App.xaml.cs` / `AppBootstrapper` | LOW | Same positional-append + DI-register pattern as the toolbar feature |

No changes to `QuickAction`, `QuickActionsSettings`, `QuickActionsGridViewModel`, `QuickActionsSummonController`, the messages, or the runtime grid/summon behavior.
