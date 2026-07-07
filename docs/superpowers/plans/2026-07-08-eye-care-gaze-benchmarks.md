# Eye-Care Gaze Pipeline Benchmarks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add BenchmarkDotNet coverage for the eye-care per-sample gaze subscriber (`FatigueMonitor` O(window) scan + the `EyeCareService.OnGazeSample` integration into `GazeEngine.GazeSampled`), closing the gap left by the existing 4-layer gaze benchmark suite.

**Architecture:** Two new Layer 5 micro-benchmark classes (`GazeFatigueMonitorBenchmarks`, params-scaled; `GazeFatigueMonitorSteadyStateBenchmarks`, fixed 60s) characterize the O(window) fatigue scan. A Layer 4 extension adds three eye-care-wired methods to the existing `GazePipelineBenchmarks`, measuring the subscriber's marginal cost in each production configuration (disabled / enabled-no-fatigue / enabled-with-fatigue). A small generator change (a defaulted `length` parameter) supplies the continuous >1000-sample streams Layer 5 needs for steady-state measurement.

**Tech Stack:** .NET 10, BenchmarkDotNet 0.15.8, xUnit + NFluent (test assertions via the shared `tests/Directory.Build.props`).

**Spec:** [`docs/superpowers/specs/2026-07-07-eye-care-gaze-benchmarks-design.md`](../specs/2026-07-07-eye-care-gaze-benchmarks-design.md)

**Conventions (from `AGENTS.md`):**
- File-scoped namespaces, Allman braces, `_camelCase` fields, nullable enabled.
- Tests use **NFluent** (`Check.That(...)`), not xUnit's `Assert.*`. Global `using Xunit` + `using NFluent` come from `tests/Directory.Build.props`.
- Test namespaces mirror source namespace.
- No mocking library — private nested fake classes.

---

## Task 1: Add `length` parameter to the gaze stream generators (TDD)

The existing `GenerateFixation/Saccade/NoisyFixation` are hard-locked to `SamplesPerBatch = 1000`. Layer 5 needs streams up to ~2800 samples on a **continuous** timestamp axis. Add a defaulted `length` parameter so all existing call sites are unchanged.

**Files:**
- Modify: `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` (3 methods: lines 33, 52, 81)
- Test: `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these tests to `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` (inside the existing `GazeStreamGeneratorTests` class, before the private `Generate` helper at the bottom):

```csharp
[Theory]
[InlineData("Fixation", 2500)]
[InlineData("Saccade", 2500)]
[InlineData("NoisyFixation", 2500)]
public void Generate_WithExplicitLength_ReturnsExactCount(string scenario, int length)
{
    var points = Generate(scenario, length: length);
    Check.That(points).HasSize(length);
}

[Theory]
[InlineData("Fixation", 2500)]
[InlineData("Saccade", 2500)]
[InlineData("NoisyFixation", 2500)]
public void Generate_WithExplicitLength_HasContinuousTimestamps(string scenario, int length)
{
    var points = Generate(scenario, length: length);
    for (int i = 0; i < points.Length; i++)
    {
        Check.That(points[i].TimestampMicroseconds).IsEqualTo(i * GazeStreamGenerator.SamplePeriodMicros);
    }
}

[Theory]
[InlineData("Fixation")]
[InlineData("Saccade")]
[InlineData("NoisyFixation")]
public void Generate_DefaultLength_StillSamplesPerBatch(string scenario)
{
    var points = Generate(scenario);
    Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
}
```

Then update the private `Generate` helper at the bottom of the file to accept the optional `length`:

```csharp
private static GazePoint[] Generate(string scenario, int seed = 42, int? length = null) => scenario switch
{
    "Fixation" => length is null
        ? GazeStreamGenerator.GenerateFixation(seed)
        : GazeStreamGenerator.GenerateFixation(seed, length.Value),
    "Saccade" => length is null
        ? GazeStreamGenerator.GenerateSaccade(seed)
        : GazeStreamGenerator.GenerateSaccade(seed, length.Value),
    "NoisyFixation" => length is null
        ? GazeStreamGenerator.GenerateNoisyFixation(seed)
        : GazeStreamGenerator.GenerateNoisyFixation(seed, length.Value),
    "WithDropout" => GazeStreamGenerator.GenerateWithDropout(seed),
    _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario)),
};
```

Note: `GenerateWithDropout` is intentionally **not** given a `length` parameter — Layer 5 does not use it, and the dropout-gap placement logic is calibrated to the 1000-sample batch. Leave it as-is.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: **COMPILE ERROR** — `GenerateFixation(seed, length)` has no overload taking `length`. The new `[Theory]` methods won't compile. This confirms the tests exercise the new surface.

- [ ] **Step 3: Add the `length` parameter to the three generators**

In `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs`, change each of the three method signatures and their internal array sizing/loop bound. The body logic (phase, noise, coordinate computation) is unchanged — only the array length and loop bound change from `SamplesPerBatch` to `length`.

For `GenerateFixation` (line 33):

```csharp
public static GazePoint[] GenerateFixation(int seed = DefaultSeed, int length = SamplesPerBatch)
{
    var rng = new Random(seed);
    var points = new GazePoint[length];
    for (int i = 0; i < length; i++)
    {
        long t = i * SamplePeriodMicros;
        double phase = 2 * Math.PI * 0.3 * (t / 1_000_000.0); // ~0.3 Hz over elapsed seconds
        double dx = Math.Cos(phase) * 8.0 + Gaussian(rng) * 1.5;
        double dy = Math.Sin(phase) * 8.0 + Gaussian(rng) * 1.5;
        points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
    }
    return points;
}
```

For `GenerateSaccade` (line 52) — same signature change; replace both `SamplesPerBatch` occurrences inside (the array allocation and the loop bound) with `length`. The `segmentLen = SamplesPerBatch / targets.Length` computation stays as `SamplesPerBatch` because the 5 target waypoints are calibrated to a 1000-sample division — but for `length != 1000` this would mis-segment. So change `segmentLen` to `length / targets.Length` to keep the segmentation proportional:

```csharp
public static GazePoint[] GenerateSaccade(int seed = DefaultSeed, int length = SamplesPerBatch)
{
    var rng = new Random(seed);
    var points = new GazePoint[length];
    // 5 targets, dividing the batch into 5 proportional segments.
    double[][] targets =
    {
        new[] { 960.0, 540.0 },
        new[] { 200.0, 200.0 },
        new[] { 1500.0, 300.0 },
        new[] { 400.0, 800.0 },
        new[] { 1200.0, 700.0 },
    };
    int segmentLen = length / targets.Length;
    for (int i = 0; i < length; i++)
    {
        long t = i * SamplePeriodMicros;
        int seg = Math.Min(i / segmentLen, targets.Length - 1);
        double tx = targets[seg][0] + Gaussian(rng) * 0.5;
        double ty = targets[seg][1] + Gaussian(rng) * 0.5;
        points[i] = new GazePoint(tx, ty, t, IsValid: true);
    }
    return points;
}
```

For `GenerateNoisyFixation` (line 81) — same signature change; replace `SamplesPerBatch` with `length` in both the array allocation and loop bound:

```csharp
public static GazePoint[] GenerateNoisyFixation(int seed = DefaultSeed, int length = SamplesPerBatch)
{
    var rng = new Random(seed);
    var points = new GazePoint[length];
    for (int i = 0; i < length; i++)
    {
        long t = i * SamplePeriodMicros;
        double dx = Gaussian(rng) * 15.0;
        double dy = Gaussian(rng) * 15.0;
        points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
    }
    return points;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: **ALL PASS** — the existing 9 tests (unchanged behavior at default length) plus the 9 new `[Theory]` cases. 18 total.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs
git commit -m "feat(benchmarks): add length parameter to gaze stream generators

Defaulted to SamplesPerBatch so all existing call sites are unchanged.
Supplies the continuous >1000-sample streams the fatigue benchmarks need
for steady-state measurement. GenerateWithDropout intentionally unchanged.
Adds length/continuity/default-length test coverage."
```

---

## Task 2: Add `EyeCtrl.EyeCare` project reference

The benchmark project must reference `EyeCtrl.EyeCare` to use `EyeCareService`, `EyeCareSettings`, `ExerciseRunner`, `FatigueMonitor`.

**Files:**
- Modify: `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj`

- [ ] **Step 1: Add the ProjectReference**

In `benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj`, add a `ProjectReference` to `EyeCtrl.EyeCare` inside the existing `<ItemGroup>` that already references `EyeCtrl.Core` and `EyeCtrl.LM`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\EyeCtrl.Core\EyeCtrl.Core.csproj" />
    <ProjectReference Include="..\..\src\EyeCtrl.LM\EyeCtrl.LM.csproj" />
    <ProjectReference Include="..\..\src\EyeCtrl.EyeCare\EyeCtrl.EyeCare.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: Verify the build resolves the new reference**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.** No code uses `EyeCtrl.EyeCare` types yet, but the reference must resolve (confirms `EyeCtrl.EyeCare` is `net10.0` with no native/WPF deps that would break the benchmark project).

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj
git commit -m "build(benchmarks): reference EyeCtrl.EyeCare for eye-care benchmarks"
```

---

## Task 3: Layer 5 — `GazeFatigueMonitorBenchmarks` (params-scaled)

The O(window) characterization, mirroring Layer 3's `[Params(5,20,50)]` target-count sweep. Direct analogue of `GazeDwellBenchmarks`.

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorBenchmarks.cs`

- [ ] **Step 1: Create the benchmark class**

Create `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 5 — FatigueMonitor O(window) scan, params-scaled. The fatigue monitor re-scans the
/// entire rolling window every sample (the foreach over _samples in OnSample). This class
/// characterizes how that cost scales with the window size, mirroring Layer 3's
/// (GazeDwellBenchmarks) [Params(5,20,50)] target-count sweep.
/// </summary>
/// <remarks>
/// PER-BATCH SIZING: the shared GazeStreamGenerator.SamplesPerBatch (1000) is too short for
/// the 60s default window (1800 samples at 30Hz). This class generates longer continuous
/// streams via the length parameter, pre-fills the window in [GlobalSetup] (untimed), then
/// the [Benchmark] methods replay a 1000-sample steady-state tail. Reported ns/op is therefore
/// steady-state per-1000-samples, directly comparable to Layers 1-4.
///
/// TIMESTAMP CONTINUITY (load-bearing): the pre-fill and timed tail are slices of ONE
/// continuous GenerateFixation(length: ...) stream. FatigueMonitor evicts via
/// Peek().ts &lt; currentTs - windowMicros; two independent calls (timestamps restarting at 0)
/// would make cutoff go negative, evict nothing, and silently measure the growth phase.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorBenchmarks
{
    private const int SamplesPerSec = 30;          // 30 Hz
    private const int SteadyTail = 1000;           // timed replay length, matches Layers 1-4

    [Params(10, 30, 60)]   // seconds → ~300 / 900 / 1800 samples in-window at 30 Hz
    public int WindowSeconds { get; set; }

    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;

    // Bumped by [Params] so each value rebuilds the monitors with the right window + pre-fill.
    [GlobalSetup]
    public void Setup()
    {
        int samplesToFill = WindowSeconds * SamplesPerSec + 2;   // +2: 33333µs/sample → 30·N < Ns
        int total = samplesToFill + SteadyTail;

        // One continuous stream each; the tail (timestamps continuing past samplesToFill) is timed.
        var fix = GazeStreamGenerator.GenerateFixation(length: total);
        var sac = GazeStreamGenerator.GenerateSaccade(length: total);
        var noi = GazeStreamGenerator.GenerateNoisyFixation(length: total);

        _fixation = fix[^SteadyTail..];
        _saccade = sac[^SteadyTail..];
        _noisyFixation = noi[^SteadyTail..];

        _fatigueFixation = BuildPrefilled(fix, samplesToFill);
        _fatigueSaccade = BuildPrefilled(sac, samplesToFill);
        _fatigueNoisyFixation = BuildPrefilled(noi, samplesToFill);
    }

    [Benchmark]
    public void Fatigue_Fixation_Win() => Replay(_fixation, _fatigueFixation);

    [Benchmark]
    public void Fatigue_Saccade_Win() => Replay(_saccade, _fatigueSaccade);

    [Benchmark]
    public void Fatigue_NoisyFixation_Win() => Replay(_noisyFixation, _fatigueNoisyFixation);

    // Naming note: the spec sketches `Fatigue_Fixation_Win60s` etc., but BenchmarkDotNet does
    // NOT interpolate [Params] values into the benchmark display name — [Benchmark(name)]
    // takes a static string. The actual output format is a "Method" column (`Fatigue_Fixation_Win`)
    // plus a separate "WindowSeconds" column (10/30/60), exactly mirroring how
    // GazeDwellBenchmarks.Update relies on the TargetCount column. The `Win` suffix is a
    // qualifier distinguishing these from the steady-state class's Fatigue_Fixation/Saccade/
    // NoisyFixation; the WindowSeconds param column carries the actual value.

    private static void Replay(GazePoint[] stream, FatigueMonitor monitor)
    {
        for (int i = 0; i < stream.Length; i++)
            monitor.OnSample(stream[i]);
    }

    private FatigueMonitor BuildPrefilled(GazePoint[] full, int samplesToFill)
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var monitor = new FatigueMonitor(settings, fixationRadiusPixels: 50, WindowSeconds);
        for (int i = 0; i < samplesToFill; i++)
            monitor.OnSample(full[i]);
        return monitor;
    }
}
```

Key correctness points baked into the code:
- `samplesToFill = WindowSeconds * 30 + 2` — the `+2` matches the spec's off-by-one fix (at 33333µs/sample, 30·N samples span slightly *under* N seconds, so the first evicting sample is the 1802nd for a 60s window).
- `fix[^SteadyTail..]` takes the trailing 1000 samples of the continuous stream — timestamps continue monotonically from the pre-fill, so the timed batch opens on a full, evicting window.
- `FatigueMonitor(settings, 50, WindowSeconds)` — the 3rd constructor arg is `windowSeconds` (see `FatigueMonitor(EyeCareSettings, double fixationRadiusPixels, int windowSeconds)`).

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.**

- [ ] **Step 3: Smoke-run just this class to confirm it executes (not full measurement)**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigueMonitorBenchmarks* --job short`
Expected: **Runs without throwing.** Produces 9 benchmark rows (3 scenarios × `[Params(10,30,60)]`). Numbers will be noisy from `--job short`; this is a smoke check, not a measurement. Confirm no `InitException` in the output.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorBenchmarks.cs
git commit -m "feat(benchmarks): add GazeFatigueMonitorBenchmarks (Layer 5, params-scaled)

O(window) fatigue scan characterization mirroring GazeDwellBenchmarks.
[Params(10,30,60)] seconds × 3 scenarios = 9 benchmarks. Pre-fills the
window on a continuous timestamp axis, times a 1000-sample steady-state tail."
```

---

## Task 4: Layer 5 — `GazeFatigueMonitorSteadyStateBenchmarks` (fixed 60s)

Separate class (no `[Params]`) for the apples-to-apples 60s baseline. BenchmarkDotNet applies `[Params]` to every method in a class, so the steady-state group must be its own class to avoid triple-running with identical numbers.

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorSteadyStateBenchmarks.cs`

- [ ] **Step 1: Create the benchmark class**

Create `benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorSteadyStateBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 5 — FatigueMonitor steady-state baseline at the default 60s window. Separate class
/// from GazeFatigueMonitorBenchmarks because BenchmarkDotNet applies [Params] to every
/// [Benchmark] in a class: putting these in the params class would triple-run them with
/// identical numbers and different param labels.
/// </summary>
/// <remarks>
/// Same per-batch sizing + timestamp-continuity discipline as the params class: pre-fill the
/// 60s window on a continuous stream, time a 1000-sample steady-state tail.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorSteadyStateBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;

    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;

    [GlobalSetup]
    public void Setup()
    {
        int samplesToFill = WindowSeconds * SamplesPerSec + 2;
        int total = samplesToFill + SteadyTail;

        var fix = GazeStreamGenerator.GenerateFixation(length: total);
        var sac = GazeStreamGenerator.GenerateSaccade(length: total);
        var noi = GazeStreamGenerator.GenerateNoisyFixation(length: total);

        _fixation = fix[^SteadyTail..];
        _saccade = sac[^SteadyTail..];
        _noisyFixation = noi[^SteadyTail..];

        _fatigueFixation = BuildPrefilled(fix, samplesToFill);
        _fatigueSaccade = BuildPrefilled(sac, samplesToFill);
        _fatigueNoisyFixation = BuildPrefilled(noi, samplesToFill);
    }

    [Benchmark]
    public void Fatigue_Fixation() => Replay(_fixation, _fatigueFixation);

    [Benchmark]
    public void Fatigue_Saccade() => Replay(_saccade, _fatigueSaccade);

    [Benchmark]
    public void Fatigue_NoisyFixation() => Replay(_noisyFixation, _fatigueNoisyFixation);

    private static void Replay(GazePoint[] stream, FatigueMonitor monitor)
    {
        for (int i = 0; i < stream.Length; i++)
            monitor.OnSample(stream[i]);
    }

    private static FatigueMonitor BuildPrefilled(GazePoint[] full, int samplesToFill)
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var monitor = new FatigueMonitor(settings, fixationRadiusPixels: 50, WindowSeconds);
        for (int i = 0; i < samplesToFill; i++)
            monitor.OnSample(full[i]);
        return monitor;
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.**

- [ ] **Step 3: Smoke-run just this class**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigueMonitorSteadyState* --job short`
Expected: **Runs without throwing.** 3 benchmark rows. No `InitException`.

- [ ] **Step 4: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeFatigueMonitorSteadyStateBenchmarks.cs
git commit -m "feat(benchmarks): add GazeFatigueMonitorSteadyStateBenchmarks (Layer 5, 60s)

Fixed-60s baseline, separate class (no [Params]) so BDN doesn't triple-run
it. Apples-to-apples per-scenario single number at the production default window."
```

---

## Task 5: Layer 4 — extend `GazePipelineBenchmarks` with eye-care-wired methods

Three new methods wiring a real `EyeCareService` into `GazeEngine.GazeSampled`, mirroring the production wiring in `App.xaml.cs:149-158`. Plus two new fakes (`FakeEyeCareHooks`, `FakeExercise`).

**Files:**
- Modify: `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs`

- [ ] **Step 1: Add the `using` directives and the eye-care fields**

At the top of `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs`, add these usings (the existing usings for `EyeCtrl.Core.*` are already present):

```csharp
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;
```

`EyeCtrl.EyeCare` for `EyeCareService` / `FatigueMonitor`; `EyeCtrl.EyeCare.Exercises` for `IExercise` / `ExerciseRunner`; `EyeCtrl.EyeCare.Scoring` for `ExerciseResult` (returned by `FakeExercise.Complete()`). `IEyeCareHooks` and `EyeCareExerciseOutcome` are in `EyeCtrl.Core.Interfaces`, which the file already imports.

In the class body, alongside the existing `_fixation`/`_noisyFixation` fields and the `_provider`/`_engine` fields, add:

```csharp
    // Continuous stream for the eye-care variants: pre-fill prefix + timed tail on one timestamp axis.
    // Built in GlobalSetup from a single GenerateFixation(length: PrefillSamples + SteadyTail).
    private const int EyeCareWindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;
    private const int PrefillSamples = EyeCareWindowSeconds * SamplesPerSec + 2;   // 1802

    private GazePoint[] _fixationTimed = null!;   // trailing 1000-sample slice, continuous with the pre-fill
    private GazePoint[] _fixationPrefill = null!;  // 1802-sample prefix, pre-fed in RebuildWithEyeCare
    private EyeCareService _service = null!;
```

- [ ] **Step 2: Extend `GlobalSetup` to build the continuous eye-care stream**

In the existing `[GlobalSetup] public void Setup()` method, after the existing `_noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();` line, add:

```csharp
        // One continuous stream for the eye-care variants: prefix pre-fed, tail timed.
        // The timed tail's timestamps continue monotonically from the pre-fill, so the
        // FatigueMonitor window is full and evicting when the timed batch starts.
        var fixationLong = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SteadyTail);
        _fixationPrefill = fixationLong[..PrefillSamples];
        _fixationTimed = fixationLong[PrefillSamples..];
```

- [ ] **Step 3: Add the three new benchmark methods**

After the existing `Pipeline_OneEuro_NoisyFixation` method (around line 72), add:

```csharp
    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Disabled() => Replay(_fixationTimed);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue() => Replay(_fixationTimed);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue() => Replay(_fixationTimed);
```

All three replay `_fixationTimed` (not the shared `_fixation`) — the timestamp-continuity invariant.

- [ ] **Step 4: Add the three targeted `[IterationSetup]` methods and the shared helper**

After the existing `[IterationSetup] public void SetupOneEuroNoisyFixation()` method (around line 57), add:

```csharp
    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Disabled))]
    public void SetupEyeCareDisabled() => RebuildWithEyeCare(BuildDisabledSettings());

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue))]
    public void SetupEyeCareNoFatigue() => RebuildWithEyeCare(BuildEnabledNoFatigueSettings());

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue))]
    public void SetupEyeCareWithFatigue() => RebuildWithEyeCare(BuildWithFatigueSettings());
```

Then, after the existing private `Rebuild` method (around line 89), add the helper + the three settings factories:

```csharp
    // Builds a fresh provider+engine+service, subscribes the service to GazeSampled (the production
    // wiring at App.xaml.cs:149-158), starts the engine, then pre-feeds PrefillSamples of the
    // continuous fixation stream so the FatigueMonitor window is full and evicting when the timed
    // batch starts. canInterrupt:false suppresses escalation (the guard requires it) while the full
    // O(window) scan still runs every sample — OnSample executes before the guard.
    private void RebuildWithEyeCare(EyeCtrlSettings settings)
    {
        _provider = new FakeGazeProvider();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(new IExercise[] { new FakeExercise() }, seed: 0),
            new FakeEyeCareHooks());
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < _fixationPrefill.Length; i++)
            _provider.SimulateGaze(_fixationPrefill[i]);
    }

    private static EyeCtrlSettings BuildDisabledSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = false;   // today's shipping default; OnGazeSample early-returns before fatigue
        return s;
    }

    private static EyeCtrlSettings BuildEnabledNoFatigueSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = true;
        s.EyeCare.FatigueMonitorEnabled = false;   // v1 promoted-on default; fatigue early-returns
        return s;
    }

    private static EyeCtrlSettings BuildWithFatigueSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = true;
        s.EyeCare.FatigueMonitorEnabled = true;    // full O(window) scan every sample
        // FatigueThreshold stays at its default 0.75 — escalation is suppressed via canInterrupt:false,
        // which keeps the setting inside its validated [0,1] range.
        return s;
    }
```

- [ ] **Step 5: Null-guard `_service` in `[IterationCleanup]`**

The existing `[IterationCleanup]` is untargeted and runs for **all** benchmarks in the class, including the 4 original methods that build no service. Replace the existing cleanup method:

```csharp
    [IterationCleanup]
    public void IterationCleanup() => _engine.Dispose();
```

with:

```csharp
    [IterationCleanup]
    public void IterationCleanup()
    {
        _engine.Dispose();
        _service?.Dispose();
        _service = null!;
    }
```

Engine first, then service — matches the spec's stated cleanup order (engine dispose, then
service dispose, then both dropped). Safe either way in practice (neither `Dispose` touches
the other), but the order is preserved as a documented invariant: if a future change ever
reused the engine across iterations, disposing the service first could leave the engine's
`GazeSampled` lambda pointing at a disposed service during the window before engine dispose.
Setting `_service = null!` after dispose ensures a clean state for the next iteration's setup.

- [ ] **Step 6: Add the two new fakes**

At the bottom of the class, after the existing `FakeInputSimulator` private nested class (before the closing class brace), add:

```csharp
    // Required for EyeCareService construction; never actually invoked during the timed loop
    // (escalation is suppressed via canInterrupt:false, so OnBreakDue never fires).
    private sealed class FakeEyeCareHooks : IEyeCareHooks
    {
        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(false);
        public Task ShowExerciseAsync(
            string exerciseName,
            long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt,
            Action<PhysicalPoint, long> recordGaze) => Task.CompletedTask;
        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;
        public void CancelActive() { }
    }

    // Single-element pool for ExerciseRunner; never invoked (escalation suppressed).
    private sealed class FakeExercise : IExercise
    {
        public string Name => "Fake";
        public double OnTargetRadiusPixels => 50.0;
        public PhysicalPoint GetTargetAt(long timestampMicroseconds) => new(960, 540);
        public void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds) { }
        public ExerciseResult Complete() => new("Fake", 0, 0, null, 0, "");
        public void Reset() { }
    }
```

- [ ] **Step 7: Verify it compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.** (If `IEyeCareHooks` or `EyeCareExerciseOutcome` aren't found, confirm `using EyeCtrl.Core.Interfaces;` is present — it already is in the existing file.)

- [ ] **Step 8: Smoke-run the new pipeline methods**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare* --job short`
Expected: **Runs without throwing.** 3 benchmark rows. No `InitException`. Confirm the `WithFatigue` variant doesn't trigger the break flow (no console output about banner/exercise — if `FakeEyeCareHooks` were called, `ShowBannerAsync` returns `false` which would snooze, but it should never be called at all because `canInterrupt:false` blocks the guard).

- [ ] **Step 9: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs
git commit -m "feat(benchmarks): wire EyeCareService into GazePipelineBenchmarks (Layer 4)

Three new methods measure the eye-care subscriber's marginal per-sample cost:
Disabled (scheduler-only), Enabled_NoFatigue (+ early-return), WithFatigue
(+ full O(window) scan). canInterrupt:false suppresses escalation while the
scan still runs. Pre-fills the fatigue window on a continuous timestamp axis
so the timed batch measures steady-state, not growth-phase."
```

---

## Task 6: Update `AGENTS.md` documentation

Update the Benchmarks section to reflect the new classes and counts.

**Files:**
- Modify: `AGENTS.md`

- [ ] **Step 1: Update the covered-subjects list and counts**

In `AGENTS.md`, find the Benchmarks section. The paragraph currently reads (approximately):

> 11 benchmark classes, 47 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, LMTokenizer (prediction), and the gaze processing pipeline (DirectFilter, SmoothedFilter, OneEuroFilter, FixationDetector, FixationCentroidTracker, GapInterpolator, DwellDetector, end-to-end GazeEngine).

Update it to:

> 13 benchmark classes, 62 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, LMTokenizer (prediction), the gaze processing pipeline (DirectFilter, SmoothedFilter, OneEuroFilter, FixationDetector, FixationCentroidTracker, GapInterpolator, DwellDetector, end-to-end GazeEngine), and the eye-care gaze subscriber (FatigueMonitor O(window) scan, EyeCareService pipeline integration).

- [ ] **Step 2: Update the filter examples in the run instructions**

In the same Benchmarks section, the gaze-pipeline run example currently reads:

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*    # gaze pipeline benchmarks (4 classes: filters, detectors, DwellDetector, end-to-end GazeEngine)
```

Update it to:

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*               # all gaze benchmarks (6 classes: filters, detectors, DwellDetector, GazeEngine, FatigueMonitor params + steady-state)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigue*        # fatigue monitor micro-benchmarks only
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*         # eye-care-wired pipeline integration only
```

Also update the inline class-count comment if the section lists "4 classes" for the gaze pipeline — it's now 6 classes.

- [ ] **Step 3: Commit**

```bash
git add AGENTS.md
git commit -m "docs: update AGENTS.md benchmarks section for eye-care gaze benchmarks

13 classes / 62 benchmarks. Adds FatigueMonitor + EyeCareService integration
to the covered-subjects list, and GazeFatigue* / WithEyeCare filter examples."
```

---

## Task 7: Full build, test, and verification gate

Final verification that everything compiles together, the generator tests pass, and the full gaze benchmark suite runs.

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build EyeCtrl.slnx`
Expected: **Build succeeds, 0 errors.** Confirms no transitive breakage from the `EyeCtrl.EyeCare` reference or generator change.

- [ ] **Step 2: Run the benchmark generator tests**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: **ALL PASS** — 18 tests (9 original + 9 new from Task 1).

- [ ] **Step 3: Run the full gaze benchmark suite (Release, short job for speed)**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze* --job short`
Expected: **All 6 gaze classes run without throwing.** Total benchmark count in the output should reconcile to **34 expanded benchmark cases** across the 6 gaze classes:

| Class | Methods | Params | Expanded |
|-------|---------|--------|----------|
| GazeFilterBenchmarks | 9 | — | 9 |
| GazeDetectorBenchmarks | 3 | — | 3 |
| GazeDwellBenchmarks | 1 | `[Params(5,20,50)]` | 3 |
| GazePipelineBenchmarks | 7 (4 original + 3 new) | — | 7 |
| GazeFatigueMonitorBenchmarks | 3 | `[Params(10,30,60)]` | 9 |
| GazeFatigueMonitorSteadyStateBenchmarks | 3 | — | 3 |
| **Total** | | | **34** |

(`GazeDwellBenchmarks` is 1 method × 3 param values = 3, not 15 — the single `Update` method is expanded once per `TargetCount`.)

- [ ] **Step 4: Inspect results against the spec's verification checklist**

After the run, inspect `benchmarks/EyeCtrl.Benchmarks/BenchmarkDotNet.Artifacts/results/` for the new classes. Confirm:
1. `GazeFatigueMonitorBenchmarks` `ns/op` grows roughly linearly with `WindowSeconds` (10→30→60) within each scenario — the O(window) claim.
2. Both Layer 5 classes report `B/op ≈ 0` (allocation-free steady-state claim — the `Queue<(long,double,double)>` value tuples + struct enumerator).
3. `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` is within noise of the existing `Pipeline_OneEuro_Fixation` (subscriber existence tax is small).
4. Three-way ladder: `Disabled` → `Enabled_NoFatigue` (within a few ns) → `WithFatigue` (notably higher). The subtraction `WithFatigue − Enabled_NoFatigue` should be in the same order of magnitude as a Layer 5 `Fatigue_Fixation` per-sample cost.

If any of these fail materially, stop and investigate before considering the task complete — the benchmarks exist to surface exactly these properties.

- [ ] **Step 5: No final commit needed if all prior tasks committed**

All changes were committed per-task. Verify with `git status` — should be clean. If anything is unstaged, stage and commit it with an appropriate message.

---

## Notes for the implementer

- **NFluent, not xUnit asserts.** All test assertions use `Check.That(...).IsEqualTo(...)` / `.HasSize(...)` etc. See the `AGENTS.md` Assertions section for the migration patterns.
- **`--job short`** in the smoke runs is for speed during implementation — it reduces iteration count so each run takes seconds instead of minutes. The final verification (Task 7 Step 3) can drop `--job short` for a full-precision measurement, but it's not required for the implementation gate.
- **The `EyeCareSettings` mutation pattern** (`new EyeCtrlSettings(); s.EyeCare.Enabled = ...`) works because `EyeCtrlSettings.EyeCare` is a mutable `[ObservableProperty]` auto-instantiated `EyeCareSettings` instance (verified in `EyeCtrlSettings.cs:40`). No need to construct `EyeCareSettings` separately and assign it.
- **Why `FatigueMonitor` is constructed directly** in Layer 5 (not via `EyeCareService`): Layer 5 characterizes the O(window) scan in isolation, exactly as Layer 1 constructs filters directly rather than via `GazeFilterFactory`. The `EyeCareService` aggregation overhead belongs to Layer 4.
