# Design: Window-management actions (gap #2) — toolbar region (B2)

**Date:** 2026-08-05 · **Status:** Design (pre-implementation)
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2
**Supersedes:** `2026-08-04-window-management-actions-design.md` (the sub-menu approach, which went 5 review rounds without converging — see `docs/superpowers/POSTMORTEM-window-mgmt-spec-rounds-1-5.md`).

## §0 Context & decision

Add **Minimize**, **Maximize/Restore** (toggle), and **Close** for the foreground window, as a region on the **existing auto-hiding toolbar**. Both OptiKey and TD Control ship window management; app-*switching* already landed in EyeCtrl (`TaskSwitcherWindow`); per-window *management actions* are the remaining open slice of gap #2.

**Why a toolbar region, not the action menu (the decision that resolved the 5-round impasse):** the action menu is for *point-actions* ("do something at this control/coordinate" — click, drag, scroll here). Window management is a *window-level* operation that has nothing to do with which control gaze is on. Forcing window ops into the point-action ring created a category mismatch that surfaced as an unbounded series of contract gaps across 5 review rounds (resolver self-hit, stash race, missing `WindowMenuVisible` arms, `TogglePause` leak, …). Moving the actions to the toolbar resolves the mismatch *and* removes the point-resolution logic from this route: the toolbar targets **the foreground window** (`GetForegroundWindow`), so there is no `WindowFromPoint`, no self-hit, no `GA_ROOTOWNER`, no stash race, no occlusion degradation. *(Shell/self/owned-dialog cases still need handling — see §2. `WS_EX_NOACTIVATE` is not relied on as a safety guarantee; own-process rejection is mandatory, §8.)*

**Why the toolbar specifically (B2, not a standalone corner bar):** EyeCtrl already ships a `ToolbarAutoHideController` (hide on gaze-leave, summon by looking past the docked edge) and a persistent `ToolbarWindow`. A window-actions region rides the toolbar's auto-hide for free (the controller is rect-only — `ToolbarAutoHideController.cs:116,159` — so adding buttons auto-widens the cached rect with zero controller change). No new overlay surface, one summon gesture, two existing systems reused.

## §1 Goal & scope

Add three gaze-dwellable buttons — Minimize `─`, Maximize/Restore `☐`, Close `✕` — to the toolbar, acting on the foreground window via three new `IWindowManager` operations.

**In scope:** 3 new toolbar buttons (View array + VM array + VM dispatch cases); 1 new message + 1 recipient in `EventCoordinator`; 3 new `IWindowManager` ops (`Minimize`/`MaximizeOrRestore`/`CloseWindow`) backed by new P/Invokes; a `ToolbarSettings.ShowWindowActions` toggle (default true); a shell/self-window guard; handling for the toolbar-disabled case.

**Out of scope:** Show Desktop (global Win+D — a different *kind* of action, not per-foreground-window; deferred to its own future toolbar button), a window-picker grid, multi-monitor cursor handling (gap #7), the action-menu sub-menu (abandoned — see §0 + postmortem).

## §2 Targeting — the foreground window *(v2: fail-closed eligibility API, synchronous read)*

`GetForegroundWindow()` (new P/Invoke, absent today — verified) returns the HWND of the active foreground window — **but it can return `IntPtr.Zero`** (during activation transitions), shell windows, or an EyeCtrl window (Calibration/Note editing make themselves focusable, `OverlayWindow.cs:44-48` applies `WS_EX_NOACTIVATE` only when `focusable:false`). Exposing the raw HWND across the layer boundary invites `PostMessage(IntPtr.Zero, WM_CLOSE, …)`, which posts a **thread message** to the caller's queue rather than targeting a window — a real footgun.

**v2 contract (BLOCKER C.1 fix):** expose a single fail-closed method on `IWindowManager`, implemented by `Win32WindowManager`, returning `null` for anything not safe to operate on:

```csharp
// IWindowManager
IntPtr? TryGetEligibleForegroundWindow();
```

Implemented as one synchronous call chain (no stash, no async hop — BLOCKER A.1 fix in §4):
1. `var hwnd = GetForegroundWindow();` → fail closed if `IntPtr.Zero`.
2. `IsWindow(hwnd)` → fail closed if false (best-effort; a foreign HWND can still vanish/recycle after the check — not race-proof, but catches the common stale case).
3. Reject if `hwnd == GetShellWindow()` (definitive shell-desktop HWND).
4. Reject if `GetClassName(hwnd)` ∈ `{Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd}` (`WorkerW` is *not* returned by `GetShellWindow` — it layers over Progman under animated wallpaper; the denylist carries it).
5. Reject if `GetWindowThreadProcessId(hwnd) == Environment.ProcessId` (own-process — **mandatory**, not defense-in-depth: `WS_EX_NOACTIVATE` is not a valid safety guarantee since some EyeCtrl windows opt into focus).

`EventCoordinator.HandleWindowAction` calls this and no-ops + logs at `Information` on null. The eligibility decision factors as a pure predicate over `(hwnd, shellHwnd, className, processId)` for unit testing; the P/Invoke shell stays untested.

**Cloaked/visible NOT added.** `GetForegroundWindow` is a different source than the abandoned `EnumWindows` path — there's no evidence it returns hidden/cloaked windows (a hidden window cannot be foreground). Do not import the prior enumeration filters without evidence. Add a manual smoke matrix instead (§9): desktop, taskbar, Start/shell frames, UWP, IME states.

**Owned-dialog policy (MAJOR A.3 — explicit, not assumed):** the foreground is **not** "by definition top-level unowned" — an owned modal dialog/message box can be the active foreground UI. **B2 operates on the returned dialog HWND, never silently resolving to its owner.** So Close on a modal dialog closes/cancels the *dialog* (behaves like its X / Esc), not the owning application; Minimize on an owned dialog may yield the legacy minimize-to-desktop-title-bar behavior (acceptable, documented). This is the deliberate inverse of the abandoned sub-menu's `GA_ROOTOWNER` policy, and it avoids "close the dialog → unexpectedly closed the whole app."

## §3 Action set *(v2: DecideWindowOp in a class, truth table fixed)*

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** (single toggle) → pure `DecideWindowOp(bool isZoomed, bool isIconic)` then the matching `SW_*`:
  - `!isZoomed && !isIconic` → Maximize (`SW_MAXIMIZE`)
  - `isZoomed && !isIconic` → Restore (`SW_RESTORE`)
  - `!isZoomed && isIconic` → Restore (un-minimize)
  - `isZoomed && isIconic` → Restore (minimized-from-maximized → un-minimize; `IsIconic` first) *(v2: was a duplicate of row 2 — corrected)*
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful; `DefWindowProc` derives from `SC_CLOSE`; the app's own "save changes?" prompt fires). Only called when `hwnd != IntPtr.Zero` (§2 fail-closed).

**Close behavior — no confirm, conditional on the target-lifecycle fix (§4).** No-confirm matches every other toolbar button (Pause/Settings/TaskSwitcher fire on dwell, no confirms). The argument that "stable position + auto-hide makes accidental fire unlikely" **only holds if the hidden toolbar cannot dwell** — round-1 review proved it can today (`ToolbarViewModel.Receive` drops the base-class `IsVisible` gate, `:92-95`; `ToolbarViewModelTests.cs:144-160` activates with `IsVisible` never set true). **v2 makes no-confirm conditional on the §4 target-lifecycle contract being implemented:** hide must deactivate dwell, so a hidden toolbar cannot fire Close. `WM_CLOSE` remains the backstop (preserves the app's save-changes prompt). **Caveats:** `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close; Close on a modal dialog closes the dialog, not its owner (§2).

## §3 Action set

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
### Op return semantics

- `PostMessage` returns `bool` — UIPI-blocked sends to elevated windows return `false`; that *is* a meaningful failure → **log at `Information`**.
- `ShowWindow` return does **not** mean success/failure (zero = "was previously hidden") → **do not interpret as failure**. After the §2 eligibility guard, call fire-and-forget.
- `Minimize`/`MaximizeOrRestore` return `void`; `CloseWindow` returns the meaningful `PostMessage` result.

### `DecideWindowOp` — pure, unit-testable *(v2: in a static class; table corrected)*

In `EyeCtrl.Core` (no Win32 reference) — **inside a named static class**, not namespace scope (round-1 MAJOR B.2: the v1 snippet was invalid C#):

```csharp
namespace EyeCtrl.Core.Models;

public enum WindowStateOp { Maximize, Restore }

public static class WindowStateOps
{
    public static WindowStateOp DecideWindowOp(bool isZoomed, bool isIconic)
        => (isZoomed, isIconic) switch
        {
            (_, true)  => WindowStateOp.Restore,  // minimized → un-minimize first
            (true, _)  => WindowStateOp.Restore,  // maximized → restore
            _          => WindowStateOp.Maximize, // normal → maximize
        };
}
```

`Win32WindowManager` reads `IsZoomed`+`IsIconic`, calls `DecideWindowOp`, maps to `SW_*`, calls `ShowWindow`. All four combinations are covered (round-1 fixed the v1 truth-table row 4, which had duplicated `isZoomed && !isIconic` instead of `isZoomed && isIconic`); `(true, true)` (minimized-from-maximized) hits the first arm → Restore, which is correct.

## §4 Placement — toolbar region (B2)

The three buttons render as a region at the end of the toolbar's `ButtonPanel`. They reuse the toolbar's existing button construction, dwell machinery, and auto-hide — no new overlay, no new dwell target type, no new menu state.

### Where the buttons plug in (verified against current code)

Two arrays must list the new button ids (the toolbar has no settings-driven `ItemsSource`; the set is a literal in two places):
- **View:** `ToolbarWindow.xaml.cs:39-46` — the hardcoded `(Id, Symbol)` array that drives rendering. Add `("Minimize", "▁")`, `("MaximizeRestore", "□")`, `("Close", "✕")`.
- **ViewModel:** `ToolbarViewModel.cs:66-76` — the matching array that creates dwell targets. Same three entries.

(Note: these two arrays are *already out of sync* today — View has 5 buttons, VM has 8. Pre-existing tech debt, unrelated to this feature. The new entries go in both, consistent with how the existing buttons are split.)

**Glyphs are BMP** (`▁` U+2581, `□` U+25A1, `✕` U+2715) — consistent with the existing toolbar glyphs and avoiding the Supplementary-Plane tofu risk flagged in prior rounds.

### Dispatch — synchronous, mirroring Task Switcher *(v2: no BeginInvoke — BLOCKER A.1)*

The toolbar's dispatch is a `switch` in `ToolbarViewModel.OnTargetActivated` (`ToolbarViewModel.cs:131-144`) sending one message per button id, received in `EventCoordinator`. The pattern mirrors `TaskSwitcherSelectionMessage(hwnd)` (`TaskSwitcherViewModel.cs:87-95` → `EventCoordinator.cs:135-141` → `HandleTaskSwitcherSelection` → `IWindowManager.BringToFront(hwnd)`) — **with one critical difference**: Task Switcher's message *carries* the selected HWND (captured at dwell time, no later re-query); `WindowActionRequestedMessage` carries only the action, so the foreground HWND is queried **inside `HandleWindowAction`**. That query must be synchronous with the dwell completion, or a foreground change in the gap can make Close affect a different window than the one present when the user dwelt.

**v2 fix (BLOCKER A.1): drop the `BeginInvoke`.** The toolbar dwell already runs on the UI dispatcher (`GazeEngineAdapter.cs:99-100` posts `GazeCursorMovedMessage` via `BeginInvoke` → `ToolbarViewModel.Receive` → `DwellDetector.Update` → `OnTargetActivated` → `Messenger.Send`). Messenger delivery is synchronous, so the recipient runs on the **same UI thread, same tick** as the dwell activation. The v1 spec's `_context.Dispatcher.BeginInvoke(() => HandleWindowAction(...))` added a queued hop for no reason (cargo-culted from Task Switcher, which doesn't need it either but is harmless there because its HWND is already on the message). v2 calls `HandleWindowAction(msg.Action)` **directly** from the recipient — query + validate + operate in one synchronous callback, no foreground-change window.

```csharp
_context.Messenger.Register<EventCoordinator, WindowActionRequestedMessage>(this, (_, msg) =>
    HandleWindowAction(msg.Action));   // synchronous — already on UI thread
```

`HandleWindowAction`:
1. `var hwnd = _context.WindowManager.TryGetEligibleForegroundWindow();` (§2 — fail-closed, returns `IntPtr?`)
2. If `null`, log + return (foreground was shell/self/zero/invalid).
3. `switch (msg.Action)` → `_context.WindowManager.Minimize(hwnd.Value)` / `MaximizeOrRestore(hwnd.Value)` / `CloseWindow(hwnd.Value)`.
4. Log the result at `Information` (per §3 return semantics).

**Regression test (round-1 ask):** drive a dwell to completion, swap the foreground during the (now-zero-length) gap, assert the op hits the original foreground — i.e. the synchronous recipient observes pre-change state. With the `BeginInvoke` removed this is trivially satisfied; the test pins it.

**One new message + a discriminating enum** (one message, not three — the action is the payload):

```csharp
// EyeCtrl.ViewModels.Messages
public record WindowActionRequestedMessage(WindowAction Action);
// EyeCtrl.Core.Models — PINNED (round-1 D NIT: resolves the dangling "or ViewModels")
public enum WindowAction { Minimize, MaximizeRestore, Close }
```

Three new cases in `OnTargetActivated`:

```csharp
case "Minimize":        Messenger.Send(new WindowActionRequestedMessage(WindowAction.Minimize)); break;
case "MaximizeRestore": Messenger.Send(new WindowActionRequestedMessage(WindowAction.MaximizeRestore)); break;
case "Close":           Messenger.Send(new WindowActionRequestedMessage(WindowAction.Close)); break;
```

### Target-lifecycle contract — hide deactivates dwell *(v2: BLOCKER C.2)*

Round 1 proved the hidden toolbar can still fire dwell: `ToolbarViewModel.Receive` **overrides** the base class and drops the `IsVisible` gate (`:92-95` calls `DwellDetector.Update` unconditionally; the base `DwellOverlayViewModelBase.Receive` gates on `IsVisible`, `:33-37`). `ToolbarViewModelTests.cs:144-160` confirms activation with `IsVisible` never set true. After auto-hide hides the WPF window (`ToolbarVisibilityAdapter.HideToolbar` only calls `Toolbar.Hide()`, not touching dwell targets), the targets persist at their former screen positions — so a user whose gaze passes where Close used to be could close the current foreground window **without the toolbar being visible**. This deletes the "auto-hide makes accidental fire unlikely" argument that no-confirm Close rests on (§3).

**v2 fix:** add a target-lifecycle contract so dwell tracks visibility:

- **Hide:** `ToolbarVisibilityAdapter.HideToolbar` (or the controller's `HideToolbar` call) must set the toolbar VM inactive and **reset detector state** (`DwellDetector.ClearTargets()` or a new `SetActive(false)` that stops feeding samples). Targets must not fire while the window is hidden.
- **Show:** `ShowToolbar` must reactivate the VM **after** the WPF window is shown, and re-collect fresh layout positions (targets are recreated from `UpdateButtonScreenPosition` reports, which fire after layout — `ToolbarWindow.xaml.cs:179-197`).

This is a toolbar-wide behavior change (it affects *all* toolbar buttons, not just the new ones — today a hidden toolbar can fire Pause/Settings/etc. too). That's arguably a pre-existing bug worth fixing as part of this feature; if scoped narrowly to the window-actions buttons only, state it. **Either way, the §3 no-confirm Close argument is conditional on this contract being implemented** — flag it as a blocker for the Close-without-confirm decision.

**Test (round-1 ask):** dwell at the former Close position while the toolbar is hidden → no activation. Plus: hide mid-dwell → progress resets; show → fresh dwell required.

### Auto-hide — rect-only, no controller change (unchanged)

`ToolbarAutoHideController` is rect-only (`:116,159`); adding buttons auto-widens the cached rect (toolbar is `SizeToContent`), picked up via the existing `RefreshToolbarRect`/`CaptureToolbarRect` calls. The window region hides and summons together with the rest of the toolbar. Zero controller change. (The target-lifecycle contract above is in the VM/adapter, not the controller.)

## §5 Settings — `ShowWindowActions` toggle

`ToolbarSettings` (`src/EyeCtrl.Core/Settings/ToolbarSettings.cs`) currently has only `IsDocked`, `DockedEdge`, `Size`. Add:

```csharp
[ObservableProperty] private bool _showWindowActions = true;
```

Gating: both the View array (`ToolbarWindow.xaml.cs:39-46`) and the VM array (`ToolbarViewModel.cs:66-76`) skip the three window buttons when `!settings.Toolbar.ShowWindowActions`. (The arrays are built in `Initialize`/the constructor, both of which have settings access.) Default `true` so existing users get the region; users who don't want it turn it off.

**Settings UI:** there's no per-button toolbar UI today (the settings page offers only toolbar Reset, `SettingsWindow.xaml.cs`). A checkbox for "Show window actions" in the toolbar section is the minimal addition; out of scope to spec the XAML in detail, but the gate is the load-bearing part.

## §5 Settings — `ShowWindowActions` toggle *(v2: live-reconciliation contract — MAJOR B.1)*

`ToolbarSettings` (`src/EyeCtrl.Core/Settings/ToolbarSettings.cs`) currently has only `IsDocked`, `DockedEdge`, `Size`. Add:

```csharp
[ObservableProperty] private bool _showWindowActions = true;
```

**v1 said "gate the two static arrays on the bool" — that is not implementable** (round-1 MAJOR B.1): `ToolbarWindow.Initialize(double buttonSize, int dwellTimeMs)` (`:31-53`) takes **no settings**; its callers pass only size and dwell (`OverlayManager.cs:105-109`, `EventCoordinator.cs:1187-1201`). `ToolbarViewModel` is a singleton built once in its constructor (`AppBootstrapper.cs:175-180`), where it creates its IDs, lookup entries, and dwell-target eligibility (`:60-90`). There is no live-settings path that rebuilds the toolbar for a new `ShowWindowActions` value: `ToolbarSnapshot` (`SettingsSnapshots.cs:40-43`) omits the flag, and clearing visuals doesn't remove VM targets (`ToolbarWindow.xaml.cs:34-36` clears `_buttonVisuals`/`ButtonPanel.Children`; `ToolbarViewModel.cs:41-57` keeps `_buttonLookup`/dwell targets until explicitly removed).

**v2 contract (the live-reconciliation):**
1. Add `ShowWindowActions` to `ToolbarSnapshot` (so the live-settings route picks up changes).
2. Pass the flag into `ToolbarWindow.Initialize` (widen its signature, or read from a settings snapshot the View already holds) — gate the View `(Id,Symbol)` array on it.
3. In `ToolbarViewModel`, add/remove the three VM buttons, their `_buttonLookup` entries, their positioned IDs, and their `DwellTarget`s **atomically** when the flag changes (not just at construction). Reset dwell state on removal; bind newly created buttons on addition.
4. Re-run positioning after the next layout pass (`UpdateTargetPositions`, `ToolbarWindow.xaml.cs:179-197`) so new buttons get fresh screen positions before becoming dwellable.
5. Settings-UI: a checkbox in the toolbar section + the Reset copy path (`SettingsWindow.xaml.cs:730-764`).

**For the startup-only happy path** (the common case — flag is default `true`, never toggled), the two array additions are sufficient: the View reports rendered IDs, the VM lazily creates targets from those reports. The reconciliation machinery matters only when the user toggles the flag at runtime.

## §6 Toolbar-disabled case *(v2: corrected — MAJOR C.3)*

v1 described a "toolbar disabled" state that doesn't exist: `ToolbarSettings` has **no enabled flag** (only `IsDocked`, `DockedEdge`, `Size`). The actual behaviors:
- `IsDocked=false`: auto-hide is disabled, but the toolbar is **force-shown floating** (`ToolbarAutoHideController.cs:125-140` `UpdateEnabled` derives enabled = `IsDocked && realTracker`). The window region stays visible.
- The toolbar is always created and shown at startup (`OverlayManager.cs:57-63,105-109`; `App.xaml.cs:455-460`).

So the window-actions region is **always available** as long as the toolbar exists. There is no "disabled toolbar" case to handle in v1. **Revised limitation:** if a future change adds a real toolbar-enabled setting (or a way to hide the toolbar entirely), the window region loses its home and the §5 reconciliation must remove the three targets. Document that dependency in the toolbar-enabled work, not here.

## §7 Components & seams *(v2)*

| Change | File | Notes |
|---|---|---|
| 3 toolbar buttons (View) | `ToolbarWindow.xaml.cs:39-46` | Add `("Minimize","▁")`, `("MaximizeRestore","□")`, `("Close","✕")` to `(Id,Symbol)`; **`Initialize` widened to take `ShowWindowActions`** (§5). |
| 3 toolbar buttons (VM) | `ToolbarViewModel.cs:66-76` | Matching entries + **atomic add/remove on flag toggle** (§5 reconciliation). |
| Target-lifecycle | `ToolbarViewModel.cs:92-95`, `ToolbarVisibilityAdapter.cs:27-38` | **Hide deactivates dwell (ClearTargets/SetActive(false)); show reactivates after layout** (§4 BLOCKER C.2). |
| Dispatch cases | `ToolbarViewModel.cs:131-144` | 3 new `case`s sending `WindowActionRequestedMessage`. |
| Message + enum | `InteractionMessages.cs`, `EyeCtrl.Core/Models/WindowAction.cs` (new) | `WindowActionRequestedMessage(WindowAction)`; `WindowAction { Minimize, MaximizeRestore, Close }` — **pinned to Core.Models** (round-1 D NIT). |
| Recipient | `EventCoordinator.cs` | Messenger registration → `HandleWindowAction` **directly (no BeginInvoke)** (§4 BLOCKER A.1). |
| Eligibility + ops | `IWindowManager.cs`, `Win32WindowManager.cs` | `IntPtr? TryGetEligibleForegroundWindow()` (§2 fail-closed), `Minimize`, `MaximizeOrRestore`, `CloseWindow`. Extends `BringToFront`'s interface. |
| Pure toggle | `EyeCtrl.Core/Models/WindowStateOps.cs` (new, **static class**) | `DecideWindowOp(bool,bool) → WindowStateOp` (§3). No Win32. |
| Pure eligibility predicate | `EyeCtrl.Core/Models/` (new) | Over `(hwnd, shellHwnd, className, processId, isWindow)` → bool. §9. |
| New P/Invoke | `NativeMethods.cs` | `GetForegroundWindow`, `GetShellWindow`, `GetClassNameW` (**`[Out] char[]`**, not StringBuilder — repo pattern `:102-107`), `ShowWindow` (+`SW_MINIMIZE=6`/`SW_MAXIMIZE=3`/`SW_RESTORE=9`), `PostMessageW` (**`SetLastError=true`**, correct int type), `IsZoomed`, `IsIconic`, **`IsWindow`** (round-1 BLOCKER C.1 omission). All `[LibraryImport]`. |
| Settings toggle | `ToolbarSettings.cs`, `SettingsSnapshots.cs:40-43` (`ToolbarSnapshot`) | `ShowWindowActions` (default true) + snapshot field for live-reconciliation. |

### DI / blast radius
`IWindowManager` DI singleton (`AppBootstrapper.cs:74`); Task Switcher + this feature are callers. **Round-1 MAJOR B.3: adding `IWindowManager` methods breaks `TaskSwitcherViewModelTests.FakeWindowManager` (`:123-132`) until it implements them — update the fake.** `EventCoordinator` already holds `_context.WindowManager`. No `InteractionManager`/`InteractionState`/`InteractionAction`/`ActionMenuViewModel`/`GazeEngineAdapter` changes.

## §8 Edge cases & safety *(v2)*

- **Foreground is the shell/desktop:** `GetForegroundWindow` returns `Progman`/`Shell_TrayWnd` when the desktop/taskbar is focused → §2 guard rejects → no-op + log.
- **Foreground is EyeCtrl (v2: corrected — MAJOR A.2):** `WS_EX_NOACTIVATE` is **not** a valid safety guarantee — `OverlayWindow` applies it only when `focusable:false` (`:44-48`), and Calibration (`CalibrationWindow.xaml.cs:92-102,144-148`) + Note editing (`NoteWindow.xaml.cs:166-196`) make themselves focusable and activate. MS docs confirm `WS_EX_NOACTIVATE` blocks activation by *user click*, not programmatic activation. **The own-process check (§2 item 5) is the real, mandatory protection** — not defense-in-depth.
- **Foreground is an owned modal dialog (v2: MAJOR A.3):** foreground is *not* "by definition top-level unowned." B2 operates on the returned dialog HWND directly (§2 owned-dialog policy): Close cancels/closes the *dialog*, not the owning app; Minimize on an owned dialog may yield legacy minimize-to-desktop behavior (documented).
- **`GetForegroundWindow` returns NULL (v2: BLOCKER C.1):** during activation transitions. §2's `TryGetEligibleForegroundWindow` fails closed; `PostMessage(NULL, WM_CLOSE)` never happens (it would post a thread message — the footgun the fail-closed API prevents).
- **Close on unsaved work:** `WM_CLOSE` graceful; app's "save changes?" prompt fires. Exceptions: `ConsoleWindowClass` terminates with no prompt; "minimize-to-tray-on-X" apps actually close.
- **Maximize/Restore on a fullscreen (F11) window:** `IsZoomed`=false → toggle would `SW_MAXIMIZE` a borderless-fullscreen window, clamping to work area. **v1-limitation, lower risk than the sub-menu** (explicit summon + dwell, not accidental). Round-1 reviewer agreed this is not a v1 blocker once the hidden-target issue is fixed. Keep the caveat; require a manual fullscreen smoke test.
- **Elevated foreground window:** `PostMessage` UIPI-blocked → `false` → logged. EyeCtrl isn't elevated; can't manage elevated apps — expected.
- **Already-minimized foreground:** not reachable via `GetForegroundWindow` (minimized ≠ foreground), so `IsIconic` arm is defense-in-depth. Kept.
- **Hidden toolbar fires dwell (v2: BLOCKER C.2):** the §4 target-lifecycle contract is the fix; without it, Close could fire from former button positions while the toolbar is invisible. Listed here as a safety case the contract must cover.

## §9 Testing *(v2)*

Unit tests (NFluent, no display, private nested fakes):
- **`DecideWindowOp`** (pure, in `WindowStateOps`): all four `(isZoomed, isIconic)` combinations — including `(true, true)` (the v1-duplicated row).
- **Eligibility predicate** (pure, over `(hwnd, shellHwnd, className, processId, isWindow)`): accepts a normal app window; rejects `Progman`/`WorkerW`/`Shell_TrayWnd`/`Shell_SecondaryTrayWnd`; rejects own-process; rejects `hwnd == shellHwnd`; **rejects `IntPtr.Zero`**; rejects `!isWindow`.
- **`HandleWindowAction` dispatch** (fake `IWindowManager` recording calls): Minimize/MaximizeRestore/Close each route to the right op on the eligible foreground HWND; a null foreground (shell/zero/own-process) is a no-op + logged; a UIPI-blocked Close logs.
- **Synchronous-recipient regression (round-1 BLOCKER A.1):** drive a dwell to completion, swap the foreground during the (zero-length) gap, assert the op hits the original foreground — pins the no-`BeginInvoke` design.
- **Target-lifecycle (round-1 BLOCKER C.2):** dwell at former Close position while toolbar hidden → no activation; hide mid-dwell → progress resets; show → fresh dwell required.
- **`ShowWindowActions` reconciliation (round-1 MAJOR B.1):** toggle false→true adds 3 targets + lookup + visuals atomically and re-positions; true→false removes them and clears dwell state; default is true.
- **`FakeWindowManager` update (round-1 MAJOR B.3):** `TaskSwitcherViewModelTests.FakeWindowManager` implements the 4 new `IWindowManager` methods (no-op/stub) so the existing Task Switcher tests still compile.
- **Drift tests (round-1 MAJOR B.3):** `ToolbarViewModelTests` (currently expects 8 logical buttons) + the UI test `AppLaunchTests` (asserts 4 rendered buttons) — update both for the +3 window buttons when `ShowWindowActions=true`.

Manual smoke matrix (round-1 ask, not unit-testable): desktop foreground, taskbar, Start/shell frames, UWP, IME states, modal dialog, fullscreen (F11) window, elevated app.

UI tests (`Category=UI`): not gating — Win32 ops need a real foreground window; cover via unit fakes + the smoke matrix.

## §10 Out of scope / future

- **Show Desktop** (global Win+D): a different *kind* of action (global, not per-foreground-window). Its own future toolbar button, sharing `WindowActionRequestedMessage`/`WindowAction` (add a `ShowDesktop` enum member) + a `Win32InputSimulator.SendModifiedKeyPress` path.
- **Window-picker grid:** reuses `IWindowManager` ops; targeting would need `EnumerateOpenWindows` (already there) — separate feature.
- **Standalone corner bar fallback (B1)** when the toolbar is disabled: shares `WindowActionRequestedMessage` + ops.
- **Fullscreen (F11) guard:** add `MonitorFromWindow`+`GetMonitorInfo` if fullscreen-toggle wedging is reported.

## §11 Why this design converges where the sub-menu didn't

For reviewers familiar with the 5-round postmortem: the table below maps each recurring failure of the sub-menu design to why B2 does not have it. This is the "does the new shape actually avoid the old failures?" check.

| Sub-menu failure (rounds 1-5) | B2 status |
|---|---|
| `WindowFromPoint` self-hits EyeCtrl's own menu (rounds 1, 3) | Avoided on B2's route — no `WindowFromPoint` on the window-action path. *(Caveat: `ExternalDwellService` still uses `WindowFromPoint`+`GA_ROOT` at `:329-340` — that's a separate surface, untouched. §11 is scoped to the window-action route.)* |
| Stash race (`ResetToFreeLook` clears before dispatch reads, round 4-5) | Avoided — no stash; `TryGetEligibleForegroundWindow` queried **synchronously** in the recipient, no `BeginInvoke` hop (§4 BLOCKER A.1 fix) |
| `Progman` passes filters → destructive Explorer close (round 2) | Covered — `GetShellWindow`+class denylist on the single foreground HWND (§2) |
| `EnumWindows` Z-order undocumented (round 2) | N/A on this route — no enumeration |
| `GetWindowRect` invisible borders (round 2) | N/A on this route — no rect hit-test |
| `GA_ROOT` vs `GA_ROOTOWNER` (rounds 1, 3, 4) | Different resolution — B2 operates on the foreground HWND directly; explicit per-op owned-dialog policy (§2/§8), not an owner walk |
| No content route (`ShowActionMenu` can't carry a list, round 4) | N/A — toolbar buttons are a static array; dispatch is a `switch` on id |
| `Receive` loops / `IsSubMenu` (round 5) | N/A — no menu levels |
| `WindowMenuVisible` arms missing in `OnActionSelected`/dismiss/backdrop/TogglePause (rounds 4-5) | N/A — no new `InteractionState` |
| `showBack` derives false (round 4) | N/A — no Back button concept |
| `PendingMenuActions` leaks through `TogglePause` (round 5) | N/A — no `PendingMenuActions` |
| Ring-position / Close-at-gaze-rest gaze-safety (round 5) | Mitigated — Close at a stable known toolbar position; **conditional on the §4 target-lifecycle contract** (hide deactivates dwell, so a hidden toolbar can't fire) |
| `ExternalDwellService` equality-gate regression (round 4) | N/A — no consolidation; B2 doesn't touch `ExternalDwellService` |

The contract surface is: 1 new message, 1 new enum, 3 new toolbar-button entries (×2 arrays + atomic reconciliation), 3 new dispatch cases, 1 synchronous recipient, 4 new `IWindowManager` methods, 8 new P/Invokes, 1 settings field + snapshot, 1 target-lifecycle contract. Still far smaller than the abandoned sub-menu's ~15 changes across `InteractionManager`/`InteractionState`/`ActionMenuViewModel`/`ActionRequestedMessage`/`ShowActionMenuMessage`/`GazeEngineAdapter`/`EventCoordinator` + two message types — and none touching `InteractionManager`.

## §12 Change log
- **2026-08-05 v2 (after B2 round-1 review, 3 BLOCKERs + 5 MAJORs):** all verified first-hand. BLOCKER A.1 (async hop before foreground read) → drop the `BeginInvoke`, call `HandleWindowAction` directly (toolbar dwell is already on the UI thread). BLOCKER C.1 (`GetForegroundWindow` NULL + `IsWindow` omitted + guard placement) → fail-closed `TryGetEligibleForegroundWindow` on `IWindowManager`, returns `IntPtr?`, includes zero/IsWindow/shell/class/own-process checks. BLOCKER C.2 (hidden toolbar still dwells — `ToolbarViewModel.Receive` drops the `IsVisible` gate) → target-lifecycle contract (hide deactivates/ClearTargets, show reactivates); **no-confirm Close is now explicitly conditional on this contract.** MAJOR A.2 (`WS_EX_NOACTIVATE` is not a valid safety guarantee — Calibration/Note opt into focus) → own-process is mandatory, not defense-in-depth. MAJOR A.3 (foreground can be an owned dialog) → explicit per-op policy: operate on the dialog, not its owner. MAJOR B.1 (`ShowWindowActions` not implementable by gating static arrays) → live-reconciliation contract via `ToolbarSnapshot` + atomic add/remove + re-position. MAJOR B.2 (`DecideWindowOp` at namespace scope is invalid C#; truth-table row 4 duplicated) → wrapped in `WindowStateOps` static class; row 4 corrected to `(true,true)`. MAJOR B.3 (adding `IWindowManager` methods breaks `FakeWindowManager` + drift tests) → §7/§9 list the fake update + `ToolbarViewModelTests`/`AppLaunchTests` updates. §6 corrected (no "toolbar disabled" state exists). §11 two rows reworded (NOACTIVATE claim, GA_ROOT row). NITs fixed: `WindowAction` pinned to Core.Models; glyphs unified to §4's (`▁`/`□`/`✕`); §0 scoped to "no point-resolution logic on this route"; "messenger registration" wording.
- **2026-08-05 v1:** initial B2 design. Supersedes the sub-menu spec (rounds 1-5 — see postmortem).

## §12 Change log
- **2026-08-05:** initial B2 design. Supersedes the sub-menu spec (`2026-08-04-window-management-actions-design.md`, rounds 1-5 — see `docs/superpowers/POSTMORTEM-window-mgmt-spec-rounds-1-5.md`). Decisions: no-confirm Close (matches toolbar); single MaxRestore toggle; scope guards = settings toggle + shell/self guard + toolbar-disabled handling + defer Show Desktop. Verified the integration map against current code (toolbar button arrays, dispatch switch, auto-hide rect-only, no existing `GetForegroundWindow`).
