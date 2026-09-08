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
