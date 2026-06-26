# Gaze Pipeline Benchmarks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Start on a dedicated feature branch or git worktree (superpowers:using-git-worktrees) before Task 1 — the tasks commit directly and must not land on `main`.

**Goal:** Add a 19-benchmark, 4-class performance suite covering the per-sample gaze processing pipeline (filters, detectors, DwellDetector stress, end-to-end `GazeEngine`), plus a generator-test project that runs in CI.

**Architecture:** A deterministic synthetic `GazeStreamGenerator` produces 1000-sample `GazePoint[]` streams (4 scenarios: Fixation, Saccade, NoisyFixation, WithDropout) at 30 Hz, fixed seed. Four focused BenchmarkDotNet classes consume those streams — one per pipeline layer — matching the repo's one-class-per-concern house style. A separate `EyeCtrl.Benchmarks.Tests` project holds correctness checks for the generator, wired into CI via a dedicated step (the benchmark project itself stays standalone, out of the slnx).

**Tech Stack:** .NET 10, BenchmarkDotNet 0.15.8, xUnit v3 (3.2.2), NFluent 3.1.0 (both auto-provided by `tests/Directory.Build.props`).

**Spec:** `docs/superpowers/specs/2026-06-25-gaze-pipeline-benchmarks-design.md`

---

## File Structure

| File | Responsibility | Action |
|------|----------------|--------|
| `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs` | Deterministic synthetic gaze stream generation (4 scenarios) | Create |
| `tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj` | Test project wiring (picks up xUnit/NFluent from shared props) | Create |
| `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs` | Generator correctness checks | Create |
| `benchmarks/EyeCtrl.Benchmarks/GazeFilterBenchmarks.cs` | Layer 1: filter micro-benchmarks (9) | Create |
| `benchmarks/EyeCtrl.Benchmarks/GazeDetectorBenchmarks.cs` | Layer 2: detector micro-benchmarks (3) | Create |
| `benchmarks/EyeCtrl.Benchmarks/GazeDwellBenchmarks.cs` | Layer 3: DwellDetector target sweep (3) | Create |
| `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs` | Layer 4: end-to-end pipeline (4) | Create |
| `.github/workflows/ci.yml` | Add dedicated generator-test step | Modify |
| `AGENTS.md` | Update benchmark class/count and covered-subjects list | Modify |

No existing Core/Platform source files are modified. All benchmarks live in the existing `EyeCtrl.Benchmarks` project (auto-discovered by `Program.cs`'s `BenchmarkSwitcher.FromAssembly`).

---

## Task 1: `GazeStreamGenerator` — shared infrastructure

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs`

- [ ] **Step 1: Create the generator file**

```csharp
namespace EyeCtrl.Benchmarks;

using EyeCtrl.Core.Models;

/// <summary>
/// Deterministic, procedural generator of synthetic gaze streams for benchmarking.
/// All streams are <see cref="SamplesPerBatch"/> points long at <see cref="SamplePeriodMicros"/>
/// (30 Hz) so cross-scenario ns/op is directly comparable. Use a fixed <paramref name="seed"/>
/// for reproducibility across machines and runs.
/// </summary>
/// <remarks>
/// <see cref="GazePoint"/> is a record (class), so each stream heap-allocates 1000 objects in
/// GlobalSetup (untimed). The generator writes pixel-scale values into GazePoint.X/Y (the tracker's
/// documented native space; calibration mapping is excluded as a non-goal — see the design spec).
/// </remarks>
public static class GazeStreamGenerator
{
    /// <summary>Number of gaze samples per generated stream.</summary>
    public const int SamplesPerBatch = 1000;
    /// <summary>Microseconds between samples (≈30 Hz, Tobii's common rate).</summary>
    public const long SamplePeriodMicros = 33_333;
    /// <summary>Pinned seed for reproducible generation.</summary>
    public const int DefaultSeed = 42;

    private const double ScreenCenterX = 960.0;
    private const double ScreenCenterY = 540.0;

    /// <summary>
    /// Fixation: gaze held near (960, 540) with slow circular drift (radius ~8 px, ~0.3 Hz)
    /// and ±1.5 px Gaussian noise. 100% valid. Exercises steady-state smoothing and OneEuro's
    /// low-velocity branch.
    /// </summary>
    public static GazePoint[] GenerateFixation(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double phase = 2 * Math.PI * 0.3 * (t / 1_000_000.0); // ~0.3 Hz over elapsed seconds
            double dx = Math.Cos(phase) * 8.0 + Gaussian(rng) * 1.5;
            double dy = Math.Sin(phase) * 8.0 + Gaussian(rng) * 1.5;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// Saccade: 5 point-to-point jumps across the screen, each held briefly, with ±0.5 px noise.
    /// 100% valid. Exercises the high-velocity filter branch and FixationDetector reset churn.
    /// </summary>
    public static GazePoint[] GenerateSaccade(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        // 5 targets, evenly dividing the 1000-sample batch into 5 segments of 200.
        double[][] targets =
        {
            new[] { 960.0, 540.0 },
            new[] { 200.0, 200.0 },
            new[] { 1500.0, 300.0 },
            new[] { 400.0, 800.0 },
            new[] { 1200.0, 700.0 },
        };
        int segmentLen = SamplesPerBatch / targets.Length;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            int seg = Math.Min(i / segmentLen, targets.Length - 1);
            double tx = targets[seg][0] + Gaussian(rng) * 0.5;
            double ty = targets[seg][1] + Gaussian(rng) * 0.5;
            points[i] = new GazePoint(tx, ty, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// NoisyFixation: held at (960, 540), near-stationary, with heavy ±15 px Gaussian jitter.
    /// 100% valid. The aggressive jitter case filters exist to tame.
    /// </summary>
    public static GazePoint[] GenerateNoisyFixation(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double dx = Gaussian(rng) * 15.0;
            double dy = Gaussian(rng) * 15.0;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }
        return points;
    }

    /// <summary>
    /// WithDropout: the Fixation drift trajectory with ~8% of samples marked invalid, clustered
    /// into two gaps. The short gap (2 invalid samples, ~66 ms) is under the 150 ms
    /// MaxGapInterpolationMs threshold and exercises GapInterpolator's hold-position path; the
    /// long gap (6 invalid samples, ~200 ms) is over the threshold and exercises the reset path.
    /// Placed so valid samples surround them.
    /// </summary>
    public static GazePoint[] GenerateWithDropout(int seed = DefaultSeed)
    {
        var rng = new Random(seed);
        var points = new GazePoint[SamplesPerBatch];
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            long t = i * SamplePeriodMicros;
            double phase = 2 * Math.PI * 0.3 * (t / 1_000_000.0);
            double dx = Math.Cos(phase) * 8.0 + Gaussian(rng) * 1.5;
            double dy = Math.Sin(phase) * 8.0 + Gaussian(rng) * 1.5;
            points[i] = new GazePoint(ScreenCenterX + dx, ScreenCenterY + dy, t, IsValid: true);
        }

        // Short gap (2 samples) near the start → under threshold → held.
        points[50] = points[50] with { IsValid = false };
        points[51] = points[51] with { IsValid = false };

        // Long gap (6 samples) near the middle → over threshold → reset.
        for (int i = 500; i < 506; i++)
            points[i] = points[i] with { IsValid = false };

        return points;
    }

    /// <summary>Standard-normal sample via Box–Muller transform (no extra dependency).</summary>
    private static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: Build succeeded, 0 errors. (The generator is unused so far; unused `public static` methods produce no warnings.)

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeStreamGenerator.cs
git commit -m "feat: add deterministic GazeStreamGenerator for benchmarks"
```

---

## Task 2: Generator test project + correctness tests

**Files:**
- Create: `tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj`
- Create: `tests/EyeCtrl.Benchmarks.Tests/GazeStreamGeneratorTests.cs`

- [ ] **Step 1: Create the test project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\benchmarks\EyeCtrl.Benchmarks\EyeCtrl.Benchmarks.csproj" />
  </ItemGroup>

</Project>
```

Note: `tests/Directory.Build.props` automatically provides xUnit v3 (3.2.2), NFluent (3.1.0), `Microsoft.NET.Test.Sdk`, and the global `Using` for `Xunit` and `NFluent`. No package references needed here.

- [ ] **Step 2: Write the generator correctness tests**

```csharp
namespace EyeCtrl.Benchmarks.Tests;

using EyeCtrl.Core.Models;

public class GazeStreamGeneratorTests
{
    [Fact]
    public void GenerateFixation_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateFixation();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateSaccade_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateSaccade();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateNoisyFixation_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateNoisyFixation();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Fact]
    public void GenerateWithDropout_ReturnsCorrectCount()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        Check.That(points).HasSize(GazeStreamGenerator.SamplesPerBatch);
    }

    [Theory]
    [InlineData("Fixation")]
    [InlineData("Saccade")]
    [InlineData("NoisyFixation")]
    [InlineData("WithDropout")]
    public void Timestamps_AreStrictlyIncreasingBySamplePeriod(string scenario)
    {
        var points = Generate(scenario);
        for (int i = 1; i < points.Length; i++)
        {
            long delta = points[i].TimestampMicroseconds - points[i - 1].TimestampMicroseconds;
            Check.That(delta).IsEqualTo(GazeStreamGenerator.SamplePeriodMicros);
        }
    }

    [Theory]
    [InlineData("Fixation", 42)]
    [InlineData("Saccade", 42)]
    [InlineData("NoisyFixation", 42)]
    [InlineData("WithDropout", 42)]
    public void SameSeed_ProducesIdenticalStreams(string scenario, int seed)
    {
        var first = Generate(scenario, seed);
        var second = Generate(scenario, seed);
        Check.That(second.Length).IsEqualTo(first.Length);
        for (int i = 0; i < first.Length; i++)
        {
            Check.That(second[i].X).IsEqualTo(first[i].X);
            Check.That(second[i].Y).IsEqualTo(first[i].Y);
            Check.That(second[i].IsValid).IsEqualTo(first[i].IsValid);
        }
    }

    [Fact]
    public void GenerateWithDropout_HasBothValidAndInvalidSamples()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        Check.That(points.Any(p => p.IsValid)).IsTrue();
        Check.That(points.Any(p => !p.IsValid)).IsTrue();
    }

    [Fact]
    public void GenerateWithDropout_HasClusteredInvalids_WithAtLeastOneLongGap()
    {
        var points = GazeStreamGenerator.GenerateWithDropout();
        // Find the longest run of consecutive invalid samples.
        int longestRun = 0;
        int currentRun = 0;
        foreach (var p in points)
        {
            if (!p.IsValid)
            {
                currentRun++;
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }
        // The long gap is 6 samples (over the 150ms threshold → reset). Must be present and clustered.
        Check.That(longestRun).IsGreaterOrEqualThan(6);
    }

    [Theory]
    [InlineData("Fixation")]
    [InlineData("Saccade")]
    [InlineData("NoisyFixation")]
    [InlineData("WithDropout")]
    public void Coordinates_AreWithinPlausibleScreenBox(string scenario)
    {
        var points = Generate(scenario);
        foreach (var p in points)
        {
            Check.That(p.X).IsStrictlyGreaterThan(-1000.0);
            Check.That(p.X).IsStrictlyLessThan(3000.0);
            Check.That(p.Y).IsStrictlyGreaterThan(-1000.0);
            Check.That(p.Y).IsStrictlyLessThan(3000.0);
            Check.That(double.IsFinite(p.X)).IsTrue();
            Check.That(double.IsFinite(p.Y)).IsTrue();
        }
    }

    private static GazePoint[] Generate(string scenario, int seed = 42) => scenario switch
    {
        "Fixation" => GazeStreamGenerator.GenerateFixation(seed),
        "Saccade" => GazeStreamGenerator.GenerateSaccade(seed),
        "NoisyFixation" => GazeStreamGenerator.GenerateNoisyFixation(seed),
        "WithDropout" => GazeStreamGenerator.GenerateWithDropout(seed),
        _ => throw new ArgumentException($"Unknown scenario: {scenario}", nameof(scenario)),
    };
}
```

- [ ] **Step 3: Run the tests — verify they pass**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: All tests PASS. (No failing-test-first stage here because the generator already exists from Task 1 — these are characterization tests for already-written code. If any fail, the generator has a bug to fix.)

- [ ] **Step 4: Commit**

```bash
git add tests/EyeCtrl.Benchmarks.Tests/
git commit -m "test: add GazeStreamGenerator correctness checks"
```

---

## Task 3: Layer 1 — `GazeFilterBenchmarks`

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeFilterBenchmarks.cs`

- [ ] **Step 1: Create the benchmark file**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 1 — per-filter micro-benchmarks. Three filters (Direct, Smoothed, OneEuro) × three
/// scenarios (Fixation, Saccade, NoisyFixation) = 9 benchmarks. WithDropout is excluded: filters
/// never receive invalid samples in production (GapInterpolator only calls Filter() for valid
/// samples), so Filter_*_WithDropout would exercise dead code.
/// </summary>
/// <remarks>
/// Each benchmark replays a pre-built batch of 1000 samples per invocation. Reported ns/op and
/// B/op are therefore per-1000-samples. Filters are constructed once in GlobalSetup and accumulate
/// state across the batch (production-representative). The last result is returned to defeat
/// dead-code elimination. Each benchmark calls its concrete filter directly (no shared helper)
/// so BenchmarkDotNet's per-method dispatch and inline optimization are not obstructed.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeFilterBenchmarks
{
    private GazePoint[] _fixation = null!;
    private GazePoint[] _saccade = null!;
    private GazePoint[] _noisyFixation = null!;

    private DirectFilter _direct = null!;
    private SmoothedFilter _smoothed = null!;
    private OneEuroFilter _oneEuro = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixation = GazeStreamGenerator.GenerateFixation();
        _saccade = GazeStreamGenerator.GenerateSaccade();
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();

        _direct = new DirectFilter();
        // SmoothedFilter has no parameterless ctor; 0.5 is the settings default.
        _smoothed = new SmoothedFilter(0.5);
        _oneEuro = new OneEuroFilter();
    }

    // --- Direct ---
    [Benchmark]
    public PhysicalPoint Filter_Direct_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _direct.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Direct_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _direct.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Direct_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _direct.Filter(_noisyFixation[i]);
        return last;
    }

    // --- Smoothed ---
    [Benchmark]
    public PhysicalPoint Filter_Smoothed_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _smoothed.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Smoothed_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _smoothed.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_Smoothed_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _smoothed.Filter(_noisyFixation[i]);
        return last;
    }

    // --- OneEuro ---
    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Fixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _fixation.Length; i++)
            last = _oneEuro.Filter(_fixation[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_Saccade()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _saccade.Length; i++)
            last = _oneEuro.Filter(_saccade[i]);
        return last;
    }

    [Benchmark]
    public PhysicalPoint Filter_OneEuro_NoisyFixation()
    {
        PhysicalPoint last = new(0, 0);
        for (int i = 0; i < _noisyFixation.Length; i++)
            last = _oneEuro.Filter(_noisyFixation[i]);
        return last;
    }
}
```

- [ ] **Step 2: Build and smoke-run**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: 0 errors. Then:

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeFilter* --job short`
Expected: 9 benchmark results in a table, all complete without throwing. Numbers are informational — no assertions.

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeFilterBenchmarks.cs
git commit -m "feat: add Layer 1 gaze filter benchmarks"
```

---

## Task 4: Layer 2 — `GazeDetectorBenchmarks`

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeDetectorBenchmarks.cs`

- [ ] **Step 1: Create the benchmark file**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 2 — per-detector micro-benchmarks. Detectors take PhysicalPoint, not GazePoint, so
/// GlobalSetup projects the GazePoint streams into parallel PhysicalPoint arrays. Detectors
/// accumulate state across the batch (production-representative). Each benchmark replays 1000
/// samples per invocation; ns/op and B/op are per-1000-samples.
/// </summary>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeDetectorBenchmarks
{
    private PhysicalPoint[] _fixationPoints = null!;
    private GazePoint[] _dropoutStream = null!;

    private FixationDetector _fixationDetector = null!;
    private FixationCentroidTracker _centroidTracker = null!;
    private GapInterpolator _gapInterpolator = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixationPoints = ToPhysical(GazeStreamGenerator.GenerateFixation());
        _dropoutStream = GazeStreamGenerator.GenerateWithDropout();

        // DwellSettings defaults: FixationRadiusPixels=50, FixationDetectionMs=300.
        _fixationDetector = new FixationDetector(50.0, 300);

        // DwellSettings default: VelocityThresholdPxPerSec=100.0.
        _centroidTracker = new FixationCentroidTracker(100.0);
        _centroidTracker.StartTracking();

        // GapInterpolator over a DirectFilter; MaxGapInterpolationMs=150 (CursorSettings default).
        _gapInterpolator = new GapInterpolator(new DirectFilter(), 150);
    }

    // FixationDetector.Update takes (PhysicalPoint, long timestampMicroseconds). Timestamps are
    // reconstructed from the sample index × SamplePeriodMicros (PhysicalPoint is a struct with no
    // timestamp field).
    [Benchmark]
    public void FixationDetector_Update()
    {
        for (int i = 0; i < _fixationPoints.Length; i++)
            _fixationDetector.Update(_fixationPoints[i], (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }

    [Benchmark]
    public void CentroidTracker_Update()
    {
        for (int i = 0; i < _fixationPoints.Length; i++)
            _centroidTracker.Update(_fixationPoints[i], (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }

    [Benchmark]
    public (PhysicalPoint Position, bool IsInGap) GapInterpolator_Process()
    {
        (PhysicalPoint Position, bool IsInGap) last = default;
        for (int i = 0; i < _dropoutStream.Length; i++)
            last = _gapInterpolator.Process(_dropoutStream[i]);
        return last;
    }

    private static PhysicalPoint[] ToPhysical(GazePoint[] stream)
    {
        var pts = new PhysicalPoint[stream.Length];
        for (int i = 0; i < stream.Length; i++)
            pts[i] = new PhysicalPoint(stream[i].X, stream[i].Y);
        return pts;
    }
}
```

- [ ] **Step 2: Build and smoke-run**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: 0 errors. Then:

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeDetector* --job short`
Expected: 3 results, all complete. Note `GapInterpolator_Process` may show 0 B/op (it delegates to DirectFilter which is allocation-free on the valid branch; the invalid branch likewise returns an existing struct).

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeDetectorBenchmarks.cs
git commit -m "feat: add Layer 2 gaze detector benchmarks"
```

---

## Task 5: Layer 3 — `GazeDwellBenchmarks`

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazeDwellBenchmarks.cs`

- [ ] **Step 1: Create the benchmark file**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 3 — DwellDetector target-sweep stress. The one non-O(1) hot path: Update scans every
/// target each call via ConcurrentDictionary.Values (twice). [Params] is scoped to this class so
/// the cartesian product expands only these 3 cases.
/// </summary>
/// <remarks>
/// ANTICIPATED FINDING: ConcurrentDictionary.Values allocates a fresh snapshot collection each
/// access, and Update enumerates it twice per call. MemoryDiagnoser WILL report nonzero B/op for
/// a path that runs once per gaze sample in production (driven off CursorMoved). This is a genuine
/// finding and an optimization target — the benchmark is expected to surface it.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazeDwellBenchmarks
{
    [Params(5, 20, 50)]
    public int TargetCount { get; set; }

    private DwellDetector _detector = null!;
    private PhysicalPoint _gazeInsideNearest = new(960, 540);

    [GlobalSetup]
    public void Setup()
    {
        _detector = new DwellDetector(cooldownMs: 0);
        // Arrange targets in a grid spanning a plausible screen area. Place the nearest target
        // at screen center so _gazeInsideNearest falls inside its hit radius.
        int cols = (int)Math.Ceiling(Math.Sqrt(TargetCount));
        double spacing = 1920.0 / (cols + 1);
        int placed = 0;
        for (int row = 0; placed < TargetCount; row++)
        {
            for (int col = 0; col < cols && placed < TargetCount; col++)
            {
                double cx = spacing * (col + 1);
                double cy = spacing * (row + 1);
                // First target sits at screen center.
                if (placed == 0)
                {
                    cx = 960.0;
                    cy = 540.0;
                }
                // Radius 60 px so the (960,540) gaze point is inside target 0's hit area.
                _detector.AddTarget(new DwellTarget($"t{placed}", new PhysicalPoint(cx, cy), 60.0, dwellTimeMs: 1000));
                placed++;
            }
        }
    }

    [Benchmark]
    public void Update()
    {
        // Replay 1000 samples to match the per-1000-samples reporting convention.
        // Gaze is stationary inside the nearest target so the full hit-test loop runs and dwell accumulates.
        for (int i = 0; i < GazeStreamGenerator.SamplesPerBatch; i++)
            _detector.Update(_gazeInsideNearest, (long)i * GazeStreamGenerator.SamplePeriodMicros);
    }
}
```

- [ ] **Step 2: Build and smoke-run**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: 0 errors. Then:

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazeDwell* --job short`
Expected: 3 results (TargetCount=5, 20, 50), all complete. Note the nonzero `B/op` column — that is the anticipated `ConcurrentDictionary.Values` allocation. If B/op is zero, investigate (the values snapshot should allocate).

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeDwellBenchmarks.cs
git commit -m "feat: add Layer 3 DwellDetector target-sweep benchmarks"
```

---

## Task 6: Layer 4 — `GazePipelineBenchmarks` (end-to-end)

**Files:**
- Create: `benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs`

- [ ] **Step 1: Create the benchmark file**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — end-to-end pipeline benchmarks. A real GazeEngine wired to private nested fakes
/// (mirroring tests/EyeCtrl.Core.Tests/GazeEngineTests.cs). The provider's GazePointReceived is
/// raised synchronously once per sample inside the timed loop.
/// </summary>
/// <remarks>
/// PER-ITERATION REBUILD: each benchmark gets a fresh provider+engine pair via a targeted
/// [IterationSetup], for two reasons: (a) InteractionManager starts in FreeLook and leaves it once
/// the Fixation scenario triggers, so reusing an engine would bias later iterations; (b) the
/// GazeEngine constructor subscribes to the provider's GazePointReceived event, so reusing a
/// provider across engines would stack subscriptions. [IterationCleanup] disposes the engine.
/// (BenchmarkDotNet runs many invocations per iteration on the one engine, so after fixation
/// triggers the state converges to Fixation and stays there — the engine drives no dwell — so the
/// reported number is steady-state-fixation cost. That is the intended apples-to-apples basis:
/// filter mode is the only variable across the four benchmarks.)
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineBenchmarks
{
    private GazePoint[] _fixation = null!;
    private GazePoint[] _noisyFixation = null!;

    // Built fresh per iteration by the targeted IterationSetup methods below — never reused across
    // iterations, so each measured run starts in FreeLook with no stacked event subscriptions.
    private FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixation = GazeStreamGenerator.GenerateFixation();
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();
    }

    [IterationSetup(Target = nameof(Pipeline_Direct_Fixation))]
    public void SetupDirect() => Rebuild(GazeFilterMode.Direct);

    [IterationSetup(Target = nameof(Pipeline_Smoothed_Fixation))]
    public void SetupSmoothed() => Rebuild(GazeFilterMode.Smoothed);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation))]
    public void SetupOneEuroFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_NoisyFixation))]
    public void SetupOneEuroNoisyFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationCleanup]
    public void IterationCleanup() => _engine.Dispose();

    [Benchmark]
    public void Pipeline_Direct_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_Smoothed_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_NoisyFixation() => Replay(_noisyFixation);

    private void Replay(GazePoint[] stream)
    {
        for (int i = 0; i < stream.Length; i++)
            _provider.SimulateGaze(stream[i]);
    }

    // Builds the fresh provider+engine pair for the upcoming iteration. The engine subscribes to
    // _provider in its constructor, so SimulateGaze on _provider drives exactly this engine.
    private void Rebuild(GazeFilterMode mode)
    {
        _provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();           // SelectionMode defaults to None
        settings.Cursor.FilterMode = mode;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _engine.Start();
    }

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
        // Records nothing during the timed loop — avoids contaminating allocation numbers.
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

- [ ] **Step 2: Build and smoke-run**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks`
Expected: 0 errors. Then:

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *GazePipeline* --job short`
Expected: 4 results, all complete. Numbers will be larger than Layer 1 (full pipeline per sample).

- [ ] **Step 3: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazePipelineBenchmarks.cs
git commit -m "feat: add Layer 4 end-to-end gaze pipeline benchmarks"
```

---

## Task 7: CI — add the generator-test step

**Files:**
- Modify: `.github/workflows/ci.yml` (add a step after the existing `Run unit tests` step, before EOF)

- [ ] **Step 1: Read the current CI file tail**

Run: read `.github/workflows/ci.yml` lines 50–55. Confirm the existing `Run unit tests` step ends at line 54 and EOF follows.

- [ ] **Step 2: Add the new step**

Append after the `Run unit tests` step (after line 54):

```yaml

      - name: Run generator tests (benchmark correctness)
        run: dotnet test tests/EyeCtrl.Benchmarks.Tests
```

This step builds the test project itself (which pulls in the benchmark project via ProjectReference). It is intentionally separate from `dotnet build EyeCtrl.slnx` + `dotnet test --no-build` so the benchmark project stays out of the slnx build graph, preserving the "benchmarks are standalone" invariant.

- [ ] **Step 3: Verify locally**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: All tests pass (same as Task 2 Step 3). This mirrors exactly what CI will run.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: run generator correctness tests in dedicated step"
```

---

## Task 8: Update `AGENTS.md`

**Files:**
- Modify: `AGENTS.md` (Benchmarks section)

- [ ] **Step 1: Read the current Benchmarks section**

Run: read `AGENTS.md`. Locate the Benchmarks section — the count line ("7 benchmark classes, 28 benchmarks total — covering ...") and the standalone-status line ("Not part of the solution file (standalone project)").

- [ ] **Step 2: Update the count and covered-subjects list**

Replace the count line:

```markdown
7 benchmark classes, 28 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, and LMTokenizer.
```

with:

```markdown
11 benchmark classes, 47 benchmarks total — covering WordPredictor, BigramModel, PhraseModel, SentenceCompletionIndex, FuzzyEditDistance, CapitalizationHelper, CasingHelper, PredictionOrchestrator, SmallLMPredictor, LMTokenizer (prediction), and the gaze processing pipeline (DirectFilter, SmoothedFilter, OneEuroFilter, FixationDetector, FixationCentroidTracker, GapInterpolator, DwellDetector, end-to-end GazeEngine).
```

- [ ] **Step 3: Add a gaze-pipeline run example**

In the Benchmarks run-instructions block, after the existing SmallLM example, add:

```markdown
dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze*    # gaze pipeline benchmarks (4 classes: filters, detectors, DwellDetector, end-to-end)
```

- [ ] **Step 4: Verify the standalone-status line still reads correctly**

The line "Not part of the solution file (standalone project)" remains accurate — the benchmark project is still not in `EyeCtrl.slnx`; only the generator *test* project is referenced directly by the dedicated CI step. Add a clarifying sentence to that paragraph:

```markdown
The generator correctness tests (`tests/EyeCtrl.Benchmarks.Tests`) are run in CI via a dedicated `dotnet test` step that references the benchmark project directly, without adding it to the solution.
```

- [ ] **Step 5: Commit**

```bash
git add AGENTS.md
git commit -m "docs: update AGENTS.md benchmark counts and gaze pipeline coverage"
```

---

## Task 9: Final verification

**Files:** none (verification only)

- [ ] **Step 1: Build everything**

Run: `dotnet build benchmarks/EyeCtrl.Benchmarks` and `dotnet build tests/EyeCtrl.Benchmarks.Tests`
Expected: both 0 errors.

- [ ] **Step 2: Run generator tests**

Run: `dotnet test tests/EyeCtrl.Benchmarks.Tests`
Expected: all pass.

- [ ] **Step 3: Run the full gaze suite (release, short job)**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Gaze* --job short`
Expected: all 19 benchmarks (9 filter + 3 detector + 3 dwell + 4 pipeline) complete without throwing.

- [ ] **Step 4: Confirm count**

Count the benchmark rows in the output. Expected: 19 total. Confirm `GazeDwellBenchmarks.Update` shows 3 rows (TargetCount 5/20/50) and nonzero B/op (the anticipated `ConcurrentDictionary.Values` finding).

- [ ] **Step 5: Confirm no regression to prediction benchmarks**

Run: `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *Bigram* --job short`
Expected: prediction benchmarks still run (auto-discovery unaffected by the new classes).
