# Gaze Pipeline Benchmarks — Design Spec

**Date:** 2026-06-25
**Status:** Draft

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
- **Adding benchmarks to the main solution file or CI** — stays standalone per `AGENTS.md`. No
  benchmark runs in CI (consistent with existing policy).

## Architecture

### File Layout

All new benchmark files live in `benchmarks/EyeCtrl.Benchmarks/`, file-scoped namespace
`EyeCtrl.Benchmarks`, matching the existing prediction benchmarks. One new test project for the
generator's correctness checks.

| File | Purpose |
|------|---------|
| `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` | Deterministic, procedural generator of `GazePoint[]` streams (one method per scenario) |
| `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` | The benchmark class (22 benchmarks across 4 layers) |
| `tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj` | New test project (xUnit + NFluent) |
| `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` | Correctness checks for the generator |

The benchmark project's `Program.cs` needs **no change** — it already uses
`BenchmarkSwitcher.FromAssembly(...).Run(args)`, so the new `[Benchmark]`-decorated class is
auto-discovered.

### Three Benchmark Layers

| Layer | Question answered | Methods |
|-------|-------------------|---------|
| **Per-component micro** | Is each stage cheap & allocation-free per sample? | Filters (12), detectors (3) |
| **DwellDetector stress** | Does the O(n) target scan stay bounded? | 1 method × 3 params |
| **End-to-end pipeline** | What does one full `OnGazePoint` cost? | 4 |

**Total: 22 benchmarks** in a single `GazePipelineBenchmarks` class.

### Pipeline Assembly (end-to-end layer)

The end-to-end layer constructs a real `GazeEngine` wired to private nested fakes, mirroring the
`FakeGazeProvider` / `FakeInputSimulator` pattern from `tests/EyeCtrl.Core.Tests/GazeEngineTests.cs`.
This follows the repo's "no mocking library — use private nested fake classes" convention. The
fakes record **nothing** during the timed loop (no `List.Add`, no allocation) to avoid
contaminating allocation numbers. The provider's `GazePointReceived` event is raised synchronously
once per sample inside the timed loop — no real threading, matching `GazeEngine`'s documented
single-threaded contract.

### Shared Pattern (house style)

Follows the exact conventions of the existing benchmark files:

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineBenchmarks
{
    // Pre-computed in GlobalSetup; replayed per invocation.
    private GazePoint[] _fixationStream = null!;
    // ...

    [GlobalSetup]
    public void Setup() { /* build streams + filter/detector instances */ }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Fixation()
    {
        // Replay full batch; return last to prevent dead-code elimination.
    }
}
```

- `[SimpleJob(RuntimeMoniker.Net10_0)]` — targets .NET 10 (matches TFM).
- `[MemoryDiagnoser]` — measures GC allocations.
- `[RankColumn]` — ranks benchmarks within the class.

### Batching

Filter calls are sub-microsecond; BenchmarkDotNet's per-invocation overhead would dominate a
single call. Each benchmark method replays a pre-built array of **1000 samples** per invocation
and the reported `ns/op` is per-batch (per-1000-samples). The class XML doc states this explicitly.
Allocation counts (`B/op`) are therefore "bytes per 1000 samples" — trivially convertible to
per-sample by dividing by 1000. The final sample's result is returned from each method to defeat
dead-code elimination.

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
~4.5 samples, so the threshold in samples sits between 4 and 5.

The `GenerateWithDropout` stream inserts **two clustered gaps** to exercise both branches:

| Gap | Length | Wall-clock at 30 Hz | vs. 150 ms threshold | Behavior exercised |
|-----|--------|---------------------|----------------------|--------------------|
| Short | 2 consecutive invalid samples | ~66 ms | under → held | `GapInterpolator` hold-position path |
| Long | 6 consecutive invalid samples | ~200 ms | over → reset | `GapInterpolator` reset path |

Gaps are **clustered** (consecutive invalid samples), not uniformly scattered — that's what real
blinks/tracking-loss look like. The generator places the short gap near the start and the long gap
near the middle, with valid samples surrounding them.

### Why Deterministic Synthetic over Recorded Replay

Every number is reproducible: a contributor running `--filter *GazePipeline*` on a different
machine gets identical input streams. No binary blob, no capture step, no platform-specific path.
The `seed` parameter exists so a motivated investigator can regenerate with different seeds, but
the default is pinned at 42.

## Benchmark Inventory

### Layer 1 — Per-Component Filter Micro (12 benchmarks)

Filters: **Direct** (`DirectFilter`), **Smoothed** (`SmoothedFilter`, default factor 0.5),
**OneEuro** (`OneEuroFilter`, default minCutoff 5.0 / beta 0.5). Built via direct construction at
default settings (not via `GazeFilterFactory`, to keep each filter isolated and avoid settings
plumbing).

Scenarios: **Fixation**, **Saccade**, **NoisyFixation**, **WithDropout** (4) × 3 modes = 12.

| Benchmark naming | Example |
|------------------|---------|
| `Filter_<Mode>_<Scenario>` | `Filter_OneEuro_NoisyFixation`, `Filter_Direct_WithDropout` |

For filters, the WithDropout scenario matters most — it exercises the invalid-sample branch
(filter returns last-valid, no smoothing work). Filters carry state (last filtered position,
velocity estimate) that accumulates across the batch — exactly as in production — so a fresh
instance is **constructed** in `[IterationSetup]` for each measured iteration, ensuring every
iteration starts from the same uninitialised state. Detectors likewise get fresh instances. The
generator streams themselves are built once in `[GlobalSetup]` (they are immutable inputs).

### Layer 2 — Per-Component Detector Micro (3 benchmarks)

| Benchmark | Component | Scenario | Notes |
|-----------|-----------|----------|-------|
| `FixationDetector_Update` | `FixationDetector.Update` | Fixation | Fires `FixationStarted` mid-batch; leave event unsubscribed (no handler cost) |
| `CentroidTracker_Update` | `FixationCentroidTracker.Update` | Fixation | `StartTracking()` called in setup; accumulates via Welford |
| `GapInterpolator_Process` | `GapInterpolator.Process` | WithDropout | Mixes valid/invalid; hits both hold and reset branches |

### Layer 3 — DwellDetector Stress (1 method × 3 params = 3 benchmarks)

The one non-O(1) hot path: `DwellDetector.Update` scans every target each call. Confirms per-update
cost scales ~linearly and stays well under a sample budget.

```csharp
[Params(5, 20, 50)]
public int TargetCount { get; set; }
```

Gaze sits inside the **nearest** target so the full hit-test loop runs and a target accumulates
dwell. `[GlobalSetup]` rebuilds the target set per `TargetCount`. Targets are arranged in a grid
spanning a plausible screen area, each with a hit radius, so the nearest-target selection logic
exercises realistically.

### Layer 4 — End-to-End Pipeline (4 benchmarks)

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
this exercises the real production wiring path.

## Testability

The benchmark project is `Exe`/`net10.0` and not part of the test runner, so generator correctness
checks need their own home.

**Decision: new `tests/EyeCtrl.Benchmarks.Tests` project** (xUnit + NFluent via the shared
`tests/Directory.Build.props`), referencing only `EyeCtrl.Benchmarks`. This keeps BenchmarkDotNet
from bleeding transitively into the Core/Platform/ViewModels test suites.

Rejected alternative: reference the benchmark project from `EyeCtrl.Core.Tests`. The generator is
`public` so no `InternalsVisibleTo` would be needed, but it would pull BenchmarkDotNet in as a
transitive dependency of the core unit-test project — surprising and ugly.

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
needs. The new test project picks up xUnit + NFluent from the shared
`tests/Directory.Build.props` (same as the other test projects).

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Synthetic streams don't reflect real tracker characteristics | Deterministic seed-based generation; documented as throughput baselines, not real-content accuracy. Quality characterization stays in unit tests. |
| Sub-microsecond filter calls dominated by BD per-invocation overhead | Batch 1000 samples per invocation; report per-batch and document it. |
| End-to-end pipeline measurement contaminated by fake allocations | Fakes record nothing during the timed loop (no `List.Add`); `GlobalSetup` does all allocation. |
| Filter state leaks across iterations | `[IterationSetup]` resets/reconstructs filter and detector instances per iteration. |
| DwellDetector target grid doesn't exercise nearest-target selection | Grid spans screen area with realistic radii; gaze placed inside nearest target. |
| Generator drifts undetected (e.g., dropout gaps stop straddling the threshold) | `GazeStreamGeneratorTests` asserts gap clustering and length explicitly. |

## Verification

After implementation:

```bash
# Build (must compile 0 errors)
dotnet build benchmarks/EyeCtrl.Benchmarks
dotnet build tests/EyeCtrl.Benchmarks.Tests

# Generator correctness (fast, no display)
dotnet test tests/EyeCtrl.Benchmarks.Tests

# All gaze pipeline benchmarks
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazePipeline*

# One layer at a time
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Filter_*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *DwellDetector_*
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Pipeline_*
```

## Documentation Update

`AGENTS.md`'s Benchmarks section is updated: counts change from "7 classes, 28 benchmarks" to
**"8 classes, 50 benchmarks"** (28 prediction + 22 gaze pipeline), and a one-line note is added
that the new `GazePipelineBenchmarks` class covers the per-sample gaze processing path.
