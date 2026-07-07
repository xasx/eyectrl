# Eye-Care Gaze Pipeline Benchmarks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add BenchmarkDotNet coverage for the eye-care per-sample gaze subscriber (`FatigueMonitor` O(window) scan + the `EyeCareService.OnGazeSample` integration into `GazeEngine.GazeSampled`), closing the gap left by the existing 4-layer gaze benchmark suite.

**Architecture:** Two new Layer 5 micro-benchmark classes (`GazeFatigueMonitorBenchmarks`, params-scaled; `GazeFatigueMonitorSteadyStateBenchmarks`, fixed 60s) characterize the O(window) fatigue scan. Layer 4 adds three eye-care-wired pipeline methods split across two classes: `Disabled`/`Enabled_NoFatigue` extend the existing `GazePipelineBenchmarks` (safe under multi-invocation — they early-return before the fatigue queue), while `WithFatigue` gets its own `GazePipelineWithFatigueBenchmarks` class with `invocationCount: 1` (the full O(window) scan would otherwise grow the window unboundedly under BDN's multi-invocation replay model). A small generator change (a defaulted `length` parameter) supplies the continuous >1000-sample streams needed for steady-state measurement.

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
/// PER-ITERATION PRE-FILL + invocationCount:1 (BOTH REQUIRED). BenchmarkDotNet runs a
/// [Benchmark] method many invocations per iteration on the SAME instance. FatigueMonitor
/// evicts via Peek().ts &lt; currentTs - windowMicros. If the method replays a fixed-ts array
/// against a persistent monitor, invocation 2+ jumps currentTs BACKWARD, cutoff goes below
/// the queue front, nothing evicts, and the window grows ~1000/invocation — producing
/// non-reproducible numbers, runaway O(queue) cost, and Queue&lt;T&gt; backing-array doublings
/// (non-zero B/op). The fix: build + pre-fill a FRESH monitor per iteration in
/// [IterationSetup], and pin invocationCount:1 so each iteration times exactly ONE clean
/// 1000-sample monotonic replay over a full, evicting window. (GazeDwellBenchmarks avoids
/// this because DwellDetector's per-sample work is O(targets) over a FIXED set — non-monotonic
/// timestamps don't grow the work. FatigueMonitor's work is O(queue), and the queue is what
/// breaks under replay.)
///
/// TIMESTAMP CONTINUITY (load-bearing): the pre-fill and timed tail are slices of ONE
/// continuous GenerateFixation(length: ...) stream. Two independent calls (timestamps
/// restarting at 0) would make cutoff go negative, evict nothing, and silently measure the
/// growth phase.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]   // ONE timed replay per iteration
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorBenchmarks
{
    private const int SamplesPerSec = 30;          // 30 Hz
    private const int SteadyTail = 1000;           // timed replay length, matches Layers 1-4

    [Params(10, 30, 60)]   // seconds → ~300 / 900 / 1800 samples in-window at 30 Hz
    public int WindowSeconds { get; set; }

    // Immutable streams — built once, reused across iterations (never mutated by OnSample).
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    // Fresh per iteration — see the class remarks. [IterationSetup] rebuilds + pre-fills these.
    private FatigueMonitor _fatigueFixation = null!;
    private FatigueMonitor _fatigueSaccade = null!;
    private FatigueMonitor _fatigueNoisyFixation = null!;

    // Streams only — cheap, immutable, safe to build once per [Params] value.
    [GlobalSetup]
    public void Setup()
    {
        int samplesToFill = WindowSeconds * SamplesPerSec + 2;   // +2: 33333µs/sample → 30·N < Ns
        int total = samplesToFill + SteadyTail;

        // One continuous stream each; the tail (timestamps continuing past samplesToFill) is timed.
        var fix = GazeStreamGenerator.GenerateFixation(length: total);
        var sac = GazeStreamGenerator.GenerateSaccade(length: total);
        var noi = GazeStreamGenerator.GenerateNoisyFixation(length: total);

        // Stash the full streams AND the pre-fill prefix length so IterationSetup can rebuild
        // monitors against the same continuous axis.
        _fixationFull = fix;
        _saccadeFull = sac;
        _noisyFixationFull = noi;
        _samplesToFill = samplesToFill;

        _fixation = fix[^SteadyTail..];
        _saccade = sac[^SteadyTail..];
        _noisyFixation = noi[^SteadyTail..];
    }

    // Fresh monitor + pre-fill EVERY iteration — invocationCount:1 means each iteration is one
    // timed replay, so this runs once per measured op. Untimed by BDN.
    [IterationSetup]
    public void IterationSetup()
    {
        _fatigueFixation = BuildPrefilled(_fixationFull, _samplesToFill);
        _fatigueSaccade = BuildPrefilled(_saccadeFull, _samplesToFill);
        _fatigueNoisyFixation = BuildPrefilled(_noisyFixationFull, _samplesToFill);
    }

    [Benchmark]
    public void Fatigue_Fixation_Win() => Replay(_fixation, _fatigueFixation);

    [Benchmark]
    public void Fatigue_Saccade_Win() => Replay(_saccade, _fatigueSaccade);

    [Benchmark]
    public void Fatigue_NoisyFixation_Win() => Replay(_noisyFixation, _fatigueNoisyFixation);

    // Naming note: BenchmarkDotNet does NOT interpolate [Params] values into the benchmark
    // display name — [Benchmark(name)] takes a static string. The actual output format is a
    // "Method" column (`Fatigue_Fixation_Win`) plus a separate "WindowSeconds" column (10/30/60),
    // exactly mirroring how GazeDwellBenchmarks.Update relies on the TargetCount column. The
    // `Win` suffix qualifies these from the steady-state class's Fatigue_Fixation/Saccade/
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

> **Fields referenced by `IterationSetup` not shown in the snippet above for brevity — add them
> alongside the existing fields:** `private GazePoint[] _fixationFull = null!;`,
> `private GazePoint[] _saccadeFull = null!;`, `private GazePoint[] _noisyFixationFull = null!;`,
> `private int _samplesToFill;`. They hold the full continuous streams + prefix length so
> `IterationSetup` can rebuild monitors against the same axis the timed tail continues.

Key correctness points baked into the code:
- **`invocationCount: 1`** — the critical fix. Without it, BDN's many-invocations-per-iteration model replays the fixed-ts array against a persistent monitor, growing the window unboundedly. With it, each iteration is exactly one timed 1000-sample replay over a fresh, full, evicting window.
- **`[IterationSetup]` rebuilds monitors** — fresh `FatigueMonitor` per iteration, pre-filled on the continuous axis. Combined with `invocationCount: 1`, the measured op always runs against a steady-state window.
- `samplesToFill = WindowSeconds * 30 + 2` — the `+2` matches the spec's off-by-one fix (at 33333µs/sample, 30·N samples span slightly *under* N seconds, so the first evicting sample is the 1802nd for a 60s window).
- `fix[^SteadyTail..]` takes the trailing 1000 samples of the continuous stream — timestamps continue monotonically from the pre-fill, so the timed batch opens on a full, evicting window.
- A single 1000-sample replay is tens of µs — comfortably above BDN's clock resolution for `invocationCount: 1` (which disables the unroll-based batching meant for sub-µs methods).
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
/// Same per-iteration pre-fill + invocationCount:1 discipline as GazeFatigueMonitorBenchmarks
/// (see that class's remarks for why both are required — BDN's multi-invocation model would
/// otherwise grow the window unboundedly under fixed-ts replay).
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]   // ONE timed replay per iteration
[MemoryDiagnoser]
[RankColumn]
public class GazeFatigueMonitorSteadyStateBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;

    // Immutable streams — built once, reused across iterations.
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    // Fresh per iteration.
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

        _fixationFull = fix;
        _saccadeFull = sac;
        _noisyFixationFull = noi;
        _samplesToFill = samplesToFill;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _fatigueFixation = BuildPrefilled(_fixationFull, _samplesToFill);
        _fatigueSaccade = BuildPrefilled(_saccadeFull, _samplesToFill);
        _fatigueNoisyFixation = BuildPrefilled(_noisyFixationFull, _samplesToFill);
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

> **Fields referenced by `IterationSetup` not shown in the snippet for brevity — add them
> alongside the existing fields:** `private GazePoint[] _fixationFull = null!;`,
> `private GazePoint[] _saccadeFull = null!;`, `private GazePoint[] _noisyFixationFull = null!;`,
> `private int _samplesToFill;`.

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

## Task 5: Layer 4 — eye-care-wired pipeline integration (two classes)

The eye-care subscriber integration splits across **two classes** because of the same
multi-invocation growth flaw that affects Layer 5:

- **`GazePipelineBenchmarks` (extended)** keeps `Disabled` and `Enabled_NoFatigue`. Both are
  safe under BDN's default multi-invocation model because `OnGazeSample` early-returns before
  touching the `FatigueMonitor` queue (`Enabled=false` returns at line 45; `FatigueMonitorEnabled=false`
  returns at line 31 of `OnSample`). Their measured cost is O(1) per sample regardless of
  invocation count, so they sit happily alongside the 4 original methods (which themselves
  rely on many invocations to converge `FreeLook → Fixation`).
- **`GazePipelineWithFatigueBenchmarks` (NEW)** holds the single `WithFatigue` method. It runs
  the full O(window) scan, so it needs `invocationCount: 1` + per-iteration rebuild — exactly
  like Layer 5. It cannot share the class with the 4 originals because `invocationCount: 1`
  would starve them of the invocations they need to converge.

Both classes need the same two fakes (`FakeEyeCareHooks`, `FakeExercise`). To avoid
duplication, the fakes live in a shared file and are marked `internal` so both classes
can use them.

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/EyeCareBenchmarkFakes.cs` (shared fakes)
- Modify: `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` (add `Disabled` + `NoFatigue`)
- Create: `benchmarks/EyeCtrl.Benchmarks/GazePipelineWithFatigueBenchmarks.cs` (the `WithFatigue` method)

- [ ] **Step 1: Create the shared fakes file**

Create `benchmarks/EyeCtrl.Benchmarks/EyeCareBenchmarkFakes.cs`:

```csharp
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;

namespace EyeCtrl.Benchmarks;

/// <summary>Shared fakes for the eye-care-wired pipeline benchmarks. Both
/// GazePipelineBenchmarks (Disabled/NoFatigue) and GazePipelineWithFatigueBenchmarks
/// construct an EyeCareService, which requires these. Neither is ever invoked during a
/// timed loop — escalation is suppressed via canInterrupt:false, so OnBreakDue never fires.</summary>
internal static class EyeCareBenchmarkFakes
{
    public static IEyeCareHooks Hooks { get; } = new FakeEyeCareHooks();
    public static IExercise[] ExercisePool { get; } = new IExercise[] { new FakeExercise() };

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

    private sealed class FakeExercise : IExercise
    {
        public string Name => "Fake";
        public double OnTargetRadiusPixels => 50.0;
        public PhysicalPoint GetTargetAt(long timestampMicroseconds) => new(960, 540);
        public void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds) { }
        public ExerciseResult Complete() => new("Fake", 0, 0, null, 0, "");
        public void Reset() { }
    }
}
```

- [ ] **Step 2: Extend `GazePipelineBenchmarks` — add usings, fields, the two safe methods**

At the top of `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs`, add these usings (the existing usings for `EyeCtrl.Core.*` are already present):

```csharp
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;
```

`EyeCtrl.EyeCare` for `EyeCareService`; `EyeCtrl.EyeCare.Exercises` for `ExerciseRunner`. (`IEyeCareHooks` / `EyeCareExerciseOutcome` are in `EyeCtrl.Core.Interfaces`, already imported. The fakes come from the shared `EyeCareBenchmarkFakes` static class in the same namespace.)

In the class body, alongside the existing fields, add:

```csharp
    // Continuous stream for the eye-care variants: pre-fill prefix + timed tail on one timestamp axis.
    private const int EyeCareWindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;
    private const int PrefillSamples = EyeCareWindowSeconds * SamplesPerSec + 2;   // 1802

    private GazePoint[] _fixationTimed = null!;   // trailing 1000-sample slice, continuous with the pre-fill
    private GazePoint[] _fixationPrefill = null!;  // 1802-sample prefix, pre-fed in RebuildWithEyeCare
    private EyeCareService _service = null!;
```

In the existing `[GlobalSetup] public void Setup()` method, after the existing `_noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();` line, add:

```csharp
        // One continuous stream for the eye-care variants: prefix pre-fed, tail timed.
        var fixationLong = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SteadyTail);
        _fixationPrefill = fixationLong[..PrefillSamples];
        _fixationTimed = fixationLong[PrefillSamples..];
```

After the existing `Pipeline_OneEuro_NoisyFixation` method (around line 72), add the two safe methods:

```csharp
    // Disabled + NoFatigue are safe under multi-invocation: OnGazeSample early-returns before
    // touching the FatigueMonitor queue, so the measured cost is O(1)/sample regardless of
    // invocation count. They share the class with the 4 originals (which need many invocations
    // to converge FreeLook -> Fixation).
    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Disabled() => Replay(_fixationTimed);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue() => Replay(_fixationTimed);
```

After the existing `[IterationSetup] public void SetupOneEuroNoisyFixation()` method (around line 57), add the two targeted setups:

```csharp
    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Disabled))]
    public void SetupEyeCareDisabled() => RebuildWithEyeCare(BuildDisabledSettings());

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation_WithEyeCare_Enabled_NoFatigue))]
    public void SetupEyeCareNoFatigue() => RebuildWithEyeCare(BuildEnabledNoFatigueSettings());
```

After the existing private `Rebuild` method (around line 89), add the helper + settings factories:

```csharp
    // Builds a fresh provider+engine+service, subscribes the service to GazeSampled, starts the
    // engine, then pre-feeds PrefillSamples so the FatigueMonitor window is full and evicting
    // when the timed batch starts. canInterrupt:false suppresses escalation while the scan runs.
    private void RebuildWithEyeCare(EyeCtrlSettings settings)
    {
        _provider = new FakeGazeProvider();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(EyeCareBenchmarkFakes.ExercisePool, seed: 0),
            EyeCareBenchmarkFakes.Hooks);
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < _fixationPrefill.Length; i++)
            _provider.SimulateGaze(_fixationPrefill[i]);
    }

    private static EyeCtrlSettings BuildDisabledSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = false;
        return s;
    }

    private static EyeCtrlSettings BuildEnabledNoFatigueSettings()
    {
        var s = new EyeCtrlSettings();
        s.EyeCare.Enabled = true;
        s.EyeCare.FatigueMonitorEnabled = false;
        return s;
    }
```

- [ ] **Step 3: Null-guard `_service` in `[IterationCleanup]`**

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

Engine first, then service — matches the spec's cleanup-order invariant.

- [ ] **Step 4: Verify `GazePipelineBenchmarks` compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.**

- [ ] **Step 5: Create `GazePipelineWithFatigueBenchmarks` (the invocationCount:1 class)**

Create `benchmarks/EyeCtrl.Benchmarks/GazePipelineWithFatigueBenchmarks.cs`:

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — the full O(window) fatigue scan wired into the real GazeEngine pipeline. This is
/// the one eye-care-pipeline variant that CANNOT share GazePipelineBenchmarks: it runs the
/// FatigueMonitor scan every sample, so BDN's multi-invocation model would grow the window
/// unboundedly under fixed-ts replay (same flaw as Layer 5). invocationCount:1 + a per-iteration
/// RebuildWithEyeCare (which pre-fills + converges Fixation) makes each measured op one clean
/// 1000-sample replay over a full, evicting window on a continuous timestamp axis.
/// </summary>
/// <remarks>
/// Disabled / Enabled_NoFatigue live in GazePipelineBenchmarks instead — they early-return
/// before the queue, so they tolerate multi-invocation and can share the class with the 4
/// originals (which need many invocations to converge FreeLook -> Fixation).
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]   // ONE timed replay per iteration
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineWithFatigueBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;
    private const int PrefillSamples = WindowSeconds * SamplesPerSec + 2;   // 1802

    private GazePoint[] _fixationTimed = null!;
    private GazePoint[] _fixationPrefill = null!;

    private FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;
    private EyeCareService _service = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fixationLong = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SteadyTail);
        _fixationPrefill = fixationLong[..PrefillSamples];
        _fixationTimed = fixationLong[PrefillSamples..];
    }

    [IterationSetup]
    public void IterationSetup() => RebuildWithEyeCare();

    [IterationCleanup]
    public void IterationCleanup()
    {
        _engine.Dispose();
        _service?.Dispose();
    }

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue() => Replay(_fixationTimed);

    private void Replay(GazePoint[] stream)
    {
        for (int i = 0; i < stream.Length; i++)
            _provider.SimulateGaze(stream[i]);
    }

    private void RebuildWithEyeCare()
    {
        _provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        settings.EyeCare.Enabled = true;
        settings.EyeCare.FatigueMonitorEnabled = true;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(EyeCareBenchmarkFakes.ExercisePool, seed: 0),
            EyeCareBenchmarkFakes.Hooks);
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < _fixationPrefill.Length; i++)
            _provider.SimulateGaze(_fixationPrefill[i]);
    }

    // Same fakes as GazePipelineBenchmarks (private nested there); duplicated here because the
    // originals are private to that class. Alternatively, move them to EyeCareBenchmarkFakes —
    // but the shared static already provides Hooks + ExercisePool; the raw provider/input fakes
    // are simple enough to duplicate. (If a reviewer prefers, lift FakeGazeProvider/FakeInputSimulator
    // into EyeCareBenchmarkFakes too and reference them from both classes.)
    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) { }
        public void LeftClick(PhysicalPoint p) { }
        public void RightClick(PhysicalPoint p) { }
        public void DoubleClick(PhysicalPoint p) { }
        public void MouseDown(PhysicalPoint p) { }
        public void MouseUp(PhysicalPoint p) { }
        public void Scroll(PhysicalPoint p, int dx, int dy) { }
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
```

> **Why duplicate the provider/input fakes instead of reusing `GazePipelineBenchmarks`'s private nested classes:** they're `private`, so unreachable. Lifting them into `EyeCareBenchmarkFakes` is the DRY option, but they're trivial (under 15 lines each, zero behavior) and the duplication is localized to two benchmark files. The plan leaves them duplicated to keep each class self-contained; if a reviewer prefers, lift them — it's a mechanical change with no behavioral impact.

- [ ] **Step 6: Verify the new class compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: **Build succeeds, 0 errors.**

- [ ] **Step 7: Smoke-run the eye-care pipeline methods**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare* --job short`
Expected: **Runs without throwing.** 3 benchmark rows (2 from `GazePipelineBenchmarks`, 1 from `GazePipelineWithFatigueBenchmarks`). No `InitException`. Confirm no break flow fires (no banner/exercise console output — `canInterrupt:false` blocks the guard, and `FakeEyeCareHooks.ShowBannerAsync` returns `false` as a backstop).

- [ ] **Step 8: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/EyeCareBenchmarkFakes.cs \
        benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs \
        benchmarks/EyeCtrl.Benchmarks/GazePipelineWithFatigueBenchmarks.cs
git commit -m "feat(benchmarks): wire EyeCareService into the gaze pipeline (Layer 4)

Three methods measure the eye-care subscriber's marginal per-sample cost:
Disabled + Enabled_NoFatigue (in GazePipelineBenchmarks — safe under
multi-invocation since they early-return before the queue) and WithFatigue
(in its own GazePipelineWithFatigueBenchmarks with invocationCount:1 — the
full O(window) scan needs one clean replay per iteration to avoid growing
the window). canInterrupt:false suppresses escalation; pre-fill on a
continuous timestamp axis keeps the window steady-state."
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

> 14 benchmark classes, 62 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, LMTokenizer (prediction), the gaze processing pipeline (DirectFilter, SmoothedFilter, OneEuroFilter, FixationDetector, FixationCentroidTracker, GapInterpolator, DwellDetector, end-to-end GazeEngine), and the eye-care gaze subscriber (FatigueMonitor O(window) scan, EyeCareService pipeline integration).

- [ ] **Step 2: Update the filter examples in the run instructions**

In the same Benchmarks section, the gaze-pipeline run example currently reads:

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*    # gaze pipeline benchmarks (4 classes: filters, detectors, DwellDetector, end-to-end GazeEngine)
```

Update it to:

```bash
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*               # all gaze benchmarks (7 classes: filters, detectors, DwellDetector, GazeEngine + WithFatigue, FatigueMonitor params + steady-state)
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFatigue*        # fatigue monitor micro-benchmarks only
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *WithEyeCare*         # eye-care-wired pipeline integration only
```

Also update the inline class-count comment if the section lists "4 classes" for the gaze pipeline — it's now 6 classes.

- [ ] **Step 3: Commit**

```bash
git add AGENTS.md
git commit -m "docs: update AGENTS.md benchmarks section for eye-care gaze benchmarks

14 classes / 62 benchmarks. Adds FatigueMonitor + EyeCareService integration
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
Expected: **ALL PASS** — 27 expanded test cases (18 original + 9 new from Task 1). The 18 original: 4 `[Fact]` (per-generator count checks + 2 dropout checks) + theories expanded (`Timestamps_AreStrictlyIncreasing` ×4, `SameSeed_ProducesIdenticalStreams` ×4, `Coordinates_AreWithinPlausibleScreenBox` ×4). The 9 new: `Generate_WithExplicitLength_ReturnsExactCount` ×3, `Generate_WithExplicitLength_HasContinuousTimestamps` ×3, `Generate_DefaultLength_StillSamplesPerBatch` ×3.

- [ ] **Step 3: Run the full gaze benchmark suite (Release, short job for speed)**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze* --job short`
Expected: **All 7 gaze classes run without throwing.** Total benchmark count in the output should reconcile to **35 expanded benchmark cases** across the 7 gaze classes:

| Class | Methods | Params | Expanded |
|-------|---------|--------|----------|
| GazeFilterBenchmarks | 9 | — | 9 |
| GazeDetectorBenchmarks | 3 | — | 3 |
| GazeDwellBenchmarks | 1 | `[Params(5,20,50)]` | 3 |
| GazePipelineBenchmarks | 6 (4 original + 2 eye-care safe) | — | 6 |
| GazePipelineWithFatigueBenchmarks | 1 | — | 1 |
| GazeFatigueMonitorBenchmarks | 3 | `[Params(10,30,60)]` | 9 |
| GazeFatigueMonitorSteadyStateBenchmarks | 3 | — | 3 |
| **Total** | | | **35** |

(`GazeDwellBenchmarks` is 1 method × 3 param values = 3, not 15 — the single `Update` method is expanded once per `TargetCount`.)

- [ ] **Step 4: Inspect results against the spec's verification checklist**

After the run, inspect `benchmarks/EyeCtrl.Benchmarks/BenchmarkDotNet.Artifacts/results/` for the new classes. Confirm:
1. `GazeFatigueMonitorBenchmarks` `ns/op` grows roughly linearly with `WindowSeconds` (10→30→60) within each scenario — the O(window) claim. (If it doesn't grow linearly, suspect the multi-invocation growth flaw — verify `invocationCount: 1` is on the `[SimpleJob]` and the monitor is rebuilt in `[IterationSetup]`, not `[GlobalSetup]`.)
2. Both Layer 5 classes report `B/op ≈ 0` (allocation-free steady-state claim — the `Queue<(long,double,double)>` value tuples + struct enumerator). Non-zero B/op means the window is still growing under replay — the same check as #1.
3. **Indicative only:** `Pipeline_OneEuro_Fixation_WithEyeCare_Disabled` vs the existing `Pipeline_OneEuro_Fixation`. These are NOT a clean apples-to-apples comparison: the eye-care variant is pre-filled (1802 samples → filter/detector fully warmed, ts range ~60s+) while the original starts cold each iteration (no pre-fill, ts 0–33s). They differ in warm-up state, not just "has the subscriber," so "within noise" may fail for reasons unrelated to the subscriber tax. The clean measurement is the internal ladder in #4.
4. Three-way ladder within the eye-care pipeline methods: `Disabled` → `Enabled_NoFatigue` (within a few ns — both early-return before the queue) → `WithFatigue` (notably higher — runs the full scan). The subtraction `WithFatigue − Enabled_NoFatigue` should be in the same order of magnitude as a Layer 5 `Fatigue_Fixation` per-sample cost. Note `WithFatigue` is measured under `invocationCount: 1` (different job config from the other two), so its noise envelope is wider — compare orders of magnitude, not exact ns.

If #1 or #2 fail materially, stop and investigate before considering the task complete — those are the properties the `invocationCount: 1` + per-iteration rebuild exists to guarantee, and a failure means the measurement is wrong, not the code under test.

- [ ] **Step 5: No final commit needed if all prior tasks committed**

All changes were committed per-task. Verify with `git status` — should be clean. If anything is unstaged, stage and commit it with an appropriate message.

---

## Notes for the implementer

- **NFluent, not xUnit asserts.** All test assertions use `Check.That(...).IsEqualTo(...)` / `.HasSize(...)` etc. See the `AGENTS.md` Assertions section for the migration patterns.
- **`--job short`** in the smoke runs is for speed during implementation — it reduces iteration count so each run takes seconds instead of minutes. The final verification (Task 7 Step 3) can drop `--job short` for a full-precision measurement, but it's not required for the implementation gate.
- **The `EyeCareSettings` mutation pattern** (`new EyeCtrlSettings(); s.EyeCare.Enabled = ...`) works because `EyeCtrlSettings.EyeCare` is a mutable `[ObservableProperty]` auto-instantiated `EyeCareSettings` instance (verified in `EyeCtrlSettings.cs:40`). No need to construct `EyeCareSettings` separately and assign it.
- **Why `FatigueMonitor` is constructed directly** in Layer 5 (not via `EyeCareService`): Layer 5 characterizes the O(window) scan in isolation, exactly as Layer 1 constructs filters directly rather than via `GazeFilterFactory`. The `EyeCareService` aggregation overhead belongs to Layer 4.
