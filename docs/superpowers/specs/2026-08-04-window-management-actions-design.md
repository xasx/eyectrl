# Design: Window-management actions (gap #2)

**Date:** 2026-08-04
**Status:** Design (pre-implementation) — **revision 3, after two review rounds**
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2 (OptiKey + Tobii Dynavox Computer Control both have it)

> **Revision history (§10):**
> - **v1** (initial) — `WindowFromPoint`+`GetAncestor(GA_ROOT)` resolver; `Window` as an `InteractionAction` member; a new `ShowWindowMenuMessage`.
> - **v2** (after review round 1, 5 BLOCKERs verified) — switched resolver to a z-ordered `EnumerateOpenWindows` hit-test; removed `Window` from the enum (model like Back); removed the new message type (re-send `ShowActionMenuMessage`); added `WindowMenuVisible` state.
> - **v3** (this revision, after review round 2, 6 BLOCKERs verified) — round 2 proved: `EnumerateOpenWindows` does NOT document Z-order (so v2's "first hit = topmost" is unsafe); `Progman` has title `"Program Manager"` and passes v2's filters (destructive-close risk reopens); a non-enum sub-menu entry has no activation route (`ShowActionMenuMessage.Actions` is `IReadOnlyList<InteractionAction>`, `OnTargetActivated` emits only Back/Action); `GetWindowRect` includes invisible borders. v3 returns to `WindowFromPoint` (the *documented* topmost-at-point primitive), resolves it **before** the sub-menu shows (fixing the v1 self-hit), consolidates the existing `ExternalDwellService.RootWindowAtPointImpl`, adds `GetShellWindow`+class denylist + DWM extended-frame bounds, and introduces a **first-class menu-item descriptor + a dedicated VM→App message** for the sub-menu (the opposite of v2's simplification — round 2 proved it's necessary).

## §0 Context & problem

The action menu offers click/scroll/drag/note actions only (`InteractionAction`: LeftClick, RightClick, DoubleClick, Drag, ScrollUp/Down/Left/Right, ScrollMode, PinNote — `src/EyeCtrl.Core/Models/InteractionAction.cs:4`). There is no way to **minimize, maximize/restore, or close the window under gaze** — a capability both OptiKey and TD Control ship. App-*switching* already landed (`TaskSwitcherWindow`); per-window *management actions* are the remaining open slice of gap #2.

Out of scope: Show Desktop (global Win+D), a window-picker grid (§8), multi-monitor cursor handling (gap #7), icon-tooltip localization.

## §1 Goal & scope

Add **Minimize**, **Maximize/Restore** (toggle), and **Close** behind a grouped "Window" sub-menu, acting on the top-level window under the resolved target point.

**In scope:** three new `InteractionAction` members (Minimize, MaximizeRestore, Close — appended after PinNote); a hardened topmost-window-at-point resolver; a first-class menu-item descriptor + message for the sub-menu; a `WindowMenuVisible` state with a retained return-state; re-show repaint; dispatch + icons + default visibility; unit tests; two enum-drift-test updates.

## §2 Resolver — `WindowFromPoint`, pre-show, hardened *(v3: replaces v2's EnumWindows hit-test)*

### Why not v2's EnumWindows hit-test

Round 2 verified two fatal flaws (both against Microsoft docs, not just the reviewer's claim):

1. **`EnumWindows` does not document Z-order** ([MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-user32-enumwindows) say only "enumerates … by passing the handle … to the callback"; [broad consensus](https://stackoverflow.com/questions/295996/is-the-order-in-which-handles-are-returned-by-enumwindows-meaningful) is the order is an implementation detail). v2's "first containing rect = topmost" could target an obscured window for Close. The documented Z-order method is `GetTopWindow`+`GetWindow(GW_HWNDNEXT)` — but see the better option below.
2. **`GetWindowRect` includes invisible 7-8px resize borders** ([MS `GetWindowRect` docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-useruser-getwindowrect) explicitly recommend `DWMWA_EXTENDED_FRAME_BOUNDS` for visible bounds). v2's hit-test would select a window whose invisible border crosses the point while gaze is visually over the window behind it.

### Adopted resolver: `WindowFromPoint` pre-show

`WindowFromPoint` returns the **topmost** HWND under a point by definition — no enumeration-order assumption. Two prior rounds worried it self-hits EyeCtrl's own non-click-through action menu; **v3 fixes that by resolving the HWND *before* the sub-menu is shown** (at the moment the user dwells the Window entry, while only the parent ring is up — and the parent ring's buttons are the dwell targets, not arbitrary points). The resolved HWND is stashed on the transition into `WindowMenuVisible`.

### Consolidate the existing helper (don't write a third copy)

`WindowFromPoint`+`GetAncestor(GA_ROOT)` is **already implemented twice** in this codebase:
- `ExternalDwellService.RootWindowAtPointImpl` (`src/EyeCtrl.App/Services/ExternalDwellService.cs:329-340`) — complete, including the `root == IntPtr.Zero ? hwnd : root` fallback.
- `ControlFinder.FallbackToWindow` (`src/EyeCtrl.Platform/Services/ControlFinder.cs:178-200`) — private `[DllImport]`.

v3 **consolidates** this into the new `IWindowManager.WindowAtPoint` seam in Platform (`NativeMethods`, `[LibraryImport]` style), and points `ExternalDwellService` at it (its `rootWindowAtPoint` is already an injectable delegate, `ExternalDwellService.cs:78` — trivially retargeted). `ControlFinder`'s private copy is left alone (out of scope; different fallback semantics).

### Hardening (round 2's destructive-close finding)

`EnumerateOpenWindows`' empty-title filter does **not** exclude the shell — `Progman`'s title is `"Program Manager"` (non-empty) and it passes every filter (visible, not tool-window, no owner, not cloaked, non-empty title). `PostMessage(WM_CLOSE)` to `Progman` restarts Explorer. Defense in depth, all applied *after* `WindowFromPoint` returns the HWND:

1. **`GetShellWindow()`** reject — the definitive shell-desktop HWND.
2. **Class-name denylist** — `Progman`, `WorkerW`, `Shell_TrayWnd`, `#32769` (SolidOS... background) — via `GetClassName`. Catches the cases where `GetShellWindow` and `WindowFromPoint` disagree (WorkerW is layered over Progman when wallpaper is animating).
3. **`WS_EX_TOOLWINDOW`** reject (catches EyeCtrl overlays if they ever win the `WindowFromPoint` race) — reuse the existing check.
4. **Own-process** reject (`NativeMethods` own-PID) — second guard.
5. **`IsWindow`** re-check at op-fire time (stale-HWND guard, §6).

### Hit-test bounds: DWM extended frame, fallback GetWindowRect

For any *rect-containment* check (the picker grid in §8; not needed for v3's `WindowFromPoint` path but the helper is added now), use `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT, ...)`; fall back to `GetWindowRect` when DWM returns a failure. The existing `DwmGetWindowAttribute` import is `out int` only (`NativeMethods.cs:131`) — add an `out RECT` overload. All coords stay physical screen space.

### API

```csharp
// IWindowManager / Win32WindowManager
AppWindow? WindowAtPoint(PhysicalPoint point);   // topmost eligible window, or null
bool Minimize(IntPtr hwnd);
bool MaximizeOrRestore(IntPtr hwnd);             // consumes DecideWindowOp
bool CloseWindow(IntPtr hwnd);                   // PostMessage WM_CLOSE
```

Returns `AppWindow?` (not `IntPtr`) — explicit "not found", reuses the existing model, gives §8's grid the title/process. **Note:** `AppWindow` currently holds only `Hwnd/Title/ProcessId/ProcessName` (`src/EyeCtrl.Core/Models/AppWindow.cs`) — the eligibility *decision* is factored as a pure predicate over a richer candidate record (§7), and a validated candidate is projected to `AppWindow` for output. The P/Invoke shell stays untested (consistent with `Win32WindowManager` today).

### New P/Invokes (all verified absent from `NativeMethods.cs`, 2026-08-04)

`WindowFromPoint`, `GetAncestor` (+`GA_ROOT`), `GetShellWindow`, `GetClassName`, `ShowWindow` (+`SW_*`), `PostMessage` (+`WM_CLOSE`), `IsZoomed`, `IsIconic`, `IsWindow`, `GetWindowRect` (+`RECT`), and a `DwmGetWindowAttribute` `out RECT` overload (+`DWMWA_EXTENDED_FRAME_BOUNDS`). `GetWindow` is already declared (`NativeMethods.cs:114`). Use `[LibraryImport]` (file style).

## §3 Action set

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** → pure `DecideWindowOp(IsZoomed, IsIconic)` then the matching `SW_*` (truth table verified correct by round 2):
  - `!isZoomed && !isIconic` → Maximize
  - `isZoomed && !isIconic` → Restore
  - `!isZoomed && isIconic` → Restore (un-minimize)
  - `isZoomed && isIconic` → Restore (minimized-from-maximized → un-minimize; `IsIconic` first)
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful; `DefWindowProc` derives this from `SC_CLOSE`)

### Op return semantics *(v3: corrected per round 2)*

- **`PostMessage`** returns `bool` — UIPI-blocked sends to elevated windows return `false`; that *is* a meaningful failure → **log at `Information`**.
- **`ShowWindow`** return does **not** mean success/failure (zero = "was previously hidden", per MS docs) → **do not interpret as failure**. After the `IsWindow` guard, call it fire-and-forget. If outcome confirmation is ever needed, read `IsZoomed`/`IsIconic` back — not the `ShowWindow` return.
- `Minimize`/`MaximizeOrRestore` therefore return `void` (or `bool` from the `IsWindow` guard only); only `CloseWindow` returns a meaningful `bool`.

### Close behavior — no confirm, conditional *(unchanged from v2)*

No confirm sub-step, consistent with LeftClick/RightClick/Drag; `WM_CLOSE` preserves the target app's "save changes?" prompt. **Defensible only because of two guards** (round 2's condition, both retained in v3): (1) the ring-position guard (§4), (2) the Back path (§4). **Caveats to document:** `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close under `WM_CLOSE`.

## §4 Placement — "Window" sub-menu, via a first-class descriptor *(v3: new plumbing)*

### The sub-menu entry needs a real activation route (round 2 BLOCKER)

Round 2 verified the v2 design has no plumbing for a non-enum entry: `ShowActionMenuMessage.Actions` is `IReadOnlyList<InteractionAction>` (`InteractionMessages.cs:21`), `ActionMenuItemViewModel` has only `Action`+`IsBackButton`, and `OnTargetActivated` emits only `BackSelectedMessage` or `ActionSelectedMessage` (`ActionMenuViewModel.cs:87-95`). A "Window" item with `Action==null` emits **neither** — it's a dead button. v2's "model it like Back" was wrong: Back doesn't flow through `Actions`, it's a separate `ShowBack` flag.

### v3: a first-class menu-item descriptor + a dedicated message

Introduce a descriptor that can carry either a real action or a sub-menu command:

```csharp
// EyeCtrl.Core.Models (Core — no layering issue; it's data)
public record ActionMenuDescriptor(string Id, InteractionAction? Action, MenuCommand? Command, string Symbol);

public enum MenuCommand { WindowSubMenu }   // extensible
```

The Window entry is `new("window", Action: null, Command: MenuCommand.WindowSubMenu, Symbol: "🗗" or similar)`. `ActionMenuViewModel.OnTargetActivated` gains a third branch: when `item.Command == MenuCommand.WindowSubMenu`, send the new message (below).

**New message** (ViewModels layer — the *existing* direction; `GazeEngineAdapter` translates it to a Core event, preserving B3 layering):

```csharp
// EyeCtrl.ViewModels.Messages
public record OpenWindowSubMenuMessage(PhysicalPoint Center, PhysicalPoint GazePoint);
```

`ShowActionMenuMessage.Actions` stays `IReadOnlyList<InteractionAction>` **unchanged** for back-compat — the descriptor list is a *new* `Actions` overload or a sibling message for the parent ring too (decide at impl; the parent ring can keep the enum list and only the Window entry is injected as a descriptor). The minimal change: `ActionMenuViewModel.Receive(ShowActionMenuMessage)` injects the Window descriptor into `Items` when settings say it's visible, without changing the message contract.

### New state — `WindowMenuVisible` with a retained return-state *(v3: round 2 MAJOR)*

Round 2: a lone `WindowMenuVisible` value can't tell Back whether to return to `ActionMenuVisible` or `ManualSelectionActionMenuVisible`. **Add the state *and* a retained `return-to` field** on `InteractionManager`:

```csharp
// InteractionManager
public InteractionState WindowMenuReturnState { get; private set; }  // set on WindowMenuVisible entry
```

Set it on entry; the new Back arm restores it; clear it on `ResetToFreeLook`. `OnManualSelectionBack` (`InteractionManager.cs:170-189`) gets a third arm (or a sibling `OnWindowMenuBack`).

### Consumer audit (round 2 MAJOR — enumerate every `CurrentState` consumer)

`WindowMenuVisible` must be handled in **all** of (verified list):
- `InteractionManager.OnActionSelected` target-point switch (`:130-142`) — add `WindowMenuVisible` arm.
- `InteractionManager.OnManualSelectionBack`/`OnWindowMenuBack` — the Back transition.
- `EventCoordinator` backdrop-dismiss handler (`:450-471`) — dismiss → free look.
- `GazeEngineAdapter` `ShowBack` + dwell-time derivation (`:108-115`) — sub-ring gets Back + the action-menu dwell.
- Pause/resume/`TogglePause` paths — same handling as the parent menu.

Consumers that need **no** new arm (verified): `CursorCircleViewModel` (FreeLook/Fixation-only display — correct to hide), `GazeEngine` (moves cursor only in FreeLook/Dragging — correct to not move), toolbar/heartbeat/adaptation/external-dwell (distinguish only `Paused`).

### Re-show must repaint *(v3: pick the hide→show sequence, not defer it)*

Round 2 (BLOCKER, re-raised): `ActionMenuWindow.Show` is driven **only** by the `IsVisible` `PropertyChanged` handler (`EventCoordinator.cs:1064-1098`); `Receive` writes `IsVisible = true` which is equality-gated. **v3 commits to the fix** rather than deferring: send an explicit `HideActionMenuMessage` → `OpenWindowSubMenuMessage` (or `ShowActionMenuMessage`) sequence. The hide flips `IsVisible=false` (fires `PropertyChanged` → `Hide()`+`HideBackdrop()`); the show flips it back to `true` (fires → `Show()` with the sub-ring list). This costs the backdrop hide/show + ~100 ms re-capture (`ActionMenuWindow.xaml.cs:92-96`) — acceptable for a deliberate sub-menu transition. **Add a regression test** asserting rendered item IDs and live dwell-target IDs change together.

Note a wrinkle verified in `OnActionMenuVMPropertyChanged` (`:1079`): it passes `_context.Settings.ActionMenu.VisibleActions` to `Show()`, **not** the VM's `Items`. So the show path is settings-driven, not message-driven. The sub-ring show must either read a sub-ring action list from the VM/state, or `ActionMenuWindow.Show` must be taught to read `Items`. State the choice.

### Ring-position guard for Close *(v3: corrected per round 2)*

Round 2 (BLOCKER): positions aren't determined by index alone — `angleStep = 360/itemCount`, angle `angleStep*i - 90°` (`ActionMenuViewModel.cs:47-70`), and parent/sub counts differ. "Close at a different index" ≠ "different physical position." **v3:** compute the parent Window button's actual screen position; choose/rotate the sub-ring so Close is farther than (dwell hit radius + safety margin) from it. Test across supported parent counts and Window positions.

## §5 Components & seams *(v3)*

| Change | File | Notes |
|---|---|---|
| New enum members | `src/EyeCtrl.Core/Models/InteractionAction.cs:4` | Append `Minimize`, `MaximizeRestore`, `Close` after `PinNote` (keeps `PinNote==9`). No `Window` member. |
| Menu descriptor + command | `src/EyeCtrl.Core/Models/` (new) | `ActionMenuDescriptor`, `MenuCommand` enum. Core data — no layering issue. |
| New state + return-state | `src/EyeCtrl.Core/Models/InteractionState.cs`, `src/EyeCtrl.Core/Processing/InteractionManager.cs` | `WindowMenuVisible` + `WindowMenuReturnState`; Back arm; consumer-audit arms (§4). |
| Resolver + ops | `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` + `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | `WindowAtPoint`, `Minimize/MaximizeOrRestore/CloseWindow`. Consolidate `ExternalDwellService.RootWindowAtPointImpl` (retarget its delegate, `:78`). |
| Pure toggle | `src/EyeCtrl.Core/Models/WindowStateOp.cs` (new) | `DecideWindowOp(bool isZoomed, bool isIconic)`. No Win32. |
| Pure eligibility predicate | `src/EyeCtrl.Core/Models/` (new) | Over a candidate record (hwnd/exStyle/owner/pid/title/cloaked/className/rect) → bool. §7. |
| New P/Invoke | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | See §2 list. `[LibraryImport]`. `DwmGetWindowAttribute` gains an `out RECT` overload. |
| Monitor API (fullscreen guard) | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | `MonitorFromWindow` + `GetMonitorInfo` — round 2 found these absent; needed by §6. Or defer the guard. |
| New message | `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `OpenWindowSubMenuMessage`. |
| VM third activation branch | `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:87-95` | `MenuCommand.WindowSubMenu` → send `OpenWindowSubMenuMessage`. |
| Adapter translation | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | `OpenWindowSubMenuMessage` → `InteractionManager` event (preserves B3). |
| Re-show | `src/EyeCtrl.App/Services/EventCoordinator.cs:1064-1098` | Hide→show sequence (§4); teach `Show` to read sub-ring list or VM `Items`. |
| Dispatch | `src/EyeCtrl.App/Services/EventCoordinator.cs:1386` switch | `Minimize`/`MaximizeRestore`/`Close` → `IWindowManager` on stashed HWND. Add `default` or keep exhaustive. |
| Icons | `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:103-116` | BMP glyphs: Minimize `▁`, MaxRestore `□`, Close `✕`. Verify on target box. |
| Default visibility | `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs:11-47` | Window entry in default visible set + `EnsureDefaults`. |
| Drift tests | `tests/.../ActionMenuViewModelSymbolTests.cs:31-46`, `tests/.../InteractionActionEnumTests.cs:14` | Extend glyph map for 3 actions; `PinNote==9` stays valid. |

### DI / blast radius
`IWindowManager` is a DI singleton (`AppBootstrapper.cs:74`); the Task Switcher is the current sole caller — **re-verify it** when adding methods. Round 2 flagged blast radius: `InteractionManager` ≈50 direct dependents / 110 total / 3 processes; `ActionMenuViewModel` ≈20. The state + message additions are the highest-risk edits — run `impact` before touching them (per `AGENTS.md`).

## §6 Edge cases & safety *(v3)*

- **Desktop / no window:** `WindowAtPoint` returns null → Window entry suppressed (sub-ring shows only Back).
- **Shell windows (Progman/WorkerW/Shell_TrayWnd):** rejected by `GetShellWindow` + class denylist (§2 hardening). **This is the round-2 destructive-close fix.**
- **EyeCtrl overlay wins the `WindowFromPoint` race:** rejected by `WS_EX_TOOLWINDOW` + own-process guards.
- **Owned dialog:** `GetAncestor(GA_ROOT)` returns the dialog itself (top-level); acceptable for v3 (minimize/close the dialog, not its owner) — document as a known divergence from owner-rooted semantics. Switch to `GA_ROOTOWNER` only if user feedback demands it.
- **Fullscreen (F11) window:** `IsZoomed`=false, so toggle would `SW_MAXIMIZE` a borderless-fullscreen window and can wedge it to the work area. **Guard needs a monitor API** (`MonitorFromWindow`+`GetMonitorInfo`) to compare window rect vs monitor rect — round 2 found these absent from `NativeMethods`. **Decision for impl:** either add the monitor API + guard, or defer the guard and document fullscreen as an unsupported case for v1. State which.
- **Already-minimized:** `IsIconic` → Restore (§3 truth table).
- **Stale HWND:** `IsWindow` re-check at op-fire; no-op if false.
- **Elevated target:** `PostMessage` UIPI-blocked → `false` → logged (§3). `ShowWindow` return is **not** a failure signal (§3).
- **`ConsoleWindowClass` host:** terminates on `WM_CLOSE` with no save prompt — document.
- **Multi-monitor:** `WindowFromPoint` is screen-space, works across monitors for *targeting* (window actions aren't blocked by gap #7); cursor movement is unchanged.

## §7 Testing *(v3)*

- **Eligibility predicate** (pure, over a candidate record incl. className/rect — round 2 found `AppWindow` lacks these): accept/reject each shell class, tool-window, own-process, owned; null when no candidate. No fake-of-interface (the round-1 circularity).
- **`DecideWindowOp`** (pure): all four `(isZoomed, isIconic)` combos (round 2 verified the table).
- **Settings:** default visible set includes Window entry; `EnsureDefaults` seeds it.
- **State machine:** enter `WindowMenuVisible` with correct `WindowMenuReturnState`; Back returns to the right parent; dismiss → free look; the `Window` descriptor sends `OpenWindowSubMenuMessage`, not `ActionRequested`.
- **Re-show regression:** rendered item IDs and dwell-target IDs change together after the hide→show sequence.
- **Dispatch:** 3 actions route to `IWindowManager` with stashed HWND; stale HWND no-ops; `CloseWindow` failure logs.
- **Drift tests** updated.
- UI tests (`Category=UI`): not gating.

## §8 Out of scope / future

- **Show Desktop:** disjoint (Win+D via `SendModifiedKeyPress`).
- **Window-picker grid (option C):** reuses `WindowAtPoint` + the 3 ops + the eligibility predicate. Its reachability is what makes the already-minimized case real → `IsIconic` settled now. The DWM-bounds rect helper added in §2 is also for this.
- **`GA_ROOTOWNER` for owned dialogs:** revisit if feedback demands owner-rooted.

## §9 Open questions for implementation *(v3)*

- **Fullscreen guard:** add `MonitorFromWindow`+`GetMonitorInfo` now, or defer and document unsupported? (§6)
- **Descriptor rollout:** inject the Window descriptor into `Items` inside `Receive` (no message-contract change), or add a descriptor-list overload to `ShowActionMenuMessage`? (§4)
- **`ActionMenuWindow.Show` action source:** read sub-ring list from VM/state, or teach `Show` to read `Items`? (§4)
- **`ExternalDwellService` consolidation:** retarget its `rootWindowAtPoint` delegate to `IWindowManager.WindowAtPoint` (recommended), or leave the third copy? (§2)

## §10 Change log
- **2026-08-04 v3 (after review round 2):** all 6 round-2 BLOCKERs verified (Progman title, no activation route, EnumWindows-Z-order undocumented, GetWindowRect borders, equality-gated re-show re-raised, ring-position-not-index) and incorporated. Resolver returned to `WindowFromPoint` (documented topmost primitive) resolved *pre-show* (fixes the v1 self-hit that made v2 abandon it), consolidating the existing `ExternalDwellService.RootWindowAtPointImpl`; hardened with `GetShellWindow`+class denylist+DWM extended-frame bounds. Introduced a first-class `ActionMenuDescriptor`+`MenuCommand`+`OpenWindowSubMenuMessage` (the v2 "no new message" simplification was proved wrong). `WindowMenuVisible` gains a retained return-state. Re-show committed to hide→show (no longer deferred). Fixed `ShowWindow` return semantics, the monitor-API gap for the fullscreen guard, and the `AppWindow`-lacks-fields issue for the predicate. Verified via Microsoft docs: EnumWindows Z-order, GetWindowRect/DWM bounds.
- **2026-08-04 v2 (after review round 1):** resolver→EnumWindows hit-test; removed `Window` enum member; removed new message type; added `WindowMenuVisible`. (Superseded by v3 — v2's resolver and message simplifications were both disproven by round 2.)
- **2026-08-04 v1 (initial):** `WindowFromPoint`+`GetAncestor` resolver; `Window` enum member; `ShowWindowMenuMessage`.
