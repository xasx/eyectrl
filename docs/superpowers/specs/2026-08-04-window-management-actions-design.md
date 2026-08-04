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

**In scope:** three new `InteractionAction` members (Minimize, MaximizeRestore, Close — appended after PinNote); a hardened topmost-window-at-point resolver; a first-class menu-item descriptor (`ActionMenuDescriptor`+`MenuCommand`) + `ActionMenuItemViewModel.Command` + a dedicated `OpenWindowSubMenuMessage`; an `InteractionManager` entry method (`OnOpenWindowSubMenu`) + `OnWindowMenuBack` sibling; a `WindowMenuVisible` state with a retained return-state; re-show repaint; a `ShowWindowSubMenuAction` settings bool; dispatch + icons + default visibility; unit tests; two enum-drift-test updates.

## §2 Resolver — `WindowFromPoint`, pre-show, hardened *(v3: replaces v2's EnumWindows hit-test)*

### Why not v2's EnumWindows hit-test

Round 2 verified two fatal flaws (both against Microsoft docs, not just the reviewer's claim):

1. **`EnumWindows` does not document Z-order** ([MS docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-user32-enumwindows) say only "enumerates … by passing the handle … to the callback"; [broad consensus](https://stackoverflow.com/questions/295996/is-the-order-in-which-handles-are-returned-by-enumwindows-meaningful) is the order is an implementation detail). v2's "first containing rect = topmost" could target an obscured window for Close. The documented Z-order method is `GetTopWindow`+`GetWindow(GW_HWNDNEXT)` — but see the better option below.
2. **`GetWindowRect` includes invisible 7-8px resize borders** ([MS `GetWindowRect` docs](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-useruser-getwindowrect) explicitly recommend `DWMWA_EXTENDED_FRAME_BOUNDS` for visible bounds). v2's hit-test would select a window whose invisible border crosses the point while gaze is visually over the window behind it.

### Adopted resolver: `WindowFromPoint` resolved at menu-INVOCATION time *(v4: fixes v3's BLOCKER)*

`WindowFromPoint` returns the **topmost** HWND under a point by definition — no enumeration-order assumption. v3 resolved it at *Window-dwell time* and claimed that was safe because "only the parent ring is up." **Round 3 proved that wrong** (verified against the code):

- `ActionMenuLayoutHelper.CalculateDisplayPoint` returns the fixation/gaze point when `ViewPosition==AtGaze` (`ActionMenuLayoutHelper.cs:39-45`), and `AtCenter` (the shipped default, `EyeCtrlSettings.cs:30`) centers on screen.
- `ActionMenuWindow` is centered exactly on that point (`Left = center.X - Width/2`, `ActionMenuWindow.xaml.cs:200-201`), is `Topmost` (`ActionMenuWindow.xaml:6`), and is **not** click-through (no `IsClickThrough` → no `WS_EX_TRANSPARENT`, `OverlayWindow.cs:84-90`).
- So for the entire dwell on the Window entry, the parent menu window **occludes the target point**. `WindowFromPoint(targetPoint)` returns the ActionMenuWindow HWND, the `WS_EX_TOOLWINDOW`/own-process guards reject it, and `WindowAtPoint` returns **null**. In AtGaze the feature is a silent no-op *always*; in AtCenter it no-ops whenever fixation lands within menu-half-size (~200+ DIP) of screen center.

**v4 fix: resolve + stash the HWND at menu-INVOCATION time**, before any menu window exists. The `ShowActionMenu` event is raised from `InteractionManager.OnActivationButtonDwelled` (`InteractionManager.cs:99,101,106,121`) — *before* `ActionMenuViewModel.Receive` sets `IsVisible=true` and `ActionMenuWindow.Show()` runs. `GazeEngineAdapter.OnShowActionMenu` (`GazeEngineAdapter.cs:108-152`) is the resolution site: call `WindowAtPoint(targetPoint)` there, stash the result on `InteractionManager` following the existing `AutoSelectionTarget` pattern ("set externally… cleared in `ResetToFreeLook`", `InteractionManager.cs:18-23`). At that instant the only non-click-through EyeCtrl window near the point is the ActivationButtonWindow, offset `circleRadius + 55` DIP from the point (`EventCoordinator.cs:960`) against its ~50 DIP half-size — it never covers the point.

Invocation-time resolution also makes §6's entry-suppression implementable: a null HWND → `Receive` omits the Window descriptor (the entry simply isn't offered when there's no eligible window under gaze).

### Consolidate the existing helper (don't write a third copy)

`WindowFromPoint`+`GetAncestor(GA_ROOT)` is **already implemented once** in this codebase — `ExternalDwellService.RootWindowAtPointImpl` (`src/EyeCtrl.App/Services/ExternalDwellService.cs:329-340`), complete with the `root == IntPtr.Zero ? hwnd : root` fallback. (`ControlFinder.FallbackToWindow`, `:178-200`, calls `WindowFromPoint` but *not* `GetAncestor` — it uses the raw HWND + `GetClientRect`; not a second copy of the same helper. v3 overclaimed this; corrected.)

v4 **consolidates** the helper into the new `IWindowManager.WindowAtPoint` seam in Platform (`NativeMethods`, `[LibraryImport]` style). Retargeting `ExternalDwellService` is **not** a trivial delegate swap (round 3 MINOR): its delegate is `Func<PhysicalPoint, long?>?` (`ExternalDwellService.cs:68`) returning `long?`, while `WindowAtPoint` returns `AppWindow?` — needs an adapter lambda (`pt => _windowManager.WindowAtPoint(pt)?.Hwnd.ToInt64()`). **Behavior change to flag:** the current impl is unhardened (returns an overlay's root over an EyeCtrl overlay); the hardened resolver returns null — a behavior change on a shipping SDK surface, covered by a predicate-level test.

### Hardening (round 2's destructive-close finding; round 3 refinements)

`EnumerateOpenWindows`' empty-title filter does **not** exclude the shell — `Progman`'s title is `"Program Manager"` (non-empty) and it passes every filter. `PostMessage(WM_CLOSE)` to `Progman` restarts Explorer. Defense in depth, **all applied to the `GA_ROOT` ancestor of the `WindowFromPoint` hit** (a desktop-icons hit yields `SysListView32` → root `Progman`; checking only the raw hit's class would miss it):

1. **`GetAncestor(hit, GA_ROOTOWNER)`** *(v4: was `GA_ROOT` — round 3 MAJOR; see §6 for the per-op policy)* to reach the owner root before any class/style check.
2. **`GetShellWindow()`** reject — the definitive shell-desktop HWND (covers `Progman` in the common case).
3. **Class-name denylist** — `Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd` *(v4: round 3 — multi-monitor secondary taskbars)*, `#32769` — via `GetClassName` on the ancestor. `WorkerW` is *not* returned by `GetShellWindow` (it layers over Progman under animated wallpaper), so the denylist carries that load. `Shell_SecondaryTrayWnd` is needed because §6 claims cross-monitor targeting works.
4. **`WS_EX_TOOLWINDOW`** reject (EyeCtrl overlays).
5. **Own-process** reject.
6. **`IsWindow`** re-check at op-fire time (stale-HWND guard, §6).

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

No confirm sub-step, consistent with LeftClick/RightClick/Drag; `WM_CLOSE` preserves the target app's "save changes?" prompt. **Defensible only because of two guards** (round 2's condition, both retained): (1) the ring-position guard (§4), (2) the Back path (§4). **Caveats to document:** `ConsoleWindowClass` hosts terminate with no prompt; "minimize-to-tray-on-X" apps actually close under `WM_CLOSE`.

### Op return types *(v4: round 3 D.1 — made consistent)*

`Minimize`/`MaximizeOrRestore` return `bool` = the `IsWindow`-guard result only (their `ShowWindow` return is not a failure signal); `CloseWindow` returns the meaningful `PostMessage` result. All log at `Information` on a false/null path.

## §4 Placement — "Window" sub-menu, via a first-class descriptor *(v4: activation hops named, settings model decided)*

### The sub-menu entry needs a real activation route (round 2 BLOCKER)

Round 2 verified the v2 design has no plumbing for a non-enum entry: `ShowActionMenuMessage.Actions` is `IReadOnlyList<InteractionAction>` (`InteractionMessages.cs:21`), `ActionMenuItemViewModel` has only `Action`+`IsBackButton`, and `OnTargetActivated` emits only `BackSelectedMessage` or `ActionSelectedMessage` (`ActionMenuViewModel.cs:87-95`). A "Window" item with `Action==null` emits **neither** — it's a dead button.

### v4: a first-class menu-item descriptor + a dedicated message

Introduce a descriptor that can carry either a real action or a sub-menu command:

```csharp
// EyeCtrl.Core.Models (Core — no layering issue; it's data)
public record ActionMenuDescriptor(string Id, InteractionAction? Action, MenuCommand? Command, string Symbol);

public enum MenuCommand { WindowSubMenu }   // extensible
```

The Window entry is `new("window", Action: null, Command: MenuCommand.WindowSubMenu, Symbol: "🗗" or similar)`.

**`ActionMenuItemViewModel` gains a `MenuCommand? Command` property** (round 3 B.2 — currently it has only `Action`+`IsBackButton`, constructed `(id, action, symbol, position, isBack)`, `ActionMenuViewModel.cs:66`). The descriptor → item-VM mapping carries `Command` through. `OnTargetActivated` gains a **third branch**: `if (item.Command == MenuCommand.WindowSubMenu) Messenger.Send(new OpenWindowSubMenuMessage());`.

**New message** (ViewModels layer — the *existing* direction; `GazeEngineAdapter` translates it to a Core event, preserving B3 layering):

```csharp
// EyeCtrl.ViewModels.Messages — empty record: the re-show flows through OnShowActionMenu,
// which recomputes Center/DwellTimeMs/CircleSize from state+settings (GazeEngineAdapter.cs:108-152).
// Carrying them would be redundant (round 3 E.2). An empty signal is sufficient.
public record OpenWindowSubMenuMessage;
```

### Full activation path — every hop named (round 3 B.2 MAJOR)

1. `ActionMenuViewModel.OnTargetActivated` third branch → `Messenger.Send(new OpenWindowSubMenuMessage())`.
2. `GazeEngineAdapter` registers `IRecipient<OpenWindowSubMenuMessage>` → calls `InteractionManager.OnOpenWindowSubMenu()` (new entry method).
3. `OnOpenWindowSubMenu`: set `WindowMenuReturnState = CurrentState`; `SetState(WindowMenuVisible)`; raise `HideActionMenu` then `ShowActionMenu` events (the re-show, below) with a **fixed 3-item sub-ring list** `{Minimize, MaximizeRestore, Close}` (a const — not from settings; the parent list in `OnShowActionMenu` reads `_settings.ActionMenu.VisibleActions`, which is the parent).
4. `GazeEngineAdapter` translates those events → `HideActionMenuMessage` → `ShowActionMenuMessage` (with `ShowBack: true`) → `Receive` rebuilds `Items`/dwell targets → repaint.

Layering preserved (`EyeCtrl.Core.csproj` has 0 `ProjectReference`; descriptor/`MenuCommand` are Core data; the message lives in ViewModels; the adapter translates — B3 intact).

### Descriptor source — a new settings bool *(v4: round 3 C.2 — v3 was unimplementable as written)*

v3 said "inject the Window descriptor in `Receive` when settings say it's visible" — but `ActionMenuSettings.VisibleActions` is `List<InteractionAction>` (`ActionMenuSettings.cs:11-18`) and the Window entry has no enum member, so "is it visible" had no expression. **v4: add a settings bool `ShowWindowSubMenuAction` (default `true`, seeded in `EnsureDefaults`)** to `ActionMenuSettings`. `Receive` constructs and prepends the Window descriptor when `_settings.ActionMenu.ShowWindowSubMenuAction && stashedHwnd != null`. The HWND-null check (§2 invocation-time resolution) means the entry simply isn't offered when there's no eligible window — the §6 suppression is now implementable.

### `ShowActionMenuMessage` contract — unchanged *(v4: round 3 C.3 collapses the either/or)*

Round 3 verified the v3 "either/or" rested on a misread: `OnActionMenuVMPropertyChanged` passes `_context.Settings.ActionMenu.VisibleActions` to `Show()` (`EventCoordinator.cs:1082`), **but `ActionMenuWindow.Show` ignores its `actions` parameter entirely** — buttons render from `vm.Items` (`ActionMenuWindow.xaml.cs:179-198`; `actions` appears only in the signature, `:38`). So the show path is already VM-Items-driven, and `Items` is message-driven via `Receive`. **Ruling: keep `ShowActionMenuMessage` unchanged; inject the descriptor in `Receive` per the settings bool; no `Show` change.** (The dead `actions` parameter is pre-existing smell, out of scope.) §9's either/or is closed.

### New state — `WindowMenuVisible` with a retained return-state *(v4: round 3 C.4 fixes the leaks)*

Add the state **and** a retained `WindowMenuReturnState` field on `InteractionManager` (set on entry, restored on Back).

**Back arm — a sibling, not a third arm in `OnManualSelectionBack`** (round 3 C.4 decision): `OnManualSelectionBack` is reached via a single `BackSelectedMessage` registration (`GazeEngineAdapter.cs:87`); rather than overload its semantics, add `InteractionManager.OnWindowMenuBack()` and route `BackSelectedMessage` to it when `CurrentState == WindowMenuVisible` (the adapter branches on state, or the message carries the menu level).

**`ResetToFreeLook` / `EndManualSelection` leak fix (round 3 C.4 — verified):** `ResetToFreeLook` is called from 5 sites (`OnActionSelected :168`, `OnManualSelectionBack :187`, `OnActionMenuDismissed :197`, `ExitScrollMode :228`, `CompleteDrag :235`) and **clears the return-state**. But `TogglePause` (`:203-221`) does **not** call it, and its `EndManualSelection` arm only covers `ManualSelectionActive`/`ManualSelectionActionMenuVisible` (`:217-218`). So pausing or dismissing from a *manual-origin* `WindowMenuVisible` leaks the manual-selection session (edge-button bar, `RestoreManualSelectionView` bookkeeping). **v4 fix:** when abandoning a manual-origin `WindowMenuVisible` (dismiss or pause), fire `EndManualSelection`. Concretely: `OnActionMenuDismissed` and `TogglePause` must treat `WindowMenuVisible` with `WindowMenuReturnState == ManualSelectionActionMenuVisible` as a manual session and call `EndManualSelection`.

### Re-show must repaint *(v4: round 3 names the sender)*

`ActionMenuWindow.Show` is driven only by the `IsVisible` `PropertyChanged` handler (`EventCoordinator.cs:1064-1098`); `Receive` writes `IsVisible = true` (equality-gated). **v4 commits to hide→show, and names the sender:** `InteractionManager.OnOpenWindowSubMenu` raises `HideActionMenu` then `ShowActionMenu` (step 3 above). The hide flips `IsVisible=false` (→ `Hide()`+`HideBackdrop()`); the show flips it back (→ `Show()` rendering `vm.Items`, which `Receive` rebuilt with the sub-ring). ~100 ms re-capture cost (`ScreenCaptureDelayMs = 100`, `ActionMenuWindow.xaml.cs:20,93`) — acceptable. **Regression test:** rendered item IDs and live dwell-target IDs change together.

### ClearTargets on sub-menu request *(v4: round 3 E.1)*

The Hide/sub-ring arrives asynchronously via the Core round-trip; meanwhile `DwellOverlayViewModelBase.Receive` keeps feeding the *parent* dwell targets (`DwellOverlayViewModelBase.cs:33-37`), so a user resting on the Window button can re-activate it post-cooldown and queue a duplicate sub-menu request. **v4:** call `DwellDetector.ClearTargets()` in the third `OnTargetActivated` branch (or on `OpenWindowSubMenuMessage` send) so the parent targets stop accumulating the moment the request fires.

### Ring-position guard for Close *(v4: round 3 B.6 — concrete margin + reason)*

Positions aren't index-determined: `angleStep = 360/itemCount`, `angle = angleStep·i − 90°` (`ActionMenuViewModel.cs:47-70`). **Reason it matters (round 3):** the user's gaze is *resting on the parent Window button* when the sub-ring appears, and `DwellDetector` immediately starts accumulating on whatever sits at that physical position. **v4:** compute the parent Window button's absolute position from `_itemLookup["window"].Position`; rotate the sub-ring so Close is center-to-center ≥ `2×physicalButtonRadius + margin` (margin ≥ one button radius) from it. Test across supported parent counts and Window positions.

## §5 Components & seams *(v4)*

| Change | File | Notes |
|---|---|---|
| New enum members | `src/EyeCtrl.Core/Models/InteractionAction.cs:4` | Append `Minimize`, `MaximizeRestore`, `Close` after `PinNote` (keeps `PinNote==9`). No `Window` member. |
| Menu descriptor + command | `src/EyeCtrl.Core/Models/` (new) | `ActionMenuDescriptor`, `MenuCommand` enum. Core data. |
| Item-VM extension | `src/EyeCtrl.ViewModels/ActionMenuItemViewModel.cs` | Add `MenuCommand? Command` (round 3 B.2). |
| Settings bool | `src/EyeCtrl.Core/Settings/ActionMenuSettings.cs` | `ShowWindowSubMenuAction` (default true, `EnsureDefaults`-seeded) — the descriptor's visibility source (round 3 C.2). |
| New state + return-state | `src/EyeCtrl.Core/Models/InteractionState.cs`, `src/EyeCtrl.Core/Processing/InteractionManager.cs` | `WindowMenuVisible` + `WindowMenuReturnState`; `OnOpenWindowSubMenu` entry method; `OnWindowMenuBack` sibling; `EndManualSelection` on manual-origin dismiss/pause (§4). |
| Resolver + ops | `src/EyeCtrl.Core/Interfaces/IWindowManager.cs` + `src/EyeCtrl.Platform/Services/Win32WindowManager.cs` | `WindowAtPoint` (returns `AppWindow?`), `Minimize/MaximizeOrRestore/CloseWindow`. Consolidate `ExternalDwellService.RootWindowAtPointImpl` via an adapter lambda (long? ↔ AppWindow?; behavior-change noted in §2). |
| Pure toggle | `src/EyeCtrl.Core/Models/WindowStateOp.cs` (new) | `DecideWindowOp(bool isZoomed, bool isIconic)`. No Win32. |
| Pure eligibility predicate | `src/EyeCtrl.Core/Models/` (new) | Over a candidate record (hwnd/exStyle/owner/pid/title/cloaked/className) → bool. §7. *(v4: `rect` dropped from v1 — the `WindowFromPoint` path has no rect; it's §8-only.)* |
| Invocation-time HWND stash | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs:108-152` | Resolve `WindowAtPoint(targetPoint)` in `OnShowActionMenu` (before the menu window exists), stash on `InteractionManager` (AutoSelectionTarget pattern). §2. |
| New P/Invoke | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | See §2 list. `[LibraryImport]`. `DwmGetWindowAttribute` gains an `out RECT` overload (+`DWMWA_EXTENDED_FRAME_BOUNDS=9`). |
| Fullscreen guard | `src/EyeCtrl.Platform/Input/NativeMethods.cs` | `MonitorFromWindow` + `GetMonitorInfoW` — **v4: decided, not deferred** (§6). |
| New message | `src/EyeCtrl.ViewModels/Messages/InteractionMessages.cs` | `OpenWindowSubMenuMessage` (empty record). |
| VM third activation branch | `src/EyeCtrl.ViewModels/ActionMenuViewModel.cs:87-95` | `MenuCommand.WindowSubMenu` → `ClearTargets()` + `Send(OpenWindowSubMenuMessage)`. |
| Adapter translation | `src/EyeCtrl.App/Services/GazeEngineAdapter.cs` | `IRecipient<OpenWindowSubMenuMessage>` → `InteractionManager.OnOpenWindowSubMenu()`. |
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

## §6 Edge cases & safety *(v4)*

- **Desktop / no window:** `WindowAtPoint` returns null → Window entry suppressed (descriptor omitted in `Receive`).
- **Shell windows (Progman/WorkerW/Shell_TrayWnd/Shell_SecondaryTrayWnd):** rejected by `GetShellWindow` + class denylist on the `GA_ROOTOWNER` ancestor (§2 hardening). **Round-2 destructive-close fix.**
- **EyeCtrl overlay wins the `WindowFromPoint` race:** rejected by `WS_EX_TOOLWINDOW` + own-process guards.
- **Owned dialog *(v4: round 3 C.1 — decided, not deferred)*:** use `GA_ROOTOWNER` (3), **not** `GA_ROOT`, for Minimize/Close. The user perceives "the app", not the modal dialog; `ShowWindow(SW_MINIMIZE)` on an owned dialog yields a legacy minimize-to-desktop-title-bar the gaze-only user can't easily undo; and `GA_ROOTOWNER` matches the codebase's owner-rooted convention (`EnumerateOpenWindows` excludes `GW_OWNER≠0`). v1 used `GA_ROOT` and round 1 flagged it; v3 reverted to `GA_ROOT` and round 3 re-flagged it; **v4 commits to `GA_ROOTOWNER`.** *(Note: this means the hardening in §2 walks `GA_ROOTOWNER`; the class checks run on that ancestor.)*
- **Fullscreen (F11) window *(v4: round 3 C.6 — decided, not deferred)*:** `IsZoomed`=false, so the toggle would `SW_MAXIMIZE` a borderless-fullscreen window, clamping it to the work area and wedging the app's rendering — and the gaze-only user is the *least* able to recover. **v4 adds the monitor API** (`MonitorFromWindow`+`GetMonitorInfoW`, `[LibraryImport]`) and skips the toggle when the window rect covers the monitor rect and `!IsZoomed`. Given gap #7 defers multi-monitor, a primary-screen-only heuristic is acceptable for v1; the API is added now so the guard generalizes.
- **Already-minimized:** `IsIconic` → Restore (§3 truth table).
- **Stale HWND:** `IsWindow` re-check at op-fire; no-op if false.
- **Elevated target:** `PostMessage` UIPI-blocked → `false` → logged (§3). `ShowWindow` return is **not** a failure signal (§3).
- **`ConsoleWindowClass` host:** terminates on `WM_CLOSE` with no save prompt — document.
- **AutoSelection *(v4: round 3 E.3 one-liner)*:** when the menu center is `AutoSelectionTarget.CenterPoint` (`InteractionManager.cs:99`), resolving there yields the control's top-level window — the correct semantic.
- **Multi-monitor:** `WindowFromPoint` is screen-space, works across monitors for *targeting* (window actions aren't blocked by gap #7); cursor movement is unchanged. `Shell_SecondaryTrayWnd` is in the denylist for this reason.

## §7 Testing *(v4)*

- **Eligibility predicate** (pure, over a candidate record incl. className — round 2 found `AppWindow` lacks these): accept/reject each shell class (incl. `Shell_SecondaryTrayWnd`, a visible non-tool `Progman` with title "Program Manager"), tool-window, own-process; null when no candidate. No fake-of-interface (the round-1 circularity).
- **`DecideWindowOp`** (pure): all four `(isZoomed, isIconic)` combos (round 2 verified the table).
- **Settings:** `ShowWindowSubMenuAction` defaults true, `EnsureDefaults`-seeded.
- **State machine:** enter `WindowMenuVisible` with correct `WindowMenuReturnState` (both parent variants); Back (`OnWindowMenuBack`) returns to the right parent; **dismiss/pause from a manual-origin WindowMenuVisible fires `EndManualSelection`** (round 3 C.4); the `Window` descriptor sends `OpenWindowSubMenuMessage`, not `ActionRequested`.
- **Re-show regression:** rendered item IDs and dwell-target IDs change together after the hide→show sequence.
- **ClearTargets:** parent dwell targets cleared on `OpenWindowSubMenuMessage` send (round 3 E.1).
- **Ring-position guard:** Close placement ≥ `2×physicalButtonRadius + margin` from the parent Window position across parent counts.
- **Dispatch:** 3 actions route to `IWindowManager` with stashed HWND; stale HWND no-ops; `CloseWindow` failure logs; fullscreen-window skip.
- **Drift tests** updated.
- UI tests (`Category=UI`): not gating.

## §8 Out of scope / future

- **Show Desktop:** disjoint (Win+D via `SendModifiedKeyPress`).
- **Window-picker grid (option C):** reuses `WindowAtPoint` + the 3 ops + the eligibility predicate. Its reachability makes the already-minimized case real → `IsIconic` settled now. The DWM-bounds rect helper added in §2 is also for this.

## §9 Open questions for implementation *(v4 — reduced)*

- **`ExternalDwellService` consolidation:** retarget via the adapter lambda (§2) — recommended, but flag the SDK behavior change. *(Previously open in v3; the adapter shape is now specified, leaving only the go/no-go.)*
- No other open questions remain in v4 — fullscreen guard, descriptor source, `Show` contract, Back arm, and return-state resets are all decided above.

## §10 Change log
- **2026-08-05 v4 (after review round 3):** round 3 confirmed the author's flagged fear — v3's "resolve at Window-dwell time" self-hits the *parent* menu (AtGaze always null; AtCenter null near screen center), verified against `ActionMenuLayoutHelper.cs:39-45` + `ActionMenuWindow.xaml.cs:200-201`. **Fix: resolve at menu-INVOCATION time** in `GazeEngineAdapter.OnShowActionMenu`, stashed on `InteractionManager` (AutoSelectionTarget pattern). Activation route hops all named (round 3 B.2) + item-VM `Command` property. Descriptor source decided: new `ShowWindowSubMenuAction` settings bool (round 3 C.2 — v3 was unimplementable). `ShowActionMenuMessage` contract unchanged (round 3 C.3 collapsed the either/or — `Show` ignores its `actions` param, renders from `vm.Items`). Back arm = sibling `OnWindowMenuBack`; `EndManualSelection` fired on manual-origin dismiss/pause (round 3 C.4 — `TogglePause` leak verified). Re-show sender named (`InteractionManager` raises Hide→Show). `ClearTargets` on sub-menu request (round 3 E.1). Ring-position margin made concrete (round 3 B.6). `GA_ROOT` → **`GA_ROOTOWNER`** for Minimize/Close (round 3 C.1 — v1/v3 re-flagged finding). Fullscreen guard **decided** (add `MonitorFromWindow`+`GetMonitorInfoW`; round 3 C.6 — v3 overclaimed "fixed" while deferring). `Shell_SecondaryTrayWnd` added to denylist (round 3 B.1). `ExternalDwellService` consolidation is an adapter lambda + flagged SDK behavior change (round 3 C.5). Corrected v3's overclaims (§10 log now matches body) and the "implemented twice" nit (`ControlFinder.FallbackToWindow` has no `GetAncestor`). Round-2's EnumWindows/DWM-bounds/re-show/ring-guard fixes confirmed genuinely closed by round 3.
- **2026-08-04 v3 (after review round 2):** resolver returned to `WindowFromPoint`; descriptor + message; `WindowMenuVisible`. *(Self-hit timing was wrong — fixed in v4.)*
- **2026-08-04 v2 (after review round 1):** resolver→EnumWindows hit-test; removed `Window` enum member + new message type. *(Superseded by v3.)*
- **2026-08-04 v1 (initial):** `WindowFromPoint`+`GetAncestor` resolver; `Window` enum member; `ShowWindowMenuMessage`.
