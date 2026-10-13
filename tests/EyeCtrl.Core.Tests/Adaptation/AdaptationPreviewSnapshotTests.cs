namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Task 3: the pure preview builder. The row FILL composition mirrors the provider's fill model
/// (spec §3.4) — clamp(learned + momentary) per direction, the dominant side naming the dwell
/// direction — while the absolute columns must be exactly what the engine would run:
/// <see cref="EffectiveSettingsProvider.Effective"/> for tolerance and
/// <see cref="EffectiveSmoothing.Compose"/> for smoothing, the latter asserted here as a direct
/// oracle call. The disabled case pins the locked §8.3 invariant through the builder: fills still
/// stored in the provider, every row reported zero.
/// </summary>
public class AdaptationPreviewSnapshotTests
{
    // One fresh (settings, provider) set per case — no shared fixture (repo convention). The provider
    // owns the SAME AdaptationSettings instance handed to Build (the app wiring: the enable gate P14
    // reads is the one the provider composes under); configure callbacks run before any fill is stored.
    private static (AdaptationSettings Adaptation, EffectiveSettingsProvider Provider, DwellSettings Dwell, CursorSettings Cursor) Make(
        Action<AdaptationSettings>? configureAdaptation = null,
        Action<DwellSettings>? configureDwell = null,
        Action<CursorSettings>? configureCursor = null)
    {
        var adaptation = new AdaptationSettings { Enabled = true };
        configureAdaptation?.Invoke(adaptation);
        var dwell = new DwellSettings();
        configureDwell?.Invoke(dwell);
        var cursor = new CursorSettings();
        configureCursor?.Invoke(cursor);
        return (adaptation, new EffectiveSettingsProvider(adaptation), dwell, cursor);
    }

    private static AdaptationPreviewRow Row(AdaptationPreviewSnapshot snapshot, AdaptationKnob knob) =>
        snapshot.Rows.Single(r => r.Knob == knob);

    [Fact]
    public void Build_AllAtBaseline_FourZeroRowsAndNothingActive()
    {
        var (adaptation, provider, dwell, cursor) = Make();

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);

        Check.That(snapshot.AnythingActive).IsFalse();
        Check.That(snapshot.Rows).HasSize(4);
        // Display order pinned: Dwell, Tolerance, Smoothing, Geometry.
        Check.That(snapshot.Rows.Select(r => r.Knob)).ContainsExactly(
            AdaptationKnob.DwellTime, AdaptationKnob.FixationRadius, AdaptationKnob.Smoothing, AdaptationKnob.TargetGeometry);
        foreach (var row in snapshot.Rows)
        {
            Check.That(row.TotalFill).IsEqualTo(0.0);
            Check.That(row.LearnedFill).IsEqualTo(0.0);
            Check.That(row.TighteningDirection).IsFalse();
        }

        // At rest the absolute columns still show what the engine would run — the exact baseline
        // (§8.3: no fill ⇒ effective == baseline), not null.
        var tolerance = Row(snapshot, AdaptationKnob.FixationRadius);
        Check.That(tolerance.AbsoluteValue).IsEqualTo(dwell.FixationRadiusPixels);
        Check.That(tolerance.BaselineValue).IsEqualTo(dwell.FixationRadiusPixels);
        var smoothing = Row(snapshot, AdaptationKnob.Smoothing);
        Check.That(smoothing.AbsoluteValue).IsEqualTo(cursor.OneEuroMinCutoff);
        Check.That(smoothing.BaselineValue).IsEqualTo(cursor.OneEuroMinCutoff);
    }

    [Fact]
    public void Build_LearnedOnly_TotalFillEqualsLearnedFillAndActive()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetLearnedFill(AdaptationKnob.Smoothing, 0.25); // within LearnedMaxFill (0.5)

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);

        var smoothing = Row(snapshot, AdaptationKnob.Smoothing);
        Check.That(smoothing.TotalFill).IsEqualTo(0.25);
        Check.That(smoothing.LearnedFill).IsEqualTo(0.25);
        Check.That(snapshot.AnythingActive).IsTrue();
        foreach (var row in snapshot.Rows.Where(r => r.Knob != AdaptationKnob.Smoothing))
        {
            Check.That(row.TotalFill).IsEqualTo(0.0);
            Check.That(row.LearnedFill).IsEqualTo(0.0);
        }
    }

    [Fact]
    public void Build_Tolerance_ComposesAbsoluteAgainstFixationRadiusBaseline()
    {
        var (adaptation, provider, dwell, cursor) = Make(
            a => a.ToleranceLooserMaxFraction = 0.5,
            d => d.FixationRadiusPixels = 45);
        provider.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 0.4, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var tolerance = Row(snapshot, AdaptationKnob.FixationRadius);

        // 45 + 0.4 × 0.5 × 45 = 54, inside the 10–200 guard — the literal pins the arithmetic, the
        // oracle assertion below pins the SOURCE: the row is the engine's own Effective call, not an
        // independent recomputation (0.4 is not dyadic, so the literal alone would be exact-arithmetic
        // pinned to the provider's internal multiplication order — it rounds to exactly 9.0 today).
        Check.That(tolerance.AbsoluteValue).IsEqualTo(54.0);
        Check.That(tolerance.AbsoluteValue).IsEqualTo(
            provider.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, dwell.FixationRadiusPixels));
        Check.That(tolerance.BaselineValue).IsEqualTo(45.0);
        Check.That(tolerance.TotalFill).IsEqualTo(0.4);
        Check.That(tolerance.LearnedFill).IsEqualTo(0.0);
        Check.That(tolerance.TighteningDirection).IsFalse();
        Check.That(tolerance.IsHzUnit).IsFalse();
        Check.That(snapshot.AnythingActive).IsTrue();
    }

    [Fact]
    public void Build_SmoothingOneEuro_HzUnitAndMatchesComposeOracle()
    {
        var (adaptation, provider, dwell, cursor) = Make(
            configureCursor: c => { c.FilterMode = GazeFilterMode.OneEuro; c.OneEuroMinCutoff = 5.0; });
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var smoothing = Row(snapshot, AdaptationKnob.Smoothing);

        Check.That(smoothing.IsHzUnit).IsTrue();
        // The brief's oracle: the row must equal a direct Compose call — the preview shows exactly
        // what the engine would run (5 − 0.5 × 0.5 × 5 = 3.75 Hz).
        Check.That(smoothing.AbsoluteValue).IsEqualTo(EffectiveSmoothing.Compose(provider, cursor));
        Check.That(smoothing.AbsoluteValue).IsEqualTo(3.75);
        Check.That(smoothing.BaselineValue).IsEqualTo(5.0);
    }

    [Fact]
    public void Build_SmoothingEma_FactorUnitAndMatchesComposeOracle()
    {
        var (adaptation, provider, dwell, cursor) = Make(
            configureCursor: c => { c.FilterMode = GazeFilterMode.Smoothed; c.SmoothingFactor = 0.9; });
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var smoothing = Row(snapshot, AdaptationKnob.Smoothing);

        Check.That(smoothing.IsHzUnit).IsFalse();
        Check.That(smoothing.AbsoluteValue).IsEqualTo(EffectiveSmoothing.Compose(provider, cursor));
        Check.That(smoothing.AbsoluteValue).IsEqualTo(0.45);
        Check.That(smoothing.BaselineValue).IsEqualTo(0.9);
    }

    [Fact]
    public void Build_SmoothingDirect_NullAbsoluteAndBaseline()
    {
        var (adaptation, provider, dwell, cursor) = Make(configureCursor: c => c.FilterMode = GazeFilterMode.Direct);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var smoothing = Row(snapshot, AdaptationKnob.Smoothing);

        // Direct has no smoothing parameter (the mode is never auto-switched): Compose returns null,
        // so both value columns are null; IsHzUnit is moot and stays false.
        Check.That(smoothing.AbsoluteValue).IsNull();
        Check.That(smoothing.BaselineValue).IsNull();
        Check.That(smoothing.IsHzUnit).IsFalse();
    }

    [Fact]
    public void Build_DwellTighteningOnly_ShowsLonger()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.0, tighteningFill: 0.3);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var dwellRow = Row(snapshot, AdaptationKnob.DwellTime);

        Check.That(dwellRow.TighteningDirection).IsTrue();
        Check.That(dwellRow.TotalFill).IsEqualTo(0.3);
        Check.That(dwellRow.LearnedFill).IsEqualTo(0.0);
        Check.That(dwellRow.AbsoluteValue).IsNull();
        Check.That(dwellRow.BaselineValue).IsNull();
        Check.That(snapshot.AnythingActive).IsTrue();
    }

    [Fact]
    public void Build_DwellMixedLargerForgiving_ShowsShorter()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.6, tighteningFill: 0.2);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var dwellRow = Row(snapshot, AdaptationKnob.DwellTime);

        Check.That(dwellRow.TighteningDirection).IsFalse();
        Check.That(dwellRow.TotalFill).IsEqualTo(0.6);
    }

    [Fact]
    public void Build_DwellTie_FavorsForgivingShorter()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.4, tighteningFill: 0.4);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var dwellRow = Row(snapshot, AdaptationKnob.DwellTime);

        // Ties ⇒ forgiving ("shorter") per the composition rule — strict >, so equal fills keep the
        // forgiving label.
        Check.That(dwellRow.TighteningDirection).IsFalse();
        Check.That(dwellRow.TotalFill).IsEqualTo(0.4);
    }

    [Fact]
    public void Build_DwellLearnedPlusMomentary_ClampsToOne()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.5); // == LearnedMaxFill
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.8, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var dwellRow = Row(snapshot, AdaptationKnob.DwellTime);

        // clamp(0.5 + 0.8, 0, 1) == 1.0; the learned shade is min(0.5, 1.0), within the total.
        Check.That(dwellRow.TotalFill).IsEqualTo(1.0);
        Check.That(dwellRow.LearnedFill).IsEqualTo(0.5);
        Check.That(dwellRow.LearnedFill <= dwellRow.TotalFill).IsTrue();
        Check.That(dwellRow.TighteningDirection).IsFalse();
    }

    [Fact]
    public void Build_DwellLearnedWithTighteningDominant_NoLearnedShade()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.0, tighteningFill: 0.8);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var dwellRow = Row(snapshot, AdaptationKnob.DwellTime);

        // Spec §3.4: learned is forgiving-only — shorter-ward by construction — so the learned shade
        // appears on the shorter side only. A tightening-dominant row ("longer") must carry NO
        // learned shade, even though min(0.5, 0.8) would be 0.5: rendering one would claim the
        // learned layer made dwell longer, which it cannot.
        Check.That(dwellRow.TighteningDirection).IsTrue();
        Check.That(dwellRow.TotalFill).IsEqualTo(0.8);
        Check.That(dwellRow.LearnedFill).IsEqualTo(0.0);
    }

    [Fact]
    public void Build_GeometryFill_NullAbsolutes()
    {
        var (adaptation, provider, dwell, cursor) = Make();
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 0.35, tighteningFill: 0.0);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);
        var geometry = Row(snapshot, AdaptationKnob.TargetGeometry);

        // Geometry is runtime-derived (no single absolute per row): band fill only.
        Check.That(geometry.TotalFill).IsEqualTo(0.35);
        Check.That(geometry.LearnedFill).IsEqualTo(0.0);
        Check.That(geometry.AbsoluteValue).IsNull();
        Check.That(geometry.BaselineValue).IsNull();
        Check.That(geometry.TighteningDirection).IsFalse();
        Check.That(snapshot.AnythingActive).IsTrue();
    }

    [Fact]
    public void Build_DisabledWithStoredFills_AllZeroRowsAndNothingActive()
    {
        var (adaptation, provider, dwell, cursor) = Make(
            a => a.Enabled = false,
            d => d.FixationRadiusPixels = 45);
        // Non-zero fills still stored in the provider — the builder must gate on the settings (P14:
        // the provider exposes no Enabled), not on what happens to be cached.
        provider.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 0.5, tighteningFill: 0.5);
        provider.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, forgivingFill: 0.4, tighteningFill: 0.0);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 0.6, tighteningFill: 0.0);
        provider.SetMomentaryFill(AdaptationKnob.TargetGeometry, DwellContext.Other, forgivingFill: 0.3, tighteningFill: 0.0);
        provider.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        provider.SetLearnedFill(AdaptationKnob.Smoothing, 0.25);

        var snapshot = AdaptationPreviewSnapshot.Build(adaptation, provider, dwell, cursor);

        Check.That(snapshot.AnythingActive).IsFalse();
        foreach (var row in snapshot.Rows)
        {
            Check.That(row.TotalFill).IsEqualTo(0.0);
            Check.That(row.LearnedFill).IsEqualTo(0.0);
            Check.That(row.TighteningDirection).IsFalse();
        }

        // §8.3 through the builder: disabled ⇒ provider.Effective returns the exact baseline, so the
        // tolerance absolute column shows 45 == its baseline, never a composed value.
        var tolerance = Row(snapshot, AdaptationKnob.FixationRadius);
        Check.That(tolerance.AbsoluteValue).IsEqualTo(45.0);
        Check.That(tolerance.BaselineValue).IsEqualTo(45.0);
    }
}
