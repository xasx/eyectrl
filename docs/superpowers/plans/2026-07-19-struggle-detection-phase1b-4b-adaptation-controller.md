# Struggle Detection — Phase 1b-④b AdaptationController — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the momentary `AdaptationController` (Core) — the stateful component that **consumes a `StruggleAssessment` per tick** and pushes bounded, reversible per-knob band-fill fractions into the 1b-④a `EffectiveSettingsProvider`, realizing the §7.1 failure-mode → typed-knob transform map and the §7.2 relax/recover/deadband stepping over the §13.1 posture presets. Forgiving-only except the **single quarantined dwell tightening** (longer dwell) that only the accidental-activation mode drives (§8.2). **Zero behaviour change:** net-new, unreferenced Core type; nothing constructs or ticks it until the `AdaptationCoordinator` (1b-⑤).

**Architecture:** `AdaptationController(EffectiveSettingsProvider provider, AdaptationSettings adaptation)` holds, per knob, a **forgiving fill** and a **tightening fill** (each a dimensionless band position `∈ [0, 1]`) plus a relax-sustain timestamp per direction. `Tick(assessment, now)` reads the posture preset (`PosturePresets.For`, live), computes each knob's **drive** = the peak *gated* score among its feeding modes (ungated ⇒ 0 — the F4 safety), steps the fill (relax past `RelaxThreshold` held `SustainSeconds`; recover below `RecoverThreshold`; deadband between), and pushes the new fills into the provider **only when a fill changes** (bounding `Changed`/rebuild churn to the tick rate, §9.1). `Reset()` zeroes every fill and clears the provider — the always-reachable escape (§8.6). Baseline-agnostic: the controller steps fractions, and the provider resolves `fraction × baseline` at read time (1b-④a), which is what lets one global fill drive the multi-baseline dwell knob and the runtime-derived geometry knob.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`, `IsCloseTo(expected, delta)` for division/arithmetic results, `IsEqualTo` only for exact or clamp-saturated values).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — **§7.1** (failure-mode → typed knob transform), **§7.2** (relax/recover stepping: `RelaxThreshold`/`SustainSeconds`/`RelaxStepFraction`, `RecoverThreshold`/`RecoverStepFraction`, deadband), **§8.2** (the single quarantined tightening — longer dwell, accidental-activation only, own sub-band, instantly escapable), §8.3 (zero delta ⇒ baseline), §8.6 (always-reachable escape), §8.8 (hysteresis + deadband), §5 (typed per-knob directions), §13.1 (posture presets), §12 (the AdaptationController test list: typed transforms/directions; sub-band value model; learned-forgiving + accidental-tightening coexistence [C1]; hysteresis/no-oscillation).

**Depends on:** **Phase 1a + 1b-①/②/③(a) + 1b-④a implemented.** From 1b-④a: `EffectiveSettingsProvider.SetMomentaryFill(AdaptationKnob, DwellContext, double forgivingFill, double tighteningFill)` + `ClearMomentaryDeltas()` + `Effective(AdaptationKnob, DwellContext, double)`; `AdaptationKnob` = `{ FixationRadius, DwellTime, Smoothing, TargetGeometry }`. From 1b-②: `PosturePresets.For(AdaptationPosture) → PostureTuning` + `AdaptationSettings` per-knob enables. From 1b-③a: `StruggleAssessment` + `FailureModeAssessment` + `FailureMode`. Written against the **post-1b-④a** source. **No dependency on 1b-③b/③c or the monitor** — the controller consumes a `StruggleAssessment` regardless of who produced it.

**Executor pre-flight (do this FIRST — MANDATORY; the upstream slices are plans-only):** confirm these against the *merged* source before writing any code:
1. `EffectiveSettingsProvider` (1b-④a, `sealed`, `EyeCtrl.Core.Adaptation`): `void SetMomentaryFill(AdaptationKnob knob, DwellContext context, double forgivingFill, double tighteningFill)`, `void ClearMomentaryDeltas()`, `double Effective(AdaptationKnob, DwellContext, double baseline)`. **If 1b-④a shipped the absolute-delta `SetMomentaryDelta` instead (e.g. if the fill-fraction decision was reversed), STOP** — this plan's controller pushes fills and is incompatible; reconcile the representation first.
2. `AdaptationKnob` has `FixationRadius`, `DwellTime`, `Smoothing`, `TargetGeometry` (1b-④a).
3. `PosturePresets.For(AdaptationPosture) → PostureTuning(double RelaxThreshold, int SustainSeconds, double RelaxStepFraction, double RecoverThreshold, double RecoverStepFraction)` (1b-②). Conservative default = `(0.50, 3, 0.10, 0.25, 0.05)`.
4. `AdaptationSettings` (1b-②): `bool Enabled`, `AdaptationPosture Posture`, `bool AdaptTolerance`, `bool AdaptDwell`, `bool AdaptSmoothing`, `bool AdaptGeometry` (all default true except `Enabled`).
5. `StruggleAssessment.ForMode(FailureMode) → FailureModeAssessment?`; `FailureModeAssessment(FailureMode Mode, double Score, double Confidence, bool GatePassed, DwellContext? Context)`; ctor `StruggleAssessment(IReadOnlyList<FailureModeAssessment> modes, double aggregateLevel)`; `FailureMode` = `{ SustainFailure, AccidentalActivation, Aiming, Drift, PoorTracking }` (1b-③a).
6. `DwellContext.Other` (1a-①).

If any differ, adjust before proceeding.

**⚠️ Core enforces XML docs as build errors:** `WarningsAsErrors=CS1591`. Every public member (ctor + `Tick` + `Reset`, with all `<param>`) is documented below. `ImplicitUsings=enable` covers `System` (`Math`) and `System.Collections.Generic` (`Dictionary`); the only explicit `using` needed is `EyeCtrl.Core.Settings` (for `AdaptationSettings`). `PostureTuning`/`PosturePresets`/`FailureMode`/`AdaptationKnob`/`StruggleAssessment` are same-namespace (`EyeCtrl.Core.Adaptation`). Private members don't need CS1591 docs but are commented. Do not strip the doc comments.

---

## Scope boundary (explicit)

This slice is the **controller only** — a pure, headless-testable Core component. Deferred:
- **DI registration + ticking + reset wiring.** Nothing constructs or calls the controller here; the `AdaptationCoordinator` (**1b-⑤**) registers it (singleton over the DI `EffectiveSettingsProvider` + `EyeCtrlSettings.Adaptation`), ticks `Tick(monitor.Assess(now), now)` on a dispatcher timer, and calls `Reset()` from the toolbar/tray escape and on session boundaries. Enable-gating the *ticking* is also ⑤'s (the controller additionally self-guards on `Enabled`, so a stray tick is safe).
- **Observability of three of the four knobs.** Only `FixationRadius` (tolerance) is routed to a consumer today (`GazeEngine`, 1b-①). The controller drives all four knobs' fills, but `DwellTime`/`Smoothing`/`TargetGeometry` remain stored no-ops until their routing slices (1b-④a scope boundary). The controller's *decisions* for those knobs are fully tested at the `provider.Effective(knob, Other, baseline)` composition level.
- **The drift "report" half of §7.1.** Drift's response is "loosen tolerance **+ report**"; this controller does the tolerance loosening. The calibration-drift *report* (a surfaced notification, never a calibration write — §8.5) is a coordinator/feedback concern (**1b-⑤**).
- **Fatigue amplification.** Fatigue is folded into the assessment's `AggregateLevel` by the `StruggleMonitor` (1b-③c), not re-applied here. The controller drives knobs off **per-mode gated scores**, not the aggregate (the aggregate drives cue intensity in ⑤).
- **Learned layer.** The forgiving learned offset (Phase 2) is pinned to 0; this controller is momentary-only. `learnedOffset` never enters the picture here.
- **Threshold configurability.** The posture presets come from `PosturePresets` (1b-②); no new settings.

---

## File Structure

**Create (Core):** `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`.
**Test (create):** `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs`.

**Zero-behaviour-change guarantee:** one net-new Core class + its tests. Nothing references `AdaptationController`; the existing suite is untouched.

---

## Task 1: `AdaptationController` — forgiving stepping engine (tolerance) + reset + gating

Builds the controller with the full relax/recover/deadband stepping engine, wired for the **tolerance** knob (the one routed knob), plus `Reset()` and the disabled/enable gating. The remaining forgiving knobs (Task 2) and the quarantined tightening (Task 3) reuse this engine.

**Files:**
- Create: `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs` (helpers here are reused by Tasks 2–3):

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class AdaptationControllerTests
{
    private const long Sec = 1_000_000; // microseconds per second

    // Enabled, Conservative posture (default): Relax 0.50 / Sustain 3 s / RelaxStep 0.10, Recover 0.25 / RecoverStep 0.05.
    private static (AdaptationController Controller, EffectiveSettingsProvider Provider, AdaptationSettings Settings) Make()
    {
        var settings = new AdaptationSettings { Enabled = true };
        var provider = new EffectiveSettingsProvider(settings);
        return (new AdaptationController(provider, settings), provider, settings);
    }

    private static StruggleAssessment Assess(params FailureModeAssessment[] modes) => new(modes, 0.0);

    // The controller reads GatePassed + Score (not Confidence or AggregateLevel), so those are arbitrary here.
    private static FailureModeAssessment Mode(FailureMode mode, double score, bool gated = true) =>
        new(mode, score, gated ? 1.0 : 0.5, gated, null);

    [Fact]
    public void Forgiving_RelaxesOnlyAfterSustain_Tolerance()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7)); // ≥ RelaxThreshold 0.5, gated

        // Below the 3 s sustain (t=0, t=2 s): no relaxation → baseline.
        c.Tick(a, 0);
        c.Tick(a, 2 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);

        // Sustain reached (t=3 s): first step (fill 0.10). Band +ToleranceLooser(0.5)×50 = +25; 10% → +2.5 → 52.5.
        c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);

        // Still sustained (t=4 s): second step (fill 0.20) → +5 → 55.
        c.Tick(a, 4 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(55.0, 1e-9);
    }

    [Fact]
    public void Forgiving_RecoversWhenStruggleSubsides()
    {
        var (c, p, _) = Make();
        var high = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(high, 0); c.Tick(high, 3 * Sec); c.Tick(high, 4 * Sec); // relax to fill 0.20 → 55
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(55.0, 1e-9);

        // No struggle (drive 0 < RecoverThreshold 0.25) → recover by RecoverStep 0.05: 0.20 → 0.15 → +3.75 → 53.75.
        c.Tick(Assess(), 5 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(53.75, 1e-9);
    }

    [Fact]
    public void Deadband_HoldsWithoutOscillation()
    {
        var (c, p, _) = Make();
        var high = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(high, 0); c.Tick(high, 3 * Sec); // relax to fill 0.10 → 52.5
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);

        // Drive in the deadband [0.25, 0.5): 0.35 → hold across ticks (neither relax nor recover, §8.8).
        var mid = Assess(Mode(FailureMode.SustainFailure, 0.35));
        c.Tick(mid, 4 * Sec);
        c.Tick(mid, 5 * Sec);
        c.Tick(mid, 6 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);
    }

    [Fact]
    public void UngatedMode_DoesNotDrive()
    {
        // High score but the confidence gate is NOT met → drive 0 → never relaxes (F4: below-gate evidence
        // never drives adaptation).
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.9, gated: false));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); c.Tick(a, 6 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void NoStruggle_ReturnsExactBaseline_EvenOutOfRange()
    {
        // §12 sub-band value model at the controller level: with no gated struggle the controller sets no
        // fill, so effective == the exact baseline — even a baseline outside the knob's validated range
        // (the zero-delta ⇒ exact-baseline invariant, resolved by the provider / EffectiveValue).
        var (c, p, _) = Make();
        c.Tick(Assess(), 0);
        c.Tick(Assess(), 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 5000)).IsEqualTo(5000.0);
    }

    [Fact]
    public void Reset_ReturnsToBaseline()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsNotEqualTo(50.0);

        c.Reset(); // the always-reachable escape (§8.6)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void MasterDisabled_ClearsMomentaryState()
    {
        var (c, p, s) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsNotEqualTo(50.0);

        s.Enabled = false;
        c.Tick(a, 6 * Sec);  // disabled branch → clears controller + provider state
        s.Enabled = true;    // re-enable: if the controller CLEARED (not just the provider gate hid it), effective is baseline
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void ToleranceDisabled_StaysBaseline_UnderStruggle()
    {
        var (c, p, s) = Make();
        s.AdaptTolerance = false;
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: FAIL to **compile** — `AdaptationController` does not exist.

- [ ] **Step 3: Create the controller (engine + tolerance)**

Create `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// The momentary struggle-adaptation controller (design §7.1/§7.2). Consumes a
/// <see cref="StruggleAssessment"/> per tick and pushes bounded, reversible per-knob band-fill fractions
/// into the <see cref="EffectiveSettingsProvider"/>. Stateful: holds each knob's current forgiving and
/// tightening fill plus a relax-sustain timer per direction, advanced by the tick timestamp. Forgiving-only
/// except the single quarantined dwell tightening (longer dwell), which only the accidental-activation mode
/// drives, lives in its own sub-band, and is instantly escapable via <see cref="Reset"/> (§8.2). Ticked by
/// the AdaptationCoordinator (plan 1b-⑤) — not self-ticking. Single-threaded (dispatcher).
/// </summary>
public sealed class AdaptationController
{
    private readonly EffectiveSettingsProvider _provider;
    private readonly AdaptationSettings _adaptation;

    // Per-knob momentary state. Fills are the [0, 1] band position; RelaxSince records when the relax
    // condition began (long.MinValue = not currently sustaining a relax), for the SustainSeconds hold.
    private sealed class KnobState
    {
        public double ForgivingFill;
        public double TighteningFill;
        public long ForgivingRelaxSince = long.MinValue;
        public long TighteningRelaxSince = long.MinValue;
    }

    private readonly Dictionary<AdaptationKnob, KnobState> _state = new()
    {
        [AdaptationKnob.FixationRadius] = new(),
        [AdaptationKnob.DwellTime] = new(),
        [AdaptationKnob.Smoothing] = new(),
        [AdaptationKnob.TargetGeometry] = new(),
    };

    /// <summary>Initializes the controller over the effective-settings provider and adaptation settings.</summary>
    /// <param name="provider">The provider the controller pushes momentary fills into.</param>
    /// <param name="adaptation">Adaptation settings: master switch, posture (→ <see cref="PosturePresets"/>), and per-knob enables — all read live per tick.</param>
    public AdaptationController(EffectiveSettingsProvider provider, AdaptationSettings adaptation)
    {
        _provider = provider;
        _adaptation = adaptation;
    }

    /// <summary>
    /// Advances the controller one tick: for each enabled knob, computes its drive from the gated feeding
    /// modes, steps its fill (relax/recover/deadband per the posture tuning and
    /// <paramref name="nowMicroseconds"/>), and pushes the new fills into the provider when they change.
    /// Clears all momentary state and returns immediately when adaptation is disabled (§12: disabled ⇒ no delta).
    /// </summary>
    /// <param name="assessment">The current struggle assessment (per-mode gated scores).</param>
    /// <param name="nowMicroseconds">The current tick timestamp, in microseconds (advances the sustain timers).</param>
    public void Tick(StruggleAssessment assessment, long nowMicroseconds)
    {
        if (!_adaptation.Enabled)
        {
            Reset();
            return;
        }

        PostureTuning tuning = PosturePresets.For(_adaptation.Posture);

        // Tolerance: loosen (forgiving = larger) on sustain-failure OR drift (union of forgiving responses, §8.2).
        UpdateKnob(AdaptationKnob.FixationRadius, _adaptation.AdaptTolerance, tuning, nowMicroseconds,
            forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.Drift),
            tighteningDrive: 0.0);
    }

    /// <summary>
    /// Clears all momentary adaptation — every knob's fill and sustain timer — and the provider's stored
    /// fills, returning effective settings to baseline. The always-reachable escape (§8.6); invoked by the
    /// coordinator on the toolbar/tray reset and on session boundaries (plan 1b-⑤).
    /// </summary>
    public void Reset()
    {
        foreach (KnobState s in _state.Values)
        {
            s.ForgivingFill = 0.0;
            s.TighteningFill = 0.0;
            s.ForgivingRelaxSince = long.MinValue;
            s.TighteningRelaxSince = long.MinValue;
        }
        _provider.ClearMomentaryDeltas();
    }

    // Steps a knob's forgiving + tightening fills for one tick and pushes them to the provider ONLY when a
    // fill changed (so Changed / any consumer rebuild fires at most once per tick — bounded by the tick rate,
    // §9.1). A disabled knob collapses immediately to baseline and drops its sustain timers (§12).
    private void UpdateKnob(AdaptationKnob knob, bool enabled, PostureTuning tuning, long now,
        double forgivingDrive, double tighteningDrive)
    {
        KnobState s = _state[knob];
        double beforeForgiving = s.ForgivingFill;
        double beforeTightening = s.TighteningFill;

        if (!enabled)
        {
            s.ForgivingFill = 0.0;
            s.TighteningFill = 0.0;
            s.ForgivingRelaxSince = long.MinValue;
            s.TighteningRelaxSince = long.MinValue;
        }
        else
        {
            s.ForgivingFill = Step(s.ForgivingFill, forgivingDrive, tuning, now, ref s.ForgivingRelaxSince);
            s.TighteningFill = Step(s.TighteningFill, tighteningDrive, tuning, now, ref s.TighteningRelaxSince);
        }

        if (s.ForgivingFill != beforeForgiving || s.TighteningFill != beforeTightening)
            _provider.SetMomentaryFill(knob, DwellContext.Other, s.ForgivingFill, s.TighteningFill);
    }

    // One relax/recover/deadband step (§7.2). relaxSince tracks when the relax condition began (advanced by
    // the tick 'now') so the score must stay above RelaxThreshold for SustainSeconds before relaxing.
    private static double Step(double fill, double drive, PostureTuning tuning, long now, ref long relaxSince)
    {
        if (drive >= tuning.RelaxThreshold)
        {
            if (relaxSince == long.MinValue)
                relaxSince = now;
            if (now - relaxSince >= (long)tuning.SustainSeconds * 1_000_000L)
                return Math.Min(1.0, fill + tuning.RelaxStepFraction); // relax toward the band edge
            return fill; // above threshold but not yet held long enough → hold
        }

        relaxSince = long.MinValue; // dropped below the relax threshold → stop sustaining
        if (drive < tuning.RecoverThreshold)
            return Math.Max(0.0, fill - tuning.RecoverStepFraction); // recover toward baseline
        return fill; // deadband [RecoverThreshold, RelaxThreshold) → hold (hysteresis, §8.8)
    }

    // Peak GATED score among a knob's feeding modes (the union of forgiving responses, §8.2). Ungated modes
    // contribute 0 — the controller acts only on gated modes (§6.3; the F4 safety).
    private static double ForgivingDrive(StruggleAssessment assessment, params FailureMode[] feeders)
    {
        double drive = 0.0;
        foreach (FailureMode mode in feeders)
            drive = Math.Max(drive, GatedScore(assessment, mode));
        return drive;
    }

    // The mode's score when its confidence gate is met, else 0 (F4: below-gate evidence never drives adaptation).
    private static double GatedScore(StruggleAssessment assessment, FailureMode mode)
    {
        FailureModeAssessment? m = assessment.ForMode(mode);
        return m.HasValue && m.Value.GatePassed ? m.Value.Score : 0.0;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: PASS (8 tests). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationController.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs
git commit -m "feat(adaptation): add AdaptationController stepping engine (tolerance + reset + gating)"
```

---

## Task 2: Route the remaining forgiving knobs (dwell-shorter, smoothing, geometry)

Extend `Tick` to the other three forgiving responses per the §7.1 map: sustain-failure → shorter dwell + more smoothing; aiming → bigger geometry; poor-tracking → more smoothing + bigger geometry. All reuse the Task-1 engine.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `AdaptationControllerTests`:

```csharp
    [Fact]
    public void SustainFailure_LoosensTolerance_ShortensDwell_IncreasesSmoothing_NotGeometry()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // fill 0.10 on each driven knob

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // +looser
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(672.0, 1e-9);      // −shorter (−0.4×700×0.10 = −28)
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (−0.5×5×0.10 = −0.25)
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);        // sustain-failure does NOT enlarge geometry
    }

    [Fact]
    public void PoorTracking_IncreasesSmoothingAndGeometry_NotToleranceOrDwell()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.PoorTracking, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // −more (smoothing fed by poor-tracking too, union)
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsCloseTo(105.0, 1e-9); // +bigger (+0.5×100×0.10 = +5)
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);          // NOT tolerance
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // NOT dwell (never tighten on poor-tracking, §7.1)
    }

    [Fact]
    public void Aiming_EnlargesGeometryOnly()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.Aiming, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsCloseTo(105.0, 1e-9);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsEqualTo(5.0);   // aiming ≠ smoothing
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Drift_LoosensToleranceOnly()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.Drift, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9); // loosen tolerance (the "report" half is 1b-⑤)
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);          // drift never shortens dwell
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);
    }

    [Fact]
    public void DwellDisabled_StaysBaseline_WhileOtherKnobsAdapt()
    {
        var (c, p, s) = Make();
        s.AdaptDwell = false;
        var a = Assess(Mode(FailureMode.SustainFailure, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec);

        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);            // dwell disabled
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsCloseTo(52.5, 1e-9);   // tolerance still adapts
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsCloseTo(4.75, 1e-9);       // smoothing still adapts
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: FAIL — the new knobs aren't driven yet, so their `Effective` returns baseline (e.g. `SustainFailure_...` expects dwell 672 but gets 700).

- [ ] **Step 3: Extend `Tick` with the remaining forgiving knobs**

In `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`, in `Tick`, immediately after the `FixationRadius` `UpdateKnob` call, add the three more forgiving knobs:

```csharp
        // Dwell: shorten (forgiving) on sustain-failure. Tightening (longer) is added in Task 3.
        UpdateKnob(AdaptationKnob.DwellTime, _adaptation.AdaptDwell, tuning, nowMicroseconds,
            forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure),
            tighteningDrive: 0.0);
        // Smoothing: more (parameter down) on sustain-failure OR poor-tracking (union).
        UpdateKnob(AdaptationKnob.Smoothing, _adaptation.AdaptSmoothing, tuning, nowMicroseconds,
            forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure, FailureMode.PoorTracking),
            tighteningDrive: 0.0);
        // Geometry: enlarge on aiming OR poor-tracking (union).
        UpdateKnob(AdaptationKnob.TargetGeometry, _adaptation.AdaptGeometry, tuning, nowMicroseconds,
            forgivingDrive: ForgivingDrive(assessment, FailureMode.Aiming, FailureMode.PoorTracking),
            tighteningDrive: 0.0);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: PASS (13 tests — 8 from Task 1 + 5 new). No `CS1591`.

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationController.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs
git commit -m "feat(adaptation): route sustain-failure/aiming/poor-tracking to dwell/smoothing/geometry"
```

---

## Task 3: The quarantined dwell tightening (accidental activation → longer dwell) + C1

Turn on the single tightening move: accidental-activation lengthens dwell, in dwell's own tightening sub-band, driven by **that mode only**, coexisting with a forgiving shorten without either being swallowed (§8.2/C1).

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `AdaptationControllerTests`:

```csharp
    [Fact]
    public void AccidentalActivation_LengthensDwell_Quarantined()
    {
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.AccidentalActivation, 0.7));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // dwell tightening fill 0.10

        // Longer band = +DwellLonger(0.5)×700 = +350; 10% → +35 → 735.
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(735.0, 1e-9);
        // Quarantine (§8.2): accidental drives ONLY the dwell tightening — nothing else moves.
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsEqualTo(5.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 100)).IsEqualTo(100.0);
    }

    [Fact]
    public void SustainFailureAndAccidental_DwellForgivingAndTighteningCoexist()
    {
        // C1 (§5/§8.2/§12): dwell forgiving (shorter) from sustain-failure and tightening (longer) from
        // accidental-activation apply in independent sub-bands and coexist — the tightening is NOT swallowed.
        var (c, p, _) = Make();
        var a = Assess(
            Mode(FailureMode.SustainFailure, 0.7),        // → dwell shorter (forgiving)
            Mode(FailureMode.AccidentalActivation, 0.7)); // → dwell longer (tightening)
        c.Tick(a, 0); c.Tick(a, 3 * Sec); // both fills 0.10

        // forgiving −0.4×700×0.10 = −28; tightening +0.5×700×0.10 = +35 → 700 − 28 + 35 = 707.
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(707.0, 1e-9);
    }

    [Fact]
    public void AccidentalUngated_DoesNotTighten()
    {
        // Below-gate accidental (high score, gate false) must not tighten dwell (F4).
        var (c, p, _) = Make();
        var a = Assess(Mode(FailureMode.AccidentalActivation, 0.9, gated: false));
        c.Tick(a, 0); c.Tick(a, 3 * Sec); c.Tick(a, 6 * Sec);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsEqualTo(700.0);
    }

    [Fact]
    public void Tightening_RecoversIndependently_WhenAccidentalSubsides()
    {
        var (c, p, _) = Make();
        var high = Assess(Mode(FailureMode.AccidentalActivation, 0.7));
        c.Tick(high, 0); c.Tick(high, 3 * Sec); c.Tick(high, 4 * Sec); // tightening fill 0.20 → +70 → 770
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(770.0, 1e-9);

        c.Tick(Assess(), 5 * Sec); // no struggle → recover 0.20 → 0.15 → +52.5 → 752.5 (instantly escapable, §8.2)
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 700)).IsCloseTo(752.5, 1e-9);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: FAIL — dwell tightening is still hardwired to `0.0`, so `AccidentalActivation_...` sees dwell 700, not 735.

- [ ] **Step 3: Drive the dwell tightening from accidental activation**

In `src/EyeCtrl.Core/Adaptation/AdaptationController.cs`, in `Tick`, change the `DwellTime` `UpdateKnob` call's `tighteningDrive` from `0.0` to the accidental-activation gated score. The `DwellTime` call becomes:

```csharp
        // Dwell: shorten (forgiving) on sustain-failure; the ONE quarantined tightening (longer dwell) on
        // accidental-activation ONLY, in its own sub-band, instantly escapable via Reset (§8.2).
        UpdateKnob(AdaptationKnob.DwellTime, _adaptation.AdaptDwell, tuning, nowMicroseconds,
            forgivingDrive: ForgivingDrive(assessment, FailureMode.SustainFailure),
            tighteningDrive: GatedScore(assessment, FailureMode.AccidentalActivation));
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationControllerTests"`
Expected: PASS (17 tests — 13 + 4 new). No `CS1591`.

- [ ] **Step 5: Full Core suite (confirm zero regressions)**

Run: `dotnet test tests/EyeCtrl.Core.Tests`
Expected: PASS. `AdaptationController` is net-new and unreferenced; the existing suite (including the 1b-④a provider tests) is unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationController.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationControllerTests.cs
git commit -m "feat(adaptation): add quarantined dwell tightening for accidental activation (C1)"
```

---

## Self-Review

**Spec coverage (this slice):**
- §7.1 failure-mode → typed knob transform — sustain-failure → looser tolerance + shorter dwell + more smoothing; accidental → longer dwell (tightening); aiming → bigger geometry; drift → looser tolerance; poor-tracking → more smoothing + bigger geometry. Each mode's footprint is asserted, including the "NOT" knobs (e.g. poor-tracking never touches dwell, sustain-failure never enlarges geometry). ✓
- §7.2 relax/recover/deadband stepping — `Step`: relax past `RelaxThreshold` held `SustainSeconds` by `RelaxStepFraction`; recover below `RecoverThreshold` by `RecoverStepFraction`; deadband holds. Verified by `Forgiving_RelaxesOnlyAfterSustain`, `Forgiving_RecoversWhenStruggleSubsides`, `Deadband_HoldsWithoutOscillation`. ✓
- §8.2 quarantined tightening — dwell longer, driven by accidental-activation ONLY, in its own tightening sub-band, coexisting with forgiving (C1), instantly escapable (`Tightening_RecoversIndependently`, `Reset`). `AccidentalActivation_LengthensDwell_Quarantined` proves nothing else moves. ✓
- §8.3 zero delta ⇒ baseline — no drive ⇒ fills stay 0 ⇒ provider returns baseline (`UngatedMode_DoesNotDrive`, and every "NOT" assertion). ✓
- §8.6 always-reachable escape — `Reset()` (`Reset_ReturnsToBaseline`). ✓
- §8.8 hysteresis + deadband — `Deadband_HoldsWithoutOscillation` (drive in the deadband holds the fill across ticks, no oscillation). ✓
- §12 C1 coexistence — `SustainFailureAndAccidental_DwellForgivingAndTighteningCoexist` (707 = 700 − 28 + 35; tightening not swallowed). ✓
- F4 (below-gate evidence never drives) — `GatedScore` returns 0 for ungated modes; `UngatedMode_DoesNotDrive`, `AccidentalUngated_DoesNotTighten`. ✓
- §9.1 bounded churn — `UpdateKnob` pushes `SetMomentaryFill` (and thus `Changed`/any rebuild) only when a fill changes; at rest and in the deadband it is silent. ✓
- **Deferred (documented, not gaps):** DI registration + ticking + reset wiring + enable-gating the ticking (1b-⑤); observability of dwell/smoothing/geometry (their routing slices); the drift "report" half (1b-⑤); fatigue amplification (already in the assessment's aggregate, 1b-③c); the learned layer (Phase 2).

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `AdaptationController(EffectiveSettingsProvider, AdaptationSettings)`, `Tick(StruggleAssessment, long)`, `Reset()` match the tests' calls. `_provider.SetMomentaryFill(AdaptationKnob, DwellContext, double, double)` + `ClearMomentaryDeltas()` match 1b-④a; `p.Effective(AdaptationKnob, DwellContext, double)` matches too. `PosturePresets.For(_adaptation.Posture)` returns `PostureTuning` with the fields `RelaxThreshold`/`SustainSeconds`/`RelaxStepFraction`/`RecoverThreshold`/`RecoverStepFraction` (1b-②). `assessment.ForMode(FailureMode)` returns `FailureModeAssessment?` with `GatePassed`/`Score` (1b-③a). `Mode(...)`/`Assess(...)` build `FailureModeAssessment`/`StruggleAssessment` in the 1b-③a positional order.

**Verified facts relied on (from source / committed plans):**
- Conservative posture `(RelaxThreshold 0.50, SustainSeconds 3, RelaxStepFraction 0.10, RecoverThreshold 0.25, RecoverStepFraction 0.05)` — the default `AdaptationSettings.Posture` (1b-②/§13.1); every test's arithmetic (52.5, 55, 53.75, 672, 4.75, 105, 735, 707, 770, 752.5) is computed from these + the 1b-④a band fractions (tolerance 0.5, dwell shorter 0.4 / longer 0.5, smoothing 0.5, geometry 0.5) against the supplied baselines.
- The band directions/edges are resolved by the 1b-④a provider (tolerance +, dwell −shorter/+longer, smoothing −, geometry +); the controller supplies only fills, so the direction signs live in one place (the provider `KnobPolicy`).
- `ref s.ForgivingRelaxSince` passes a mutable instance field of the `KnobState` reference type by ref — legal in C#.
- `EyeCtrl.Core.csproj` `ImplicitUsings=enable` + `WarningsAsErrors=CS1591`; NFluent `IsCloseTo(expected, delta)` for arithmetic, `IsEqualTo`/`IsNotEqualTo` for exact/baseline.

**Performance (design §9/§9.1):** `Tick` runs at the controller tick (a few Hz, dispatcher), **not** the gaze hot path. Per tick it does four `UpdateKnob` calls, each a couple `ForgivingDrive`/`GatedScore` dictionary lookups + a small `params FailureMode[]` allocation (1–2 elements) — negligible and off the per-sample path (§9.1's zero-per-sample-allocation budget governs 1a's gaze aggregation, not this tick). The push-on-change guard keeps `Changed` (and the tolerance-knob fixation-detector rebuild, 1b-①) firing at most once per changed knob per tick.

**Honest testability limits:** the controller is a pure, framework-agnostic Core component, fully headless-unit-tested (17 tests) by ticking synthetic `StruggleAssessment` sequences with a supplied `now` and reading `provider.Effective(knob, Other, baseline)`. The stepping engine (relax/sustain/recover/deadband/hysteresis), the full §7.1 mode→knob map (each mode's footprint + its "NOT" knobs), the quarantined tightening + C1 coexistence, F4 gating, per-knob and master enable-gating, and `Reset` are all asserted directly. **Only the tolerance knob is verified end-to-end through a real consumer** (`GazeEngine`, via 1b-①/④a); dwell/smoothing/geometry are verified at the provider composition level because they have no consumer until their routing slices. Not exercised here (correctly, per scope): the coordinator's real-time ticking / `Reset` wiring / enable-gating of the tick loop (1b-⑤). Posture presets and band fractions are the §13.1 starting values (1b-②); the tests assert the stepping/composition *structure*, not field-calibrated constants.

**Deferred (documented above, not gaps):** DI + ticking + reset/enable wiring (1b-⑤); the drift report (1b-⑤); dwell/smoothing/geometry consumer routing (their slices); the learned layer + persistence (Phase 2).
