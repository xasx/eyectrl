namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class EffectiveValueTests
{
    // dwell-like knob: baseline 700 ms, forgiving = shorter (band [-300,0]), tightening = longer (band [0,500]).
    private static readonly (double Min, double Max) Forgiving = (-300, 0);
    private static readonly (double Min, double Max) Tightening = (0, 500);

    [Fact]
    public void ZeroDelta_ReturnsExactBaseline()
    {
        double v = EffectiveValue.Compute(700, 0, 0, Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(700.0);
    }

    [Fact]
    public void ZeroDelta_ReturnsBaseline_EvenWhenBaselineIsOutsideTheValidatedRange()
    {
        // A legacy/hand-edited baseline ABOVE validMax. With zero delta the value model must return
        // it exactly — the validated-range clamp only engages when delta != 0 (design §5). This is
        // the invariant that keeps "adaptation idle == the user's baseline" true even for bad settings.
        double v = EffectiveValue.Compute(2500, 0, 0, Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(2500.0);
    }

    [Fact]
    public void TighteningIsNotSwallowedByForgiving()
    {
        // Forgiving pinned to its floor (-300) AND tightening +500 both apply (independent sub-bands):
        // 700 - 300 + 500 = 900. A naive sum-then-one-sided-clamp would have lost the tightening.
        double v = EffectiveValue.Compute(700, forgivingDelta: -1000, tighteningDelta: 500,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(900.0);
    }

    [Fact]
    public void ForgivingClampedToItsBand()
    {
        double v = EffectiveValue.Compute(700, forgivingDelta: -1000, tighteningDelta: 0,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(400.0); // -1000 clamped to -300
    }

    [Fact]
    public void EffectiveClampedToValidatedRange()
    {
        double v = EffectiveValue.Compute(1900, forgivingDelta: 0, tighteningDelta: 500,
            Forgiving, Tightening, validMin: 200, validMax: 2000);
        Check.That(v).IsEqualTo(2000.0); // 1900 + 500 = 2400 clamped to 2000
    }
}
