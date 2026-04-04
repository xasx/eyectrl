namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

public class GapInterpolatorTests
{
    // Minimal stub — passes valid gaze through, returns (X,Y) as PhysicalPoint
    private sealed class PassThroughFilter : IGazeFilter
    {
        public PhysicalPoint Filter(GazePoint g) => new(g.X, g.Y);
        public void Reset() { }
    }

    private static GazePoint Valid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: true);

    private static GazePoint Invalid(double x, double y, long ts) =>
        new(x, y, ts, IsValid: false);

    [Fact]
    public void Process_ValidSample_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Valid(100, 200, 0));
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_ValidSample_PositionMatchesRaw()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (pos, _) = gi.Process(Valid(100, 200, 0));
        Assert.Equal(100, pos.X);
        Assert.Equal(200, pos.Y);
    }

    [Fact]
    public void Process_ShortInvalidGap_IsInGapTrue()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));                 // establish last valid
        var (_, isInGap) = gi.Process(Invalid(0, 0, 50_000));  // 50ms < 150ms
        Assert.True(isInGap);
    }

    [Fact]
    public void Process_ShortInvalidGap_HoldsLastValidPosition()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        var (pos, _) = gi.Process(Invalid(0, 0, 50_000));
        Assert.Equal(100, pos.X);
        Assert.Equal(200, pos.Y);
    }

    [Fact]
    public void Process_LongInvalidGap_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        // 200ms >= 150ms -> long gap, treat as new sequence
        var (_, isInGap) = gi.Process(Invalid(0, 0, 200_000));
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_ResumeAfterShortGap_IsInGapFalse()
    {
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        gi.Process(Valid(100, 200, 0));
        gi.Process(Invalid(0, 0, 50_000));     // short gap
        var (_, isInGap) = gi.Process(Valid(105, 205, 100_000));  // resume
        Assert.False(isInGap);
    }

    [Fact]
    public void Process_NoValidSampleYet_InvalidSample_IsInGapFalse()
    {
        // No prior valid sample — can't be "in gap"
        var gi = new GapInterpolator(new PassThroughFilter(), maxGapMs: 150);
        var (_, isInGap) = gi.Process(Invalid(0, 0, 0));
        Assert.False(isInGap);
    }
}
