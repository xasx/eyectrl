# Toolbar Auto-Hide on Gaze

**Date:** 2026-07-09
**Status:** Draft
**Author:** EyeCtrl Team

## Problem

When a real eye tracker is driving the app, the always-visible docked toolbar (`ToolbarWindow`) sits on screen permanently, even when the user is not using it. For an eye-gaze-driven workflow this is wasted screen real estate and a dwell-miss hazard: the toolbar's buttons are large dwell targets, and they remain active whether or not the user wants to interact with them.

The toolbar should get out of the way on its own, and come back through a deliberate gaze gesture.

## Goals

- The toolbar hides itself automatically when gaze is not on it.
- The toolbar returns via a deliberate, directional "hot edge" gaze gesture: looking **beyond the screen edge in the direction the toolbar is docked**.
- The behavior is on by default, but **only when a real eye tracker is active**. Mouse/test mode keeps the current always-visible toolbar.
- The user always knows the state of the system — in particular, they are never left with no feedback when gaze is paused and the toolbar (with its resume button) is hidden.

## Non-Goals

- No user-facing settings toggle in this iteration. The code is structured so one can be added later (it reads enablement from a single `IsEnabled` property), but none is shipped now.
- No change to mouse/test-mode behavior. The toolbar remains always visible there.
- No change to the toolbar's contents, dock position, or dwell behavior when visible.
- No auto-hide for any overlay other than the toolbar.
- **Auto-hide only applies when the toolbar is docked (`ToolbarSettings.IsDocked == true`).** When floating, the toolbar is always visible and the controller stays disabled. Rationale: the directional hot-edge summon targets a screen edge; a floating toolbar has no associated edge, and a floating toolbar in the middle of the screen auto-hiding would be surprising rather than helpful. If the user toggles `IsDocked` at runtime, `OnSettingsChanged` re-evaluates the controller's enablement (see Edge cases #8).
- **Single (primary) display only.** The screen-size and off-screen math assume the primary monitor (`_screenWidth`/`_screenHeight`). Multi-monitor off-screen semantics are not modeled. Fine for the PCEye 5 daily-driver setup; not worth addressing now.

## Terminology

- **Eye-tracker mode** — a real eye tracker (not the mouse fallback) is driving gaze. Detected as `App.ActiveProviderType != GazeProviderType.Mouse`. There is no single `IsEyeTracking` flag today; this design introduces `App.ActiveProviderType` (see Wiring).
- **Hot edge** — the screen edge in the direction the toolbar is docked. Looking past it is the summon gesture.
- **Hysteresis** — run-length timers that debounce state transitions so brief glances and blinks do not flicker the toolbar.

## Design

### Approach

A dedicated controller, `ToolbarAutoHideController`, lives in the App layer (`src/EyeCtrl.App/Services/`). It subscribes to the raw gaze stream, owns a small hysteresis state machine, and drives the toolbar window's `Hide()`/`Show()` plus the pause-indicator overlay. It has one input (`OnSample(GazePoint)` on `GazeEngine.GazeSampled`) and one effect (window visibility).

Rejected alternatives:

- **Logic in `ToolbarViewModel`** — the VM receives *filtered* `GazeCursorMovedMessage`s on the UI thread, not the raw `GazeSampled` stream, and the per-sample dispatch is short-circuited after toolbar init (`EventCoordinator.SetupGazeRouting`). Off-screen coordinates (needed for the summon gesture) are not reliably available on that path. The VM layer is also the wrong home for eye-tracker-mode gating and physical-screen-size knowledge.
- **Extending `EyeCareScheduler`** — the eye-care subsystem is a leaf project referencing Core and cannot command an App-layer overlay without breaking the dependency direction. Its "away" semantics (invalid-gaze, seconds-scale natural breaks) are also quite different from a sub-second directional hot-edge gesture.

### State machine

The controller runs a per-sample state machine with two states. All inputs are computed on the gaze thread against cached geometry (see Threading).

**Per-sample inputs:**

- `isOffScreen` — the **summon** condition. `gaze.IsValid && BeyondDockedEdge(gaze)`, where `BeyondDockedEdge` depends on `ToolbarSettings.DockedEdge`:

  | Docked edge | Summon when valid gaze is past |
  |---|---|
  | Top | the top edge: `Y < -margin` |
  | Bottom | the bottom edge: `Y > height + margin` |
  | Left | the left edge: `X < -margin` |
  | Right | the right edge: `X > width + margin` |

  Only the docked edge summons. Looking past any other edge does nothing. `margin` (default `50` physical px) is a dead-band so a sample resting at the bezel does not flake. **Invalid samples never count as off-screen** — this avoids blink false-positives (the explicit design choice).

  > **⚠ Critical assumption — must be empirically confirmed before implementation.** This summon path works *only if* the Tobii Stream Engine emits **valid** samples with out-of-range normalized coordinates (so `_screenWidth * x` lands past the edge) when the user looks beyond the bezel. In practice, when the eyes move past the screen edge the tracker often *loses the eyes and marks the sample invalid* rather than extrapolating past 1.0 — and the provider then sends `GazePoint(0,0,…,IsValid:false)` (`TobiiStreamEngineProvider.cs:148-163`), which this design explicitly excludes. If that is what happens on the target device, the off-screen-valid summon **never fires**, and since the only exit from `HIDDEN` is summon, the user is stranded (worst case: paused + hidden + no resume button + can't summon).
  >
  > **Action required before building the state machine:** capture raw `GazeSampled` values on the actual hardware (PCEye 5) while deliberately looking past each edge, and confirm `IsValid==true` samples with out-of-range coords arrive. The implementation plan includes a throwaway logging probe for this. **If the assumption does not hold**, the summon signal must be redesigned — candidate fallbacks: (a) a valid sample dwelling in an edge *band just inside* the screen (e.g. `X < margin` for a top-docked toolbar), or (b) a run of invalid samples immediately following a valid sample near the edge. The `isOffScreen` definition above is written so only this one predicate needs to change.
  >
  > **Stranding guardrail (regardless of assumption outcome):** a tray-menu "Show toolbar" item is added (`TrayIconManager` builds its menu inline; current items are Pause / Settings / Exit) so the user always has a hardware-independent way to re-show a hidden toolbar. This is described in Wiring.

- `isOnToolbar` — `gaze.IsValid && toolbarRect.Contains(gaze.X, gaze.Y)`, using the cached toolbar rectangle in **physical pixels**. Note WPF exposes `Toolbar.Left/Top/ActualWidth/ActualHeight` in device-independent (logical) units while `GazePoint` coordinates are physical pixels; the cached rect is built by converting the logical rect via the existing `DpiHelper` (e.g. `DpiHelper.LogicalToPhysicalX/Y`) so both sides of the comparison are physical pixels. Stored as a `PhysicalRect`. **`PhysicalRect` currently has no `Contains` method** (`PhysicalRect.cs` is a `readonly record struct` with only `CenterX/CenterY/Center`); the implementation adds `bool Contains(double x, double y)` to `PhysicalRect` (Core) as part of this work. (The invalid-exclusion rule also defuses the `GazePoint(0,0,…,false)` sentinel — because invalid samples are skipped before any hit-test, that sentinel can never masquerade as "gaze at the top-left corner.")

**States:**

```
                 [VISIBLE]   ← initial state when feature is active
                    │  ▲
   gaze off toolbar continuously   │  │  Show() (transition, see below)
   for HIDE_DELAY (5s)             │  │
                    ▼  │
                  [HIDDEN]
              ▲                │
     Hide()   │   isOffScreen continuously for SUMMON_DELAY (400ms)
              └────────────────┘
```

- `VISIBLE` → toolbar shown. If `!isOnToolbar` continuously for **`HIDE_DELAY`** (`5000ms`) → `Hide()`, transition to `HIDDEN`.
- `HIDDEN` → toolbar hidden. If `isOffScreen` continuously for **`SUMMON_DELAY`** (`400ms`) → `Show()`, transition to `VISIBLE`.

**Hysteresis rules (prevent flicker):**

- Both timers are run-length: any sample that breaks the condition resets that timer to zero. A glance back onto the toolbar resets the hide countdown; a sample that returns valid-and-onscreen (off the hot edge) resets the summon countdown.
- The only exit from `HIDDEN` is the off-screen summon. `isOnToolbar` is **not** evaluated in `HIDDEN` (otherwise the toolbar would pop back the instant the user's gaze wanders across its former location).
- Invalid samples reset neither timer.

**Defaults** (private `const`, easy to tune):

```
HIDE_DELAY    = 5000 ms
SUMMON_DELAY  = 400 ms
OffScreenMarginPx = 50
```

`HIDE_DELAY` is intentionally forgiving (5s) so the toolbar does not hide while the user is reading the screen near it. `SUMMON_DELAY` is fast enough to feel responsive yet long enough to reject a single noisy off-edge sample. **Note on feel:** because "idle" is defined as "gaze not on the toolbar," in eye-tracker mode the toolbar *will* hide ~5s into normal active screen use — not only when the user walks away. This is the intended behavior (the goal is to reclaim screen real estate whenever the toolbar isn't being used), but it is aggressive; the value is a `const` and should be tuned during hardware validation. If it proves too aggressive in practice, the natural fix is either raising `HIDE_DELAY` or switching the idle definition — both are localized changes.

### Pause indicator

When the toolbar is hidden and the app is paused (`InteractionState.Paused`), a **thin edge bar** is shown along the docked edge so the user is not left with no feedback (the toolbar — and its resume button — is off-screen). The bar is hidden whenever the toolbar is visible, or when the app is not paused.

This doubles as a discoverability hint: the edge bar sits on the hot edge the user should look past to summon the controls.

Rendering: a new minimal overlay window, `PauseIndicatorWindow`, derived from `OverlayWindow` (same base as `ToolbarWindow`). `OverlayWindow` defaults to click-through / non-focusable (`IsClickThrough = true`), which is correct for a passive indicator — the implementation confirms it keeps that default. A single colored rectangle, positioned along the docked edge via the same `SetMode(isDocked, edge)` positioning approach already used by the toolbar. Toggled with `.Show()`/`.Hide()` alongside toolbar state transitions.

### Threading model

`GazeSampled` fires on the gaze thread; WPF forbids cross-thread reads of window properties. The split:

- **Gaze thread (per sample):** compute `isOffScreen` and `isOnToolbar` against the **cached** toolbar rect + screen size. Advance/reset hysteresis timers. Decide the target visibility. All reads are against cached primitives — no cross-thread window-property access.
- **UI thread (only on a state transition):** `_context.Dispatcher.BeginInvoke` → call `OverlayManager.Toolbar.Hide()`/`.Show()`, toggle the edge-bar overlay, then **refresh the cached rect** (read `Toolbar.Left/Top/ActualWidth/ActualHeight` on the UI thread) so subsequent gaze-thread hit-tests are accurate. The dispatcher hop happens only when the decision flips — once per hide/summon, not per sample.

**When the cached rect + active edge are refreshed** (all on the UI thread):

1. Once at startup after the toolbar is shown and laid out (the controller's `Initialize` enqueues a deferred read).
2. After every `Show()` (rect may differ if the dock moved). Read in logical units and converted to physical via `DpiHelper`.
3. When `ToolbarSettings` dock/size changes — `EventCoordinator.OnSettingsChanged` already rebuilds toolbar geometry; it additionally pings the controller to refresh its cache and its active edge direction.

## Wiring

- **Construction.** The controller is created in `EventCoordinator.Initialize`, immediately after `SetupGazeRouting()`. It receives everything it needs from `EventCoordinationContext`: `GazeEngine` (for the `GazeSampled` subscription), `OverlayManager` (for the `Toolbar` window and to host the edge bar), `Dispatcher`, `InteractionManager` (for pause state), `Settings` (for the dock edge).

- **Mode gating.** A new static property is added to `App`: `public static GazeProviderType ActiveProviderType { get; private set; }`, set in `App.OnStartup` from the existing local `activeProviderType` (App.xaml.cs:79), right alongside where `IsTestMode` is set. This mirrors the `App.IsTestMode` precedent. The controller's `IsEnabled` is `App.ActiveProviderType != GazeProviderType.Mouse && ToolbarSettings.IsDocked`. When disabled, the controller subscribes to nothing and the toolbar stays always visible — exactly today's behavior.

- **Subscription.** The controller subscribes to `GazeEngine.GazeSampled` (the raw, pre-interpolation event carrying `IsValid` and physical-pixel coords) — the same hook eye-care uses (App.xaml.cs:150-158). It does **not** subscribe to `CursorMoved` (filtered/clamped) or ride `EventCoordinator.SetupGazeRouting` (which short-circuits after toolbar init).

- **Pause reactivity.** To react to pause changes promptly rather than only on the next sample, the controller subscribes to `StateChangedMessage` via the messenger. A paused→resumed transition hides the edge bar immediately even if the toolbar remains hidden.

- **Lifecycle.** `EventCoordinator.Dispose` unsubscribes `GazeSampled`, unregisters the messenger handler, and disposes the edge-bar overlay.

- **Stranding guardrail — tray "Show toolbar" item.** A new menu item is added to the tray icon's context menu (`TrayIconManager` builds its menu inline; current items are Pause / Settings / Exit). The item is wired per the existing pattern: a `ToolStripMenuItem`, its `Click` handler marshals onto the WPF dispatcher and commands the controller to force-show the toolbar (and hide the edge bar). This guarantees the user can always recover a hidden toolbar through the mouse-driven tray menu, independent of whether the gaze summon gesture works on the hardware — so a mis-tuned or unfireable gesture can never brick access. The item is shown whenever auto-hide is active (eye-tracker mode + docked); hidden otherwise so it doesn't clutter the menu in mouse mode.

## Edge cases

1. **Mouse/test mode** — controller never hides (gated on provider type). Toolbar is always visible, as today.
2. **Keyboard or settings window open** — auto-hide is **suspended** while either is open. The `_keyboardVisible`/`_settingsOpen` flags are `private volatile bool` on `EventCoordinator` (`EventCoordinator.cs:41-42`), not accessible to a standalone controller. Mechanism: `EventCoordinator` calls `controller.Suspend(true)` when either flag becomes true and `controller.Suspend(false)` when both clear — mirroring the existing `OnSettingsChanged` ping pattern already used to refresh the cached rect. While suspended: no hide transitions occur; if currently hidden, the toolbar is force-shown so the user is not stranded. Re-evaluates when they close.
3. **Calibration running** — suspended when `_context.CalibrationService.IsSessionActive` (`CalibrationService.cs:45`) is true. (The eye-care wiring does not itself check calibration — it delegates to `EyeCareService`; here we name the explicit gate.) No hide/summon during calibration.
4. **Toolbar re-summoned while paused** — edge bar hides, the full toolbar (with the resume button) shows.
5. **Invalid samples** — ignored for both hide and summon. They reset neither timer, so a brief blink mid-idle-count does not restart the 5s.
6. **Gaze resumes onto toolbar** — resets the hide countdown (no flicker).
7. **App startup** — controller starts in `VISIBLE`; it waits one layout pass before caching the rect (no premature hide).
8. **`IsDocked` toggled at runtime** — when the user changes docked/floating or the dock edge in settings, `OnSettingsChanged` fires. The controller re-evaluates `IsEnabled` (`IsDocked` may have flipped to false → controller disables, force-shows the toolbar), and if still enabled refreshes its cached rect and active edge direction.

## Settings

No user-facing toggle is shipped in this iteration. The controller reads enablement from `App.ActiveProviderType != Mouse` via a single `IsEnabled` property, which is trivially rewired to a future `ToolbarSettings.AutoHideOnLookAway` field when the toggle is wanted. The dock edge is read from `ToolbarSettings.DockedEdge` at init and refreshed on settings change. If a toggle is added later, it would be exposed on the Toolbar tab in `SettingsViewModel.BuildTabs` (around line 146) and propagated live via `OnSettingsChanged`; none of that is built now.

## Testing

Following repo conventions (NFluent 3.1.0, private nested fakes, `XxxTests` underscore-PascalCase naming, no mocking library).

The controller's gaze-thread decision logic is a pure method over injected state (current rect, screen size, active edge, pause state, suspension flags), so it is unit-testable without a real dispatcher or window — the dispatcher and overlay calls are injected seams. Concretely, the controller depends on a small `IToolbarVisibility` interface (`void Show()`, `void Hide()`, plus an edge-bar show/hide pair) implemented by the real `OverlayManager` wiring and by a recorded-call fake in tests. (This interface is created as part of this work; delegates `Action show/hide` are a lighter alternative if the surface stays this small, but the interface leaves room for the rect-refresh callback.)

Cases:

- Hide after sustained idle (gaze off toolbar beyond `HIDE_DELAY`).
- No hide while gaze is on toolbar.
- Directional summon, one case per docked edge (Top/Bottom/Left/Right).
- No summon when looking past a non-docked edge.
- Summon requires sustained off-screen for `SUMMON_DELAY` (a single off-edge sample does not summon).
- Blink (invalid sample) does not reset the idle countdown.
- Suspended while keyboard open; suspended while settings open; force-show if hidden when a modal opens.
- Suspended during calibration.
- Pause while hidden shows the edge bar; resume hides it.
- Summon while paused hides the edge bar and shows the toolbar.
- Mouse mode never hides (controller disabled).

No UI tests are required — the state machine is pure logic and the edge-bar window is a trivial single-rectangle overlay. All coverage is unit tests.

## Open questions

1. **[BLOCKING] Does the Tobii Stream Engine emit valid samples past the screen edge?** This is the load-bearing assumption of the off-screen summon gesture (see the "Critical assumption" callout in Design → State machine). It cannot be settled on paper — it requires capturing raw `GazeSampled` output on the actual PCEye 5 while looking beyond each edge. The implementation plan's first step is a throwaway logging probe to answer this. The summon-predicate is isolated so that, if the answer is no, only `isOffScreen` needs to change (edge-band-inside or invalid-run fallback). The tray "Show toolbar" fallback is built regardless, so a negative answer never bricks access. All other design decisions (trigger semantics, idle definition, defaults/scope, hide rendering, pause interaction, pause-indicator form, directional summon) are resolved.

2. **[TUNING, post-hardware] `HIDE_DELAY` feel.** 5s of "gaze not on toolbar" is aggressive in eye-tracker mode (hides during normal active use). Confirm the feel during hardware validation; the value is a `const` and is a localized change.
