# Design: Window-management actions (gap #2)

**Date:** 2026-08-04
**Status:** Design (pre-implementation)
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2 (OptiKey + Tobii Dynavox Computer Control both have it)

## §0 Context & problem

The action menu currently offers click/scroll/drag/note actions only (`InteractionAction`: LeftClick, RightClick, DoubleClick, Drag, ScrollUp/Down/Left/Right, ScrollMode, PinNote — `src/EyeCtrl.Core/Models/InteractionAction.cs:4`). There is no way to **minimize, maximize/restore, or close the window under gaze** — a capability both OptiKey and TD Control ship. App-*switching* already landed (`src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml.cs`), but per-window *management actions* are the remaining open slice of gap #2.

This design closes that slice. It deliberately **does not** add Show Desktop (global Win+D, doesn't fit "window under gaze" — a separate global action for later) or a window-picker grid (future expansion; §8).

## §1 Goal & scope

Add **Minimize**, **Maximize/Restore** (single toggle), and **Close** to the action menu, acting on the top-level window under the already-resolved target point.

**In scope:** three new actions, a window-under-point resolver, a "Window" sub-menu entry in the action menu, dispatch + icons + default visibility, unit tests.

**Out of scope:** Show Desktop, a window-picker grid (option C), multi-monitor coordinate handling (separate gap #7), localization of new icon tooltips.

## §2 Targeting — implicit, window-of-target-point

The target point is already resolved by `InteractionManager.OnActionSelected` (`src/EyeCtrl.Core/Processing/InteractionManager.cs:130-142`) into one of:

- the snapped UIA control center (AutoSelection + resolved control → `AutoSelectionTarget.CenterPoint`, L135-136),
- the original fixation point (where the menu was invoked, L137-138), or
- the precise zoom dwell point (`ManualSelectionDwellPoint`, L139-140).

Window-management actions resolve **the top-level window owning that point** — reusing the existing target point. Zero new UI affordance for targeting; the model matches how LeftClick/Drag already "act on what I was looking at."

### New resolver

`ControlFinder.FindTarget` (`src/EyeCtrl.Platform/Services/ControlFinder.cs:38`) only resolves **leaf** interactive controls (it walks *up* via `TreeWalker.ControlViewWalker.GetParent` to the first interactive ancestor, L73-109, and never to a `ControlType.Window`). So a new resolver is required, not a reuse.

Add to `IWindowManager` / `Win32WindowManager`:

```csharp
IntPtr TopLevelWindowFromPoint(PhysicalPoint point);
```

Implementation: `NativeMethods.WindowFromPoint` → `GetAncestor(hwnd, GA_ROOT)` to reach the top-level owner (cleaner than the client-rect fallback in `ControlFinder.FallbackToWindow`, L178-191, which discards the HWND). This resolver is also exactly what the future window-picker grid (option C, §8) would need per tile — so option A does not preclude C.

## §3 Action set

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** (single toggle button) → `IsZoomed(hwnd)` ? `SW_RESTORE` : `SW_MAXIMIZE`
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful — lets the target app prompt "save changes?" normally; chosen over `WM_SYSCOMMAND SC_CLOSE`/`TerminateProcess` deliberately)

The Maximize/Restore toggle avoids two buttons where one is always a no-op (the standard window-manager convention). Minimize is always its own button.

### Close behavior (deliberate: no confirm sub-step)

Close fires immediately on dwell completion, **without a confirm sub-step**, consistent with LeftClick/RightClick/Drag which all fire without confirmation in the existing flow (dwell = intent). `WM_CLOSE` preserves the target app's own "save changes?" prompt, so an accidental dwell still gives the user a chance to cancel at the *application* level. This is the call most worth confirming during review; a Close→confirm sub-step is the documented alternative.

## §4 Placement — "Window" sub-menu button

A single **"Window"** button in the main ring opens a sub-ring: `{Minimize, Maximize/Restore, Close, Back}`. This keeps the main ring at **9 items** (the 8 existing defaults + Window) instead of 11 (~32° apart — too dense for reliable dwell on a 100-DIP-diameter button; `ActionMenuGeometry.ButtonRadius = 50`, `src/EyeCtrl.App/Overlays/ActionMenuGeometry.cs:17`).

### How (reuse, no second overlay)

Handle the `Window` action like the existing special-cases in `InteractionManager.OnActionSelected` (ScrollMode at `InteractionManager.cs:146`, Drag at `:160`) — intercept it rather than raising `ActionRequested`:

1. Resolve the top-level HWND from the target point (§2) and **stash it** as state on the action-request that follows (the HWND must survive across the menu transition; see §6 stale-HWND edge case).
2. Raise a new `ShowWindowMenuMessage(PhysicalPoint Center, IReadOnlyList<InteractionAction> Actions)` rather than `ActionRequested`.
3. `EventCoordinator` / `ActionMenuViewModel` re-shows the **same** `ActionMenuWindow` with the filtered `{Minimize, MaximizeRestore, Close}` list + a Back button. The Back concept already exists (`BackSelectedMessage`, `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`; handled in `ActionMenuViewModel.OnTargetActivated`, `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:92`) — Back returns to the main action ring.

The window-management actions (`Minimize`/`MaximizeRestore`/`Close`) then go through the normal `ActionRequested` path → new cases in the `EventCoordinator.HandleActionRequested` switch (`src/EyeCtrl.App/Services/EventCoordinator.cs:1386`; existing cases at `:1388-1395`). Note the switch has **no `default`** case — `PinNote` is special-cased in an `if` before the switch (~`:1378`), and `ScrollMode`/`Drag` never reach here (intercepted in `InteractionManager`). A new enum member reaching this switch without a case is a silent no-op, so exhaustiveness must be verified at implementation.

No new overlay window type is needed.

### Alternative considered (flat top-level ring buttons)

Add Minimize/MaximizeRestore/Close directly to the main ring (no sub-menu). Simpler code (no new message, no menu-state transition), but the ring grows to 11 items at ~32° spacing — too dense for reliable dwell. Rejected in favor of the grouped sub-menu.

## §5 Components & seams

| Change | File | Notes |
|---|---|---|
| New enum members | `src/EyeCtrl.Core/Models/InteractionAction.cs:4` | Add `Window` (sub-menu entry), `Minimize`, `MaximizeRestore`, `Close` |
| Window-under-point + ops | `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` + `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | Add `TopLevelWindowFromPoint(point)`, `Minimize(hwnd)`, `MaximizeOrRestore(hwnd)`, `CloseWindow(hwnd)` |
| New Win32 P/Invoke | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | Add `WindowFromPoint`, `ShowWindow` (+ `SW_MINIMIZE`/`SW_MAXIMIZE`/`SW_RESTORE`), `PostMessage` (+ `WM_CLOSE = 0x0010`), `GetAncestor` (+ `GA_ROOT = 2`), `IsZoomed`. Verified absent 2026-08-04: only `IsWindow` of the needed set is already present (`EnumWindows`/`IsWindowVisible`/`GetWindow`/`SetForegroundWindow` are present but not relevant here). |
| Sub-menu interception | `src/EyeCtrl.Core/Processing/InteractionManager.cs:146` | New `Window` case → resolve HWND, stash it, raise `ShowWindowMenuMessage` |
| New message | `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `record ShowWindowMenuMessage(PhysicalPoint Center, IReadOnlyList<InteractionAction> Actions)` |
| Dispatch | `src/EyeCtrl.App/Services/EventCoordinator.cs:1386` switch | New `Minimize`/`MaximizeRestore`/`Close` cases → call `IWindowManager` on the stashed HWND. No `default` exists today — add one or ensure exhaustive. |
| Icons | `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:103-116` (`GetActionSymbol`) | New glyph cases: Window ⧉, Minimize 🗕, MaxRestore 🗖, Close ✕. Falls through to `action.ToString()` otherwise (`:115`). |
| Default visibility | `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:11-18` + `EnsureDefaults :28-47` | Add `Window` to the default `VisibleActions` list and to the `EnsureDefaults` seed list |

### DI flow
`IWindowManager` is already a registered singleton (`AppBootstrapper.cs:75`) injected into the coordinator context (`IEventCoordinator.cs:83`, constructed `App.xaml.cs:206`). The new methods on it flow through DI with no new registration.

## §6 Edge cases & safety

- **Target is the desktop / no top-level window** (menu invoked on empty desktop): resolver returns `IntPtr.Zero` → the `Window` sub-menu action is suppressed (or the sub-ring hides its window-actions and shows only Back). The resolver must never hand a zero HWND to `ShowWindow`/`PostMessage`.
- **Target is EyeCtrl's own overlay**: `Win32WindowManager` already excludes `WS_EX_TOOLWINDOW` windows (all EyeCtrl overlays carry it, `Win32WindowManager.cs:31-34`). The resolver reuses that filter so EyeCtrl never minimizes/closes itself. The resolver must also reject the current process (`NativeMethods.OwnProcessId`, as `ControlFinder` does at `ControlFinder.cs:14,81`) as a second guard.
- **Target is a child/owned window**: `GetAncestor(GA_ROOT)` resolves to the top-level owner, so a click on a dialog or tool-window child acts on the correct root. Owned tool windows are excluded by the `WS_EX_TOOLWINDOW` filter; non-tool owned windows (rare) act on their root — acceptable.
- **Close on unsaved work**: `WM_CLOSE` is graceful; the target app's own "save changes?" dialog fires normally. This is the reason `PostMessage(WM_CLOSE)` is chosen over force-close.
- **Maximize/Restore on a fullscreen (F11) window**: `IsZoomed` returns false for a fullscreen window, so the toggle would `SW_MAXIMIZE` an already-fullscreen window — harmless (no visible change). Acceptable for v1.
- **Stale HWND between menu-open and action-fire**: the HWND is resolved when the user dwells `Window` (opening the sub-ring), and a non-trivial dwell time elapses before they dwell `Minimize`/`MaximizeRestore`/`Close`. If the window was closed by other means in that window, the HWND is stale. Guard each op with `NativeMethods.IsWindow(hwnd)` (already declared) and no-op if false. This is the one new timing-dependent case the existing click actions don't have.
- **Multi-monitor points**: still primary-only (gap #7, `Win32InputSimulator.cs:12-15`). `WindowFromPoint` is screen-space and works regardless of monitor, so window actions are *not* blocked by #7 the way cursor movement is — but coordinate handling is unchanged here; full multi-monitor is a separate gap.

## §7 Testing

Unit tests (no display, NFluent per `AGENTS.md`; private nested fakes, no mocking lib):

- **`TopLevelWindowFromPoint` resolver** (via a fake `IWindowManager` in coordinator tests): returns root owner, skips `WS_EX_TOOLWINDOW`, skips own-process, returns `IntPtr.Zero` for desktop/empty regions.
- **Maximize/Restore toggle logic**: extract `WindowStateOp` into a pure function `(bool isZoomed) → SW_RESTORE | SW_MAXIMIZE` so it's unit-testable without Win32. Test both branches.
- **Settings**: `ActionMenuSettings.EnsureDefaults` includes `Window`; default `VisibleActions` is the documented 9-item list.
- **`InteractionManager.OnActionSelected` for `Window`**: raises `ShowWindowMenuMessage` (not `ActionRequested`); does not transition to a persistent state (contrast ScrollMode/Drag which do); resolves + stashes HWND.
- **Dispatch**: `Minimize`/`MaximizeRestore`/`Close` cases in `EventCoordinator.HandleActionRequested` route to `IWindowManager` (fake) with the stashed HWND; a stale (`!IsWindow`) HWND is a no-op.

UI tests (`Category=UI`): **not gating.** The Win32 calls need a real top-level window; cover via the unit-level fakes + a manual smoke note in the implementation plan. (UI tests are excluded from CI per `AGENTS.md`.)

## §8 Out of scope / future expansion

- **Show Desktop** (global Win+D via the existing `Win32InputSimulator.SendModifiedKeyPress`): a separate global action, doesn't fit the window-under-gaze targeting. Add later as its own menu item, independent of this design.
- **Window-picker grid (option C)**: a fullscreen grid of live window tiles (reuse `TaskSwitcherWindow`) where the user dwells Minimize/Close on the chosen tile — lets you act on *any* window, not just the one under gaze. The `TopLevelWindowFromPoint` resolver built here is reusable as the per-tile HWND source, so option A does not preclude C. Defer until A ships and proves the dispatch path.
- **Per-action dwell times**: this design reuses the existing `ActionMenuSettings` / action-menu dwell time (`DwellSettings.ActionMenuDwellMs`). Separate per-surface dwell for window actions is a later polish, not needed for v1.

## §9 Change log
- 2026-08-04: initial design, reviewed against `main` @ `b590a87`. Seams verified via read-only trace (action-menu dispatch path, `Win32WindowManager` surface, `ControlFinder` leaf-only behavior, all `InteractionAction` dispatch sites). Self-review corrections: all 6 new P/Invokes confirmed absent from `NativeMethods` (only `IsWindow` present); dispatch switch at `EventCoordinator.cs:1386` (not `:1391`) and has no `default` case. Open questions flagged for reviewer: Close no-confirm (§3), `IWindowManager` vs new abstraction as the home for window ops (§5).
