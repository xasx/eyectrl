# Struggle Detection — Phase 1b-③a Core Signal Scorers & Assessment Types — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the Core assessment/signal **types** (`SignalScore`, `FailureMode`, `FailureModeAssessment`, `StruggleAssessment`) and the **four core struggle-signal scorers** — Abandonment, Accidental-activation, Jitter, Poor-tracking — as **pure, stateless functions over an already-windowed evidence slice**, exactly per spec §13.2. **Zero behaviour change:** these are new, unreferenced pure types + functions; nothing consumes them until the `StruggleMonitor` (plan 1b-③b) and `AdaptationController` (plan 1b-④) land.

**Architecture:** The `StruggleMonitor` (1b-③b) will read the 1a `BufferingTelemetrySink`, filter its snapshot to the `[max(sessionStart, now−60 s), now]` evidence window, classify, and build a `StruggleAssessment`. This slice supplies the *leaves* of that design: a `StruggleSignals` static class whose four scorers each map an **already-filtered** `IReadOnlyList<DwellOutcome>` or `IReadOnlyList<GazeQualitySummary>` to a `SignalScore` (`score` + `confidence` + `gatePassed`), plus the `StruggleAssessment` output contract the monitor will populate. Keeping the scorers pure (no sink, no clock, no session state) makes them fully headless-testable over synthetic sequences and leaves all window/session/classification concerns to 1b-③b.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`, `IsCloseTo(expected, delta)` for division results).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.2** (the approved 1b-③ scoring design: signal table, "slow/effortful folded into abandonment as a retry term", "jitter/tracking px-thresholds are uncalibrated starting guesses", the 1b-③a/③b decomposition, `StruggleAssessment` output), §13.1 (accidental re-approach W≈1.5 s / R≈2× radius + floor / same-context; keyboard→Backspace corroboration), §6.1 (`DwellOutcome`), §6.2/§6.3 (signals; poor-tracking time-based gate; per-signal confidence), §7.1/§13.2 (failure-mode → response table).

**Depends on:** **Phase 1a implemented** — specifically the 1a contracts this slice's scorers consume: `DwellOutcome` + `OutcomeKind` + `AbandonReason` + `DwellContext` (plan 1a-①) and `GazeQualitySummary` (plan 1a-④). Also the pre-existing `PhysicalPoint` (unchanged). **No dependency on 1b-①/②** (no `EffectiveValue`, `AdaptationSettings`, or provider), and **no dependency on `BufferingTelemetrySink` / `StruggleMonitor`** — the scorers take plain lists; the sink read + windowing is 1b-③b. Written against the **post-1a** source.

**Executor pre-flight (do this FIRST — MANDATORY; 1a is plans-only, not yet in the tree):** confirm these 1a contracts against the *merged* 1a source before writing any code — a shape mismatch breaks every task silently:
1. `DwellOutcome` is a `readonly record struct` with (at least) `long TimestampMicroseconds`, `DwellContext Context`, `string TargetId`, `OutcomeKind Kind`, `AbandonReason Reason`, `double ProgressAtEnd`, `PhysicalPoint TargetCenter`, `double TargetRadius` (plan 1a-① §6.1). This slice reads `TimestampMicroseconds`, `Context`, `TargetId`, `Kind`, `ProgressAtEnd`, `TargetCenter`, `TargetRadius` (it does **not** need `GazePoint`/`NearMiss`/`TargetBounds` — those feed the Aiming/Drift scorers in 1b-③b).
2. `OutcomeKind { Activated, Abandoned }` and `AbandonReason { None, GazeLeft, NearestChanged }` (plan 1a-①).
3. `DwellContext` is an enum including `Keyboard` and `Other` (plan 1a-①).
4. `GazeQualitySummary` is a `readonly record struct(long TimestampMicroseconds, double InvalidFraction, double JitterStdDev, double FatigueScore, int SampleCount)` (plan 1a-④).
5. `PhysicalPoint` exposes `double DistanceSquaredTo(PhysicalPoint other)` (verified pre-existing at `src/EyeCtrl.Core/Models/PhysicalPoint.cs:94`).

If any differ (renamed field, different `OutcomeKind` members, etc.), adjust the scorers/tests to match before proceeding.

**⚠️ Core enforces XML docs as build errors:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every public member below — **including every enum member, every positional record-struct `<param>`, and every public method's `<param>`/`<returns>`** — is documented. `EyeCtrl.Core` has `ImplicitUsings=enable`, so `System` (`Math`, `Array`), `System.Collections.Generic` (`IReadOnlyList`, `List`), and `System.Linq` are in scope without explicit `using`s; the scorers reference only same-namespace (`EyeCtrl.Core.Adaptation`) types, so **no extra `using` is required in the production files**. Do not strip the doc comments.

---

## Scope boundary (explicit — deferred pieces noted where they land)

This slice is the **pure-function leaves** of the 1b-③ design. Deferred:

- **The `StruggleMonitor` itself** (reads the 1a `BufferingTelemetrySink`; filters the snapshot to `[max(sessionStart, now−60 s), now]`; `ResetSession(now)` on app-start/resume-from-Pause; auto-reset on gaze-idle gap > 120 s; monotonic guard; **classification** of signal scores → `FailureMode`s; the **aggregate struggle level**; `Assess(now) → StruggleAssessment`) → **1b-③b**. This slice's scorers take an already-filtered list precisely so all of that lives in one place in 1b-③b.
- **The remaining three scorers** — Aiming (`|NearMiss| ÷ TargetRadius`), Drift (the complex cross-context ±30°-coherent one, §13.1), and the Fatigue amplifier (`GazeQualitySummary.FatigueScore`) → **1b-③b**.
- **Populating `StruggleAssessment`** — defined here as the output contract (with `None` and a `ForMode` lookup), but only *constructed by* the monitor (1b-③b) and *consumed by* the controller (1b-④). This slice tests the type's shape, not a producer.
- **Consuming the assessment** (typed knob transforms, sub-band clamps, the quarantined tightening) → the `AdaptationController`, **1b-④**.
- **Detection-threshold configurability.** The scorers' numeric thresholds are **optional parameters whose defaults are the §13.2 starting values** — deliberately *not* wired into `AdaptationSettings` (§10's settings surface is the safety *envelope* + posture, not the detection thresholds, which §13.2 marks "tuned in review / Phase 1b"). 1b-③b calls the scorers with the defaults; a later tuning pass may promote some to settings. The jitter/tracking px thresholds are **uncalibrated starting guesses** (jitter is measured on the *filtered* stream, 1a-④) and are documented as such, not authoritative.

`FailureMode` is defined with **all five** modes now (the full §7.1/§13.2 set) — a foundational enum the controller consumes — even though this slice's scorers only feed three of them (sustain-failure, accidental-activation, poor-tracking); Aiming/Drift are populated in 1b-③b. This mirrors 1a-① defining the full `DwellContext` enum up front.

---

## File Structure

**Create (Core — one type per file, `EyeCtrl.Core.Adaptation` namespace):**
- `src/EyeCtrl.Core/Adaptation/SignalScore.cs` — `readonly record struct` (score + confidence + gate); the scorer return type.
- `src/EyeCtrl.Core/Adaptation/FailureMode.cs` — enum of the five failure modes.
- `src/EyeCtrl.Core/Adaptation/FailureModeAssessment.cs` — `readonly record struct`: one mode's classified assessment (+ optional context).
- `src/EyeCtrl.Core/Adaptation/StruggleAssessment.cs` — `sealed class`: the monitor's per-tick output (mode list + aggregate + `ForMode`/`None`).
- `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs` — `static class` with the four scorers (created with the dwell-outcome scorers in Task 2, extended with the gaze-quality scorers in Task 3).

**Test (create):**
- `tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs` (Task 1).
- `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs` (Task 2, extended in Task 3).

**Zero-behaviour-change guarantee:** every file is net-new, and nothing in the existing tree references any of these types. No production wiring, no DI, no settings. The full existing suite is untouched.

---

## Task 1: Assessment & signal types (`SignalScore`, `FailureMode`, `FailureModeAssessment`, `StruggleAssessment`)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/SignalScore.cs`, `FailureMode.cs`, `FailureModeAssessment.cs`, `StruggleAssessment.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class StruggleAssessmentTests
{
    [Fact]
    public void SignalScore_None_IsZeroAndUngated()
    {
        var none = SignalScore.None;
        Check.That(none.Score).IsEqualTo(0.0);
        Check.That(none.Confidence).IsEqualTo(0.0);
        Check.That(none.GatePassed).IsFalse();
    }

    [Fact]
    public void FailureMode_DefinesTheFiveResponseModes()
    {
        // Locks the §7.1/§13.2 mode set so the monitor (1b-③b) and controller (1b-④) can rely on it.
        var modes = Enum.GetValues<FailureMode>();
        Check.That(modes).HasSize(5);
        Check.That(modes).Contains(
            FailureMode.SustainFailure,
            FailureMode.AccidentalActivation,
            FailureMode.Aiming,
            FailureMode.Drift,
            FailureMode.PoorTracking);
    }

    [Fact]
    public void StruggleAssessment_None_IsEmpty()
    {
        Check.That(StruggleAssessment.None.Modes).IsEmpty();
        Check.That(StruggleAssessment.None.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void ForMode_ReturnsMatchingEntry_OrNull()
    {
        // Positional-record named args are PascalCase (Mode/Score/Confidence/GatePassed/Context).
        var sustain = new FailureModeAssessment(
            FailureMode.SustainFailure, Score: 0.7, Confidence: 1.0, GatePassed: true, Context: null);
        var aiming = new FailureModeAssessment(
            FailureMode.Aiming, Score: 0.4, Confidence: 0.8, GatePassed: false, Context: DwellContext.ActionMenu);
        var assessment = new StruggleAssessment(new[] { sustain, aiming }, aggregateLevel: 0.7);

        Check.That(assessment.ForMode(FailureMode.SustainFailure).HasValue).IsTrue();
        Check.That(assessment.ForMode(FailureMode.SustainFailure)!.Value.Score).IsEqualTo(0.7);
        Check.That(assessment.ForMode(FailureMode.Aiming)!.Value.Context).IsEqualTo(DwellContext.ActionMenu);
        Check.That(assessment.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleAssessmentTests"`
Expected: FAIL to **compile** — `SignalScore`, `FailureMode`, `FailureModeAssessment`, `StruggleAssessment` do not exist.

- [ ] **Step 3: Create `SignalScore`**

`src/EyeCtrl.Core/Adaptation/SignalScore.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The output of one struggle-signal scorer (design §6.3/§13.2): a normalized struggle
/// <paramref name="Score"/> with an evidence <paramref name="Confidence"/> and whether the signal's
/// confidence gate is met. The controller (plan 1b-④) acts on a signal's failure mode only when
/// <paramref name="GatePassed"/> is true — there is no single mixed gate across signals.
/// </summary>
/// <param name="Score">Struggle score in [0, 1] (0 = no struggle evidence).</param>
/// <param name="Confidence">Evidence confidence in [0, 1], ramping to 1 as evidence reaches the gate.</param>
/// <param name="GatePassed">True when enough evidence has accrued for the controller to act on this signal.</param>
public readonly record struct SignalScore(double Score, double Confidence, bool GatePassed)
{
    /// <summary>A zero score with no confidence and an unmet gate — returned when the evidence window is empty.</summary>
    public static SignalScore None => new(0.0, 0.0, false);
}
```

- [ ] **Step 4: Create `FailureMode`**

`src/EyeCtrl.Core/Adaptation/FailureMode.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The struggle failure modes the monitor classifies signals into; each selects a distinct safe
/// response (design §7.1/§13.2). At most one assessment per mode is produced per controller tick.
/// </summary>
public enum FailureMode
{
    /// <summary>High-progress abandonment and/or jitter (plus retries): shorter dwell, looser tolerance, more smoothing.</summary>
    SustainFailure,

    /// <summary>Activation followed by a reversal (re-approach or keyboard Backspace): the one quarantined tightening — longer dwell, bounded and instantly escapable.</summary>
    AccidentalActivation,

    /// <summary>Large near-miss on abandons: enlarge target geometry (and its coupled visible size, atomically).</summary>
    Aiming,

    /// <summary>Cross-context coherent near-miss bias: looser tolerance and report only — calibration is never auto-touched.</summary>
    Drift,

    /// <summary>High invalid-sample fraction: more smoothing, enlarge geometry, caregiver alert if sustained; never tighten.</summary>
    PoorTracking,
}
```

- [ ] **Step 5: Create `FailureModeAssessment`**

`src/EyeCtrl.Core/Adaptation/FailureModeAssessment.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// One failure mode's classified assessment for a controller tick (design §13.2 output). Built by the
/// <c>StruggleMonitor</c> (plan 1b-③b) from the per-signal <see cref="SignalScore"/>s and consumed by
/// the AdaptationController (plan 1b-④).
/// </summary>
/// <param name="Mode">The failure mode this entry describes.</param>
/// <param name="Score">The mode's struggle score in [0, 1].</param>
/// <param name="Confidence">The mode's evidence confidence in [0, 1].</param>
/// <param name="GatePassed">True when the mode's confidence gate is met and the controller may act.</param>
/// <param name="Context">The dominant interaction context for context-specific modes; null for global modes.</param>
public readonly record struct FailureModeAssessment(
    FailureMode Mode,
    double Score,
    double Confidence,
    bool GatePassed,
    DwellContext? Context);
```

- [ ] **Step 6: Create `StruggleAssessment`**

`src/EyeCtrl.Core/Adaptation/StruggleAssessment.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// The <c>StruggleMonitor</c>'s per-tick output (design §13.2): the classified per-failure-mode
/// assessments plus an aggregate struggle level, consumed by the AdaptationController (plan 1b-④).
/// Constructed by the monitor (plan 1b-③b); this slice (1b-③a) defines the contract plus the signal
/// scorers that feed it.
/// </summary>
public sealed class StruggleAssessment
{
    /// <summary>Initializes a new assessment.</summary>
    /// <param name="modes">The per-mode assessments for this tick (at most one entry per <see cref="FailureMode"/>).</param>
    /// <param name="aggregateLevel">Aggregate struggle level in [0, 1] (drives cue intensity / logging).</param>
    public StruggleAssessment(IReadOnlyList<FailureModeAssessment> modes, double aggregateLevel)
    {
        Modes = modes;
        AggregateLevel = aggregateLevel;
    }

    /// <summary>The per-failure-mode assessments for this tick (at most one entry per <see cref="FailureMode"/>).</summary>
    public IReadOnlyList<FailureModeAssessment> Modes { get; }

    /// <summary>Aggregate struggle level in [0, 1] across all modes.</summary>
    public double AggregateLevel { get; }

    /// <summary>An empty assessment (no modes, zero level) — the monitor's output when idle or before any evidence.</summary>
    public static StruggleAssessment None { get; } = new(Array.Empty<FailureModeAssessment>(), 0.0);

    /// <summary>Returns the assessment for <paramref name="mode"/>, or null when that mode has no entry this tick.</summary>
    /// <param name="mode">The failure mode to look up.</param>
    /// <returns>The matching <see cref="FailureModeAssessment"/>, or null.</returns>
    public FailureModeAssessment? ForMode(FailureMode mode)
    {
        foreach (var m in Modes)
            if (m.Mode == mode)
                return m;
        return null;
    }
}
```

- [ ] **Step 7: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleAssessmentTests"`
Expected: PASS (4 tests). If any `CS1591` error appears, a doc comment was dropped — restore it.

- [ ] **Step 8: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/SignalScore.cs src/EyeCtrl.Core/Adaptation/FailureMode.cs src/EyeCtrl.Core/Adaptation/FailureModeAssessment.cs src/EyeCtrl.Core/Adaptation/StruggleAssessment.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleAssessmentTests.cs
git commit -m "feat(adaptation): add struggle assessment + signal-score types"
```

---

## Task 2: Dwell-outcome scorers — `ScoreAbandonment` + `ScoreAccidental`

Creates `StruggleSignals` with the two scorers that consume `DwellOutcome`s. (The two gaze-quality scorers + the `Ramp` helper are added in Task 3.)

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;

public class StruggleSignalsTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // A dwell outcome with the fields the 1b-③a scorers read. GazePoint is set to the target center
    // (unused by these scorers — NearMiss feeds the Aiming/Drift scorers in 1b-③b).
    private static DwellOutcome Dwell(
        long ts, OutcomeKind kind, double progress,
        DwellContext ctx = DwellContext.ActionMenu, string id = "t",
        double cx = 100, double cy = 100, double radius = 30) => new(
            ts, ctx, id, kind,
            kind == OutcomeKind.Abandoned ? AbandonReason.GazeLeft : AbandonReason.None,
            progress, new PhysicalPoint(cx, cy), new PhysicalPoint(cx, cy), radius, null);

    private static DwellOutcome Activate(long ts, DwellContext ctx = DwellContext.ActionMenu,
        string id = "t", double cx = 100, double cy = 100, double radius = 30) =>
        Dwell(ts, OutcomeKind.Activated, 1.0, ctx, id, cx, cy, radius);

    private static DwellOutcome Abandon(long ts, double progress, DwellContext ctx = DwellContext.ActionMenu,
        string id = "t", double cx = 100, double cy = 100, double radius = 30) =>
        Dwell(ts, OutcomeKind.Abandoned, progress, ctx, id, cx, cy, radius);

    // ---- Abandonment ----

    [Fact]
    public void Abandonment_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAbandonment(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Abandonment_ScoreIsProgressWeightedHighProgressRate()
    {
        // 5 attempts: 3 activations + 2 high-progress abandons (0.8, 1.0), placed last so nothing
        // follows them → no retries. score = (0.8 + 1.0) / 5 = 0.36.
        var window = new List<DwellOutcome>
        {
            Activate(0 * Sec),
            Activate(10 * Sec),
            Activate(20 * Sec),
            Abandon(30 * Sec, 0.8),
            Abandon(40 * Sec, 1.0),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.36, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Abandonment_LowProgressAbandons_ExcludedFromNumerator_ButCountAsAttempts()
    {
        // 4 attempts: 2 low-progress abandons (0.40, 0.45 < 0.5) + 2 activations, no retries → score 0.
        var window = new List<DwellOutcome>
        {
            Abandon(0 * Sec, 0.40),
            Abandon(10 * Sec, 0.45),
            Activate(20 * Sec),
            Activate(30 * Sec),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // 4 attempts meets the default gate
    }

    [Fact]
    public void Abandonment_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 attempts (< default 4): one high-progress abandon (0.8) + one activation → score 0.8/2 = 0.4.
        var window = new List<DwellOutcome> { Abandon(0, 0.8), Activate(10 * Sec) };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.4, 1e-9);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9); // 2 / 4
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Abandonment_RetrySameContextWithinWindow_AddsToScore()
    {
        // Abandon(0.6) then a same-context outcome 1 s later (≤ 3 s) → 1 retry.
        // base = 0.6/4 = 0.15; retry term = 0.5 * (1/4) = 0.125; score = 0.275.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(1 * Sec, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        var s = StruggleSignals.ScoreAbandonment(window);
        Check.That(s.Score).IsCloseTo(0.275, 1e-9);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Abandonment_RetryDifferentContext_NotCounted()
    {
        // Same as above but the following outcome is a DIFFERENT context → no retry → score = 0.15.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(1 * Sec, ctx: DwellContext.Toolbar),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.15, 1e-9);
    }

    [Fact]
    public void Abandonment_RetryBeyondTimeWindow_NotCounted()
    {
        // Same context but 4 s later (> default 3 s retry window) → no retry → score = 0.15.
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Activate(4 * Sec, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.15, 1e-9);
    }

    [Fact]
    public void Abandonment_FollowingSameContextAbandon_AlsoCountsAsRetry()
    {
        // A retry is ANY strictly-later same-context outcome — a re-attempt that ALSO fails
        // (abandon → abandon) is still effort evidence (a deliberate reading of §13.2's "reactivate";
        // see ScoreAbandonment docs). Pins that the follow-up need not be an activation.
        // base = (0.6 + 0.6)/4 = 0.30; #0's follow-up (#1, same ctx, 1 s) → 1 retry; term = 0.5*(1/4)
        // = 0.125; score = 0.425. (A literal "must reactivate" reading would give 0.30.)
        var window = new List<DwellOutcome>
        {
            Abandon(0, 0.6, ctx: DwellContext.ActionMenu),
            Abandon(1 * Sec, 0.6, ctx: DwellContext.ActionMenu),
            Activate(10 * Sec, ctx: DwellContext.Toolbar),
            Activate(20 * Sec, ctx: DwellContext.Toolbar),
        };
        Check.That(StruggleSignals.ScoreAbandonment(window).Score).IsCloseTo(0.425, 1e-9);
    }

    // ---- Accidental activation ----

    [Fact]
    public void Accidental_NoActivations_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAccidental(new List<DwellOutcome> { Abandon(0, 0.5) });
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Accidental_ReapproachWithinRadiusSameContext_CountsReversal()
    {
        // Activation at (100,100) r30, then an outcome 0.5 s later at (120,100): distance 20 ≤ 2*30=60,
        // same context → reversal. 3 activations total, 1 reversed → score = 1/3.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Abandon(500_000, 0.5, DwellContext.ActionMenu, "A2", 120, 100, 30),
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        };
        var s = StruggleSignals.ScoreAccidental(window);
        Check.That(s.Score).IsCloseTo(1.0 / 3.0, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0); // 3 activations meets the default gate
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Accidental_ReapproachOutsideRadius_NotReversal()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(500_000, DwellContext.ActionMenu, "B", 200, 100, 30), // dist 100 > 60
            Activate(1_000_000, DwellContext.ActionMenu, "C", 300, 100, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_ReapproachOutsideTimeWindow_NotReversal()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(2 * Sec, DwellContext.ActionMenu, "B", 110, 100, 30), // near, but 2 s > 1.5 s
            Activate(4 * Sec, DwellContext.ActionMenu, "C", 500, 100, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_DifferentContext_NotReapproach()
    {
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            Activate(500_000, DwellContext.Toolbar, "B", 100, 100, 30), // same center, different context
            Activate(1_000_000, DwellContext.ActionMenu, "C", 500, 500, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);
    }

    [Fact]
    public void Accidental_RectTarget_UsesRadiusFloor()
    {
        // Rect target (radius 0): re-approach radius falls back to the 40 px floor. Following outcome at
        // distance 30 ≤ 40 → reversal. 3 activations, 1 reversed → 1/3.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 0),
            Abandon(500_000, 0.5, DwellContext.ActionMenu, "A2", 130, 100, 0), // dist 30 ≤ floor 40
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        };
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsCloseTo(1.0 / 3.0, 1e-9);
    }

    [Fact]
    public void Accidental_KeyboardBackspaceCorroboration_CountsReversal_OnlyWhenIdSupplied()
    {
        // Key "e" activation, then Backspace activation 0.5 s later, FAR away (800 px) so the geometric
        // re-approach path can't catch it — only the backspace corroboration can. 4 activations, 1 reversed.
        var window = new List<DwellOutcome>
        {
            Activate(0, DwellContext.Keyboard, "e", 100, 100, 20),
            Activate(500_000, DwellContext.Keyboard, "Back", 900, 100, 20),
            Activate(10 * Sec, DwellContext.Keyboard, "x", 100, 100, 20),
            Activate(20 * Sec, DwellContext.Keyboard, "y", 200, 100, 20),
        };

        // Disabled by default (no backspace id) → the far Backspace is not a reversal → score 0.
        Check.That(StruggleSignals.ScoreAccidental(window).Score).IsEqualTo(0.0);

        // Enabled → key→Backspace is a reversal → 1/4.
        var s = StruggleSignals.ScoreAccidental(window, backspaceTargetId: "Back");
        Check.That(s.Score).IsCloseTo(0.25, 1e-9);
        Check.That(s.GatePassed).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: FAIL to **compile** — `StruggleSignals` does not exist.

- [ ] **Step 3: Create `StruggleSignals` with the two dwell-outcome scorers**

`src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Pure, stateless scorers for the core struggle signals (design §13.2). Each maps an
/// <b>already time-windowed</b> slice of telemetry (oldest-first, as the sink snapshots it) to a
/// <see cref="SignalScore"/>. The <c>StruggleMonitor</c> (plan 1b-③b) owns the evidence-window
/// filtering, session resets, and classification and simply calls these; keeping them pure makes them
/// fully headless-testable over synthetic sequences.
///
/// All numeric thresholds are optional parameters whose defaults are the §13.2 <b>starting values</b>,
/// tunable in review. The jitter/tracking px thresholds in particular are <b>uncalibrated guesses</b>
/// (jitter is measured on the FILTERED gaze stream — plan 1a-④) and are not authoritative.
/// </summary>
public static class StruggleSignals
{
    /// <summary>
    /// Abandonment / sustain-failure signal (design §13.2): the progress-weighted high-progress
    /// abandonment rate plus a retry term that folds in the "slow/effortful" signal (§6.2 — retries
    /// carry the same "make it easier" meaning; true time-to-activate is not derivable from
    /// <see cref="DwellOutcome"/>, which has no dwell-enter timestamp). Score =
    /// Σ(progress of high-progress abandons) ÷ attempts + retryBoost × (retries ÷ attempts), clamped
    /// to [0, 1]. "Attempts" is every outcome in the window (each activation or abandonment is one).
    /// A "retry" is ANY strictly-later same-context outcome within <paramref name="retryWindowMicroseconds"/>
    /// — activation OR abandonment: a re-attempt that also fails is still effort evidence. This is a
    /// deliberate reading of §13.2's "reactivate" (a re-engagement, not necessarily a success); tunable.
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window, oldest first.</param>
    /// <param name="minAttempts">Confidence gate: minimum outcomes before the controller may act. Default 4.</param>
    /// <param name="highProgressThreshold">Minimum <see cref="DwellOutcome.ProgressAtEnd"/> for an abandon to count as "high-progress" (near-complete). Default 0.5.</param>
    /// <param name="retryBoost">Weight of the retry rate added to the score. Default 0.5.</param>
    /// <param name="retryWindowMicroseconds">Maximum gap for a later same-context outcome to count as a retry of an abandon. Default 3 000 000 (3 s).</param>
    /// <returns>The abandonment signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScoreAbandonment(
        IReadOnlyList<DwellOutcome> window,
        int minAttempts = 4,
        double highProgressThreshold = 0.5,
        double retryBoost = 0.5,
        long retryWindowMicroseconds = 3_000_000)
    {
        int attempts = window.Count;
        if (attempts == 0)
            return SignalScore.None;

        double highProgressSum = 0.0;
        int retries = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned)
                continue;

            if (o.ProgressAtEnd >= highProgressThreshold)
                highProgressSum += o.ProgressAtEnd;

            // Retry: a strictly-later same-context outcome within the retry window. The window is
            // time-ordered, so once the gap exceeds the retry window no later outcome can be closer.
            for (int j = i + 1; j < window.Count; j++)
            {
                long gap = window[j].TimestampMicroseconds - o.TimestampMicroseconds;
                if (gap > retryWindowMicroseconds)
                    break;
                if (gap > 0 && window[j].Context == o.Context)
                {
                    retries++;
                    break; // count each abandon at most once
                }
            }
        }

        double score = Math.Clamp(
            highProgressSum / attempts + retryBoost * ((double)retries / attempts),
            0.0, 1.0);
        double confidence = Math.Clamp((double)attempts / minAttempts, 0.0, 1.0);
        return new SignalScore(score, confidence, attempts >= minAttempts);
    }

    /// <summary>
    /// Accidental-activation signal (design §13.1/§13.2): the fraction of activations that were
    /// reversed. A reversal is either a <b>re-approach</b> — a strictly-later outcome in the SAME
    /// context whose target center lies within R of the activated target's center, within the
    /// re-approach window — or, in <see cref="DwellContext.Keyboard"/>, a key activation followed by a
    /// Backspace-key activation within the window (the corroborating signal, enabled only when
    /// <paramref name="backspaceTargetId"/> is supplied). This is the ONLY signal that drives a
    /// tightening (longer-dwell) response, quarantined and bounded (§8.2).
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window, oldest first.</param>
    /// <param name="minActivations">Confidence gate: minimum activations before the controller may act. Default 3.</param>
    /// <param name="reapproachWindowMicroseconds">Maximum gap for a following outcome to count as a reversal. Default 1 500 000 (1.5 s).</param>
    /// <param name="radiusMultiplier">Re-approach radius as a multiple of the activated target's radius. Default 2.0.</param>
    /// <param name="radiusFloorPx">Absolute floor (px) for the re-approach radius, covering small or rectangular (radius 0) targets. Default 40.</param>
    /// <param name="backspaceTargetId">Target id of the keyboard Backspace key; when non-null, enables the key→Backspace corroboration. Default null (disabled).</param>
    /// <returns>The accidental-activation signal score; <see cref="SignalScore.None"/> when the window has no activations.</returns>
    public static SignalScore ScoreAccidental(
        IReadOnlyList<DwellOutcome> window,
        int minActivations = 3,
        long reapproachWindowMicroseconds = 1_500_000,
        double radiusMultiplier = 2.0,
        double radiusFloorPx = 40.0,
        string? backspaceTargetId = null)
    {
        int activations = 0;
        for (int i = 0; i < window.Count; i++)
            if (window[i].Kind == OutcomeKind.Activated)
                activations++;
        if (activations == 0)
            return SignalScore.None;

        int reversals = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome a = window[i];
            if (a.Kind != OutcomeKind.Activated)
                continue;

            double radius = Math.Max(radiusMultiplier * a.TargetRadius, radiusFloorPx);
            double radiusSq = radius * radius;
            for (int j = i + 1; j < window.Count; j++)
            {
                long gap = window[j].TimestampMicroseconds - a.TimestampMicroseconds;
                if (gap > reapproachWindowMicroseconds)
                    break; // time-ordered → nothing later is closer
                if (gap <= 0)
                    continue;

                DwellOutcome n = window[j];
                bool reapproach = n.Context == a.Context
                    && n.TargetCenter.DistanceSquaredTo(a.TargetCenter) <= radiusSq;
                bool backspace = backspaceTargetId is not null
                    && a.Context == DwellContext.Keyboard
                    && n.Context == DwellContext.Keyboard
                    && n.Kind == OutcomeKind.Activated
                    && n.TargetId == backspaceTargetId;
                if (reapproach || backspace)
                {
                    reversals++;
                    break; // count each activation at most once
                }
            }
        }

        double score = Math.Clamp((double)reversals / activations, 0.0, 1.0);
        double confidence = Math.Clamp((double)activations / minActivations, 0.0, 1.0);
        return new SignalScore(score, confidence, activations >= minActivations);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: PASS (15 tests — 8 abandonment + 7 accidental). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleSignals.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs
git commit -m "feat(adaptation): add abandonment + accidental-activation signal scorers"
```

---

## Task 3: Gaze-quality scorers — `ScoreJitter` + `ScorePoorTracking`

Extends `StruggleSignals` with the two scorers that consume `GazeQualitySummary`s, plus the shared private `Ramp` helper.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these methods to `StruggleSignalsTests` (before the closing brace), plus the `Quality` helper:

```csharp
    // A gaze-quality summary with the fields the gaze-quality scorers read (FatigueScore unused here).
    private static GazeQualitySummary Quality(long ts, double invalidFraction, double jitter, int sampleCount = 30) =>
        new(ts, invalidFraction, jitter, 0.0, sampleCount);

    // ---- Jitter ----

    [Fact]
    public void Jitter_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreJitter(new List<GazeQualitySummary>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Jitter_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 summaries (< default 3), both above the high threshold → score 1, gate false, confidence 2/3.
        var window = new List<GazeQualitySummary> { Quality(0, 0.0, 500), Quality(Sec, 0.0, 500) };
        var s = StruggleSignals.ScoreJitter(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.Confidence).IsCloseTo(2.0 / 3.0, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Jitter_MeanBelowLow_ScoreZero()
    {
        var window = new List<GazeQualitySummary> { Quality(0, 0, 10), Quality(Sec, 0, 20), Quality(2 * Sec, 0, 30) };
        var s = StruggleSignals.ScoreJitter(window); // mean 20 < 50
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Jitter_MeanAboveHigh_ScoreOne()
    {
        var window = new List<GazeQualitySummary> { Quality(0, 0, 500), Quality(Sec, 0, 500), Quality(2 * Sec, 0, 500) };
        Check.That(StruggleSignals.ScoreJitter(window).Score).IsEqualTo(1.0);
    }

    [Fact]
    public void Jitter_MidRamp_IsLinear()
    {
        // mean 225 is the midpoint of the [50, 400] ramp → 0.5.
        var window = new List<GazeQualitySummary> { Quality(0, 0, 225), Quality(Sec, 0, 225), Quality(2 * Sec, 0, 225) };
        Check.That(StruggleSignals.ScoreJitter(window).Score).IsCloseTo(0.5, 1e-9);
    }

    // ---- Poor tracking ----

    [Fact]
    public void PoorTracking_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScorePoorTracking(new List<GazeQualitySummary>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void PoorTracking_SpanBelowGate_GateFalse()
    {
        // Two summaries spanning 1 s (< default 2 s) → gate false, confidence 0.5. Score still computed.
        var window = new List<GazeQualitySummary> { Quality(0, 0.6, 0), Quality(Sec, 0.6, 0) };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(1.0); // mean invalid 0.6 ≥ high 0.5
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void PoorTracking_TimeBasedGate_FiresWithZeroInteractions()
    {
        // Summaries span 2 s with a high invalid fraction and NO dwell outcomes anywhere → gate passes
        // purely on elapsed time (§6.3: tracking loss produces no interactions but must still be detected).
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.6, 0),
            Quality(Sec, 0.6, 0),
            Quality(2 * Sec, 0.6, 0),
        };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void PoorTracking_MeanIsSampleCountWeighted()
    {
        // 90 valid samples then 10 samples at 0.4 invalid → true fraction 0.04 (< low 0.1) → score 0.
        // A naive per-summary mean would be 0.2 and score 0.25 — this asserts the weighting.
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.0, 0, sampleCount: 90),
            Quality(2 * Sec, 0.4, 0, sampleCount: 10),
        };
        var s = StruggleSignals.ScorePoorTracking(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // 2 s span
    }

    [Fact]
    public void PoorTracking_MidRamp_IsLinear()
    {
        // Uniform invalid fraction 0.3 → midpoint of [0.1, 0.5] ramp → 0.5.
        var window = new List<GazeQualitySummary>
        {
            Quality(0, 0.3, 0),
            Quality(Sec, 0.3, 0),
            Quality(2 * Sec, 0.3, 0),
        };
        Check.That(StruggleSignals.ScorePoorTracking(window).Score).IsCloseTo(0.5, 1e-9);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: FAIL to **compile** — `ScoreJitter` / `ScorePoorTracking` do not exist.

- [ ] **Step 3: Add the two gaze-quality scorers + the `Ramp` helper**

In `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`, add these members inside the `StruggleSignals` class (e.g. after `ScoreAccidental`):

```csharp
    /// <summary>
    /// Jitter / shakiness signal (design §13.2): a linear ramp of the mean fixation gaze-speed
    /// standard deviation across the window's summaries. Feeds the sustain-failure mode.
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window, oldest first.</param>
    /// <param name="minSummaries">Confidence gate: minimum summaries before the controller may act. Default 3.</param>
    /// <param name="lowJitterPxPerSec">Ramp start (px/s); at or below this the score is 0. Default 50 — UNCALIBRATED starting guess (jitter is on the filtered stream, plan 1a-④).</param>
    /// <param name="highJitterPxPerSec">Ramp end (px/s); at or above this the score is 1. Default 400 — UNCALIBRATED starting guess.</param>
    /// <returns>The jitter signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScoreJitter(
        IReadOnlyList<GazeQualitySummary> window,
        int minSummaries = 3,
        double lowJitterPxPerSec = 50.0,
        double highJitterPxPerSec = 400.0)
    {
        int count = window.Count;
        if (count == 0)
            return SignalScore.None;

        double sum = 0.0;
        for (int i = 0; i < window.Count; i++)
            sum += window[i].JitterStdDev;
        double mean = sum / count;

        double score = Ramp(mean, lowJitterPxPerSec, highJitterPxPerSec);
        double confidence = Math.Clamp((double)count / minSummaries, 0.0, 1.0);
        return new SignalScore(score, confidence, count >= minSummaries);
    }

    /// <summary>
    /// Poor-tracking signal (design §13.2/§6.3): a linear ramp of the sample-count-weighted mean
    /// invalid-sample fraction. Its gate is <b>time-based</b> (the summaries must span a minimum
    /// duration), NOT interaction-count-based, so tracking loss — which produces no dwell outcomes — is
    /// still detected promptly. Feeds the poor-tracking mode (never a tightening response).
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window, oldest first.</param>
    /// <param name="minSpanMicroseconds">Confidence gate: minimum time span the summaries must cover. Default 2 000 000 (2 s).</param>
    /// <param name="lowInvalidFraction">Ramp start; at or below this invalid fraction the score is 0. Default 0.1.</param>
    /// <param name="highInvalidFraction">Ramp end; at or above this invalid fraction the score is 1. Default 0.5.</param>
    /// <returns>The poor-tracking signal score; <see cref="SignalScore.None"/> for an empty window.</returns>
    public static SignalScore ScorePoorTracking(
        IReadOnlyList<GazeQualitySummary> window,
        long minSpanMicroseconds = 2_000_000,
        double lowInvalidFraction = 0.1,
        double highInvalidFraction = 0.5)
    {
        int count = window.Count;
        if (count == 0)
            return SignalScore.None;

        double invalidSamples = 0.0;
        long totalSamples = 0;
        for (int i = 0; i < window.Count; i++)
        {
            GazeQualitySummary s = window[i];
            invalidSamples += s.InvalidFraction * s.SampleCount;
            totalSamples += s.SampleCount;
        }
        double meanInvalid = totalSamples > 0 ? invalidSamples / totalSamples : 0.0;

        long span = window[count - 1].TimestampMicroseconds - window[0].TimestampMicroseconds;
        double score = Ramp(meanInvalid, lowInvalidFraction, highInvalidFraction);
        double confidence = Math.Clamp((double)span / minSpanMicroseconds, 0.0, 1.0);
        return new SignalScore(score, confidence, span >= minSpanMicroseconds);
    }

    // Linear ramp: 0 at/below low, 1 at/above high, linear between. Guards a degenerate low >= high
    // (returns a hard step at high) so a mis-passed threshold pair can't divide by zero.
    private static double Ramp(double value, double low, double high)
    {
        if (high <= low)
            return value >= high ? 1.0 : 0.0;
        return Math.Clamp((value - low) / (high - low), 0.0, 1.0);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: PASS (25 tests — 15 from Task 2 + 10 gaze-quality). No `CS1591`.

- [ ] **Step 5: Full Core suite (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS. Everything is net-new and unreferenced, so all existing Core tests are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleSignals.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs
git commit -m "feat(adaptation): add jitter + poor-tracking signal scorers"
```

---

## Self-Review

**Spec coverage (this slice of Phase 1b-③):**
- §13.2 decomposition "1b-③a = `StruggleAssessment` + failure-mode/signal types + the four core signal scorers (Abandonment, Jitter, Poor-tracking, Accidental) as pure, testable functions over the evidence window" — Task 1 (`SignalScore`, `FailureMode`, `FailureModeAssessment`, `StruggleAssessment`) + Task 2 (Abandonment, Accidental) + Task 3 (Jitter, Poor-tracking). ✓
- §13.2 Abandonment "Σ(progress of high-progress abandons) ÷ total attempts", gate "≥ ~4 attempts" — `ScoreAbandonment` (`minAttempts=4`); high-progress threshold 0.5 discriminates near-complete abandons (detector emits abandons ≥ 0.3 per 1a-①) from barely-started ones. ✓
- §13.2 design call "slow/effortful folded into abandonment as a retry term" — `retryBoost × (retries ÷ attempts)`; retry = a strictly-later same-context outcome within 3 s. Deliberately reads §13.2's "reactivate" as re-engagement (activation **or** abandonment — a re-attempt that also fails is still effort evidence; documented + pinned by `Abandonment_FollowingSameContextAbandon_AlsoCountsAsRetry`, review round 1); `true time-to-activate not derivable` noted (DwellOutcome has no enter timestamp). ✓
- §13.2 Jitter "linear ramp between low/high px/s thresholds", gate "≥ ~3 summaries"; "uncalibrated starting guesses" flagged — `ScoreJitter` (mean `JitterStdDev`, ramp [50, 400] documented UNCALIBRATED, `minSummaries=3`). ✓
- §13.2 Poor-tracking "linear ramp ~0.1 → ~0.5", "**time-based** (≥ ~2 s), fires with 0 interactions (§6.3)" — `ScorePoorTracking` (sample-weighted mean `InvalidFraction`, ramp [0.1, 0.5], time-span gate `minSpanMicroseconds=2 000 000`; `TimeBasedGate_FiresWithZeroInteractions` proves interaction-independence). ✓
- §13.2/§13.1 Accidental "re-approach (another outcome ≤ 1.5 s within 2× radius, same context) OR keyboard key→Backspace", score "reversals ÷ activations", gate "≥ ~3 activations" — `ScoreAccidental` (W=1.5 s, R=2× radius with a 40 px floor for rect/radius-0 targets, same-context; opt-in `backspaceTargetId` corroboration; `minActivations=3`). ✓
- §13.2 output "`StruggleAssessment` = per-failure-mode `{ score, confidence, gatePassed, context? }` + aggregate struggle level" — `StruggleAssessment` (`Modes` of `FailureModeAssessment` + `AggregateLevel` + `ForMode`/`None`). Populated by the monitor (1b-③b). ✓
- §6.3 per-signal confidence gate (no single mixed gate) — each scorer returns its own `GatePassed`/`Confidence`; confidence ramps to 1 as evidence reaches that signal's gate. ✓
- **Deferred (documented, not gaps):** the `StruggleMonitor` (sink read, window filter, session/idle reset, monotonic guard, classification, aggregate) → 1b-③b; Aiming/Drift/Fatigue scorers → 1b-③b; consuming the assessment → 1b-④; threshold configurability → later tuning pass.

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `SignalScore(double Score, double Confidence, bool GatePassed)` + `SignalScore.None` are used identically in every scorer return and in `StruggleAssessmentTests`. `FailureModeAssessment(FailureMode, double, double, bool, DwellContext?)` positional order matches its construction in `StruggleAssessmentTests` and its consumption in `StruggleAssessment.ForMode`. `StruggleAssessment(IReadOnlyList<FailureModeAssessment>, double)` matches the test construction and `None`. The four scorer signatures (`ScoreAbandonment`, `ScoreAccidental`, `ScoreJitter`, `ScorePoorTracking`) match every call in `StruggleSignalsTests` (including the `backspaceTargetId:` named argument). `DwellOutcome`/`GazeQualitySummary` field reads (`Kind`, `ProgressAtEnd`, `Context`, `TimestampMicroseconds`, `TargetCenter`, `TargetRadius`, `TargetId`; `JitterStdDev`, `InvalidFraction`, `SampleCount`, `TimestampMicroseconds`) match the 1a-①/1a-④ contracts (pre-flight-confirmed). `PhysicalPoint.DistanceSquaredTo` matches source (`PhysicalPoint.cs:94`).

**Verified facts relied on (from source):**
- `PhysicalPoint` is `readonly record struct(double X, double Y)` with `double DistanceSquaredTo(PhysicalPoint)` (sqrt-free — ideal for the radius² re-approach compare) at `src/EyeCtrl.Core/Models/PhysicalPoint.cs:20,94`.
- `EyeCtrl.Core.csproj`: `ImplicitUsings=enable` (so `Math`/`Array`/`IReadOnlyList`/`List`/LINQ need no `using`), `GenerateDocumentationFile=true` + `WarningsAsErrors=CS1591` (every public member documented). `InternalsVisibleTo EyeCtrl.Core.Tests` (not relied on — types are public).
- `tests/Directory.Build.props` globally imports `Xunit` + `NFluent`; `EyeCtrl.Core.Tests.csproj` has `ImplicitUsings=enable` — so the test files add only `EyeCtrl.Core.Adaptation` (+ `EyeCtrl.Core.Models` for `PhysicalPoint`). NFluent's approximate-double check is `IsCloseTo(expected, delta)` (used across the suite, e.g. `MagicNumberReplacementTests.cs:49`).
- The 1a contracts (`DwellOutcome`, `OutcomeKind`, `AbandonReason`, `DwellContext`, `GazeQualitySummary`) are consumed as defined in plans 1a-① / 1a-④ — this plan is written against post-1a source with the pre-flight above.

**Honest testability limits:** every type and scorer is a pure, framework-agnostic Core function, fully headless-unit-tested (29 tests across the two files) over synthetic sequences — no display, sink, clock, or threading dependency. Boundary behaviour (empty → `None`, below-gate score-still-computed, ramp low/mid/high, sample-count weighting, radius floor, context/time-window exclusions, backspace opt-in) is asserted directly. What is **not** exercised here (correctly, per the scope boundary): the monitor's window filtering / session reset / monotonic-guard / classification (all 1b-③b), and end-to-end assessment production from a live sink (1b-③b + 1b-④). The numeric thresholds are §13.2 **starting values** (jitter/tracking px thresholds explicitly uncalibrated) — the tests assert the *structure* (monotonic ramps, gating, boundaries), not that any absolute number is field-calibrated.

**Deferred (documented above, not gaps):** the `StruggleMonitor` + the Aiming/Drift/Fatigue scorers (1b-③b); the `AdaptationController` consuming the assessment (1b-④); promoting detection thresholds to `AdaptationSettings` (later tuning pass).
