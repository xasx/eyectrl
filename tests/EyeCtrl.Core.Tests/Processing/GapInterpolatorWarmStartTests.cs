namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class GapInterpolatorWarmStartTests
{
    // Covers both filter kinds the GazeEngine rebuild can produce (design R1: both return the seed
    // unchanged on first call). A cold rebuild emits the raw sample; a warm-started rebuild smooths
    // from the carried-over seed — below the raw, and never past the seed (bounded one-sample lag).
    private static void AssertWarmStartSmoothsFirstSample(Func<IGazeFilter> makeFilter)
    {
        // Establish a filtered position near (500,500) on the "old" interpolator.
        var old = new GapInterpolator(makeFilter(), maxGapMs: 150);
        long t = 0;
        for (int i = 0; i < 10; i++) { old.Process(new GazePoint(500, 500, t, true)); t += 33_000; }

        // Rebuild WITH warm-start, then feed a sample at x=600.
        var warm = new GapInterpolator(makeFilter(), maxGapMs: 150);
        warm.CopyStateFrom(old);
        warm.WarmStartFromCurrentState();
        double warmX = warm.Process(new GazePoint(600, 500, t, true)).Position.X;

        // Rebuild WITHOUT warm-start (cold filter): first Process returns the raw sample.
        var cold = new GapInterpolator(makeFilter(), maxGapMs: 150);
        cold.CopyStateFrom(old);
        double coldX = cold.Process(new GazePoint(600, 500, t, true)).Position.X;

        Check.That(coldX).IsEqualTo(600.0);   // cold: emits the raw point (the jump)
        Check.That(warmX < 600.0).IsTrue();    // warm: smoothed below the raw...
        Check.That(warmX >= 500.0).IsTrue();   // ...and no overshoot past the seed (bounded lag)
    }

    [Fact]
    public void WarmStart_SmoothsFirstSample_OneEuro()
        => AssertWarmStartSmoothsFirstSample(() => new OneEuroFilter());

    [Fact]
    public void WarmStart_SmoothsFirstSample_Ema()
        => AssertWarmStartSmoothsFirstSample(() => new SmoothedFilter(0.5));

    [Fact]
    public void WarmStart_IsNoOp_WithNoPriorState()
    {
        var i = new GapInterpolator(new OneEuroFilter(), maxGapMs: 150);
        i.WarmStartFromCurrentState(); // no established position yet → must not throw
    }
}
