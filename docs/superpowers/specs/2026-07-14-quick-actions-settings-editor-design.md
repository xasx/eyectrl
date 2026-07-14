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

`BuildQuickActionsPages()` switches on `_qaView` and delegates to a per-view builder. A dwell that changes the view sets `_qaView`, resets `CurrentPage = 0`, then calls `RebuildContent()` **directly** — it does not touch `CurrentTabName`, so drill navigation (List → PickType → PickShortcut) is preserved. Resetting `CurrentPage` on every view transition makes each view open at its **first** page, so a picker never inherits a stale page index from the previous view's pagination (e.g. drilling from action page 3 into PickApp would otherwise render a clamped/empty first frame). This reset lives in the transition handler next to the `_qaView` assignment — **never** inside `BuildQuickActionsPages` (that would fight the `pag_prev`/`pag_next` controls).

**The reset to `List` (also `CurrentPage = 0`) on tab change lives in `OnSettingsViewModelPropertyChanged`'s `case nameof(SettingsViewModel.CurrentTabName)` branch, before `RebuildContent()` — NOT at the top of `BuildQuickActionsPages`** (that would defeat drill navigation, snapping every rebuild back to List). That branch fires whenever the active tab changes, including re-entering Quick Actions, so the user never returns to a stale picker.

| View | Content (all big `DwellButton`s / dwell controls) |
|------|---------------------------------------------------|
| `List` | **Page 1 (pinned):** the 5 config controls (unchanged). **Pages 2+:** one **row per action** — `DwellLabel` (type icon + label) · `DwellToggle` (enabled) · `DwellButton` "✕" (delete) — auto-paginated, with the **"＋ Add action"** button after the last row. Pinning config to its own page keeps the controls from scattering across the action pages. Each row's toggle/delete dwell-ids are suffixed with the action's `Id` (a GUID): that both guarantees per-page dwell-id uniqueness and carries the key into `ToggleEnabled(id)` / `DeleteAction(id)`. |
| `PickType` | Three buttons: **Keyboard Shortcut**, **Media Control**, **Launch App**; plus **Back**. |
| `PickShortcut` | One button per keyboard preset, auto-paginated; plus **Back**. |
| `PickMedia` | Seven buttons (one per transport command); plus **Back**. |
| `PickApp` | One button per installed app, paginated across as many pages as needed; plus **Back**. |

Dwelling a preset / media / app button creates the action, saves, sets `_qaView = List` (with `CurrentPage = 0`), and rebuilds. Dwelling a type / Back button only swaps the view. Delete dwells a `DwellButton` that calls the existing `ShowModal(message, onYes)` (Yes/No dwell buttons) before removing. `ShowModal` is **already** used for per-row destructive confirms elsewhere (e.g. the Models tab's per-model *Delete* / *Download* rows in `BuildModelsPages`), so its modal dwell isolation (`DwellCategory.Modal` targets active while the page's `Control` targets are suppressed) is already proven for this kind of multi-row use — the editor just reuses it.

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

**Caching / threading.** `Win32InstalledAppProvider` is a process **singleton that caches the enumeration** via a thread-safe `Lazy<IReadOnlyList<InstalledApp>>`. This matters because `EventCoordinator.ShowSettingsWindow` reconstructs the editor VM on every open — caching on the *VM* would re-walk `AppsFolder` each open; caching in the singleton makes it once-per-process. Two requirements:

- **Apartment (STA).** Shell `AppsFolder` COM (`IShellItem`/`IEnumShellItems`) must run in a single-threaded apartment. Thread-pool threads (`Task.Run`) are MTA, so the walk runs on a **dedicated STA worker thread** created inside the provider (`ApartmentState.STA`, run the walk, join, cache). `GetInstalledApps()` is therefore safe to call from any apartment (the STA UI thread or a background/MTA prewarm) — it only triggers/returns the cached lazy value.
- **Failure isolation.** The `Lazy` value factory wraps the walk in try/catch and **returns an empty list on any failure** (headless/minimal shell, COM error). It must not let the exception escape — a default `Lazy` caches and re-throws on every subsequent access, which would break PickApp for the whole process.

To avoid a first-drill hitch, the provider is **pre-warmed** by triggering `GetInstalledApps()` fire-and-forget after the DI container is built (spinning up the STA worker early); the thread-safe lazy means a concurrent first real call just blocks on the same enumeration.

`IInstalledAppProvider` is registered in `AppBootstrapper` (singleton) and threaded to the editor VM through `EventCoordinationContext` (the editor VM is manually constructed in `EventCoordinator.ShowSettingsWindow`).

### Editor ViewModel (extend existing)

`SettingsQuickActionsEditorViewModel` (already present, and currently unused — no caller resolves it, and `ShowSettingsWindow` `new`s a fresh instance) gains a constructor parameter `IInstalledAppProvider`. Its existing `Load` / `Save` / `ToggleEnabled(string id)` / `DeleteAction(string id)` are reused. Its existing **`AddAction(QuickActionType)` and `UpdateAction(...)` are removed**: `AddAction` only creates a blank, empty-payload action (useless as a preset) and `UpdateAction` is an edit path v1 doesn't offer — both are dead (no callers) and cut against the "no separate edit / no dead code" stance. They are replaced by three typed factories:

```csharp
public void AddKeyboardShortcut(string label, string chord); // Type=KeyboardShortcut, Payload=chord
public void AddMediaControl(string label, string command);   // Type=MediaControl,     Payload=command
public void AddApp(InstalledApp app);                        // Type=LaunchWindowsApp, Payload=Aumid, Label=DisplayName
public IReadOnlyList<InstalledApp> InstalledApps => _provider.GetInstalledApps(); // provider-cached
```

Each `Add*` constructs a `QuickAction` (its `Id` auto-generates), appends it to `Actions`, and calls `Save()` — so subsequent List rebuilds read the up-to-date in-memory `Actions` without re-reading disk.

**`Load()` timing.** `EventCoordinator.ShowSettingsWindow` calls `editor.Load()` exactly once, immediately after constructing the editor VM (which happens on every settings-open). That populates `Actions` before the List view first renders — **without it the List shows zero rows.** No per-view reload is needed: `Add*` / `ToggleEnabled` / `DeleteAction` mutate `Actions` in place and persist, and the running grid re-reads the store on its next summon.

**On `QuickAction.Id`:** the model carries a 5th member `Id` (`Guid` string) that `ToggleEnabled` / `DeleteAction` / `IQuickActionsStore.FindById` key on and that the runtime tiles already use as their dwell-target id. The `Add*` factories don't set it (auto-generated); the List view's per-row toggle/delete buttons carry it (see the `List` view note). "`QuickAction` is unchanged" is **relative to this branch** — the model was introduced by the toolbar feature this branch builds on; it does not exist on `main`.

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
| `src/EyeCtrl.Core/Models/QuickActionPresets.cs` | Core | Keyboard + media preset tables (co-located with `QuickAction` in `Models/`) |
| `src/EyeCtrl.Platform/Services/Win32InstalledAppProvider.cs` | Platform | Shell `AppsFolder` COM enumeration |
| `tests/EyeCtrl.Core.Tests/Models/QuickActionPresetsTests.cs` | Test | Every preset chord maps to a non-zero VK |
| `tests/EyeCtrl.ViewModels.Tests/SettingsQuickActionsEditorViewModelTests.cs` | Test | Add/toggle/delete behavior with fakes |
| `tests/EyeCtrl.Platform.Tests/Services/Win32InstalledAppProviderTests.cs` | Test | Smoke: returns packaged apps |

### Modified files
| File | Change |
|------|--------|
| `src/EyeCtrl.ViewModels/SettingsQuickActionsEditorViewModel.cs` | Add `IInstalledAppProvider` ctor param + `AddKeyboardShortcut`/`AddMediaControl`/`AddApp`/`InstalledApps` |
| `src/EyeCtrl.App/Services/AppBootstrapper.cs` | Register `IInstalledAppProvider → Win32InstalledAppProvider` (singleton) + fire-and-forget background pre-warm; **remove the orphaned `AddSingleton<SettingsQuickActionsEditorViewModel>()`** (dead — `ShowSettingsWindow` `new`s its own instance) |
| `src/EyeCtrl.App/Services/IEventCoordinator.cs` | Append `IInstalledAppProvider InstalledAppProvider` to `EventCoordinationContext` |
| `src/EyeCtrl.App/App.xaml.cs` | Pass the provider into the context (append at end) |
| `src/EyeCtrl.App/Services/EventCoordinator.cs` | In `ShowSettingsWindow`: pass `_context.InstalledAppProvider` into `new SettingsQuickActionsEditorViewModel(...)` **and call `editor.Load()` once right after** |
| `src/EyeCtrl.App/Views/SettingsWindow.xaml.cs` | `_qaView` state + rewrite `BuildQuickActionsPages` into per-view builders; delete-confirm via `ShowModal`; reset `_qaView` **+ `CurrentPage = 0`** on every view transition and on tab change (in `OnSettingsViewModelPropertyChanged`) |
| `tests/EyeCtrl.ViewModels.Tests/SettingsWindowViewModelTests.cs` | Fake `IInstalledAppProvider` for the editor VM ctor |
| `tests/EyeCtrl.App.Tests/Services/EventCoordinatorTests.cs` | Add the media-preset dispatch test (each `QuickActionPresets` media command → `ExecuteMediaControl` key press); extends the existing fake-input harness |

## Testing

- **`SettingsQuickActionsEditorViewModelTests`** (fake store + fake app provider): `AddKeyboardShortcut` → one `KeyboardShortcut` action with the chord + label; `AddMediaControl` → `MediaControl` with the command; `AddApp` → `LaunchWindowsApp` with the AUMID and app name; `ToggleEnabled`/`DeleteAction` mutate and persist; `Load` reflects the store (including disabled actions).
- **`QuickActionPresetsTests`** (Core): for every **keyboard** preset, resolve the terminal key **in the same order `ExecuteKeyboardShortcut` uses** — single-character keys via `Win32InputSimulator.CharToVirtualKey(char.ToLowerInvariant(c))`, then (only if that yields 0) multi-character keys via `KeyNameMapper.TryMap(key, out var vk, out _)` (two `out` params) — and assert a non-zero VK. Note `Win+D` / `Win+E` / `Win+L` have single-char terminal keys, so they resolve through `CharToVirtualKey` (→ `VkKeyScanW`), **not** `KeyNameMapper` — a test that exercised only `KeyNameMapper.TryMap` would mis-validate them. (Media commands are validated App-side — next bullet — since `ExecuteMediaControl` is private to `EventCoordinator`.)
- **`EventCoordinatorTests` (media presets)**: drive **every** `QuickActionPresets` media command through `ExecuteMediaControl` (reusing the existing fake-input harness) and assert each produces a key press — i.e. no preset command falls through the executor's `switch` to a silent no-op.
- **`Win32InstalledAppProviderTests`**: smoke — `GetInstalledApps()` **never throws**, and **every returned entry** has a non-empty `DisplayName` and an AUMID containing `!`. An **empty result is a pass** (CI / minimal Windows images may have no packaged apps or an unavailable shell namespace); the test asserts shape-if-present, not non-emptiness.
- No new test framework or pattern — xUnit + NFluent + private nested fakes, matching the toolbar work.

## Risks

- **Shell `AppsFolder` COM interop** — moderate boilerplate (`IShellItem`/`IShellItem2`/`IEnumShellItems`/`PROPERTYKEY`), but a well-documented, stable surface; matches existing Platform COM. Risk: medium. Mitigation: filter to packaged AUMIDs for reliable activation; wrap enumeration in try/catch returning an empty list.
- **`SettingsWindow` view state** — adds statefulness to a tab. Mitigation: single `_qaView` field, reset (with `CurrentPage = 0`) on tab change and on every view transition; each view is a pure builder of dwell controls. The reset never lives inside `BuildQuickActionsPages`.
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
