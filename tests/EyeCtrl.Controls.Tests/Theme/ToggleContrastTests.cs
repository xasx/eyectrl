namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// Toggle state must be carried by lightness, not hue (spec §4.1.1 rule 7).
/// The pre-migration pair managed 1.52:1 and was near-invisible to a deuteranope.
/// </summary>
public class ToggleContrastTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void ToggleOnAndOff_DifferByAtLeastThreeToOne()
    {
        var ratio = Contrast(Tokens.ToggleOn, Tokens.ToggleOff);
        Check.WithCustomMessage(
                $"ToggleOn/ToggleOff contrast is {ratio:F2}:1; spec §4.1.1 rule 7 "
                + "requires >= 3.0 so state survives any colour-vision deficiency.")
            .That(ratio).IsStrictlyGreaterThan(3.0);
    }

    [Fact]
    public void ToggleOff_IsNotRed()
    {
        // "Off" is not an error. Red is reserved for AccentDanger (§4.1.2).
        var c = Tokens.ToggleOff;
        Check.WithCustomMessage("ToggleOff must not be red-dominant")
            .That(c.R <= c.B).IsTrue();
    }
}
