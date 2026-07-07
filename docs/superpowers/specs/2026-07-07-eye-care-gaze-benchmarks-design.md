# Eye-Care Gaze Pipeline Benchmarks — Design Spec

**Date:** 2026-07-07
**Status:** Draft
**Type:** Benchmark suite extension

## Goal

The gaze pipeline gained a new per-sample subscriber when eye care landed:
`GazeEngine.GazeSampled` → `EyeCareService.OnGazeSample` → `EyeCareScheduler.Update`
(always) + `FatigueMonitor.OnSample` (when enabled) — wired in
[`App.xaml.cs:149-158`](src/EyeCtrl.App/App.xaml.cs:149). The existing 4-layer gaze
benchmark suite (filter → detector → dwell → pipeline, added in
[`2026-06-25-gaze-pipeline-benchmarks-design.md`](2026-06-25-gaze-pipeline-benchmarks-design.md))
has **zero coverage** of this path: `GazePipelineBenchmarks` builds a bare `GazeEngine`
with no eye-care wiring.

`FatigueMonitor.OnSample` is **O(samples-in-window)** — it re-scans the entire rolling
window every sample (the `foreach (var s in _samples)` re-scan loop in
`FatigueMonitor.OnSample`). That is the same cost-shape as `DwellDetector`'s O(targets)
scan that Layer 3 already
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
- **New gaze-stream scenarios** — reuses the existing fixation / saccade / noisy-fixation
  *trajectories* from `GazeStreamGenerator`. The only generator change is adding a `length`
  parameter (defaulted to `SamplesPerBatch`) so Layer 5 can request streams longer than
  1000 samples on a continuous timestamp axis; see "Per-batch sizing". No new trajectory
  shapes, no new noise model.

## Architecture

### File Layout

| File | Status | Purpose |
|------|--------|---------|
| `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorBenchmarks.cs` | NEW | Layer 5 — O(window) fatigue scan, params-scaled (`[Params(10,30,60)]`) |
| `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorSteadyStateBenchmarks.cs` | NEW | Layer 5 — fixed-60s baseline (separate class; no `[Params]`) |
| `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` | MODIFIED | Add `length` parameter (defaulted) to the 3 trajectory methods; default leaves all existing call sites unchanged |
| `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` | MODIFIED | Layer 4 — adds 3 eye-care-wired methods + fakes |
| `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj` | MODIFIED | Add `ProjectReference` to `EyeCtrl.EyeCare` |
| `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` | MODIFIED | Assert non-default `length` returns exact count + continuous timestamps |
| `AGENTS.md` | MODIFIED | Benchmarks section: covered-subjects list + class/benchmark counts |

No change to `Program.cs` (auto-discovery via `BenchmarkSwitcher.FromAssembly`).
No new test file (see Testability).

The benchmark project currently references `EyeCtrl.Core` + `EyeCtrl.LM`. Adding
`EyeCtrl.EyeCare` is one `ProjectReference` line. `EyeCtrl.EyeCare` is `net10.0` and
references only Core — no native deps, no WPF — so the benchmark project stays
framework-clean.

### Benchmark Inventory (15 new across 3 files)

| Class | Layer | Question answered | New benchmarks |
|-------|-------|-------------------|----------------|
| `GazeFatigueMonitorBenchmarks` | 5 | Does the O(window) fatigue scan stay bounded as the window grows? | 9 |
| `GazeFatigueMonitorSteadyStateBenchmarks` | 5 | Per-scenario baseline at the default 60 s window | 3 |
| `GazePipelineBenchmarks` (extended) | 4 | What does the eye-care subscriber add to one full `OnGazePoint`? | 3 |

**Total: 15 new benchmarks (9 + 3 + 3).** Existing 47 → **62 benchmarks across 13 classes**
(was 11 classes / 47 benchmarks; +2 classes, +15 benchmarks).

> The steady-state group is a **separate class** from the params-scaled group, not a
> targeted-setup group inside it. BenchmarkDotNet expands the `[Params]` matrix across
> *every* `[Benchmark]` method in a class — a targeted `[GlobalSetup]` can override the
> value inside the setup body, but it cannot prevent the matrix expansion. Putting the
> fixed-window methods in the params class would make each run 3× with identical numbers
> and different param labels. See "Steady-state single-window" below.

All new classes carry the `Gaze` token so the existing `--filter *Gaze*` suite selector
picks them up alongside `GazeFilterBenchmarks` / `GazeDetectorBenchmarks` /
`GazeDwellBenchmarks` / `GazePipelineBenchmarks`. The fatigue micro-layer is
additionally selectable via `--filter *GazeFatigue*`, and the pipeline-with-eye-care
methods via `--filter *WithEyeCare*`. (Naming the fatigue classes `GazeFatigueMonitor*`
rather than `FatigueMonitor*` is what keeps them inside the `*Gaze*` glob — BenchmarkDotNet
matches the filter against the class-qualified benchmark name, so a class without `Gaze`
in its name would be silently dropped from the suite run.)

## Layer 5 — Fatigue monitor micro-benchmarks (two classes)

Direct analogue of Layer 3 (`GazeDwellBenchmarks`). Constructs `FatigueMonitor` directly
with `new EyeCareSettings { FatigueMonitorEnabled = true }` and the default fixation
radius (50 px, matching `FatigueMonitorTests` and the `DwellSettings` default). Reuses
the existing `GenerateFixation` / `GenerateSaccade` / `GenerateNoisyFixation` streams.

Layer 5 is split across **two classes** because BenchmarkDotNet applies `[Params]` to
every `[Benchmark]` in a class. The params-scaled sweep lives in
`GazeFatigueMonitorBenchmarks`; the fixed-60 s baseline lives in a separate
`GazeFatigueMonitorSteadyStateBenchmarks` without `[Params]`.

### Per-batch sizing (steady-state correctness)

The shared `GazeStreamGenerator.SamplesPerBatch = 1000` is too short for the default
60 s window: at 30 Hz a 60 s window holds 1800 samples, so a single 1000-sample batch
**never fills** it — the benchmark would measure window-growth (enqueue-only, no
eviction, no full-window re-scan), not the steady-state O(window) cost that is the
point of the layer.

Layer 5 therefore needs streams longer than 1000 samples — up to ~2800 (pre-fill 1801 +
1000-sample tail) for the 60 s case. The existing `GenerateFixation/Saccade/NoisyFixation`
methods take only a `seed` and are hard-locked to `SamplesPerBatch`. To get a longer
continuous stream without duplicating the generator logic, **add a `length` parameter**
to each existing method:

```csharp
public static GazePoint[] GenerateFixation(int seed = DefaultSeed, int length = SamplesPerBatch);
public static GazePoint[] GenerateSaccade(int seed = DefaultSeed, int length = SamplesPerBatch);
public static GazePoint[] GenerateNoisyFixation(int seed = DefaultSeed, int length = SamplesPerBatch);
```

The default keeps every existing call site unchanged (Layers 1–4, `GazeStreamGeneratorTests`).
Timestamps remain `i * SamplePeriodMicros` for `i ∈ [0, length)`, so a longer stream is a
**single continuous timestamp axis** — not a concatenation of two independent calls.

**Timestamp continuity is the load-bearing invariant.** `FatigueMonitor.OnSample` evicts
via `while ( Peek().ts < currentTs - windowMicros )`. Two independent `GenerateFixation()`
calls each start timestamps at 0; feeding the second as the "tail" would make `cutoff` go
negative, evict nothing, and silently grow the window during the timed loop — measuring
exactly the growth-phase cost the layer exists to avoid. The numbers still come out; they
are just wrong. The single continuous stream is what makes pre-fill + tail correct.

```
SamplesToFill  = WindowSeconds * 30 + 2     // ≥ one past the first evicting sample
SteadyTail     = 1000                        // the timed replay length
```

> The `+2` (not `+1`): at 33 333 µs/sample, 30·N samples span slightly *under* N seconds,
> so the first sample whose timestamp falls outside a 60 s window is the 1802nd, not the
> 1801st. `+2` clears that bar with margin. This is harmless either way — the timed tail
> evicts on every sample regardless — but the comment is now accurate.

The `[GlobalSetup]` (untimed) pre-feeds `SamplesToFill` samples into the monitor so the
window is full and eviction is active, **then** the `[Benchmark]` method replays only the
`SteadyTail` (1000-sample) suffix. Reported `ns/op` is therefore steady-state per-1000
samples, directly comparable to Layers 1–4 (which all report per-1000-samples). B/op is
likewise per-1000-samples steady-state.

### Params-scaled (the O(window) characterization)

Mirrors Layer 3's `[Params(5,20,50)]` target-count sweep:

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorBenchmarks
{
    [Params(10, 30, 60)]   // seconds → ~300 / 900 / 1800 samples in-window at 30 Hz
    public int WindowSeconds { get; set; }
    // ...
}
```

× 3 scenarios = **9 benchmarks**: `Fatigue_<Scenario>_Win<N>s` — e.g.
`Fatigue_Fixation_Win60s`, `Fatigue_NoisyFixation_Win30s`. Each replayed through a
fresh `FatigueMonitor` pre-filled for that window size.

### Steady-state single-window — `GazeFatigueMonitorSteadyStateBenchmarks`

A **separate class** (no `[Params]`) holding 3 benchmarks at the fixed default window
(60 s): `Fatigue_Fixation`, `Fatigue_Saccade`, `Fatigue_NoisyFixation`. Same per-batch
sizing as the params class (`SamplesToFill = 60 * 30 + 2`, `SteadyTail = 1000`),
hard-coded via a `private const int WindowSeconds = 60`.

```csharp
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorSteadyStateBenchmarks
{
    private const int WindowSeconds = 60;
    // Fatigue_Fixation, Fatigue_Saccade, Fatigue_NoisyFixation
}
```

**Why not a targeted-setup group inside the params class:** BenchmarkDotNet expands the
`[Params]` matrix across *every* `[Benchmark]` method in the class. A targeted
`[GlobalSetup]` body can assign `WindowSeconds = 60` for those methods, but BDN has
already expanded the matrix — the steady-state methods would each run 3 times (once per
param value) with identical numbers but different param labels, polluting the output and
tripling their wall-clock cost. A separate class sidesteps the matrix entirely. The
`GazePipelineBenchmarks` targeted-`[IterationSetup]` pattern is safe there because that
class has **no** `[Params]` property — targeted setups select *which* fresh build runs,
not a param value.

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
`foreach (var s in _samples)` re-scan loop in `FatigueMonitor.OnSample` is the first
place to look.

## Layer 4 extension — `GazePipelineBenchmarks` (integration)

Adds 3 methods that wire a real `EyeCareService` into `GazeEngine.GazeSampled`,
mirroring the production wiring in [`App.xaml.cs:149-158`](src/EyeCtrl.App/App.xaml.cs:149).
The file gains `using EyeCtrl.EyeCare;` and `using EyeCtrl.EyeCare.Exercises;` (for
`EyeCareService` / `ExerciseRunner`). All three use OneEuro + Fixation (the representative
scenario already used by the existing
`Pipeline_OneEuro_Fixation`) so the only variable is the eye-care configuration — a clean
before/after.

| New benchmark | `EyeCareSettings` | What it measures |
|---------------|-------------------|------------------|
| `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` | `Enabled = false` (today's shipping default) | The subscriber's existence tax: `scheduler.Update` + early-return before fatigue |
| `Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue` | `Enabled = true`, `FatigueMonitorEnabled = false` (v1 promoted-on default) | + the `FatigueMonitorEnabled` early-return |
| `Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue` | `Enabled = true`, `FatigueMonitorEnabled = true` (defaults otherwise) | + full O(window) fatigue scan |

All three pass the same lambda args — see "Lambda arguments" below. Read the three as a
ladder: `Disabled` → `Enabled_NoFatigue` → `WithFatigue`. The subtraction that isolates
the O(window) scan is **`WithFatigue − Enabled_NoFatigue`** (the `Disabled` baseline
conflates the scan with the no-fatigue early-return path — see Verification).

### Lambda arguments (load-bearing for escalation suppression)

The benchmark lambda mirrors the production wiring (`App.xaml.cs:149-158`) but with
fixed arguments so the measured path is deterministic across all three variants:

```csharp
engine.GazeSampled += sample =>
    _eyeCareService.OnGazeSample(sample, isPaused: false, canInterrupt: false);
```

- **`isPaused: false`** — keeps active time accruing in `EyeCareScheduler.Update`
  (matches the production non-paused steady state; `true` would skip the accrual branch).
- **`canInterrupt: false`** — the escalation-suppression knob. See the next section.

### Fresh-build-per-iteration (extends the existing pattern)

The existing class already rebuilds a fresh `FakeGazeProvider` + `GazeEngine` per
iteration via targeted `[IterationSetup]` methods (one per benchmark — see
`GazePipelineBenchmarks.SetupDirect`/`SetupSmoothed`/`SetupOneEuroFixation`/
`SetupOneEuroNoisyFixation`), because (a) `InteractionManager` starts in `FreeLook` and
converges to `Fixation`, and (b) the constructor subscribes to the provider's event and
reusing a provider would stack subscriptions. BenchmarkDotNet's `[IterationSetup(Target = ...)]`
targets a **single** method, so each new pipeline benchmark gets its own targeted setup
that delegates to a shared helper:

```csharp
private void RebuildWithEyeCare(EyeCareSettings eyeCareSettings)
{
    _provider = new FakeGazeProvider();
    var settings = new EyeCtrlSettings();
    settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
    _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
    _service = new EyeCareService(eyeCareSettings, BuildExerciseRunner(), new FakeEyeCareHooks());
    _engine.GazeSampled += sample =>
        _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
    _engine.Start();
}

[IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Disabled))]
public void SetupEyeCareDisabled() => RebuildWithEyeCare(DisabledSettings());

[IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue))]
public void SetupEyeCareNoFatigue() => RebuildWithEyeCare(EnabledNoFatigueSettings());

[IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue))]
public void SetupEyeCareWithFatigue() => RebuildWithEyeCare(WithFatigueSettings());
```

**Fatigue window pre-fill (apples-to-apples with Layer 5).** The timed batch is 1000
samples (~33 s at 30 Hz), but `FatigueMonitor`'s window is 60 s. Without pre-fill the
`WithFatigue` benchmark would measure the window-growth phase (`O(0)→O(1000)`) rather
than steady-state `O(1800)` — not the same regime Layer 5 measures. The
`RebuildWithEyeCare` helper therefore pre-feeds ~60 s (1802 samples) of the Fixation
stream into the engine via `_provider.SimulateGaze` **before** returning, so the timed
batch opens on a full, evicting window. This also pre-warms `InteractionManager` past
its `FreeLook → Fixation` transition, which is the intended steady state for the
integration measurement. (The `Disabled`/`Enabled_NoFatigue` variants pre-fill too, for
symmetry — the cost is identical since their fatigue path early-returns.)

**Cleanup null-guard:** the existing `[IterationCleanup]` is untargeted and runs for
*every* benchmark in the class, including the 4 original methods that build no service.
So the cleanup must null-guard `_service` (`_service?.Dispose()`) — or the original 4
benchmarks NRE on cleanup. `_engine` is already non-null for every method so it needs no
guard, but `_service` is null for the original 4 and non-null only for the 3 eye-care
variants.

**Note on the `GazeSampled` subscription:** `EyeCareService.Dispose()` only unsubscribes
from `_settings.PropertyChanged` and `_scheduler.BreakDue` — it does **not** unsubscribe
the `engine.GazeSampled` lambda (the production wiring in `App.xaml.cs` never does
either; both are app-lifetime singletons there). This is intentional and safe in the
benchmark because the engine and service are co-lifetime per iteration: both fall out of
scope together in `[IterationCleanup]` and are GC'd as a pair, so the dangling delegate
reference never stacks. The cleanup order (engine dispose, then service dispose, then
both dropped) must be preserved — if a future change reused the engine across iterations
the lambda adapter would need explicit `-=` removal.

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
is 0.75, so on a Fixation stream the escalation guard in `EyeCareService.OnGazeSample`
would be true after the first few samples — *if* `canInterrupt` were true.

In production that is correct behavior — it fires the break flow. But the break flow
(`OnBreakDue` → async-void → `ShowBannerAsync` → exercise → summary) is **not a
per-sample path**: it runs at most once per interval and is UI-driven. Letting it fire
during the timed loop would (a) contaminate the measurement with one-shot UI-hook cost,
and (b) set `_busy = true` mid-batch, silently changing the code path measured by every
later sample in the batch.

**Suppression mechanism: `canInterrupt: false`** in the benchmark lambda (see "Lambda
arguments"). This is preferable to an out-of-range setting because:

1. **It stays inside the validated range.** `EyeCareSettings.Validate()` enforces
   `FatigueThreshold ∈ [0.0, 1.0]`; setting `2.0` is stored as-is (there is no clamping
   `OnChanging` guard) and only "works" because the benchmark never calls `Validate()`.
   `canInterrupt: false` needs no invalid value.
2. **It suppresses escalation without touching the hot path.** The escalation guard is
   `FatigueMonitorEnabled && !_busy && canInterrupt && Score >= threshold`
   (`EyeCareService.OnGazeSample`). `_fatigueMonitor.OnSample` — the actual O(window)
   path under measurement — runs on the line *before* the guard. So `canInterrupt: false`
   keeps the guard false while the full fatigue scan still executes every sample.

This mirrors production semantics: `canInterrupt: true` only in `FreeLook`
(`App.xaml.cs:149-158`); the benchmark measures the "overlay up / not FreeLook" path,
which is the steady state the break flow waits out. Documented inline in the class XML doc.

The `Disabled` and `Enabled_NoFatigue` variants need no mitigation: the escalation guard
is gated on `FatigueMonitorEnabled && Score >= threshold`, and either `Enabled=false`
(`OnGazeSample` returns before fatigue) or `FatigueMonitorEnabled=false` (Score stays
0.0 — `FatigueMonitor.OnSample` early-returns when the monitor is disabled) makes the
guard false.

## Testability / CI

**Decision: extend the existing generator test, no new test file.** This spec adds a
`length` parameter to `GazeStreamGenerator.GenerateFixation/Saccade/NoisyFixation`
(see "Per-batch sizing"). That is new public generator surface, so the drift guard the
prior spec established in `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs`
must cover it. Concretely, the existing tests already call every generator with the
default `length`, so the regression guard is: add assertions that a non-default `length`
returns exactly `length` points and that its timestamps are continuous
(`points[i].TimestampMicroseconds == i * SamplePeriodMicros` for `i ∈ [0, length)`).

This is exactly the invariant Layer 5 depends on (see "Timestamp continuity is the
load-bearing invariant"). A future change that broke continuity — e.g. refactoring the
generator to restart timestamps — would fail this test instead of silently making Layer 5
measure the wrong thing.

The only other testable surface would be asserting the fakes construct and the benchmark
fields are non-null after `[GlobalSetup]` — low value, and BenchmarkDotNet's own pilot
run already fails fast with a clear `InitException` on a null setup.

CI workflow: **no change.** The existing `dotnet test tests/EyeCtrl.Benchmarks.Tests`
step picks up the new assertions (same file, same project). The new benchmark classes
carry the `Gaze` token and are picked up by the existing `--filter *Gaze*`;
`--filter *GazeFatigue*` selects Layer 5.

## Dependencies

No new NuGet packages. The benchmark project gains one `ProjectReference` to
`EyeCtrl.EyeCare` (which itself pulls only Core). No transitive native/WPF deps
introduced — the benchmark project stays `net10.0` console-clean.

## Documentation

`AGENTS.md` Benchmarks section:

- The gaze-pipeline bullet's component list gains `FatigueMonitor` + "eye-care-pipeline
  integration".
- The class count changes from **"11 benchmark classes, 47 benchmarks total"** to
  **"13 benchmark classes, 62 benchmarks total"**.
- The `--filter *Gaze*` example still selects the whole gaze suite (now including the
  two `GazeFatigueMonitor*` classes); a `--filter *GazeFatigue*` example is added
  alongside the existing per-layer filters.

## Risk Assessment

| Risk | Mitigation |
|------|-----------|
| Escalation fires mid-batch on Fixation, contaminating `WithFatigue` | `canInterrupt: false` in the benchmark lambda — the escalation guard requires it, but `_fatigueMonitor.OnSample` (the measured path) runs before the guard, so the full scan still executes every sample. Stays in the validated settings range (no out-of-range `FatigueThreshold`). |
| 1000-sample batch under-fills the 60s window, measuring growth not steady-state | **Layer 5:** new `length` generator parameter; `[GlobalSetup]` pre-fills on a continuous timestamp axis, `[Benchmark]` replays a 1000-sample steady-state tail. **Layer 4:** `RebuildWithEyeCare` pre-feeds ~60 s of samples through the engine before the timed batch, so the integration measurement opens on a full, evicting window (apples-to-apples with Layer 5). |
| Timestamp discontinuity silently makes Layer 5 measure growth not steady-state | Single continuous stream (not two `GenerateFixation()` calls); continuity asserted in `GazeStreamGeneratorTests` (see Testability) |
| `EyeCareService` constructor requires non-empty `ExerciseRunner` pool | Single-element `FakeExercise` pool; never invoked (escalation suppressed via `canInterrupt: false`) |
| Shared `[IterationCleanup]` NREs on the 4 original benchmarks (no service built) | Null-guard `_service?.Dispose()`; documented in "Fresh-build-per-iteration" |
| Subscription stacking across iterations | Targeted `[IterationSetup]` builds fresh provider+engine+service each iteration; `[IterationCleanup]` disposes both engine and service. Note: `EyeCareService.Dispose()` does not unsubscribe the `engine.GazeSampled` lambda, but engine+service are co-lifetime per iteration (GC'd together) so the reference cannot stack — see "Fresh-build-per-iteration" |
| `EyeCareSettings.PropertyChanged` handler leak across iterations | `EyeCareService.Dispose()` unsubscribes; called from `[IterationCleanup]` |
| `FatigueMonitor` allocates unexpectedly in steady state | Anticipated finding, not a surprise — if `MemoryDiagnoser` reports nonzero B/op, the `foreach` re-scan loop is the first place to look. Benchmark exists to confirm the expectation. |
| Generator `length` parameter drifts (e.g. continuity broken by a refactor) | `GazeStreamGeneratorTests` extended to assert non-default `length` returns exact count + continuous timestamps |

## Verification

After implementation:

```bash
# Build (must compile 0 errors; confirms EyeCtrl.EyeCare reference resolves)
dotnet build benchmarks/EyeCtrl.Benchmarks

# Generator tests including the new length/continuity assertions
dotnet test tests/EyeCtrl.Benchmarks.Tests

# New fatigue micro-layer (Layer 5)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigue*

# Full gaze suite including the new pipeline-with-eye-care methods
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*

# Just the new pipeline integration methods
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*
```

The check-the-results step: after a Release run, inspect
`BenchmarkDotNet.Artifacts/results/` for the new classes. Confirm:
1. Layer 5 params-sweep `ns/op` grows roughly linearly with `WindowSeconds` (the O(window)
   claim). The steady-state class (fixed 60 s) is a separate single-number baseline, not
   part of this linearity check.
2. Layer 5 `B/op` is ≈0 across all configurations (the allocation-free-steady-state claim).
3. `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` is within noise of the existing
   `Pipeline_OneEuro_Fixation` (the subscriber's existence tax is small).
4. Compare the three pipeline numbers as a ladder, not a single subtraction:

   - `Disabled` — scheduler-only tax (baseline).
   - `Enabled_NoFatigue` — `Disabled` + the `FatigueMonitor.OnSample` early-return
     (`if (!_settings.FatigueMonitorEnabled) { Score = 0.0; return; }`). Should be within
     a few ns of `Disabled`.
   - `WithFatigue` — `Enabled_NoFatigue` + the full O(window) scan.

   The subtraction that isolates the scan is **`WithFatigue − Enabled_NoFatigue`**, *not*
   `WithFatigue − Disabled` (the latter conflates the scan with the early-return cost).
   That isolated delta should be in the **same order of magnitude** as a Layer 5
   `Fatigue_Fixation` per-sample cost — not numerically equal. The integration path adds
   `GazeSampled` event dispatch and `EyeCareService.OnGazeSample` method-dispatch overhead
   the micro-benchmark doesn't, so expect the pipeline delta to be a few ns higher than
   the isolated fatigue scan. The claim is only that the fatigue scan *dominates* the
   integration cost; if the subtraction is an order of magnitude off, something unexpected
   is on the path.
