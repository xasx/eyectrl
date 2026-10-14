namespace EyeCtrl.App.Tests.Overlays;

using System.Text;
using Xunit;

/// <summary>
/// Round 2's classification logic, headless: black (nothing reaches the surface), the two flat
/// greys (neutral = the expected never-activated fallback, tinted = suspect) and varying (a live
/// material), plus the UNREADABLE lift it inherits from round 1. Pure arithmetic over synthetic
/// samples — no window, no screen — so it runs in CI's <c>Category!=UI</c> set. Which black cause
/// it is (the client still opaque, no system backdrop, no frame) is the script's pairwise reading,
/// not the per-case label.
/// </summary>
public class BackdropMaterialClassificationTests
{
    [Fact]
    public void LiftedCapture_IdenticalToItsBaseline_IsUnreadable()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 120.4, meanG: 118.2, meanB: 130.3, lumaStdDev: 12.4);

        var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, new StringBuilder());

        Check.WithCustomMessage($"an unreadable lift must say so, not pass for a state: {verdict}")
            .That(verdict).StartsWith("UNREADABLE");
    }

    [Fact]
    public void BlackCapture_IsNothingReachingTheSurface()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 0, meanG: 0, meanB: 0, lumaStdDev: 0, blackFraction: 1.0);

        var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("black");
    }

    [Fact]
    public void UniformNeutralGrey_IsTheInactiveFallback()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 30, meanG: 30, meanB: 32, lumaStdDev: 0.4);

        var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("flat neutral grey");
    }

    [Fact]
    public void UniformTintedCapture_IsFlaggedSuspect()
    {
        // review-02's advisory: the inactive fallback is neutral; a tinted flat is not it.
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 30, meanG: 30, meanB: 60, lumaStdDev: 0.4);

        var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("flat but tinted");
    }

    [Fact]
    public void VaryingCapture_IsALiveMaterial()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 28, meanG: 30, meanB: 40, lumaStdDev: 6);

        var verdict = BackdropMaterialLiveCheckTests.Classify(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("varying");
    }

    private static BackdropAbLiveCheckTests.ScreenSample Sample(
        double meanR, double meanG, double meanB, double lumaStdDev, double blackFraction = 0.0)
    {
        var luma = 0.2126 * meanR + 0.7152 * meanG + 0.0722 * meanB;
        var stats = new BackdropAbLiveCheckTests.SampleStats(
            meanR, meanG, meanB, luma, lumaStdDev, blackFraction, Pixels: 10_000);
        return new BackdropAbLiveCheckTests.ScreenSample(stats, stats);
    }
}
