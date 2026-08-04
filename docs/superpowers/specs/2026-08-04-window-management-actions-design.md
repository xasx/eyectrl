# Design: Window-management actions (gap #2)

**Date:** 2026-08-04
**Status:** Design (pre-implementation) — **revised after spec review**
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2 (OptiKey + Tobii Dynavox Computer Control both have it)

> **Revision note (2026-08-04):** the first draft was reviewed read-only against every seam it named. All 5 BLOCKERs were verified correct against the code (self-hit on EyeCtrl's own menu, destructive close of the Explorer desktop via `Progman`, a Core→ViewModels layering violation, a dead Back button, and an equality-gated re-show leaving invisible hit zones), as were two enum-drift tests. The design below incorporates those findings. The biggest change is the **resolver**: the `WindowFromPoint`+`GetAncestor` approach is replaced by a z-ordered hit-test over `EnumerateOpenWindows()`, which closes three findings at once. The sub-menu entry is **no longer an `InteractionAction` member** and there is **no new message type** — both reuse existing mechanisms. See §10 change-log.

## §0 Context & problem

The action menu currently offers click/scroll/drag/note actions only (`InteractionAction`: LeftClick, RightClick, DoubleClick, Drag, ScrollUp/Down/Left/Right, ScrollMode, PinNote — `src/EyeCtrl.Core/Models/InteractionAction.cs:4`). There is no way to **minimize, maximize/restore, or close the window under gaze** — a capability both OptiKey and TD Control ship. App-*switching* already landed (`src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs`), but per-window *management actions* are the remaining open slice of gap #2.

This design closes that slice. It deliberately **does not** add Show Desktop (global Win+D, doesn't fit "window under gaze" — a separate global action for later) or a window-picker grid (future expansion; §8).

## §1 Goal & scope

Add **Minimize**, **Maximize/Restore** (single toggle), and **Close** to the action menu, acting on the top-level window under the already-resolved target point, behind a grouped "Window" sub-menu.

**In scope:** three new `InteractionAction` members (Minimize, MaximizeRestore, Close — appended after PinNote), a window-under-point resolver, a sub-menu state + Back path, dispatch + icons + default visibility, unit tests, two enum-drift-test updates.

**Out of scope:** Show Desktop, a window-picker grid (option C), multi-monitor coordinate handling (separate gap #7), localization of icon tooltips.

## §2 Targeting — implicit, window-of-target-point

The target point is already resolved by `InteractionManager.OnActionSelected` (`src/EyeCtrl.Core/Processing/InteractionManager.cs:130-142`) into the snapped UIA control center (L135-136), the original fixation point (L137-138), or the precise zoom dwell point (`ManualSelectionDwellPoint`, L139-140). Window actions resolve **the top-level window owning that point** — reusing the existing target point. Zero new UI affordance for targeting.

### Resolver — z-ordered hit-test over `EnumerateOpenWindows()` *(revised: was `WindowFromPoint`+`GetAncestor`)*

`ControlFinder.FindTarget` (`src/EyeCtrl.Platform/Services/ControlFinder.cs:38`) only resolves **leaf** controls (walks *up* to interactive ancestors, never to a `Window`), so a new resolver is required. The first draft used `WindowFromPoint` → `GetAncestor(GA_ROOT)`. **Review rejected this** for three verified reasons:

1. **Self-hit (BLOCKER):** `ActionMenuWindow` is **not** click-through — `OverlayWindow.cs:84-87` gates `WS_EX_TRANSPARENT` on `IsClickThrough`, and `ActionMenuWindow` never sets it. `WindowFromPoint` hit-tests it, so resolving the HWND *at Window-dwell time* returns EyeCtrl's own menu (or `IntPtr.Zero` after the tool-window guard). *(Fixed by resolving the HWND before the menu is shown — but see the consolidated fix.)*
2. **Destructive desktop close (BLOCKER):** on the bare desktop `WindowFromPoint` returns `Progman`/`WorkerW`, which is visible, not tool-window, not own-process — it passes both guards the draft relied on. `PostMessage(WM_CLOSE)` to `Progman` tears down the Explorer desktop + taskbar — unacceptable for a gaze-only user.
3. **Owner-vs-parent (MAJOR):** `GA_ROOT` walks the parent chain; an owned modal dialog is top-level, so `GA_ROOT` returns the dialog, not its owner — diverging from the codebase's owner-rooted convention (`EnumerateOpenWindows` excludes `GW_OWNER != 0`, `Win32WindowManager.cs:37-38`).

**Adopted fix (reviewer's consolidated proposal):** add to `IWindowManager` / `Win32WindowManager`:

```csharp
AppWindow? WindowAtPoint(PhysicalPoint point);
```

implemented as a **z-ordered hit-test over the existing `EnumerateOpenWindows()` pass** — `EnumWindows` already yields windows top-to-bottom, so the first enumerated window whose rect (`GetWindowRect`) contains the point is the topmost one under gaze. This closes all three findings at once:

- **No self-hit:** `EnumerateOpenWindows` filters `WS_EX_TOOLWINDOW` (all EyeCtrl overlays) *before* hit-testing (`Win32WindowManager.cs:31-34`).
- **No `Progman`:** it also filters empty-title (`Win32WindowManager.cs:49-56`), cloaked (`:42-44`), and owned (`:37-38`) windows — `Progman`/`WorkerW`/`Shell_TrayWnd` all fail.
- **No owner decision:** `EnumerateOpenWindows` only ever yields unowned roots, so `GA_ROOT`/`GA_ROOTOWNER` is moot.

Return type is `AppWindow?` (not `IntPtr`) — makes "not found" explicit, reuses the existing model (`src/EyeCtrl.Core/Models/AppWindow.cs`), and gives the future picker grid (§8) the title/process it will need. `GetWindowRect` is the **only** new P/Invoke. `IsWindow` is **not** currently declared anywhere in `src/` (verified — the first draft's self-review incorrectly claimed otherwise); it must be added for the stale-handle guard (§6).

**Cost:** one full `EnumWindows` pass per `Window` dwell. Irrelevant next to the ~500 ms UIA budget already spent on control resolution (`GazeEngineAdapter.cs:205-207`).

### Pure-predicate factor for testability

The "is this `AppWindow` a valid target, and does its rect contain the point?" decision is extracted as a pure function over injected `AppWindow` + point data, so it is unit-testable with NFluent and **no** Win32, **no** fake `IWindowManager`. (Testing a fake of the interface under test would be circular.) The P/Invoke shell around it stays untested — consistent with how `Win32WindowManager` is treated today.

## §3 Action set

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** (single toggle) → pure `WindowStateOp` over `IsZoomed`/`IsIconic` (see below)
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful; `DefWindowProc` derives this from `SC_CLOSE`)

### Maximize/Restore toggle — pure function *(revised: now also handles `IsIconic`)*

Extract into `EyeCtrl.Core` (no Win32 reference → unit-testable without fakes):

```csharp
WindowStateOp DecideWindowOp(bool isZoomed, bool isIconic)
    => isIconic ? WindowStateOp.Restore            // un-minimize first
     : isZoomed ? WindowStateOp.Restore            // maximized → restore
     : WindowStateOp.Maximize;                      // normal → maximize
```

`Win32WindowManager` consumes it: read `IsZoomed`+`IsIconic`, call `ShowWindow` with the chosen `SW_*`. `IsIconic` is needed because the deferred picker grid (§8) can reach an already-minimized window (unreachable by gaze today), and a window minimized *from* maximized keeps `WS_MAXIMIZE` — `IsZoomed` is true but the right op is `Restore` (un-minimize), not a second maximize.

### Close behavior — no confirm, **conditional** on two guards *(revised)*

Close fires immediately on dwell completion, **without** a confirm sub-step — consistent with LeftClick/RightClick/Drag (no confirms anywhere in the existing flow), and `WM_CLOSE` preserves the target app's own "save changes?" prompt. **This is defensible only because of two guards** (the reviewer's condition, accepted):

1. **Ring-position guard (§4):** Close is never placed at the ring position the `Window` button occupied, so the sub-ring appearing under the user's resting gaze cannot accidentally fire Close.
2. **Back path fixed (§4):** a gaze-only user who opens the Window sub-menu by mistake has a working way out.

**Caveats to document (review MINOR):** `ConsoleWindowClass` hosts terminate on `WM_CLOSE` with no save prompt (so "still gives a chance to cancel" is not universal), and apps that map "X = minimize to tray" via `WM_SYSCOMMAND` will *actually close* under `WM_CLOSE`. State these rather than implying a blanket safety net.

### Observable failures *(revised per review MAJOR)*

Every op returns `bool`; a failed op (`PostMessage` to an elevated window is blocked by UIPI and returns `false`; `ShowWindow`/`PostMessage` return values checked) is logged at `Information` like the existing dispatch (`EventCoordinator.cs:1369-1371`). The decision on a *visible* cue for failure is deferred — the struggle-detection cue overlay exists as precedent if user confusion warrants it. At minimum the decision is explicit, not silent.

## §4 Placement — "Window" sub-menu, with its own state *(substantially revised)*

A single **"Window"** button in the main ring opens a sub-ring: `{Minimize, MaximizeRestore, Close, Back}`. Keeps the main ring readable and groups related actions. (The density arithmetic in the first draft was off by omitting Back; the manual-selection ring is already 9 items today, `angleStep = 360/allItems.Count` including Back — the sub-menu is still the better call for *grouping* reasons, not the 11-at-32° figure the draft cited.)

### The sub-menu entry is NOT an `InteractionAction` member *(revised — review MAJOR)*

The first draft added a `Window` enum member. Review correctly noted this forces a navigation marker through five unrelated sites (settings defaults, `EnsureDefaults`, the glyph map, the `EventCoordinator` switch, persisted settings). **Back already models menu navigation without the enum** — `ActionMenuItemViewModel` carries `InteractionAction? Action` + `bool IsBackButton`, and `OnTargetActivated` branches on the flag with `Action == null` (`ActionMenuItemViewModel.cs:9,16`; `ActionMenuViewModel.cs:47-51,87-95`). The Window entry is modelled the same way: a menu item with `Action == null` + a `IsSubMenu`/kind flag. **Only the three real actions (Minimize, MaximizeRestore, Close) are added to `InteractionAction`.**

### New state required — `WindowMenuVisible` *(revised — review BLOCKER + MAJOR)*

The first draft claimed "no persistent state." Review verified this is wrong on three counts: (a) **Back is a dead button** — `OnManualSelectionBack` (`InteractionManager.cs:170-189`) handles only `ManualSelectionActionMenuVisible` and `ManualSelectionActive`, returning *silently* in `ActionMenuVisible`; (b) `GazeEngineAdapter` derives `ShowBack` **and** dwell time from `CurrentState` (`GazeEngineAdapter.cs:112-115`), so a sub-ring can't get a Back button without a state; (c) the backdrop-dismiss handler (`EventCoordinator.cs:466-470`) and the target-point `switch` (`InteractionManager.cs:132-142`) both route on state.

**Add `InteractionState.WindowMenuVisible`** and enumerate its behaviour: Back → return to the parent ring (`ActionMenuVisible`/`ManualSelectionActionMenuVisible`); dismiss → free look; pause/`TogglePause` → same handling as the parent menu. The stashed target HWND lives on the transition into this state.

### No new message type *(revised — review MAJOR)*

The first draft proposed `ShowWindowMenuMessage`. Review verified (a) this would be raised from `InteractionManager` in **Core**, but `EyeCtrl.Core.csproj` has **zero** `ProjectReference`s — Core→ViewModels is backwards; `InteractionManager` communicates by C# `event`s that `GazeEngineAdapter` translates to messages; and (b) `ShowWindowMenuMessage(Center, Actions)` would be missing `DwellTimeMs`/`CircleSize`/`GazePoint` that `ActionMenuViewModel.Receive` needs.

**Re-send `ShowActionMenuMessage`** (`InteractionMessages.cs:21`) with a filtered `Actions` list and `ShowBack: true`. It already carries every field the VM needs. This follows the existing `ShowActionMenu` event → `ShowActionMenuMessage` path in `GazeEngineAdapter.cs:134-155`.

### Re-show must actually repaint *(revised — review BLOCKER)*

`ActionMenuWindow.Show` is called **only** from the `IsVisible` `PropertyChanged` handler (`EventCoordinator.cs:1071-1090`). If the sub-menu message arrives while the menu is already up, `Receive` rebuilds `Items`/dwell targets (`ActionMenuViewModel.cs:43-73`) but `IsVisible = true` is equality-gated — no `PropertyChanged`, `Show()` never re-runs, and the user sees the **old ring** over **new invisible hit zones**. The design must either:

- send an explicit `HideActionMenuMessage` → `ShowActionMenuMessage` sequence (accepting the backdrop hide/show + ~100 ms re-capture at `ActionMenuWindow.xaml.cs:92-96`), **or**
- have the VM force the `IsVisible` notification (flip false→true, or raise `PropertyChanged` unconditionally).

Pick one at implementation; state which.

### Ring-position guard for Close (§3 condition 1)

Ring position = list order, item 0 at 12 o'clock (`angleStep * i - 90°`, `ActionMenuViewModel.cs:57`). The sub-ring `Actions` list is ordered so **Close is never at the index the `Window` button occupied in the parent ring.** Trivially satisfiable since the sub-ring has ≤4 items and the sender controls list order.

## §5 Components & seams *(revised)*

| Change | File | Notes |
|---|---|---|
| New enum members (real actions only) | `src/EyeCtrl.Core/Models/InteractionAction.cs:4` | Append `Minimize`, `MaximizeRestore`, `Close` **after `PinNote`** (the `PinNote==9` test pins ordering). No `Window` member. |
| New state | `src/EyeCtrl.Core/Models/InteractionState.cs` | Add `WindowMenuVisible`. |
| Resolver + ops | `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` + `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | Add `AppWindow? WindowAtPoint(PhysicalPoint)` (z-ordered hit-test over `EnumerateOpenWindows`), `Minimize/MaximizeOrRestore/CloseWindow(IntPtr hwnd)` returning `bool`. Update the interface summary (it currently says only "Enumerates … and brings to front"). |
| Pure toggle | `src/EyeCtrl.Core` (new small type, e.g. `Models/WindowStateOp.cs`) | `DecideWindowOp(bool isZoomed, bool isIconic) → WindowStateOp`. No Win32 reference. |
| New Win32 P/Invoke | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | Add `GetWindowRect` (only new one for the resolver), `ShowWindow` + `SW_*`, `PostMessage` + `WM_CLOSE`, `IsZoomed`, `IsIconic`, `IsWindow`. Use `[LibraryImport]` (file style), **not** `[DllImport]`. **Verified absent 2026-08-04:** none of these are in `NativeMethods.cs`. (`WindowFromPoint`/`GetAncestor` already exist elsewhere — `ExternalDwellService.cs:329-340`, `ControlFinder.cs:181,193-200` — but are not reused here; decide at implementation whether to consolidate `ExternalDwellService.RootWindowAtPointImpl` into the new seam or leave it. Not a blocker.) |
| Sub-menu entry + Back | `InteractionManager.cs`, `GazeEngineAdapter.cs`, `ActionMenuViewModel.cs` | `Window` modelled as `Action==null`+kind (like Back); add `WindowMenuVisible` state + a Back transition into it; no new message type (re-send `ShowActionMenuMessage` filtered + `ShowBack:true`). |
| Dispatch | `src/EyeCtrl.App/Services/EventCoordinator.cs:1386` switch | New `Minimize`/`MaximizeRestore`/`Close` cases → `IWindowManager` on the stashed HWND, returning `bool`, logged. No `default` exists today — add one or ensure exhaustive. |
| Icons | `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:103-116` (`GetActionSymbol`) | New glyph cases. **Use BMP glyphs** (review MINOR): Minimize `▁` (U+2581), Maximize/Restore `□` (U+25A1), Close `✕` (U+2715). The first draft's `iminalage`/` Expand` are Supplementary-Plane (U+1F5D5/1F5D6) — no symbol font is set, WPF fallback is unreliable, tofu here is a usability bug. Verify on the target box before locking the symbol test. |
| Default visibility | `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:11-18` + `EnsureDefaults :28-47` | Add the Window sub-menu entry (not an enum member) to the default visible set + `EnsureDefaults` seed. |
| Drift tests | `tests/EyeCtrl.ViewModels.Tests/ActionMenuViewModelSymbolTests.cs:31-46`, `tests/EyeCtrl.Core.Tests/Models/InteractionActionEnumTests.cs:14` | Extend the glyph map for the 3 new actions; the `PinNote==9` pin stays valid (new members append after). |

### DI flow
`IWindowManager` is already a registered singleton (`AppBootstrapper.cs:74`) injected into the coordinator context (`IEventCoordinator.cs:83`, constructed `App.xaml.cs:202`). The new methods flow through DI with no new registration. A second consumer means the **Task Switcher path must be re-verified** — it is currently the only `IWindowManager` caller.

## §6 Edge cases & safety *(revised)*

- **Target is the desktop / no top-level window:** resolver returns `null` (`AppWindow?`) — never `Progman`/`WorkerW` (filtered by `EnumerateOpenWindows`). The Window sub-menu action is suppressed (sub-ring shows only Back).
- **Target is EyeCtrl's own overlay:** filtered by `WS_EX_TOOLWINDOW` *before* hit-test in `EnumerateOpenWindows`. Re-verify own-process guard as a second line.
- **Target is a child/owned window:** `EnumerateOpenWindows` only yields unowned roots, so the owner-vs-parent question is moot.
- **Close on unsaved work:** `WM_CLOSE` is graceful — the target app's own prompt fires. **Exceptions to document:** `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close. Not a universal safety net.
- **Maximize/Restore on a fullscreen (F11) window:** F11 fullscreen is typically a borderless window sized to the monitor *without* `WS_MAXIMIZE`; `SW_MAXIMIZE` on it can resize it to the **work area**, leaving a taskbar strip — a visible, wedged state a gaze-only user may not escape. **Guard:** skip the toggle when the window rect covers the monitor rect and `!IsZoomed`. (The first draft asserted this was "harmless" without evidence — corrected.)
- **Already-minimized window:** `IsIconic` → `Restore` (un-minimize first). Relevant for the deferred picker grid (§8).
- **Stale HWND between menu-open and action-fire:** guard each op with `IsWindow(hwnd)` (must be added) and no-op if false. This timing-dependent case doesn't exist for click actions.
- **Elevated target window:** `PostMessage`/`ShowWindow` blocked by UIPI, returns `false` → logged (§3), no silent failure.
- **Multi-monitor points:** still primary-only (gap #7). `WindowAtPoint` is screen-space and works regardless of monitor, so window actions are *not* blocked by #7 — but full multi-monitor cursor handling is unchanged here.

## §7 Testing *(revised)*

Unit tests (no display, NFluent; private nested fakes, no mocking lib):

- **Resolver predicate** (pure function, no fake-of-interface): over injected `AppWindow` + point — accepts a containing rect, rejects empty-title/owned/cloaked/tool-window/own-process, returns null when no window contains the point. *(First draft's "fake `IWindowManager`" was circular — corrected.)*
- **`DecideWindowOp`** (pure): all four `(isZoomed, isIconic)` combinations.
- **Settings:** default visible set includes the Window sub-menu entry; `EnsureDefaults` seeds it.
- **`InteractionState` + `InteractionManager`:** entering `WindowMenuVisible`; Back returns to the parent ring; dismiss → free look; the `Window` entry raises the re-show path, not `ActionRequested`.
- **Dispatch:** `Minimize`/`MaximizeRestore`/`Close` route to `IWindowManager` with the stashed HWND; return `bool`; a stale (`!IsWindow`) HWND is a no-op; failures log.
- **Drift tests updated** (above).

UI tests (`Category=UI`): **not gating** — Win32 calls need a real top-level window; cover via fakes + a manual smoke note. Excluded from CI per `AGENTS.md`.

## §8 Out of scope / future expansion

- **Show Desktop** (global Win+D via `Win32InputSimulator.SendModifiedKeyPress`): separate global action, disjoint seam. Independent.
- **Window-picker grid (option C):** reuses `WindowAtPoint` *and* the three ops — §8's "A does not preclude C" holds. The grid is what makes the already-minimized case (§6) *reachable*, which is why `IsIconic` is settled now rather than deferred.
- **Per-action dwell times:** reuse `ActionMenuDwellMs`. Later polish.

## §9 Open questions for implementation

- **Re-show mechanism (§4):** hide→show sequence vs. forced `IsVisible` notification — pick one.
- **`ExternalDwellService.RootWindowAtPointImpl` consolidation (§5):** fold into the new `IWindowManager.WindowAtPoint` seam, or leave as a third copy? Not a blocker; state the decision.
- **Visible failure cue (§3):** log-only for v1; revisit if user confusion warrants the struggle-detection cue overlay.

## §10 Change log
- **2026-08-04 (revision after review):** all 5 BLOCKERs + the load-bearing MAJORs verified correct against the code and incorporated. Resolver changed from `WindowFromPoint`+`GetAncestor` to a z-ordered `EnumerateOpenWindows` hit-test (closes self-hit, Progman, owner-vs-parent). Sub-menu entry removed from `InteractionAction` (modelled like Back); no new message type (re-send `ShowActionMenuMessage`); new `WindowMenuVisible` state + Back path. Close stays no-confirm, conditional on ring-position + Back guards. Maximize/Restore now an `IsIconic`-aware pure function. Glyphs switched to BMP. Two drift tests flagged. Corrected the first draft's inverted P/Invoke self-review (`IsWindow` is absent, not present). Line refs corrected (`AppBootstrapper.cs:74`, `App.xaml.cs:202`, `EventCoordinator.cs:1373`).
- **2026-08-04 (initial):** first draft, reviewed against `main` @ `b590a87`. Self-review corrected P/Invoke presence and dispatch-switch line (but the P/Invoke correction was itself inverted — fixed in this revision).
