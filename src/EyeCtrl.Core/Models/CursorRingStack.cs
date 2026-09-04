namespace EyeCtrl.Core.Models;

/// <summary>
/// One concentric band of the cursor ring's stroke stack. Widths are logical pixels;
/// <see cref="DashPeriod"/> and <see cref="DashOffset"/> are lengths ALONG the arc, in the
/// same units — the renderer converts them into WPF's multiples-of-StrokeThickness.
/// </summary>
/// <param name="Hex">The hex colour string (e.g. "#A5F3FC").</param>
/// <param name="Thickness">The band width in logical pixels.</param>
/// <param name="Opacity">The opacity level, 0 to 1.</param>
/// <param name="DashPeriod">Dash length, equal to the gap length. 0 for a solid band.</param>
/// <param name="DashOffset">Dash offset along the arc.</param>
public readonly record struct RingBand(
    string Hex, double Thickness, double Opacity, double DashPeriod, double DashOffset);

/// <summary>The two bands a cursor ring is drawn from. <see cref="Outer"/> paints first.</summary>
public readonly record struct CursorRingStack(RingBand Outer, RingBand Core);

/// <summary>
/// Maps a <see cref="CursorRingStyle"/> onto the two bands that render it.
///
/// Pure and framework-free on purpose: EyeCtrl.Core cannot reference EyeCtrl.Controls (the
/// dependency runs the other way), so the palette colours arrive as hex strings from the
/// caller rather than from Tokens. Keeping the decision here — rather than in the overlay —
/// is what lets the geometry be tested headlessly.
/// </summary>
public static class CursorRingStacks
{
    /// <summary>Builds the stack for <paramref name="style"/>.</summary>
    /// <param name="style">The cursor ring style to render.</param>
    /// <param name="size">Ring diameter in logical pixels; sets the dash period.</param>
    /// <param name="thickness">The core band's width; the outline derives from it.</param>
    /// <param name="opacity">User opacity. Applies to the core band only.</param>
    /// <param name="markHex">The user's ring colour.</param>
    /// <param name="haloHex">Tokens.StrokeHalo.</param>
    /// <param name="lightHex">Tokens.TextPrimary.</param>
    public static CursorRingStack For(
        CursorRingStyle style, double size, double thickness, double opacity,
        string markHex, string haloHex, string lightHex) => style switch
    {
        CursorRingStyle.WeightedSandwich => new CursorRingStack(
            new RingBand(haloHex, thickness + 4, 1.0, 0, 0),
            new RingBand(markHex, thickness, opacity, 0, 0)),
        CursorRingStyle.AchromaticReticle => new CursorRingStack(
            new RingBand(lightHex, thickness + 4, 1.0, 0, 0),
            new RingBand(haloHex, thickness, opacity, 0, 0)),
        _ => throw new ArgumentOutOfRangeException(
            nameof(style), style, "Unknown cursor ring style")
    };
}
