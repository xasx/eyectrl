# Struggle Detection — Phase 1b-③b Remaining Signal Scorers (Aiming / Drift / Fatigue) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the three remaining struggle-signal scorers from spec §13.2 — **Aiming** (large near-miss), **Drift** (cross-context ±30°-coherent near-miss bias, the §13.1/C2 discrimination), and **Fatigue** (opt-in pass-through amplifier) — as further **pure, stateless functions** on the existing `StruggleSignals` class. **Zero behaviour change:** net-new, unreferenced functions; nothing consumes them until the `StruggleMonitor` (plan 1b-③c) lands.

**Architecture:** These complete the seven §13.2 signals. Like the 1b-③a four, each is a pure function over an already-windowed list — Aiming/Drift over `IReadOnlyList<DwellOutcome>`, Fatigue over `IReadOnlyList<GazeQualitySummary>` — returning a `SignalScore`. All three are **order-independent** (means / per-context grouping / coherence clustering — no early-`break` or span), so none needs the `AssertOrdered` tripwire. Drift is the one non-trivial algorithm: group abandons by context, take each context's mean `NearMiss` vector, and fire only when ≥ 2 contexts (each with ≥ 5 abandons) share a direction within ±30° — a single-context bias is UI geometry, **not** calibration drift (§6.2/C2).

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`, `IsCloseTo(expected, delta)` for division results).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§13.2** (signal table rows for Aiming / Drift / Fatigue; "Fatigue … pass-through amplifier … opt-in"; the 1b-③a/③b decomposition), **§13.1** (drift: coherent `NearMiss` bias within ≈ ±30° across ≥ 2 contexts with ≥ 5 abandons each; single-context bias = UI geometry), §6.2 (fatigue reused from `FatigueMonitor.Score`, opt-in via `EyeCareSettings.FatigueMonitorEnabled`; drift evaluated across contexts, C2), §7.1 (aiming → enlarge geometry; drift → loosen tolerance + report).

**Depends on:** **Phase 1a AND Phase 1b-③a implemented.** From 1b-③a: `StruggleSignals` (static class), `SignalScore(double Score, double Confidence, bool GatePassed)` + `SignalScore.None`, and the `private static double Ramp(double value, double low, double high)` helper (reused by Aiming). From 1a: `DwellOutcome` (with `NearMissX`/`NearMissY` computed properties, `TargetRadius`, `Context`, `Kind`) and `GazeQualitySummary` (with `FatigueScore`). Also the pre-existing Core setting `EyeCareSettings.FatigueMonitorEnabled` (referenced only in docs; the scorer takes a `bool`). Written against the **post-1a + post-1b-③a** source. **No dependency on 1b-①/②** and **no dependency on the `StruggleMonitor`** (that is 1b-③c).

**Executor pre-flight (do this FIRST — MANDATORY; 1a and 1b-③a are plans-only, not yet in the tree):** confirm against the *merged* 1a + 1b-③a source:
1. `StruggleSignals` is a `public static class` in `EyeCtrl.Core.Adaptation` (from 1b-③a) with the four scorers already present, and a `private static double Ramp(double value, double low, double high)` helper (1b-③a Task 3). This plan **adds methods to that class** and **reuses `Ramp`** in `ScoreAiming`; if 1b-③a named the helper differently, adjust the `ScoreAiming` call.
2. `SignalScore` is `readonly record struct(double Score, double Confidence, bool GatePassed)` with a static `None` (1b-③a).
3. `DwellOutcome` exposes `double NearMissX`/`double NearMissY` (computed = `GazePoint − TargetCenter`), `double TargetRadius`, `DwellContext Context`, `OutcomeKind Kind` (1a-① §6.1).
4. `GazeQualitySummary` exposes `double FatigueScore` (1a-④).
5. `EyeCareSettings.FatigueMonitorEnabled` is a `bool` on the Core `EyeCareSettings` (verified pre-existing at `src/EyeCtrl.Core/Settings/EyeCareSettings.cs`).
6. `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs` exists (1b-③a) with helpers `Dwell`/`Activate`/`Abandon`/`Quality` and `const long Sec = 1_000_000` — this plan **appends** helpers + tests to it.

If any differ, adjust before proceeding.

**⚠️ Core enforces XML docs as build errors:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every public method's `<summary>`/`<param>`/`<returns>` is documented below. `ImplicitUsings=enable` covers `System` (`Math`) and `System.Collections.Generic` (`Dictionary`, `List`, `IReadOnlyList`); the three new scorers reference only same-namespace (`EyeCtrl.Core.Adaptation`) types, so **no new `using` is required** (`StruggleSignals.cs` already has `using System.Diagnostics;` from 1b-③a, unused by these three). Do not strip the doc comments.

---

## Scope boundary (explicit)

**Slice decision (per §13.2 "1b-③b … may split further if large"):** the original 1b-③b scope was "Aiming/Drift/Fatigue **+ the `StruggleMonitor`**". The Drift scorer is a non-trivial cross-context clustering algorithm, and the `StruggleMonitor` is a stateful component (sink read + 60 s window filter + session/idle-gap reset + monotonic guard + classification + aggregate). Bundling them would produce one oversized plan, so this slice is **the three pure scorers only**; the `StruggleMonitor` is **1b-③c**.

Deferred:
- **The `StruggleMonitor`** (reads the 1a `BufferingTelemetrySink`; filters to `[max(sessionStart, now−60 s), now]`; `ResetSession`/idle-gap reset; monotonic guard; **classification** of the seven signal scores → `FailureMode`s with context attribution; the **aggregate struggle level**; **how the Fatigue score amplifies** the assessment; `Assess(now) → StruggleAssessment`) → **1b-③c**.
- **Consuming the assessment** (typed knob transforms) → 1b-④.
- **Fatigue amplification semantics.** `ScoreFatigue` here only *passes through* the windowed fatigue level behind the opt-in gate; Fatigue has **no** `FailureMode` of its own (the enum's five modes are unchanged) — it is an amplifier the monitor (1b-③c) applies to the aggregate / other modes. Defining exactly how it amplifies is 1b-③c.
- **Detection-threshold configurability.** As in 1b-③a, thresholds are optional parameters defaulting to §13.2/§13.1 values; the Aiming ramp bounds and the Drift `magnitudeNormalizerPx` are **uncalibrated starting guesses** (flagged), while the Drift **±30° / ≥2 contexts / ≥5 abandons** gates are the spec's stated §13.1 numbers.
- **Rich drift reporting** (the exact bias direction/vector for the §7.1 "report" response) → surfaced later if the coordinator needs it; this scorer yields only the drift *score/confidence/gate*, enough for the monitor to classify the Drift mode.

---

## File Structure

**Modify (Core):** `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs` — append `ScoreAiming`, `ScoreDrift`, `ScoreFatigue`.
**Test (modify):** `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs` — append helpers (`AbandonNM`, `QualityFat`) + the new tests.

**Zero-behaviour-change guarantee:** only additive public static methods on an existing class + additive tests. Nothing references the new scorers yet; the whole existing suite is unaffected.

---

## Task 1: `ScoreAiming` (large near-miss on abandons)

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `StruggleSignalsTests` (add the shared `AbandonNM` helper — also used by Task 2 — plus the aiming tests):

```csharp
    // An abandonment with a controlled NearMiss vector: TargetCenter=(0,0), GazePoint=(nmX,nmY),
    // so NearMissX/Y == nmX/nmY. radius feeds the aiming |NearMiss|÷radius ratio (0 = rect target).
    // Shared by the Aiming and Drift tests.
    private static DwellOutcome AbandonNM(
        long ts, DwellContext ctx, double radius, double nmX, double nmY, string id = "t") => new(
            ts, ctx, id, OutcomeKind.Abandoned, AbandonReason.GazeLeft, 0.5,
            new PhysicalPoint(nmX, nmY), new PhysicalPoint(0, 0), radius, null);

    // ---- Aiming ----

    [Fact]
    public void Aiming_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreAiming(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Aiming_NoCircularAbandons_ReturnsNone()
    {
        // Only activations + a rect (radius 0) abandon → nothing usable → None.
        var window = new List<DwellOutcome>
        {
            Activate(0),
            AbandonNM(10 * Sec, DwellContext.ActionMenu, radius: 0, nmX: 90, nmY: 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Aiming_MeanRatioBelowLow_ScoreZero()
    {
        // radius 30, |NearMiss| 15 → ratio 0.5 (< low 1.0) → score 0.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 15, 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Aiming_MeanRatioAboveHigh_ScoreOne()
    {
        // radius 30, |NearMiss| 90 → ratio 3.0 (> high 2.5) → score 1.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 90, 0),
        };
        Check.That(StruggleSignals.ScoreAiming(window).Score).IsEqualTo(1.0);
    }

    [Fact]
    public void Aiming_MidRamp_IsLinear()
    {
        // radius 40, |NearMiss| 70 → ratio 1.75, midpoint of the [1.0, 2.5] ramp → 0.5.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 40, 70, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 40, 70, 0),
        };
        Check.That(StruggleSignals.ScoreAiming(window).Score).IsCloseTo(0.5, 1e-9);
    }

    [Fact]
    public void Aiming_RectAndActivationsExcluded_OnlyCircularAbandonsCount()
    {
        // 4 circular abandons at ratio 0.5 → mean 0.5 → score 0. Two poison entries that must be ignored,
        // each chosen so INCLUSION would change the score (so asserting 0.0 discriminates BOTH exclusions):
        //   • an ACTIVATION carrying a large near-miss (150 px ÷ radius 30 = ratio 5) → if counted, mean
        //     rises to (2.0 + 5.0)/5 = 1.4 → score ≈ 0.27. Excluded by the Kind guard.
        //   • a RECT abandon (radius 0) → if counted, |NearMiss| ÷ 0 = +∞ drives the mean → score 1.0.
        //     Excluded by the radius > 0 guard.
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(2 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            AbandonNM(3 * Sec, DwellContext.ActionMenu, 30, 15, 0),
            new DwellOutcome(4 * Sec, DwellContext.ActionMenu, "act", OutcomeKind.Activated,
                AbandonReason.None, 1.0, new PhysicalPoint(150, 0), new PhysicalPoint(0, 0), 30, null),
            AbandonNM(5 * Sec, DwellContext.ActionMenu, radius: 0, nmX: 500, nmY: 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsTrue(); // exactly 4 circular abandons meet the gate
    }

    [Fact]
    public void Aiming_BelowGate_ScoreStillComputed_GateFalse()
    {
        // 2 circular abandons (< default 4) → gate false, confidence 0.5, score still computed (ratio 3 → 1).
        var window = new List<DwellOutcome>
        {
            AbandonNM(0, DwellContext.ActionMenu, 30, 90, 0),
            AbandonNM(1 * Sec, DwellContext.ActionMenu, 30, 90, 0),
        };
        var s = StruggleSignals.ScoreAiming(window);
        Check.That(s.Score).IsEqualTo(1.0);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsFalse();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: FAIL to **compile** — `ScoreAiming` does not exist.

- [ ] **Step 3: Add `ScoreAiming`**

In `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`, add this method inside the `StruggleSignals` class (e.g. after `ScorePoorTracking`):

```csharp
    /// <summary>
    /// Aiming signal (design §13.2/§7.1): a linear ramp of the mean near-miss ratio
    /// <c>|NearMiss| ÷ TargetRadius</c> over circular-target abandons. A ratio &gt; 1 means the gaze was
    /// outside the target's hit radius at abandonment — poor aim → enlarge target geometry. Rectangular
    /// (radius 0) targets carry no usable radius, so they are excluded from both the mean and the gate
    /// count (rect-target aiming would need bounds-based near-miss — deferred). Feeds the aiming mode.
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window.</param>
    /// <param name="minAbandons">Confidence gate: minimum circular-target abandons before the controller may act. Default 4.</param>
    /// <param name="lowRatio">Ramp start; at or below this near-miss ratio the score is 0. Default 1.0 (gaze at the radius edge).</param>
    /// <param name="highRatio">Ramp end; at or above this ratio the score is 1. Default 2.5 — a tunable starting value.</param>
    /// <returns>The aiming signal score; <see cref="SignalScore.None"/> when there are no circular-target abandons.</returns>
    public static SignalScore ScoreAiming(
        IReadOnlyList<DwellOutcome> window,
        int minAbandons = 4,
        double lowRatio = 1.0,
        double highRatio = 2.5)
    {
        double ratioSum = 0.0;
        int count = 0;
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned || o.TargetRadius <= 0.0)
                continue; // only circular-target abandons have a usable near-miss ratio
            double magnitude = Math.Sqrt(o.NearMissX * o.NearMissX + o.NearMissY * o.NearMissY);
            ratioSum += magnitude / o.TargetRadius;
            count++;
        }

        if (count == 0)
            return SignalScore.None;

        double score = Ramp(ratioSum / count, lowRatio, highRatio);
        double confidence = Math.Clamp((double)count / minAbandons, 0.0, 1.0);
        return new SignalScore(score, confidence, count >= minAbandons);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: PASS (32 tests in `StruggleSignalsTests` — the 25 from 1b-③a + 7 aiming). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleSignals.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs
git commit -m "feat(adaptation): add aiming (near-miss ratio) signal scorer"
```

---

## Task 2: `ScoreDrift` (cross-context ±30°-coherent near-miss bias)

The one non-trivial scorer: it must fire on genuine calibration drift (a bias coherent across multiple surfaces) yet **not** on a single-surface bias (a UI-geometry artifact — §6.2/C2).

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `StruggleSignalsTests` (reuses `AbandonNM` from Task 1):

```csharp
    // ---- Drift ----

    [Fact]
    public void Drift_Empty_ReturnsZeroUngated()
    {
        var s = StruggleSignals.ScoreDrift(new List<DwellOutcome>());
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Drift_SingleContextStrongBias_IsNotDrift()
    {
        // 8 abandons in ONE context all biased hard right. A single-context bias is UI geometry, NOT
        // calibration drift (§6.2/C2) → only 1 qualifying context → gate false, score 0.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 8; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, nmX: 80, nmY: 0));
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9); // 1 qualifying context / min 2
    }

    [Fact]
    public void Drift_TwoContextsCoherent_FiresDrift()
    {
        // Two contexts, 5 abandons each, both mean-biased (50,0). Coherent + magnitude 50 vs the 100 px
        // normalizer → coherence 1.0 × magNorm 0.5 = score 0.5.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsCloseTo(0.5, 1e-9);
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }

    [Fact]
    public void Drift_TwoContextsIncoherent_GateFalse()
    {
        // Both contexts qualify (≥5 abandons) but bias 90° apart (right vs down) → not within ±30° →
        // no coherent pair → gate false, score 0. Confidence still 1.0 (two qualifying contexts).
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 0, 50));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Confidence).IsEqualTo(1.0);
    }

    [Fact]
    public void Drift_ContextBelowMinAbandons_DoesNotQualify()
    {
        // Context A: 5 coherent abandons. Context B: only 4 (< 5) → B does not qualify → 1 qualifying
        // context → gate false.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
        for (int i = 0; i < 4; i++)
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.GatePassed).IsFalse();
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.Confidence).IsCloseTo(0.5, 1e-9);
    }

    [Fact]
    public void Drift_LargerBias_SaturatesScore()
    {
        // Same coherent-pair shape but magnitude 100 == normalizer → magNorm 1.0 → score 1.0.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 100, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 100, 0));
        }
        Check.That(StruggleSignals.ScoreDrift(window).Score).IsCloseTo(1.0, 1e-9);
    }

    [Fact]
    public void Drift_CoherentPairPlusIncoherentThird_StillFires()
    {
        // A and B coherent (50,0); C (0,50) is incoherent but qualifies. The coherent PAIR must still
        // trigger drift (score 0.5 from {A,B}); an outlier context does not block it.
        var window = new List<DwellOutcome>();
        for (int i = 0; i < 5; i++)
        {
            window.Add(AbandonNM(i * Sec, DwellContext.ActionMenu, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Keyboard, 30, 50, 0));
            window.Add(AbandonNM(i * Sec, DwellContext.Toolbar, 30, 0, 50));
        }
        var s = StruggleSignals.ScoreDrift(window);
        Check.That(s.Score).IsCloseTo(0.5, 1e-9);
        Check.That(s.GatePassed).IsTrue();
        Check.That(s.Confidence).IsEqualTo(1.0);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: FAIL to **compile** — `ScoreDrift` does not exist.

- [ ] **Step 3: Add `ScoreDrift`**

In `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`, add this method inside the `StruggleSignals` class (e.g. after `ScoreAiming`):

```csharp
    /// <summary>
    /// Drift signal (design §13.1/§13.2/§6.2, C2): fires only when a near-miss bias is directionally
    /// coherent (within ±<paramref name="coherenceAngleDegrees"/>) across at least
    /// <paramref name="minCoherentContexts"/> distinct contexts, each with at least
    /// <paramref name="minAbandonsPerContext"/> abandons — the signature of a calibration slip. A bias
    /// confined to a single context is UI geometry, not drift, and never fires. Score = the coherent
    /// cluster's directional coherence × its normalized mean magnitude. The response is loosen tolerance
    /// + report; calibration is NEVER auto-touched (§8.5).
    /// </summary>
    /// <param name="window">Dwell outcomes in the evidence window.</param>
    /// <param name="minAbandonsPerContext">Abandons a context needs to qualify as evidence. Default 5 (§13.1).</param>
    /// <param name="minCoherentContexts">Coherent contexts required to classify drift. Default 2 (§13.1).</param>
    /// <param name="coherenceAngleDegrees">Half-angle (degrees) of the coherence cone. Default 30 (§13.1).</param>
    /// <param name="magnitudeNormalizerPx">Mean bias magnitude (px) at which the magnitude term saturates to 1. Default 100 — an UNCALIBRATED starting guess.</param>
    /// <returns>The drift signal score; a zero, ungated score when fewer than <paramref name="minCoherentContexts"/> contexts qualify or none are coherent.</returns>
    public static SignalScore ScoreDrift(
        IReadOnlyList<DwellOutcome> window,
        int minAbandonsPerContext = 5,
        int minCoherentContexts = 2,
        double coherenceAngleDegrees = 30.0,
        double magnitudeNormalizerPx = 100.0)
    {
        // Per-context accumulation of NearMiss vectors over abandons (order-independent).
        var groups = new Dictionary<DwellContext, (double X, double Y, int N)>();
        for (int i = 0; i < window.Count; i++)
        {
            DwellOutcome o = window[i];
            if (o.Kind != OutcomeKind.Abandoned)
                continue;
            groups.TryGetValue(o.Context, out var g);
            groups[o.Context] = (g.X + o.NearMissX, g.Y + o.NearMissY, g.N + 1);
        }

        // Qualifying contexts: enough abandons AND a non-zero mean bias direction.
        var vectors = new List<(double X, double Y, double Mag)>();
        foreach (var g in groups.Values)
        {
            if (g.N < minAbandonsPerContext)
                continue;
            double mx = g.X / g.N, my = g.Y / g.N;
            double mag = Math.Sqrt(mx * mx + my * my);
            if (mag > 0.0)
                vectors.Add((mx, my, mag));
        }

        // Confidence = evidence volume (qualifying-context count); GatePassed = coherence achieved. These
        // are DECOUPLED here (unlike the 1b-③a scorers where the gate tracks confidence): a fully-confident
        // "not drift" — Confidence 1.0 with GatePassed false — is a valid, intended outcome (two biased
        // contexts that simply don't align). §13.2's gate is "the §13.1 gate itself" (coherence), not volume.
        double confidence = Math.Clamp((double)vectors.Count / minCoherentContexts, 0.0, 1.0);
        if (vectors.Count < minCoherentContexts)
            return new SignalScore(0.0, confidence, false);

        // Largest cluster of context-bias vectors mutually within the coherence cone of an anchor.
        // (a,b within θ) ⟺ dot(a,b) >= |a||b|·cos θ. Score the winning cluster by its resultant
        // coherence (|Σv| ÷ Σ|v| ∈ (0,1]) × its normalized mean magnitude. Ties in size break toward the
        // higher score, so the result is independent of dictionary enumeration order.
        double cosThreshold = Math.Cos(coherenceAngleDegrees * Math.PI / 180.0);
        int bestSize = 0;
        double bestScore = 0.0;
        for (int a = 0; a < vectors.Count; a++)
        {
            double rx = 0.0, ry = 0.0, sumMag = 0.0;
            int count = 0;
            for (int b = 0; b < vectors.Count; b++)
            {
                double dot = vectors[a].X * vectors[b].X + vectors[a].Y * vectors[b].Y;
                if (dot >= vectors[a].Mag * vectors[b].Mag * cosThreshold)
                {
                    rx += vectors[b].X;
                    ry += vectors[b].Y;
                    sumMag += vectors[b].Mag;
                    count++;
                }
            }

            double coherence = Math.Sqrt(rx * rx + ry * ry) / sumMag; // 1 = perfectly aligned
            double magNorm = Math.Clamp((sumMag / count) / magnitudeNormalizerPx, 0.0, 1.0);
            double clusterScore = Math.Clamp(coherence * magNorm, 0.0, 1.0);
            if (count > bestSize || (count == bestSize && clusterScore > bestScore))
            {
                bestSize = count;
                bestScore = clusterScore;
            }
        }

        bool gate = bestSize >= minCoherentContexts;
        return new SignalScore(gate ? bestScore : 0.0, confidence, gate);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: PASS (39 tests in `StruggleSignalsTests` — 32 + 7 drift). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleSignals.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs
git commit -m "feat(adaptation): add cross-context drift signal scorer (C2 discrimination)"
```

---

## Task 3: `ScoreFatigue` (opt-in pass-through amplifier)

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `StruggleSignalsTests` (add the `QualityFat` helper + the fatigue tests):

```csharp
    // A gaze-quality summary carrying only a fatigue score (other fields 0 — unused by ScoreFatigue).
    private static GazeQualitySummary QualityFat(long ts, double fatigue) => new(ts, 0.0, 0.0, fatigue, 30);

    // ---- Fatigue (opt-in pass-through amplifier) ----

    [Fact]
    public void Fatigue_Disabled_ReturnsNone()
    {
        var window = new List<GazeQualitySummary> { QualityFat(0, 0.9), QualityFat(Sec, 0.9) };
        var s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: false);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Fatigue_Empty_ReturnsNone()
    {
        var s = StruggleSignals.ScoreFatigue(new List<GazeQualitySummary>(), fatigueMonitorEnabled: true);
        Check.That(s.Score).IsEqualTo(0.0);
        Check.That(s.GatePassed).IsFalse();
    }

    [Fact]
    public void Fatigue_Enabled_PassesThroughMeanScore()
    {
        var window = new List<GazeQualitySummary> { QualityFat(0, 0.2), QualityFat(Sec, 0.4), QualityFat(2 * Sec, 0.6) };
        var s = StruggleSignals.ScoreFatigue(window, fatigueMonitorEnabled: true);
        Check.That(s.Score).IsCloseTo(0.4, 1e-9); // mean of 0.2/0.4/0.6
        Check.That(s.Confidence).IsEqualTo(1.0);
        Check.That(s.GatePassed).IsTrue();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: FAIL to **compile** — `ScoreFatigue` does not exist.

- [ ] **Step 3: Add `ScoreFatigue`**

In `src/EyeCtrl.Core/Adaptation/StruggleSignals.cs`, add this method inside the `StruggleSignals` class (e.g. after `ScoreDrift`):

```csharp
    /// <summary>
    /// Fatigue amplifier (design §13.2/§6.2): a pass-through of the mean windowed EyeCare fatigue score,
    /// gated on the opt-in <c>EyeCareSettings.FatigueMonitorEnabled</c> (passed as
    /// <paramref name="fatigueMonitorEnabled"/>). Unlike the other signals it maps to NO failure mode of
    /// its own — the <c>StruggleMonitor</c> (plan 1b-③c) uses it to AMPLIFY the aggregate / other modes.
    /// Reuses <c>FatigueMonitor.Score</c> (already computed on the gaze thread); it is not recomputed here.
    /// </summary>
    /// <param name="window">Gaze-quality summaries in the evidence window.</param>
    /// <param name="fatigueMonitorEnabled">The <c>EyeCareSettings.FatigueMonitorEnabled</c> opt-in; when false the amplifier is inert.</param>
    /// <returns>A pass-through score of the mean <see cref="GazeQualitySummary.FatigueScore"/>; <see cref="SignalScore.None"/> when disabled or the window is empty.</returns>
    public static SignalScore ScoreFatigue(
        IReadOnlyList<GazeQualitySummary> window,
        bool fatigueMonitorEnabled)
    {
        if (!fatigueMonitorEnabled || window.Count == 0)
            return SignalScore.None;

        double sum = 0.0;
        for (int i = 0; i < window.Count; i++)
            sum += window[i].FatigueScore;

        return new SignalScore(sum / window.Count, 1.0, true);
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~StruggleSignalsTests"`
Expected: PASS (42 tests in `StruggleSignalsTests` — 39 + 3 fatigue). No `CS1591`.

- [ ] **Step 5: Full Core suite (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS. All three scorers are net-new and unreferenced; the existing suite (including 1b-③a's `StruggleAssessmentTests` + the other `StruggleSignals` scorers) is unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/StruggleSignals.cs tests/EyeCtrl.Core.Tests/Adaptation/StruggleSignalsTests.cs
git commit -m "feat(adaptation): add fatigue pass-through amplifier scorer"
```

---

## Self-Review

**Spec coverage (this slice):**
- §13.2 decomposition "1b-③b = the remaining scorers (Aiming, Drift [the complex cross-context one], Fatigue amplifier)" — Task 1 (Aiming) + Task 2 (Drift) + Task 3 (Fatigue). The `StruggleMonitor` is split to 1b-③c per §13.2's "may split further if large" (flagged in the scope boundary). ✓
- §13.2 Aiming "mean(|NearMiss| ÷ TargetRadius) on abandons, linear ramp above ~1.0, gate ≥ ~4 abandons" — `ScoreAiming` (ramp `[1.0, 2.5]`, `minAbandons=4`); rect/radius-0 targets and activations excluded (they have no usable ratio), proved by `Aiming_RectAndActivationsExcluded_*`. ✓
- §13.2/§13.1 Drift "per-context mean NearMiss vectors; coherent (±30°) across ≥ 2 contexts, ≥ 5 abandons each; coherence × magnitude; gate = the §13.1 gate itself" — `ScoreDrift` (`coherenceAngleDegrees=30`, `minCoherentContexts=2`, `minAbandonsPerContext=5`; score = cluster coherence × normalized mean magnitude). ✓
- §6.2/C2 "single-context bias is UI geometry, not drift" — `Drift_SingleContextStrongBias_IsNotDrift` (8 hard-biased abandons in one context → gate false, score 0); `Drift_CoherentPairPlusIncoherentThird_StillFires` proves an outlier context doesn't block a genuine coherent pair. ✓
- §13.2/§6.2 Fatigue "pass-through amplifier, opt-in (EyeCareSettings.FatigueMonitorEnabled)" — `ScoreFatigue` (mean windowed `FatigueScore`, gated on the opt-in bool); reuses `FatigueMonitor.Score`, does not recompute (§9.1). No `FailureMode` — the monitor amplifies (deferred to 1b-③c). ✓
- §6.3 per-signal confidence gate — each scorer returns its own `Confidence`/`GatePassed`. ✓
- **Deferred (documented, not gaps):** the `StruggleMonitor` (1b-③c); consuming the assessment (1b-④); fatigue amplification semantics (1b-③c); rich drift reporting (later); threshold configurability (later).

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** all three scorers return `SignalScore(double, double, bool)` and short-circuit to `SignalScore.None`, matching 1b-③a. `ScoreAiming` reuses 1b-③a's `private static double Ramp(double, double, double)`. `DwellOutcome` reads (`Kind`, `TargetRadius`, `NearMissX`, `NearMissY`, `Context`) and `GazeQualitySummary.FatigueScore` match the 1a contracts (pre-flight-confirmed). The test helpers `AbandonNM`/`QualityFat` construct `DwellOutcome`/`GazeQualitySummary` in the exact 1a positional order; `AbandonNM` sets `TargetCenter=(0,0)` and `GazePoint=(nmX,nmY)` so `NearMissX/Y == nmX/nmY`. Method/argument names (`minAbandons`, `minAbandonsPerContext`, `minCoherentContexts`, `coherenceAngleDegrees`, `magnitudeNormalizerPx`, `fatigueMonitorEnabled`, `lowRatio`/`highRatio`) match between the code and the tests' named/positional usage.

**Verified facts relied on (from source):**
- `EyeCareSettings.FatigueMonitorEnabled` is a Core `bool` setting (`src/EyeCtrl.Core/Settings/EyeCareSettings.cs`), referenced in docs; the scorer takes a plain `bool` so Core keeps no new dependency.
- `DwellOutcome.NearMissX`/`NearMissY` are computed `= GazePoint − TargetCenter` (1a-① §6.1); `TargetRadius` is `0` for rect targets (1a-① doc), so the `> 0` guard in `ScoreAiming` correctly skips them.
- `EyeCtrl.Core.csproj` `ImplicitUsings=enable` + `WarningsAsErrors=CS1591`; NFluent `IsCloseTo(expected, delta)` for division results, `IsEqualTo` only on exact values (`0.0`, `1.0`, ratios that land exactly).
- `Math.Cos`/`Math.Sqrt`/`Math.PI`/`Math.Clamp` resolve via `ImplicitUsings`; `Dictionary`/`List` via `System.Collections.Generic`.

**Order-independence:** all three scorers are order-independent (Aiming = mean; Drift = per-context grouping + means + clustering; Fatigue = mean), so — unlike 1b-③a's abandonment/accidental/poor-tracking — **none calls `AssertOrdered`**, and none relies on the oldest-first window precondition. (Correct: there is no early-`break` prune or first/last-element span here.)

**Drift algorithm notes (the load-bearing correctness):** grouping is by `Context`; a context qualifies only at `≥ minAbandonsPerContext` abandons and a non-zero mean vector. Coherence uses the cosine test `dot(a,b) ≥ |a||b|·cosθ` (no `atan2`); the anchor is always in its own cluster (`dot(a,a)=|a|² ≥ |a|²·cosθ` since `cosθ ≤ 1`), so `sumMag > 0` and coherence never divides by zero. The winner is the largest cluster, ties broken toward higher score — so the result does not depend on `Dictionary` enumeration order (a deliberate guard, since dictionary order is not contractual). `Drift_TwoContextsCoherent` (score 0.5) and `Drift_LargerBias_SaturatesScore` (score 1.0) pin the coherence×magnitude math; `Drift_TwoContextsIncoherent` and `Drift_ContextBelowMinAbandons` pin the two gate failure paths.

**Honest testability limits:** every scorer is a pure, framework-agnostic Core function, fully headless-unit-tested (17 new tests: 7 aiming + 7 drift + 3 fatigue) over synthetic sequences — no display, sink, clock, or threading dependency. Boundary behaviour (empty → `None`, below-gate score-still-computed, ramp low/mid/high, rect/activation exclusion, single-vs-cross-context drift, incoherent/under-qualified drift gates, magnitude saturation, fatigue opt-in) is asserted directly. What is **not** exercised here (correctly, per the scope boundary): how the monitor classifies these scores into `FailureMode`s, how Fatigue amplifies, and end-to-end assessment production from a live sink — all 1b-③c/1b-④. The Aiming ramp bounds and the Drift `magnitudeNormalizerPx` are **uncalibrated starting values** (the ±30° / ≥2 / ≥5 drift gates are the spec's §13.1 numbers) — the tests assert the *structure* (ramps, gating, coherence discrimination), not that any absolute px number is field-calibrated.

**Deferred (documented above, not gaps):** the `StruggleMonitor` itself (1b-③c); the `AdaptationController` (1b-④); fatigue amplification semantics (1b-③c); rich drift reporting; threshold→settings promotion.
