namespace EyeCtrl.ViewModels.Helpers;

/// <summary>
/// The magnifier's fit rule (magnifier-default-zoom, Part 2, decision 3(a)): the effective circle
/// radius in DIP, so that the view - <c>2 × radius × zoom</c> DIP across - fits within
/// <see cref="ScreenFraction"/> of the screen's shorter side. Purely a function: the zoom stays as
/// set and the settings are never written. Every consumer (the overlay's size and capture, the
/// action menu's display radius and its preview radius) calls this, so they cannot disagree.
/// </summary>
public static class MagnifierFit
{
    /// <summary>Share of the screen's shorter side the view may occupy.</summary>
    public const double ScreenFraction = 0.9;

    /// <summary>The effective radius floor in DIP; the view never shrinks below it.</summary>
    public const int MinimumRadiusDip = 50;

    /// <summary>
    /// The effective circle radius in DIP for a circle of <paramref name="circleRadiusDip"/> DIP at
    /// <paramref name="magnification"/> on a screen whose shorter side is
    /// <paramref name="shorterSideDip"/> DIP. Unchanged when the view already fits; otherwise
    /// <c>⌊ScreenFraction × shorterSide ÷ (2 × zoom)⌋</c>, never below
    /// <see cref="MinimumRadiusDip"/>.
    /// </summary>
    public static int EffectiveCircleRadius(int circleRadiusDip, double magnification, double shorterSideDip)
    {
        double limitDip = ScreenFraction * shorterSideDip;
        if (2.0 * circleRadiusDip * magnification <= limitDip)
            return circleRadiusDip;

        return (int)Math.Max(MinimumRadiusDip, Math.Floor(limitDip / (2.0 * magnification)));
    }
}
