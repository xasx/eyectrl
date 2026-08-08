# Core End-to-End Test Plan — Design

**Date:** 2026-08-08
**Status:** Approved design, not yet implemented

## Problem

The app's reason for existing — a person looks at a thing and it gets clicked — is not
covered by any test at any layer. The unit suites are large (873 `[Fact]`/`[Theory]`
attributes in `EyeCtrl.Core.Tests` alone) and the state machine is well covered in
isolation, but nothing proves the assembled system dispatches a real click at the right
screen coordinate.

The existing end-to-end layer is `tests/EyeCtrl.App.Tests`, using FlaUI + UIA3, gated
behind `[Trait("Category", "UI")]` and the `EYECTRL_UI_TESTS=1` opt-in
(`tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs:44`). It is five classes and thirteen
tests, and every assertion is structural: a window exists, the toolbar has four buttons,
the cursor circle has an ellipse, a dwell toggles pause, the settings window has eight
tabs. Useful as a smoke layer. It says nothing about clicks, coordinates, typing, drag,
or scroll.

Two absences matter most for a daily driver:

- **No test proves a click lands where the user looked.**
- **No test proves a click is *not* dispatched** when it shouldn't be — while paused, or
  when a fixation is abandoned before the activation dwell completes. A spurious click is
  worse than a missed one.

## Constraints

**The user operates this machine solely by eye tracking.** Any test that drives the OS
cursor takes away their only input channel for the duration of the run. This is the
governing constraint on the whole design, not a footnote.

**Real input is unavoidable for a truthful test.** `Win32InputSimulator` dispatches via
`SendInput` with `MOUSEEVENTF_ABSOLUTE`
(`src/EyeCtrl.Platform/Input/Win32InputSimulator.cs:36`) — system-wide input landing in
whatever window occupies those coordinates. Faking that seam produces a test that no
longer measures the thing in question.

**Gaze cannot be injected into a launched app.** In `--test-mode` the gaze provider is
hard-coded to `MouseGazeProvider` (`src/EyeCtrl.App/Services/AppBootstrapper.cs:88-89`),
which polls the real OS cursor via `GetCursorPos`
(`src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs:66`). There is no pipe, file, or flag
that feeds synthetic gaze. Therefore "scripting gaze" means "moving the real cursor",
and the app then moves the cursor again itself from `GazeEngine.OnGazePoint`.

**Named pipes are machine-global.** A concurrent `dotnet test` in any of the repo's
worktrees steals pipe names and produces failures that read as flaky product code
(`AGENTS.md`, Testing Conventions). Runs must happen on a quiet box.

## The point-resolution contract

Worth stating explicitly, because it is the single most valuable thing for e2e to pin
and it is resolved in two layers that can disagree.

`InteractionManager.OnActionSelected` picks the target
(`src/EyeCtrl.Core/Processing/InteractionManager.cs:132-142`):

| State / mode | Target point |
|---|---|
| `ActionMenuVisible` + `AutoSelection` + resolved target | `AutoSelectionTarget.CenterPoint` |
| `ActionMenuVisible` otherwise | `FixationPoint` |
| `ManualSelectionActionMenuVisible` | `ManualSelectionDwellPoint` |

Then `EventCoordinator.HandleActionRequested` **overrides** it
(`src/EyeCtrl.App/Services/EventCoordinator.cs:1374`):

```csharp
var pt = msg.IsPreciseTarget ? msg.Point : (_context.GazeEngine.StableCentroid ?? msg.Point);
```

So for the non-precise path the click uses `GazeEngine.StableCentroid`, discarding the
`FixationPoint` the state machine chose. The two are computed by different components
from different histories. Whether they agree in practice is an open question that only
an end-to-end coordinate assertion can answer.

## Architecture

Four components, three tiers.

### Components

**`tests/EyeCtrl.TestTarget`** — a WPF app acting as the oracle. Full-screen, records
every real mouse and keyboard event it receives (button, screen coordinates, timestamp,
wheel delta) and streams them over a named pipe. Follows the repo's existing pipe
convention: a `DefaultPipeName` constant plus a test-injected GUID-suffixed name, the
pattern `ExternalDwellService` and `HeartbeatPublisher` already use (`AGENTS.md`, #76).

Chosen over driving real apps via UIA (brittle against Store-app tree changes, cannot
verify *where* a click landed, leaves the user's real applications in an unknown state
on failure) and over a `WH_MOUSE_LL` hook recorder (proves input was sent, not that a
window received it). The hook recorder remains the fallback if overlay z-order makes a
full-screen target unworkable.

**`GazePath` driver** — replaces `AppFixture.SimulateDwellAt`
(`tests/EyeCtrl.App.Tests/Fixtures/AppFixture.cs:248`), which teleports the cursor and
holds. Instead it emits a *sequence* of cursor positions over time modelling
saccade → fixation → dwell, so `FixationDetector` and `FixationCentroidTracker` see a
signal shaped like real gaze. A teleport may satisfy `DwellDetector` while producing a
centroid unlike anything the real pipeline computes, which would make J2's assertion
meaningless.

**Safety harness** — the existing `EYECTRL_UI_TESTS=1` opt-in, plus:

- cursor position saved before the run and restored after, including on crash or abort;
- a hard wall-clock cap on the whole run that kills `EyeCtrl.App` and the target on expiry;
- a low-level keyboard hook watching for a *physical* (non-injected) Esc, so a person at
  the keyboard can abort early.

The cap is the actual safety net. Esc is a convenience, because during a supervised run
the user has no input at all.

**Desktop isolation** — `CreateDesktop` places the app, the target, and the runner on
their own desktop object. `SendInput` and UIA are both per-desktop, so the run should be
invisible to the user's session.

### Tiers

**Tier 0 — isolation spike.** Not tests. A throwaway probe answering three questions: does
WPF render on an alternate desktop (no DWM composition there), can UIA read that tree, and
does `SendInput` stay contained. Gates whether tier 1 runs supervised or unattended.
Failure costs nothing — supervised mode is the fallback.

**Tier 1 — depth.** Eleven journeys against the recording target, asserting on real
recorded events and their coordinates.

**Tier 2 — breadth.** One shallow smoke per feature area, catching "area X is dead on
startup".

## Tier 1 journeys

| # | Journey | Asserts | Pins |
|---|---|---|---|
| J1 | Left click, `SelectionMode=None` | Exactly one LeftDown/LeftUp pair at the fixation point | The flagship path |
| J2 | As J1, but the gaze path to the radial button is far from the target | Click still lands at the fixation point, not at the menu button | `StableCentroid` override, EventCoordinator:1374 |
| J3 | Right click / double click | RightDown+RightUp; two Left pairs within the system double-click time | EventCoordinator:1394-1395 |
| J4 | `AutoSelection`, gaze deliberately off-centre of a UIA button | Click lands at the **control centre**, not the gaze point | InteractionManager:135-136 |
| J5 | `ManualSelection` zoom → precise dwell → click | Click lands at `ManualSelectionDwellPoint`, *not* overridden by `StableCentroid` | The `IsPreciseTarget=true` branch |
| J6 | Drag | LeftDown at origin, movement, LeftUp at destination | EventCoordinator:832 + `InteractionManager.CompleteDrag` |
| J7 | Scroll mode (joystick) | Wheel events at the anchor with correct sign, and they **stop on exit** | `ScrollWorker` |
| J8 | Discrete ScrollUp/Down/Left/Right | Wheel and h-wheel deltas with correct sign | EventCoordinator:1397-1400 |
| J9 | Keyboard types into the target's text field | Recorded keystrokes and resulting text match | Keyboard → real input |
| J10 | **Paused**: run the full J1 script | Target recorded **zero** events | Pause suppresses dispatch |
| J11 | **Abandoned fixation**: look away before the activation dwell completes | Target recorded **zero** events | No spurious clicks |

J10 and J11 are the highest-value tests in the set and the two that must not be dropped
for schedule.

### Coordinate tolerance

`SendInput` normalises to a 0–65535 grid and `DpiHelper` scaling sits in the path, so
assertions use a tolerance rather than exact equality. **Start at ±2 physical pixels and
pin the real figure from the first green run.** Do not encode a tolerance that has not
been observed; if the true error is larger, that is a finding about the pipeline, not a
number to quietly widen.

## Tier 2 breadth

One smoke per area: toolbar, keyboard → real text, settings persistence round-trip
(change a setting, restart, assert it held), zoom / manual selection entry, quick
actions, eye care, adaptation, pause, tray.

**Task switcher — fixed before this plan starts.** `ToolbarViewModel` defines eight buttons
(`src/EyeCtrl.ViewModels/ToolbarViewModel.cs:66-76`); `ToolbarWindow.Initialize` rendered
five, omitting `Calibrate`, `TaskSwitcher` and `QuickActions`. Two of those three are
reachable elsewhere — `Calibrate` from the settings window
(`src/EyeCtrl.App/Views/SettingsWindow.xaml.cs:1207`) and `QuickActions` from the hot-edge
summon controller (`src/EyeCtrl.App/Services/EventCoordinator.cs:704`) — so their absence
from the toolbar is a design choice. `TaskSwitcher` had no other sender anywhere, making the
feature unreachable; its button has been added.

The reason it went unnoticed generalises, and matters for this plan: the only test that
counted toolbar buttons was `Category=UI`, skipped by default, and its expected count was
itself stale by one. A skipped test is not a guard. `ToolbarButtonDriftTests` now enforces
both directions headlessly — every rendered button must have a view-model entry and an
activation case, and every view-model button must be rendered or carry a recorded
alternative entry point.

## Execution

### Runnable today

Headless — no display, no cursor. Safe at any time:

```bash
dotnet test --filter "Category!=UI"
```

CI runs the same filter, adding `--no-build` after a preceding build step
(`.github/workflows/ci.yml:54`).

Existing UI tests — **takes the cursor**, and is a no-op unless `EYECTRL_UI_TESTS=1`:

```bash
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"
```

Three known ways a run lies, all documented in `AGENTS.md`: test output is German, so
per-test failures appear as `Fehler <FQN>` and grepping for "Failed" reports green on a
red run; concurrent suites collide on machine-global pipe names; and
`ExternalDwellServiceTests` fails 8/8 when run alone because it depends on another test
in the same host having populated `Application.Current.Resources`.

### Verified baseline

Recorded from an actual run, not derived. `dotnet test --filter "Category!=UI"` at commit
`c1ad9bb`, Debug, on a quiet box (no competing `dotnet`/`testhost`/`EyeCtrl` processes),
2026-08-08. Exit code 0, ~25 s wall clock.

| Assembly | Passed | Skipped | Failed |
|---|---:|---:|---:|
| EyeCtrl.Core.Tests | 1065 | 1 | 0 |
| EyeCtrl.App.Tests | 349 | 0 | 0 |
| EyeCtrl.ViewModels.Tests | 257 | 0 | 0 |
| EyeCtrl.Controls.Tests | 66 | 0 | 0 |
| EyeCtrl.Platform.Tests | 55 | 0 | 0 |
| EyeCtrl.EyeCare.Tests | 48 | 0 | 0 |
| EyeCtrl.Supervisor.Tests | 45 | 0 | 0 |
| EyeCtrl.LM.Tests | 20 | 0 | 0 |
| EyeCtrl.Sdk.Abstractions.Tests | 18 | 0 | 0 |
| EyeCtrl.Sdk.Wpf.Tests | 3 | 0 | 0 |
| EyeCtrl.Sdk.WinUI.Tests | 3 | 0 | 0 |
| **Total (11 assemblies)** | **1929** | **1** | **0** |

The single skip is `SettingsPropertyValidationTests.Validation_PerformsQuickly`, a
timing-sensitive test skipping itself.

`EyeCtrl.Benchmarks.Tests` is **not** in this figure — it sits outside the solution file
deliberately and CI runs it as a separate step (`.github/workflows/ci.yml:57`). A full
CI-equivalent local run needs that second command as well.

Counts were read from the German per-assembly summary counters
(`Fehler: 0, erfolgreich: N, …`), with a separate check for zero `Fehler <FQN>` per-test
failure lines. Parsing this output for the English word "Failed" reports green on a red
run.

So the plan builds on a clean floor: the e2e gap is an absence of tests, not a backlog of
failing ones.

### New project

Tier 1 lives in a new `tests/EyeCtrl.E2E.Tests` with its own `Category=E2E` trait, not
folded into `App.Tests`. CI runs part of `App.Tests` headless and should not have to
filter around a second risk class, and `App.Tests` carries the
`Application.Current.Resources` cross-test coupling noted above, which makes isolation
unreliable.

### Two modes

**Supervised** — default, works today. Requires an explicit go-ahead from the user before
each run, bounded duration, and the safety harness guarantees above. During the run the
user has no input.

**Isolated desktop** — after the tier 0 spike succeeds. No supervision needed. Residual
risk: the alternate desktop has no shell process, so anything taskbar-dependent in tier 2
may still require supervised mode.

### CI

Tiers 1 and 2 stay out of CI, for the same reason `Category=UI` already is — the
`windows-latest` runner has no interactive desktop session. CI continues to run
`Category!=UI`. The new tiers are a deliberate local pre-merge gate.

## Build order

1. Tier 0 spike — decides the execution mode for everything after.
2. `EyeCtrl.TestTarget` + pipe protocol.
3. `GazePath` driver + safety harness.
4. J1, J10, J11 — the flagship and the two negative tests. First real value.
5. Remaining tier 1 journeys.
6. Tier 2 breadth.

Steps 1–4 are the meaningful slice. If the work stops after step 4, the app has its first
proof that a click lands where the user looked and does not land when it shouldn't.

## Open questions

- Whether `GazeEngine.StableCentroid` and `InteractionManager.FixationPoint` agree in
  practice. J2 answers it. If they diverge materially, the override at
  EventCoordinator:1374 needs its own design discussion.
- The real coordinate tolerance, measurable only from the first green run.
- Whether a full-screen recording target can coexist with EyeCtrl's overlays without
  z-order interference. If not, fall back to the `WH_MOUSE_LL` recorder for coordinate
  assertions and use a smaller, positioned target window for hit-testing.
