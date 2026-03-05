# Gaze Improvements Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add gap interpolation (blink suppression) and I-VT fixation centroid tracking (accurate click targets) to the gaze processing pipeline.

**Architecture:** `GapInterpolator` wraps `IGazeFilter` and returns an `isInGap` flag GazeEngine uses to suppress fixation updates during blinks. `FixationCentroidTracker` runs in parallel with `FixationDetector`, accumulating a velocity-filtered running mean of gaze during fixations; GazeEngine substitutes this stable centroid for the raw action point when executing clicks.

**Tech Stack:** C# 12, .NET 8, xUnit, CommunityToolkit.Mvvm (`[ObservableProperty]`)

---

## Task 1: Add settings properties

**Files:**
- Modify: `src/EyeCtrl.Core/Settings/CursorSettings.cs`
- Modify: `src/EyeCtrl.Core/Settings/DwellSettings.cs`

**Step 1: Add MaxGapInterpolationMs to CursorSettings**

Open `CursorSettings.cs`. After the `_snapRadiusPixels` line, add:

```csharp
[ObservableProperty] private int _maxGapInterpolationMs = 150;
```

**Step 2: Add VelocityThresholdPxPerSec to DwellSettings**

Open `DwellSettings.cs`. After `_fixationRadiusPixels`, add:

```csharp
[ObservableProperty] private double _velocityThresholdPxPerSec = 100.0;
```

**Step 3: Build to verify**

```
dotnet build EyeCtrl.slnx
```
Expected: Build succeeded, 0 errors.

**Step 4: Commit**

```bash
git add src/EyeCtrl.Core/Settings/CursorSettings.cs src/EyeCtrl.Core/Settings/DwellSettings.cs
git commit -m "feat: add MaxGapInterpolationMs and VelocityThresholdPxPerSec settings"
```

---

## Task 2: GapInterpolator — tests

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorTests.cs`

**Step 1: Create the test file**

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class GapInterpolatorTests
{
    // Minimal stub — passes valid gaze through, returns (X,Y) as ScreenPoint
    private sealed class PassThroughFilter : IGazeFilter
    {
        public ScreenPoint Filter(GazePoint g) => new(g.X, g.Y);
        public void Reset() { }
    }

    private static GazePoint Valid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: true);

    private static GazePoint Invalid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: false);

    [Fact]
    public void Process_ValidSample_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Valid(100, 200, 0));
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_ValidSample_PositionMatchesRaw()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (pos, _) = gi.Process(Valid(100, 200, 0));
        Assert.Equal(100, pos.X);
        Assert.Equal(200, pos.Y);
    }

    [Fact]
    public void Process_ShortInvalidGap_IsInGapTrue()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));                 // establish last valid
        var (_, isInGap) = gi.Process(Invalid(0, 0, 50_000));  // 50ms < 150ms
        Assert.True(isInGap);
    }

    [Fact]
    public void Process_ShortInvalidGap_HoldsLastValidPosition()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        var (pos, _) = gi.Process(Invalid(0, 0, 50_000));
        Assert.Equal(100, pos.X);
        Assert.Equal(200, pos.Y);
    }

    [Fact]
    public void Process_LongInvalidGap_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        // 200ms >= 150ms → long gap, treat as new sequence
        var (_, isInGap) = gi.Process(Invalid(0, 0, 200_000));
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_ResumeAfterShortGap_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 50_000));     // short gap
        var (_, isInGap) = gi.Process(Valid(105, 205, 100_000));  // resume
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_NoValidSampleYet_InvalidSample_IsInGapFalse()
    {
        // No prior valid sample — can't be "in gap"
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Invalid(0, 0, 0));
        Assert.False(isInGap);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "GapInterpolatorTests"
```
Expected: build error — `GapInterpolator` does not exist yet.

---

## Task 3: GapInterpolator — implementation

**Files:**
- Create: `src/EyeCtrl.Core/Processing/GapInterpolator.cs`

**Step 1: Create the class**

```csharp
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public sealed class GapInterpolator
{
    private readonly IGazeFilter _filter;
    private readonly long _maxGapMicroseconds;
    private ScreenPoint _lastValidPosition = new(0, 0);
    private long _lastValidTimestamp = -1;

    public GapInterpolator(IGazeFilter filter, int maxGapMs)
    {
        _filter = filter;
        _maxGapMicroseconds = maxGapMs * 1000L;
    }

    public (ScreenPoint Position, bool IsInGap) Process(GazePoint rawPoint)
    {
        if (rawPoint.IsValid)
        {
            var filtered = _filter.Filter(rawPoint);
            _lastValidPosition = filtered;
            _lastValidTimestamp = rawPoint.TimestampMicroseconds;
            return (filtered, false);
        }

        // No prior valid sample — can't determine gap duration
        if (_lastValidTimestamp < 0)
            return (_lastValidPosition, false);

        long gap = rawPoint.TimestampMicroseconds - _lastValidTimestamp;

        if (gap >= _maxGapMicroseconds)
        {
            // Gap too long — treat as new sequence, reset filter
            _filter.Reset();
            _lastValidTimestamp = -1;
            return (_lastValidPosition, false);
        }

        // Short gap (blink) — hold position, suppress fixation/dwell
        return (_lastValidPosition, true);
    }

    public void Reset()
    {
        _filter.Reset();
        _lastValidPosition = new(0, 0);
        _lastValidTimestamp = -1;
    }
}
```

**Step 2: Run tests**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "GapInterpolatorTests"
```
Expected: 6 tests pass.

**Step 3: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Processing/GapInterpolatorTests.cs src/EyeCtrl.Core/Processing/GapInterpolator.cs
git commit -m "feat: add GapInterpolator with blink gap suppression"
```

---

## Task 4: Wire GapInterpolator into GazeEngine

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`

**Step 1: Replace `_filter` field with `_gapInterpolator`**

Replace:
```csharp
private IGazeFilter _filter;
```
With:
```csharp
private GapInterpolator _gapInterpolator;
```

**Step 2: Update constructor — create GapInterpolator**

Replace:
```csharp
_filter = GazeFilterFactory.Create(settings.Cursor);

_settings.Cursor.PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(CursorSettings.FilterMode))
        _filter = GazeFilterFactory.Create(_settings.Cursor);
};
```
With:
```csharp
_gapInterpolator = new GapInterpolator(
    GazeFilterFactory.Create(settings.Cursor),
    settings.Cursor.MaxGapInterpolationMs);

_settings.Cursor.PropertyChanged += (_, e) =>
{
    if (e.PropertyName is nameof(CursorSettings.FilterMode)
                       or nameof(CursorSettings.MaxGapInterpolationMs))
        _gapInterpolator = new GapInterpolator(
            GazeFilterFactory.Create(_settings.Cursor),
            _settings.Cursor.MaxGapInterpolationMs);
};
```

**Step 3: Update OnGazePoint to use GapInterpolator and skip fixation when in gap**

Replace:
```csharp
private void OnGazePoint(GazePoint gaze)
{
    var filtered = _filter.Filter(gaze);

    // Always fire CursorMoved so the cursor circle + toolbar receive gaze
    // (this allows unpause via toolbar dwell even when paused)
    CursorMoved?.Invoke(filtered, gaze.TimestampMicroseconds);

    if (InteractionManager.CurrentState == InteractionState.Paused)
        return;

    if (InteractionManager.CurrentState == InteractionState.FreeLook)
    {
        _inputSimulator.MoveCursor(filtered);
    }

    if (!SuppressFixation)
        _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
}
```
With:
```csharp
private void OnGazePoint(GazePoint gaze)
{
    var (filtered, isInGap) = _gapInterpolator.Process(gaze);

    // Always fire CursorMoved so the cursor circle + toolbar receive gaze
    // (this allows unpause via toolbar dwell even when paused)
    CursorMoved?.Invoke(filtered, gaze.TimestampMicroseconds);

    if (InteractionManager.CurrentState == InteractionState.Paused)
        return;

    if (InteractionManager.CurrentState == InteractionState.FreeLook)
    {
        _inputSimulator.MoveCursor(filtered);
    }

    if (!SuppressFixation && !isInGap)
        _fixationDetector.Update(filtered, gaze.TimestampMicroseconds);
}
```

**Step 4: Build and run all tests**

```
dotnet test EyeCtrl.slnx
```
Expected: all existing tests pass plus the 6 new GapInterpolator tests.

**Step 5: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: wire GapInterpolator into GazeEngine, suppress fixation during blinks"
```

---

## Task 5: FixationCentroidTracker — tests

**Files:**
- Create: `tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerTests.cs`

**Step 1: Create the test file**

```csharp
namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class FixationCentroidTrackerTests
{
    // 30ms intervals in microseconds (matches Tobii sample rate)
    private const long Dt = 30_000;

    [Fact]
    public void StableCentroid_BeforeTracking_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StableCentroid_AfterStartTracking_NoUpdates_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StableCentroid_SlowSamples_ReturnsMean()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // Slow movement: 1px in 30ms = ~33 px/s < 100 threshold
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.Update(new ScreenPoint(101, 201), Dt);
        tracker.Update(new ScreenPoint(100, 200), Dt * 2);

        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(100.33, centroid!.X, precision: 1);
        Assert.Equal(200.33, centroid.Y, precision: 1);
    }

    [Fact]
    public void StableCentroid_FastSamplesOnly_IsNull()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        // Fast movement: 300px in 30ms = 10000 px/s >> 100 threshold
        // First sample has no velocity, so it IS included — skip first, then add fast ones
        tracker.Update(new ScreenPoint(100, 200), 0);      // first: no velocity yet
        tracker.Update(new ScreenPoint(400, 500), Dt);     // 10000 px/s → fast, excluded

        // Only the first sample (velocity=0 ≤ threshold) contributes
        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);  // first sample contributed
        Assert.Equal(100, centroid!.X);
        Assert.Equal(200, centroid.Y);
    }

    [Fact]
    public void StableCentroid_MixedSamples_OnlySlowContribute()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();

        tracker.Update(new ScreenPoint(100, 100), 0);           // first, velocity=0 → included
        tracker.Update(new ScreenPoint(500, 500), Dt);          // fast → excluded
        tracker.Update(new ScreenPoint(102, 102), Dt * 2);      // slow (~94 px/s) → included

        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(101, centroid!.X);
        Assert.Equal(101, centroid.Y);
    }

    [Fact]
    public void StopTracking_ClearsStableCentroid()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.StopTracking();
        Assert.Null(tracker.StableCentroid);
    }

    [Fact]
    public void StartTracking_AfterStop_FreshAccumulation()
    {
        var tracker = new FixationCentroidTracker(velocityThresholdPxPerSec: 100);
        tracker.StartTracking();
        tracker.Update(new ScreenPoint(100, 200), 0);
        tracker.StopTracking();

        tracker.StartTracking();
        tracker.Update(new ScreenPoint(300, 400), 0);

        var centroid = tracker.StableCentroid;
        Assert.NotNull(centroid);
        Assert.Equal(300, centroid!.X);
    }
}
```

**Step 2: Run tests to verify they fail**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FixationCentroidTrackerTests"
```
Expected: build error — `FixationCentroidTracker` does not exist yet.

---

## Task 6: FixationCentroidTracker — implementation

**Files:**
- Create: `src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs`

**Step 1: Create the class**

```csharp
namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

public sealed class FixationCentroidTracker
{
    private readonly double _velocityThresholdPxPerSec;
    private bool _isTracking;
    private ScreenPoint? _lastPoint;
    private long _lastTimestamp;

    // Welford online mean
    private int _count;
    private double _meanX;
    private double _meanY;

    public FixationCentroidTracker(double velocityThresholdPxPerSec)
    {
        _velocityThresholdPxPerSec = velocityThresholdPxPerSec;
    }

    public ScreenPoint? StableCentroid =>
        _count > 0 ? new ScreenPoint(_meanX, _meanY) : null;

    public void StartTracking()
    {
        _isTracking = true;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
    }

    public void StopTracking()
    {
        _isTracking = false;
        _lastPoint = null;
        _count = 0;
        _meanX = 0;
        _meanY = 0;
    }

    public void Update(ScreenPoint point, long timestampMicroseconds)
    {
        if (!_isTracking) return;

        if (_lastPoint == null)
        {
            // First sample — no velocity, always include
            Include(point);
            _lastPoint = point;
            _lastTimestamp = timestampMicroseconds;
            return;
        }

        double dtSeconds = (timestampMicroseconds - _lastTimestamp) / 1_000_000.0;
        double speed = dtSeconds > 0
            ? Math.Sqrt(
                Math.Pow(point.X - _lastPoint.X, 2) +
                Math.Pow(point.Y - _lastPoint.Y, 2)) / dtSeconds
            : 0;

        if (speed <= _velocityThresholdPxPerSec)
            Include(point);

        _lastPoint = point;
        _lastTimestamp = timestampMicroseconds;
    }

    private void Include(ScreenPoint point)
    {
        _count++;
        _meanX += (point.X - _meanX) / _count;
        _meanY += (point.Y - _meanY) / _count;
    }
}
```

**Step 2: Run tests**

```
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FixationCentroidTrackerTests"
```
Expected: 6 tests pass.

**Step 3: Commit**

```bash
git add tests/EyeCtrl.Core.Tests/Processing/FixationCentroidTrackerTests.cs src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs
git commit -m "feat: add FixationCentroidTracker with velocity-filtered running mean"
```

---

## Task 7: Wire FixationCentroidTracker into GazeEngine

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs`

**Step 1: Add field**

After `private readonly FixationDetector _fixationDetector;`, add:

```csharp
private readonly FixationCentroidTracker _centroidTracker;
```

**Step 2: Construct and wire in constructor**

After the `_fixationDetector = new FixationDetector(...)` line, add:

```csharp
_centroidTracker = new FixationCentroidTracker(settings.Dwell.VelocityThresholdPxPerSec);
```

After the existing fixation event subscriptions (`_fixationDetector.FixationStarted += InteractionManager.OnFixationStarted;`), add:

```csharp
_fixationDetector.FixationStarted += _ => _centroidTracker.StartTracking();
_fixationDetector.FixationEnded += _centroidTracker.StopTracking;
```

**Step 3: Feed centroid tracker in OnGazePoint**

In `OnGazePoint`, after the `CursorMoved?.Invoke(...)` line and before the `if (InteractionManager.CurrentState == InteractionState.Paused)` check, add:

```csharp
_centroidTracker.Update(filtered, gaze.TimestampMicroseconds);
```

**Step 4: Substitute stable centroid in OnActionRequested**

Replace:
```csharp
private void OnActionRequested(InteractionAction action, ScreenPoint point)
{
    switch (action)
    {
        case InteractionAction.LeftClick:
            _inputSimulator.LeftClick(point);
            break;
```
With:
```csharp
private void OnActionRequested(InteractionAction action, ScreenPoint point)
{
    var target = _centroidTracker.StableCentroid ?? point;
    switch (action)
    {
        case InteractionAction.LeftClick:
            _inputSimulator.LeftClick(target);
            break;
```

And update all remaining `point` references in the switch body to `target`:
- `_inputSimulator.RightClick(target)`
- `_inputSimulator.DoubleClick(target)`
- `_inputSimulator.MouseDown(target)`
- `_inputSimulator.MouseUp(target)`
- `_inputSimulator.Scroll(target, ...)` (all four scroll cases)

**Step 5: Build and run all tests**

```
dotnet test EyeCtrl.slnx
```
Expected: all tests pass (30 existing + 12 new = 42 total).

**Step 6: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs
git commit -m "feat: wire FixationCentroidTracker, use stable centroid for click target"
```

---

## Done

Both features are implemented. Summary of new files and changes:

| File | Change |
|---|---|
| `src/EyeCtrl.Core/Settings/CursorSettings.cs` | +`MaxGapInterpolationMs` |
| `src/EyeCtrl.Core/Settings/DwellSettings.cs` | +`VelocityThresholdPxPerSec` |
| `src/EyeCtrl.Core/Processing/GapInterpolator.cs` | New |
| `src/EyeCtrl.Core/Processing/FixationCentroidTracker.cs` | New |
| `src/EyeCtrl.Core/GazeEngine.cs` | Use GapInterpolator; feed centroid tracker; stable centroid in OnActionRequested |
| `tests/.../GapInterpolatorTests.cs` | New (6 tests) |
| `tests/.../FixationCentroidTrackerTests.cs` | New (6 tests) |
