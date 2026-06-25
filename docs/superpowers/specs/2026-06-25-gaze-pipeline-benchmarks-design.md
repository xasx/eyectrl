# Gaze Pipeline Benchmarks — Design Spec

**Date:** 2026-06-25
**Status:** Draft (revised after code review)

## Goal

Add a comprehensive benchmark suite for the per-sample gaze processing pipeline — the hottest
path in the application, executed once for every incoming gaze sample (~30–90 Hz). The existing
`benchmarks/EyeCtrl.Benchmarks/` project covers **only** the prediction/text subsystem
(7 classes, 28 benchmarks). The gaze pipeline itself
(`OneEuroFilter`, `SmoothedFilter`, `DirectFilter`, `GapInterpolator`, `FixationDetector`,
`FixationCentroidTracker`, `DwellDetector`, and the end-to-end `GazeEngine.OnGazePoint`) has
**zero** benchmark coverage. This spec closes that gap.

The suite answers three questions: (1) is each pipeline stage cheap and allocation-free per
sample? (2) does the one O(n) hot path — `DwellDetector`'s per-update target scan — stay bounded
as targets grow? (3) what does one full `OnGazePoint` actually cost, per filter mode?

## Non-Goals

- **Smoothing quality metrics** (jitter, lag, RMSE vs. ground truth) — out of scope. Those stay
  in the existing unit tests (`OneEuroFilterValidationTests`). This suite is performance-only
  (latency + allocations). Rationale: BenchmarkDotNet is the wrong tool for quality
  characterization, and mixing the two muddies both.
- **`Reset()` benchmarks** — reset is rare (session start, long gap). Not a hot path.
- **Calibration transform in the pipeline benchmark** — `CalibrationService.CurrentTransform`
  adds a trivial affine apply (two multiplies). Tested without it; add later only if it proves
  relevant.
- **Recorded gaze replay** — synthetic generation only (see Input Model). No binary blobs in the
  repo, no capture step.
- **Adding benchmarks to the main solution file or CI** — the benchmark **project** stays
  standalone per `AGENTS.md`. The new generator **test** project runs in CI via an explicit step
  (see Testability).
- **`Filter_*_WithDropout` benchmarks** — see "Dropout scenario scoping" below. Filters never see
  invalid samples in production; that path is covered at the `GapInterpolator` layer instead.

## Architecture

### File Layout

New benchmark files live in `benchmarks/EyeCtrl.Benchmarks/`, file-scoped namespace
`EyeCtrl.Benchmarks`, matching the existing prediction benchmarks. One new test project for the
generator's correctness checks.

| File | Purpose |
|------|---------|
| `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` | Deterministic, procedural generator of `GazePoint[]` streams (one method per scenario) |
| `benchmarks/EyeCtrl.Benchmarks/GazeFilterBenchmarks.cs` | Filter micro-benchmarks (Layer 1) |
| `benchmarks/EyeCtrl.Benchmarks/GazeDetectorBenchmarks.cs` | Detector micro-benchmarks (Layer 2) |
| `benchmarks/EyeCtrl.Benchmarks/DwellDetectorBenchmarks.cs` | DwellDetector target-sweep (Layer 3) |
| `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` | End-to-end pipeline (Layer 4) |
| `tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj` | New test project (xUnit + NFluent) |
| `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` | Correctness checks for the generator |

The benchmark project's `Program.cs` needs **no change** — it already uses
`BenchmarkSwitcher.FromAssembly(...).Run(args)`, so the new `[Benchmark]`-decorated classes are
auto-discovered.

### Four Benchmark Classes (matching house style)

The existing convention is one focused class per concern (7 prediction classes). The suite splits
into four classes — one per layer — rather than one mega-class. This matches the repo, keeps each
class's `[GlobalSetup]`/`[IterationSetup]` homogeneous (a single untargeted setup can serve all
methods in the class), and scopes `[Params]` to only the class that needs it.

| Class | Layer | Question answered | Benchmarks |
|-------|-------|-------------------|------------|
| `GazeFilterBenchmarks` | 1 | Is each filter cheap & allocation-free per sample? | 9 |
| `GazeDetectorBenchmarks` | 2 | Are detectors cheap & allocation-free per sample? | 3 |
| `DwellDetectorBenchmarks` | 3 | Does the O(n) target scan stay bounded? | 3 (1 method × `[Params(5,20,50)]`) |
| `GazePipelineBenchmarks` | 4 | What does one full `OnGazePoint` cost? | 4 |

**Total: 19 benchmarks across 4 classes.**

### Pipeline Assembly (Layer 4)

The end-to-end layer constructs a real `GazeEngine` wired to private nested fakes, mirroring the
`FakeGazeProvider` / `FakeInputSimulator` pattern from
`tests/EyeCtrl.Core.Tests/GazeEngineTests.cs`. This follows the repo's "no mocking library — use
private nested fake classes" convention. The fakes record **nothing** during the timed loop (no
`List.Add`, no allocation) to avoid contaminating allocation numbers. The provider's
`GazePointReceived` event is raised synchronously once per sample inside the timed loop — no real
threading, matching `GazeEngine`'s documented single-threaded contract.

### Shared Pattern (house style)

Follows the exact conventions of the existing benchmark files:

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFilterBenchmarks
{
    // Pre-computed in GlobalSetup; replayed per invocation.
    private GazePoint[] _fixationStream = null!;
    // ...

    [GlobalSetup]
    public void Setup() { /* build streams + filter instances */ }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Fixation()
    {
        // Replay full batch; return last to prevent dead-code elimination.
    }
}
```

- `[SimpleJob(RuntimeMoniker.Net10_0)]` — targets .NET 10 (matches TFM). This is the correct house
  style: 6 of 7 existing files use `Net10_0`; the lone `Net90` in `PredictionBenchmarks.cs:11` is
  a stale outlier not worth touching here.
- `[MemoryDiagnoser]` — measures GC allocations.
- `[RankColumn]` — ranks benchmarks within the class.

### Batching

Filter calls are sub-microsecond; BenchmarkDotNet's per-invocation overhead would dominate a
single call. Each benchmark method replays a pre-built array of **1000 samples** per invocation
and the reported `ns/op` is per-batch (per-1000-samples). Each class's XML doc states this
explicitly. Allocation counts (`B/op`) are therefore "bytes per 1000 samples" — trivially
convertible to per-sample by dividing by 1000. The final sample's result is returned from each
method to defeat dead-code elimination.

`GazePoint` is a `record` (class), so the generator heap-allocates 1000 objects per stream in
`GlobalSetup`. This is fine (setup is untimed) but worth noting: the input arrays are not struct
arrays.

### Per-iteration Setup — scoped to where it's needed

BenchmarkDotNet runs many invocations per iteration on the same instance, and its docs discourage
`[IterationSetup]` for microbenchmarks. With 1000-sample batches the init branch is 1/1000 of the
work, so:

- **Filters & detectors (Layers 1–2):** construct once in `[GlobalSetup]`, let state accumulate
  across the batch. This is production-representative (a real filter accumulates state across
  samples) and avoids needless per-iteration churn.
- **Pipeline (Layer 4):** rebuild a **fresh** `FakeGazeProvider` + `GazeEngine` per iteration via
  `[IterationSetup]`. Two reasons: (a) `InteractionManager` starts in `FreeLook`; after the
  Fixation scenario triggers a state transition, `MoveCursor` stops being called — so iteration 2+
  would measure a different path. (b) The `GazeEngine` constructor subscribes to the provider's
  `GazePointReceived` event; reusing a provider across engine rebuilds stacks subscriptions,
  leaking N engines' `OnGazePoint` per sample. A fresh provider+engine pair each iteration
  avoids both. `[IterationCleanup]` disposes the prior engine.

## Input Model — `GazeStreamGenerator`

The generator is the keystone: unrealistic streams make filter-comparison numbers meaningless.
All streams share the same length and sample period so cross-scenario `ns/op` is directly
comparable.

### Common Parameters

```
SamplesPerBatch       = 1000
SamplePeriodMicros    = 33_333      // ~30 Hz, Tobii's common rate
DefaultSeed           = 42          // fixed; reproducible across machines/runs
```

Timestamps are monotonically increasing multiples of the period (`0, 33333, 66666, …`), matching
what the real tracker delivers.

### Public Surface

```csharp
public static class GazeStreamGenerator
{
    public const int SamplesPerBatch = 1000;
    public const long SamplePeriodMicros = 33_333;
    public const int DefaultSeed = 42;

    public static GazePoint[] GenerateFixation(int seed = DefaultSeed);
    public static GazePoint[] GenerateSaccade(int seed = DefaultSeed);
    public static GazePoint[] GenerateNoisyFixation(int seed = DefaultSeed);
    public static GazePoint[] GenerateWithDropout(int seed = DefaultSeed);
}
```

- A single `Random` is constructed per call (seeded by `seed`), so results are deterministic
  regardless of call order or parallelism.
- Gaussian noise via Box–Muller transform (no extra NuGet dependency).
- The class is `public static` so the test project can reference it directly (no
  `InternalsVisibleTo` needed).

### Coordinate space note

`GazePoint.X/Y` are documented as the tracker's native coordinate space (calibration maps
native→pixels; that mapping is excluded here as a non-goal). The generator writes pixel-scale
values (0–2000) into these fields directly. This is harmless for performance measurement — the
filters and detectors do pure arithmetic on the doubles — but the streams should not be read as
physically calibrated gaze.

### Scenario Definitions

| Scenario | Trajectory | Noise | Valid samples | Exercises |
|----------|------------|-------|---------------|-----------|
| **Fixation** | Held near `(960, 540)` with slow circular drift (radius ~8 px, ~0.3 Hz) | ±1.5 px Gaussian | 100% valid | Steady-state smoothing; OneEuro's low-velocity branch |
| **Saccade** | 5 point-to-point jumps across the screen (e.g. 960→200→1500→…), each held briefly | ±0.5 px (precise) | 100% valid | High-velocity branch; `FixationDetector` reset churn |
| **NoisyFixation** | Held at `(960, 540)`, near-stationary | ±15 px Gaussian (heavy) | 100% valid | Aggressive jitter — the case filters exist to tame |
| **WithDropout** | Fixation drift trajectory (reused) | ±1.5 px | ~8% marked `IsValid=false`, **clustered** into gaps | `GapInterpolator` invalid branch; filter hold-position path |

### Dropout Gap Placement (critical correctness detail)

`GapInterpolator` behaves differently for short vs. long gaps: short gaps hold the last position
(and suppress fixation), long gaps reset the filter. The default `MaxGapInterpolationMs` is
**150 ms** (`CursorSettings._maxGapInterpolationMs = 150`). At 30 Hz (33 333 µs/sample) that is
~4.5 samples, so the threshold in samples sits between 4 and 5. `GapInterpolator.Process` measures
the gap from the *last valid* timestamp, so reset fires at the 5th consecutive invalid sample.

The `GenerateWithDropout` stream inserts **two clustered gaps** to exercise both branches:

| Gap | Length | Wall-clock at 30 Hz | vs. 150 ms threshold | Behavior exercised |
|-----|--------|---------------------|----------------------|--------------------|
| Short | 2 consecutive invalid samples | ~66 ms | under → held | `GapInterpolator` hold-position path |
| Long | 6 consecutive invalid samples | ~200 ms | over → reset | `GapInterpolator` reset path |

Gaps are **clustered** (consecutive invalid samples), not uniformly scattered — that's what real
blinks/tracking-loss look like. The generator places the short gap near the start and the long gap
near the middle, with valid samples surrounding them. This stream also hits the `_lastValidTimestamp
< 0` re-init branch after the reset.

### Why Deterministic Synthetic over Recorded Replay

Every number is reproducible: a contributor running `--filter *Gaze*` on a different machine gets
identical input streams. No binary blob, no capture step, no platform-specific path. The `seed`
parameter exists so a motivated investigator can regenerate with different seeds, but the default
is pinned at 42.

## Benchmark Inventory

### Layer 1 — `GazeFilterBenchmarks` (9 benchmarks)

Filters: **Direct** (`DirectFilter`), **Smoothed** (`new SmoothedFilter(0.5)` — there is no
parameterless ctor; 0.5 is the settings default passed explicitly), **OneEuro**
(`new OneEuroFilter()` — default minCutoff 5.0 / beta 0.5). Built via direct construction at
default settings, **not** via `GazeFilterFactory`, to keep each filter isolated and avoid settings
plumbing.

Scenarios: **Fixation**, **Saccade**, **NoisyFixation** (3) × 3 modes = 9.

| Benchmark naming | Example |
|------------------|---------|
| `Filter_<Mode>_<Scenario>` | `Filter_OneEuro_NoisyFixation`, `Filter_Direct_Saccade` |

#### Dropout scenario scoping

`Filter_*_WithDropout` is **deliberately excluded**. In production, `GazeFilterFactory.Create` is
called only inside `GazeEngine`, both times feeding a `GapInterpolator`
([GazeEngine.cs:58,67](src/EyeCtrl.Core/GazeEngine.cs:58)), and `GapInterpolator.Process` calls
`_filter.Filter()` **only for valid samples** ([GapInterpolator.cs:33-35](src/EyeCtrl.Core/Processing/GapInterpolator.cs:33)).
So a filter never receives an invalid sample on the real path — the `if (!rawGaze.IsValid &&
_initialized) return _lastFiltered` early-return ([OneEuroFilter.cs:150](src/EyeCtrl.Core/Processing/OneEuroFilter.cs:150))
is dead code in production. Invalid-sample handling is covered instead at the `GapInterpolator`
layer (Layer 2), where it actually happens.

### Layer 2 — `GazeDetectorBenchmarks` (3 benchmarks)

Detectors take `PhysicalPoint`, not `GazePoint` (`FixationDetector.Update`,
`FixationCentroidTracker.Update`, `DwellDetector.Update`). The generator emits `GazePoint[]`, so
`[GlobalSetup]` pre-builds a parallel `PhysicalPoint[]` via projection
(`new PhysicalPoint(g.X, g.Y)`) for each stream. This projection happens in setup (untimed).

| Benchmark | Component | Scenario | Notes |
|-----------|-----------|----------|-------|
| `FixationDetector_Update` | `FixationDetector.Update` | Fixation | Fires `FixationStarted` mid-batch; event left unsubscribed (no handler cost) |
| `CentroidTracker_Update` | `FixationCentroidTracker.Update` | Fixation | `StartTracking()` called in setup; accumulates via Welford |
| `GapInterpolator_Process` | `GapInterpolator.Process` | WithDropout | Mixes valid/invalid; hits both hold and reset branches — the only place invalid-sample handling is exercised |

### Layer 3 — `DwellDetectorBenchmarks` (1 method × 3 params = 3 benchmarks)

The one non-O(1) hot path: `DwellDetector.Update` scans every target each call. `[Params]` is
scoped to this class (not class-level on a shared class) so it expands only these 3 cases.

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class DwellDetectorBenchmarks
{
    [Params(5, 20, 50)]
    public int TargetCount { get; set; }
    // ...
}
```

Gaze sits inside the **nearest** target so the full hit-test loop runs and a target accumulates
dwell. `[GlobalSetup]` rebuilds the target set per `TargetCount`. Targets are arranged in a grid
spanning a plausible screen area, each with a hit radius, so the nearest-target selection logic
exercises realistically.

#### Anticipated finding: DwellDetector is not allocation-free

`Update` enumerates `_targets.Values` **twice** per call ([DwellDetector.cs:105,123](src/EyeCtrl.Core/Processing/DwellDetector.cs:105)),
and `ConcurrentDictionary.Values` allocates a fresh snapshot collection each access. So
`MemoryDiagnoser` **will** report nonzero `B/op` for a path that runs once per gaze sample in
production (driven off `CursorMoved`). This is a genuine finding and an optimization target — the
benchmark is expected to surface it, not be surprised by it. The risk table records this
anticipation explicitly.

### Layer 4 — `GazePipelineBenchmarks` (4 benchmarks)

Real `GazeEngine` wired to `FakeGazeProvider`/`FakeInputSimulator`, per filter mode. The provider's
`GazePointReceived` is raised synchronously once per sample inside the timed loop.

| Benchmark | Filter mode | Scenario |
|-----------|-------------|----------|
| `Pipeline_Direct_Fixation` | Direct | Fixation |
| `Pipeline_Smoothed_Fixation` | Smoothed | Fixation |
| `Pipeline_OneEuro_Fixation` | OneEuro | Fixation |
| `Pipeline_OneEuro_NoisyFixation` | OneEuro | NoisyFixation |

The first three give the apples-to-apples per-mode cost on a representative scenario; the fourth
is the worst-case (heaviest filter + heaviest jitter). Filter mode is set on `EyeCtrlSettings`
before constructing the `GazeEngine`, so `GazeFilterFactory.Create` produces the right filter —
this exercises the real production wiring path. Per "Per-iteration Setup" above, a fresh
provider+engine pair is built each iteration via `[IterationSetup]` to avoid state leak and
subscription stacking.

## Testability

The benchmark project is `Exe`/`net10.0` and deliberately **not** part of the solution
(`AGENTS.md`: "Not part of the solution file"). Generator correctness checks therefore need their
own home that doesn't erode that invariant.

**Decision: new `tests/EyeCtrl.Benchmarks.Tests` project** (xUnit + NFluent via the shared
`tests/Directory.Build.props`, which auto-provides both to anything under `tests/`), referencing
only `EyeCtrl.Benchmarks`. This keeps BenchmarkDotNet from bleeding transitively into the
Core/Platform/ViewModels test suites.

Rejected alternative: reference the benchmark project from `EyeCtrl.Core.Tests`. The generator is
`public` so no `InternalsVisibleTo` would be needed, but it would pull BenchmarkDotNet in as a
transitive dependency of the core unit-test project — surprising and ugly.

### CI wiring — explicit step (not slnx)

The new test project is **not** added to `EyeCtrl.slnx` (which would pull the benchmark project +
BenchmarkDotNet into the slnx build graph via `ProjectReference`, eroding the standalone
invariant). Instead, CI gets an explicit step in `.github/workflows/ci.yml` that builds and runs
just this project:

```yaml
      - name: Run generator tests (benchmark correctness)
        run: dotnet test tests/EyeCtrl.Benchmarks.Tests
```

Placed after the existing `Run unit tests` step. This honors "benchmarks are standalone" while
still giving the drift guard automated coverage — the property the review flagged as missing.

### `GazeStreamGeneratorTests` Coverage

- Each `Generate*` returns exactly `SamplesPerBatch` points.
- Timestamps strictly increasing by exactly `SamplePeriodMicros`.
- **Determinism:** same seed → identical arrays (call twice, compare byte-for-byte via sequence
  equality of `X`, `Y`, `IsValid`).
- `GenerateWithDropout` contains both valid and invalid samples, invalids are clustered
  (≥2 consecutive where present), and includes at least one gap of length ≥ 6 (the long gap).
- Coordinate magnitudes are sane (within a plausible screen box, e.g. 0–2000 px) — catches a
  generator bug that produces NaN/Infinity.

## Dependencies

No new NuGet packages for the benchmark project. Existing `BenchmarkDotNet` 0.15.8 covers all
needs. The new test project picks up xUnit + NFluent from the shared `tests/Directory.Build.props`
(same as the other test projects).

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Synthetic streams don't reflect real tracker characteristics | Deterministic seed-based generation; documented as throughput baselines, not real-content accuracy. Quality characterization stays in unit tests. |
| Sub-microsecond filter calls dominated by BD per-invocation overhead | Batch 1000 samples per invocation; report per-batch and document it. |
| End-to-end pipeline measurement contaminated by fake allocations | Fakes record nothing during the timed loop (no `List.Add`); `[GlobalSetup]`/`[IterationSetup]` do all allocation. |
| Engine subscription stacking across iterations | `[IterationSetup]` builds a fresh `FakeGazeProvider` + `GazeEngine` per iteration; `[IterationCleanup]` disposes the prior engine. Old pair is GC'd with no subscribers. |
| `InteractionManager` state leak biases iteration 2+ | Same fresh-engine-per-iteration approach; engine always starts in `FreeLook`. |
| `DwellDetector.Update` allocates per call (`ConcurrentDictionary.Values` ×2) | Anticipated finding, not a surprise — benchmark surfaces it as an optimization target. Risk table records the expectation. |
| Generator drifts undetected (e.g., dropout gaps stop straddling the threshold) | `GazeStreamGeneratorTests` asserts gap clustering and length explicitly; runs in CI via dedicated step. |

## Verification

After implementation:

```bash
# Build (must compile 0 errors)
dotnet build benchmarks/EyeCtrl.Benchmarks
dotnet build tests/EyeCtrl.Benchmarks.Tests

# Generator correctness (fast, no display)
dotnet test tests/EyeCtrl.Benchmarks.Tests

# All gaze pipeline benchmarks (all 4 classes)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*

# One layer at a time
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Filter_*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Detector_*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Pipeline_*
```

## Documentation Update

`AGENTS.md`'s Benchmarks section ([AGENTS.md:132](AGENTS.md:132)) is updated: the covered-subjects
list is extended to name the gaze pipeline components, and the count changes from
"7 benchmark classes, 28 benchmarks total" to **"11 benchmark classes, 47 benchmarks total"**
(28 prediction + 19 gaze pipeline). The "Not part of the solution file" line
([AGENTS.md:121](AGENTS.md:121)) remains accurate: the benchmark project is still standalone; only
the generator test project is referenced directly, by the dedicated CI step.
