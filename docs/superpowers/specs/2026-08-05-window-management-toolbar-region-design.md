# Design: Window-management actions (gap #2) — toolbar region (B2)

**Date:** 2026-08-05 · **Status:** Design (pre-implementation)
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2
**Supersedes:** `2026-08-04-window-management-actions-design.md` (the sub-menu approach, which went 5 review rounds without converging — see `docs/superpowers/POSTMORTEM-window-mgmt-spec-rounds-1-5.md`).

## §0 Context & decision

Add **Minimize**, **Maximize/Restore** (toggle), and **Close** for the foreground window, as a region on the **existing auto-hiding toolbar**. Both OptiKey and TD Control ship window management; app-*switching* already landed in EyeCtrl (`TaskSwitcherWindow`); per-window *management actions* are the remaining open slice of gap #2.

**Why a toolbar region, not the action menu (the decision that resolved the 5-round impasse):** the action menu is for *point-actions* ("do something at this control/coordinate" — click, drag, scroll here). Window management is a *window-level* operation that has nothing to do with which control gaze is on. Forcing window ops into the point-action ring created a category mismatch that surfaced as an unbounded series of contract gaps across 5 review rounds (resolver self-hit, stash race, missing `WindowMenuVisible` arms, `TogglePause` leak, …). Moving the actions to the toolbar resolves the mismatch *and* the resolver class of problems at once: the toolbar targets **the foreground window** (`GetForegroundWindow`), which Windows tracks and which never returns EyeCtrl's `WS_EX_NOACTIVATE` overlays — so there is no `WindowFromPoint`, no self-hit, no Progman, no `GA_ROOTOWNER`, no stash race, no occlusion degradation.

**Why the toolbar specifically (B2, not a standalone corner bar):** EyeCtrl already ships a `ToolbarAutoHideController` (hide on gaze-leave, summon by looking past the docked edge) and a persistent `ToolbarWindow`. A window-actions region rides the toolbar's auto-hide for free (the controller is rect-only — `ToolbarAutoHideController.cs:116,159` — so adding buttons auto-widens the cached rect with zero controller change). No new overlay surface, one summon gesture, two existing systems reused.

## §1 Goal & scope

Add three gaze-dwellable buttons — Minimize `─`, Maximize/Restore `☐`, Close `✕` — to the toolbar, acting on the foreground window via three new `IWindowManager` operations.

**In scope:** 3 new toolbar buttons (View array + VM array + VM dispatch cases); 1 new message + 1 recipient in `EventCoordinator`; 3 new `IWindowManager` ops (`Minimize`/`MaximizeOrRestore`/`CloseWindow`) backed by new P/Invokes; a `ToolbarSettings.ShowWindowActions` toggle (default true); a shell/self-window guard; handling for the toolbar-disabled case.

**Out of scope:** Show Desktop (global Win+D — a different *kind* of action, not per-foreground-window; deferred to its own future toolbar button), a window-picker grid, multi-monitor cursor handling (gap #7), the action-menu sub-menu (abandoned — see §0 + postmortem).

## §2 Targeting — the foreground window

`GetForegroundWindow()` (new P/Invoke, absent today — verified) returns the HWND of the active top-level window. Read it **at op-fire time** (inside the recipient handler, synchronous), not stashed across an async hop. This is the key simplification vs the abandoned sub-menu design: there is no stash, so there is no stash race.

**Shell/self guard (defense-in-depth):** `GetForegroundWindow` can return shell windows (`Progman`, `WorkerW`, `Shell_TrayWnd`) when the desktop/taskbar is focused, and could in principle return an EyeCtrl window. Reject the HWND if:
1. It equals `GetShellWindow()` (the definitive shell-desktop HWND).
2. Its class name (`GetClassName`) is in `{Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd}` — covers the cases where `GetShellWindow` and `GetForegroundWindow` disagree (e.g. `WorkerW` under animated wallpaper; secondary taskbars on multi-monitor).
3. Its process is the current process (`ProcessId == Environment.ProcessId`) — EyeCtrl never minimizes/closes itself.

On rejection: no-op the op, log at `Information` ("foreground window rejected: shell/self"). The buttons remain dwellable; nothing destructive can happen.

This guard reuses the eligibility concept from the abandoned spec's §2, but applied to *one* HWND (`GetForegroundWindow`) rather than resolved through `WindowFromPoint`+`GetAncestor` — far simpler, no owner-vs-parent decision (foreground is by definition a top-level unowned window).

## §3 Action set

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** (single toggle) → pure `DecideWindowOp(bool isZoomed, bool isIconic)` then the matching `SW_*`:
  - `!isZoomed && !isIconic` → Maximize (`SW_MAXIMIZE`)
  - `isZoomed && !isIconic` → Restore (`SW_RESTORE`)
  - `!isZoomed && isIconic` → Restore (un-minimize)
  - `isZoomed && !isIconic` → Restore (minimized-from-maximized → un-minimize; `IsIconic` first)
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful; `DefWindowProc` derives from `SC_CLOSE`; the app's own "save changes?" prompt fires).

**Close behavior — no confirm** (decision: matches the toolbar). Every other toolbar button (Pause, Settings, TaskSwitcher) fires on dwell with no confirm; Close is consistent. The gaze-safety concern that made no-confirm contentious in the sub-menu design is much weaker here: Close sits at a **stable, known position** on the toolbar (not at a gaze-rest spot that shifts with the ring), and the toolbar **auto-hides** so it is not present while the user is doing other things. A user must deliberately summon the toolbar and dwell the Close button to fire it. `WM_CLOSE` additionally preserves the app's own save-changes prompt as a backstop. **Caveats to document:** `ConsoleWindowClass` hosts terminate on `WM_CLOSE` with no prompt; "minimize-to-tray-on-X" apps actually close under `WM_CLOSE`.

### Op return semantics

- `PostMessage` returns `bool` — UIPI-blocked sends to elevated windows return `false`; that *is* a meaningful failure → **log at `Information`**.
- `ShowWindow` return does **not** mean success/failure (zero = "was previously hidden") → **do not interpret as failure**. After the shell/self `IsWindow` guard, call fire-and-forget.
- `Minimize`/`MaximizeOrRestore` return `void` (or `bool` from the guard only); `CloseWindow` returns the meaningful `PostMessage` result.

### `DecideWindowOp` — pure, unit-testable

In `EyeCtrl.Core` (no Win32 reference):

```csharp
public enum WindowStateOp { Maximize, Restore }
public static WindowStateOp DecideWindowOp(bool isZoomed, bool isIconic)
    => (isZoomed, isIconic) switch
    {
        (_, true) => WindowStateOp.Restore,   // minimized → un-minimize first
        (true, _) => WindowStateOp.Restore,   // maximized → restore
        _ => WindowStateOp.Maximize,          // normal → maximize
    };
```

`Win32WindowManager` reads `IsZoomed`+`IsIconic`, calls `DecideWindowOp`, maps to `SW_*`, calls `ShowWindow`. The 3 non-default combinations are covered; `(isZoomed:true, isIconic:true)` (minimized-from-maximized) hits the first arm → Restore (un-minimize), which is correct.

## §4 Placement — toolbar region (B2)

The three buttons render as a region at the end of the toolbar's `ButtonPanel`. They reuse the toolbar's existing button construction, dwell machinery, and auto-hide — no new overlay, no new dwell target type, no new menu state.

### Where the buttons plug in (verified against current code)

Two arrays must list the new button ids (the toolbar has no settings-driven `ItemsSource`; the set is a literal in two places):
- **View:** `ToolbarWindow.xaml.cs:39-46` — the hardcoded `(Id, Symbol)` array that drives rendering. Add `("Minimize", "▁")`, `("MaximizeRestore", "□")`, `("Close", "✕")`.
- **ViewModel:** `ToolbarViewModel.cs:66-76` — the matching array that creates dwell targets. Same three entries.

(Note: these two arrays are *already out of sync* today — View has 5 buttons, VM has 8. Pre-existing tech debt, unrelated to this feature. The new entries go in both, consistent with how the existing buttons are split.)

**Glyphs are BMP** (`▁` U+2581, `□` U+25A1, `✕` U+2715) — consistent with the existing toolbar glyphs and avoiding the Supplementary-Plane tofu risk flagged in prior rounds.

### Dispatch — one message per button, mirroring Task Switcher

The toolbar's dispatch is a `switch` in `ToolbarViewModel.OnTargetActivated` (`ToolbarViewModel.cs:131-144`) sending one message per button id, received in `EventCoordinator`. This is the **exact pattern** `TaskSwitcherSelectionMessage(hwnd)` already uses (`TaskSwitcherViewModel.cs:87-95` → `EventCoordinator.cs:135-141` → `HandleTaskSwitcherSelection` → `IWindowManager.BringToFront(hwnd)`).

Three new cases in `OnTargetActivated`:

```csharp
case "Minimize":        Messenger.Send(new WindowActionRequestedMessage(WindowAction.Minimize)); break;
case "MaximizeRestore": Messenger.Send(new WindowActionRequestedMessage(WindowAction.MaximizeRestore)); break;
case "Close":           Messenger.Send(new WindowActionRequestedMessage(WindowAction.Close)); break;
```

**One new message + a discriminating enum** (one message, not three — the action is the payload):

```csharp
// EyeCtrl.ViewModels.Messages
public record WindowActionRequestedMessage(WindowAction Action);
// EyeCtrl.Core.Models (or ViewModels — see §5)
public enum WindowAction { Minimize, MaximizeRestore, Close }
```

**One new recipient** in `EventCoordinator`:

```csharp
_context.Messenger.Register<EventCoordinator, WindowActionRequestedMessage>(this, (_, msg) =>
    _context.Dispatcher.BeginInvoke(() => HandleWindowAction(msg.Action)));
```

`HandleWindowAction`:
1. `var hwnd = _context.WindowManager.GetForegroundWindow();` (new `IWindowManager` method)
2. Apply the §2 shell/self guard; if rejected, log + return.
3. `switch (msg.Action)` → `_context.WindowManager.Minimize(hwnd)` / `MaximizeOrRestore(hwnd)` / `CloseWindow(hwnd)`.
4. Log the result at `Information` (per §3 return semantics).

### Auto-hide — no change

`ToolbarAutoHideController` is rect-only (`:116,159`); adding buttons auto-widens the cached rect (the toolbar is `SizeToContent`), which the controller picks up via the existing `RefreshToolbarRect`/`CaptureToolbarRect` calls in `EventCoordinator`. The window region hides and summons together with the rest of the toolbar. Zero controller change.

## §5 Settings — `ShowWindowActions` toggle

`ToolbarSettings` (`src/EyeCtrl.Core/Settings/ToolbarSettings.cs`) currently has only `IsDocked`, `DockedEdge`, `Size`. Add:

```csharp
[ObservableProperty] private bool _showWindowActions = true;
```

Gating: both the View array (`ToolbarWindow.xaml.cs:39-46`) and the VM array (`ToolbarViewModel.cs:66-76`) skip the three window buttons when `!settings.Toolbar.ShowWindowActions`. (The arrays are built in `Initialize`/the constructor, both of which have settings access.) Default `true` so existing users get the region; users who don't want it turn it off.

**Settings UI:** there's no per-button toolbar UI today (the settings page offers only toolbar Reset, `SettingsWindow.xaml.cs`). A checkbox for "Show window actions" in the toolbar section is the minimal addition; out of scope to spec the XAML in detail, but the gate is the load-bearing part.

## §6 Toolbar-disabled case

If a user disables the toolbar (or runs with `IsDocked=false`, where auto-hide is disabled), the window region has no home. **Decision: document as a known limitation for v1** — the window actions require the docked toolbar enabled. Rationale: building a fallback surface (a standalone corner bar — the abandoned B1) would re-introduce a second persistent overlay and a second summon gesture, which B2 was chosen to avoid. If a user disables the toolbar, they lose window management until they re-enable it; that's an acceptable trade for v1 and worth a release note.

(Future: if demand appears, the standalone corner bar B1 can be added as a fallback that shares the same `WindowActionRequestedMessage` and `IWindowManager` ops — the message + ops are surface-agnostic.)

## §7 Components & seams

| Change | File | Notes |
|---|---|---|
| 3 toolbar buttons (View) | `src/EyeCtrl.App/Overlays/ToolbarWindow.xaml.cs:39-46` | Add `("Minimize","▁")`, `("MaximizeRestore","□")`, `("Close","✕")` to the `(Id,Symbol)` array; gate on `ShowWindowActions`. |
| 3 toolbar buttons (VM) | `src/EyeCtrl.ViewModels/ToolbarViewModel.cs:66-76` | Matching array entries; gate on `ShowWindowActions`. |
| Dispatch cases | `src/EyeCtrl.ViewModels/ToolbarViewModel.cs:131-144` | 3 new `case`s sending `WindowActionRequestedMessage`. |
| New message + enum | `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs`, `src/EyeCtrl.Core/Models/` | `WindowActionRequestedMessage(WindowAction)`, `WindowAction { Minimize, MaximizeRestore, Close }`. |
| Recipient | `src/EyeCtrl.App/Services/EventCoordinator.cs` | `IRecipient<WindowActionRequestedMessage>` → `HandleWindowAction` (GetForegroundWindow + §2 guard + switch on action). |
| Ops | `src/EyeCtrl.Core/Interfaces/IWindowManager.cs`, `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | `GetForegroundWindow()`, `Minimize(hwnd)`, `MaximizeOrRestore(hwnd)`, `CloseWindow(hwnd)`. Extends the interface that already has `BringToFront(IntPtr)` — same handle vocabulary. |
| Pure toggle | `src/EyeCtrl.Core/Models/WindowStateOp.cs` (new) | `DecideWindowOp(bool,bool) → WindowStateOp`. No Win32. |
| Shell/self guard | `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` (or a pure helper) | `GetShellWindow` + class denylist + own-process. Factor as a pure predicate over (hwnd, shellHwnd, className, processId) for unit testing. |
| New P/Invoke | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | `GetForegroundWindow`, `GetShellWindow`, `GetClassName`, `ShowWindow` (+`SW_MINIMIZE`/`SW_MAXIMIZE`/`SW_RESTORE`), `PostMessage` (+`WM_CLOSE=0x0010`), `IsZoomed`, `IsIconic`. All `[LibraryImport]`. (Verified absent today.) |
| Settings toggle | `src/EyeCtrl.Core/Settings/ToolbarSettings.cs` | `ShowWindowActions` (default true). |

### DI / blast radius
`IWindowManager` is a DI singleton (`AppBootstrapper.cs:74`); the Task Switcher is its current sole caller alongside this feature — re-verify it. `EventCoordinator` already holds `_context.WindowManager` (it's how `HandleTaskSwitcherSelection` calls `BringToFront`). Far smaller blast radius than the abandoned sub-menu design: no `InteractionManager` changes, no `InteractionState` changes, no `InteractionAction` enum changes, no `ActionMenuViewModel` changes, no `GazeEngineAdapter` changes.

## §8 Edge cases & safety

- **Foreground is the shell/desktop:** `GetForegroundWindow` returns `Progman`/`Shell_TrayWnd` when the desktop/taskbar is focused → §2 guard rejects → no-op + log.
- **Foreground is EyeCtrl:** EyeCtrl windows are `WS_EX_NOACTIVATE` (they never take focus), so `GetForegroundWindow` should not return them — but the own-process guard is a second defense.
- **Close on unsaved work:** `WM_CLOSE` is graceful; the app's "save changes?" prompt fires. Exceptions: `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close. Documented caveats.
- **Maximize/Restore on a fullscreen (F11) window:** `IsZoomed`=false, so toggle would `SW_MAXIMIZE` a borderless-fullscreen window and clamp it to the work area. **Acceptable for v1** (the fullscreen guard from the abandoned spec required `MonitorFromWindow` + `GetMonitorInfo`, adding complexity; deferred — flag as a known limitation). The risk is lower here than in the sub-menu: the user explicitly summoned the toolbar and dwelt the toggle, so it isn't an accidental fire.
- **Elevated foreground window:** `PostMessage` UIPI-blocked → `false` → logged. EyeCtrl is not elevated, so it cannot minimize/close elevated apps — expected, documented.
- **Already-minimized foreground:** not reachable via `GetForegroundWindow` (a minimized window isn't foreground), so `IsIconic` arm of `DecideWindowOp` is mostly defense-in-depth. Kept for completeness.
- **Toolbar disabled/undocked:** window region has no home → §6 limitation.

## §9 Testing

Unit tests (NFluent, no display, private nested fakes):
- **`DecideWindowOp`** (pure): all four `(isZoomed, isIconic)` combinations.
- **Shell/self guard predicate** (pure, over `(hwnd, shellHwnd, className, processId)`): accepts a normal app window; rejects `Progman`/`WorkerW`/`Shell_TrayWnd`/`Shell_SecondaryTrayWnd`; rejects own-process; rejects `hwnd == shellHwnd`.
- **`HandleWindowAction` dispatch** (with a fake `IWindowManager` recording calls): Minimize/MaximizeRestore/Close each route to the right op on the foreground HWND; a rejected (shell) foreground is a no-op + logged; a UIPI-blocked Close logs.
- **Settings:** `ShowWindowActions` defaults true; the View/VM arrays exclude the three buttons when false.

UI tests (`Category=UI`): not gating — the Win32 ops need a real foreground window; cover via unit-level fakes + a manual smoke note.

## §10 Out of scope / future

- **Show Desktop** (global Win+D): a different *kind* of action (global, not per-foreground-window). Its own future toolbar button, sharing `WindowActionRequestedMessage`/`WindowAction` (add a `ShowDesktop` enum member) + a `Win32InputSimulator.SendModifiedKeyPress` path.
- **Window-picker grid:** reuses `IWindowManager` ops; targeting would need `EnumerateOpenWindows` (already there) — separate feature.
- **Standalone corner bar fallback (B1)** when the toolbar is disabled: shares `WindowActionRequestedMessage` + ops.
- **Fullscreen (F11) guard:** add `MonitorFromWindow`+`GetMonitorInfo` if fullscreen-toggle wedging is reported.

## §11 Why this design converges where the sub-menu didn't

For reviewers familiar with the 5-round postmortem: the table below maps each recurring failure of the sub-menu design to why B2 does not have it. This is the "does the new shape actually avoid the old failures?" check.

| Sub-menu failure (rounds 1-5) | B2 status |
|---|---|
| `WindowFromPoint` self-hits EyeCtrl's own menu (rounds 1, 3) | N/A — no `WindowFromPoint`; `GetForegroundWindow` can't return `WS_EX_NOACTIVATE` EyeCtrl windows |
| Stash race (`ResetToFreeLook` clears before dispatch reads, round 4-5) | N/A — no stash; `GetForegroundWindow` read synchronously at op-fire |
| `Progman` passes filters → destructive Explorer close (round 2) | Covered — `GetShellWindow`+class denylist guard on the single foreground HWND |
| `EnumWindows` Z-order undocumented (round 2) | N/A — no enumeration |
| `GetWindowRect` invisible borders (round 2) | N/A — no rect hit-test |
| `GA_ROOT` vs `GA_ROOTOWNER` (rounds 1, 3, 4) | N/A — foreground is by definition top-level unowned |
| No content route (`ShowActionMenu` can't carry a list, round 4) | N/A — toolbar buttons are a static array; dispatch is a `switch` on id |
| `Receive` loops / `IsSubMenu` (round 5) | N/A — no menu levels |
| `WindowMenuVisible` arms missing in `OnActionSelected`/dismiss/backdrop/TogglePause (rounds 4-5) | N/A — no new `InteractionState` |
| `showBack` derives false (round 4) | N/A — no Back button concept |
| `PendingMenuActions` leaks through `TogglePause` (round 5) | N/A — no `PendingMenuActions` |
| Ring-position / Close-at-gaze-rest gaze-safety (round 5) | Mitigated — Close at a stable known toolbar position; toolbar auto-hides when not in use |
| `ExternalDwellService` equality-gate regression (round 4) | N/A — no consolidation; B2 doesn't touch `ExternalDwellService` |

The contract surface is: 1 new message, 1 new enum, 3 new toolbar-button array entries (×2 arrays), 3 new dispatch cases, 1 new recipient, 4 new `IWindowManager` methods, 7 new P/Invokes, 1 settings field. Compare the abandoned sub-menu design's ~15 contract changes across `InteractionManager`, `InteractionState`, `ActionMenuViewModel`, `ActionMenuItemViewModel`, `ActionRequestedMessage`, `ShowActionMenuMessage`, `GazeEngineAdapter`, `EventCoordinator`, and two new message types.

## §12 Change log
- **2026-08-05:** initial B2 design. Supersedes the sub-menu spec (`2026-08-04-window-management-actions-design.md`, rounds 1-5 — see `docs/superpowers/POSTMORTEM-window-mgmt-spec-rounds-1-5.md`). Decisions: no-confirm Close (matches toolbar); single MaxRestore toggle; scope guards = settings toggle + shell/self guard + toolbar-disabled handling + defer Show Desktop. Verified the integration map against current code (toolbar button arrays, dispatch switch, auto-hide rect-only, no existing `GetForegroundWindow`).
