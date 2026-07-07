# Eye-Care Gaze Pipeline Benchmarks — Design Spec

**Date:** 2026-07-07
**Status:** Draft
**Type:** Benchmark suite extension

## Goal

The gaze pipeline gained a new per-sample subscriber when eye care landed:
`GazeEngine.GazeSampled` → `EyeCareService.OnGazeSample` → `EyeCareScheduler.Update`
(always) + `FatigueMonitor.OnSample` (when enabled) — wired in
[`App.xaml.cs:151-159`](src/EyeCtrl.App/App.xaml.cs:151). The existing 4-layer gaze
benchmark suite (filter → detector → dwell → pipeline, added in
[`2026-06-25-gaze-pipeline-benchmarks-design.md`](2026-06-25-gaze-pipeline-benchmarks-design.md))
has **zero coverage** of this path: `GazePipelineBenchmarks` builds a bare `GazeEngine`
with no eye-care wiring.

`FatigueMonitor.OnSample` is **O(samples-in-window)** — it re-scans the entire rolling
window every sample (see [`FatigueMonitor.cs:60-68`](src/EyeCtrl.EyeCare/FatigueMonitor.cs:60)).
That is the same cost-shape as `DwellDetector`'s O(targets) scan that Layer 3 already
characterizes and was anticipated to surface an allocation finding.

This spec adds benchmarks answering two questions: (1) is the fatigue scan cheap per
sample, and does it stay bounded as the window grows? (2) what does the eye-care
subscriber add to one full `OnGazePoint`, in each of the three production configurations?

## Non-Goals

- **Exercise scoring benchmarks** — `ExerciseMetrics.RecordDelta`/`Complete` and the five
  exercise types. These run **once per break** (every ~20 min), not once per sample. Not a
  hot path; out of scope.
- **`EyeCareScheduler.Update` in isolation** — O(1) bookkeeping with no scaling story. A
  dedicated microbenchmark would measure BenchmarkDotNet per-invocation overhead. Its cost
  is captured aggregated in the pipeline integration layer, which is how it is paid in
  production.
- **The break flow** (`OnBreakDue` → `ShowBannerAsync` → exercise → summary) — async/UI
  driven, fires at most once per interval. Deliberately suppressed in the pipeline layer
  (see "Escalation suppression").
- **Modifying `GazePipelineBenchmarks`'s existing 4 methods** — those stay as the
  eye-care-off baseline. New methods are additive.
- **New gaze-stream scenarios** — reuses the existing `GenerateFixation` /
  `GenerateSaccade` / `GenerateNoisyFixation` from `GazeStreamGenerator`, which are
  already CI-asserted in `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs`.

## Architecture

### File Layout

| File | Status | Purpose |
|------|--------|---------|
| `benchmarks/EyeCtrl.Benchmarks/FatigueMonitorBenchmarks.cs` | NEW | Layer 5 — O(window) fatigue scan, params-scaled + steady-state |
| `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` | MODIFIED | Layer 4 — adds 3 eye-care-wired methods + fakes |
| `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj` | MODIFIED | Add `ProjectReference` to `EyeCtrl.EyeCare` |
| `AGENTS.md` | MODIFIED | Benchmarks section: covered-subjects list + class/benchmark counts |

No change to `Program.cs` (auto-discovery via `BenchmarkSwitcher.FromAssembly`).
No new test file (see Testability).

The benchmark project currently references `EyeCtrl.Core` + `EyeCtrl.LM`. Adding
`EyeCtrl.EyeCare` is one `ProjectReference` line. `EyeCtrl.EyeCare` is `net10.0` and
references only Core — no native deps, no WPF — so the benchmark project stays
framework-clean.

### Benchmark Inventory (15 new across 2 files)

| Class | Layer | Question answered | New benchmarks |
|-------|-------|-------------------|----------------|
| `FatigueMonitorBenchmarks` | 5 | Is the O(window) fatigue scan cheap per sample, and does it stay bounded? | 12 |
| `GazePipelineBenchmarks` (extended) | 4 | What does the eye-care subscriber add to one full `OnGazePoint`? | 3 |

**Total: 15 new benchmarks (12 + 3).** Existing 47 → **62 benchmarks across 12 classes**
(was 11 classes / 47 benchmarks; +1 class, +15 benchmarks).

Both classes share the `Gaze` token for the existing `--filter *Gaze*` suite selector;
the new micro-layer is selectable via `--filter *Fatigue*`, mirroring the `*Bigram*` /
`*SmallLM*` / `*GazeDwell*` class-name convention.

## Layer 5 — `FatigueMonitorBenchmarks` (micro)

Direct analogue of Layer 3 (`GazeDwellBenchmarks`). Constructs `FatigueMonitor` directly
with `new EyeCareSettings { FatigueMonitorEnabled = true }` and the default fixation
radius (50 px, matching `FatigueMonitorTests` and the `DwellSettings` default). Reuses
the existing `GenerateFixation` / `GenerateSaccade` / `GenerateNoisyFixation` streams.

### Per-batch sizing (steady-state correctness)

The shared `GazeStreamGenerator.SamplesPerBatch = 1000` is too short for the default
60 s window: at 30 Hz a 60 s window holds 1800 samples, so a single 1000-sample batch
**never fills** it — the benchmark would measure window-growth (enqueue-only, no
eviction, no full-window re-scan), not the steady-state O(window) cost that is the
point of the layer.

`FatigueMonitorBenchmarks` therefore generates its **own** sized streams locally rather
than reusing the shared `SamplesPerBatch` constant:

```
SamplesToFill  = WindowSeconds * 30Hz + 1     // enough to first trigger eviction
SteadyTail     = 1000                          // the timed replay length
```

The `[GlobalSetup]` (untimed) pre-feeds `SamplesToFill` samples into the monitor so the
window is full and eviction is active, **then** the `[Benchmark]` method replays only the
`SteadyTail` (1000-sample) suffix. Reported `ns/op` is therefore steady-state per-1000
samples, directly comparable to Layers 1–4 (which all report per-1000-samples). B/op is
likewise per-1000-samples steady-state.

For `Win10`/`Win30` (300/900 samples to fill) the pre-fill is shorter than 1000; for
`Win60` (1800 to fill) it is longer. Either way the timed portion is a fixed 1000
samples of steady-state behavior.

### Params-scaled (the O(window) characterization)

Mirrors Layer 3's `[Params(5,20,50)]` target-count sweep:

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class FatigueMonitorBenchmarks
{
    [Params(10, 30, 60)]   // seconds → ~300 / 900 / 1800 samples in-window at 30 Hz
    public int WindowSeconds { get; set; }
    // ...
}
```

× 3 scenarios = **9 benchmarks**: `Fatigue_<Scenario>_Win<N>s` — e.g.
`Fatigue_Fixation_Win60s`, `Fatigue_NoisyFixation_Win30s`. Each replayed through a
fresh `FatigueMonitor` pre-filled for that window size.

### Steady-state single-window (apples-to-apples baseline)

3 benchmarks at the fixed default window (60 s), **not** driven by `[Params]` so they
rank independently of the params sweep. They live in the **same** `FatigueMonitorBenchmarks`
class as the params-scaled group, using BenchmarkDotNet's targeted-setup support:
`[GlobalSetup(Target = nameof(Fatigue_Fixation))]` etc. build a fresh monitor pinned at
`WindowSeconds = 60` for each steady-state method. This mirrors the targeted-setup
pattern already established in `GazePipelineBenchmarks` (which uses `[IterationSetup(Target = ...)]`
per filter mode) and keeps everything reachable via a single `--filter *Fatigue*`.

Benchmarks: `Fatigue_Fixation`, `Fatigue_Saccade`, `Fatigue_NoisyFixation` (all at
`WindowSeconds = 60`, fixed, not `[Params]`-driven).

### Anticipated finding: queue is allocation-free in steady state

`FatigueMonitor` holds a `Queue<(long ts, double x, double y)>` of *value tuples*
(unboxed primitives). Once the queue's internal array has grown to capacity during the
untimed pre-fill, steady-state enqueue/dequeue is allocation-free. `MemoryDiagnoser`
is therefore expected to report **≈0 B/op** for all Layer 5 benchmarks. If it does not,
that is a genuine finding worth investigating (e.g. unexpected boxing, or the per-sample
`foreach` over `_samples` allocating an enumerator — `Queue<T>.GetEnumerator` is a struct,
so it should not, but this is exactly the kind of thing the benchmark exists to confirm).

The one allocation the *re-scan* loop performs is pure arithmetic on doubles — no
per-sample heap traffic. If the benchmark surfaces nonzero B/op, the inner
`foreach (var s in _samples)` loop ([`FatigueMonitor.cs:60`](src/EyeCtrl.EyeCare/FatigueMonitor.cs:60))
is the first place to look.

## Layer 4 extension — `GazePipelineBenchmarks` (integration)

Adds 3 methods that wire a real `EyeCareService` into `GazeEngine.GazeSampled`,
mirroring the production wiring in [`App.xaml.cs:151-159`](src/EyeCtrl.App/App.xaml.cs:151).
All three use OneEuro + Fixation (the representative scenario already used by the existing
`Pipeline_OneEuro_Fixation`) so the only variable is the eye-care configuration — a clean
before/after.

| New benchmark | `EyeCareSettings` | What it measures |
|---------------|-------------------|------------------|
| `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` | `Enabled = false` (today's shipping default) | The subscriber's existence tax: `scheduler.Update` + early-return before fatigue |
| `Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue` | `Enabled = true`, `FatigueMonitorEnabled = false` (v1 promoted-on default) | + the `FatigueMonitorEnabled` early-return |
| `Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue` | `Enabled = true`, `FatigueMonitorEnabled = true`, `FatigueThreshold = 2.0` | + full O(window) fatigue scan |

Subtraction (`WithFatigue − Disabled`) gives the eye-care subscriber's marginal
per-sample cost on the fully-wired pipeline.

### Fresh-build-per-iteration (extends the existing pattern)

The existing class already rebuilds a fresh `FakeGazeProvider` + `GazeEngine` per
iteration via targeted `[IterationSetup]` methods, because (a) `InteractionManager`
starts in `FreeLook` and converges to `Fixation`, and (b) the constructor subscribes to
the provider's event and reusing a provider would stack subscriptions. The new methods
add the same pattern for the eye-care pair: each targeted setup builds a fresh
`FakeGazeProvider` + `GazeEngine` + `EyeCareService`, subscribes the service to
`engine.GazeSampled` (the production wiring), and `[IterationCleanup]` disposes both
engine and service.

### Fakes required

The existing `FakeGazeProvider` and `FakeInputSimulator` private nested classes are
reused unchanged. Two new private nested fakes:

- **`FakeEyeCareHooks : IEyeCareHooks`** — all four methods return completed
  `Task.FromResult` / no-op. Required for the `EyeCareService` constructor. **Never
  actually invoked during the timed loop** (escalation is suppressed — see below), so its
  implementation cost is irrelevant.
- **`FakeExercise : IExercise`** — single-element pool for `ExerciseRunner`. Same
  suppression reasoning: never invoked during the timed loop.

A minimal `IExercise[] { new FakeExercise() }` is passed to `new ExerciseRunner(pool, seed)`.

### Escalation suppression (critical correctness)

`FatigueMonitor.Score` on the Fixation stream is ≈ 1.0 (fixationRatio ≈ 1.0; boxArea is
tiny so shrinkScore ≈ 1.0; combined and clamped → ≈1.0). The default `FatigueThreshold`
is 0.75, so on a Fixation stream the escalation guard
([`EyeCareService.cs:50-54`](src/EyeCtrl.EyeCare/EyeCareService.cs:50)) is true after
the first few samples.

In production that is correct behavior — it fires the break flow. But the break flow
(`OnBreakDue` → async-void → `ShowBannerAsync` → exercise → summary) is **not a
per-sample path**: it runs at most once per interval and is UI-driven. Letting it fire
during the timed loop would (a) contaminate the measurement with one-shot UI-hook cost,
and (b) set `_busy = true` mid-batch, silently changing the code path measured by every
later sample in the batch.

The `WithFatigue` variant therefore sets `FatigueThreshold = 2.0` (unreachable: Score is
`Math.Clamp`-ed to [0,1]). This isolates the **per-sample fatigue scoring** — the actual
hot path — from the break flow, which has no business being benchmarked here. Documented
inline in the class XML doc.

The `Disabled` and `Enabled_NoFatigue` variants need no mitigation: the escalation guard
is gated on `FatigueMonitorEnabled && Score >= threshold`, and either `Enabled=false`
(`OnGazeSample` returns before fatigue — [`EyeCareService.cs:45`](src/EyeCtrl.EyeCare/EyeCareService.cs:45))
or `FatigueMonitorEnabled=false` (Score stays 0.0 — [`FatigueMonitor.cs:32-34`](src/EyeCtrl.EyeCare/FatigueMonitor.cs:32))
makes the guard false.

## Testability / CI

**Decision: no new test file.** The prior gaze-pipeline-benchmarks spec added
`tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` to guard the generator's
correctness. This spec adds **no new generator code** — it reuses the existing,
already-CI-asserted streams (`GenerateFixation`/`Saccade`/`NoisyFixation`). The
Layer 5 per-batch sizing is local arithmetic (`WindowSeconds * 30 + 1`), not a new
public generator surface that could drift.

The only new testable surface would be asserting the fakes construct and the benchmark
fields are non-null after `[GlobalSetup]` — low value, and BenchmarkDotNet's own pilot
run already fails fast with a clear `InitException` on a null setup. The maintenance
cost of a dedicated test file outweighs the benefit.

CI workflow: **no change.** The existing `dotnet test tests/EyeCtrl.Benchmarks.Tests`
step still passes (no new project, no new tests). The new benchmark classes are picked
up by the existing `--filter *Gaze*` and a new `--filter *Fatigue*` selects Layer 5.

Rejected alternative considered and declined: assert the per-batch sizing math
(`SamplesToFill ≥ WindowSeconds * 30`) in a unit test. This tests arithmetic the
compiler already validates; declined as performative.

## Dependencies

No new NuGet packages. The benchmark project gains one `ProjectReference` to
`EyeCtrl.EyeCare` (which itself pulls only Core). No transitive native/WPF deps
introduced — the benchmark project stays `net10.0` console-clean.

## Documentation

`AGENTS.md` Benchmarks section:

- The gaze-pipeline bullet's component list gains `FatigueMonitor` + "eye-care-pipeline
  integration".
- The class count changes from **"11 benchmark classes, 47 benchmarks total"** to
  **"12 benchmark classes, 62 benchmarks total"**.
- The `--filter *Gaze*` example still selects the whole gaze suite; a new
  `--filter *Fatigue*` example is added alongside the existing per-layer filters.

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Escalation fires mid-batch on Fixation, contaminating `WithFatigue` | `FatigueThreshold = 2.0` (unreachable; Score clamped to [0,1]); documented inline |
| 1000-sample batch under-fills the 60s window, measuring growth not steady-state | Per-batch sizing in the fatigue class: `[GlobalSetup]` pre-fills, `[Benchmark]` replays a 1000-sample steady-state tail |
| `EyeCareService` constructor requires non-empty `ExerciseRunner` pool | Single-element `FakeExercise` pool; never invoked (escalation suppressed) |
| Subscription stacking across iterations | Targeted `[IterationSetup]` builds fresh provider+engine+service each iteration; `[IterationCleanup]` disposes both engine and service |
| `EyeCareSettings.PropertyChanged` handler leak across iterations | `EyeCareService.Dispose()` unsubscribes (`EyeCareService.cs:73-77`); called from `[IterationCleanup]` |
| `FatigueMonitor` allocates unexpectedly in steady state | Anticipated finding, not a surprise — if `MemoryDiagnoser` reports nonzero B/op, the `foreach` re-scan loop is the first place to look. Benchmark exists to confirm the expectation. |
| Generator drift undetected | N/A — no new generator code; existing `GazeStreamGeneratorTests` still covers all reused streams |

## Verification

After implementation:

```bash
# Build (must compile 0 errors; confirms EyeCtrl.EyeCare reference resolves)
dotnet build benchmarks/EyeCtrl.Benchmarks

# Existing generator tests still pass (no new test file added)
dotnet test tests/EyeCtrl.Benchmarks.Tests

# New fatigue micro-layer (Layer 5)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Fatigue*

# Full gaze suite including the new pipeline-with-eye-care methods
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*

# Just the new pipeline integration methods
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*
```

The check-the-results step: after a Release run, inspect
`BenchmarkDotNet.Artifacts/results/` for the new classes. Confirm:
1. Layer 5 `ns/op` grows roughly linearly with `WindowSeconds` (the O(window) claim).
2. Layer 5 `B/op` is ≈0 across all configurations (the allocation-free-steady-state claim).
3. `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` is within noise of the existing
   `Pipeline_OneEuro_Fixation` (the subscriber's existence tax is small).
4. `Pipeline_..._WithFatigue − Pipeline_..._Disabled` ≈ a Layer 5 `Fatigue_Fixation`
   per-sample cost (the integration cost is dominated by the fatigue scan, as expected).
