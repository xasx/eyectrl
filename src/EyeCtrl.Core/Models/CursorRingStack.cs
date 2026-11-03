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
        CursorRingStyle.BiTonalSegments => BiTonal(size, thickness, opacity, markHex, haloHex),
        _ => throw new ArgumentOutOfRangeException(
            nameof(style), style, "Unknown cursor ring style")
    };

    /// <summary>
    /// Interleaved halo and mark segments at one radius and width.
    ///
    /// The dash length is a 32nd of the circumference, so a full on/off cycle is a 16th and
    /// the pattern closes at the wrap point for any diameter. The mark band is offset by ONE
    /// dash length — half a cycle — which is what drops its segments into the halo's gaps.
    /// An offset of a whole cycle would put the two in phase and quietly void the style.
    /// </summary>
    private static CursorRingStack BiTonal(
        double size, double thickness, double opacity, string markHex, string haloHex)
    {
        var dash = Math.PI * size / 32.0;
        return new CursorRingStack(
            new RingBand(haloHex, thickness, 1.0, dash, 0),
            new RingBand(markHex, thickness, opacity, dash, dash));
    }

    /// <summary>Angular dash segments around a solid blocked ring (N).</summary>
    public const int BlockedDashSegments = 32;

    /// <summary>
    /// C10's blocked look, derived purely from the rest stack so the window never invents dash
    /// geometry:
    /// <list type="bullet">
    /// <item>Solid styles (WeightedSandwich, AchromaticReticle): one angular DIP dash, the SAME
    /// angular length for both bands — <c>π·(size − t_band)/(2N)</c> — so both bands break at the
    /// same angles and the pattern closes at the wrap. <c>StrokeDashArray</c> is in multiples of
    /// StrokeThickness, which is why the window's <c>Apply</c> gets a DIP length per band.</item>
    /// <item>BiTonalSegments: the interleave is preserved at half the period, with the mark band
    /// offset by exactly ONE new dash. Overwriting both with one phase would paint the mark band
    /// over the halo band and leave a one-colour ring (ring review r01 F1).</item>
    /// </list>
    /// Colour, opacity and thickness are untouched: C1's lesson is that the pointer must never get
    /// fainter, and the owner's decision is dash-only.
    /// </summary>
    /// <param name="rest">The settings-driven stack.</param>
    /// <param name="size">The ring diameter in DIP (the same value the ellipses are sized to).</param>
    /// <returns>The blocked stack.</returns>
    public static CursorRingStack Blocked(CursorRingStack rest, double size)
    {
        if (size <= 0)
            return rest;

        var dashed = rest.Outer.DashPeriod > 0 || rest.Core.DashPeriod > 0;
        if (dashed)
        {
            var outerPeriod = Math.Max(rest.Outer.DashPeriod / 2, 1e-6);
            var corePeriod = Math.Max(rest.Core.DashPeriod / 2, 1e-6);
            return new CursorRingStack(
                rest.Outer with { DashPeriod = outerPeriod, DashOffset = 0 },
                rest.Core with { DashPeriod = corePeriod, DashOffset = corePeriod });
        }

        return new CursorRingStack(
            BlockedBand(rest.Outer, size),
            BlockedBand(rest.Core, size));
    }

    private static RingBand BlockedBand(RingBand band, double size)
    {
        var radius = Math.Max(0, (size - band.Thickness) / 2);
        var dash = 2 * Math.PI * radius / (2.0 * BlockedDashSegments);
        return band with { DashPeriod = dash, DashOffset = 0 };
    }
}
