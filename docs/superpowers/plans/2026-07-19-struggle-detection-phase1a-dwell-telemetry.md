# Struggle Detection — Phase 1a-① Dwell Telemetry Contract & Emission — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Core data contract for dwell-interaction telemetry and make `DwellDetector` emit a reason-tagged outcome on activation and genuine (gaze-driven) abandonment — with **zero behaviour change** when no sink is attached.

**Architecture:** A new `EyeCtrl.Core.Adaptation` namespace holds value-type contracts (`DwellContext`, `DwellOutcome`, `OutcomeKind`, `AbandonReason`) and an `IStruggleTelemetrySink` interface. `DwellDetector` gains an optional `(context, sink)` pair; its existing hot-path `Update` emits a `DwellOutcome` only when a sink is present, so all current call sites (which pass nothing) behave byte-for-byte as before. No surface wiring, no adaptation logic yet — those are plans 1a-② and 1a-③.

**Tech Stack:** C# / .NET 10, `EyeCtrl.Core` (framework-agnostic), xUnit v3 + NFluent (`Check.That(...)`) tests.

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` (§4 contracts, §6.1 `DwellOutcome`, §7.3/§8, §9.1 allocation-free hot path, §12 tests).

**⚠️ Core enforces XML docs as build errors:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` **and** `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every publicly visible member — **including every enum member and every positional-record parameter** (via `<param>` tags) — MUST have an XML doc comment, or the build fails. All snippets below are already fully documented to satisfy this; do not strip the doc comments.

---

## File Structure

**Create (Core contracts — one type per file, `EyeCtrl.Core.Adaptation` namespace):**
- `src/EyeCtrl.Core/Adaptation/DwellContext.cs` — enum: which surface a dwell came from.
- `src/EyeCtrl.Core/Adaptation/OutcomeKind.cs` — enum: `Activated | Abandoned`.
- `src/EyeCtrl.Core/Adaptation/AbandonReason.cs` — enum: `None | GazeLeft | NearestChanged`.
- `src/EyeCtrl.Core/Adaptation/DwellOutcome.cs` — `readonly record struct` outcome payload (circle **and** rect geometry).
- `src/EyeCtrl.Core/Adaptation/IStruggleTelemetrySink.cs` — `Report(in DwellOutcome)`.

**Modify:**
- `src/EyeCtrl.Core/Processing/DwellDetector.cs` — add `(context, sink)` ctor params + emission in `Update`.

**Test (create):**
- `tests/EyeCtrl.Core.Tests/Adaptation/DwellOutcomeTests.cs`
- `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs`

**Zero-behaviour-change guarantee:** `DwellDetector`'s new parameters default to `DwellContext.Other` / `null`. When the sink is `null`, no outcome is built or reported and `Update` executes the identical code path it does today. Existing `DwellDetectorTests` and `DwellDetectorThreadSafetyTests` must continue to pass unchanged.

---

## Task 1: Core telemetry contracts

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/DwellContext.cs`, `OutcomeKind.cs`, `AbandonReason.cs`, `DwellOutcome.cs`, `IStruggleTelemetrySink.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/DwellOutcomeTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Adaptation/DwellOutcomeTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class DwellOutcomeTests
{
    [Fact]
    public void NearMiss_IsGazeMinusTargetCenter()
    {
        var outcome = new DwellOutcome(
            TimestampMicroseconds: 1_000,
            Context: DwellContext.ActionMenu,
            TargetId: "btn",
            Kind: OutcomeKind.Abandoned,
            Reason: AbandonReason.GazeLeft,
            ProgressAtEnd: 0.5,
            GazePoint: new PhysicalPoint(120, 90),
            TargetCenter: new PhysicalPoint(100, 100),
            TargetRadius: 30,
            TargetBounds: null);

        Check.That(outcome.NearMissX).IsEqualTo(20.0);
        Check.That(outcome.NearMissY).IsEqualTo(-10.0);
        Check.That(outcome.TargetBounds.HasValue).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellOutcomeTests"`
Expected: FAIL to **compile** — `DwellOutcome`, `DwellContext`, `OutcomeKind`, `AbandonReason` do not exist.

- [ ] **Step 3: Create the five contract files (fully XML-documented — required by CS1591)**

`src/EyeCtrl.Core/Adaptation/DwellContext.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>The interaction surface a dwell outcome originated from, used for per-context
/// struggle scoring.</summary>
public enum DwellContext
{
    /// <summary>Un-instrumented or aggregate-only surface; the default.</summary>
    Other = 0,
    /// <summary>On-screen keyboard keys (nearest-key dwell).</summary>
    Keyboard,
    /// <summary>Keyboard word-prediction suggestion buttons.</summary>
    KeyboardPrediction,
    /// <summary>Radial action-menu buttons.</summary>
    ActionMenu,
    /// <summary>Manual (zoom-to-point) selection and its buttons.</summary>
    ManualSelection,
    /// <summary>Utility toolbar buttons.</summary>
    Toolbar,
    /// <summary>Unified right-edge button bar.</summary>
    EdgeButtonBar,
    /// <summary>Quick-actions grid tiles.</summary>
    QuickActions,
    /// <summary>Task-switcher tiles.</summary>
    TaskSwitcher,
    /// <summary>Emoji category tabs and grid.</summary>
    Emoji,
    /// <summary>Pinned sticky-note controls.</summary>
    Notes,
    /// <summary>Gaze-scroll stop/control buttons.</summary>
    Scroll,
    /// <summary>Gaze-trail drag-and-drop release buttons.</summary>
    DragTrail,
    /// <summary>The post-fixation activation button.</summary>
    ActivationButton,
    /// <summary>Glass-backdrop hide button.</summary>
    Backdrop,
    /// <summary>External hardware-button dwell activation.</summary>
    ExternalDwell,
    /// <summary>Eye-care break banner buttons.</summary>
    EyeCare,
}
```

`src/EyeCtrl.Core/Adaptation/OutcomeKind.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>Whether a dwell interaction completed or was abandoned before completion.</summary>
public enum OutcomeKind
{
    /// <summary>The dwell reached its required time and fired.</summary>
    Activated,
    /// <summary>The dwell was abandoned before completion (gaze-driven).</summary>
    Abandoned,
}
```

`src/EyeCtrl.Core/Adaptation/AbandonReason.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>Why a dwell was abandoned.</summary>
public enum AbandonReason
{
    /// <summary>Not an abandonment (used for activations).</summary>
    None = 0,
    /// <summary>No target was under gaze when accumulation stopped (gaze left the target).</summary>
    GazeLeft,
    /// <summary>Another target became the nearest candidate before this one completed.</summary>
    NearestChanged,
}
```

`src/EyeCtrl.Core/Adaptation/DwellOutcome.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;

/// <summary>
/// A completed dwell interaction outcome (activation or abandonment), emitted by dwell surfaces
/// for struggle detection. A value type so it is allocation-free on the gaze hot path (design §9.1).
/// </summary>
/// <param name="TimestampMicroseconds">Timestamp of the outcome, in microseconds.</param>
/// <param name="Context">The interaction surface the dwell occurred on.</param>
/// <param name="TargetId">Identifier of the dwell target.</param>
/// <param name="Kind">Whether the dwell activated or was abandoned.</param>
/// <param name="Reason">Why an abandonment occurred; <see cref="AbandonReason.None"/> for activations.</param>
/// <param name="ProgressAtEnd">Dwell fraction [0..1] reached at the outcome (1.0 for activations).</param>
/// <param name="GazePoint">Gaze position at the outcome, in physical screen pixels.</param>
/// <param name="TargetCenter">Target center, in physical screen pixels.</param>
/// <param name="TargetRadius">Circular target hit radius in physical pixels; 0 for rectangular targets.</param>
/// <param name="TargetBounds">Rectangular target bounds when the target is a rect; null for circular targets.</param>
public readonly record struct DwellOutcome(
    long TimestampMicroseconds,
    DwellContext Context,
    string TargetId,
    OutcomeKind Kind,
    AbandonReason Reason,
    double ProgressAtEnd,
    PhysicalPoint GazePoint,
    PhysicalPoint TargetCenter,
    double TargetRadius,
    PhysicalRect? TargetBounds)
{
    /// <summary>Horizontal near-miss (gaze − target center) in physical pixels.</summary>
    public double NearMissX => GazePoint.X - TargetCenter.X;

    /// <summary>Vertical near-miss (gaze − target center) in physical pixels.</summary>
    public double NearMissY => GazePoint.Y - TargetCenter.Y;
}
```

`src/EyeCtrl.Core/Adaptation/IStruggleTelemetrySink.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Receives dwell outcomes (and, in later phases, gaze-quality summaries) from every interaction
/// surface. Implementations must be cheap to invoke on the gaze/UI thread and must not throw
/// (see design §11 — enforcement lands in the serialized App-layer sink wrapper in plan 1a-②).
/// A null sink means "not instrumented" and disables emission at the source.
/// </summary>
public interface IStruggleTelemetrySink
{
    /// <summary>Reports a completed dwell interaction (activation or abandonment).</summary>
    /// <param name="outcome">The outcome to record.</param>
    void Report(in DwellOutcome outcome);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellOutcomeTests"`
Expected: PASS (1 test). If any `CS1591` error appears, a doc comment was dropped — restore it.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation tests/EyeCtrl.Core.Tests/Adaptation/DwellOutcomeTests.cs
git commit -m "feat(adaptation): add dwell telemetry contracts (DwellOutcome, sink, enums)"
```

---

## Task 2: DwellDetector emits an Activated outcome

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class DwellDetectorTelemetryTests
{
    private sealed class RecordingSink : IStruggleTelemetrySink
    {
        public List<DwellOutcome> Outcomes { get; } = new();
        public void Report(in DwellOutcome outcome) => Outcomes.Add(outcome);
    }

    // Drives gaze inside a target for `samples` frames at 30 ms steps starting at `startTs`.
    private static long DriveInside(DwellDetector d, PhysicalPoint p, int samples, long startTs)
    {
        long t = startTs;
        for (int i = 0; i < samples; i++) { d.Update(p, t); t += 30_000; }
        return t;
    }

    [Fact]
    public void Activation_EmitsActivatedOutcome_WithContextGeometryAndFullProgress()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(cooldownMs: 0, context: DwellContext.ActionMenu, sink: sink);
        detector.AddTarget(new DwellTarget("btn", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        // 200 ms dwell at 30 ms/sample → activates within ~8 samples.
        DriveInside(detector, new PhysicalPoint(100, 100), 9, startTs: 0);

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Activated);
        Check.That(o.Reason).IsEqualTo(AbandonReason.None);
        Check.That(o.Context).IsEqualTo(DwellContext.ActionMenu);
        Check.That(o.TargetId).IsEqualTo("btn");
        Check.That(o.ProgressAtEnd).IsEqualTo(1.0);
        // Geometry / vector (design §12: activation "with correct reason/geometry/vector").
        Check.That(o.GazePoint).IsEqualTo(new PhysicalPoint(100, 100));
        Check.That(o.TargetCenter).IsEqualTo(new PhysicalPoint(100, 100));
        Check.That(o.TargetRadius).IsEqualTo(30.0);
        Check.That(o.TargetBounds.HasValue).IsFalse();
        Check.That(o.NearMissX).IsEqualTo(0.0);
        Check.That(o.NearMissY).IsEqualTo(0.0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorTelemetryTests"`
Expected: FAIL to **compile** — `DwellDetector` has no `context`/`sink` constructor parameters.

- [ ] **Step 3: Add ctor params, fields, and activation emission**

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`, add this using after the existing `using` lines:

```csharp
using EyeCtrl.Core.Adaptation;
```

Replace the fields block and constructor. Change:

```csharp
    private long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    /// <summary>
    /// Initializes a new instance of the <see cref="DwellDetector"/> class.
    /// </summary>
    /// <param name="cooldownMs">Cooldown period in milliseconds after an activation before another can trigger.</param>
    public DwellDetector(int cooldownMs = 0)
    {
        _cooldownMicroseconds = cooldownMs * MathConstants.MicrosecondsPerMillisecond;
    }
```

to:

```csharp
    private long _cooldownMicroseconds;
    private long _lastTimestamp;
    private long? _lastActivationTimestamp;

    private readonly DwellContext _context;
    private readonly IStruggleTelemetrySink? _sink;

    /// <summary>
    /// Initializes a new instance of the <see cref="DwellDetector"/> class.
    /// </summary>
    /// <param name="cooldownMs">Cooldown period in milliseconds after an activation before another can trigger.</param>
    /// <param name="context">Interaction surface this detector serves; tags emitted outcomes.</param>
    /// <param name="sink">Optional telemetry sink. When null, no outcomes are emitted and the
    /// hot path is byte-for-byte identical to a detector with no telemetry.</param>
    public DwellDetector(int cooldownMs = 0, DwellContext context = DwellContext.Other,
        IStruggleTelemetrySink? sink = null)
    {
        _cooldownMicroseconds = cooldownMs * MathConstants.MicrosecondsPerMillisecond;
        _context = context;
        _sink = sink;
    }
```

In the `Update(..., Func<DwellTarget,bool>? targetFilter)` method, add an outcome local just before the accumulation loop (next to `string? activatedId = null;`):

```csharp
        string? activatedId = null;
        DwellOutcome? activationOutcome = null;
```

Inside the accumulation loop, in the branch that fires activation, build the outcome only when a sink is present. Change:

```csharp
                    if (elapsed >= target.DwellTimeMicroseconds)
                    {
                        activatedId = target.Id;
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                        _lastActivationTimestamp = timestampMicroseconds;
                    }
```

to:

```csharp
                    if (elapsed >= target.DwellTimeMicroseconds)
                    {
                        activatedId = target.Id;
                        if (_sink is not null)
                            activationOutcome = new DwellOutcome(
                                timestampMicroseconds, _context, target.Id,
                                OutcomeKind.Activated, AbandonReason.None,
                                1.0, gazePoint, target.Center, target.Radius, target.Bounds);
                        target.EnteredTimestamp = null;
                        target.IsGazeInside = false;
                        _lastActivationTimestamp = timestampMicroseconds;
                    }
```

After the loop, extend the activation-firing block. Change:

```csharp
        // Fire activation after loop — handlers may modify the target collection
        if (activatedId != null)
        {
            TargetProgress?.Invoke(activatedId, 0);
            TargetActivated?.Invoke(activatedId);
        }
```

to:

```csharp
        // Fire activation after loop — handlers may modify the target collection
        if (activatedId != null)
        {
            TargetProgress?.Invoke(activatedId, 0);
            TargetActivated?.Invoke(activatedId);
        }

        if (_sink is not null && activationOutcome is { } completed)
            _sink.Report(completed);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorTelemetryTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Run the existing DwellDetector suite to confirm zero behaviour change**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorTests|FullyQualifiedName~DwellDetectorThreadSafetyTests"`
Expected: PASS (all existing tests, unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs
git commit -m "feat(adaptation): emit Activated dwell outcome (sink-gated, zero behaviour change)"
```

---

## Task 3: DwellDetector emits Abandoned outcomes (gaze-driven only, never spuriously)

**Files:**
- Modify: `src/EyeCtrl.Core/Processing/DwellDetector.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these methods to `DwellDetectorTelemetryTests`:

```csharp
    [Fact]
    public void GazeLeavesAfterEnoughProgress_EmitsAbandoned_GazeLeft()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        // Enter at t=0, dwell to ~50% (5 samples spans 120 ms of the 300 ms), then leave to empty space.
        long t = DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0);
        detector.Update(new PhysicalPoint(700, 700), t); // no target under gaze → nearest is null

        Check.That(sink.Outcomes).HasSize(1);
        var o = sink.Outcomes[0];
        Check.That(o.Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(o.Reason).IsEqualTo(AbandonReason.GazeLeft);
        Check.That(o.Context).IsEqualTo(DwellContext.Keyboard);
        Check.That(o.ProgressAtEnd >= 0.3).IsTrue();
        Check.That(o.NearMissX).IsEqualTo(600.0); // 700 - 100
    }

    [Fact]
    public void NearestSwitchesAfterEnoughProgress_EmitsAbandoned_NearestChanged()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Toolbar, sink: sink);
        detector.AddTarget(new DwellTarget("A", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));
        detector.AddTarget(new DwellTarget("B", new PhysicalPoint(400, 100), radius: 30, dwellTimeMs: 300));

        long t = DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // dwell A to ~50%
        detector.Update(new PhysicalPoint(400, 100), t); // gaze jumps to B → A abandoned, B now nearest

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].TargetId).IsEqualTo("A");
        Check.That(sink.Outcomes[0].Kind).IsEqualTo(OutcomeKind.Abandoned);
        Check.That(sink.Outcomes[0].Reason).IsEqualTo(AbandonReason.NearestChanged);
    }

    [Fact]
    public void GazeLeavesBelowThreshold_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        // Only ~1 sample of accumulation (~10% of 300 ms) before leaving — below the 0.3 threshold.
        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 30_000);
        detector.Update(new PhysicalPoint(700, 700), 60_000);

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void AbandonAtExactlyThreshold_Emits()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        detector.Update(new PhysicalPoint(100, 100), 0);       // enter, EnteredTimestamp = 0
        detector.Update(new PhysicalPoint(700, 700), 60_000);  // leave → progress = 60000/200000 = 0.3 exactly

        Check.That(sink.Outcomes).HasSize(1);
        Check.That(sink.Outcomes[0].ProgressAtEnd).IsEqualTo(0.3);
    }

    [Fact]
    public void FilterExcludesTargetMidDwell_EmitsNothing()
    {
        // Regression: an administratively excluded target (gaze physically unchanged) must NOT
        // be reported as an abandonment. Mirrors Update_FilterFlipsMidDwell_ExcludedTargetStateResets.
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.ActionMenu, sink: sink);
        detector.AddTarget(new DwellTarget("t", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        detector.Update(new PhysicalPoint(100, 100), 0);
        detector.Update(new PhysicalPoint(100, 100), 150_000);                       // ~75% progress
        detector.Update(new PhysicalPoint(100, 100), 180_000, targetFilter: _ => false); // excluded, gaze unchanged

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void ResetAllState_AfterPartialDwell_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Keyboard, sink: sink);
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // ~50%, no completion
        detector.ResetAllState(); // programmatic reset — not a struggle signal

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void TargetRemoval_AfterPartialDwell_EmitsNothing()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(context: DwellContext.Notes, sink: sink);
        detector.AddTarget(new DwellTarget("n", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 300));

        DriveInside(detector, new PhysicalPoint(100, 100), 5, startTs: 0); // ~50%
        detector.RemoveTarget("n");
        detector.ClearTargets();

        Check.That(sink.Outcomes).IsEmpty();
    }

    [Fact]
    public void Cooldown_AfterActivation_NeverEmitsAbandoned()
    {
        var sink = new RecordingSink();
        var detector = new DwellDetector(cooldownMs: 300, context: DwellContext.Toolbar, sink: sink);
        detector.AddTarget(new DwellTarget("b", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));

        // Keep gazing through activation and into the cooldown window.
        DriveInside(detector, new PhysicalPoint(100, 100), 20, startTs: 0);

        Check.That(sink.Outcomes).Not.IsEmpty();
        Check.That(sink.Outcomes.TrueForAll(o => o.Kind == OutcomeKind.Activated)).IsTrue();
    }

    [Fact]
    public void NullSink_NeverThrows_AndStillActivates()
    {
        var detector = new DwellDetector(); // default: context Other, no sink
        detector.AddTarget(new DwellTarget("k", new PhysicalPoint(100, 100), radius: 30, dwellTimeMs: 200));
        bool activated = false;
        detector.TargetActivated += _ => activated = true;

        DriveInside(detector, new PhysicalPoint(100, 100), 9, startTs: 0);

        Check.That(activated).IsTrue();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorTelemetryTests"`
Expected: FAIL — the two positive abandonment tests (`GazeLeft`, `NearestChanged`), `AbandonAtExactlyThreshold`, fail (no `Abandoned` outcomes recorded). The negative tests (`BelowThreshold`, `FilterExcludes`, `ResetAllState`, `TargetRemoval`, `Cooldown`, `NullSink`) already pass — that is fine.

- [ ] **Step 3: Add the abandonment const and gaze-driven emission**

In `src/EyeCtrl.Core/Processing/DwellDetector.cs`, add the threshold const next to the fields added in Task 2:

```csharp
    private readonly DwellContext _context;
    private readonly IStruggleTelemetrySink? _sink;

    /// <summary>Minimum dwell fraction [0..1] an abandoned target must have reached to be reported.</summary>
    private const double AbandonProgressThreshold = 0.3;
```

Add a second outcome local next to `activationOutcome`:

```csharp
        string? activatedId = null;
        DwellOutcome? activationOutcome = null;
        DwellOutcome? abandonOutcome = null;
```

In the accumulation loop's `else` branch (non-nearest targets), build an abandonment outcome **only** when a previously-inside target that is still eligible this frame is being reset after reaching the threshold. The eligibility guard (`targetFilter is null || targetFilter(target)`) prevents administrative filter-exclusions — where gaze has not moved — from being misreported as abandonment. Change:

```csharp
            else
            {
                if (target.IsGazeInside)
                    TargetProgress?.Invoke(target.Id, 0);
                target.IsGazeInside = false;
                target.EnteredTimestamp = null;
            }
```

to:

```csharp
            else
            {
                if (target.IsGazeInside)
                {
                    TargetProgress?.Invoke(target.Id, 0);
                    if (_sink is not null && target.EnteredTimestamp.HasValue
                        && (targetFilter is null || targetFilter(target)))
                    {
                        long abandonElapsed = timestampMicroseconds - target.EnteredTimestamp.Value;
                        double abandonProgress = Math.Min(1.0,
                            (double)abandonElapsed / target.DwellTimeMicroseconds);
                        if (abandonProgress >= AbandonProgressThreshold)
                            abandonOutcome = new DwellOutcome(
                                timestampMicroseconds, _context, target.Id,
                                OutcomeKind.Abandoned,
                                nearestId is null ? AbandonReason.GazeLeft : AbandonReason.NearestChanged,
                                abandonProgress, gazePoint, target.Center, target.Radius, target.Bounds);
                    }
                }
                target.IsGazeInside = false;
                target.EnteredTimestamp = null;
            }
```

Extend the post-loop reporting block to also report abandonment. Change:

```csharp
        if (_sink is not null && activationOutcome is { } completed)
            _sink.Report(completed);
```

to:

```csharp
        if (_sink is not null)
        {
            if (abandonOutcome is { } abandoned)
                _sink.Report(abandoned);
            if (activationOutcome is { } completed)
                _sink.Report(completed);
        }
```

Note: the `ResetAllState` method and the cooldown early-return branch remain untouched, so programmatic resets, target removal, and post-activation cooldown never emit — verified by `ResetAllState_*`, `TargetRemoval_*`, and `Cooldown_*` tests.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~DwellDetectorTelemetryTests"`
Expected: PASS (10 tests).

- [ ] **Step 5: Run the full Core suite to confirm zero regressions**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS (all Core tests, including the untouched `DwellDetectorTests`/`DwellDetectorThreadSafetyTests`).

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Processing/DwellDetector.cs tests/EyeCtrl.Core.Tests/Adaptation/DwellDetectorTelemetryTests.cs
git commit -m "feat(adaptation): emit gaze-driven Abandoned outcomes (threshold + eligibility gated)"
```

---

## Self-Review

**Spec coverage (this plan's slice of Phase 1a):**
- §4 contracts (`IStruggleTelemetrySink`, `DwellContext`) — Task 1. ✓
- §6.1 `DwellOutcome` (timestamp, context, target id, kind, reason, progress, gaze, center, radius, **rect bounds**, near-miss) — Task 1. ✓
- §6.1 fired from `DwellDetector.Update` on genuine gaze-driven departure; **never** from `ResetAllState`, cooldown, removal, or administrative filter-exclusion — Task 2 (activation) + Task 3 (abandonment gate + `FilterExcludes`/`ResetAllState`/`TargetRemoval`/`Cooldown` tests). ✓
- §6.1 abandonment threshold `0.3` inclusive — Task 3 (`AbandonAtExactlyThreshold`, `GazeLeavesBelowThreshold`). ✓
- §12 activation "with correct reason/geometry/vector" — Task 2 asserts `GazePoint`/`TargetCenter`/`TargetRadius`/`TargetBounds`/`NearMiss`. ✓
- §9.1 allocation-free / zero-cost when disabled — sink-gated emission; `readonly record struct` + `Nullable<struct>` stack locals + `in` param, no heap allocation; verified by the null-sink test and the untouched existing suites. ✓
- §11 no-throw — documented on `IStruggleTelemetrySink`; enforcement point (App-layer serialized sink wrapper) called out for plan 1a-②. ✓
- **Deferred (documented, not gaps):** surface construction-site wiring + keyboard net-new emission + gaze-quality signals + serialized App sink → plan 1a-②. Effective-settings layer + reconfiguration → plan 1a-③. `KeyboardViewModel` and every `DwellDetector` construction site are intentionally untouched here (default `null` sink ⇒ no behaviour change).

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `DwellOutcome`'s 10 positional parameters (`TimestampMicroseconds`, `Context`, `TargetId`, `Kind`, `Reason`, `ProgressAtEnd`, `GazePoint`, `TargetCenter`, `TargetRadius`, `TargetBounds`) are constructed identically in the `DwellOutcomeTests` fixture, both emission sites (activation in Task 2, abandonment in Task 3), and match the `DwellTarget` source (`Center`, `Radius`, `Bounds`). `DwellDetector` ctor `(int cooldownMs = 0, DwellContext context = DwellContext.Other, IStruggleTelemetrySink? sink = null)` is consistent across Tasks 2 and 3. `AbandonProgressThreshold` is declared once (Task 3) and used once.

**CS1591 gate:** all three enums document every member, `DwellOutcome` documents every positional parameter with `<param>`, and every interface/method has a `<summary>` — so the contracts compile under `WarningsAsErrors=CS1591`.

**Implementer notes:**
- The `EyeCtrl.Core.Tests` project already wires NFluent + xUnit v3 globally (`tests/Directory.Build.props`), so `Check.That(...)`, `Not`, `[Fact]`, and `List<>` need no extra `using`. Add only `EyeCtrl.Core.Adaptation`, `.Models`, `.Processing`.
- **Sequencing dependency:** plans 1a-② and 1a-③ are not yet written. After this plan merges, `IStruggleTelemetrySink` and `DwellOutcome` are intentionally unconsumed (no production sink until 1a-②) — expected for phased slicing, not dead-code rot.
