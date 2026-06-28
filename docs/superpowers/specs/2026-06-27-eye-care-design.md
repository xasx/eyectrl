# Eye Fatigue Countermeasure — Design Spec

**Date:** 2026-06-27
**Status:** Approved (brainstorm complete)
**Type:** New feature

## Summary

A hybrid eye-fatigue countermeasure for EyeCtrl. A smart scheduler detects when a
user has been fixating too long (or a timer elapses) and surfaces a non-intrusive,
**dwellable** top-edge banner to confirm. On acceptance, a short **gaze-tracked
exercise** runs (rotated from a mix of five), ending with a brief, encouraging
**summary card**. The feature reuses EyeCtrl's existing gaze pipeline, dwell UX, and
overlay infrastructure. All novel logic lives in a new CI-testable project so it can
later be extracted as a plugin.

This positions EyeCtrl uniquely: existing break-reminder tools (Stretchly, Workrave,
EyeLeo) are blind timers, and existing gaze-games (Optics Trainer, Sensory Eye FX 2)
are standalone toys. EyeCtrl can both *detect* fatigue from gaze behavior *and* offer
gaze-driven relief in the same process the user already runs.

## Goals

- Reduce eye fatigue for users of an eye-controlled input tool — the population most
  at risk (sustained near-fixation, often reduced mobility to "just look away").
- Be a good gaze citizen: every interaction is **dwellable**, no mouse or keyboard
  required, no precision needed.
- Keep all novel domain logic in a framework-agnostic, CI-testable assembly.
- Establish a clean plugin seam so the feature can be extracted later.

## Non-Goals (v1)

- **Blink detection** and the `BlinkCoachExercise` — postponed to a paired future
  milestone (blink detection is needed to score the blink coach).
- **Fatigue-escalation** actively firing breaks early — the `FatigueMonitor` is
  *built* but ships **off by default**. It is promoted to default-on only after
  validation against real gaze data.
- **A casual gaze game** — out of scope for v1; the exercise runner is the
  foundation a game layer can be built on later.
- Clinical vision-therapy claims — exercises are framed as fatigue relief, not
  treatment.

## Architecture — Project Layout

**Approach:** Logic leaf + UI in App (chosen over a full vertical slice or single
Windows project). Mirrors the proven `EyeCtrl.LM` leaf pattern: one new `net10.0`
project holds all domain logic; only WPF rendering stays in App.

```
src/EyeCtrl.EyeCare/                    (NEW — net10.0, references Core)
├── EyeCtrl.EyeCare.csproj
├── IEyeCareService.cs                  (service contract the host calls into;
│                                        lives here in v1; relocates to Core for
│                                        full plugin swappability — see Future Milestones)
├── EyeCareService.cs                   (orchestrator: scheduler → confirmation → exercise)
├── EyeCareScheduler.cs                 (timer cadence + gaze modulation)
├── FatigueMonitor.cs                   (gaze-derived fatigue scoring heuristic)
├── IClock.cs                           (injectable clock for deterministic tests)
├── Exercises/
│   ├── IExercise.cs                    (target-path + duration + scoring contract)
│   ├── ExerciseBase.cs                 (shared: gaze-sample buffering, accuracy math)
│   ├── NearFarFocusExercise.cs
│   ├── SmoothPursuitExercise.cs        (circle / figure-8)
│   ├── Calibration202020Exercise.cs
│   ├── SaccadeExercise.cs
│   └── DirectionalSweepExercise.cs
├── ExerciseRunner.cs                   (drives the active exercise, emits scoring events)
├── Scoring/
│   ├── ExerciseResult.cs               (accuracy %, steadiness, reaction-time, duration)
│   └── ExerciseMetrics.cs              (per-frame gaze-vs-target deltas)
└── Settings/
    └── EyeCareSettings.cs              (cadence, durations, enabled, escalation threshold)

src/EyeCtrl.Core/
├── Interfaces/IEyeCareHooks.cs         (NEW — abstraction so EyeCare can request UI
│                                        without referencing WPF; the plugin seam host side)
├── GazeEngine.cs                       (MODIFIED — expose a raw-sample event carrying
│                                        validity; see "GazeEngine signal" below. The
│                                        existing CursorMoved event is insufficient.)
└── Settings/EyeCtrlSettings.cs         (MODIFIED — add EyeCare property + Validate branch;
                                         also extend SettingsViewModel.ResetAllSettings())

src/EyeCtrl.App/
├── Overlays/EyeCareBannerWindow.xaml(.cs)   (NEW — top-edge dwellable confirmation)
├── Overlays/EyeCareExerciseWindow.xaml(.cs) (NEW — renders target, gaze cursor, summary)
├── Services/EyeCareHooks.cs            (NEW — implements IEyeCareHooks, bridges to WPF)
└── Services/EventCoordinator.cs        (MODIFIED — wire EyeCareService into lifecycle)

tests/EyeCtrl.EyeCare.Tests/            (NEW — mirrors Core.Tests conventions)
```

**Why this split.** Every domain decision (when to break, how tired the user is,
which exercise to run, how well they did) is pure logic in a `net10.0` assembly →
unit-testable in CI without a display, exactly like `EyeCtrl.LM`. The App side is
thin rendering plus a hooks bridge. `IEyeCareHooks` (in Core) is the **plugin seam**:
a future plugin implements the logic; the host App always supplies the WPF.

## Trigger Model

The scheduler decides *when* to surface a break.

### GazeEngine signal (required modification)

The trigger model needs to know when the user is **actively gazing on-screen vs.
looking away** — the active-session clock and natural-break suppression both depend on
it. The existing `GazeEngine.CursorMoved` event **cannot** supply this: its signature is
`Action<PhysicalPoint, long>` (position + timestamp, no validity), and it fires the
*gap-interpolated* point — when the eyes are lost, `GapInterpolator` holds the last
on-screen point, so a `CursorMoved` subscriber sees continuous on-screen motion and can
detect neither "eyes lost" nor "gaze off-screen."

`GazeEngine` therefore gains one **additive** event that carries the raw,
pre-interpolation sample including its validity flag:

```csharp
// EyeCtrl.Core/GazeEngine.cs (NEW event, fired inside OnGazePoint before interpolation)
public event Action<GazePoint>? GazeSampled;   // GazePoint = (X, Y, TimestampMicroseconds, IsValid)
```

This is a non-breaking addition (new event, raised alongside the existing `CursorMoved`).
`EyeCareService` subscribes to `GazeSampled` for the active-session clock, away-suppression,
and the `FatigueMonitor` heuristics below. `CursorMoved` is still used for the live gaze
cursor in the exercise overlay (interpolated motion is desirable there).

### `EyeCareScheduler`

- Maintains an **active-session clock** — accumulates time only while the user is
  actively gazing on-screen (not idle, not paused, not looking away). Pausing the app
  (`InteractionManager` paused) freezes the clock. This is the 20-20-20 cadence base.
- **Cadence trigger:** fires a break request when active time since the last completed
  break ≥ `IntervalMinutes` (default **20**).
- **Suppression:** if the gaze provider reports the user already looked away for ≥
  `NaturalBreakSeconds` (default **30s**) within the last interval, the scheduled
  break is skipped — they took a natural micro-break, no need to nag.
- **Escalation (v1, gated):** if `FatigueMonitor.Score` crosses `FatigueThreshold`
  *before* the timer, fire early. **Off by default** (`FatigueMonitorEnabled = false`);
  promoted once the heuristic is validated.

### `FatigueMonitor` heuristic

Consumes the raw `GazeSampled` stream (above). Fixation/saccade structure is derived
inside the monitor from the position samples — it does **not** depend on `GazeEngine`'s
internal `FixationDetector` events, which are private and not exposed. Computes a rolling
0.0–1.0 score from cheap, defensible signals:

- **Sustained-fixation ratio** — % of recent samples inside a small radius (staring).
  High ratio ⇒ fatigue.
- **Saccade-range shrink** — the bounding box of recent saccades; a shrinking box
  means the eyes are "locking in."

(Blink-rate estimate is **not** in v1 — postponed with `BlinkCoachExercise`.)

Output is a score + a confidence flag. Thresholds are **user-tunable** so a
misbehaving heuristic never traps the user.

### Trigger flow

```
GazeEngine.GazeSampled ─┬─► FatigueMonitor ─► (score)
 (raw point + IsValid)  └─► EyeCareScheduler ─► OnBreakDue ─► EyeCareService
                                                                   │
                          (user paused?) ◄── InteractionManager.CurrentState
                          (looking away?) ◄── GazeSampled.IsValid run-length
                                                                   │
                                                       ─► IEyeCareHooks.ShowBanner()
```

### Invariants

- The scheduler **never** fires while `InteractionManager.CurrentState == Paused`,
  during active overlay input, or while another exercise/break is in progress.
- Everything is **time-based with real timestamps** (microsecond clocks already flow
  through `GazeEngine`) — testable by injecting a fake clock, no `Task.Delay` in logic.
- **No timers in Core logic.** The scheduler is driven by the gaze tick
  (`Update(timestampUs)`) plus a host-provided wall-clock, both injectable → fully
  deterministic unit tests.

## Settings — `EyeCareSettings`

Added to `EyeCtrlSettings.EyeCare`, validated in the existing `Validate()` chain.

| Property | Default | Purpose |
|----------|---------|---------|
| `Enabled` | `false` | Master switch (opt-in for v1) |
| `IntervalMinutes` | `20` | Cadence |
| `NaturalBreakSeconds` | `30` | Suppression threshold |
| `ExerciseDurationSeconds` | `45` | Per-exercise length |
| `BannerAutoSnoozeSeconds` | `15` | Banner auto-dismiss if ignored |
| `SnoozeMinutes` | `5` | Postpone after dismiss |
| `FatigueMonitorEnabled` | `false` | v1 gated escalation |
| `FatigueThreshold` | `0.75` | Escalation trigger |

## Exercises (v1 set — five)

Each is a moving target the gaze tracks; the runner **rotates** which exercise runs
each break (round-robin, never the same twice in a row). Rotation state is **in-memory**
(resets on app restart — persisting it across sessions is not worth the storage and adds
nothing for a daily driver). Target speed and path amplitude adapt to screen size and
`ExerciseDurationSeconds`.

1. **Near-Far Focus** — alternate gaze between a large near dot and a small far dot;
   dwell each ~3s. Exercises accommodation. *Score: focus-hold steadiness.*
2. **Smooth Pursuit** — a dot orbits a circle (round 1), then traces a figure-8
   (round 2). *Score: gaze-to-target distance variance (steadiness).*
3. **20-20-20 Calibration** — a centered marker; hold steady gaze ~20s. The "rest"
   exercise. *Score: fixation stability (drift).*
4. **Saccade Jumps** — one dot lights at a time at random positions; snap gaze to
   each ASAP. *Score: saccade latency (reaction time).*
5. **Directional Sweeps** — a dot sweeps edge-to-edge across horizontal, vertical,
   then diagonal paths. *Score: tracking accuracy across the full sweep.*

### Scoring

- **During the exercise:** no score is shown (decided: no pressure during a break).
  A live gaze cursor (green ring) tracks against the target so the user sees they're
  following.
- **After the exercise:** a 3-second summary card shows **two** exercise-specific
  metrics + an encouraging line, then auto-closes.

`IExercise` contract:

```csharp
public interface IExercise
{
    string Name { get; }
    PhysicalPoint GetTargetAt(long timestampMicroseconds);
    void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds);
    ExerciseResult Complete();   // computes metrics from recorded samples
}
```

## Overlays (WPF, in App)

All three are `OverlayWindow` subclasses (same base as `CalibrationWindow`).

### A. `EyeCareBannerWindow` — confirmation

- **Placement:** full-width strip along the top edge.
- **Buttons:** two **large** dwell circles — ▶ Start (accept) and ✕ Snooze. Minimum
  diameter matches or exceeds the app's existing activation/dwell targets, so they
  remain easy to hit with imprecise gaze.
- **Behavior:** reuses `DwellDetector` for the buttons — identical dwell UX to the
  rest of the app. Auto-snoozes after `BannerAutoSnoozeSeconds`.
- **Non-intrusive:** work area below the banner is dimmed (~45%) but the window stays
  click-through; it never steals focus or input.

### B. `EyeCareExerciseWindow` — the exercise

- **Full-screen** canvas (needs the room for target paths).
- Renders the moving target + a live gaze cursor (green ring).
- HUD: exercise name + countdown timer only (no live score). Progress bar at bottom.
- Small skip button (corner) to abort.
- Gaze input taps the existing `GazeEngine.CursorMoved` stream — no new gaze plumbing.

### C. Post-exercise summary

- Centered card over a dimmed screen.
- Two metrics + an encouraging line ("Nice work!").
- Auto-closes in 3s; scheduler then resets its cadence clock.

## Testing & Integration

### Testability (CI, no display)

| Unit | How tested | Key seam |
|------|------------|----------|
| `EyeCareScheduler` | Inject fake `IClock` + feed scripted `GazePoint` samples (`IsValid` runs = looking-away) and `InteractionState`. Assert break-due timing, suppression-on-natural-break, pause-freeze. | `Update(timestampUs)` fed from `GazeSampled`, no `Task.Delay` |
| `FatigueMonitor` | Feed recorded `GazePoint` sequences (staring vs. healthy). Assert score crosses threshold only on the staring sequence. | Pure function of `GazeSampled` points + timestamps |
| `ExerciseRunner` + each `IExercise` | Feed a recorded gaze path that tracks the target → assert accuracy ~100%. Offset path → lower score. Reaction-time tests feed timestamped acquire events. | `IExercise.GetTargetAt(tUs)` + `RecordGaze(point, tUs)` |
| `Scoring` | Pure math — feed deltas, assert `ExerciseResult`. | No dependencies |

New test project `tests/EyeCtrl.EyeCare.Tests/` mirrors `EyeCtrl.Core.Tests`
conventions (`XxxTests`, `_`-separated PascalCase, NFluent, private nested fakes — no
mocking libs). `EyeCtrl.EyeCare` declares `InternalsVisibleTo` to it (same as LM/Core).
All domain logic lives here and runs headless — no display required.

Any App-side test that drives the banner/exercise/summary windows must be tagged
`Category=UI` (same as existing `App.Tests`), since those depend on a real window pump
and fail in headless CI; the headless-safe run is `dotnet test --filter "Category!=UI"`.

### App-side wiring

- `AppBootstrapper`: register `EyeCareService` (singleton), the WPF windows,
  `EyeCareHooks`.
- `EventCoordinator`: subscribe `EyeCareService` to `GazeEngine.GazeSampled` (scheduler +
  fatigue scoring); the exercise overlay separately taps `CursorMoved` for the live gaze
  cursor. Wire `OnBreakDue → hooks.ShowBanner`, `banner.Accept → hooks.ShowExercise`,
  `exercise.Complete → hooks.ShowSummary`.
- `EyeCareSettings` added to `EyeCtrlSettings.EyeCare` + the `Validate()` chain, **and**
  enumerated in `SettingsViewModel.ResetAllSettings()` (project convention: both must list
  every sub-setting).
- Settings UI: a new section in `SettingsWindow` for the `EyeCareSettings` knobs. All knobs
  map to existing `Slider`/`Toggle` `SettingItemViewModel` types — no new item type needed.

### Lifecycle invariants (must hold)

- Banner/exercise/summary **never** appear while `InteractionManager` is paused or
  another modal overlay (action menu, keyboard, manual selection) is active — same
  guard the dwell pipeline uses.
- On app shutdown, any in-flight exercise is torn down cleanly via `IDisposable`
  (mirrors `GazeEngine.Dispose` / `EventCoordinator.Dispose`).
- Toggling `Enabled` off mid-session immediately hides any visible banner and cancels
  the scheduler.

## Future Milestones

- **Blink detection + `BlinkCoachExercise`** — paired; enables blink-rate as a
  `FatigueMonitor` signal too.
- **Promote `FatigueMonitor` escalation to default-on** after validation against real
  gaze data.
- **Casual gaze game** — built on the exercise runner foundation.
- **Plugin extraction** — `IEyeCareHooks` (in Core) is already the host-side seam;
  for a fully swappable plugin, `IEyeCareService` relocates from `EyeCtrl.EyeCare` to
  `EyeCtrl.Core` so a plugin assembly can be dropped in without App recompiling.

## Open Questions

None — all resolved during brainstorm.
