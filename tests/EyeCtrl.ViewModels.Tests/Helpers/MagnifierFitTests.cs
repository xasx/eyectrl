namespace EyeCtrl.ViewModels.Tests.Helpers;

using EyeCtrl.ViewModels.Helpers;

/// <summary>
/// G4 (magnifier-default-zoom, decision 3(a) + Part 2): the fit rule's table. The effective circle
/// radius shrinks so the view - 2 x radius x zoom DIP - fits within 90 % of the screen's shorter
/// side, and never below 50 DIP. The zoom is untouched and the settings are never written.
/// </summary>
public class MagnifierFitTests
{
    [Theory]
    [InlineData(1200.0, 4.0, 100, 100)]   // the owner's 1200-DIP screen: 800 <= 1080, untouched
    [InlineData(720.0, 4.0, 100, 81)]     // 0.9 * 720 / 8 = 81
    [InlineData(768.0, 4.0, 100, 86)]     // 0.9 * 768 / 8 = 86.4 -> 86 (floor)
    [InlineData(720.0, 2.0, 150, 150)]    // today's pair on the same screen: 600 <= 648, untouched
    public void Fit_Table(double shorterSideDip, double magnification, int circleDip, int expected)
    {
        Check.That(MagnifierFit.EffectiveCircleRadius(circleDip, magnification, shorterSideDip))
            .IsEqualTo(expected);
    }

    [Fact]
    public void Fit_NeverGoesBelowFiftyDip()
    {
        // 0.9 * 400 / 10 = 36, floored to the 50-DIP minimum.
        Check.That(MagnifierFit.EffectiveCircleRadius(100, 5.0, 400.0)).IsEqualTo(50);
    }

    [Fact]
    public void Fit_IsPure_TheConfiguredCircleIsNeverRewritten()
    {
        var settings = new EyeCtrl.Core.Settings.ManualSelectionSettings
        {
            CircleRadiusPixels = 100,
            MagnificationLevel = 4.0
        };

        var effective = MagnifierFit.EffectiveCircleRadius(
            settings.CircleRadiusPixels, settings.MagnificationLevel, 720.0);

        Check.That(effective).IsEqualTo(81);
        Check.WithCustomMessage("the settings are never written by the fit rule")
            .That(settings.CircleRadiusPixels).IsEqualTo(100);
        Check.That(settings.MagnificationLevel).IsEqualTo(4.0);
    }
}
