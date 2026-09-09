# Repeat-Click Mode + "Repeat Last Action" — Design

**Date:** 2026-09-08
**Gap:** #4 in `docs/COMPETITIVE_FEATURE_ANALYSIS.md` (2026-09-08 refresh) — the last open Tier-1 item.
**Branch:** `feat/repeat-click-mode`, stacked on `feat/mouse-osk-polish` (PR #138, still open; touches the same enum/icon wiring files — stacking avoids conflicting edits).
**Reference:** TD Control v1.80.1 — `ModesMenu: ["Continuous", "RepeatClick", "Drawing", "None"]`, `ContinuousModeMenu: ["LeftClick", "ClickAndDrag", "HoldAndDrag", "None"]`, `StartInContinuousMode: false`. TD exposes no repeat-interval value (its Continuous mode is dwell-per-fixation, not interval-based), so EyeCtrl's interval default is chosen, not copied.

## What is being built

Two related affordances for the click family, both shipped **OFF by default**:

1. **Repeat (one-shot)** — a ring action that re-fires the *last click-family action* at the
   resolved target, without hunting for its slot. The doc's "cheap 80%": the biggest win is for
   second-layer actions (MiddleClick, ScrollLeft, …) — one dwell on layer one instead of
   More → action.
2. **Repeat Mode (locked repeat)** — a ring action that locks the last click-family action
   (LeftClick when nothing has fired yet) to re-fire at the **live stable gaze point** every
   `IntervalMs`, until stopped. This is TD's Continuous/RepeatClick concept: the user aims by
   looking; the click repeats wherever they look.

**Click family** = `LeftClick`, `RightClick`, `DoubleClick`, `MiddleClick`. Scroll already has a
continuous mode (`ScrollMode`); drag already follows gaze; `PinNote` and the one-shot scroll
notches are not things a user re-fires rhythmically.

## Owner-vetoable decision ledger

The session ran autonomously (owner unreachable for interactive design review). Every
judgement call is numbered here; veto any row and the corresponding piece is reworked.

| # | Decision | Rationale | Rejected alternative |
|---|----------|-----------|---------------------|
| D1 | Two ring entries (`RepeatLastAction`, `RepeatMode`), not a TD-style ModesMenu | The task scope names exactly these two; a modes menu is a third surface for a two-item choice (YAGNI) | Sub-menu / modes wheel |
| D2 | TD's **Click-on-Fixation** variant is declined | It is direct dwell-to-click — EyeCtrl's Interaction-First two-step philosophy exists to *prevent* accidental clicks; shipping it would erode differentiator positioning | Also implement click-on-fixation |
| D3 | Locked action = last click-family action, captured at mode entry; `LeftClick` when no history | Zero extra UI to choose the action; do-then-lock matches TD's flow | A per-mode action picker; lock-left-click only |
| D4 | Ticks land at the **live stable centroid** (`GazeEngine.StableCentroid`), not a fixed point | Scope text: "repeats at the gaze point"; TD Continuous clicks at what you look at; photo-advancing/games need retargeting | Repeat at the locked entry point |
| D5 | Ticking lives App-side (`RepeatModeController`, `DispatcherTimer`); Core exposes a guarded `FireRepeatTick(point)` | `ScrollModeController`/`ScrollWorker` precedent: timers that drive input simulation live App-side; the guard keeps the state machine the single authority for when repeats are legal | A Core-side timer raising `ActionRequested` on its own |
| D6 | Exit affordances: right-edge **Stop** dwell button + **ESC** key; toolbar Pause is blocked during RepeatMode | Mirrors scroll-stop + DragCoordinator's ESC hatch; pause-during-repeat would leave the timer racing a hidden state | Tray-pause as exit; no ESC |
| D7 | New `RepeatSettings` (`IntervalMs` 250–5000, default **1000**; `StopButtonDwellMs` 200–2000, default 600, unexposed) | `ScrollSettings` precedent; TD publishes no interval to copy — 1000 ms is non-frantic for repeat clicking; stop dwell mirrors every other edge button and stays unexposed exactly like `ScrollStopButtonDwellMs` | Cramming both into `DwellSettings`; exposing the stop dwell |
| D8 | Interval read **at mode entry** | Same as ScrollModeController's settings reads; live retuning mid-session is untested complexity for no recorded need | Live interval updates |
| D9 | `RepeatLastAction` is converted to the remembered action **inside `InteractionManager.OnActionSelected`**, before `ActionRequested` fires | Dispatch stays one path (`GazeEngineAdapter` → `ActionRequestedMessage` → `KeyboardInteractionDispatcher`); the dispatcher switch needs no new arms; `isPrecise` resolution (auto-snap / manual-selection point) works unchanged | A new message type for "repeat this" |
| D10 | During RepeatMode: physical cursor follows gaze (GazeEngine gate) and the cursor circle stays visible (allow-list) | The user must see where ticks will land; reuses the standard pointer, no new overlay window. ScrollMode hides the circle because its overlay shows an anchor dot instead — repeat has no anchor | A dedicated repeat overlay; hiding the cursor |
| D11 | External dwell unchanged during RepeatMode (only Paused suppresses it today) | Same behaviour as Dragging/ScrollMode; the combination is no more incoherent than what already ships | Suppress external dwell while repeating |
| D12 | Repeat ticks are **raw gaze point**, no per-tick ControlFinder snap | A UIA lookup per tick (500 ms timeout budget) would cap the repeat rate and add a thread hop; auto-snap already happened for the action that seeded the lock | Snap each tick |
| D13 | Enum members appended (`RepeatLastAction` = 11, `RepeatMode` = 12, after `PinNote` = 10) | `InteractionActionEnumTests` pins `PinNote` = 10; settings files carry no numeric values, but value stability keeps the pin honest | Inserting before PinNote |
| D14 | Defaults: neither action in `VisibleActions`/`EnsureDefaults`; `PrimaryActions` untouched | New ring actions ship OFF (task constraint); the second layer must stay at exactly 4 defaults (`ActionMenuViewModelLayerTests.TheSecondLayer_ReusesTheSameFiveSlots`); enable via Settings → Action Menu (rows are auto-generated per enum member) | Adding to defaults / promoting to layer one |
| D15 | Docs: gap #4 moves to "Resolved" with evidence; queue re-ranks to #22 next | Keeps the competitive doc the single roadmap source | Leaving the doc for a separate pass |

## Components

### Core — state machine (`InteractionManager`)

```
InteractionState.RepeatMode           // new state, alongside ScrollMode/Dragging
InteractionManager.LastRepeatableAction   // get-only; click-family memory (null until first click)
InteractionManager.RepeatAction           // get-only; the locked action (null outside RepeatMode)
InteractionManager.FireRepeatTick(point)  // guarded: raises ActionRequested(RepeatAction, point)
InteractionManager.ExitRepeatMode()       // guarded: clears RepeatAction, ResetToFreeLook
```

- `OnActionSelected`:
  - click-family action → update `LastRepeatableAction`, then existing dispatch;
  - `RepeatLastAction` → resolve `effective = LastRepeatableAction ?? LeftClick`, set
    `LastRepeatableAction = effective`, `ActionRequested(effective, targetPoint)`, reset;
  - `RepeatMode` → hide menu / end manual selection (ScrollMode branch shape), capture
    `RepeatAction = LastRepeatableAction ?? LeftClick`, `SetState(RepeatMode)`, no ActionRequested.
- `TogglePause`: add `RepeatMode` to the blocked states.
- `ResetToFreeLook`: clears `RepeatAction`.
- `OnFixationStarted`: already ignored outside FreeLook — no change.
- New `InteractionActionExtensions.IsRepeatableClick` (Models) — single classification site.

### Core — settings

`RepeatSettings : ObservableObject, IValidatable` (ScrollSettings shape): `IntervalMs`
(default 1000, range 250–5000, both setter-guard and `Validate()`), `StopButtonDwellMs`
(default 600, range 200–2000). Registered on `EyeCtrlSettings` (`Repeat` member) and in
`EnumerateSubSettings` — `EyeCtrlSettingsEnumerationTests` count moves 19 → 20.

### ViewModels

`ActionMenuViewModel`: `FriendlyName` arms (`RepeatLastAction` → "Repeat",
`RepeatMode` → "Repeat mode") and `GetActionIcon` arms (throwing switch —
`EveryAction_MapsToADistinctIcon` forces both).

### Icons (`ActionIcon` + `ActionIcons`)

Two new members after `PinNote`. Mouse-silhouette convention (24-unit grid):

- `RepeatLastAction` — `Body + Buttons`, no fill (plain, action-agnostic: it repeats
  *whatever was last* — the wheel stroke would misread as scroll) plus one circular renewal
  arrow in the right margin.
- `RepeatMode` — `Body + Buttons` with `LeftButtonFill` (it actively clicks) plus renewal
  arrows in **both** margins — outline-level difference from RepeatLastAction, per the
  peripheral-vision rule in `ActionIcons`' header.

`ActionIconsTests.EveryIcon_FitsTheGrid` is the geometry verifier.

### App — `RepeatModeController` (Services/Coordinators, `CoordinatorBase`)

- Enters on `StateChanged == RepeatMode`: shows right-edge Stop button
  (`CreateRepeatStopButtonDefinition`, internal static for a pin test; symbol `\u23F9` — same
  "stop" glyph as scroll-stop, mutually exclusive modes), starts a UI-thread
  `DispatcherTimer` with `settings.Repeat.IntervalMs`.
- Tick: `if (GazeEngine.StableCentroid is { } p) InteractionManager.FireRepeatTick(p)` —
  no stable point (gaze away/lost) → skip the tick, never click a stale point.
- Exits on any state change away while active: stops timer, hides edge bar.
- `ExitRepeatModeMessage` registration → `ExitRepeatMode()` on the dispatcher.
- ESC via `ComponentDispatcher.ThreadFilterMessage` (VK_ESCAPE in RepeatMode → send
  `ExitRepeatModeMessage`), DragCoordinator pattern.
- Wired in `EventCoordinator.Initialize`/`Dispose` beside `DragCoordinator`.

### App — feedback + settings UI

- `GazeEngine.OnGazePoint`: cursor-follow gate becomes `FreeLook or Dragging or RepeatMode`.
- `CursorCircleViewModel`: allow-list becomes `FreeLook or Fixation or RepeatMode`.
- `AdvancedPageSource`: new "Repeat" section — `Repeat Interval (ms)` slider
  (250–5000, step 50) + reset-handler arm.

### Messages

`ExitRepeatModeMessage` in `InteractionMessages.cs` (beside `ExitScrollModeMessage`).

## Error handling

- `FireRepeatTick` outside RepeatMode: no-op (guard), never throws.
- Tick with null stable centroid: skipped.
- Exit paths (stop button, ESC, state change) are idempotent; timer stopped before state
  change is observed to avoid a late tick after exit.
- Dispatcher sees only click-family actions (D9) — no unknown-action path.

## Testing

- **Core** `InteractionManagerRepeatModeTests` (scroll-mode test style): entry/exit states,
  no ActionRequested on entry, RepeatAction capture (history / no-history), LastRepeatableAction
  updated by click family only, RepeatLastAction conversion + reset, FireRepeatTick guard +
  stays in state, TogglePause blocked, fixation ignored, StateChanged on enter/exit.
- **Core** enum/extension tests: value pins (11/12, PinNote still 10), `IsRepeatableClick`
  truth table.
- **Core** `RepeatSettings` validation tests (range rejects, defaults valid).
- **Core** enumeration drift: 19 → 20.
- **ViewModels**: `EveryAction_MapsToADistinctIcon` expected map + 2 entries.
- **Controls**: grid-fit/frozen coverage picks up the new icons automatically.
- **App**: `CreateRepeatStopButtonDefinition` pin (id/symbol/dwell source), mirroring the
  scroll-stop pin test.

## Out of scope

Click-on-Fixation (D2), TD's Drawing Mode (Tier-2 #10), a per-mode action picker, live
interval retuning, multi-monitor (Tier-2 #7).

## Impact analysis (gitnexus, re-indexed)

`InteractionManager` upstream = **CRITICAL** (10 symbols, 7 processes: startup, external
dwell, benchmarks, samples) — hub-ness, not breakage: no exhaustive `InteractionState`
switches exist outside the manager; every consumer equality-checks specific states. The
change is additive except three deliberate touch points (GazeEngine cursor gate,
CursorCircleViewModel allow-list, TogglePause guard). `OnActionSelected` upstream = LOW
(single caller). `InteractionAction` consumers: 29 files; full-enum enumerations only in
`ActionMenuPageSource` (auto rows) and `ActionMenuViewModelSymbolTests` (updated).

## Review round 1 — external review dispositions (2026-09-08)

Findings from the external PR review (empirical headless probes on the PR head):

| # | Finding | Disposition |
|---|---------|-------------|
| R1 | Repeat kept clicking after the tracker lost the eyes — `StableCentroid` never goes null (the gap interpolator returns the frozen last-good point **unflagged** past `MaxGapInterpolationMs`, so the centroid tracker keeps consuming it and can even manufacture a new centroid after a proper fixation end). | **Fixed.** Core gate: `InteractionManager.NoteGazeSample(isValid, ts)` latches RAW sample validity (forwarded by `GazeEngine.OnGazePoint` before any interpolation — the only view where lost eyes are still observable). `FireRepeatTick` skips when validity age > 750 ms; `NoteGazeSample` auto-exits repeat mode at 2 s of lost gaze (the user cannot dwell the stop button with eyes away); a dead-stream guard skips ticks when no sample arrived since the previous tick (with no samples, validity age cannot grow). Controller `StateChanged` handling now marshals to the UI thread — the auto-exit fires on the gaze thread. Tests `RepeatMode_SkipsTicksWhenGazeIsLost_AndAutoExitsAfterTwoSeconds` and `RepeatMode_SkipsTicksWhenTheStreamDies` were written first and failed without the fix. |
| R2 | The ESC caregiver hatch is unreachable in the mode's own scenario: `ComponentDispatcher.ThreadFilterMessage` sees only EyeCtrl's own windows; overlays are `WS_EX_NOACTIVATE`; repeat clicks into the foreground app. | **Verified, fix deferred (follow-up issue).** Inherited verbatim from `DragCoordinator` (pre-existing on `main`), so fixing it here would drag Platform scope into this PR. Real fix: `WH_KEYBOARD_LL` via `SetWindowsHookEx` in `EyeCtrl.Platform` — ~150–250 lines: a dedicated hook thread with a message pump (LL hooks require the installing thread to pump), a callback that must `CallNextHookEx` immediately (a slow callback delays system-wide keyboard input — interacts with the Supervisor hang thresholds), and uninstall on teardown; LL hooks auto-remove on process exit, so a crash cannot leak one. Fixes both coordinators. Meanwhile: the gaze-reachable exits (stop button, R1's auto-exit) cover the lost-eyes scenario; the class doc no longer oversells ESC. |
| R3 | The escape path fired clicks: `EdgeButtonBarWindow` is click-through, so during the stop dwell the locked click passed through ⏹ onto whatever owns the right edge. | **Fixed.** `RepeatModeController.IsInStopButtonZone(point, center, radius)` (radius × 1.5 margin, covering the centroid's dwell-time trail) suppresses ticks aimed at the stop zone — broader than "dwell in progress": it covers pre-dwell hover too. Pinned by `IsInStopButtonZone_CoversTheButtonAndItsDwellMargin`. |
| R4 | Tick point (fixation centroid) ≠ cursor circle (live filtered point): a 45 px within-fixation correction left a ~45 px gap decaying ~0.45 px/sample — clicks land behind the circle for several ticks. | **Declined — ticks stay at the centroid.** (1) Consistency: every non-precise click in the app already lands at `StableCentroid ?? point` (`KeyboardInteractionDispatcher.HandleActionRequested`); repeat should not introduce a second click semantic. (2) Stability: the filtered-but-unfixated point jitters at sample rate; the slow centroid IS the stabilization — firing at the raw filtered point would spray clicks across the jitter radius, worse at short intervals. (3) The lag is bounded to sub-fixation-radius corrections (<50 px default) — larger shifts end the fixation and the tracker resets onto the new one. The cursor-vs-click mismatch is pre-existing app-wide (every non-precise click has it); if it matters it is `FixationCentroidTracker` convergence tuning on `main`, not special-casing here. |
| R5 | Small: `FireRepeatTick`'s state guard half unreachable; sub-500 ms intervals read as double-clicks; `Enter()` lacked a leaked-timer guard. | Dual guard kept deliberately (defense in depth — with auto-exit, the state half is now reachable mid-race). Interval row description warns about <500 ms double-clicks. `Enter()` early-returns when already active. |

## Rework round — owner direction (2026-09-09): toolbar Modes menu

The owner vetoed D1 ("two ring entries"): *"the continuous mode and repeat are better suited in a
different place. Probably a totally different mode switchable from the main toolbar."* Follow-up
decisions confirmed interactively: the toolbar button **opens a mode menu** (TD ModesMenu analog,
extensible for Drawing mode #10), the one-shot Repeat is **dropped entirely**, and the rework lands
**on #139 in place**. Revised ledger:

| # | Decision | Rationale | Rejected |
|---|----------|-----------|----------|
| D1′ | Entry UX = a **Modes toolbar button** opening a small mode-menu overlay; "Repeat clicks" is its first row (toggle) | Owner direction; mode-semantic actions belong at mode scope, not per-gaze-point in the action ring; the menu is where Drawing/Click-on-Fixation would later live | Ring entries (D1, vetoed); direct toggle button (considered, owner chose the menu); two buttons (incoherent — a toolbar-dwelled one-shot fires its click at the toolbar itself) |
| D16 | One-shot `RepeatLastAction` is **deleted**, not relocated | Its whole value was one-dwell proximity at the ring's resolved target; from the toolbar it would click the toolbar. `LastRepeatableAction` memory stays — it feeds the toggle's locked action | Keep in ring; toolbar one-shot |
| D17 | `InteractionManager.EnterRepeatMode()` — direct Core entry, guarded to `FreeLook` | Entry no longer rides an action-menu selection; strict guard keeps modes mutually exclusive (no silent state trampling from Paused/ScrollMode/Dragging) | Enter from any state; auto-unpause-then-enter |
| D18 | Modes menu = TaskSwitcher pattern: `ModesMenuViewModel : DwellOverlayViewModelBase, IRecipient<ModesMenuToggledMessage>`, `IsVisible` watched by the binding coordinator; small click-through panel under the toolbar, top-center | Reuses the proven toolbar-toggled-overlay seam end to end; no new coordinator — `ModesMenuToggledMessage` sent by the toolbar button, VM toggles itself | New coordinator; hot-edge summon (QuickActions is a bigger surface with its own controller) |
| D19 | The single row toggles repeat mode and closes the menu; while repeat is active the row shows it (`● on`) — dwelling it exits | Toggle-at-a-glance; the menu closes on ANY state change too (`StateChangedMessage`), so entries from elsewhere collapse it | Separate on/off rows |
| D20 | `RepeatModeController` registers the new `EnterRepeatModeMessage` (→ `EnterRepeatMode()`), keeping all repeat-mode message handling in one coordinator | Symmetry with its `ExitRepeatModeMessage` registration | GazeEngineAdapter as the handler |
| D21 | **Gaze-departure guard**: the first tick fires only after the stable centroid leaves an 80-px radius around the entry point | Entering from the toolbar means gaze is ON the toolbar/menu at entry; without the guard the first tick (+IntervalMs) can click EyeCtrl's own toolbar. 80 px clears the row without excluding nearby targets | Suppress-by-toolbar-rect (needs toolbar geometry plumbing); doubled first interval (crude) |
| D22 | The two `InteractionAction` members (11/12) and their ring wiring are removed, not deprecated | The branch is unmerged — no surface ever shipped; removal reverts the enum to PinNote-last with no value churn | Deprecate-then-remove |

Everything else stands: RepeatSettings + Advanced row, the validity gates, stop button, ESC hatch,
`RepeatModeController` cadence, OFF-by-default philosophy (now structural — nothing ring-visible
ships at all).
