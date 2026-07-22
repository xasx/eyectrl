# Struggle Detection — Phase 1b-④a Knob Expansion & Fill-Fraction Provider — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the `EffectiveSettingsProvider` value model so the Phase-1b `AdaptationController` (plan 1b-④b) can drive **all four §5 knob families** — expand `AdaptationKnob` to `{ FixationRadius, DwellTime, Smoothing, TargetGeometry }`, evolve the provider's momentary store from **absolute deltas to dimensionless band-fill fractions** (resolved to `fraction × baseline` at read time, so the multi-baseline dwell knob and the runtime-derived geometry knob compose correctly per consumer), and **migrate the band source** from the transitional 1b-① `KnobPolicy` constants to the `AdaptationSettings` fractions (1b-②). **Only `FixationRadius` is observably routed today** (via `GazeEngine`, from 1b-①); `DwellTime`/`Smoothing`/`TargetGeometry` fills are stored-but-unrouted until their routing slices — honestly documented. **Zero behaviour change** while `AdaptationSettings.Enabled == false` (the locked exit invariant: effective == baseline).

**Architecture:** The controller (1b-④b) will step a **fill fraction `∈ [0, 1]`** per knob/direction (§7.2 "a fraction of the band") and call `SetMomentaryFill(knob, ctx, forgivingFill, tighteningFill)`; the provider resolves the absolute delta at `Effective(knob, ctx, baseline)` read time as `fill × sign × bandFraction(AdaptationSettings) × baseline`, then composes through the pure `EffectiveValue.Compute` (1a-⑤). Resolving the baseline at **read time** (where each consumer knows its own baseline) is what lets one global fill drive a knob with many per-surface baselines (dwell) or a runtime-derived one (geometry) — the very reason those knobs' routing was deferred. This **replaces** 1b-①'s absolute-delta `SetMomentaryDelta`; the committed 1b-① provider/`GazeEngine` tests all saturated their bands, so they stay green after a mechanical rewording to the fill API.

**Tech Stack:** C# / .NET 10; `EyeCtrl.Core` (framework-agnostic); xUnit v3 + NFluent (`Check.That(...)`, `IsCloseTo(expected, delta)` for division/arithmetic results, `IsEqualTo` only for exact or clamp-saturated values).

**Source of truth:** design spec `docs/superpowers/specs/2026-07-19-struggle-detection-self-adaptation-design.md` — §5 (value model: `effective = baseline + clamped per-direction sub-bands`; zero-delta ⇒ exact baseline; typed per-knob directions/units; the four knob families), §7.1 (mode→transform directions), §8.3/§8.4 (zero-delta ⇒ exact baseline; sub-bands strictly inside validated ranges), §13.1 (fixed safety envelope; dwell shorter −40 %, dwell longer +50 %, tolerance +50 %, smoothing "more" = param **down**, geometry +50 %).

**Depends on:** **Phase 1a + 1b-① + 1b-② implemented.** From 1a-⑤: `EffectiveValue.Compute(...)`. From 1b-①: `EffectiveSettingsProvider` (absolute-delta form), `AdaptationKnob` (= `{ FixationRadius }`), and its two committed test files. From 1b-②: `AdaptationSettings` band fractions + per-knob enables. Written against the **post-1b-①/② source**. **No dependency on 1b-③/④b.**

**Executor pre-flight (do this FIRST — MANDATORY; 1a/1b-①/② are plans-only, not yet in the tree):** confirm these against the *merged* source before writing any code — a shape mismatch breaks the tasks silently:
1. `EffectiveValue.Compute(double baseline, double forgivingDelta, double tighteningDelta, (double Min, double Max) forgivingBand, (double Min, double Max) tighteningBand, double validMin, double validMax)` returning `double`, with the zero-delta ⇒ exact-baseline short-circuit (1a-⑤).
2. `EffectiveSettingsProvider` (1b-①, `EyeCtrl.Core.Adaptation`, `sealed`): ctor `EffectiveSettingsProvider(AdaptationSettings)`, `event Action? Changed`, `double Effective(AdaptationKnob, DwellContext, double baseline)`, `void SetMomentaryDelta(AdaptationKnob, DwellContext, double forgiving, double tightening)`, `void ClearMomentaryDeltas()`, and an internal `KnobPolicy` record struct + static `Policies` dictionary (FixationRadius only). **This plan replaces `SetMomentaryDelta` with `SetMomentaryFill` and the store's value type with fills** — confirm the current names before rewording the tests.
3. `AdaptationKnob` (1b-①) currently declares exactly one member, `FixationRadius`.
4. `AdaptationSettings` (1b-②) exposes `double` band fractions `ToleranceLooserMaxFraction` (0.50), `DwellShorterMaxFraction` (0.40), `DwellLongerMaxFraction` (0.50), `SmoothingMoreMaxFraction` (0.50), `GeometryBiggerMaxFraction` (0.50), plus `Enabled` (bool) — reachable as `EyeCtrlSettings.Adaptation`.
5. `DwellContext` (1a-①) includes `Other`.
6. The committed 1b-① test files `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` and `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs` exist and call `SetMomentaryDelta`. (Task 2 rewrites the former in full and reworders the latter's two call sites.)
7. **Pre-existing (verified against real source, not a plan):** `DwellSettings.FixationRadiusPixels` guard 10–200 (`DwellSettings.cs:102-109,173`); the dwell-time settings ranges are 200–2000 (nine of them) and 100–1500 (keyboard key + prediction) → **union [100, 2000]** (`DwellSettings.cs:11-37`); `CursorSettings.OneEuroMinCutoff` 0.1–20 and `SmoothingFactor` 0–1 → **union [0, 20]** (`CursorSettings.cs:11-14,34-50`).

If any differ (renamed members, different fraction defaults, different guards), adjust before proceeding.

**⚠️ Core enforces XML docs as build errors:** `src/EyeCtrl.Core/EyeCtrl.Core.csproj` sets `<GenerateDocumentationFile>true</GenerateDocumentationFile>` + `<WarningsAsErrors>CS1591</WarningsAsErrors>`. Every public member below — **including every enum member** and every public method's `<param>`/`<returns>` — is documented. `EyeCtrl.Core` has `ImplicitUsings=enable` (so `System` — `Math`, `Func`, `Action` — and `System.Collections.Generic` — `Dictionary`, `IReadOnlyDictionary` — need no `using`); the provider file adds only `using EyeCtrl.Core.Settings;`. Do not strip the doc comments.

---

## Scope boundary (explicit — deferred pieces noted where they land)

This slice completes the **value-model plumbing**. It does **not** contain the controller (1b-④b) and does not route the three new knobs. Verified deferrals:

- **The `AdaptationController`** (consumes `StruggleAssessment`; steps the fills via relax/recover/deadband; the quarantined dwell tightening) → **1b-④b**. This slice supplies the provider surface it pushes into.
- **Routing `DwellTime` / `Smoothing` / `TargetGeometry` to consumers.** Only the tolerance knob is read by a Core consumer today (`GazeEngine._fixationDetector`, from 1b-①). The other three fills are stored and composable via `Effective(knob, ctx, baseline)` but **nothing calls that** for them yet — dwell routing (~20 creation sites), smoothing routing (the filter rebuild reading the effective `OneEuroMinCutoff`/`SmoothingFactor`), and cursor/geometry routing are each their own later slice. The fills are honest no-ops until then.
- **Per-consumer / per-mode / runtime validated ranges for the three new knobs.** This slice gives them **provider-side superset ranges** (dwell = the union of the 11 dwell-time guards `[100, 2000]`; smoothing = the union of both filter ranges `[0, 20]`; geometry = unbounded above, `[0, double.MaxValue]`, since it has **no** pre-existing validated range — §8.4 for geometry stays deferred exactly as 1b-① stated). Each knob's precise range (per dwell surface; per active filter mode; the runtime-derived geometry bound) is re-applied by its routing slice. The fill × fraction already bounds the delta well inside these supersets, so the superset clamp is a safety net, not the operative bound.
- **`FixationDetectionMs` is still NOT an adaptation knob** (§13.1 gives no fixation-*time* band) — unchanged from 1b-①.

`AdaptationKnob` reaches its **complete** §5 set here (the four knob families), because the controller (1b-④b) routes every failure mode to a knob and needs the full enum; the incremental part is which knobs are *observably routed to consumers*, not which exist.

---

## File Structure

**Modify (Core):**
- `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs` — add `DwellTime`, `Smoothing`, `TargetGeometry` (Task 1).
- `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` — fill-fraction store + `SetMomentaryFill` + band-source migration + full `KnobPolicy` table (Task 2).

**Test (modify):**
- `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationKnobTests.cs` — **create** (Task 1): locks the four-knob set.
- `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` — **rewrite** to the fill API (Task 2).
- `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs` — reword its two `SetMomentaryDelta` call sites (Task 2).

**Not touched:** `src/EyeCtrl.Core/GazeEngine.cs` — it reads tolerance via `Effective(FixationRadius, Other, baseline)`, whose signature is unchanged; only the *set* side changes, and `GazeEngine` never sets deltas (the controller does, in 1b-④b). `src/EyeCtrl.App/Services/AppBootstrapper.cs` — the provider ctor signature `EffectiveSettingsProvider(AdaptationSettings)` is unchanged, so the 1b-① DI registration still resolves.

**Zero-behaviour-change guarantee:** with `AdaptationSettings.Enabled == false`, `Effective` short-circuits to the exact baseline (unchanged from 1b-①). The three new knobs have no consumer, so their fills are inert. The one intended, observable path (tolerance, `Enabled == true`) is behaviourally identical to 1b-① (the committed tests saturated their bands; fill `1.0` reproduces the same edge). No production wiring changes.

---

## Task 1: Expand `AdaptationKnob` to the four knob families

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationKnobTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `tests/EyeCtrl.Core.Tests/Adaptation/AdaptationKnobTests.cs`:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class AdaptationKnobTests
{
    [Fact]
    public void DefinesTheFourKnobFamilies()
    {
        // Locks the complete §5 knob set so the controller (1b-④b) and the provider policy table can
        // rely on it. These four are the whole set — further growth is not expected; routing (not new
        // knobs) is what lands incrementally.
        var knobs = Enum.GetValues<AdaptationKnob>();
        Check.That(knobs).HasSize(4);
        Check.That(knobs).Contains(
            AdaptationKnob.FixationRadius,
            AdaptationKnob.DwellTime,
            AdaptationKnob.Smoothing,
            AdaptationKnob.TargetGeometry);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationKnobTests"`
Expected: FAIL to **compile** — `DwellTime`, `Smoothing`, `TargetGeometry` do not exist.

- [ ] **Step 3: Add the three knob members**

Replace the body of `src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs` (keep the namespace) with:

```csharp
namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// Identifies an adaptable setting ("knob") the <see cref="EffectiveSettingsProvider"/> composes a
/// bounded momentary fill onto. Each knob has its own safe direction, units, and safety band
/// (design §5/§13.1). All four §5 knob families are declared; only <see cref="FixationRadius"/> is
/// routed to a Core consumer today (via <c>GazeEngine</c>, plan 1b-①) — the others are composable but
/// unrouted until their routing slices land.
/// </summary>
public enum AdaptationKnob
{
    /// <summary>Fixation-detection hit radius in physical pixels — the tolerance knob (forgiving = looser = larger). Routed via <c>GazeEngine</c>.</summary>
    FixationRadius,

    /// <summary>Dwell activation time in milliseconds (forgiving = shorter; the one quarantined tightening = longer, for accidental activation). Unrouted until the dwell-routing slice.</summary>
    DwellTime,

    /// <summary>The active gaze filter's smoothing parameter (forgiving = more smoothing = parameter DOWN for both filters). Unrouted until the smoothing-routing slice.</summary>
    Smoothing,

    /// <summary>Target ring/hit radius and its coupled visible size in physical pixels (forgiving = bigger). Unrouted until the geometry-routing slice.</summary>
    TargetGeometry,
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~AdaptationKnobTests"`
Expected: PASS (1 test). No `CS1591`. The existing `EffectiveSettingsProviderTests` (1b-①) still pass — the provider's `Policies` table has only a `FixationRadius` entry, and the new members are unreferenced by it, so `Effective` for a new knob hits the defensive baseline fallback (harmless; Task 2 adds the policies).

- [ ] **Step 5: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/AdaptationKnob.cs tests/EyeCtrl.Core.Tests/Adaptation/AdaptationKnobTests.cs
git commit -m "feat(adaptation): expand AdaptationKnob to the four knob families"
```

---

## Task 2: Evolve `EffectiveSettingsProvider` to fill-fractions + band-source migration

Replace the momentary store's value type (absolute deltas → fills `∈ [0, 1]`), replace `SetMomentaryDelta` with `SetMomentaryFill`, source the band **fractions** from `AdaptationSettings` (band-source migration), and give all four knobs a `KnobPolicy` (signed direction + fraction source + validated range). Rewrite the provider tests to the fill API and reword `GazeEngineFixationRebuildTests`' two call sites so the whole suite stays green.

**Files:**
- Modify: `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs`
- Test: `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` (rewrite), `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs` (reword 2 calls)

- [ ] **Step 1: Rewrite the provider tests to the fill API**

Replace the entire contents of `tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs` with:

```csharp
namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class EffectiveSettingsProviderTests
{
    // Returns the provider AND its settings so tests can mutate band fractions (band-source migration).
    private static (EffectiveSettingsProvider Provider, AdaptationSettings Settings) Make(bool enabled)
    {
        var settings = new AdaptationSettings { Enabled = enabled };
        return (new EffectiveSettingsProvider(settings), settings);
    }

    [Fact]
    public void Disabled_ReturnsBaseline_EvenWithFillSet()
    {
        var (p, _) = Make(enabled: false);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Enabled_NoFill_ReturnsBaseline()
    {
        var (p, _) = Make(enabled: true);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void ZeroFill_ReturnsExactBaseline_EvenWhenBaselineOutOfRange()
    {
        // Locked exit invariant (§8.3): zero fill ⇒ exact baseline, even a baseline above the validated
        // max — the validated-range clamp only engages when the delta is non-zero (EffectiveValue, 1a-⑤).
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.0, 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 5000)).IsEqualTo(5000.0);
    }

    [Fact]
    public void Tolerance_HalfForgivingFill_MovesHalfTheBand()
    {
        // Tolerance forgiving is +looser: band edge = +ToleranceLooserMaxFraction(0.50) × baseline(50) = +25.
        // Half fill → +12.5 → 62.5. Proves intermediate (sub-saturation) stepping composes correctly.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsCloseTo(62.5, 1e-9);
    }

    [Fact]
    public void Tolerance_FillOverOne_SaturatesAtBandEdge()
    {
        // Fill > 1 is clamped by the sub-band to the edge (+25) → 75. The controller (1b-④b) clamps fills
        // to [0, 1]; the provider's band clamp is the safety net.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 5.0, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(75.0);
    }

    [Fact]
    public void Tolerance_NeverExceedsValidatedMax()
    {
        // baseline 190, full fill → +95 = 285, clamped to the validated max 200.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 190)).IsEqualTo(200.0);
    }

    [Fact]
    public void Dwell_ForgivingShortens()
    {
        // Dwell forgiving is −shorter: band edge = −DwellShorterMaxFraction(0.40) × baseline(700) = −280.
        // Full fill → 700 − 280 = 420.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, baseline: 700)).IsCloseTo(420.0, 1e-9);
    }

    [Fact]
    public void Dwell_ForgivingAndTightening_Coexist_TighteningNotSwallowed()
    {
        // C1 at the provider level (§5/§8.2): dwell forgiving (shorter, −) and tightening (longer, +) are
        // clamped in independent sub-bands and summed, so the bounded tightening is never swallowed by the
        // forgiving offset. forgiving edge −280; tightening edge +DwellLongerMaxFraction(0.50)×700 = +350.
        // Full both → 700 − 280 + 350 = 770.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 1.0);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, baseline: 700)).IsCloseTo(770.0, 1e-9);
    }

    [Fact]
    public void Smoothing_MoreSmoothing_DecreasesParameter()
    {
        // Smoothing forgiving is −more (parameter DOWN for both filters, §13.1). OneEuro baseline 5.0;
        // full fill → −SmoothingMoreMaxFraction(0.50) × 5 = −2.5 → 2.5.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, baseline: 5.0)).IsCloseTo(2.5, 1e-9);
    }

    [Fact]
    public void Geometry_Bigger_IncreasesSize()
    {
        // Geometry forgiving is +bigger. baseline 100; full fill → +GeometryBiggerMaxFraction(0.50)×100 = +50 → 150.
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, baseline: 100)).IsCloseTo(150.0, 1e-9);
    }

    [Fact]
    public void BandSourceMigration_ToleranceFractionReadFromSettings_IsLive()
    {
        // Band-source migration: the provider reads AdaptationSettings.ToleranceLooserMaxFraction live (not
        // a baked constant). Narrowing it to 0.25 halves the band → full fill gives +12.5, not +25.
        var (p, s) = Make(enabled: true);
        s.ToleranceLooserMaxFraction = 0.25;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsCloseTo(62.5, 1e-9);
    }

    [Fact]
    public void ClearMomentaryDeltas_RestoresBaseline()
    {
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        p.ClearMomentaryDeltas();
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void SetAndClear_RaiseChanged()
    {
        var (p, _) = Make(enabled: true);
        int changed = 0;
        p.Changed += () => changed++;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        p.ClearMomentaryDeltas();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void FixationRadius_EffectiveStaysWithinDwellSettingsValidatedRange()
    {
        // Drift guard (from 1b-①, updated to the fill API): KnobPolicy hardcodes the fixation-radius
        // validated MAX (200) — a provider-owned constant NOT migrated to AdaptationSettings — duplicating
        // DwellSettings.FixationRadiusPixels's guard. Use that setter as the ORACLE: if the provider's
        // ValidMax ever drifts above the settings guard, assigning the effective value back throws.
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var p = new EffectiveSettingsProvider(settings.Adaptation);
        settings.Dwell.FixationRadiusPixels = 200; // top of the validated range
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 5.0, tighteningFill: 0.0);
        double eff = p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, settings.Dwell.FixationRadiusPixels);

        bool threw = false;
        try { settings.Dwell.FixationRadiusPixels = eff; }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check.That(threw).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveSettingsProviderTests"`
Expected: FAIL to **compile** — `SetMomentaryFill` does not exist (the provider still has `SetMomentaryDelta`), and the `DwellTime`/`Smoothing`/`TargetGeometry` compositions have no policy yet.

- [ ] **Step 3: Rewrite the provider**

Replace the entire contents of `src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs` with:

```csharp
namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Settings;

/// <summary>
/// Stateful composition layer over the pure <see cref="EffectiveValue"/> calculator. Holds a momentary
/// per-<c>(knob, context)</c> <b>fill fraction</b> (a dimensionless band position in <c>[0, 1]</c>) the
/// Phase-1b <c>AdaptationController</c> (plan 1b-④b) pushes in, applies the
/// <see cref="AdaptationSettings.Enabled"/> gate, and exposes the effective value each consumer reads.
/// The absolute delta is resolved <b>at read time</b> as <c>fill × sign × bandFraction × baseline</c>
/// (band fractions sourced live from <see cref="AdaptationSettings"/>, so the multi-baseline dwell knob
/// and the runtime-derived geometry knob each compose against the caller's own baseline). When adaptation
/// is disabled or no non-zero fill is stored, <see cref="Effective"/> returns the exact baseline — the
/// locked "disabled ⇒ effective == baseline" invariant (design §8.3).
/// Not thread-safe for concurrent writes: fills are set on the serialized controller/dispatcher thread.
/// In this slice the only reader (<c>GazeEngine.RebuildFixationDetector</c>, plan 1b-①) runs on that same
/// thread; the other three knobs are composable but have no consumer yet (design §9).
/// </summary>
public sealed class EffectiveSettingsProvider
{
    // §13.1 fixed safety envelope. A knob's forgiving/tightening MAGNITUDE (a fraction of baseline) is
    // sourced live from AdaptationSettings (the band-source migration, 1b-④a); the SIGN encodes the knob's
    // safe direction (§5 typed transforms); ValidMin/ValidMax stay provider-owned constants (NOT migrated).
    //
    // ⚠️ FixationRadius's ValidMin/ValidMax DUPLICATE DwellSettings.FixationRadiusPixels's guard (10–200);
    // the Task-2 drift-guard test uses that setter as the oracle so a divergence fails the build. The three
    // unrouted knobs get SUPERSET validated ranges (dwell = union of the 11 dwell-time guards [100, 2000];
    // smoothing = union of both filter ranges [0, 20]; geometry = unbounded above, since geometry has NO
    // pre-existing validated range — §8.4 for geometry stays deferred). The per-consumer/mode/runtime range
    // is re-applied by each knob's routing slice; here the fraction clamp is the operative bound and the
    // superset is only a safety net. A knob reaches this table only when it has a stored fill (see Effective).
    private readonly record struct KnobPolicy(
        double ForgivingSign,
        Func<AdaptationSettings, double> ForgivingFraction,
        double TighteningSign,
        Func<AdaptationSettings, double>? TighteningFraction,
        double ValidMin,
        double ValidMax);

    private static readonly IReadOnlyDictionary<AdaptationKnob, KnobPolicy> Policies =
        new Dictionary<AdaptationKnob, KnobPolicy>
        {
            // Tolerance: forgiving = looser = larger (+); no tightening. Range 10–200 (DwellSettings guard).
            [AdaptationKnob.FixationRadius] = new(
                +1.0, static a => a.ToleranceLooserMaxFraction, 0.0, null, 10.0, 200.0),
            // Dwell: forgiving = shorter (−); the one quarantined tightening = longer (+). Range = union of
            // the 11 dwell-time guards [100, 2000] (per-surface ranges re-applied at dwell routing).
            [AdaptationKnob.DwellTime] = new(
                -1.0, static a => a.DwellShorterMaxFraction, +1.0, static a => a.DwellLongerMaxFraction, 100.0, 2000.0),
            // Smoothing: forgiving = more = parameter DOWN (−); no tightening. Range = union of both filter
            // ranges [0, 20] (per-mode range re-applied at smoothing routing). Discharges 1b-②'s flagged
            // "revisit fraction-vs-absolute" question: ④ ADOPTS fraction-of-baseline — the uniform fill
            // model applies cleanly (OneEuro baseline 5 → 2.5; EMA 0.5 → 0.25, both in-range) and needs no
            // per-filter special-casing here; a mode-aware absolute per-filter delta, if ever needed, is a
            // smoothing-routing concern (this knob is unrouted, so the model choice is not yet load-bearing).
            [AdaptationKnob.Smoothing] = new(
                -1.0, static a => a.SmoothingMoreMaxFraction, 0.0, null, 0.0, 20.0),
            // Geometry: forgiving = bigger (+); no tightening. No pre-existing validated range (runtime-
            // derived) → unbounded above; the +fraction band is the operative bound (§8.4 deferred, per 1b-①).
            // The absolute geometry bound §13.1 expects "Phase 1b" to introduce lands with the GEOMETRY-ROUTING
            // slice, which supplies both the runtime-derived baseline and the first consumer — it cannot be
            // validated before then. This knob is unrouted here, so the fraction bounds the (no-op) delta.
            [AdaptationKnob.TargetGeometry] = new(
                +1.0, static a => a.GeometryBiggerMaxFraction, 0.0, null, 0.0, double.MaxValue),
        };

    private readonly AdaptationSettings _adaptation;
    private readonly Dictionary<(AdaptationKnob Knob, DwellContext Context), (double ForgivingFill, double TighteningFill)> _fills = new();

    /// <summary>Raised whenever a momentary fill is set or cleared, so consumers can re-read effective values.</summary>
    public event Action? Changed;

    /// <summary>Initializes the provider with the master adaptation settings (the enable gate + band-fraction source).</summary>
    /// <param name="adaptation">The adaptation settings whose <see cref="AdaptationSettings.Enabled"/> gates composition and whose band fractions bound each knob.</param>
    public EffectiveSettingsProvider(AdaptationSettings adaptation) => _adaptation = adaptation;

    /// <summary>
    /// Returns the effective value for <paramref name="knob"/> in <paramref name="context"/>, resolving the
    /// stored fill fraction to an absolute delta (<c>fill × sign × bandFraction × baseline</c>) and composing
    /// it onto <paramref name="baseline"/> via <see cref="EffectiveValue.Compute"/>. Returns the exact
    /// <paramref name="baseline"/> when adaptation is disabled or no non-zero fill is stored.
    /// </summary>
    /// <param name="knob">The knob being read.</param>
    /// <param name="context">The interaction context; use <see cref="DwellContext.Other"/> for the global momentary layer.</param>
    /// <param name="baseline">The consumer's baseline value for the knob (its own, so the fill resolves per-consumer).</param>
    /// <returns>The composed, clamped effective value, or the baseline.</returns>
    public double Effective(AdaptationKnob knob, DwellContext context, double baseline)
    {
        if (!_adaptation.Enabled)
            return baseline;
        if (!_fills.TryGetValue((knob, context), out var f) || (f.ForgivingFill == 0.0 && f.TighteningFill == 0.0))
            return baseline;
        if (!Policies.TryGetValue(knob, out var p))
            return baseline; // defensive (design §11): a fill for a knob without a policy falls back to baseline

        // Signed band edges resolved against the caller's baseline. fill ∈ [0, 1] keeps the delta within the
        // band; EffectiveValue.Compute re-clamps (the C1 safety net + any fill > 1) then applies the range.
        double forgivingEdge = p.ForgivingSign * p.ForgivingFraction(_adaptation) * baseline;
        double tighteningEdge = p.TighteningFraction is null ? 0.0 : p.TighteningSign * p.TighteningFraction(_adaptation) * baseline;

        return EffectiveValue.Compute(
            baseline,
            f.ForgivingFill * forgivingEdge,
            f.TighteningFill * tighteningEdge,
            (Math.Min(0.0, forgivingEdge), Math.Max(0.0, forgivingEdge)),
            (Math.Min(0.0, tighteningEdge), Math.Max(0.0, tighteningEdge)),
            p.ValidMin, p.ValidMax);
    }

    /// <summary>
    /// Sets the momentary band-fill fractions for a knob/context and raises <see cref="Changed"/>. Called by
    /// the Phase-1b controller (and tests). Fills are the dimensionless band position in <c>[0, 1]</c>; the
    /// absolute delta and its sub-band clamp are resolved in <see cref="Effective"/> against the consumer's
    /// baseline.
    /// </summary>
    /// <param name="knob">The knob.</param>
    /// <param name="context">The context (<see cref="DwellContext.Other"/> for the global momentary layer).</param>
    /// <param name="forgivingFill">Forgiving band-fill fraction toward "easier" (expected in <c>[0, 1]</c>; over-range values are clamped at read time).</param>
    /// <param name="tighteningFill">Tightening band-fill fraction (non-zero only for the dwell knob under accidental activation; zero for forgiving-only knobs).</param>
    public void SetMomentaryFill(AdaptationKnob knob, DwellContext context, double forgivingFill, double tighteningFill)
    {
        _fills[(knob, context)] = (forgivingFill, tighteningFill);
        Changed?.Invoke();
    }

    /// <summary>Clears every momentary fill (toolbar reset / session boundary) and raises <see cref="Changed"/> if any were present.</summary>
    public void ClearMomentaryDeltas()
    {
        if (_fills.Count == 0)
            return;
        _fills.Clear();
        Changed?.Invoke();
    }
}
```

- [ ] **Step 4: Reword the two `GazeEngineFixationRebuildTests` call sites**

In `tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs`, the two tests that simulate a controller delta call `SetMomentaryDelta`. Update both to the fill API (fill `1.0` reproduces the old `forgiving: 100` saturation — both push past the `+50 %` band edge, so the observed effective radius is identical).

In `AdaptationDelta_LoosensToleranceLive_WhenEnabled`, change:

```csharp
        // Controller-style forgiving delta: +100 clamps to +50% (=15) → effective radius 45 >= 40.
        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
```

to:

```csharp
        // Controller-style full forgiving fill → +50% band edge (=15) → effective radius 45 >= 40.
        effective.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
```

In `AdaptationDelta_HasNoEffect_WhenDisabled`, change:

```csharp
        effective.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 100, tightening: 0);
```

to:

```csharp
        effective.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
```

- [ ] **Step 5: Run the provider + GazeEngine suites**

Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~EffectiveSettingsProviderTests"`
Expected: PASS (14 tests). If any `CS1591` error appears, a doc comment was dropped — restore it.
Run: `dotnet test tests/EyeCtrl.Core.Tests --filter "FullyQualifiedName~GazeEngineFixationRebuildTests"`
Expected: PASS (4 tests) — the tolerance rebuild behaviour is unchanged; only the delta→fill call reworded.

- [ ] **Step 6: Full build + headless suites (confirm zero regressions)**

Run: `dotnet build EyeCtrl.slnx`
Run: `dotnet test tests/EyeCtrl.Core.Tests`
Run: `dotnet test tests/EyeCtrl.App.Tests --filter "Category!=UI"`
Expected: PASS. The provider ctor signature is unchanged, so the 1b-① DI registration in `AppBootstrapper` still resolves; `GazeEngine` reads tolerance through `Effective` (unchanged signature). Everything else is net-new or a mechanical rename.

- [ ] **Step 7: Commit**

```bash
git add src/EyeCtrl.Core/Adaptation/EffectiveSettingsProvider.cs tests/EyeCtrl.Core.Tests/Adaptation/EffectiveSettingsProviderTests.cs tests/EyeCtrl.Core.Tests/GazeEngineFixationRebuildTests.cs
git commit -m "feat(adaptation): fill-fraction provider + band-source migration for all four knobs"
```

---

## Self-Review

**Spec coverage (this slice):**
- §5 four knob families — `AdaptationKnob` = `{ FixationRadius, DwellTime, Smoothing, TargetGeometry }` (Task 1). ✓
- §5 typed per-knob directions/units — `KnobPolicy` signs: tolerance +looser, dwell −shorter / +longer, smoothing −more (param down), geometry +bigger; verified by `Dwell_ForgivingShortens`, `Smoothing_MoreSmoothing_DecreasesParameter`, `Geometry_Bigger_IncreasesSize`, `Tolerance_HalfForgivingFill_MovesHalfTheBand`. ✓
- §5/§8.2 sub-band separation (C1) at the provider level — `Dwell_ForgivingAndTightening_Coexist_TighteningNotSwallowed` (independent sub-band clamps summed). ✓
- §8.3 zero-delta / disabled ⇒ exact baseline — `Effective` short-circuits; `ZeroFill_ReturnsExactBaseline_EvenWhenBaselineOutOfRange`, `Disabled_ReturnsBaseline_EvenWithFillSet`. ✓
- §8.4 sub-bands strictly inside validated ranges — the fraction bound + the `EffectiveValue` validated-range clamp; `Tolerance_NeverExceedsValidatedMax`. Geometry's §8.4 remains deferred (unbounded provider range, documented), consistent with 1b-①. ✓
- §13.1 band-source migration — the fractions are read live from `AdaptationSettings` (not baked constants): `BandSourceMigration_ToleranceFractionReadFromSettings_IsLive`; defaults reproduce the 1b-① numbers, so the drift-guard stays green. ✓
- §7.2 "a fraction of the band" — the store holds a fill `∈ [0, 1]`; `SetMomentaryFill` + read-time resolution let the controller (1b-④b) step baseline-agnostically. ✓

**Placeholder scan:** none — every step has full code and exact commands.

**Type consistency:** `SetMomentaryFill(AdaptationKnob, DwellContext, double forgivingFill, double tighteningFill)`, `Effective(AdaptationKnob, DwellContext, double)`, `ClearMomentaryDeltas()`, `event Action Changed`, ctor `EffectiveSettingsProvider(AdaptationSettings)` are used identically in `EffectiveSettingsProviderTests`, the reworded `GazeEngineFixationRebuildTests`, and (next slice) the controller. `EffectiveValue.Compute(double, double, double, (double,double), (double,double), double, double)` matches its 1a-⑤ definition. The `KnobPolicy` fraction delegates read the `AdaptationSettings` members confirmed in the pre-flight (`ToleranceLooserMaxFraction`, `DwellShorterMaxFraction`, `DwellLongerMaxFraction`, `SmoothingMoreMaxFraction`, `GeometryBiggerMaxFraction`).

**Verified facts relied on (from source):**
- `DwellSettings.FixationRadiusPixels` guard 10–200 (`DwellSettings.cs:102-109,173`); the nine 200–2000 dwell-time guards + the two 100–1500 keyboard guards → union `[100, 2000]` (`DwellSettings.cs:11-37`).
- `CursorSettings.OneEuroMinCutoff` 0.1–20, `SmoothingFactor` 0–1 → union `[0, 20]` (`CursorSettings.cs:11-14,43-50`); more smoothing = parameter DOWN for both filters (verified against `SmoothedFilter.cs`/`OneEuroFilter.cs` in the 1b design work).
- `EyeCtrl.Core.csproj`: `ImplicitUsings=enable`, `GenerateDocumentationFile=true` + `WarningsAsErrors=CS1591`; `InternalsVisibleTo EyeCtrl.Core.Tests` (not relied on — the types are public). NFluent `IsCloseTo(expected, delta)` for arithmetic results, `IsEqualTo` for exact/clamp-saturated.
- The 1b-①/② surfaces (`EffectiveSettingsProvider`, `AdaptationKnob`, `AdaptationSettings`, `EffectiveValue`) are consumed as defined in those committed plans, with the pre-flight above.

**Honest testability limits:** the provider is fully headless-unit-tested (14 tests) across all four knobs' compositions, directions, the exit invariant, the C1 coexistence at the provider level, and the band-source migration. **Only `FixationRadius` is verified end-to-end** through a consumer (the reworded `GazeEngineFixationRebuildTests`, via the interaction state machine — from 1b-①); `DwellTime`/`Smoothing`/`TargetGeometry` are verified only at the `Effective(knob, ctx, baseline)` composition level, because they have no consumer until their routing slices. Their provider-side validated ranges are documented supersets (dwell/smoothing) or unbounded (geometry) — the per-consumer/mode/runtime range is re-applied at routing; the fraction bound is the operative limit here, so the superset never over-clamps a realistic baseline (a full-fill dwell shorten reaches 0.6× baseline, a smoothing "more" reaches 0.5× baseline, both well inside the supersets).

**Deferred (documented above, not gaps):** the `AdaptationController` (1b-④b); routing the three new knobs to consumers + their precise per-consumer/mode/runtime ranges (later slices); `FixationDetectionMs` is not a knob.
