namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;

public class EyeCareBannerGeometryTests
{
    [Fact]
    public void ComputeDwellGeometry_At100Percent_MatchesXamlButtonCenters()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1920, scaleX: 1.0, scaleY: 1.0);
        Check.That(g.Start.X).IsCloseTo(1920 - 52, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132, 0.001);
        Check.That(g.Start.Y).IsCloseTo(40, 0.001);
        Check.That(g.Radius).IsCloseTo(40, 0.001);
    }

    [Fact]
    public void ComputeDwellGeometry_At150Percent_ScalesEverything()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1280, scaleX: 1.5, scaleY: 1.5);
        Check.That(g.Start.X).IsCloseTo(1920 - 52 * 1.5, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132 * 1.5, 0.001);
        Check.That(g.Start.Y).IsCloseTo(40 * 1.5, 0.001);
        Check.That(g.Radius).IsCloseTo(40 * 1.5, 0.001);
    }
}
