# Struggle Detection — Phase 1b-③c StruggleMonitor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the `StruggleMonitor` (Core) — the component that **reads** the 1a `BufferingTelemetrySink`, filters its snapshots to the momentary evidence window, runs the seven §13.2 signal scorers (1b-③a/③b), classifies them into per-`FailureMode` assessments, and produces a `StruggleAssessment` per controller tick via `Assess(now)`. **Zero behaviour change:** net-new, unreferenced Core type; nothing calls it until the `AdaptationCoordinator` (1b-⑤) ticks it. The controller (1b-④) consumes its output.

**Architecture:** `StruggleMonitor` holds a reference to the concrete `BufferingTelemetrySink` (reusing its bounded ring + monotonic guard — no DI rewiring, no reimplemented sink) and `EyeCareSettings` (read live only for the fatigue opt-in). `Assess(nowMicroseconds)` → `SelectEvidence` (filters both snapshots to `[max(sessionStart, now − 60 s, idle-boundary), now]`, order-preserving) → runs the 7 scorers → `AddMode` classification into `FailureModeAssessment`s (context = null; the Phase-1b momentary response is global per §7.2) → fatigue-amplified aggregate → `StruggleAssessment`. `ResetSession(now)` moves the boundary (app-start / resume-from-Pause); a gaze-idle gap > 120 s auto-advances it. Single-threaded: `Assess`/`ResetSession` run on the dispatcher thread, the same serialized thread the sink is fed on (§9), so no locking.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`, `IsCloseTo(expected, delta)` for division/amplification results).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.2** (`StruggleMonitor` architecture: reads the sink, `Assess(now) → StruggleAssessment`, evidence = snapshot filtered to `[max(sessionStart, now−60 s), now]`, `ResetSession` + idle-gap>120 s auto-reset, monotonic guard; the classification map signal→`FailureMode`; the seven signals; `StruggleAssessment` output), **§13.1** (60 s rolling max-age; session resets on app-start / resume-from-Pause / gaze-idle > 120 s), §9 (single serialized thread; the monitor consumes the sink's already-ordered stream), §7.2 (momentary layer is global per knob — hence context = null here), §6.2/§6.3 (fatigue is an opt-in amplifier; poor-tracking is time-gated).

**Depends on:** **Phase 1a + 1b-③a + 1b-③b implemented.** From 1b-③a/③b: `StruggleSignals` with the seven scorers, `SignalScore(double Score, double Confidence, bool GatePassed)` + `None`, `FailureMode` (5 modes), `FailureModeAssessment(FailureMode, double, double, bool, DwellContext?)`, `StruggleAssessment(IReadOnlyList<FailureModeAssessment>, double)` + `None` + `ForMode`. From 1a: `BufferingTelemetrySink.Snapshot()`/`SnapshotQuality()`, `DwellOutcome`, `GazeQualitySummary`. Pre-existing: `EyeCareSettings.FatigueMonitorEnabled`. Written against the **post-1a + post-1b-③a/③b** source. **No dependency on 1b-①/②/④** (no `EffectiveValue`/`AdaptationSettings`/controller).

**Executor pre-flight (do this FIRST — MANDATORY; 1a/1b-③a/③b are plans-only):** confirm against the *merged* source:
1. `StruggleSignals` (`public static class`, `EyeCtrl.Core.Adaptation`) exposes exactly: `ScoreAbandonment(IReadOnlyList<DwellOutcome>, …)`, `ScoreAccidental(IReadOnlyList<DwellOutcome>, …, string? backspaceTargetId = null)`, `ScoreJitter(IReadOnlyList<GazeQualitySummary>, …)`, `ScorePoorTracking(IReadOnlyList<GazeQualitySummary>, …)`, `ScoreAiming(IReadOnlyList<DwellOutcome>, …)`, `ScoreDrift(IReadOnlyList<DwellOutcome>, …)`, `ScoreFatigue(IReadOnlyList<GazeQualitySummary>, bool fatigueMonitorEnabled)` — each returning `SignalScore`. This plan calls them with defaults except `ScoreAccidental`'s `backspaceTargetId` and `ScoreFatigue`'s `fatigueMonitorEnabled`.
2. `SignalScore` fields `Score`/`Confidence`/`GatePassed` + `SignalScore.None`.
3. `FailureMode` members `SustainFailure`, `AccidentalActivation`, `Aiming`, `Drift`, `PoorTracking`.
4. `FailureModeAssessment(FailureMode Mode, double Score, double Confidence, bool GatePassed, DwellContext? Context)` and `StruggleAssessment(IReadOnlyList<FailureModeAssessment> modes, double aggregateLevel)` + static `None` + `FailureModeAssessment? ForMode(FailureMode)`.
5. `BufferingTelemetrySink` (`EyeCtrl.Core.Adaptation`) has `BufferingTelemetrySink(int capacity)` + `Report(in DwellOutcome)` + `IReadOnlyList<DwellOutcome> Snapshot()` (from **1a-②**) **and** `ReportGazeQuality(in GazeQualitySummary)` + `IReadOnlyList<GazeQualitySummary> SnapshotQuality()` (the **1a-④** extension — a second parallel buffer), all oldest-first / non-decreasing (per-stream monotonic guard). The quality methods are the load-bearing 1a-④ additions this monitor reads — confirm those exact names (`ReportGazeQuality`/`SnapshotQuality`), as this plan spans three upstream plans (1a-①/②/④).
6. `EyeCareSettings.FatigueMonitorEnabled` is a `bool` on the Core `EyeCtrl.Core.Settings.EyeCareSettings` (verified at `EyeCareSettings.cs:21`).

If any differ, adjust before proceeding.

**⚠️ Core enforces XML docs as build errors:** `WarningsAsErrors=CS1591`. Every public member (ctor + `ResetSession` + `Assess`, with all `<param>`/`<returns>`) is documented below. `ImplicitUsings=enable` covers `System` (`Math`, `Func`) and `System.Collections.Generic` (`List`, `IReadOnlyList`); the only explicit `using` needed is `EyeCtrl.Core.Settings` (for `EyeCareSettings`). `SelectEvidence` is `internal` (tested via `InternalsVisibleTo EyeCtrl.Core.Tests`); internal/private members don't need CS1591 docs but are commented. Do not strip the doc comments.

---

## Scope boundary (explicit)

This slice is the **monitor only** — a pure, headless-testable Core component. Deferred:
- **DI registration + ticking.** Nothing constructs or calls the monitor here; the `AdaptationCoordinator` (**1b-⑤**) registers it (singleton over the DI `BufferingTelemetrySink` + `EyeCtrlSettings.EyeCare`), ticks `Assess` on a dispatcher timer, calls `ResetSession` on app-start / resume-from-Pause, and gates ticking on `AdaptationSettings.Enabled` (so the monitor itself is unconditional — the enable-gate is upstream, §9.1).
- **Consuming the `StruggleAssessment`** (typed knob transforms, sub-band clamps) → the `AdaptationController`, **1b-④**.
- **Backspace-corroboration wiring.** `Assess` passes `_backspaceTargetId` (constructor-injected, default null) to `ScoreAccidental`; the App wires the actual keyboard Backspace id later. Until then the re-approach heuristic is the sole accidental signal (as designed — it's the primary, §13.1).
- **Per-context mode attribution.** All `FailureModeAssessment`s carry `Context = null`: the Phase-1b momentary response is **global per knob** (§7.2); per-context offsets are the Phase-2 learned layer (§5). The `Context?` field is populated then.
- **Threshold configurability.** The monitor's `fatigueAmplifyWeight` (and the scorers' internal thresholds) are code defaults / constructor params; promoting them to `AdaptationSettings` is a later tuning pass.

**Idle-gap note (honest):** with the default 60 s `evidenceMaxAgeMicroseconds`, the 120 s idle-gap auto-reset is **belt-and-suspenders** — a > 120 s gap cannot sit inside a 60 s window, so the max-age term already excludes pre-idle evidence. It is implemented anyway because §13.1 requires it, it is correct, and it becomes load-bearing if the max-age is ever raised. Its logic is tested **in isolation** (Task 1) by constructing the monitor with a large max-age so the window term doesn't mask it.

---

## File Structure

**Create (Core):** `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`.
**Test (create):** `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs`.

**Zero-behaviour-change guarantee:** one net-new Core class + its tests. Nothing references `StruggleMonitor`; the existing suite is untouched.

---

## Task 1: `StruggleMonitor` shell — construction, `ResetSession`, and evidence selection

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs` (helpers here are reused by Tasks 2–3):

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

public class StruggleMonitorTests
{
    private const long Sec = 1_000_000; // microseconds per second

    private static EyeCareSettings EyeCare(bool fatigue = false) => new() { FatigueMonitorEnabled = fatigue };

    private static BufferingTelemetrySink SinkWith(
        IEnumerable<DwellOutcome>? dwell = null,
        IEnumerable<GazeQualitySummary>? quality = null)
    {
        var sink = new BufferingTelemetrySink(capacity: 512);
        if (dwell is not null)
            foreach (var d in dwell) sink.Report(d);      // must be fed ascending per the monotonic guard
        if (quality is not null)
            foreach (var q in quality) sink.ReportGazeQuality(q);
        return sink;
    }

    // A controlled abandonment: TargetCenter=(0,0), GazePoint=(nmX,nmY) → NearMissX/Y == nmX/nmY.
    private static DwellOutcome Abandon(long ts, DwellContext ctx, double progress, double radius,
        double nmX = 0, double nmY = 0, string id = "t") => new(
            ts, ctx, id, OutcomeKind.Abandoned, AbandonReason.GazeLeft, progress,
            new PhysicalPoint(nmX, nmY), new PhysicalPoint(0, 0), radius, null);

    private static DwellOutcome Activate(long ts, DwellContext ctx, string id, double cx, double cy, double radius) =>
        new(ts, ctx, id, OutcomeKind.Activated, AbandonReason.None, 1.0,
            new PhysicalPoint(cx, cy), new PhysicalPoint(cx, cy), radius, null);

    private static GazeQualitySummary Quality(long ts, double invalid, double jitter, double fatigue = 0.0, int count = 30) =>
        new(ts, invalid, jitter, fatigue, count);

    [Fact]
    public void SelectEvidence_RollingMaxAge_ExcludesOlderThanWindow()
    {
        // Default 60 s window. now=100 s → window [40 s, 100 s]. Dwell at 10 s excluded, 90 s included.
        var sink = SinkWith(dwell: new[]
        {
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.5, 30),
            Abandon(90 * Sec, DwellContext.ActionMenu, 0.5, 30),
        });
        var monitor = new StruggleMonitor(sink, EyeCare());

        var (dwell, _) = monitor.SelectEvidence(100 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(90 * Sec);
    }

    [Fact]
    public void SelectEvidence_ResetSession_IgnoresPreBoundaryEvidence_EvenIfWithinWindow()
    {
        // Both dwell items are < 60 s old at now=100 s, but ResetSession(80 s) discards the 50 s one.
        var sink = SinkWith(dwell: new[]
        {
            Abandon(50 * Sec, DwellContext.ActionMenu, 0.5, 30),
            Abandon(90 * Sec, DwellContext.ActionMenu, 0.5, 30),
        });
        var monitor = new StruggleMonitor(sink, EyeCare());
        monitor.ResetSession(80 * Sec);

        var (dwell, _) = monitor.SelectEvidence(100 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(90 * Sec);
    }

    [Fact]
    public void SelectEvidence_IdleGap_ResetsBoundary_IsolatedFromWindow()
    {
        // Large max-age (600 s) so the rolling window does NOT do the exclusion — isolates the idle logic.
        // A 180 s gap in the quality stream (20 s → 200 s) is idle; pre-gap evidence (both streams) drops.
        var sink = SinkWith(
            dwell: new[]
            {
                Abandon(15 * Sec, DwellContext.ActionMenu, 0.5, 30),  // pre-gap → excluded
                Abandon(205 * Sec, DwellContext.ActionMenu, 0.5, 30), // post-gap → kept
            },
            quality: new[]
            {
                Quality(10 * Sec, 0.1, 5),
                Quality(20 * Sec, 0.1, 5),   // last before the gap
                Quality(200 * Sec, 0.1, 5),  // first after the 180 s gap → boundary
                Quality(210 * Sec, 0.1, 5),
            });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (dwell, quality) = monitor.SelectEvidence(250 * Sec);
        Check.That(dwell).HasSize(1);
        Check.That(dwell[0].TimestampMicroseconds).IsEqualTo(205 * Sec);
        Check.That(quality).HasSize(2);
        Check.That(quality[0].TimestampMicroseconds).IsEqualTo(200 * Sec);
    }

    [Fact]
    public void SelectEvidence_GapBelowIdleThreshold_KeepsAll()
    {
        // 100 s gap (< 120 s) is not idle; with a large window all summaries are kept.
        var sink = SinkWith(quality: new[]
        {
            Quality(0, 0.1, 5),
            Quality(100 * Sec, 0.1, 5),
        });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (_, quality) = monitor.SelectEvidence(150 * Sec);
        Check.That(quality).HasSize(2);
    }

    [Fact]
    public void SelectEvidence_ExcludesEvidenceAfterNow()
    {
        var sink = SinkWith(dwell: new[] { Abandon(200 * Sec, DwellContext.ActionMenu, 0.5, 30) });
        var monitor = new StruggleMonitor(sink, EyeCare(), evidenceMaxAgeMicroseconds: 600 * Sec);

        var (dwell, _) = monitor.SelectEvidence(100 * Sec); // evidence is in the future relative to now
        Check.That(dwell).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: FAIL to **compile** — `StruggleMonitor` does not exist.

- [ ] **Step 3: Create the monitor shell**

`src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Reads the 1a <see cref="BufferingTelemetrySink"/>, filters its snapshots to the momentary evidence
/// window, runs the seven §13.2 signal scorers (<see cref="StruggleSignals"/>), classifies them into
/// per-<see cref="FailureMode"/> assessments, and produces a <see cref="StruggleAssessment"/> per
/// controller tick (design §13.2). Reuses the sink's bounded ring + monotonic guard rather than
/// reimplementing a sink. Single-threaded: <c>Assess</c>/<see cref="ResetSession"/> run on the
/// dispatcher thread — the same serialized thread the sink is fed on (§9) — so no locking is needed.
/// </summary>
public sealed class StruggleMonitor
{
    private readonly BufferingTelemetrySink _sink;
    private readonly EyeCareSettings _eyeCare;
    private readonly long _evidenceMaxAgeMicroseconds;
    private readonly long _idleResetMicroseconds;
    private readonly string? _backspaceTargetId;
    private long _sessionStart = long.MinValue;

    /// <summary>Initializes the monitor over an existing telemetry sink.</summary>
    /// <param name="sink">The 1a buffering sink to read (dwell outcomes + gaze-quality summaries).</param>
    /// <param name="eyeCare">Eye-care settings; only <see cref="EyeCareSettings.FatigueMonitorEnabled"/> is read (live, in <c>Assess</c>), gating the fatigue amplifier.</param>
    /// <param name="evidenceMaxAgeMicroseconds">Rolling evidence-window length. Default 60 000 000 (60 s, §13.1).</param>
    /// <param name="idleResetMicroseconds">A gaze-idle gap this long (inferred from the gaze-quality stream) resets the session boundary. Default 120 000 000 (120 s, §13.1).</param>
    /// <param name="backspaceTargetId">Keyboard Backspace target id for the accidental-activation corroboration; null disables it (wired by the App later).</param>
    public StruggleMonitor(
        BufferingTelemetrySink sink,
        EyeCareSettings eyeCare,
        long evidenceMaxAgeMicroseconds = 60_000_000,
        long idleResetMicroseconds = 120_000_000,
        string? backspaceTargetId = null)
    {
        _sink = sink;
        _eyeCare = eyeCare; // read in Assess (Task 3 fatigue gate)
        _evidenceMaxAgeMicroseconds = evidenceMaxAgeMicroseconds;
        _idleResetMicroseconds = idleResetMicroseconds;
        _backspaceTargetId = backspaceTargetId; // read in Assess (Task 2)
    }

    /// <summary>
    /// Starts a fresh momentary session at <paramref name="nowMicroseconds"/>: evidence at or before it
    /// is ignored by the next <c>Assess</c> (even if younger than the rolling window). Invoked by
    /// the coordinator on app-start and on resume from an explicit Pause (§13.1). No buffer mutation —
    /// only the boundary moves.
    /// </summary>
    /// <param name="nowMicroseconds">The session-start timestamp, in microseconds.</param>
    public void ResetSession(long nowMicroseconds) => _sessionStart = nowMicroseconds;

    // Selects the evidence in [start, now], start = max(sessionStart, now − maxAge, idle-boundary). The
    // sink guarantees oldest-first, non-decreasing timestamps (§9); filtering preserves that order, so the
    // scorers' DEBUG AssertOrdered holds. No buffer mutation.
    internal (IReadOnlyList<DwellOutcome> Dwell, IReadOnlyList<GazeQualitySummary> Quality) SelectEvidence(long now)
    {
        IReadOnlyList<DwellOutcome> dwell = _sink.Snapshot();
        IReadOnlyList<GazeQualitySummary> quality = _sink.SnapshotQuality();

        long start = Math.Max(_sessionStart, now - _evidenceMaxAgeMicroseconds);

        // Idle-gap auto-reset (§13.1): a gap > idleReset in the continuous ~2 Hz gaze-quality stream marks
        // an idle period; evidence before it belongs to a prior session. Belt-and-suspenders under the
        // default 60 s max-age (a >120 s gap cannot fit a 60 s window) but spec-required and correct.
        long idleBoundary = LatestIdleBoundary(quality, now);
        if (idleBoundary > start)
            start = idleBoundary;

        return (Within(dwell, start, now, static o => o.TimestampMicroseconds),
                Within(quality, start, now, static q => q.TimestampMicroseconds));
    }

    // Timestamp of the summary immediately AFTER the latest idle gap (> idleReset) in the ordered stream,
    // or long.MinValue if none. Ordered ascending, so a later gap overwrites an earlier one.
    private long LatestIdleBoundary(IReadOnlyList<GazeQualitySummary> quality, long now)
    {
        long boundary = long.MinValue;
        long prev = long.MinValue;
        bool hasPrev = false;
        for (int i = 0; i < quality.Count; i++)
        {
            long ts = quality[i].TimestampMicroseconds;
            if (ts > now)
                break; // ordered → nothing later is ≤ now
            if (hasPrev && ts - prev > _idleResetMicroseconds)
                boundary = ts;
            prev = ts;
            hasPrev = true;
        }
        return boundary;
    }

    // Order-preserving filter to [start, now]. Ordered input → break once past now.
    private static IReadOnlyList<T> Within<T>(IReadOnlyList<T> items, long start, long now, Func<T, long> timestamp)
    {
        var result = new List<T>();
        for (int i = 0; i < items.Count; i++)
        {
            long ts = timestamp(items[i]);
            if (ts > now)
                break;
            if (ts >= start)
                result.Add(items[i]);
        }
        return result;
    }
}
```

(`_eyeCare` and `_backspaceTargetId` are assigned here but first *read* in Tasks 2–3 — reference-type fields, so no "assigned but unused" warning; `fatigueAmplifyWeight` is added to the ctor in Task 3, where it is used, to avoid a transient CS0414 on a value-type field.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: PASS (5 tests). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs
git commit -m "feat(adaptation): add StruggleMonitor shell (evidence-window selection + session reset)"
```

---

## Task 2: `Assess` — run the scorers + classify into per-mode assessments

Adds `Assess` (runs six scorers — fatigue is added in Task 3), the `AddMode` classifier, and a base aggregate (peak gated mode score; fatigue amplification is Task 3).

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `StruggleMonitorTests`:

```csharp
    // 4 near-complete abandons in one context, spaced > 3 s (no retries), tiny near-miss (ratio < 1):
    // gates Abandonment (score 0.8) → SustainFailure, WITHOUT gating Aiming (ratio 0.5) or Drift (1 ctx).
    private static DwellOutcome[] SustainFailureDwell() => new[]
    {
        Abandon(0 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(10 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(20 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        Abandon(30 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
    };

    [Fact]
    public void Assess_Empty_ReturnsNone()
    {
        var monitor = new StruggleMonitor(SinkWith(), EyeCare());
        var result = monitor.Assess(100 * Sec);
        Check.That(result.Modes).IsEmpty();
        Check.That(result.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void Assess_HighProgressAbandonment_ClassifiesSustainFailure()
    {
        var monitor = new StruggleMonitor(SinkWith(dwell: SustainFailureDwell()), EyeCare());
        var a = monitor.Assess(40 * Sec);

        var sustain = a.ForMode(FailureMode.SustainFailure);
        Check.That(sustain.HasValue).IsTrue();
        Check.That(sustain!.Value.GatePassed).IsTrue();
        Check.That(sustain!.Value.Score > 0).IsTrue();
        Check.That(sustain!.Value.Context.HasValue).IsFalse(); // global momentary response (§7.2)
        // Not misclassified as aiming (ratio 0.5) or drift (single context).
        Check.That(a.ForMode(FailureMode.Aiming).HasValue).IsFalse();
        Check.That(a.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }

    [Fact]
    public void Assess_JitterAlone_ContributesToSustainFailure()
    {
        // No dwell; only high-jitter quality (mean 500 ≥ high 400, ≥ 3 summaries) → jitter gates →
        // SustainFailure present (jitter is a sustain-failure feeder alongside abandonment).
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.0, 500), Quality(Sec, 0.0, 500), Quality(2 * Sec, 0.0, 500),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.SustainFailure)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_ReApproach_ClassifiesAccidentalActivation()
    {
        // 3 activations; the first (center 100,100) is reversed by a same-context follow-up whose CENTER
        // (120,100) is within 2× radius (60 px) and within 1.5 s. The follow-up is built inline so its
        // TargetCenter is controlled — the Abandon helper fixes center at (0,0), which wouldn't re-approach.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Activate(0, DwellContext.ActionMenu, "A", 100, 100, 30),
            new DwellOutcome(500_000, DwellContext.ActionMenu, "A2", OutcomeKind.Abandoned,
                AbandonReason.GazeLeft, 0.5, new PhysicalPoint(120, 100), new PhysicalPoint(120, 100), 30, null),
            Activate(10 * Sec, DwellContext.Toolbar, "B", 500, 500, 30),
            Activate(20 * Sec, DwellContext.Toolbar, "C", 500, 500, 30),
        }), EyeCare());
        var a = monitor.Assess(21 * Sec);

        Check.That(a.ForMode(FailureMode.AccidentalActivation)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_TimeBasedPoorTracking_ClassifiesPoorTracking_WithZeroInteractions()
    {
        // No dwell at all; quality with high invalid fraction over ≥ 2 s → poor-tracking gates on time.
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.6, 0), Quality(Sec, 0.6, 0), Quality(2 * Sec, 0.6, 0),
        }), EyeCare());
        var a = monitor.Assess(3 * Sec);

        Check.That(a.ForMode(FailureMode.PoorTracking)!.Value.GatePassed).IsTrue();
    }

    [Fact]
    public void Assess_HighNearMissLowProgress_ClassifiesAimingOnly()
    {
        // 4 abandons, ratio 3 (nmX 90 / radius 30) but progress 0.35 (< 0.5 high-progress) and spaced
        // > 3 s (no retries): gates Aiming, NOT SustainFailure (abandonment score 0). Single context and
        // < 5 abandons → not Drift.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Abandon(0 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(20 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            Abandon(30 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
        }), EyeCare());
        var a = monitor.Assess(40 * Sec);

        Check.That(a.ForMode(FailureMode.Aiming)!.Value.GatePassed).IsTrue();
        Check.That(a.ForMode(FailureMode.SustainFailure).HasValue).IsFalse();
        Check.That(a.ForMode(FailureMode.Drift).HasValue).IsFalse();
    }

    [Fact]
    public void Assess_BelowGateStruggle_IsSurfacedButNotGated()
    {
        // 2 high-progress abandons (< 4-attempt gate) → SustainFailure present with score > 0, gate false.
        var monitor = new StruggleMonitor(SinkWith(dwell: new[]
        {
            Abandon(0 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
            Abandon(10 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
        }), EyeCare());
        var a = monitor.Assess(20 * Sec);

        var sustain = a.ForMode(FailureMode.SustainFailure);
        Check.That(sustain.HasValue).IsTrue();
        Check.That(sustain!.Value.Score > 0).IsTrue();
        Check.That(sustain!.Value.GatePassed).IsFalse();
    }

    [Fact]
    public void Assess_AggregateIsPeakGatedModeScore()
    {
        // Aiming (score 1.0, gated) + poor-tracking (invalid 0.3 → score 0.5, gated). Base aggregate =
        // max gated score = 1.0. (Fatigue disabled → no amplification; stable across Task 3.)
        var monitor = new StruggleMonitor(SinkWith(
            dwell: new[]
            {
                Abandon(0 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(10 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(20 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
                Abandon(30 * Sec, DwellContext.ActionMenu, 0.35, 30, nmX: 90),
            },
            quality: new[] { Quality(0, 0.3, 0), Quality(Sec, 0.3, 0), Quality(2 * Sec, 0.3, 0) }),
            EyeCare());
        var a = monitor.Assess(40 * Sec);

        Check.That(a.ForMode(FailureMode.Aiming).HasValue).IsTrue();
        Check.That(a.ForMode(FailureMode.PoorTracking).HasValue).IsTrue();
        Check.That(a.AggregateLevel).IsEqualTo(1.0);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: FAIL to **compile** — `Assess` does not exist.

- [ ] **Step 3: Add `Assess` + `AddMode`**

In `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`, add these members to the class (e.g. after `ResetSession`):

```csharp
    /// <summary>
    /// Assesses momentary struggle as of <paramref name="nowMicroseconds"/>: selects the in-window
    /// evidence, runs the signal scorers, classifies them into per-failure-mode assessments (context =
    /// null — the Phase-1b momentary response is global per knob, §7.2), and computes the aggregate level.
    /// </summary>
    /// <param name="nowMicroseconds">The current controller-tick timestamp, in microseconds.</param>
    /// <returns>The struggle assessment for this tick, or <see cref="StruggleAssessment.None"/> when no mode shows struggle.</returns>
    public StruggleAssessment Assess(long nowMicroseconds)
    {
        var (dwell, quality) = SelectEvidence(nowMicroseconds);

        SignalScore abandonment = StruggleSignals.ScoreAbandonment(dwell);
        SignalScore accidental = StruggleSignals.ScoreAccidental(dwell, backspaceTargetId: _backspaceTargetId);
        SignalScore jitter = StruggleSignals.ScoreJitter(quality);
        SignalScore poorTracking = StruggleSignals.ScorePoorTracking(quality);
        SignalScore aiming = StruggleSignals.ScoreAiming(dwell);
        SignalScore drift = StruggleSignals.ScoreDrift(dwell);

        var modes = new List<FailureModeAssessment>(5);
        AddMode(modes, FailureMode.SustainFailure, abandonment, jitter); // §13.2: abandonment(+retries, folded in ③a) + jitter
        AddMode(modes, FailureMode.AccidentalActivation, accidental);
        AddMode(modes, FailureMode.Aiming, aiming);
        AddMode(modes, FailureMode.Drift, drift);
        AddMode(modes, FailureMode.PoorTracking, poorTracking);

        if (modes.Count == 0)
            return StruggleAssessment.None;

        double aggregate = PeakGatedScore(modes);
        return new StruggleAssessment(modes, aggregate);
    }

    // Adds a per-mode assessment when any feeding signal shows struggle (score > 0). Score = PEAK feeder
    // score (not a sum: two correlated feeders — e.g. abandonment + jitter both signalling sustain-failure
    // — must not double-count; the dominant signal drives the mode); GatePassed = any feeder gated;
    // Confidence = peak feeder confidence. Context is null (the Phase-1b momentary response is global per
    // knob, §7.2). A mode with score 0 is omitted, so StruggleAssessment.ForMode returns null for it.
    private static void AddMode(List<FailureModeAssessment> modes, FailureMode mode, params SignalScore[] feeders)
    {
        double score = 0.0, confidence = 0.0;
        bool gate = false;
        foreach (SignalScore s in feeders)
        {
            score = Math.Max(score, s.Score);
            confidence = Math.Max(confidence, s.Confidence);
            gate = gate || s.GatePassed;
        }
        if (score > 0.0)
            modes.Add(new FailureModeAssessment(mode, score, confidence, gate, null));
    }

    // Peak struggle among GATED modes (the actionable ones). Uses gated SCORES (not confidences), so a
    // single biased surface cannot inflate it — Drift scores 0 unless genuinely cross-context-coherent
    // (F4, §6.2/C2). Base for the (Task-3) fatigue-amplified aggregate.
    private static double PeakGatedScore(List<FailureModeAssessment> modes)
    {
        double peak = 0.0;
        foreach (FailureModeAssessment m in modes)
            if (m.GatePassed)
                peak = Math.Max(peak, m.Score);
        return peak;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: PASS (13 tests — 5 from Task 1 + 8 classification). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs
git commit -m "feat(adaptation): StruggleMonitor.Assess runs scorers + classifies failure modes"
```

---

## Task 3: Fatigue amplification of the aggregate level

Adds the seventh scorer (`ScoreFatigue`, opt-in) and amplifies the aggregate by the windowed fatigue level.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `StruggleMonitorTests`:

```csharp
    // A gated poor-tracking mode at score 0.5 (invalid 0.3 → ramp midpoint), plus a fatigue level, over
    // ≥ 2 s. Base aggregate 0.5; fatigue amplification = 0.5 · (1 + 0.5 · fatigue).
    private static GazeQualitySummary[] PoorTrackingWithFatigue(double fatigue) => new[]
    {
        Quality(0, 0.3, 0, fatigue), Quality(Sec, 0.3, 0, fatigue), Quality(2 * Sec, 0.3, 0, fatigue),
    };

    [Fact]
    public void Assess_FatigueDisabled_DoesNotAmplify()
    {
        var monitor = new StruggleMonitor(SinkWith(quality: PoorTrackingWithFatigue(0.8)), EyeCare(fatigue: false));
        Check.That(monitor.Assess(3 * Sec).AggregateLevel).IsCloseTo(0.5, 1e-9); // base only
    }

    [Fact]
    public void Assess_FatigueEnabled_AmplifiesAggregate()
    {
        var monitor = new StruggleMonitor(SinkWith(quality: PoorTrackingWithFatigue(0.8)), EyeCare(fatigue: true));
        // 0.5 · (1 + 0.5 · 0.8) = 0.5 · 1.4 = 0.70.
        Check.That(monitor.Assess(3 * Sec).AggregateLevel).IsCloseTo(0.70, 1e-9);
    }

    [Fact]
    public void Assess_FatigueWithoutStruggle_DoesNotCreateAggregate()
    {
        // High fatigue but no struggle mode (invalid 0.0, jitter 0) → amplifier can't create struggle → None.
        var monitor = new StruggleMonitor(SinkWith(quality: new[]
        {
            Quality(0, 0.0, 0, 0.9), Quality(Sec, 0.0, 0, 0.9), Quality(2 * Sec, 0.0, 0, 0.9),
        }), EyeCare(fatigue: true));
        var result = monitor.Assess(3 * Sec);
        Check.That(result.Modes).IsEmpty();
        Check.That(result.AggregateLevel).IsEqualTo(0.0);
    }

    [Fact]
    public void Assess_FatigueAmplification_ClampsToOne()
    {
        // Base 0.5 with a large amplify weight would exceed 1 → clamped.
        var monitor = new StruggleMonitor(SinkWith(quality: PoorTrackingWithFatigue(1.0)),
            EyeCare(fatigue: true), fatigueAmplifyWeight: 5.0);
        Check.That(monitor.Assess(3 * Sec).AggregateLevel).IsEqualTo(1.0);
    }

    [Fact]
    public void Assess_FatigueDoesNotAmplifyBelowGateStruggle()
    {
        // Below-gate struggle (2 high-progress abandons < the 4-attempt gate → SustainFailure surfaced,
        // score > 0, gate FALSE) plus high fatigue. Fatigue amplifies only the GATED base (PeakGatedScore
        // = 0 here), so the aggregate stays 0 even though a struggle mode is present — the amplifier
        // cannot turn ungated evidence into an actionable aggregate.
        var monitor = new StruggleMonitor(SinkWith(
            dwell: new[]
            {
                Abandon(0 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
                Abandon(10 * Sec, DwellContext.ActionMenu, 0.8, 30, nmX: 15),
            },
            quality: new[] { Quality(0, 0.0, 0, 0.9), Quality(Sec, 0.0, 0, 0.9), Quality(2 * Sec, 0.0, 0, 0.9) }),
            EyeCare(fatigue: true));
        var a = monitor.Assess(20 * Sec);

        var sustain = a.ForMode(FailureMode.SustainFailure);
        Check.That(sustain.HasValue).IsTrue();
        Check.That(sustain!.Value.GatePassed).IsFalse();
        Check.That(a.AggregateLevel).IsEqualTo(0.0);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: FAIL to **compile** — the `fatigueAmplifyWeight` constructor argument does not exist yet.

- [ ] **Step 3: Add the `fatigueAmplifyWeight` ctor param + fatigue amplification**

In `src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs`, add the field next to `_backspaceTargetId`:

```csharp
    private readonly string? _backspaceTargetId;
    private readonly double _fatigueAmplifyWeight;
```

Extend the constructor — append the optional parameter (existing calls that omit it are unaffected) + document it + assign the field. Change the signature's last line and the body:

```csharp
        long idleResetMicroseconds = 120_000_000,
        string? backspaceTargetId = null)
    {
```

to (append the parameter + its `<param>` doc above the ctor, and assign it):

```csharp
        long idleResetMicroseconds = 120_000_000,
        string? backspaceTargetId = null,
        double fatigueAmplifyWeight = 0.5)
    {
```

Add this `<param>` line to the constructor's doc block (after the `backspaceTargetId` line):

```csharp
    /// <param name="fatigueAmplifyWeight">How strongly the fatigue level amplifies the aggregate (multiplicative). Default 0.5 — a tunable starting value.</param>
```

And add the field assignment in the ctor body (after `_backspaceTargetId = backspaceTargetId;`):

```csharp
        _fatigueAmplifyWeight = fatigueAmplifyWeight;
```

Then, in `Assess`, add the fatigue scorer and replace the aggregate computation. Change:

```csharp
        SignalScore aiming = StruggleSignals.ScoreAiming(dwell);
        SignalScore drift = StruggleSignals.ScoreDrift(dwell);
```

to (add the seventh scorer):

```csharp
        SignalScore aiming = StruggleSignals.ScoreAiming(dwell);
        SignalScore drift = StruggleSignals.ScoreDrift(dwell);
        SignalScore fatigue = StruggleSignals.ScoreFatigue(quality, _eyeCare.FatigueMonitorEnabled);
```

and change:

```csharp
        double aggregate = PeakGatedScore(modes);
        return new StruggleAssessment(modes, aggregate);
```

to (amplify by fatigue — multiplicative, so fatigue with no struggle stays 0; clamped to [0,1]):

```csharp
        // Fatigue is a pass-through AMPLIFIER (§13.2), not a failure mode: it scales the actionable
        // struggle level but cannot create struggle from nothing (base 0 → 0). Opt-in via EyeCare.
        double baseLevel = PeakGatedScore(modes);
        double aggregate = Math.Clamp(baseLevel * (1.0 + _fatigueAmplifyWeight * fatigue.Score), 0.0, 1.0);
        return new StruggleAssessment(modes, aggregate);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleMonitorTests"`
Expected: PASS (18 tests — 13 + 5 fatigue). The Task-2 `Assess_AggregateIsPeakGatedModeScore` and `Assess_FatigueDisabled_DoesNotAmplify` still pass (fatigue disabled ⇒ `fatigue.Score` 0 ⇒ `base · 1`). No `CS1591`.

- [ ] **Step 5: Full Core suite (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS. `StruggleMonitor` is net-new and unreferenced; the existing suite (all scorer + assessment tests) is unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleMonitor.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleMonitorTests.cs
git commit -m "feat(adaptation): amplify StruggleMonitor aggregate by opt-in fatigue level"
```

---

## Self-Review

**Spec coverage (this slice):**
- §13.2 architecture — `StruggleMonitor` **reads** `BufferingTelemetrySink` (`Snapshot`/`SnapshotQuality`), `Assess(now) → StruggleAssessment` per tick, evidence filtered to `[max(sessionStart, now−60 s), now]`. ✓
- §13.1/§13.2 session boundaries — `ResetSession(now)` (app-start / resume-from-Pause; ignores even sub-window pre-reset evidence, tested) + gaze-idle-gap > 120 s auto-reset (inferred from the quality stream; tested in isolation with a large max-age) + 60 s rolling max-age (tested). ✓
- §13.2 classification map — `AddMode`: SustainFailure ← Abandonment + Jitter; AccidentalActivation ← Accidental; Aiming ← Aiming; Drift ← Drift; PoorTracking ← PoorTracking. Each tested (incl. jitter-alone → SustainFailure; poor-tracking with zero interactions). ✓
- §13.2 output — `StruggleAssessment` = per-mode `{score, confidence, gatePassed, context?}` + aggregate. Score/gate independent (below-gate struggle surfaced with gate false, tested). ✓
- §7.2 momentary layer is global — all modes `Context = null` (per-context = Phase-2 learned layer, §5). ✓
- §13.2/§6.2 fatigue amplifier — `ScoreFatigue` opt-in pass-through; multiplicatively amplifies the aggregate (base 0 → 0; disabled → no change; clamped). No `FailureMode`. Tested. ✓
- §9 single serialized thread — documented; no locking; the monitor consumes the sink's already-ordered stream and preserves order when filtering. ✓
- **F4 caveat honored (from 1b-③b review round 2):** the aggregate uses gated **scores**, not confidences, and Drift scores 0 unless genuinely cross-context-coherent — so a single biased surface (UI geometry, C2) cannot inflate the aggregate. Documented at `PeakGatedScore`. ✓
- **Deferred (documented, not gaps):** DI registration + ticking + enable-gating (1b-⑤); consuming the assessment (1b-④); backspace-id wiring; per-context attribution (Phase 2); threshold→settings promotion.

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `Assess`/`ResetSession`/ctor signatures match the tests' calls (including the Task-3 `fatigueAmplifyWeight:` and `evidenceMaxAgeMicroseconds:` named args). The seven `StruggleSignals` calls match the ③a/③b signatures (pre-flight-confirmed); `ScoreAccidental(dwell, backspaceTargetId: _backspaceTargetId)` and `ScoreFatigue(quality, _eyeCare.FatigueMonitorEnabled)` match. `FailureModeAssessment(mode, score, confidence, gate, null)` and `new StruggleAssessment(modes, aggregate)` match the 1b-③a types; `StruggleAssessment.None`/`ForMode` used as defined. `SelectEvidence` returns the named tuple the Task-1 tests destructure. Test helpers build `DwellOutcome`/`GazeQualitySummary`/`EyeCareSettings`/`BufferingTelemetrySink` in the exact 1a/1a-④ shapes.

**Verified facts relied on (from source):**
- `EyeCareSettings.FatigueMonitorEnabled` is a Core `bool` (`EyeCareSettings.cs:21`), constructable via `new EyeCareSettings { FatigueMonitorEnabled = … }` (matches the benchmark usages).
- `BufferingTelemetrySink(int capacity)` + `Report`/`ReportGazeQuality` + `Snapshot`/`SnapshotQuality` returning oldest-first `IReadOnlyList<…>` (1a-②/1a-④); its per-stream monotonic guard means the monitor never sees out-of-order evidence, so `LatestIdleBoundary`/`Within`'s ordered-scan `break` is sound.
- `EyeCtrl.Core.csproj` `ImplicitUsings=enable` (Math/Func/List/IReadOnlyList need no using) + `WarningsAsErrors=CS1591`; `InternalsVisibleTo EyeCtrl.Core.Tests` makes `internal SelectEvidence` test-visible. NFluent `IsCloseTo(expected, delta)` for amplification results; the no-struggle path is asserted behaviourally (`Modes` empty + `AggregateLevel` 0), not by reference identity.

**Performance (design §9/§9.1):** `Assess` runs at the controller tick (a few Hz, dispatcher), not the gaze hot path. Per tick it takes two `Snapshot` copies + two `Within` filter passes (each allocating a small `List<>`) + seven scorer calls over the ≤ 60 s window (n ≈ tens); the scorers' own costs (O(n) / bounded O(n²)) dominate and are all trivial at this size. The idle scan is a single O(n) pass over the quality snapshot. These per-tick allocations are negligible and off the gaze hot path — §9.1's zero-per-sample-allocation budget governs the per-gaze-sample path (1a's aggregation), not this few-Hz tick; there is no per-gaze-sample work here.

**Note for 1b-⑤ (idle detection is quality-stream-only):** `LatestIdleBoundary` infers idle solely from gaps in the ~2 Hz `GazeQualitySummary` stream (the continuous gaze-activity signal; dwell outcomes are too sparse to indicate idle and §13.1 says "gaze-idle"). Correct — but it means if quality emission ever stops while dwell outcomes still flow (e.g. a provider disconnect that pauses the gaze-quality aggregator but not dwell), the idle auto-reset goes blind. The coordinator (1b-⑤) should call `ResetSession` explicitly on such provider-state transitions rather than relying on the timestamp-gap inference.

**Honest testability limits:** the monitor is a pure, framework-agnostic Core component, fully headless-unit-tested (18 tests) by pre-loading a real `BufferingTelemetrySink` and calling `SelectEvidence`/`Assess` single-threaded — no dispatcher, timer, or DI. `SelectEvidence` is tested directly (internal) for the window/reset/idle/future-guard paths; classification and aggregation are tested through `Assess`. The idle-gap reset is verified in isolation (large max-age) since it is otherwise subsumed by the 60 s window (documented). **Not** exercised here (correctly, per scope): the coordinator's real-time ticking / `ResetSession` triggers / enable-gating (1b-⑤), and the controller's consumption (1b-④). The `fatigueAmplifyWeight` (0.5) is a tunable starting value; tests assert the amplification *structure* (multiplicative, base-preserving when disabled, clamped), not a calibrated constant.

**Deferred (documented above, not gaps):** DI + ticking + enable-gate (1b-⑤); assessment consumption (1b-④); backspace-id + per-context attribution; threshold→settings promotion.
