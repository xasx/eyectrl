namespace EyeCtrl.App.Tests;

using System;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// The dim render's contrast claim, as an executable matrix. A disabled action-menu item
/// (<see cref="EyeCtrl.ViewModels.ActionMenuItemViewModel.IsDisabled"/>) is drawn by
/// <c>ActionMenuWindow.Show</c> (through <c>ActionMenuRingPainter</c>) as a whole group at <c>DisabledOpacity</c> over whatever the
/// screen behind the transparent overlay shows, so both its icon AND its fill blend with the
/// backdrop. The claim has two halves:
///
/// <list type="bullet">
/// <item><description>
/// <b>Identifiability</b> — the dimmed icon stays tellable from the dimmed button fill
/// (<c>IdentifiabilityFloor</c>), so a dimmed item still reads as its icon, not as a hole.
/// </description></item>
/// <item><description>
/// <b>Separability</b> — whichever of the dimmed icon/fill pair was the STRONGER edge against
/// the backdrop while live is still separable from the LIVE icon colour
/// (<c>SeparabilityFloor</c>), so dimmed items stay visibly subordinate to live ones.
/// </description></item>
/// </list>
///
/// The matrix is the <c>HaloContrastTests</c> backdrop set crossed with every theme — the same
/// nine backdrops the §4.1.3 guarantee is validated over — plus one own-fill silhouette row per
/// theme (backdrop = the theme's own fill), which is the hardest case: the dimmed fill against
/// the colour the button is made of.
/// </summary>
public class ActionIconDimContrastTests
{
    // External review I2: the matrix validates the PRODUCT opacity, not a copy of it — if the
    // window's dim value changes, this matrix must re-run against the new number.
    private static double DisabledOpacity => ActionMenuRingPainter.DisabledOpacity;

    private const double IdentifiabilityFloor = 2.0;
    private const double SeparabilityFloor = 1.5;

    // ONE MemberData holding the CARTESIAN PRODUCT - two [MemberData] attributes on a
    // two-parameter theory are two separate one-column row sources, not a product.
    public static TheoryData<string, ITheme> BackdropThemePairs()
    {
        var data = new TheoryData<string, ITheme>();
        foreach (var backdrop in HaloContrastTests.Backdrops)   // the repo's nine backdrops
            foreach (var theme in ThemeRegistry.All)             // ThemeRegistry.cs:15 — xUnit v3
                                                                 // TheoryData<T> enumerates rows, not T
                data.Add(backdrop, theme);
        // Own-fill silhouette case (round-5 F7): one row per theme whose backdrop equals the
        // theme's own fill hex — the hardest silhouette case.
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme.SurfaceMedium.ToString(), theme);
        return data;
    }

    [Theory]
    [MemberData(nameof(BackdropThemePairs))]
    public void DimmedIcon_StaysIdentifiable_AndSeparableFromLive(string backdrop, ITheme theme)
    {
        var icon = theme.TextPrimary;      // DwellArcButton's icon brush (NOT theme.IconColor - no such member)
        var fill = theme.SurfaceMedium;    // the button fill (DwellArcButton.cs:165)
        // The brief's snippet passed the raw hex string to Contrast(Color, Color), which cannot
        // compile; parsed here once instead — same value Blend blends against.
        var backdropColor = (Color)ColorConverter.ConvertFromString(backdrop);

        var dimmed = Blend(icon, DisabledOpacity, backdrop);
        var dimmedFill = Blend(fill, DisabledOpacity, backdrop);

        Check.That(Contrast(dimmed, dimmedFill)).IsGreaterOrEqualThan(IdentifiabilityFloor);
        Check.That(Contrast(Contrast(icon, backdropColor) > Contrast(fill, backdropColor) ? dimmed : dimmedFill, icon)).IsStrictlyGreaterThan(SeparabilityFloor);
    }

    private static Color Blend(Color source, double opacity, string backdropHex)
    {
        var backdrop = (Color)ColorConverter.ConvertFromString(backdropHex);
        return Color.FromRgb(
            (byte)(source.R * opacity + backdrop.R * (1 - opacity)),
            (byte)(source.G * opacity + backdrop.G * (1 - opacity)),
            (byte)(source.B * opacity + backdrop.B * (1 - opacity)));
    }

    private static double Contrast(Color a, Color b)
    {
        double L(Color c)
        {
            double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            return 0.2126 * Channel(c.R / 255.0) + 0.7152 * Channel(c.G / 255.0) + 0.0722 * Channel(c.B / 255.0);
        }
        var lighter = Math.Max(L(a), L(b));
        var darker = Math.Min(L(a), L(b));
        return (lighter + 0.05) / (darker + 0.05);
    }
}
