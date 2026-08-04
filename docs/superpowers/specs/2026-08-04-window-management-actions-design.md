# Design: Window-management actions (gap #2)

**Date:** 2026-08-04 · **Status:** Design (pre-implementation) — **revision 5, after four review rounds**
**Branch:** `feat/window-management-actions` (off `main` @ `b590a87`)
**Gap source:** `docs/COMPETITIVE_FEATURE_ANALYSIS.md` §Tier-1 #2

> **Revision history (§10):** v1 `WindowFromPoint`+enum+new msg → v2 EnumWindows hit-test → v3 back to `WindowFromPoint`+descriptor+msg → v4 invocation-time resolution. **v5** fixes the 7 BLOCKERs round 4 found: the stash race (HWND cleared before dispatch reads it), the missing content route (`ShowActionMenu` can't carry a list), and five `WindowMenuVisible` gaps (no arm in `OnActionSelected`/`OnActionMenuDismissed`/backdrop-dismiss/`TogglePause`; `showBack` derives false; `Receive` loops). Round 4 also proved the `ExternalDwellService` consolidation would regress a shipping SDK surface — **v5 abandons the consolidation** and leaves the SDK on its raw primitive. The author has been wrong four times in ways reviews caught; v5 specifies every hop concretely rather than "decide at impl."

## §0 Context & problem

The action menu offers click/scroll/drag/note actions only (`InteractionAction`: LeftClick, RightClick, DoubleClick, Drag, ScrollUp/Down/Left/Right, ScrollMode, PinNote — `src/EyeCtrl.Core/Models/InteractionAction.cs:4`). No way to **minimize, maximize/restore, or close the window under gaze** — both OptiKey and TD Control ship it. App-*switching* landed (`TaskSwitcherWindow`); per-window *management actions* are the remaining open slice of gap #2.

Out of scope: Show Desktop (global Win+D), a window-picker grid (§8), multi-monitor cursor handling (gap #7), icon-tooltip localization.

## §1 Goal & scope

Add **Minimize**, **Maximize/Restore** (toggle), and **Close** behind a grouped "Window" sub-menu, acting on the top-level window under the resolved target point.

**In scope:**
- 3 new `InteractionAction` members (Minimize, MaximizeRestore, Close — appended after PinNote, keeping `PinNote==9`).
- A hardened topmost-window-at-point resolver, resolved at menu-**invocation** time.
- A first-class menu-item descriptor (`ActionMenuDescriptor`+`MenuCommand`) + `ActionMenuItemViewModel.Command` + `OpenWindowSubMenuMessage`.
- `InteractionState.WindowMenuVisible` + `WindowMenuReturnState` + entry/Back methods.
- **Contract changes (the v5 crux, all verified against current shapes):**
  - `ActionRequestedMessage` gains an `IntPtr? TargetWindow` field (stash-race fix).
  - `InteractionManager.ShowActionMenu` event widened to carry the action list, OR a `PendingMenuActions` property read by the adapter (decided in §4 — pick the property).
  - `WindowMenuVisible` arms added in 4 sites: `OnActionSelected`, `OnActionMenuDismissed`, backdrop-dismiss, `TogglePause`.
  - `OnShowActionMenu` derives `showBack`+`dwellMs` for `WindowMenuVisible`.
  - `Receive` injection gated on state (no loops).
  - `GazeEngineAdapter` routes `BackSelectedMessage` by state.
- Dispatch + icons + `ShowWindowSubMenuAction` settings bool; unit tests; 2 enum-drift-test updates.

## §2 Resolver — `WindowFromPoint` at invocation time, hardened *(v5: placement pinned, consolidation abandoned)*

### Why `WindowFromPoint`, resolved at invocation time

Rounds 2-4 established: `WindowFromPoint` is the documented topmost primitive (no `EnumWindows`-order assumption); resolving at sub-menu-show time self-hits the parent menu (v3 BLOCKER); resolving at menu-**invocation** time is occlusion-free because the menu window doesn't exist yet. `ShowActionMenu` is raised from `InteractionManager.OnActivationButtonDwelled` (`:99/101/106/121`) before `ActionMenuViewModel.Receive` sets `IsVisible=true` (`ActionMenuViewModel.cs:73`).

### v5 pins the placement (round 4 BLOCKER)

`GazeEngineAdapter.OnShowActionMenu` is *entirely* a `_dispatcher.BeginInvoke(() => {...})` body (`GazeEngineAdapter.cs:108-152`) — "resolve in `OnShowActionMenu`" was ambiguous between two readings that differ in safety. **v5 specifies: resolve *inside the dispatched lambda*, immediately before `_messenger.Send(new ShowActionMenuMessage(...))`, on the raw `point` argument** (not `clampedPoint` — under the shipped `AtCenter` default `clampedPoint` is screen centre, `ActionMenuLayoutHelper.cs:39-41`). This depends on the queued `HideActivationButton` (raised at `InteractionManager.cs:88`, before the `:99/101/106` `ShowActionMenu` raises) having drained: both adapter handlers `BeginInvoke` at the same priority → FIFO → hide first → `ActivationButtonWindow.HideImmediate()` (`ActivationButtonWindow.xaml.cs:58-62`) is synchronous. **Regression test:** assert the activation-button window is hidden when resolution runs.

### Load-bearing assumption to document (round 4 MINOR)

`CursorCircleWindow` is centred on the gaze point and **still visible** at resolution time (hidden only later, `EventCoordinator.cs:1076`). The feature works because `WindowFromPoint` honours `WS_EX_TRANSPARENT` and skips click-through overlays (`CursorCircleWindow.xaml.cs:17`). MS docs only promise the hidden/disabled skip; the evidence it holds for transparent windows is empirical and in-repo (`ExternalDwellService`'s occlusion gate uses the same primitive in production). **Document this assumption in §2** — it cannot be unit-tested.

### Non-click-through overlay degradation (round 4 MAJOR — declared, not solved)

The non-click-through set is larger than v4 claimed: `ActionMenuWindow`, `ActivationButtonWindow`, `KeyboardWindow`, `NoteWindow` (`NoteWindow.xaml.cs:44`), `QuickActionsGridWindow`, `ScrollOverlayWindow`, `SplashWindow`, `ToolbarWindow`, `TaskSwitcherWindow`. Pinned notes and the toolbar float *over* application windows, so a gaze target behind one resolves to a tool window → reject → null → the Window entry silently vanishes. **v5 declares this an intentional degradation in §6** (documented limitation, not a beneath-walk) — a beneath-walk (`GetWindow(GW_HWNDNEXT)`+DWM-bounds containment) is deferred to §8. Test: a target occluded by a pinned note resolves null and the entry is suppressed.

### Hardening (rounds 2-4; v5: +visibility/cloaked, on `GA_ROOTOWNER`)

Applied to the `GA_ROOTOWNER` ancestor of the `WindowFromPoint` hit (a desktop-icons hit yields `SysListView32` → root `Progman`; checking only the raw hit's class would miss it):

1. **`GetAncestor(hit, GA_ROOTOWNER)`** (3) — owner-rooted, matches the codebase convention (`EnumerateOpenWindows` excludes `GW_OWNER≠0`, `Win32WindowManager.cs:37-38`). Returns self for unowned top-levels (harmless).
2. **`IsWindowVisible`** + **`DWMWA_CLOAKED`** *(v5: round 4 MAJOR — a visible owned dialog can have a hidden/cloaked owner; both primitives already declared, `NativeMethods.cs:111,141`)*.
3. **`GetShellWindow()`** reject.
4. **Class-name denylist** on the ancestor: `Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, `#32769` — via `GetClassName`.
5. **`WS_EX_TOOLWINDOW`** reject.
6. **Own-process** reject.
7. **`IsWindow`** re-check at op-fire time.

### v5 abandons the `ExternalDwellService` consolidation (round 4 BLOCKER)

Round 4 proved retargeting `ExternalDwellService`'s `rootWindowAtPoint` delegate (`Func<PhysicalPoint, long?>?`, `ExternalDwellService.cs:68`) regresses a **shipping SDK surface**: the returned root is used in an *equality gate* (`h == r`, `:131`) against the HWND an SDK client registered. `GA_ROOT`→`GA_ROOTOWNER` changes the identity → equality never holds for owned client windows → those targets never activate; and the hardening rejects `WS_EX_TOOLWINDOW`/own-process → null for any SDK client that is itself a tool-window overlay (the natural `EyeCtrl.Sdk.Wpf`/`Sdk.WinUI` shape) → every target dead. §7's predicate test cannot catch this (the predicate never sees the equality gate). **v5: do NOT consolidate.** Leave `ExternalDwellService.RootWindowAtPointImpl` (`GA_ROOT`, unhardened) untouched — it's the correct primitive for the SDK's equality-gate use. Build the hardened `WindowAtPoint` only for window-management. Delete §2's "consolidate" language; remove §9's go/no-go.

### Hit-test bounds: DWM extended frame (§8 future only)

`DWMWA_EXTENDED_FRAME_BOUNDS=9` via a new `DwmGetWindowAttribute` `out RECT` overload (existing import is `out int` only, `NativeMethods.cs:131`); fall back to `GetWindowRect`. **Note (round 3 NIT):** DWM bounds are physical px (not DPI-virtualized); `GetWindowRect` is DPI-virtualized — the fallback mixes coordinate spaces under non-per-monitor-aware processes. State the assumption. Needed only for the §8 picker grid's rect containment; not on the v5 `WindowFromPoint` path.

### API + new P/Invokes

```csharp
// IWindowManager / Win32WindowManager
AppWindow? WindowAtPoint(PhysicalPoint point);        // topmost eligible, or null
bool Minimize(IntPtr hwnd);                           // ShowWindow SW_MINIMIZE (IsWindow-guard result)
bool MaximizeOrRestore(IntPtr hwnd);                  // consumes DecideWindowOp
bool CloseWindow(IntPtr hwnd);                        // PostMessage WM_CLOSE (meaningful bool)
```

New P/Invokes (all verified absent from `NativeMethods.cs`): `WindowFromPoint`, `GetAncestor`(+`GA_ROOTOWNER=3`), `GetShellWindow`, `GetClassName`, `ShowWindow`(+`SW_*`), `PostMessage`(+`WM_CLOSE`), `IsZoomed`, `IsIconic`, `IsWindow`, `GetWindowRect`(+`RECT`), `MonitorFromWindow`+`GetMonitorInfoW` (§6 fullscreen), `DwmGetWindowAttribute` `out RECT` overload. `GetWindow` (`:114`), `IsWindowVisible` (`:111`), `DWMWA_CLOAKED` (`:141`) already exist. Use `[LibraryImport]`.

## §3 Action set *(v5: +stash-race fix on ActionRequestedMessage)*

- **Minimize** → `ShowWindow(hwnd, SW_MINIMIZE)`
- **Maximize/Restore** → pure `DecideWindowOp(IsZoomed, IsIconic)` then the matching `SW_*` (round 2 verified the table): normal→Maximize, maximized→Restore, minimized-normal→Restore, minimized-from-maximized→Restore (`IsIconic` first).
- **Close** → `PostMessage(hwnd, WM_CLOSE)` (graceful; `DefWindowProc` derives from `SC_CLOSE`).

**Op return types (round 3 D.1):** `Minimize`/`MaximizeOrRestore` return `bool` = the `IsWindow`-guard result only (`ShowWindow`'s return is not a failure signal — zero = "was previously hidden"); `CloseWindow` returns the meaningful `PostMessage` result (UIPI-blocked sends to elevated windows return `false`). All log at `Information` on a false/null path.

**Close behavior — no confirm, conditional:** no confirm sub-step (consistent with LeftClick/RightClick/Drag); `WM_CLOSE` preserves the app's "save changes?" prompt. Defensible only because of two guards (§4): ring-position + Back. **Caveats:** `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close; **Close on a modal dialog closes the owning application** (because `GA_ROOTOWNER` resolves to the owner — round 4 B.2) — list alongside `ConsoleWindowClass`.

### Stash-race fix (round 4 BLOCKER — the one independent defect)

`InteractionManager.OnActionSelected` raises `ActionRequested` at `:167` then calls `ResetToFreeLook()` synchronously at `:168`. `AutoSelectionTarget` survives (read synchronously at `:136`), but a stashed HWND field would be cleared before the adapter's `BeginInvoke` delivers it to `HandleActionRequested` (`EventCoordinator.cs:1365`). **Fix: carry the HWND on `ActionRequestedMessage`** (`InteractionMessages.cs:64`):

```csharp
public record ActionRequestedMessage(
    InteractionAction Action, PhysicalPoint Point, bool IsPreciseTarget = false,
    IntPtr? TargetWindow = null);   // v5: the resolved owner HWND, for window-management actions
```

`InteractionManager.OnActionSelected` passes the stashed HWND; `EventCoordinator.HandleActionRequested` reads `msg.TargetWindow` for Minimize/MaximizeRestore/Close (ignoring `pt` for those — round 4 F). The stash field on `InteractionManager` is still cleared in `ResetToFreeLook`, but its value has already been captured on the message. This is the **only** contract change needed for the stash race, and it's independent of sub-menu vs flat.

## §4 Placement — "Window" sub-menu *(v5: every hop specified, contracts widened)*

### Sub-menu entry + activation route (rounds 2-4)

A first-class descriptor carries the sub-menu command:

```csharp
// EyeCtrl.Core.Models
public record ActionMenuDescriptor(string Id, InteractionAction? Action, MenuCommand? Command, string Symbol);
public enum MenuCommand { WindowSubMenu }
```

`ActionMenuItemViewModel` gains `MenuCommand? Command` (round 3 B.2). `ActionMenuViewModel.OnTargetActivated` gets a third branch (`:87-95`): `if (item.Command == MenuCommand.WindowSubMenu) { DwellDetector.ClearTargets(); Messenger.Send(new OpenWindowSubMenuMessage()); }`. `OpenWindowSubMenuMessage` is empty (round 3 E.2 — the re-show flows through `OnShowActionMenu`, which recomputes Center/Dwell/CircleSize from state+settings).

### v5: the content route (round 4 BLOCKER — the event must carry the list)

Round 4: `ShowActionMenu` is `event Action<PhysicalPoint>` (`InteractionManager.cs:36`) — it cannot carry the sub-ring list, and `OnShowActionMenu` hard-codes `_settings.ActionMenu.VisibleActions` (`GazeEngineAdapter.cs:136`). Under "contract unchanged" the sub-ring renders the **parent** list. **v5 fix: add a `PendingMenuActions` property on `InteractionManager`** that `OnShowActionMenu` reads (chosen over widening the event signature — fewer call-site changes, and `OnShowActionMenu` already reads `_interactionManager` state at `:111`):

```csharp
// InteractionManager
public IReadOnlyList<InteractionAction>? PendingMenuActions { get; private set; }
// OnOpenWindowSubMenu sets it to {Minimize, MaximizeRestore, Close} before raising ShowActionMenu;
// OnShowActionMenu in the adapter reads it (sub-ring) or falls back to settings.VisibleActions (parent);
// cleared in ResetToFreeLook.
```

`OnShowActionMenu` (`GazeEngineAdapter.cs:135`): `var actions = im.PendingMenuActions ?? _settings.ActionMenu.VisibleActions;` then passes `actions` to `ShowActionMenuMessage.Actions`. This is the content route. **Add a §5 row.**

### v5: `OnActionSelected` arm for `WindowMenuVisible` (round 4 BLOCKER)

Round 4: the sub-ring items carry a real `InteractionAction`, so `OnTargetActivated` sends `ActionSelectedMessage` → `OnActionSelected` → its target-point switch (`InteractionManager.cs:132-142`) has **no `WindowMenuVisible` arm**, falls to `_ => null`, returns at `:144` → `ActionRequested` never fires → dead actions. **v5 fix: add `WindowMenuVisible` to the switch**, resolving `targetPoint` to the same point used for the parent (the stashed invocation-time point, or `FixationPoint`). The action then flows through `ActionRequested` (with `TargetWindow`, §3) → `HandleActionRequested` → the new switch cases.

### v5: `Receive` gating — no loops (round 4 BLOCKER)

Round 4: `Receive` runs for both parent and sub-ring shows (same `ShowActionMenuMessage`); injecting the Window descriptor on every `Receive` puts a Window entry in the sub-ring → activating it re-enters `OnOpenWindowSubMenu` with `CurrentState == WindowMenuVisible` → `WindowMenuReturnState = WindowMenuVisible` → Back loops. **v5 fix (two-part):** (a) gate the descriptor injection in `Receive` on `_settings.ActionMenu.ShowWindowSubMenuAction && stashedHwnd != null && /* not sub-ring */ CurrentState != WindowMenuVisible` — but `Receive` is in the VM and shouldn't read `InteractionManager.CurrentState`. So (b) **cleaner: gate on the message's `Actions` list** — if `message.Actions` is the sub-ring const (or: carry an `IsSubMenu` init flag on `ShowActionMenuMessage`), don't inject. **v5 picks: add `bool IsSubMenu` init property to `ShowActionMenuMessage`** (default false); `Receive` injects the descriptor only when `!message.IsSubMenu`. `OnShowActionMenu` sets it from `im.CurrentState == WindowMenuVisible`. This reverses v4's "contract unchanged" ruling — round 4 proved a level flag is necessary.

### v5: `showBack` derivation + `OnShowActionMenu` arms (round 4 BLOCKER)

Round 4: `showBack = im.CurrentState == ManualSelectionActionMenuVisible` (`GazeEngineAdapter.cs:112`) → **false** in `WindowMenuVisible` → no Back button → deletes §3 guard #2. **v5 fix:** `var showBack = im.CurrentState is ManualSelectionActionMenuVisible or WindowMenuVisible;`. Likewise `dwellMs`: sub-ring uses `_settings.Dwell.ActionMenuDwellMs` (same as parent auto path). `ManualSelectionDisplayRadius`/`ManualSelectionCenter`/`ManualSelectionRadius` stay null/0 for `WindowMenuVisible` (round 4 D MINOR — the preview circle loses magnifier context, acceptable since the sub-ring isn't manual-selection).

### v5: the four `WindowMenuVisible` state arms (round 4 BLOCKERs)

`WindowMenuVisible` must be handled in **all** of:
1. **`OnActionSelected` target-point switch** (`:132-142`) — arm added above (content route).
2. **`OnActionMenuDismissed`** (`:190-197`) — currently guards on `ActionMenuVisible` only. **Add `WindowMenuVisible` arm:** `HideActionMenu` +, if `WindowMenuReturnState == ManualSelectionActionMenuVisible`, `EndManualSelection` (round 4 C MAJOR — dismiss from a manual-origin sub-ring must end the manual session), then `ResetToFreeLook`.
3. **Backdrop-dismiss** (`EventCoordinator.cs:466-470`) — currently branches on `ActionMenuVisible`/`ManualSelectionActive`. **Add `WindowMenuVisible` → `im.OnActionMenuDismissed()`** (which now handles it). Without this, the backdrop hides (`:458`) but the menu stays up and the state machine sticks — unrecoverable for a gaze-only user (round 4 C).
4. **`TogglePause`** (`:203-221`) — its `EndManualSelection` arm covers `ManualSelectionActive`/`ManualSelectionActionMenuVisible` (`:217-218`). **Add `WindowMenuVisible` with `WindowMenuReturnState == ManualSelectionActionMenuVisible`** to that arm's condition. (Pause from an auto-origin sub-ring just sets Paused; the return-state is benign-overwritten on next entry.)

Consumers verified to need **no** arm: `CursorCircleViewModel` (FreeLook/Fixation-only), `GazeEngine` (moves cursor only in FreeLook/Dragging), toolbar/heartbeat/adaptation/external-dwell (distinguish only `Paused`).

### v5: Back routing decided (round 4 MAJOR)

`BackSelectedMessage` is parameterless with one registration (`GazeEngineAdapter.cs:86-87`). **v5 pins: branch in the adapter on `_interactionManager.CurrentState`:**

```csharp
messenger.Register<BackSelectedMessage>(this, (_, _) => _dispatcher.BeginInvoke(() =>
{
    if (_interactionManager.CurrentState == InteractionState.WindowMenuVisible)
        _interactionManager.OnWindowMenuBack();
    else
        _interactionManager.OnManualSelectionBack();
}));
```

`OnWindowMenuBack()` (new sibling): restore `WindowMenuReturnState`, raise `HideActionMenu` then `ShowActionMenu` (re-show the parent ring — `PendingMenuActions = null` so the adapter falls back to `settings.VisibleActions`), clear `WindowMenuReturnState`. The adapter already reads `CurrentState` at `:112,198` — precedent.

### Re-show repaint (round 4, pinned)

`OnOpenWindowSubMenu` (and `OnWindowMenuBack`) raise `HideActionMenu` then `ShowActionMenu`. The hide flips `IsVisible=false` (`ActionMenuViewModel.cs:78`, fires `PropertyChanged` → `Hide()`+`HideBackdrop()`, `EventCoordinator.cs:1093-1097`); the show flips it back (→ `Show()` rendering `vm.Items`). ~100 ms re-capture (`ScreenCaptureDelayMs=100`, `ActionMenuWindow.xaml.cs:20,93`). **Regression test:** rendered item IDs and dwell-target IDs change together. **`ActionMenuWindow.Show` is NOT changed** — it already renders from `vm.Items` and ignores its `actions` param (`:179-198`); round 3 C.3 holds.

### `ClearTargets` (round 4 MINOR — defence-in-depth)

Call `DwellDetector.ClearTargets()` in the third `OnTargetActivated` branch before sending `OpenWindowSubMenuMessage`. Note `ClearTargets` does **not** reset `_lastActivationTimestamp`, so the sub-ring inherits the cooldown — desirable; state it. Primary rationale is dispatcher-backlog defence, not the common case.

### Ring-position guard for Close (round 4 BLOCKER — collapsed to a static invariant)

Round 4: `_itemLookup` is cleared by `Receive(HideActionMenuMessage)` (`:84`) and at the top of `Receive(ShowActionMenuMessage)` (`:45`), so `_itemLookup["window"]` is empty when the sub-ring lays out (and keys are `action.ToString()` PascalCase, not `"window"`). **But** the descriptor is **prepended** → the parent Window button is always index 0 → angle `−90°` → straight up from `Center`. **v5 collapses the guard to a static invariant: no sub-ring item at index 0 may be Close.** `{Minimize, MaximizeRestore, Close}` (+Back) already satisfies it (Close at 90° with 4 items). State the invariant, test it across parent counts, drop the runtime lookup. The suppressed-Window case needs no special handling.

## §5 Components & seams *(v5)*

| Change | File | Notes |
|---|---|---|
| New enum members | `InteractionAction.cs:4` | Append Minimize, MaximizeRestore, Close after PinNote. |
| Descriptor + command | `EyeCtrl.Core/Models/` (new) | `ActionMenuDescriptor`, `MenuCommand`. |
| Item-VM extension | `ActionMenuItemViewModel.cs` | `MenuCommand? Command`. |
| Settings bool | `ActionMenuSettings.cs` | `ShowWindowSubMenuAction` (default true, `EnsureDefaults`). |
| **`ActionRequestedMessage`** | `InteractionMessages.cs:64` | **+`IntPtr? TargetWindow`** (stash-race fix, §3). |
| **`ShowActionMenuMessage`** | `InteractionMessages.cs:21` | **+`bool IsSubMenu` init** (default false; Receive gating, §4). |
| **`PendingMenuActions`** | `InteractionManager.cs` | **New property**; adapter reads it for the sub-ring list (§4 content route). |
| New state + return-state | `InteractionState.cs`, `InteractionManager.cs` | `WindowMenuVisible` + `WindowMenuReturnState`; `OnOpenWindowSubMenu`/`OnWindowMenuBack`; **4 state arms** (OnActionSelected, OnActionMenuDismissed+EndManualSelection, backdrop-dismiss, TogglePause). |
| Resolver + ops | `IWindowManager.cs`, `Win32WindowManager.cs` | `WindowAtPoint`, Minimize/MaximizeOrRestore/CloseWindow. **No ExternalDwellService consolidation** (§2). |
| Pure toggle | `EyeCtrl.Core/Models/WindowStateOp.cs` | `DecideWindowOp(bool,bool)`. |
| Pure eligibility predicate | `EyeCtrl.Core/Models/` | Over candidate record (hwnd/exStyle/owner/pid/title/cloaked/className) → bool. Drops `rect` (§8-only). |
| Invocation-time HWND stash | `GazeEngineAdapter.cs:108-152` | Resolve `WindowAtPoint(point)` inside the dispatched lambda, before `Send`; stash on `InteractionManager`. |
| **`OnShowActionMenu` arms** | `GazeEngineAdapter.cs:111-119,135` | **`showBack` includes `WindowMenuVisible`**; **`actions = im.PendingMenuActions ?? settings.VisibleActions`**; **`IsSubMenu = im.CurrentState == WindowMenuVisible`**. |
| **Back routing** | `GazeEngineAdapter.cs:86-87` | **Branch on `CurrentState`** → `OnWindowMenuBack`/`OnManualSelectionBack`. |
| **Backdrop-dismiss arm** | `EventCoordinator.cs:466-470` | **+ `WindowMenuVisible → OnActionMenuDismissed`**. |
| New P/Invoke | `NativeMethods.cs` | See §2 list. `[LibraryImport]`. |
| Dispatch | `EventCoordinator.cs:1386` switch | Minimize/MaximizeRestore/Close → `IWindowManager` on `msg.TargetWindow`; ignore `pt` for those. Add `default`. |
| Icons | `ActionMenuViewModel.cs:103-116` | BMP glyphs: Minimize `▁`, MaxRestore `□`, Close `✕`. |
| Drift tests | `ActionMenuViewModelSymbolTests.cs:47-48`, `InteractionActionEnumTests.cs:14` | Extend glyph map; PinNote==9 stays valid. |

### DI / blast radius
`IWindowManager` DI singleton (`AppBootstrapper.cs:74`); Task Switcher sole caller — re-verify it. `InteractionManager` ≈50 direct dependents / 110 total / 3 processes (GitNexus); `ActionMenuViewModel` ≈20. The state + contract changes are highest-risk — run `impact` before editing (per `AGENTS.md`).

## §6 Edge cases & safety *(v5)*

- **Desktop / no window:** `WindowAtPoint` null → Window descriptor omitted → entry suppressed.
- **Shell windows (Progman/WorkerW/Shell_TrayWnd/Shell_SecondaryTrayWnd):** rejected (`GetShellWindow`+class denylist on the `GA_ROOTOWNER` ancestor, §2). Round-2 destructive-close fix.
- **Hidden/cloaked owner** *(v5: round 4 MAJOR)*: a visible owned dialog with a hidden/cloaked owner — `IsWindowVisible`+`DWMWA_CLOAKED` on the ancestor reject it.
- **EyeCtrl overlay wins `WindowFromPoint`:** rejected (tool-window + own-process).
- **Owned dialog:** `GA_ROOTOWNER` → Close closes the owning application (round 4 B.2 — **stated in §3 caveats**, not just §6).
- **Fullscreen (F11):** `IsZoomed`=false → toggle would `SW_MAXIMIZE` a borderless-fullscreen window and wedge it. **v5 adds `MonitorFromWindow`+`GetMonitorInfoW`** and skips the toggle when the window rect ⊇ monitor rect (with border tolerance, not equality — `GetWindowRect`'s invisible frame makes a fullscreen window *larger* than `rcMonitor`) and `!IsZoomed`. Both rects are physical virtual-screen coords (round 4 D MINOR — no coord-space mismatch). (Delete v4's "primary-screen-only heuristic acceptable" sentence — `MonitorFromWindow` generalizes for free.)
- **Already-minimized:** `IsIconic` → Restore.
- **Stale HWND:** `IsWindow` at op-fire.
- **Elevated target:** `PostMessage` UIPI-blocked → `false` → logged.
- **Target occluded by a non-click-through overlay** *(v5: round 4 MAJOR — declared limitation)*: a pinned note / toolbar over the target → resolves null → entry suppressed. **Documented limitation;** beneath-walk deferred to §8.
- **AutoSelection:** menu center is `AutoSelectionTarget.CenterPoint` (`:99`); resolving there yields the control's top-level window — correct semantic.
- **Multi-monitor:** `WindowFromPoint` screen-space, works across monitors for targeting; cursor movement unchanged (gap #7).

## §7 Testing *(v5)*

- **Eligibility predicate** (pure, over candidate record incl. className + cloaked + visible): accept/reject each shell class (incl. `Shell_SecondaryTrayWnd`, visible non-tool `Progman` titled "Program Manager"), tool-window, own-process, hidden/cloaked owner; null when no candidate. No fake-of-interface.
- **`DecideWindowOp`**: all four `(isZoomed, isIconic)` combos.
- **Settings:** `ShowWindowSubMenuAction` default true, seeded.
- **State machine:** enter `WindowMenuVisible` with both return-state variants; `OnWindowMenuBack` restores the right parent; **all 4 arms** (OnActionSelected, OnActionMenuDismissed+EndManualSelection on manual-origin, backdrop-dismiss, TogglePause) behave correctly; `OnOpenWindowSubMenu` sets `PendingMenuActions` and raises Hide→Show; `OpenWindowSubMenuMessage` flows VM→adapter→`OnOpenWindowSubMenu`.
- **Stash race (round 4):** `OnActionSelected` for a window action puts the HWND on `ActionRequestedMessage.TargetWindow`; `HandleActionRequested` reads it (not a cleared stash).
- **Receive gating (round 4):** descriptor injected only when `!IsSubMenu`; sub-ring has no Window entry.
- **Content route (round 4):** sub-ring renders `{Minimize,MaximizeRestore,Close}` from `PendingMenuActions`, not the parent list.
- **Re-show regression:** item IDs + dwell-target IDs change together.
- **Ring-position invariant:** Close never at sub-ring index 0, across parent counts.
- **Fullscreen guard:** skip when window-rect ⊇ monitor-rect + `!IsZoomed`.
- **Dispatch:** 3 actions route to `IWindowManager` on `msg.TargetWindow`; stale HWND no-ops; `CloseWindow` failure logs.
- **Drift tests** updated.
- UI tests (`Category=UI`): not gating.

## §8 Out of scope / future

- **Show Desktop:** disjoint (Win+D).
- **Window-picker grid (option C):** reuses `WindowAtPoint` + the 3 ops + the eligibility predicate + the DWM-bounds rect helper. Its reachability makes already-minimized real → `IsIconic` settled.
- **Beneath-walk for occluded targets** *(v5: round 4 MAJOR deferral)*: `GetWindow(GW_HWNDNEXT)` + DWM-bounds containment to resolve a target hidden behind a pinned note/toolbar. Deferred; v5 declares the occlusion-as-null-suppression limitation.

## §9 Open questions *(v5 — none material)*

Round 4's only remaining item (the `ExternalDwellService` go/no-go) is closed: **do not consolidate** (§2). All contract decisions are specified in §4/§5. No open questions block implementation.

## §10 Change log
- **2026-08-05 v5 (after review round 4, 7 BLOCKERs):** round 4 verified two independent defects — the **stash race** (`ResetToFreeLook` at `:168` clears before dispatch reads) and the **`ExternalDwellService` equality-gate regression** (GA_ROOT→GA_ROOTOWNER + hardening breaks the SDK's `h == r` gate for owned/tool-window clients). v5 carries the HWND on `ActionRequestedMessage.TargetWindow` (stash race) and **abandons the consolidation** (leave the SDK on its raw `GA_ROOT` primitive). The other 5 BLOCKERs shared one root cause — the sub-menu needs menu-level context the contracts don't carry — fixed by: `PendingMenuActions` property (content route), `WindowMenuVisible` arms in 4 sites (OnActionSelected, OnActionMenuDismissed+EndManualSelection, backdrop-dismiss, TogglePause), `showBack` derives for WindowMenuVisible, `ShowActionMenuMessage.IsSubMenu` (Receive gating, reversing v4's "contract unchanged"), Back routed by adapter state branch, ring-position collapsed to a static invariant (Close never at index 0). Pinned the resolution placement (inside the dispatched lambda, after HideActivationButton drains; CursorCircle click-through assumption documented). Declared the non-click-through-overlay occlusion a limitation (§6, beneath-walk deferred to §8). Added `IsWindowVisible`+`DWMWA_CLOAKED` to the hardening (hidden/cloaked owner). `GA_ROOTOWNER` Close-on-dialog-closes-app stated in §3 caveats. Removed v4's `ExternalDwellService` consolidation row, the "Default visibility" contradiction, the duplicated rows, and the stale v3 self-references.
- **2026-08-04 v4:** invocation-time resolution; descriptor+message; WindowMenuVisible. *(Stash race, content route, and 4 state-arm gaps found by round 4.)*
- **2026-08-04 v3:** back to WindowFromPoint; descriptor+message; WindowMenuVisible. *(Self-hit moved to parent menu — fixed in v4.)*
- **2026-08-04 v2:** EnumWindows hit-test; removed Window enum member + new message. *(Superseded.)*
- **2026-08-04 v1:** WindowFromPoint+GA_ROOT; Window enum member; ShowWindowMenuMessage.
