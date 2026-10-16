namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Task-2 extraction: <see cref="EffectiveSmoothing.Compose"/> is the §13.3 smoothing composition+clamp
/// moved VERBATIM from <c>GazeEngine.EffectiveSmoothingParameter</c> (the engine now delegates), so a
/// later preview builder composes the identical value. These four cases pin the per-mode composition at
/// the helper level; the engine-level oracle (<c>GazeEngineSmoothingRoutingTests</c>, including the
/// assign-back guard test that pins the clamp literals against the CursorSettings setters) still pins
/// the same behaviour end-to-end and is untouched by the extraction.
/// </summary>
public class EffectiveSmoothingTests
{
    // One fresh (provider, cursor) pair per case — no shared fixture (repo convention). The provider owns
    // its AdaptationSettings (the enable gate + band fractions); both configure callbacks run BEFORE the
    // provider is constructed. P13: each case sets ONLY the baseline its filter mode reads — One-Euro
    // cases never touch SmoothingFactor, EMA cases never touch OneEuroMinCutoff (and stay inside the
    // factor's 0–1 guard).
    private static (EffectiveSettingsProvider Provider, CursorSettings Cursor) Make(
        Action<CursorSettings>? configureCursor = null,
        Action<AdaptationSettings>? configureAdaptation = null)
    {
        var cursor = new CursorSettings();
        configureCursor?.Invoke(cursor);
        var adaptation = new AdaptationSettings { Enabled = true };
        configureAdaptation?.Invoke(adaptation);
        return (new EffectiveSettingsProvider(adaptation), cursor);
    }

    [Fact]
    public void Compose_DirectMode_ReturnsNull()
    {
        // §13.3: DirectFilter takes no smoothing parameter and the mode is never auto-switched (§5), so
        // the knob no-ops — Direct returns null regardless of any fill.
        var (provider, cursor) = Make(c => c.FilterMode = GazeFilterMode.Direct);

        Check.That(EffectiveSmoothing.Compose(provider, cursor)).IsNull();
    }

    [Fact]
    public void Compose_OneEuro_FloorBaselineFullFill_ClampsToTheFloor()
    {
        // The §13.3 safety-clamp case: a 0.1 Hz baseline at fraction 1.0 composes to exactly 0.0 — below
        // the 0.1 floor OneEuroFilter's constructor rejects — so the [0.1, 20] guard must re-engage at
        // composition time. Expected: the clamped 0.1, never the composed 0.0.
        var (provider, cursor) = Make(
            c => { c.FilterMode = GazeFilterMode.OneEuro; c.OneEuroMinCutoff = 0.1; },
            a => a.SmoothingMoreMaxFraction = 1.0);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        Check.That(EffectiveSmoothing.Compose(provider, cursor)).IsEqualTo(0.1);
    }

    [Fact]
    public void Compose_Ema_ComposesAgainstSmoothingFactor()
    {
        // The baseline is resolved per mode at read time: the SAME fill composes against SmoothingFactor
        // here (0.9 − 0.50 × 0.9 = 0.45, in-range). This pins the baseline SELECTION, not a clamp — the
        // EMA floor (0.0) is unreachable from in-range inputs, since smoothing is forgiving-only and the
        // composed value never exceeds the baseline the setters keep inside the range.
        var (provider, cursor) = Make(
            c => { c.FilterMode = GazeFilterMode.Smoothed; c.SmoothingFactor = 0.9; },
            a => a.SmoothingMoreMaxFraction = 0.5);
        provider.SetMomentaryFill(AdaptationKnob.Smoothing, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);

        Check.That(EffectiveSmoothing.Compose(provider, cursor)).IsEqualTo(0.45);
    }

    [Fact]
    public void Compose_Disabled_ReturnsTheCursorBaselineExactly()
    {
        // The locked "disabled ⇒ effective == baseline" invariant (design §8.3): with adaptation disabled
        // and no fills stored, the composed value is the cursor's own baseline, exactly.
        var (provider, cursor) = Make(
            c => { c.FilterMode = GazeFilterMode.OneEuro; c.OneEuroMinCutoff = 5.0; },
            a => a.Enabled = false);

        Check.That(EffectiveSmoothing.Compose(provider, cursor)).IsEqualTo(5.0);
    }
}
