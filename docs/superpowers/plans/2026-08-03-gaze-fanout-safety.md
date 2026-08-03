# Gaze Fan-Out Safety — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop a single faulting gaze subscriber from freezing the cursor or killing the process, without letting the fix introduce a silent stale-data distortion or leave the toolbar hit-test comparing coordinates from two different spaces.

**Architecture:** Four independent fixes, no new abstractions. `GazeEngine`'s two events keep their public `+=`/`-=` surface but gain hand-written accessors over a copy-on-write handler array, so each handler can be invoked inside its own `try/catch` with no per-sample allocation. Both gaze providers gain a guard so nothing escapes to a thread root. Eye care publishes its fatigue score with the timestamp it last processed, so a consumer can detect staleness instead of inheriting a frozen value. `ToolbarAutoHideController` receives an optional to-screen mapper and applies it to its on-screen rect test only, leaving its off-screen edge test on tracker-native coordinates.

**Tech Stack:** C# / .NET 10, WPF (`net10.0-windows`), xUnit v3 + NFluent, Serilog via `Microsoft.Extensions.Logging.Abstractions`, BenchmarkDotNet 0.15.8.

**Spec:** `docs/superpowers/specs/2026-08-03-gaze-fanout-safety-design.md`

**Branch:** `worktree-gaze-observer-hub` (worktree at `.claude/worktrees/gaze-observer-hub`; spec + benchmarks already committed).

---

## Constraints that will bite you

Read these before Task 1. Each has already cost a build or a false green in this repo.

1. **`EyeCtrl.Core` sets `<WarningsAsErrors>CS1591</WarningsAsErrors>`.** Every new *public* member in Core needs an XML doc comment or the build fails. Internal members do not.
2. **Test output is German.** A per-test failure prints as `Fehler <FQN>`, not `Failed`. Grepping for "Failed" reports false green. Look for the `Bestanden!`/`Fehler:` summary line.
3. **Tests use NFluent, never `Assert.*`** — `Check.That(x).IsEqualTo(y)`, `.IsTrue()`, `.IsNotNull()`. Wiring is central in `tests/Directory.Build.props`.
4. **`tests/EyeCtrl.Benchmarks.Tests` is not in `EyeCtrl.slnx`.** A solution-wide test run silently skips it. Task 9 touches `benchmarks/`, so it must be run explicitly.
5. **Do not launch the app, run UI tests, or simulate input.** The author uses this app as their sole input method. Everything in this plan is verifiable headless.
6. **`EyeCtrl.Core` has no project references.** Anything added there must not need WPF or Platform.

## Task ordering rationale

The spec's Sequencing section groups Fix 1 and Fix 3 as "one change". This plan orders them so that **no intermediate commit is worse than `main`**, which is stronger:

- Fix 3 (staleness) lands **before** Fix 1 (isolation). Alone, Fix 3 is inert — the multicast still aborts, so eye care and gaze quality still fail together — but it means the commit that introduces isolation cannot ship the frozen-score distortion.
- Fix 4 lands first because it is a no-op until a calibration loads, so it is the safest thing to put in early, and the separately-landing calibration work is what activates the bug it fixes.

## File Structure

**Core (`EyeCtrl.Core`)** — no new project references:
- `src/EyeCtrl.Core/Diagnostics/HandlerFaultReporter.cs` — **create**: internal, rate-limited per-delegate fault reporting. Sole responsibility: decide whether this fault gets emitted now, and format it.
- `src/EyeCtrl.Core/GazeEngine.cs` — **modify**: replace both events' compiler-generated accessors with copy-on-write ones; guard both fan-outs; accept an optional logger.
- `tests/EyeCtrl.Core.Tests/Diagnostics/HandlerFaultReporterTests.cs` — **create**.
- `tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs` — **create**.

**EyeCare (`EyeCtrl.EyeCare`)**:
- `src/EyeCtrl.EyeCare/IEyeCareService.cs` — **modify**: add `TryGetFatigueScore`.
- `src/EyeCtrl.EyeCare/EyeCareService.cs` — **modify**: stamp the last processed sample; implement `TryGetFatigueScore`.
- `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs` — **modify**: staleness tests, reusing that file's private doubles.

**Platform (`EyeCtrl.Platform`)**:
- `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs` — **modify**: guard the `PollLoop` body.
- `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs` — **modify**: add a `catch` to `OnTick`; accept an optional logger.
- `tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderFaultTests.cs` — **create**.

**App (`EyeCtrl.App`)**:
- `src/EyeCtrl.App/Services/GazeQualityBridge.cs` — **create**: extracts the `App.xaml.cs` lambda that feeds `GazeQualityAggregator`, so the staleness substitution is unit-testable and the handler gains a name.
- `src/EyeCtrl.App/App.xaml.cs` — **modify**: use the bridge instead of an inline lambda.
- `src/EyeCtrl.App/Services/AppBootstrapper.cs` — **modify**: register the bridge.
- `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs` — **modify**: accept and apply a to-screen mapper.
- `src/EyeCtrl.App/Services/EventCoordinator.cs` — **modify**: pass the mapper at the existing construction site.
- `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs` — **modify**: mapper tests + helper overload.
- `tests/EyeCtrl.App.Tests/Services/GazeQualityBridgeTests.cs` — **create**.

**Benchmarks**:
- `benchmarks/EyeCtrl.Benchmarks/GazeObserverFanOutBenchmarks.cs` — **modify**: add a delegate-array variant so Fix 1's cost is measured, not projected.

---

### Task 1: Toolbar to-screen mapper (Fix 4)

**Files:**
- Modify: `src/EyeCtrl.App/Services/ToolbarAutoHideController.cs:59-73` (constructor), `:157-160` (Visible branch)
- Modify: `src/EyeCtrl.App/Services/EventCoordinator.cs:579-588`
- Test: `tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `ToolbarAutoHideControllerTests.cs`, before the helpers section:

```csharp
    // ── Coordinate space (Fix 4) ──

    [Fact]
    public void RectTest_UsesMappedCoordinates_SoRawInsideButMappedOutsideIsOffToolbar()
    {
        // Mapper shifts gaze 500 px down. Raw (960, 25) is inside the top toolbar rect;
        // mapped (960, 525) is not. The controller must treat this as OFF toolbar and hide.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y + 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 25, valid: true), 0);
        sut.Feed(CreateSample(960, 25, valid: true), 6_000_000); // past the 5s hide delay

        Check.That(vis.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void RectTest_UsesMappedCoordinates_SoRawOutsideButMappedInsideIsOnToolbar()
    {
        // Inverse of the above: raw (960, 525) is outside the rect, mapped (960, 25) is inside.
        // The controller must treat this as ON toolbar and never hide.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y - 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 525, valid: true), 0);
        sut.Feed(CreateSample(960, 525, valid: true), 6_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
    }

    [Fact]
    public void EdgeTest_IgnoresTheMapper_AndStaysOnTrackerNativeCoordinates()
    {
        // A mapper that would pull off-screen gaze back on-screen must NOT reach IsPastDockedEdge,
        // or the summon gesture is erased. Raw y = -100 is past the top edge; mapped y = +400 is not.
        var sut = NewController(toScreen: p => new PhysicalPoint(p.X, p.Y + 500));
        var vis = new FakeVisibility();
        sut.Attach(vis);
        sut.HideForTest(vis);

        sut.Feed(CreateSample(960, -100, valid: true), 0);
        sut.Feed(CreateSample(960, -100, valid: true), 500_000); // past the 400ms summon delay

        Check.That(vis.ShowCalls).IsEqualTo(1);
    }

    [Fact]
    public void NullMapper_BehavesAsIdentity()
    {
        var sut = NewController(toScreen: null);
        var vis = new FakeVisibility();
        sut.Attach(vis);

        sut.Feed(CreateSample(960, 25, valid: true), 0);          // inside the rect
        sut.Feed(CreateSample(960, 25, valid: true), 6_000_000);

        Check.That(vis.HideCalls).IsEqualTo(0);
    }
```

Replace the `NewController` helper at `:386-389` with an overload that carries the new parameter:

```csharp
    private static ToolbarAutoHideController NewController(bool enabled = true, string edge = Edge,
        Func<bool>? calibrationActive = null, Func<PhysicalPoint, PhysicalPoint>? toScreen = null)
        => new(enabled, edge, ScreenW, ScreenH, ToolbarRect,
               hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50, calibrationActive, toScreen);
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"
```

Expected: **compile error** — `ToolbarAutoHideController` has no 10th constructor parameter. That is the correct first failure; the tests cannot run until Step 3.

- [ ] **Step 3: Add the parameter and apply it to the rect test only**

In `ToolbarAutoHideController.cs`, add a field beside `_calibrationActive` (`:19`):

```csharp
    private readonly Func<PhysicalPoint, PhysicalPoint>? _toScreen;
```

Extend the constructor (`:59-73`) — new parameter last, so existing call sites keep compiling:

```csharp
    public ToolbarAutoHideController(bool enabled, string edge, double screenW, double screenH,
        PhysicalRect toolbarRect, int hideDelayMs, int summonDelayMs, double offScreenMarginPx,
        Func<bool>? calibrationActive = null,
        Func<PhysicalPoint, PhysicalPoint>? toScreen = null)
    {
        _enabled = enabled;
        _edge = edge;
        _edgeKey = edge.ToLowerInvariant();
        _screenW = screenW;
        _screenH = screenH;
        _toolbarRect = toolbarRect;
        _hideDelayUs = hideDelayMs * 1000L;
        _summonDelayUs = summonDelayMs * 1000L;
        _offScreenMarginPx = offScreenMarginPx;
        _calibrationActive = calibrationActive;
        _toScreen = toScreen;
    }
```

In `Feed`, change only the `State.Visible` branch (`:157-160`). Braces are needed because of the new local:

```csharp
            case State.Visible:
            {
                // The rect is screen geometry captured from the live window, but the sample arrives
                // pre-calibration (GazeSampled fires at GazeEngine.cs:230, before the transform at
                // :233). Map into screen space for THIS test only. IsPastDockedEdge below keeps raw
                // coordinates: off-screen gaze is outside the transform's fitted domain.
                var probe = _toScreen is null
                    ? new PhysicalPoint(sample.X, sample.Y)
                    : _toScreen(new PhysicalPoint(sample.X, sample.Y));
                if (sample.IsValid && _toolbarRect.Contains(probe.X, probe.Y))
```

Close the new brace after the `break;` that ends the Visible case:

```csharp
                break;
            }
```

Leave `IsPastDockedEdge` and the `State.Hidden` branch untouched.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~ToolbarAutoHideControllerTests"
```

Expected: `Bestanden!` with `Fehler: 0`. Every pre-existing test in this class must still pass — they all pass `toScreen: null` via the helper default, which is the identity path.

- [ ] **Step 5: Wire the real mapper**

In `EventCoordinator.cs`, extend the existing construction call (`:579-588`). `_context.CalibrationService` is already in scope — it supplies `calibrationActive` on the line above:

```csharp
        var controller = new ToolbarAutoHideController(
            enabled: enabled,
            edge: s.DockedEdge,
            screenW: System.Windows.SystemParameters.PrimaryScreenWidth * DpiHelper.ScaleX,
            screenH: System.Windows.SystemParameters.PrimaryScreenHeight * DpiHelper.ScaleY,
            toolbarRect: toolbarRect,
            hideDelayMs: 5000,
            summonDelayMs: 400,
            offScreenMarginPx: 50,
            calibrationActive: () => _context.CalibrationService.IsSessionActive,
            // Read per call, not cached, so a mid-session recalibration takes effect. Identity while
            // no calibration is loaded, which makes this change a no-op until one is.
            toScreen: p => _context.CalibrationService.CurrentTransform?.Apply(p) ?? p);
```

- [ ] **Step 6: Verify the whole headless suite still passes**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Expected: `Fehler: 0` on every assembly. Baseline for comparison is 1710 passed / 0 failed / 1 skipped across 11 assemblies.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/ToolbarAutoHideController.cs src/EyeCtrl.App/Services/EventCoordinator.cs tests/EyeCtrl.App.Tests/Services/ToolbarAutoHideControllerTests.cs
git commit -m "fix(toolbar): hit-test the toolbar rect in screen space, not tracker space

GazeSampled fires before the calibration transform, so the rect containment
test compared tracker-native coordinates against screen geometry. Harmless
only while CurrentTransform was null; a loaded calibration separates the
gaze-sensitive region from the visible toolbar by up to 25% of the screen
diagonal, which is what IsNearIdentity permits.

IsPastDockedEdge deliberately keeps raw coordinates — the transform is fit to
on-screen targets, so beyond the edge it extrapolates outside its domain, and
mapping there would erase the summon gesture rather than degrade it."
```

---

### Task 2: Eye care publishes a staleness stamp (Fix 3a)

**Files:**
- Modify: `src/EyeCtrl.EyeCare/IEyeCareService.cs`
- Modify: `src/EyeCtrl.EyeCare/EyeCareService.cs:42-56` (`OnGazeSample`), and near `:78` (`FatigueScore`)
- Test: `tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs` (modify — **not** a new file)

- [ ] **Step 1: Write the failing tests**

Add these to the **existing** `EyeCareServiceTests.cs`. Do not create a new test file: the doubles this needs (`FakeHooks` at `:11`, `StubExercise` at `:22`) are `private sealed` nested classes in that file and are unreachable from anywhere else, and its `NewService(settings, hooks)` helper at `:31-35` already builds the service correctly.

```csharp
    // ── Fatigue staleness (Fix 3) ──

    [Fact]
    public void TryGetFatigueScore_ReturnsFalse_ForASampleItNeverProcessed()
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        Check.That(sut.TryGetFatigueScore(12_345, out _)).IsFalse();
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsTrue_ForTheSampleItJustProcessed()
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        sut.OnGazeSample(new GazePoint(960, 540, 5_000_000, IsValid: true), isPaused: false);

        Check.That(sut.TryGetFatigueScore(5_000_000, out _)).IsTrue();
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsFalse_ForALaterSample_SoAFrozenScoreCannotLeak()
    {
        // The regression that matters: eye care processed sample N, then stopped (a fault). A consumer
        // asking about sample N+1 must be told the score is stale, not handed the old one.
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        sut.OnGazeSample(new GazePoint(960, 540, 5_000_000, IsValid: true), isPaused: false);

        Check.That(sut.TryGetFatigueScore(5_033_333, out var score)).IsFalse();
        Check.That(score).IsEqualTo(0.0);
    }
```

If `FakeHooks` needs constructor arguments, mirror exactly how the existing tests in that file instantiate it.

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj --filter "FullyQualifiedName~EyeCareServiceTests"
```

Expected: compile error — `TryGetFatigueScore` is not defined.

- [ ] **Step 3: Add the contract member**

In `IEyeCareService.cs`, after the `FatigueScore` property:

```csharp
    /// <summary>
    /// Reads the fatigue score only if it reflects the sample identified by
    /// <paramref name="sampleTimestampUs"/>. Returns <c>false</c> (and <paramref name="score"/> 0)
    /// when this service has not processed that sample — which is how a consumer distinguishes a
    /// live score from one frozen by a fault or by eye-care being disabled.
    /// </summary>
    /// <param name="sampleTimestampUs">Timestamp of the sample the caller is handling.</param>
    /// <param name="score">The fatigue score [0..1], or 0 when not fresh.</param>
    /// <returns><c>true</c> when the score is fresh for that sample.</returns>
    bool TryGetFatigueScore(long sampleTimestampUs, out double score);
```

- [ ] **Step 4: Implement it**

In `EyeCareService.cs`, add a field beside the others:

```csharp
    // Timestamp of the last sample actually fed to the fatigue monitor. Written and read on the gaze
    // thread only (both this service's handler and the gaze-quality bridge run there), so a plain
    // field is sufficient. Long.MinValue means "nothing processed yet" and cannot collide with a real
    // tracker timestamp.
    private long _lastFatigueSampleUs = long.MinValue;
```

In `OnGazeSample`, stamp it immediately after the monitor is fed — *after*, so a throw inside `OnSample` leaves the stamp stale, which is exactly the signal we want:

```csharp
    public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        _scheduler.Update(sample, isPaused, canInterrupt);
        if (!_settings.Enabled) return;
        _fatigueMonitor.OnSample(sample);
        _lastFatigueSampleUs = sample.TimestampMicroseconds;
```

Leave the rest of the method unchanged. Add the implementation next to `FatigueScore`:

```csharp
    /// <inheritdoc />
    public bool TryGetFatigueScore(long sampleTimestampUs, out double score)
    {
        if (_lastFatigueSampleUs != sampleTimestampUs)
        {
            score = 0.0;
            return false;
        }
        score = _fatigueMonitor.Score;
        return true;
    }
```

- [ ] **Step 5: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.EyeCare.Tests/EyeCtrl.EyeCare.Tests.csproj --filter "FullyQualifiedName~EyeCareServiceTests"
```

Expected: `Bestanden!`, `Fehler: 0` — the three new tests plus every pre-existing test in that class.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.EyeCare/IEyeCareService.cs src/EyeCtrl.EyeCare/EyeCareService.cs tests/EyeCtrl.EyeCare.Tests/EyeCareServiceTests.cs
git commit -m "feat(eyecare): publish the fatigue score with the sample it belongs to

FatigueMonitor.Score is written only by the constructor, OnSample and Reset,
and nothing decays it. A consumer reading it after eye care stops being called
inherits a frozen value indefinitely. TryGetFatigueScore lets the consumer ask
'is this fresh for the sample I am handling?' instead."
```

---

### Task 3: Route gaze quality through a testable bridge (Fix 3b)

**Files:**
- Create: `src/EyeCtrl.App/Services/GazeQualityBridge.cs`
- Modify: `src/EyeCtrl.App/App.xaml.cs:339-343`
- Modify: `src/EyeCtrl.App/Services/AppBootstrapper.cs` (register the bridge next to the `GazeQualityAggregator` registration)
- Test: `tests/EyeCtrl.App.Tests/Services/GazeQualityBridgeTests.cs` (create)

The substitution currently has to live in an inline lambda in `App.xaml.cs`, which no test can reach. Extracting it is what makes Fix 3 verifiable, and it gives the handler a name in fault logs once Task 5 lands.

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.App.Tests/Services/GazeQualityBridgeTests.cs`:

```csharp
namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using NFluent;

public class GazeQualityBridgeTests
{
    private sealed class FakeEyeCare : IEyeCareService
    {
        public bool Fresh { get; set; } = true;
        public double Score { get; set; }

        public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true) { }
        public void Cancel() { }
        public double FatigueScore => Score;

        public bool TryGetFatigueScore(long sampleTimestampUs, out double score)
        {
            score = Fresh ? Score : 0.0;
            return Fresh;
        }
    }

    private sealed class CapturingQualitySink : IGazeQualitySink
    {
        public readonly List<GazeQualitySummary> Summaries = [];
        public void ReportGazeQuality(in GazeQualitySummary summary) => Summaries.Add(summary);
    }

    [Fact]
    public void PassesTheFatigueScore_WhenEyeCareReportsItFresh()
    {
        var eyeCare = new FakeEyeCare { Fresh = true, Score = 0.75 };
        var (bridge, sink) = NewBridge(eyeCare);

        FeedOneWindow(bridge);

        Check.That(sink.Summaries).Not.IsEmpty();
        Check.That(sink.Summaries[0].FatigueScore).IsEqualTo(0.75);
    }

    [Fact]
    public void SubstitutesZero_WhenEyeCareReportsItStale()
    {
        // The regression: eye care faulted, so its score is frozen at 0.75. The bridge must report 0
        // rather than let a stale value drive adaptation.
        var eyeCare = new FakeEyeCare { Fresh = false, Score = 0.75 };
        var (bridge, sink) = NewBridge(eyeCare);

        FeedOneWindow(bridge);

        Check.That(sink.Summaries).Not.IsEmpty();
        Check.That(sink.Summaries[0].FatigueScore).IsEqualTo(0.0);
    }

    private static (GazeQualityBridge Bridge, CapturingQualitySink Sink) NewBridge(IEyeCareService eyeCare)
    {
        var adaptation = new AdaptationSettings { Enabled = true };
        var sink = new CapturingQualitySink();
        var aggregator = new GazeQualityAggregator(sink, adaptation, windowMs: 500);
        return (new GazeQualityBridge(aggregator, eyeCare, () => 0.0), sink);
    }

    // Drives the aggregator past its 500 ms window so it emits exactly one summary.
    private static void FeedOneWindow(GazeQualityBridge bridge)
    {
        bridge.OnGazeSample(new GazePoint(960, 540, 1_000_000, IsValid: true));
        bridge.OnGazeSample(new GazePoint(960, 540, 1_600_000, IsValid: true));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~GazeQualityBridgeTests"
```

Expected: compile error — `GazeQualityBridge` does not exist.

- [ ] **Step 3: Create the bridge**

Create `src/EyeCtrl.App/Services/GazeQualityBridge.cs`:

```csharp
namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare;

/// <summary>
/// Feeds <see cref="GazeQualityAggregator"/> from the raw gaze stream, supplying the fixation jitter
/// and the fatigue score it cannot obtain itself.
/// </summary>
/// <remarks>
/// Exists as a named class rather than a lambda in App.OnStartup for two reasons. It makes the
/// staleness substitution testable — the case that matters is eye care having faulted, which no test
/// can reach through an inline closure. And once the gaze fan-out reports faults per handler, a named
/// handler means a fault log can say which feature failed.
/// <para>
/// Runs on the gaze thread. Must stay cheap: no I/O, no locks, no dispatcher-synchronous work.
/// </para>
/// </remarks>
public sealed class GazeQualityBridge
{
    private readonly GazeQualityAggregator _aggregator;
    private readonly IEyeCareService _eyeCare;
    private readonly Func<double> _fixationJitter;

    /// <summary>Initializes a new instance of the <see cref="GazeQualityBridge"/> class.</summary>
    /// <param name="aggregator">The aggregator that windows samples into periodic summaries.</param>
    /// <param name="eyeCare">Supplies the fatigue score, and whether it is fresh for this sample.</param>
    /// <param name="fixationJitter">Reads the engine's latched jitter for the last completed fixation.</param>
    public GazeQualityBridge(
        GazeQualityAggregator aggregator, IEyeCareService eyeCare, Func<double> fixationJitter)
    {
        _aggregator = aggregator;
        _eyeCare = eyeCare;
        _fixationJitter = fixationJitter;
    }

    /// <summary>Handles one raw gaze sample. Wire this to <c>GazeEngine.GazeSampled</c>.</summary>
    /// <param name="sample">The raw sample, valid or invalid.</param>
    public void OnGazeSample(GazePoint sample)
    {
        // Substitute 0 when eye care has not processed this sample. GazeQualitySummary already
        // documents fatigue as "0 when fatigue monitoring is disabled", so a stale reading lands on
        // an existing contract instead of feeding a frozen score into StruggleSignals.
        double fatigue = _eyeCare.TryGetFatigueScore(sample.TimestampMicroseconds, out var fresh)
            ? fresh
            : 0.0;
        _aggregator.OnSample(sample, _fixationJitter(), fatigue);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.App.Tests/EyeCtrl.App.Tests.csproj --filter "FullyQualifiedName~GazeQualityBridgeTests"
```

Expected: `Bestanden!`, `Fehler: 0`, 2 tests.

- [ ] **Step 5: Register and wire it**

In `AppBootstrapper.cs`, immediately after the existing `GazeQualityAggregator` registration (which ends at `:173`):

```csharp
        // The jitter delegate resolves GazeEngine lazily. Factory registrations are not evaluated at
        // registration time, so it does not matter that GazeEngine is registered earlier at :149 —
        // and GazeEngineAdapter already resolves it the same way at :203.
        services.AddSingleton(sp => new GazeQualityBridge(
            sp.GetRequiredService<GazeQualityAggregator>(),
            sp.GetRequiredService<IEyeCareService>(),
            () => sp.GetRequiredService<GazeEngine>().LastFixationJitter));
```

Replace the lambda in `App.xaml.cs:339-343`:

```csharp
            // Gaze-quality telemetry. The bridge substitutes 0 for a stale fatigue score, so a
            // faulting eye-care handler degrades to the documented "disabled" value rather than
            // freezing its last reading into adaptation.
            var gazeQuality = _serviceProvider.GetRequiredService<GazeQualityBridge>();
            gazeEngine.GazeSampled += gazeQuality.OnGazeSample;
```

Delete the `Registered after eye-care so eyeCare.FatigueScore reflects this sample when read here.` comment at `:337` — the ordering dependency it documents no longer exists.

- [ ] **Step 6: Verify the whole headless suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Expected: `Fehler: 0` everywhere, with nothing needing an update. `TelemetrySinkRegistrationTests` resolves `GazeQualityAggregator` and feeds it directly rather than inspecting `App.OnStartup`'s subscription, and `OverlayTelemetryWiringTests` lives in `EyeCtrl.ViewModels.Tests` and never touches `App.xaml.cs` — so neither asserts the lambda shape this task replaces. If either does fail, that is a genuine surprise worth investigating rather than a test to adjust.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.App/Services/GazeQualityBridge.cs src/EyeCtrl.App/App.xaml.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.App.Tests/Services/GazeQualityBridgeTests.cs
git commit -m "refactor(adaptation): route gaze quality through a bridge that detects stale fatigue

Extracts the App.OnStartup lambda so the staleness substitution is testable,
and removes the registration-order dependency it relied on: gaze quality no
longer needs eye care to have run first for the same sample."
```

---

### Task 4: Rate-limited handler-fault reporting (Fix 1a)

**Files:**
- Create: `src/EyeCtrl.Core/Diagnostics/HandlerFaultReporter.cs`
- Test: `tests/EyeCtrl.Core.Tests/Diagnostics/HandlerFaultReporterTests.cs` (create)

Internal, so no CS1591 obligation, and `InternalsVisibleTo EyeCtrl.Core.Tests` is already declared in Core's csproj. It takes an emit callback and a clock rather than an `ILogger` so tests need no logging fake.

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Diagnostics/HandlerFaultReporterTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Diagnostics;

using EyeCtrl.Core.Diagnostics;
using NFluent;

public class HandlerFaultReporterTests
{
    private static void Handler() { }
    private static void OtherHandler() { }

    [Fact]
    public void Report_EmitsTheFirstFaultImmediately()
    {
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(1);
        Check.That(emitted[0]).Contains("GazeSampled");
    }

    [Fact]
    public void Report_SuppressesRepeatsInsideTheWindow()
    {
        var (sut, emitted, _) = NewReporter();

        for (int i = 0; i < 100; i++)
            sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(1);
    }

    [Fact]
    public void Report_EmitsAgainAfterTheWindow_AndCountsWhatWasSuppressed()
    {
        var (sut, emitted, clock) = NewReporter();

        for (int i = 0; i < 10; i++)
            sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        clock.Advance(31);
        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
        // 10 in-window reports, of which 1 was emitted, so 9 were suppressed.
        Check.That(emitted[1]).Contains("9");
    }

    [Fact]
    public void Report_RateLimitsEachHandlerIndependently()
    {
        var (sut, emitted, _) = NewReporter();

        sut.Report("GazeSampled", Handler, new InvalidOperationException("boom"));
        sut.Report("GazeSampled", OtherHandler, new InvalidOperationException("boom"));

        Check.That(emitted).HasSize(2);
    }

    private sealed class FakeClock
    {
        public long Seconds { get; private set; }
        public void Advance(long seconds) => Seconds += seconds;
    }

    private static (HandlerFaultReporter Sut, List<string> Emitted, FakeClock Clock) NewReporter()
    {
        var emitted = new List<string>();
        var clock = new FakeClock();
        var sut = new HandlerFaultReporter(
            (message, _) => emitted.Add(message),
            () => clock.Seconds,
            windowSeconds: 30);
        return (sut, emitted, clock);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~HandlerFaultReporterTests"
```

Expected: compile error — `HandlerFaultReporter` does not exist.

- [ ] **Step 3: Implement it**

Create `src/EyeCtrl.Core/Diagnostics/HandlerFaultReporter.cs`:

```csharp
namespace EyeCtrl.Core.Diagnostics;

/// <summary>
/// Emits at most one report per handler per window when an event handler throws, carrying a count of
/// what was suppressed in between.
/// </summary>
/// <remarks>
/// Rate limiting is the point. The gaze fan-out runs at 30-90 Hz inside the gaze provider's lock, so
/// an unbounded log write per fault would stall the gaze stream — a worse outcome than the exception
/// being reported. A handler that throws on every sample must cost one log line per window, not one
/// per sample.
/// <para>
/// Not thread-safe, and safe only because of an invariant worth stating: every gaze provider publishes
/// from one thread at a time. Tobii uses a dedicated <c>PollLoop</c> thread. MouseGazeProvider uses a
/// one-shot <see cref="System.Threading.Timer"/> re-armed in a <c>finally</c>, so although successive
/// ticks may land on different pool threads, two ticks never overlap. Switching that to a periodic
/// timer, or running two providers concurrently, would require a lock here.
/// </para>
/// <para>
/// The dictionary is touched on the fault path only, never per sample, and has no eviction. That is
/// deliberate and bounded: the compiler caches a lambda per call site, so each of the lambda
/// subscribers contributes exactly one entry; method-group subscriptions allocate a fresh delegate per
/// <c>+=</c>, but every subscription site in this codebase runs once at startup. A site that
/// re-subscribes in a loop would grow this dictionary and would need eviction added.
/// </para>
/// </remarks>
internal sealed class HandlerFaultReporter
{
    private sealed class Entry
    {
        public long WindowStartSeconds;
        public int SuppressedSinceEmit;
    }

    private readonly Action<string, Exception> _emit;
    private readonly Func<long> _nowSeconds;
    private readonly long _windowSeconds;
    private readonly Dictionary<Delegate, Entry> _entries = [];

    /// <param name="emit">Receives the formatted message and the exception.</param>
    /// <param name="nowSeconds">Monotonic clock in seconds; injectable so tests need no real time.</param>
    /// <param name="windowSeconds">Minimum gap between two reports for the same handler.</param>
    public HandlerFaultReporter(Action<string, Exception> emit, Func<long> nowSeconds, long windowSeconds)
    {
        _emit = emit;
        _nowSeconds = nowSeconds;
        _windowSeconds = windowSeconds;
    }

    /// <summary>Records a fault, emitting a report if this handler is outside its window.</summary>
    /// <param name="eventName">Name of the event that was being published.</param>
    /// <param name="handler">The handler that threw; identified by its method for the message.</param>
    /// <param name="exception">The exception it threw.</param>
    public void Report(string eventName, Delegate handler, Exception exception)
    {
        long now = _nowSeconds();

        // First fault for this handler: emit at once and open its window.
        if (!_entries.TryGetValue(handler, out var entry))
        {
            _entries[handler] = new Entry { WindowStartSeconds = now, SuppressedSinceEmit = 0 };
            Emit(eventName, handler, exception, suppressed: 0);
            return;
        }

        // Inside the window: count it and stay silent.
        if (now - entry.WindowStartSeconds < _windowSeconds)
        {
            entry.SuppressedSinceEmit++;
            return;
        }

        // Window elapsed: emit, reporting what was swallowed, and open a fresh window.
        int suppressed = entry.SuppressedSinceEmit;
        entry.WindowStartSeconds = now;
        entry.SuppressedSinceEmit = 0;
        Emit(eventName, handler, exception, suppressed);
    }

    private void Emit(string eventName, Delegate handler, Exception exception, int suppressed)
    {
        // Method.DeclaringType + Method.Name names the enclosing method even for a lambda — e.g.
        // App+<>c__DisplayClass0_0.<OnStartup>b__0 — which is enough to locate the offender.
        string who = $"{handler.Method.DeclaringType?.FullName}.{handler.Method.Name}";
        string message = suppressed > 0
            ? $"{eventName} handler {who} threw; {suppressed} further fault(s) suppressed since the last report"
            : $"{eventName} handler {who} threw";
        _emit(message, exception);
    }
}
```

**Emission is decided by the window timestamp alone, never by the counter.** An earlier draft of this plan gated on `Occurrences > 1 && !windowElapsed` while also resetting `Occurrences = 0` on the emit path. That does not rate-limit at all: the next fault increments the counter back to 1, `1 > 1` is false, so it falls through and emits again — every single time. Measured on a transcription of that draft: **10 emits from 10 in-window faults, and 11 total where 2 were wanted.** A plausible-looking variant that gates on `Occurrences == 1 || windowElapsed` fails identically, for the same reason. If you are tempted to simplify this method, re-run the Step 2 tests — `Report_SuppressesRepeatsInsideTheWindow` is the one that catches it.

Sanity check on the shipped shape: a handler throwing on every sample at 33 Hz for 60 s produces **2** log lines, not 1,980.

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~HandlerFaultReporterTests"
```

Expected: `Bestanden!`, `Fehler: 0`, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Diagnostics/HandlerFaultReporter.cs tests/EyeCtrl.Core.Tests/Diagnostics/HandlerFaultReporterTests.cs
git commit -m "feat(core): add rate-limited handler-fault reporting

Foundation for isolating the gaze fan-out. Unbounded logging on the gaze
thread inside the provider lock would be worse than the fault it reports, so
a repeatedly-throwing handler costs one line per window plus a suppressed
count."
```

---

### Task 5: Isolate the `GazeSampled` fan-out (Fix 1b)

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:62-65` (event), `:222-256` (`OnGazePoint`), `:77-83` (constructor), `:259-269` (`Dispose`)
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs`. It reuses the fakes pattern from `tests/EyeCtrl.Core.Tests/EyeCare/GazeEngineSampledTests.cs:11-35` — copy those two nested fake classes in verbatim rather than referencing them, since they are private there.

```csharp
namespace EyeCtrl.Core.Tests;

using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using NFluent;

public class GazeEngineFanOutIsolationTests
{
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

    [Fact]
    public void AThrowingHandler_DoesNotStarveLaterHandlers()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int reached = 0;

        engine.GazeSampled += _ => throw new InvalidOperationException("boom");
        engine.GazeSampled += _ => reached++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(reached).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingHandler_DoesNotEscapeToTheProvider()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        engine.GazeSampled += _ => throw new InvalidOperationException("boom");

        // Must not throw. Before the fix this propagated out of OnGazePoint into the provider's thread.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
    }

    [Fact]
    public void AThrowingHandler_DoesNotStopTheCursorMoving()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int cursorMoves = 0;

        engine.GazeSampled += _ => throw new InvalidOperationException("boom");
        engine.CursorMoved += (_, _) => cursorMoves++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(cursorMoves).IsEqualTo(1);
    }

    [Fact]
    public void HandlersRun_InRegistrationOrder()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        var order = new List<int>();

        engine.GazeSampled += _ => order.Add(1);
        engine.GazeSampled += _ => order.Add(2);
        engine.GazeSampled += _ => order.Add(3);

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(order).ContainsExactly(1, 2, 3);
    }

    [Fact]
    public void Unsubscribe_RemovesOnlyTheLastMatchingHandler()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int hits = 0;
        Action<GazePoint> handler = _ => hits++;

        engine.GazeSampled += handler;
        engine.GazeSampled += handler;
        engine.GazeSampled += handler;
        engine.GazeSampled -= handler;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        // Multicast -= removes one occurrence, not all three.
        Check.That(hits).IsEqualTo(2);
    }

    [Fact]
    public void AThrowingHandler_StaysSubscribed_AndIsCalledAgain()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int calls = 0;

        engine.GazeSampled += _ => { calls++; throw new InvalidOperationException("boom"); };

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        provider.SimulateGaze(new GazePoint(100, 200, 1_033_333, IsValid: true));

        // No trip-and-disable, deliberately: nothing silently disappears, so no downstream consumer
        // can be left reading a frozen value. The rate limiter is what bounds the logging cost.
        Check.That(calls).IsEqualTo(2);
    }

    [Fact]
    public void AHandlerUnsubscribingItself_DoesNotDisturbTheInFlightPublication()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int firstHits = 0;
        int secondHits = 0;
        Action<GazePoint>? first = null;
        first = _ => { firstHits++; engine.GazeSampled -= first!; };

        engine.GazeSampled += first;
        engine.GazeSampled += _ => secondHits++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
        provider.SimulateGaze(new GazePoint(100, 200, 1_033_333, IsValid: true));

        Check.That(firstHits).IsEqualTo(1);   // ran once, then removed itself
        Check.That(secondHits).IsEqualTo(2);  // never skipped
    }

    private static (FakeGazeProvider Provider, GazeEngine Engine) NewEngine()
    {
        var provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.Direct;
        var engine = new GazeEngine(provider, new FakeInputSimulator(), settings);
        engine.Start();
        return (provider, engine);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngineFanOutIsolationTests"
```

Expected — **four** fail, because the exception propagates out of `SimulateGaze`: `AThrowingHandler_DoesNotStarveLaterHandlers`, `..._DoesNotEscapeToTheProvider`, `..._DoesNotStopTheCursorMoving`, and `AThrowingHandler_StaysSubscribed_AndIsCalledAgain` (it never reaches the second sample, so `calls` stops at 1).

`HandlersRun_InRegistrationOrder`, `Unsubscribe_RemovesOnlyTheLastMatchingHandler` and `AHandlerUnsubscribingItself_DoesNotDisturbTheInFlightPublication` should already **pass** against the multicast delegate. That is deliberate: they characterise the semantics the rewrite must preserve, so they are regression guards rather than red-to-green tests.

- [ ] **Step 3: Replace the event with copy-on-write accessors**

In `GazeEngine.cs`, add these fields near the other private fields:

```csharp
    // Copy-on-write handler array. Publication does one volatile read and iterates by index, so it
    // never allocates and cannot be disturbed by a subscription change mid-publication — which
    // matters because a handler may unsubscribe itself from inside its own callback.
    // GetInvocationList() was the obvious alternative and was rejected: it allocates per sample at
    // 30-90 Hz, trading a crash risk for GC pressure on the gaze thread.
    //
    // INVARIANT: array elements are NEVER mutated in place. Add/remove build a whole new array under
    // _handlerLock and publish it by assigning this field. That assignment is the only fence there
    // is: `volatile` orders the reference read and write, but the elements are plain references, so
    // correctness depends on a reader that observes the new reference observing a fully-constructed
    // array. Writing into an existing array — even under the lock — would let a concurrent publisher
    // read a half-updated slot, and no amount of `volatile` on this field would catch it.
    private volatile Action<GazePoint>[] _gazeSampledHandlers = [];
    private readonly object _handlerLock = new();
    private readonly HandlerFaultReporter _faults;
```

Add `using EyeCtrl.Core.Diagnostics;` to the file's usings.

Replace the field-like event at `:62-65` with:

```csharp
    /// <summary>Fires the raw (pre-interpolation) gaze sample including its validity flag.
    /// Used by eye-care scheduling/away-detection. CursorMoved fires the interpolated point;
    /// this event fires the raw point so subscribers can detect eyes-lost / looking-away.
    /// <para>
    /// Handlers run on the gaze thread, inside the gaze provider's lock. They must be cheap and must
    /// not block — blocking here also blocks Stop() and TryGetLatestStatus. A handler that throws is
    /// caught and reported; it does not abort the remaining handlers and does not reach the provider
    /// thread, where an unhandled exception would terminate the process.
    /// </para></summary>
    public event Action<GazePoint>? GazeSampled
    {
        add
        {
            if (value is null) return;
            lock (_handlerLock)
                _gazeSampledHandlers = [.. _gazeSampledHandlers, value];
        }
        remove
        {
            if (value is null) return;
            lock (_handlerLock)
                _gazeSampledHandlers = RemoveLast(_gazeSampledHandlers, value);
        }
    }
```

Add the removal helper as a private static member of `GazeEngine`:

```csharp
    // Mirrors multicast -= semantics: remove the LAST matching handler, not every match. A filter
    // such as Where(h => !h.Equals(value)) would drop all duplicates and silently change behaviour
    // for any caller that subscribed the same target twice.
    private static T[] RemoveLast<T>(T[] source, T value) where T : class
    {
        for (int i = source.Length - 1; i >= 0; i--)
        {
            if (!source[i].Equals(value))
                continue;
            var result = new T[source.Length - 1];
            Array.Copy(source, 0, result, 0, i);
            Array.Copy(source, i + 1, result, i, source.Length - i - 1);
            return result;
        }
        return source;
    }
```

- [ ] **Step 4: Publish through the guard**

Add the publication method:

```csharp
    private void PublishGazeSampled(GazePoint gaze)
    {
        var handlers = _gazeSampledHandlers;   // one volatile read; the loop owns this snapshot
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](gaze);
            }
            catch (Exception ex)
            {
                _faults.Report(nameof(GazeSampled), handlers[i], ex);
            }
        }
    }
```

In `OnGazePoint`, replace line `:230`:

```csharp
        GazeSampled?.Invoke(gaze);
```

with:

```csharp
        PublishGazeSampled(gaze);
```

- [ ] **Step 5: Accept a logger and build the reporter**

Extend the constructor at `:77-83` — new parameter **last**, so all 31 existing `new GazeEngine(...)` call sites keep compiling:

```csharp
    /// <param name="logger">Optional logger for handler faults. When null, faults are swallowed
    /// silently — acceptable in tests, but production should always supply one.</param>
    public GazeEngine(
        IGazeProvider gazeProvider,
        IInputSimulator inputSimulator,
        EyeCtrlSettings settings,
        CalibrationService? calibrationService = null,
        EffectiveSettingsProvider? effectiveSettings = null,
        ILogger<GazeEngine>? logger = null)
    {
```

Add `using Microsoft.Extensions.Logging;` to the usings. As the first statement in the constructor body:

```csharp
        _faults = new HandlerFaultReporter(
            (message, ex) => logger?.LogError(ex, "{FaultMessage}", message),
            () => Environment.TickCount64 / 1000,
            windowSeconds: 30);
```

In `AppBootstrapper.cs`, add the logger as the sixth argument to the existing `GazeEngine` registration at `:149`. Read the current block first — the four arguments before `logger` must be preserved exactly as they are; only the trailing argument is new:

```csharp
        services.AddSingleton(sp => new GazeEngine(
            sp.GetRequiredService<IGazeProvider>(),
            sp.GetRequiredService<IInputSimulator>(),
            sp.GetRequiredService<EyeCtrlSettings>(),
            sp.GetRequiredService<CalibrationService>(),
            sp.GetRequiredService<EffectiveSettingsProvider>(),
            sp.GetRequiredService<ILogger<GazeEngine>>()));
```

If the existing argument list differs from the above (it is the current shape as of `b590a87`, but Spec C may have changed the `EffectiveSettingsProvider` argument to `IEffectiveSettings`), keep whatever is there and append only the logger.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngineFanOutIsolationTests"
```

Expected: `Bestanden!`, `Fehler: 0`, 7 tests. Then confirm the pre-existing gaze tests are unaffected:

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngine"
```

Expected: `Fehler: 0`. `GazeEngineSampledTests` in particular must still pass untouched — it asserts the raw pre-interpolation contract, which this change preserves.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs src/EyeCtrl.App/Services/AppBootstrapper.cs tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs
git commit -m "fix(core): isolate each GazeSampled handler so one fault cannot starve the rest

A bare multicast invoke aborted the chain on the first throw, so handlers
registered later silently stopped receiving samples, the cursor did not move
for that sample, and the exception escaped onto the provider's background
thread where it terminates the process.

The public event surface is unchanged — += and -= still compile at all twelve
subscription sites — because the accessors are hand-written over a
copy-on-write array. That also keeps publication allocation-free, which
GetInvocationList() would not."
```

---

### Task 6: Isolate the `CursorMoved` fan-out (Fix 1c)

**Files:**
- Modify: `src/EyeCtrl.Core/GazeEngine.cs:59-60` (event), `:242` (invoke)
- Test: `tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `GazeEngineFanOutIsolationTests.cs`:

```csharp
    [Fact]
    public void AThrowingCursorMovedHandler_DoesNotStarveLaterHandlers()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        int reached = 0;

        engine.CursorMoved += (_, _) => throw new InvalidOperationException("boom");
        engine.CursorMoved += (_, _) => reached++;

        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));

        Check.That(reached).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingCursorMovedHandler_DoesNotStopFixationProcessing()
    {
        var (provider, engine) = NewEngine();
        using var _ = engine;
        engine.CursorMoved += (_, _) => throw new InvalidOperationException("boom");

        // Must not throw: the remainder of OnGazePoint (centroid tracking, fixation) still runs.
        provider.SimulateGaze(new GazePoint(100, 200, 1_000_000, IsValid: true));
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngineFanOutIsolationTests"
```

Expected: the two new tests fail; the seven from Task 5 still pass.

- [ ] **Step 3: Apply the same pattern**

Add the field. The same never-mutate-in-place invariant documented on `_gazeSampledHandlers` in Task 5 applies here, and both fields share `_handlerLock`:

```csharp
    // Copy-on-write, same invariant as _gazeSampledHandlers: elements are never mutated in place;
    // add/remove build a new array under _handlerLock and publish it by assigning this field.
    private volatile Action<PhysicalPoint, long>[] _cursorMovedHandlers = [];
```

Replace the event at `:59-60`:

```csharp
    /// <summary>Fired when cursor moves with filtered position in physical screen pixels.
    /// <para>
    /// Handlers run on the gaze thread and must be cheap and non-blocking, as with
    /// <see cref="GazeSampled"/>. A throwing handler is caught and reported rather than aborting the
    /// remaining handlers or the rest of the pipeline. Note this event carries the filtered point and
    /// deliberately does NOT carry validity — consumers needing eyes-lost detection or off-screen
    /// coordinates must use <see cref="GazeSampled"/>.
    /// </para></summary>
    public event Action<PhysicalPoint, long>? CursorMoved
    {
        add
        {
            if (value is null) return;
            lock (_handlerLock)
                _cursorMovedHandlers = [.. _cursorMovedHandlers, value];
        }
        remove
        {
            if (value is null) return;
            lock (_handlerLock)
                _cursorMovedHandlers = RemoveLast(_cursorMovedHandlers, value);
        }
    }
```

Add the publication method:

```csharp
    private void PublishCursorMoved(PhysicalPoint point, long timestampUs)
    {
        var handlers = _cursorMovedHandlers;
        for (int i = 0; i < handlers.Length; i++)
        {
            try
            {
                handlers[i](point, timestampUs);
            }
            catch (Exception ex)
            {
                _faults.Report(nameof(CursorMoved), handlers[i], ex);
            }
        }
    }
```

Replace `:242`:

```csharp
        CursorMoved?.Invoke(filtered, gaze.TimestampMicroseconds);
```

with:

```csharp
        PublishCursorMoved(filtered, gaze.TimestampMicroseconds);
```

- [ ] **Step 4: Run to verify they pass**

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter "FullyQualifiedName~GazeEngineFanOutIsolationTests"
```

Expected: `Bestanden!`, `Fehler: 0`, 9 tests.

- [ ] **Step 5: Verify the whole headless suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Expected: `Fehler: 0` on all 11 assemblies.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/GazeEngine.cs tests/EyeCtrl.Core.Tests/GazeEngineFanOutIsolationTests.cs
git commit -m "fix(core): isolate each CursorMoved handler too

Same treatment as GazeSampled. GazeEngineAdapter marshals onto the dispatcher
here, and BeginInvoke can throw during shutdown, which would otherwise escape
onto the gaze thread. No registry and no signature change: CursorMoved is the
app's own output path, and dropping IsValid is deliberate."
```

---

### Task 7: Guard the Tobii poll loop (Fix 2a)

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs:296-312`

This file is entirely inside `#if TOBII_AVAILABLE`, so it only compiles when the Tobii SDK is present at `C:\Program Files\Tobii\Tobii EyeX\`. If the SDK is absent this task is a no-op edit that cannot be compiled or tested locally — verify by inspection, note it in the commit, and do not attempt to fake the SDK.

- [ ] **Step 1: Wrap the loop body**

Replace the block at `:296-312`:

```csharp
            bool hasError;
            lock (_lock)
            {
                if (!_running) return;
                tobii_error_t result = Interop.tobii_wait_for_callbacks(
                    new[] { _deviceContext });
                hasError = IsError(result);

                if (!hasError)
                {
                    result = Interop.tobii_device_process_callbacks(_deviceContext);
                    hasError = IsError(result);
                }
            }
```

with:

```csharp
            bool hasError = false;
            try
            {
                lock (_lock)
                {
                    if (!_running) return;
                    tobii_error_t result = Interop.tobii_wait_for_callbacks(
                        new[] { _deviceContext });
                    hasError = IsError(result);

                    if (!hasError)
                    {
                        result = Interop.tobii_device_process_callbacks(_deviceContext);
                        hasError = IsError(result);
                    }
                }
            }
            catch (Exception ex)
            {
                // tobii_device_process_callbacks invokes the managed gaze callback, so a subscriber
                // fault re-enters managed code here. This thread is IsBackground = true, and an
                // unhandled exception on it terminates the process — which for this app costs the
                // user their only input channel. GazeEngine guards its own handlers, so reaching
                // this catch means a fault in the pipeline itself; treat it as a device error and
                // fall into the existing backoff rather than spinning.
                //
                // The catch is OUTSIDE the lock on purpose: the backoff below must not hold _lock,
                // or Stop() cannot make progress.
                _logger.LogError(ex, "Gaze callback threw; treating as a device error and backing off");
                hasError = true;
            }
```

- [ ] **Step 2: Verify it compiles, or state that it was excluded**

```bash
dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj -c Debug
```

⚠️ **A green build here does not prove this change compiles.** The entire file sits inside `#if TOBII_AVAILABLE`, which `Tobii.props` defines only when both SDK DLLs exist under `C:\Program Files\Tobii\Tobii EyeX\`. Without the SDK the build succeeds *having compiled none of your edit*. Determine which case you are in and say so explicitly:

```bash
dotnet build src/EyeCtrl.Platform/EyeCtrl.Platform.csproj -c Debug -v:n 2>&1 | grep -i "define\|TOBII_AVAILABLE" | head -3
ls "C:/Program Files/Tobii/Tobii EyeX/tobii_stream_engine.dll" 2>/dev/null && echo "SDK PRESENT" || echo "SDK ABSENT - guarded code NOT compiled"
```

If the SDK is absent, verify by inspection only and record that in the commit message. Do not claim the change was compiled, and do not fake the SDK to force compilation. `_logger` already exists on the provider (`:20`), so the log call is well-formed either way.

- [ ] **Step 3: Verify the platform tests still pass**

```bash
dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj
```

Expected: `Fehler: 0`. `MouseGazeProviderTests` is a known load-dependent flake — if it fails, re-run it alone before investigating.

- [ ] **Step 4: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/TobiiStreamEngineProvider.cs
git commit -m "fix(gaze): stop an exception escaping PollLoop and killing the process

PollLoop had no try/catch on an IsBackground thread, so anything reaching it
terminated the process. Defence in depth alongside the per-handler guards in
GazeEngine: this also covers a fault in the pipeline itself, which no handler
guard can catch. The catch sits outside the lock so the backoff does not block
Stop()."
```

---

### Task 8: Guard the mouse provider's timer callback (Fix 2b)

**Files:**
- Modify: `src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs:22-25` (constructor), `:47-68` (`OnTick`)
- Test: `tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderFaultTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderFaultTests.cs`:

The obvious test — "the provider keeps ticking after a subscriber throws" — is worthless here, and it is worth knowing why before writing it. The exception escapes on a pool thread that xUnit does not observe, and the existing `finally` re-arms the timer regardless, so such a test **passes against the unfixed code**. It would be permanent false assurance: delete the `catch` later and it stays green.

Assert the thing only the `catch` can produce instead — the log. That genuinely fails without the fix. Use a private nested fake; this repo uses no mocking library.

```csharp
namespace EyeCtrl.Platform.Tests.Gaze;

using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Gaze;
using Microsoft.Extensions.Logging;
using NFluent;

public class MouseGazeProviderFaultTests
{
    private sealed class CapturingLogger : ILogger
    {
        public readonly List<Exception> Faults = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Faults.Add(exception);
        }
    }

    [Fact]
    public async Task AThrowingSubscriber_IsCaughtAndLogged_NotLeftToKillTheProcess()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        sut.GazePointReceived += _ => throw new InvalidOperationException("boom");

        sut.Start();
        await Task.Delay(200);
        sut.Stop();

        // Without the catch, the exception escapes the timer callback and nothing is ever logged.
        Check.That(logger.Faults).Not.IsEmpty();
        Check.That(logger.Faults[0]).IsInstanceOf<InvalidOperationException>();
    }

    [Fact]
    public async Task AThrowingSubscriber_DoesNotStopTheProviderTicking()
    {
        var logger = new CapturingLogger();
        using var sut = new MouseGazeProvider(pollIntervalMs: 10, logger: logger);
        int calls = 0;
        sut.GazePointReceived += _ => { calls++; throw new InvalidOperationException("boom"); };

        sut.Start();
        await Task.Delay(200);
        sut.Stop();

        // The finally already re-armed before this change; this guards that the added catch did not
        // break the re-arm.
        Check.That(calls > 1).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify the first test fails**

```bash
dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~MouseGazeProviderFaultTests"
```

Expected: compile error first — `MouseGazeProvider` has no `logger` parameter. After Step 3 adds it but *before* the `catch` is added, `AThrowingSubscriber_IsCaughtAndLogged_NotLeftToKillTheProcess` must **fail** with an empty `Faults` list. If it passes at that point, the `catch` is not doing the work and something else is swallowing the exception — stop and investigate rather than proceeding.

- [ ] **Step 3: Add the catch and a logger**

Add `using Microsoft.Extensions.Logging;` and a field:

```csharp
    private readonly ILogger? _logger;
```

Extend the constructor — new parameter last, so all three existing `new MouseGazeProvider()` call sites keep compiling:

```csharp
    /// <summary>Initializes a new instance of the <see cref="MouseGazeProvider"/> class.</summary>
    /// <param name="pollIntervalMs">Cursor sampling interval in milliseconds.</param>
    /// <param name="logger">Optional logger for subscriber faults.</param>
    public MouseGazeProvider(int pollIntervalMs = 30, ILogger? logger = null)
    {
        _intervalMs = pollIntervalMs;
        _logger = logger;
    }
```

Add a `catch` between the existing `try` and `finally` in `OnTick`:

```csharp
        catch (Exception ex)
        {
            // OnTick is a System.Threading.Timer callback, so it runs on a pooled thread where an
            // unhandled exception terminates the process. The finally below already re-arms the
            // timer; this catch is what stops the fault from being fatal.
            _logger?.LogError(ex, "Gaze subscriber threw during a mouse-provider tick");
        }
```

- [ ] **Step 4: Run the test again**

```bash
dotnet test tests/EyeCtrl.Platform.Tests/EyeCtrl.Platform.Tests.csproj --filter "FullyQualifiedName~MouseGazeProviderFaultTests"
```

Expected: `Bestanden!`, `Fehler: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Platform/Gaze/MouseGazeProvider.cs tests/EyeCtrl.Platform.Tests/Gaze/MouseGazeProviderFaultTests.cs
git commit -m "fix(gaze): catch subscriber faults in the mouse provider's timer callback

OnTick had try/finally with no catch. The finally re-armed the timer — so a
throwing subscriber was anticipated — but the exception still escaped a pooled
timer callback, where an unhandled exception terminates the process."
```

---

### Task 9: Measure the delegate-array fan-out (closes the projection)

**Files:**
- Modify: `benchmarks/EyeCtrl.Benchmarks/GazeObserverFanOutBenchmarks.cs`

The spec projects Fix 1 as roughly cost-neutral but says plainly it is a projection: the existing benchmarks measure *interface* dispatch, and Fix 1 uses a *delegate array*. This closes that gap.

- [ ] **Step 1: Add the variant**

Add a field and a `[GlobalSetup]` addition in `GazeObserverFanOutBenchmarks`:

```csharp
    private Action<GazePoint>[] _delegateHandlers = null!;
```

In `Setup()`, after the existing `_multicast` construction:

```csharp
        // Fix 1's actual shape: a delegate array iterated by index, guarded per handler.
        _delegateHandlers = [.. _registered.Select(o => (Action<GazePoint>)o.OnGazeSample)];
```

Add `using System.Linq;` if not already present. Then the two benchmarks:

```csharp
    /// <summary>
    /// The shape Fix 1 actually ships: a delegate array iterated by index, no try/catch. Read against
    /// A_BareMulticastEvent to price replacing multicast dispatch with an array loop.
    /// </summary>
    [Benchmark]
    public long I_DelegateArray()
    {
        var stream = _stream;
        var handlers = _delegateHandlers;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < handlers.Length; k++)
                handlers[k](sample);
        }
        return _observers.Checksum();
    }

    /// <summary>
    /// Fix 1 complete: delegate array plus a per-handler try/catch. The difference against
    /// A_BareMulticastEvent is the number the spec needs — whether isolation costs anything at all.
    /// </summary>
    [Benchmark]
    public long J_DelegateArray_TryCatch()
    {
        var stream = _stream;
        var handlers = _delegateHandlers;
        for (int i = 0; i < SamplesPerBatch; i++)
        {
            ref readonly var sample = ref stream[i & StreamMask];
            for (int k = 0; k < handlers.Length; k++)
            {
                try { handlers[k](sample); }
                catch { /* fault path priced in GazeObserverFaultBenchmarks */ }
            }
        }
        return _observers.Checksum();
    }
```

- [ ] **Step 2: Build the benchmarks**

```bash
dotnet build benchmarks/EyeCtrl.Benchmarks/EyeCtrl.Benchmarks.csproj -c Release
```

Expected: `0 Fehler`, `0 Warnung(en)`.

- [ ] **Step 3: Run the harness tests**

`EyeCtrl.Benchmarks.Tests` is **not** in `EyeCtrl.slnx`, so it must be named explicitly:

```bash
dotnet test tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj
```

Expected: `Bestanden!`, `Fehler: 0`, 27 tests.

- [ ] **Step 4: Record the measurement**

```bash
dotnet run -c Release --project benchmarks/EyeCtrl.Benchmarks -- --filter "*GazeObserverFanOut*" --artifacts ./BenchmarkDotNet.Artifacts
```

Do **not** add `--inProcess`: the suite pins its own toolchain via `PinnedProjectToolchain.cs`, and in-process runs have no isolation. BDN **exits 0 even when every benchmark fails**, so verify by finding `executed benchmarks: 10` in the output — never trust the exit code.

Divide each `Mean` by 2,500,000 for ns/sample. Compare `J_DelegateArray_TryCatch` against `A_BareMulticastEvent`: the spec claims approximately cost-neutral. If `J` is more than ~1.5× `A`, stop and report — that would contradict the spec's cost argument and needs a decision, not a workaround.

This run also discharges the spec's "no allocation per publication" requirement, which is why there is no unit test for it: an allocation assertion in xUnit is brittle, whereas BDN's `Allocated` column measures it directly. Both new rows must read `-`. Anything else means the loop is allocating per sample — most likely a closure captured inside it.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/EyeCtrl.Benchmarks/GazeObserverFanOutBenchmarks.cs
git commit -m "test(bench): measure the delegate-array fan-out Fix 1 actually ships

The existing benchmarks priced interface dispatch; Fix 1 uses a delegate
array, so the spec's cost claim was a projection. These two variants make it
a measurement."
```

---

## Final verification

- [ ] **Full headless suite**

```bash
dotnet test EyeCtrl.slnx --filter "Category!=UI"
```

Expected: `Fehler: 0` on all 11 assemblies. The pre-change baseline is 1710 passed / 0 failed / 1 skipped.

- [ ] **The project the solution misses**

```bash
dotnet test tests/EyeCtrl.Benchmarks.Tests/EyeCtrl.Benchmarks.Tests.csproj
```

- [ ] **The samples, which are also outside the solution**

```bash
dotnet build samples/OffScreenGazeProbe/OffScreenGazeProbe.csproj -c Debug
```

No sample should need changing — both events keep their signatures — so a failure here means a signature was altered by mistake.

- [ ] **Confirm no Core doc-comment regressions**

```bash
dotnet build src/EyeCtrl.Core/EyeCtrl.Core.csproj -c Debug
```

Core treats CS1591 as an error, so a missing `///` on a new public member fails here rather than at review.

- [ ] **Update the spec's status**

Add a line at the top of `docs/superpowers/specs/2026-08-03-gaze-fanout-safety-design.md` recording that all four fixes are implemented, with the commit range, and replace the projected cost claim in "Cost" with the figure Task 9 recorded.
