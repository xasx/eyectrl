namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;

/// <summary>
/// The §13.3 smoothing-routing contract of <see cref="GazeFilterFactory.Create(CursorSettings,double?)"/>:
/// the effective parameter must behave exactly like the same value set as the baseline, and Direct — which
/// has no smoothing parameter — must ignore it. Verified by step-response differentials (no filter
/// internals), with beta pinned to 0 on One-Euro so cutoff == minCutoff at every speed.
/// <para>
/// The step is a 10 px one. Anything larger trips the retuned One-Euro's jump detector, which runs at
/// the sample-rate cutoff cap no matter what minCutoff says — so a big step makes every One-Euro
/// candidate answer identically and the differentials below stop distinguishing anything.
/// </para>
/// </summary>
public class GazeFilterFactoryEffectiveParameterTests
{
    [Fact]
    public void Create_OneEuro_EffectiveMinCutoff_BehavesLikeThatBaseline()
    {
        // 0.1 Hz (the settings floor, maximum smoothing) must lag a step far more than the 5.0 Hz default.
        Check.That(OneEuroStepX(effectiveMinCutoff: 0.1))
            .IsStrictlyLessThan(OneEuroStepX(effectiveMinCutoff: 5.0));
    }

    [Fact]
    public void Create_OneEuro_NullParameter_EqualsTheBaselineComposition()
    {
        // Read the default rather than restating it: the Stage 3 retune moved it (5.0 → 1.5), and a
        // literal here would have turned a default change into a failure of the null-parameter contract.
        Check.That(OneEuroStepX(effectiveMinCutoff: null))
            .IsEqualTo(OneEuroStepX(effectiveMinCutoff: new CursorSettings().OneEuroMinCutoff));
    }

    [Fact]
    public void Create_Ema_EffectiveSmoothingFactor_BehavesLikeThatBaseline()
    {
        // EMA has no speed adaptation, so the effect is large and unambiguous: 0.05 lags far more than 0.5.
        Check.That(EmaStepX(effectiveSmoothingFactor: 0.05))
            .IsStrictlyLessThan(EmaStepX(effectiveSmoothingFactor: 0.5));
    }

    [Fact]
    public void Create_Ema_NullParameter_EqualsTheBaselineComposition()
    {
        Check.That(EmaStepX(effectiveSmoothingFactor: null))
            .IsEqualTo(EmaStepX(effectiveSmoothingFactor: 0.5));
    }

    [Fact]
    public void Create_Direct_IgnoresTheParameter()
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.Direct };
        Check.That(GazeFilterFactory.Create(settings, 0.1)).IsInstanceOf<DirectFilter>();
    }

    // 12 steady samples at (500,500), then one step to x=510; the step sample's filtered X is the response.
    private static double OneEuroStepX(double? effectiveMinCutoff)
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.OneEuro, OneEuroBetaPerDiagonal = 0.0 };
        return StepX(GazeFilterFactory.Create(settings, effectiveMinCutoff));
    }

    private static double EmaStepX(double? effectiveSmoothingFactor)
    {
        var settings = new CursorSettings { FilterMode = GazeFilterMode.Smoothed };
        return StepX(GazeFilterFactory.Create(settings, effectiveSmoothingFactor));
    }

    private static double StepX(IGazeFilter filter)
    {
        long ts = 0;
        for (int i = 0; i < 12; i++, ts += 33_000)
            filter.Filter(new GazePoint(500, 500, ts, true));
        return filter.Filter(new GazePoint(510, 500, ts, true)).X;
    }
}
