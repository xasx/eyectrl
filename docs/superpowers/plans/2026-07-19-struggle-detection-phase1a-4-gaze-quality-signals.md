# Struggle Detection — Phase 1a-④ Gaze-Quality Signals — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Feed the three gaze-quality struggle signals — tracking quality (invalid-sample fraction), jitter (gaze-speed variance during fixation), and fatigue (EyeCare `FatigueMonitor.Score`) — into the telemetry sink as periodic summaries, aggregated **on the gaze thread** and marshaled through the existing serialized channel, with **zero behaviour change**.

**Architecture:** A new `GazeQualitySummary` value type + `IGazeQualitySink` interface (Core). `FixationCentroidTracker` gains a Welford gaze-speed variance (jitter), surfaced by `GazeEngine.FixationJitter`. A pure, timestamp-windowed `GazeQualityAggregator` (Core) consumes raw samples + the current jitter + fatigue and emits a summary each window. `BufferingTelemetrySink` and `DispatcherTelemetrySink` (from 1a-②) also implement `IGazeQualitySink`, so summaries ride the same UI-dispatcher-serialized path as dwell outcomes. The App subscribes the aggregator to `GazeSampled` (a third subscriber, after eye-care).

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core`, `EyeCtrl.EyeCare`, `EyeCtrl.App`; xUnit v3 + NFluent.

**Depends on:** 1a-① (`DwellOutcome`, sink), 1a-② (`BufferingTelemetrySink` + `DispatcherTelemetrySink` + DI). **Core enforces CS1591** — all public members documented.

**Zero behaviour change:** the aggregator only pushes summaries into the buffering sink; nothing consumes them until the StruggleMonitor (Phase 1b). `FixationCentroidTracker`'s new fields/property don't touch its centroid math. When eye-care/fatigue is off, `FatigueScore` is 0.

**Scope boundary:** the effective-settings layer + cold-filter-safe reconfiguration is plan 1a-⑤. Off-screen fraction (needs screen bounds) is deferred — `InvalidFraction` from `GazePoint.IsValid` is the tracking-quality signal here.

---

## File Structure

**Create (Core):** `Adaptation/GazeQualitySummary.cs`, `Adaptation/IGazeQualitySink.cs`, `Adaptation/GazeQualityAggregator.cs`.
**Modify (Core):** `Adaptation/BufferingTelemetrySink.cs` (implement `IGazeQualitySink`), `Processing/FixationCentroidTracker.cs` (jitter), `GazeEngine.cs` (`FixationJitter`).
**Modify (App):** `Services/DispatcherTelemetrySink.cs` (implement `IGazeQualitySink`), `Services/AppBootstrapper.cs` (registrations), `App.xaml.cs` (subscribe aggregator).
**Modify (EyeCare):** `IEyeCareService.cs` + `EyeCareService.cs` (`FatigueScore`).
**Test (create):** `tests/EyeCtrl.Core.Tests/Adaptation/GazeQualityAggregatorTests.cs`, `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkQualityTests.cs`, `tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerJitterTests.cs`.

---

## Task 1: Core gaze-quality contracts + `BufferingTelemetrySink` extension

**Files:** Create `Adaptation/GazeQualitySummary.cs`, `Adaptation/IGazeQualitySink.cs`; Modify `Adaptation/BufferingTelemetrySink.cs`; Test `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkQualityTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkQualityTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class BufferingTelemetrySinkQualityTests
{
    private static GazeQualitySummary Summary(long ts) => new(ts, 0.1, 12.0, 0.0, 30);

    [Fact]
    public void RetainsSummariesInOrder_AndDropsOutOfOrder()
    {
        var sink = new BufferingTelemetrySink(capacity: 8);
        sink.ReportGazeQuality(Summary(1000));
        sink.ReportGazeQuality(Summary(2000));
        sink.ReportGazeQuality(Summary(500)); // older than last accepted → dropped

        var snap = sink.SnapshotQuality();
        Check.That(snap).HasSize(2);
        Check.That(snap[0].TimestampMicroseconds).IsEqualTo(1000L);
        Check.That(snap[1].TimestampMicroseconds).IsEqualTo(2000L);
        Check.That(sink.DroppedOutOfOrderQuality).IsEqualTo(1L);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BufferingTelemetrySinkQualityTests"`
Expected: FAIL to compile — `GazeQualitySummary` / `IGazeQualitySink` / `ReportGazeQuality` / `SnapshotQuality` do not exist.

- [ ] **Step 3: Create the contracts and extend the sink**

`src/EyeCtrl.Core/Adaptation/GazeQualitySummary.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// A periodic snapshot of gaze quality over a short window, aggregated on the gaze thread and
/// pushed through the telemetry sink for struggle detection. A value type (allocation-free).
/// </summary>
/// <param name="TimestampMicroseconds">Timestamp of the window end, in microseconds.</param>
/// <param name="InvalidFraction">Fraction [0..1] of samples in the window that were invalid (tracking lost).</param>
/// <param name="JitterStdDev">Gaze-speed standard deviation (px/s) during fixation — a shakiness proxy — snapshot at window end.</param>
/// <param name="FatigueScore">EyeCare fatigue score [0..1]; 0 when fatigue monitoring is disabled.</param>
/// <param name="SampleCount">Number of samples aggregated in the window.</param>
public readonly record struct GazeQualitySummary(
    long TimestampMicroseconds,
    double InvalidFraction,
    double JitterStdDev,
    double FatigueScore,
    int SampleCount);
```

`src/EyeCtrl.Core/Adaptation/IGazeQualitySink.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Receives periodic <see cref="GazeQualitySummary"/> snapshots. Separate from
/// <see cref="IStruggleTelemetrySink"/> so dwell-outcome sinks need not implement it; the same
/// concrete sink may implement both. Implementations must be cheap and must not throw (design §11).
/// </summary>
public interface IGazeQualitySink
{
    /// <summary>Reports one gaze-quality window summary.</summary>
    /// <param name="summary">The window summary.</param>
    void ReportGazeQuality(in GazeQualitySummary summary);
}
```

In `src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs`, add `IGazeQualitySink` to the class declaration and a parallel summary buffer. Change:

```csharp
public sealed class BufferingTelemetrySink : IStruggleTelemetrySink
{
    private readonly DwellOutcome[] _buffer;
    private int _count;
    private int _next;
    private long _lastAcceptedTimestamp = long.MinValue;

    /// <summary>Number of outcomes dropped because their timestamp preceded the last accepted one.</summary>
    public long DroppedOutOfOrder { get; private set; }
```

to (add the interface + summary-buffer fields):

```csharp
public sealed class BufferingTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly DwellOutcome[] _buffer;
    private int _count;
    private int _next;
    private long _lastAcceptedTimestamp = long.MinValue;

    private readonly GazeQualitySummary[] _qualityBuffer;
    private int _qualityCount;
    private int _qualityNext;
    private long _lastAcceptedQualityTimestamp = long.MinValue;

    /// <summary>Number of outcomes dropped because their timestamp preceded the last accepted one.</summary>
    public long DroppedOutOfOrder { get; private set; }

    /// <summary>Number of gaze-quality summaries dropped as out-of-order.</summary>
    public long DroppedOutOfOrderQuality { get; private set; }
```

In the constructor, allocate the second buffer (same capacity). Change:

```csharp
        _buffer = new DwellOutcome[capacity];
```

to:

```csharp
        _buffer = new DwellOutcome[capacity];
        _qualityBuffer = new GazeQualitySummary[capacity];
```

Add the `IGazeQualitySink` implementation + snapshot (place after `Snapshot()`):

```csharp
    /// <inheritdoc />
    public void ReportGazeQuality(in GazeQualitySummary summary)
    {
        if (summary.TimestampMicroseconds < _lastAcceptedQualityTimestamp)
        {
            DroppedOutOfOrderQuality++;
            return;
        }
        _lastAcceptedQualityTimestamp = summary.TimestampMicroseconds;
        _qualityBuffer[_qualityNext] = summary;
        _qualityNext = (_qualityNext + 1) % _qualityBuffer.Length;
        if (_qualityCount < _qualityBuffer.Length) _qualityCount++;
    }

    /// <summary>Returns the retained gaze-quality summaries, oldest first.</summary>
    /// <returns>A snapshot list of retained summaries.</returns>
    public IReadOnlyList<GazeQualitySummary> SnapshotQuality()
    {
        var result = new List<GazeQualitySummary>(_qualityCount);
        int start = (_qualityNext - _qualityCount + _qualityBuffer.Length) % _qualityBuffer.Length;
        for (int i = 0; i < _qualityCount; i++)
            result.Add(_qualityBuffer[(start + i) % _qualityBuffer.Length]);
        return result;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~BufferingTelemetrySinkQualityTests"`
Expected: PASS (1 test). Existing `BufferingTelemetrySinkTests` (dwell) still pass — the DwellOutcome path is untouched.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/GazeQualitySummary.cs src/EyeCtrl.Core/Adaptation/IGazeQualitySink.cs src/EyeCtrl.Core/Adaptation/BufferingTelemetrySink.cs tests/EyeCtrl.Core.Tests/Adaptation/BufferingTelemetrySinkQualityTests.cs
git commit -m "feat(adaptation): add gaze-quality summary contract + buffering sink support"
```

---

## Task 2: `FixationCentroidTracker` jitter + `GazeEngine.FixationJitter`

**Files:** Modify `src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs`, `src/EyeCtrl.Core/GazeEngine.cs`; Test `tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerJitterTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerJitterTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class FixationCentroidTrackerJitterTests
{
    [Fact]
    public void SpeedStdDev_IsZero_WithFewerThanTwoSpeeds()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 1000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);      // first sample — no speed yet
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }

    [Fact]
    public void SpeedStdDev_IsPositive_WhenSpeedsVary()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        // dt = 100 ms per step (100_000 us). Move 1px, then 5px, then 1px → speeds 10, 50, 10 px/s.
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(1, 0), 100_000);
        t.Update(new PhysicalPoint(6, 0), 200_000);
        t.Update(new PhysicalPoint(7, 0), 300_000);
        Check.That(t.SpeedStdDev > 0).IsTrue();
    }

    [Fact]
    public void StartTracking_ResetsJitter()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(50, 0), 100_000);
        Check.That(t.SpeedStdDev >= 0).IsTrue();
        t.StartTracking(); // new fixation resets accumulation
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }

    [Fact]
    public void StopTracking_LatchesLastCompletedJitter_ThenResetsLive()
    {
        var t = new FixationCentroidTracker(velocityThresholdPxPerSec: 100_000);
        t.StartTracking();
        t.Update(new PhysicalPoint(0, 0), 0);
        t.Update(new PhysicalPoint(1, 0), 100_000);
        t.Update(new PhysicalPoint(6, 0), 200_000);
        t.Update(new PhysicalPoint(7, 0), 300_000);
        double live = t.SpeedStdDev;
        Check.That(live > 0).IsTrue();

        t.StopTracking(); // fixation end: latch the completed jitter, reset the live accumulator

        Check.That(t.LastCompletedSpeedStdDev).IsEqualTo(live);
        Check.That(t.SpeedStdDev).IsEqualTo(0.0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~FixationCentroidTrackerJitterTests"`
Expected: FAIL to compile — `SpeedStdDev` does not exist.

- [ ] **Step 3: Add the Welford accumulator**

In `src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs`, add fields next to the Welford-mean fields:

```csharp
    // Welford online mean
    private int _count;
    private double _meanX;
    private double _meanY;
```

becomes:

```csharp
    // Welford online mean (centroid)
    private int _count;
    private double _meanX;
    private double _meanY;

    // Welford online variance of per-sample gaze speed (jitter / shakiness proxy)
    private int _speedCount;
    private double _speedMean;
    private double _speedM2;
```

Add the public property (after `StableCentroid`):

```csharp
    /// <summary>
    /// Standard deviation (px/s) of per-sample gaze speed accumulated during the current fixation —
    /// a shakiness/jitter proxy. Zero until at least two speeds have been observed; reset on
    /// <see cref="StartTracking"/>/<see cref="StopTracking"/>.
    /// </summary>
    public double SpeedStdDev =>
        _speedCount > 1 ? Math.Sqrt(_speedM2 / (_speedCount - 1)) : 0.0;

    /// <summary>
    /// Gaze-speed standard deviation of the most recently *completed* fixation, latched at
    /// <see cref="StopTracking"/> (fixation end) before the live accumulator resets. Persists
    /// between fixations, so a windowed reader sees the last completed fixation's jitter rather
    /// than a mid-fixation fragment or a between-fixations zero (design §6.2 — "emitted at fixation end").
    /// </summary>
    public double LastCompletedSpeedStdDev { get; private set; }
```

In `StartTracking`, reset the live accumulator — add these three lines to its body:

```csharp
        _speedCount = 0;
        _speedMean = 0;
        _speedM2 = 0;
```

In `StopTracking`, **latch the completed jitter before resetting** (`StopTracking` is wired to `FixationEnded`, so this captures the per-fixation statistic at fixation end — design §6.2). Add:

```csharp
        LastCompletedSpeedStdDev = SpeedStdDev;
        _speedCount = 0;
        _speedMean = 0;
        _speedM2 = 0;
```

In `Update`, accumulate the speed right after it is computed. Change:

```csharp
        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? point.DistanceTo(_lastPoint.Value) / dtSeconds
            : 0;

        if (speed <= _velocityThresholdPxPerSec)
            Include(point);
```

to:

```csharp
        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? point.DistanceTo(_lastPoint.Value) / dtSeconds
            : 0;

        // Welford accumulation of per-sample speed for the jitter statistic.
        // Skip degenerate dt (duplicate / out-of-order timestamps → phantom zero speed) so it
        // doesn't deflate the variance.
        if (dtSeconds > 0)
        {
            _speedCount++;
            double speedDelta = speed - _speedMean;
            _speedMean += speedDelta / _speedCount;
            _speedM2 += speedDelta * (speed - _speedMean);
        }

        if (speed <= _velocityThresholdPxPerSec)
            Include(point);
```

- [ ] **Step 4: Expose it from `GazeEngine`**

In `src/EyeCtrl.Core/GazeEngine.cs`, add after the `StableCentroid` property:

```csharp
    /// <summary>Gaze-speed standard deviation (px/s) of the last *completed* fixation — a jitter/shakiness
    /// proxy latched at fixation end (design §6.2). Persists between fixations.</summary>
    public double LastFixationJitter => _centroidTracker.LastCompletedSpeedStdDev;
```

- [ ] **Step 5: Run tests + confirm no regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~FixationCentroidTrackerJitterTests"`
Expected: PASS (3 tests).
Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~FixationCentroidTracker|FullyQualifiedName~GazeEngine"`
Expected: PASS (centroid math and gaze processing unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerJitterTests.cs
git commit -m "feat(adaptation): expose fixation gaze-speed jitter (Welford) via GazeEngine"
```

---

## Task 3: `GazeQualityAggregator` (pure, timestamp-windowed)

**Files:** Create `src/EyeCtrl.Core/Adaptation/GazeQualityAggregator.cs`; Test `tests/EyeCtrl.Core.Tests/Adaptation/GazeQualityAggregatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/GazeQualityAggregatorTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class GazeQualityAggregatorTests
{
    private sealed class RecordingQualitySink : IGazeQualitySink
    {
        public List<GazeQualitySummary> Summaries { get; } = new();
        public void ReportGazeQuality(in GazeQualitySummary summary) => Summaries.Add(summary);
    }

    private static GazePoint Sample(long ts, bool valid) => new()
    {
        X = 100,
        Y = 100,
        IsValid = valid,
        TimestampMicroseconds = ts,
    };

    [Fact]
    public void EmitsSummary_OncePerWindow_WithInvalidFraction()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        // Window is 500 ms = 500_000 us. Feed 4 samples across the window; 1 invalid.
        agg.OnSample(Sample(0, valid: true), fixationJitter: 10, fatigueScore: 0.2);
        agg.OnSample(Sample(150_000, valid: false), 10, 0.2);
        agg.OnSample(Sample(300_000, valid: true), 10, 0.2);
        agg.OnSample(Sample(500_000, valid: true), fixationJitter: 25, fatigueScore: 0.4); // closes window

        Check.That(sink.Summaries).HasSize(1);
        var s = sink.Summaries[0];
        Check.That(s.SampleCount).IsEqualTo(4);
        Check.That(s.InvalidFraction).IsEqualTo(0.25);
        Check.That(s.JitterStdDev).IsEqualTo(25.0);  // latest jitter at window close
        Check.That(s.FatigueScore).IsEqualTo(0.4);   // latest fatigue at window close
    }

    [Fact]
    public void EmitsMultipleSummaries_AcrossMultipleWindows()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        for (long t = 0; t <= 1_200_000; t += 100_000)
            agg.OnSample(Sample(t, valid: true), 10, 0.0);

        Check.That(sink.Summaries.Count >= 2).IsTrue();
    }

    [Fact]
    public void EmitsNothing_BeforeWindowElapses()
    {
        var sink = new RecordingQualitySink();
        var agg = new GazeQualityAggregator(sink, windowMs: 500);

        agg.OnSample(Sample(0, valid: true), 10, 0.0);
        agg.OnSample(Sample(100_000, valid: true), 10, 0.0);

        Check.That(sink.Summaries).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeQualityAggregatorTests"`
Expected: FAIL to compile — `GazeQualityAggregator` does not exist.

- [ ] **Step 3: Create the aggregator**

`src/EyeCtrl.Core/Adaptation/GazeQualityAggregator.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;

/// <summary>
/// Aggregates raw gaze samples into periodic <see cref="GazeQualitySummary"/> snapshots on the gaze
/// thread and pushes them through an <see cref="IGazeQualitySink"/> (design §9.1: O(1) per sample,
/// summary marshaled by the sink). Timestamp-driven — no wall clock — so it is deterministic and
/// unit-testable. Not thread-safe: call <see cref="OnSample"/> from one thread (the gaze poll thread).
/// </summary>
public sealed class GazeQualityAggregator
{
    private readonly IGazeQualitySink _sink;
    private readonly long _windowMicroseconds;

    private long _windowStart = long.MinValue;
    private int _total;
    private int _invalid;
    private double _lastJitter;
    private double _lastFatigue;

    /// <summary>Initializes the aggregator.</summary>
    /// <param name="sink">Destination for window summaries.</param>
    /// <param name="windowMs">Window length in milliseconds; must be positive. Default 500.</param>
    public GazeQualityAggregator(IGazeQualitySink sink, int windowMs = 500)
    {
        if (windowMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowMs), windowMs, "Window must be positive.");
        _sink = sink;
        _windowMicroseconds = windowMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Feeds one raw gaze sample plus the current fixation jitter and fatigue score. Emits a summary
    /// (and resets the window) when the window duration has elapsed since the first sample of the window.
    /// </summary>
    /// <param name="sample">Raw gaze sample (valid or invalid).</param>
    /// <param name="fixationJitter">Current gaze-speed std-dev during fixation (from GazeEngine).</param>
    /// <param name="fatigueScore">Current fatigue score [0..1] (0 when disabled).</param>
    public void OnSample(GazePoint sample, double fixationJitter, double fatigueScore)
    {
        if (_windowStart == long.MinValue)
            _windowStart = sample.TimestampMicroseconds;

        _total++;
        if (!sample.IsValid) _invalid++;
        _lastJitter = fixationJitter;
        _lastFatigue = fatigueScore;

        if (sample.TimestampMicroseconds - _windowStart >= _windowMicroseconds)
        {
            _sink.ReportGazeQuality(new GazeQualitySummary(
                sample.TimestampMicroseconds,
                _total > 0 ? (double)_invalid / _total : 0.0,
                _lastJitter,
                _lastFatigue,
                _total));

            _windowStart = sample.TimestampMicroseconds;
            _total = 0;
            _invalid = 0;
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeQualityAggregatorTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/GazeQualityAggregator.cs tests/EyeCtrl.Core.Tests/Adaptation/GazeQualityAggregatorTests.cs
git commit -m "feat(adaptation): add timestamp-windowed GazeQualityAggregator"
```

---

## Task 4: App wiring — fatigue accessor, dispatcher sink, DI, subscription

App-layer plumbing. Emission correctness of Tasks 1-3 is already unit-proven; this task connects them. Verified by `dotnet build` + the `EyeCareService.FatigueScore` change (trivially testable).

**Files:** `EyeCare/IEyeCareService.cs`, `EyeCare/EyeCareService.cs`, `App/Services/DispatcherTelemetrySink.cs`, `App/Services/AppBootstrapper.cs`, `App/App.xaml.cs`

- [ ] **Step 1: Expose the fatigue score**

In `src/EyeCtrl.EyeCare/IEyeCareService.cs`, add to the interface:

```csharp
    /// <summary>Current fatigue score [0..1] from the fatigue monitor (0 when fatigue monitoring is off).</summary>
    double FatigueScore { get; }
```

In `src/EyeCtrl.EyeCare/EyeCareService.cs`, add the property (it already holds `private readonly FatigueMonitor _fatigueMonitor;`):

```csharp
    /// <inheritdoc />
    public double FatigueScore => _fatigueMonitor.Score;
```

- [ ] **Step 2: `DispatcherTelemetrySink` also serializes gaze-quality summaries**

In `src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs`, add `IGazeQualitySink` to the class, an optional `innerQuality` on both constructors, and a marshaled `ReportGazeQuality`.

Change the declaration:

```csharp
public sealed class DispatcherTelemetrySink : IStruggleTelemetrySink
{
    private readonly IStruggleTelemetrySink _inner;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Action> _post;
    private readonly ILogger<DispatcherTelemetrySink>? _logger;
```

to:

```csharp
public sealed class DispatcherTelemetrySink : IStruggleTelemetrySink, IGazeQualitySink
{
    private readonly IStruggleTelemetrySink _inner;
    private readonly IGazeQualitySink? _innerQuality;
    private readonly Func<bool> _hasThreadAccess;
    private readonly Action<Action> _post;
    private readonly ILogger<DispatcherTelemetrySink>? _logger;
```

Production constructor — add the `innerQuality` parameter and forward it:

```csharp
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Dispatcher dispatcher,
        ILogger<DispatcherTelemetrySink>? logger = null, IGazeQualitySink? innerQuality = null)
        : this(inner, dispatcher.CheckAccess, a => dispatcher.BeginInvoke(a), logger, innerQuality)
    {
    }
```

Testing constructor — add the parameter and assign the field:

```csharp
    public DispatcherTelemetrySink(IStruggleTelemetrySink inner, Func<bool> hasThreadAccess,
        Action<Action> post, ILogger<DispatcherTelemetrySink>? logger = null,
        IGazeQualitySink? innerQuality = null)
    {
        _inner = inner;
        _innerQuality = innerQuality;
        _hasThreadAccess = hasThreadAccess;
        _post = post;
        _logger = logger;
    }
```

Add the gaze-quality implementation (mirrors `Report`/`Forward`):

```csharp
    /// <inheritdoc />
    public void ReportGazeQuality(in GazeQualitySummary summary)
    {
        if (_innerQuality is null) return;
        var copy = summary;
        if (_hasThreadAccess())
            ForwardQuality(copy);
        else
            _post(() => ForwardQuality(copy));
    }

    private void ForwardQuality(GazeQualitySummary summary)
    {
        try { _innerQuality!.ReportGazeQuality(summary); }
        catch (Exception ex) { _logger?.LogError(ex, "Gaze-quality sink threw; summary dropped."); }
    }
```

- [ ] **Step 3: Update DI registration to expose both interfaces + the aggregator**

In `src/EyeCtrl.App/Services/AppBootstrapper.cs`, replace the 1a-② sink registration:

```csharp
        services.AddSingleton<BufferingTelemetrySink>();
        services.AddSingleton<IStruggleTelemetrySink>(sp => new DispatcherTelemetrySink(
            sp.GetRequiredService<BufferingTelemetrySink>(),
            dispatcher,
            sp.GetRequiredService<ILogger<DispatcherTelemetrySink>>()));
```

with (register the concrete dispatcher sink once, map both interfaces to it, add the aggregator):

```csharp
        services.AddSingleton<BufferingTelemetrySink>();
        services.AddSingleton<DispatcherTelemetrySink>(sp => new DispatcherTelemetrySink(
            sp.GetRequiredService<BufferingTelemetrySink>(),
            dispatcher,
            sp.GetRequiredService<ILogger<DispatcherTelemetrySink>>(),
            innerQuality: sp.GetRequiredService<BufferingTelemetrySink>()));
        services.AddSingleton<IStruggleTelemetrySink>(sp => sp.GetRequiredService<DispatcherTelemetrySink>());
        services.AddSingleton<IGazeQualitySink>(sp => sp.GetRequiredService<DispatcherTelemetrySink>());
        services.AddSingleton(sp => new GazeQualityAggregator(sp.GetRequiredService<IGazeQualitySink>()));
```

(Add `using EyeCtrl.Core.Adaptation;` if not already present.)

- [ ] **Step 4: Subscribe the aggregator to raw gaze samples**

In `src/EyeCtrl.App/App.xaml.cs`, immediately after the eye-care subscription (the block ending at line 245), add a third `GazeSampled` subscriber. Because it is registered after eye-care, `eyeCare.FatigueScore` reflects this sample:

```csharp
            // Gaze-quality telemetry: aggregate tracking/jitter/fatigue on the gaze thread and push
            // periodic summaries through the (dispatcher-serialized) telemetry sink. Buffered only
            // until the StruggleMonitor (Phase 1b) consumes them — zero behaviour change today.
            var gazeQuality = _serviceProvider.GetRequiredService<GazeQualityAggregator>();
            gazeEngine.GazeSampled += sample =>
                // LastFixationJitter = the last *completed* fixation's jitter (latched at fixation
                // end); FatigueScore is fresh (eye-care's handler ran first this event). Both are
                // read on the gaze thread, same as this handler.
                gazeQuality.OnSample(sample, gazeEngine.LastFixationJitter, eyeCare.FatigueScore);
```

(Add `using EyeCtrl.Core.Adaptation;` to `App.xaml.cs` if not present.)

- [ ] **Step 5: Build + headless suites**

Run: `dotnet build EyeCtrl.slnx`
Expected: builds — `IGazeQualitySink`/`GazeQualityAggregator` resolve; `eyeCare` and `gazeEngine` are in scope at the subscription site.
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS. (`DispatcherTelemetrySinkTests` from 1a-② still pass — the added `innerQuality` param is optional and defaults null.)

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.EyeCare/IEyeCareService.cs src/EyeCtrl.EyeCare/EyeCareService.cs src/EyeCtrl.App/Services/DispatcherTelemetrySink.cs src/EyeCtrl.App/Services/AppBootstrapper.cs src/EyeCtrl.App/App.xaml.cs
git commit -m "feat(adaptation): wire gaze-quality aggregator to the gaze stream"
```

---

## Self-Review

**Spec coverage:** §6.2 poor-tracking (`InvalidFraction`); shaky/jitter (`JitterStdDev` = the last *completed* fixation's gaze-speed std-dev, **latched at fixation end** in `FixationCentroidTracker.StopTracking` per §6.2's "emitted at fixation end", surfaced by `GazeEngine.LastFixationJitter` — the R3 fix, since velocity was previously discarded; not a live mid-window snapshot, which would read 0 between fixations); fatigue (reused `FatigueMonitor.Score`, not reimplemented). §9/§9.1: aggregation is O(1) per sample on the gaze thread; only ~2 Hz summaries cross the dispatcher (via `DispatcherTelemetrySink`); monotonic guard on summaries in `BufferingTelemetrySink`; the Welford accumulation skips degenerate `dt ≤ 0` samples. §11: `ReportGazeQuality` no-throw (buffer makes no external calls; dispatcher wrapper try/catch). Deferred: off-screen fraction (needs screen bounds); effective-settings (1a-⑤).

**Note for 1b (L2):** jitter is measured on the *filtered* gaze stream (`_centroidTracker` is fed filtered points), so smoothing suppresses measured jitter — and "more smoothing" is itself a 1b adaptation response, a mild feedback path. Design R3 accepted this location; 1b's monitor should calibrate jitter thresholds accordingly (a later refinement could derive jitter from raw/calibrated gaze). Jitter read at the aggregator is also inherently one fixation behind (latched at the *previous* fixation's end) — intended, since a completed statistic is more meaningful than a mid-fixation fragment.

**Placeholder scan:** none. The one conditional note (Task 3 `GazePoint` construction) points the implementer at the real file for the exact initializer shape — the field set needed is explicit.

**Type consistency:** `GazeQualitySummary`'s 5 positional args are constructed identically in the aggregator, the sink tests, and `BufferingTelemetrySink`. `IGazeQualitySink.ReportGazeQuality(in GazeQualitySummary)` is implemented by `BufferingTelemetrySink` and `DispatcherTelemetrySink`. `GazeEngine.LastFixationJitter` → `FixationCentroidTracker.LastCompletedSpeedStdDev` (latched at `StopTracking`). `IEyeCareService.FatigueScore` → `EyeCareService._fatigueMonitor.Score`. `GazeQualityAggregator(IGazeQualitySink, int)` matches the DI factory.

**Zero behaviour change:** `FixationCentroidTracker` centroid math untouched (only new fields/property added; reset extended); `GazeEngine.FixationJitter` is read-only; the aggregator only buffers; `DispatcherTelemetrySink`'s `innerQuality` is optional (1a-② tests unaffected). Nothing consumes summaries until 1b.

**Verified facts:** `IEyeCareService` currently exposes only `OnGazeSample`/`Cancel` (adding `FatigueScore` is additive); `EyeCareService` holds `_fatigueMonitor` with a public-get `Score`; `App.xaml.cs:237-245` is the eye-care `GazeSampled` subscription (the aggregator is added right after, so `eyeCare`/`gazeEngine` locals are in scope and fatigue is fresh); `FixationCentroidTracker.Update` computes per-sample `speed` (previously discarded) only while tracking (between `FixationStarted`/`FixationEnded`), so the jitter is fixation-gated.

**Honest testability limits:** Tasks 1-3 are fully headless-TDD. Task 4 is App wiring verified by `dotnet build` + the (Core-testable) `EyeCareService.FatigueScore` accessor; the `GazeSampled` subscription and DI are build-verified. A future end-to-end pass / 1b's StruggleMonitor will exercise the live summary flow.
