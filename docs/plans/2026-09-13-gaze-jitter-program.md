# Gaze jitter program — 2026-09-13

What was done about the reported gaze jitter, why, and what remains. Four PRs are on `main`
(tip `614b20e8`): [#144](https://github.com/xasx/eyectrl/pull/144),
[#145](https://github.com/xasx/eyectrl/pull/145),
[#146](https://github.com/xasx/eyectrl/pull/146),
[#147](https://github.com/xasx/eyectrl/pull/147).

## Why

Gaze was reported "very jittery" in daily use. A five-seat analysis (signal processing,
fixation/interaction, measurement, real-time architecture, tracker/calibration) established that
the complaint had several independent causes, none of which was "the filter needs a bigger
number".

## What was wrong

1. **The One-Euro filter was effectively a pass-through.** At shipped `minCutoff 5 / beta 0.5`,
   noise attenuation was 2–20 % and fell as tracker noise rose. The speed term is driven by raw
   finite-difference velocity, so tracker noise itself opened the cutoff. `beta` was ~50× out of
   scale for pixel units and the 0.1-step slider could not express the correct value (~0.01).
   At that `beta` the filter's residual noise is approximately invariant to input improvements —
   a quieter tracker, a higher sample rate, or a better calibration is handed back by the
   adaptive term.
2. **Discrete pipeline defects** no filter can fix: a raw pop on every blink recovery, phantom
   fixation/dwell during blink holds, an uncoalesced UI-thread hop, and a cursor ring that keeps
   moving while the OS pointer is frozen during fixation.
3. **No calibration file existed** on the test machine, so gaze ran uncalibrated.
4. **The only jitter metric measured the wrong thing** (speed-domain, latched at fixation end,
   post-filter, blink-deflated, and gated off by default), and the calibration flow could accept a
   fit with residuals up to 20 % of the screen diagonal (~907 px on 3840×2400).

## What shipped

### Stage 1 — correctness fixes (`#144`, merge `79c3256d`)

- `IsInGap` remains true through a long-gap tail, including the triggering sample — closes the
  phantom-fixation hole (`src/EyeCtrl.Core/Processing/GapInterpolator.cs`).
- Gap-held samples no longer enter the fixation centroid (blink-deflated jitter closed,
  `src/EyeCtrl.Core/GazeEngine.cs`).
- `InteractionManager.CurrentState` is read once per gaze sample (removes a read-twice race).
- A `GazeFrame` carries the gap flag to all dwell surfaces, so blink-held samples cannot advance
  dwell (8 surfaces, including the keyboard path).
- Atomic calibration/settings writes, and the calibration clear now logs.

### Stage 2 — measurement instrument (`#144`, opt-in, off by default)

- **Raw-gaze recorder**: an `IGazeProvider` decorator (raw-only, allocation-free on the gaze
  thread, self-disabling, provider-unwrapping, 16 MB rotating CSV with a JSON header and a
  dropped-sample trailer; a missing trailer refuses the recording).
- **Replay analyzer** (`tests/EyeCtrl.Core.Tests/Analysis/`): deterministic replay with p95/RMS
  deviation, duration-bucketed pop counter, step latency, dwell time-to-activate and reset
  counts, PSD, and the acceptance gate as executable pass/fail.
- **UI instruments**: dispatcher enqueue→execute p95 (withdraws coalescing if under one frame)
  and ring pixel-quantization/cadence.
- **Scripted ground truth**: a calibration run with recording on emits a sidecar of target
  positions and device-time windows; a σ report surfaces per-target noise with centre/corner.
- First measured finding: the shipped One-Euro covered 90 % of a 400 px step **within one
  sample** — the pass-through claim, measured.

### Calibration reliability (`#145`, merge `b7bd22c1`)

- Post-arrival samples count only **in-band**; the stability arrival rule can no longer certify a
  target from anywhere on screen; per-target **coverage** is reported alongside σ (null when
  unknown, never 0).
- The drawn tolerance ring equals the enforced band; pacing accumulates in-band samples across
  short holds; safety cap is two-stage (8 s before arrival, 25 s after).
- Acceptance gate: residual ≤ 5 % of the diagonal (≈226 px at 3840×2400), radial σ ≤ 100 px,
  coverage ≥ 50 %, with target-specific failure messages.
- Recording header now captures the true calibration state (it was built before the calibration
  loaded, so calibrated recordings were mislabeled).

### Stage 3 — filter retune (`#146`, merge `a9ba6259`)

- `OneEuroBeta` → `OneEuroBetaPerDiagonal`: `cutoff = minCutOff + beta × (speed / 2000 px)`,
  default 20 (the old correct `0.01` in the new unit), `minCutoff` default 1.5, slider 0–60.
  A **key-based migration** updates existing profiles once, converting rather than overwriting
  user-chosen values (`src/EyeCtrl.Core/Settings/SettingsService.cs`).
- Filter: speed derived from the filtered signal; first-sample jump detector
  (`max(15 px, 3 × running RMS of the innovation)`); cutoff capped at `sampleRate/3`; the `dt`
  clamp applied only inside `Alpha()` (`src/EyeCtrl.Core/Processing/OneEuroFilter.cs`).
- The noise estimate outlives initialisation, blinks and rebuilds, crossing a rebuild through an
  opaque capture/adopt contract rather than a type test, with a reflective guard that fails the
  build for any wrapper that does not forward it.
- Synthetic result: p95 deviation −59 %, RMS −57 %, isolated pops −100 %, dwell resets −100 %,
  step latency 0 → 67 ms.

**Recorded gate risks** (decided at the acceptance run, not tuned away):

| Risk | Evidence |
|---|---|
| Dwell after a saccade starts later | +33/+67/+100 ms at 100/600/1200 px — over the +50 ms veto at larger saccades |
| Stability margin is thin | −59.1 % against a ≥60 % gate |
| Small steps miss the latency budget | 20 px step ≈ 233 ms at σ=10/15 — over 150 ms |

### Calibration UX (`#147`, merge `614b20e8`)

- Coverage margin 20 % → **5 %** of the shorter side (120 px at 3840×2400; 80 px floor, 20 %
  ceiling), so the transform spans the screen.
- Target order is an **exact minimax Hamiltonian path** (the previous single-pass DP was unsound —
  a running max and a running sum are not jointly dominated).
- The whole target set is visible for the session, and the active target **slides 220 ms eased**
  to the next position instead of teleporting.
- Accepted: the corner ring extends off-screen at the new margin (expected; ~63 % of the disc is
  collectable), and a sensor stuck at the screen origin is refused later, by the cross-target
  collapse check.

## What the operator session revealed

- A first calibration produced σ ≈ 400 px RMS and the user could not hold targets. The tracker's
  quiet-window floor measured **1.1–2.1 px**, so the 400 px was **motion captured by the
  collection window** — a missing in-radius gate, not device noise. The device is healthy
  (0.42 % invalid samples, single monitor, full coordinate span).
- A second session recorded 442.9 s of free-view gaze; those quiet-window and hold-duration
  statistics come from it.

## Current state and next steps

1. **A device-side Tobii recalibration is now a prerequisite** before the in-app calibration can
   succeed. If the raw offset still exceeds the 240 px band, the app refuses to complete a
   calibration rather than accepting a bad fit.
2. **Then one session with `Diagnostics.RecordRawGaze` armed**: run the calibration, use the app
   normally for a minute or two, exit via the tray (so the recorder writes its trailer). This
   single session yields the σ/PSD verdict and the acceptance-run input.
3. **The σ/PSD kill condition is unevaluated, and `#146` was merged ahead of it.** If the
   recording shows σ ≤ 2 px or a fixation PSD dominated below 2 Hz, the retune is to be
   **withdrawn as written**, not re-tuned.
4. **Held work:** the median prefilter (conditional on the isolated-pop bucket), the branched
   deadband stabilizer (which compounds the saccade-dwell risk), and the acceptance run itself.
   After acceptance: fixation hysteresis, dwell grace, device output frequency (paired with the
   retune), and UI coalescing if instrument 1 says so.

## Verification approach

- Every change was independently reviewed (separate reviewer agents, fix rounds, focused
  re-checks before commits).
- Mutation testing throughout: 38 mutants on the filter change, 21 on calibration UX, 14+ on the
  instruments; every survivor explained or shown behaviourally equivalent.
- Test suites are run one at a time on a developer machine (machine-global named pipes). UI tests
  are manual by design and carry checklists.

## Follow-up backlog

- Settings/adaptation version counter — segment recordings by effective parameters.
- Stray `.tmp` cleanup after failed atomic saves (observed).
- `MeasureStepLatencyMs` degeneracy on noisy streams (nulls and multi-second artefacts at every
  amplitude); the acceptance procedure uses ensemble-mean trajectories until it is fixed.
- Eye-care banner dwell vs the exercise recorder (gap flag).
