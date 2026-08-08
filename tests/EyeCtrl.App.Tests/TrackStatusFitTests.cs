namespace EyeCtrl.App.Tests;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// TrackStatusWindow is the only window whose ramp-affected content overflows its fixed size,
/// and the 12px ramp floor is what pushed it over (design spec §8.6): measured content height
/// 95.88px against the 94px its Padding="8" leaves inside a 110px window.
///
/// This couples the window's height to the arithmetic, so the exception cannot silently rot
/// if the ramp floor or the row structure changes.
/// </summary>
public class TrackStatusFitTests
{
    /// <summary>
    /// Measured <c>FontFamily.LineSpacing</c> for every member of the active font chain
    /// (<c>"Segoe UI Variable Text, Segoe UI"</c>, and each name in it individually) is
    /// <c>1.330078125</c>. <c>1.33</c> is a deliberate 4-significant-figure rounding of that
    /// value (delta 7.8e-5, about 0.001px at the 12px ramp floor) — not a claim that the ratio
    /// is exactly 1.33. Safe here because <see cref="TheWindowIsTallEnoughForTheRampFloor"/>
    /// only needs an inequality with 4.12px of slack at Height=116, far more than the rounding
    /// could ever move.
    /// </summary>
    private const double LineHeightRatio = 1.33;

    private const double CanvasHeight = 40;   // the eye-dot track box
    private const double Padding = 8;         // Border Padding="8", top and bottom
    private const double HeaderMargin = 4;    // "Track Status" Margin="0,0,0,4"
    private const double DistanceRowMargin = 4;

    [Fact]
    public void TheWindowIsTallEnoughForTheRampFloor()
    {
        var line = Tokens.FontSizeBody * LineHeightRatio;
        var content = (line + HeaderMargin)          // "Track Status"
                    + CanvasHeight                   // eye-dot canvas
                    + (line + DistanceRowMargin)     // Distance row
                    + line;                          // Quality row

        var xaml = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TrackStatusWindow.xaml"));
        var height = double.Parse(
            Regex.Match(xaml, @"Height=""(?<h>[\d.]+)""").Groups["h"].Value,
            System.Globalization.CultureInfo.InvariantCulture);

        var available = height - (2 * Padding);

        Check.WithCustomMessage(
                $"TrackStatusWindow needs {content:F2}px of content space but Height={height} "
                + $"leaves only {available:F2}. At the 12px ramp floor the bottom row clips, "
                + "cutting the descender from \"Quality\" and its value. Design spec §8.6 sets "
                + "Height=116; if the ramp floor or the row structure changed, redo that "
                + "arithmetic rather than widening the window again.")
            .That(available).IsGreaterOrEqualThan(content);
    }
}
