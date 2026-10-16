namespace EyeCtrl.App.Tests.Overlays;

using System.Text;
using Xunit;

/// <summary>
/// The A/B's verdict logic, headless (review-01 findings 1 and 2). The sitting's readout must not
/// depend on the owner's wallpaper to be readable, so the cases are decided by comparison and the
/// failure of the comparison — an affinity lift that never reached the capture — has a name of its
/// own. These are pure arithmetic over synthetic samples: no window, no screen, and they run in
/// CI's <c>Category!=UI</c> set with the rest of the headless suite.
/// </summary>
public class BackdropAbClassificationTests
{
    [Fact]
    public void LiftedCapture_IdenticalToItsBaseline_IsUnreadable()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 120.4, meanG: 118.2, meanB: 130.3, lumaStdDev: 12.4);

        var verdict = BackdropClassification.LiveFlatBlack(baseline, lifted, new StringBuilder());

        Check.WithCustomMessage($"an unreadable lift must say so, not pass for a state: {verdict}")
            .That(verdict).StartsWith("UNREADABLE");
    }

    [Fact]
    public void BlackCapture_IsNoMaterial()
    {
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 0, meanG: 0, meanB: 0, lumaStdDev: 0, blackFraction: 1.0);

        var verdict = BackdropClassification.LiveFlatBlack(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("black");
    }

    [Fact]
    public void UniformGreyCapture_IsFlat()
    {
        // The inactive fallback: a fixed grey whatever lies behind, so no variation and a mean that
        // is nothing like the wallpaper's.
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 30, meanG: 30, meanB: 32, lumaStdDev: 0.4);

        var verdict = BackdropClassification.LiveFlatBlack(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("flat");
    }

    [Fact]
    public void VaryingCapture_OffTheWallpapersMean_IsLive()
    {
        // Live Mica: a darkened, tinted sample of the wallpaper — spatial variation, and a mean
        // that has moved away from the baseline's.
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 28, meanG: 30, meanB: 40, lumaStdDev: 6);

        var verdict = BackdropClassification.LiveFlatBlack(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("live");
    }

    [Fact]
    public void VaryingCapture_ThatTheBaselineExplains_IsNotCalledFlat()
    {
        // A live material over a strongly varying wallpaper may share the desktop's spread, but its
        // mean still moves: the unreadable check is mean AND stddev together, so this stays live.
        var baseline = Sample(meanR: 120, meanG: 118, meanB: 130, lumaStdDev: 12);
        var lifted = Sample(meanR: 60, meanG: 59, meanB: 65, lumaStdDev: 11.8);

        var verdict = BackdropClassification.LiveFlatBlack(baseline, lifted, new StringBuilder());

        Check.That(verdict).StartsWith("live");
    }

    private static BackdropCapture.ScreenSample Sample(
        double meanR, double meanG, double meanB, double lumaStdDev, double blackFraction = 0.0)
    {
        var luma = 0.2126 * meanR + 0.7152 * meanG + 0.0722 * meanB;
        var stats = new BackdropCapture.SampleStats(
            meanR, meanG, meanB, luma, lumaStdDev, blackFraction, Pixels: 10_000);
        return new BackdropCapture.ScreenSample(stats, stats);
    }
}
