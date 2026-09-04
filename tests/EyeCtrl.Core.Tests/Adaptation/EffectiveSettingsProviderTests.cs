namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class EffectiveSettingsProviderTests
{
    // Returns the provider AND its settings so tests can mutate band fractions (band-source migration).
    private static (EffectiveSettingsProvider Provider, AdaptationSettings Settings) Make(bool enabled = false)
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
    public void ClearMomentaryFills_RestoresBaseline()
    {
        var (p, _) = Make(enabled: true);
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        p.ClearMomentaryFills();
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void SetAndClear_RaiseChanged()
    {
        var (p, _) = Make(enabled: true);
        int changed = 0;
        p.Changed += () => changed++;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 1.0, 0.0);
        p.ClearMomentaryFills();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void FixationRadius_EffectiveStaysWithinDwellSettingsValidatedMax()
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

    [Fact]
    public void Learned_SumsWithMomentary_AtTheBandEdge()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.ToleranceLooserMaxFraction = 0.5;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.9, 0.0);
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.3);
        // 0.3 + 0.9 = 1.2 band-fills → clamped to the +50% band edge: 50 → 75.
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(75.0);
    }

    [Fact]
    public void Learned_Alone_ComposesTheForgivingDirection()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.DwellShorterMaxFraction = 0.4;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.4);
        // delta = −0.4 fill × 0.4 fraction × 1000 ms = −160 → 840. Learned alone must move effective.
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(840.0);
    }

    [Fact]
    public void LearnedZero_EffectiveIsExactlyMomentaryOnly()
    {
        var (p, a) = Make();
        a.Enabled = true;
        a.ToleranceLooserMaxFraction = 0.5;
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.0); // stored zero — must behave like absence
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.6, 0.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsEqualTo(65.0);
    }

    [Fact]
    public void Disabled_ReturnsBaseline_EvenWithLearnedFillSet()
    {
        var (p, a) = Make();
        a.Enabled = false;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 1.0, 0.0);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(1000.0);
    }

    [Fact]
    public void SetLearnedFill_RaisesChangedOnlyOnValueChange()
    {
        var (p, _) = Make();
        int raised = 0;
        p.Changed += () => raised++;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
        Check.That(raised).IsEqualTo(1);
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2); // same value → no raise
        Check.That(raised).IsEqualTo(1);
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.3);
        Check.That(raised).IsEqualTo(2);
    }

    [Fact]
    public void ClearLearnedFills_ClearsAndRaises_OnlyWhenPresent()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.4);
        int raised = 0;
        p.Changed += () => raised++;
        p.ClearLearnedFills();
        Check.That(raised).IsEqualTo(1);
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsEqualTo(1000.0);
        p.ClearLearnedFills(); // already empty → no raise
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void Learned_IsForgivingOnly_EveryKnobMovesInItsSafeDirection()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.5);
        p.SetLearnedFill(AdaptationKnob.Smoothing, 0.5);
        p.SetLearnedFill(AdaptationKnob.TargetGeometry, 0.5);
        p.SetLearnedFill(AdaptationKnob.FixationRadius, 0.5);
        // dwell shorter, smoothing parameter DOWN, geometry bigger, tolerance looser (§7.1).
        Check.That(p.Effective(AdaptationKnob.DwellTime, DwellContext.Other, 1000.0)).IsStrictlyLessThan(1000.0);
        Check.That(p.Effective(AdaptationKnob.Smoothing, DwellContext.Other, 5.0)).IsStrictlyLessThan(5.0);
        Check.That(p.Effective(AdaptationKnob.TargetGeometry, DwellContext.Other, 40.0)).IsStrictlyGreaterThan(40.0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50.0)).IsStrictlyGreaterThan(50.0);
    }

    [Fact]
    public void SetLearnedFill_ClampsToLearnedMaxFill()
    {
        var (p, _) = Make();
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.9);
        var entries = p.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsEqualTo(AdaptationSettings.LearnedMaxFill);
    }

    [Fact]
    public void BandFractionEdit_WhileFillStored_RaisesChanged()
    {
        // Leftover (a): a band edit used to refresh the applied value only at the next fill write.
        var (p, a) = Make();
        a.Enabled = true;
        p.SetMomentaryFill(AdaptationKnob.FixationRadius, DwellContext.Other, 0.6, 0.0);
        int raised = 0;
        p.Changed += () => raised++;
        a.ToleranceLooserMaxFraction = 0.3; // still valid; composition-relevant
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void GeometryMarginCapEdit_RaisesChanged()
    {
        var (p, a) = Make();
        a.Enabled = true;
        int raised = 0;
        p.Changed += () => raised++;
        a.GeometryMaxMarginPx = 150.0;
        Check.That(raised).IsEqualTo(1);
        a.Posture = AdaptationPosture.Balanced; // not composition-relevant → no raise
        Check.That(raised).IsEqualTo(1);
    }

    [Fact]
    public void TryGetMomentaryFill_ReturnsStoredValues_AndFalseWhenAbsent()
    {
        var (p, a) = Make();
        a.Enabled = true;
        p.SetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, 0.4, 0.2);
        Check.That(p.TryGetMomentaryFill(AdaptationKnob.DwellTime, DwellContext.Other, out double f, out double t)).IsTrue();
        Check.That(f).IsEqualTo(0.4);
        Check.That(t).IsEqualTo(0.2);
        Check.That(p.TryGetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, out _, out _)).IsFalse();
    }

    [Fact]
    public void EnumerateLearnedFills_ListsNonZeroEntriesOnly()
    {
        var (p, _) = Make();
        p.SetLearnedFill(AdaptationKnob.DwellTime, 0.2);
        p.SetLearnedFill(AdaptationKnob.Smoothing, 0.0); // stored zero → not listed
        var entries = p.EnumerateLearnedFills();
        Check.That(entries).HasSize(1);
        Check.That(entries[0].Knob).IsEqualTo(AdaptationKnob.DwellTime);
        Check.That(entries[0].Fill).IsCloseTo(0.2, 1e-9);
    }
}
