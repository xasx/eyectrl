namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Settings;

public class EffectiveSettingsProviderTests
{
    private static EffectiveSettingsProvider Make(bool enabled)
        => new(new AdaptationSettings { Enabled = enabled });

    [Fact]
    public void Disabled_ReturnsBaseline_EvenWithDeltaSet()
    {
        var p = Make(enabled: false);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 40, tightening: 0);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Enabled_NoDelta_ReturnsBaseline()
    {
        var p = Make(enabled: true);
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void Enabled_ForgivingDelta_LoosensRadius_ClampedToBand()
    {
        var p = Make(enabled: true);
        // Forgiving (looser = larger) delta of +40; band is [0, +50% of baseline] = [0, 25].
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 40, tightening: 0);
        double eff = p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 50);
        Check.That(eff > 50.0).IsTrue();
        Check.That(eff).IsEqualTo(75.0); // +40 clamped to +25 → 50 + 25
    }

    [Fact]
    public void Enabled_ForgivingDelta_NeverExceedsValidatedMax()
    {
        var p = Make(enabled: true);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 1000, tightening: 0);
        // 190 + min(1000, 190*0.5=95) = 285, clamped to the validated max (200).
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, baseline: 190)).IsEqualTo(200.0);
    }

    [Fact]
    public void ClearMomentaryDeltas_RestoresBaseline()
    {
        var p = Make(enabled: true);
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, 40, 0);
        p.ClearMomentaryDeltas();
        Check.That(p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, 50)).IsEqualTo(50.0);
    }

    [Fact]
    public void SetAndClear_RaiseChanged()
    {
        var p = Make(enabled: true);
        int changed = 0;
        p.Changed += () => changed++;
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, 40, 0);
        p.ClearMomentaryDeltas();
        Check.That(changed).IsEqualTo(2);
    }

    [Fact]
    public void FixationRadius_EffectiveStaysWithinDwellSettingsValidatedRange()
    {
        // Drift guard: KnobPolicy hardcodes the fixation-radius validated range (10–200), duplicating
        // DwellSettings.FixationRadiusPixels's guard. Use that setter as the ORACLE — if KnobPolicy's
        // ValidMax ever drifts above the settings guard, assigning the effective value back throws and
        // this fails. No magic number in the assertion (Phase 1b-② removes the duplication).
        var settings = new EyeCtrlSettings();
        settings.Adaptation.Enabled = true;
        var p = new EffectiveSettingsProvider(settings.Adaptation);
        settings.Dwell.FixationRadiusPixels = 200; // top of the validated range
        p.SetMomentaryDelta(AdaptationKnob.FixationRadius, DwellContext.Other, forgiving: 10_000, tightening: 0);
        double eff = p.Effective(AdaptationKnob.FixationRadius, DwellContext.Other, settings.Dwell.FixationRadiusPixels);

        bool threw = false;
        try { settings.Dwell.FixationRadiusPixels = eff; }
        catch (ArgumentOutOfRangeException) { threw = true; }
        Check.That(threw).IsFalse();
    }
}
