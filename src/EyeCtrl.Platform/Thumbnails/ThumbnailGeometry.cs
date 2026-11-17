namespace EyeCtrl.Platform.Thumbnails;

using EyeCtrl.Core.Models;

/// <summary>
/// The Task Switcher's preview geometry (S4-C), pure and headless: one DIP box + the live DPI scale
/// + the source window's physical size → the destination rectangle a DWM thumbnail is drawn into.
///
/// <para>
/// All four box edges are converted to physical pixels FIRST, each rounded once, and the fitted
/// source rectangle is computed from those rounded edges. That is what keeps the picture inside
/// the drawn box at a fractional scale: converting the size separately and then adding the rounded
/// origin can land one pixel outside. The fit keeps the source's aspect ratio (scale is the smaller
/// of the width and height ratios) and centres the result, so a wide, tall or square window is
/// never stretched and never touches the box edge on its long axis.
/// </para>
///
/// <para>
/// The result is in the destination window's CLIENT coordinates, physical pixels — the coordinate
/// space <c>DwmUpdateThumbnailProperties</c> documents for <c>rcDestination</c>.
/// </para>
/// </summary>
public static class ThumbnailGeometry
{
    /// <summary>
    /// The fitted destination rectangle of a thumbnail inside one preview box.
    /// </summary>
    /// <param name="boxLeftDip">The box's left edge, DIP, relative to the destination window's client area.</param>
    /// <param name="boxTopDip">The box's top edge, DIP, relative to the destination window's client area.</param>
    /// <param name="boxWidthDip">The box width, DIP (168 at S4-C's design).</param>
    /// <param name="boxHeightDip">The box height, DIP (94 at S4-C's design).</param>
    /// <param name="scaleX">The live horizontal DPI scale (physical = DIP × scale).</param>
    /// <param name="scaleY">The live vertical DPI scale.</param>
    /// <param name="sourceWidth">The source window's width in physical pixels.</param>
    /// <param name="sourceHeight">The source window's height in physical pixels.</param>
    /// <returns>
    /// The physical rectangle, aspect-fitted and centred inside the box. Always at least 1×1 and
    /// always inside the box.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A scale or a source dimension is zero or negative. The caller checks the source size through
    /// <see cref="IWindowThumbnailService.TryGetSourceSize"/> first, so this is a programming error
    /// rather than a runtime failure path.
    /// </exception>
    public static PhysicalRect FitDestination(
        double boxLeftDip,
        double boxTopDip,
        double boxWidthDip,
        double boxHeightDip,
        double scaleX,
        double scaleY,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaleY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        // Every EDGE is scaled and rounded once. (Rounding the width and adding it to a rounded
        // origin is the same trap as the 2026-08-17 double-scaled dwell targets: two roundings of
        // one edge can differ by a pixel, which puts the picture over the box's border.)
        var left = RoundToPixel(boxLeftDip * scaleX);
        var top = RoundToPixel(boxTopDip * scaleY);
        var right = RoundToPixel((boxLeftDip + boxWidthDip) * scaleX);
        var bottom = RoundToPixel((boxTopDip + boxHeightDip) * scaleY);

        var boxWidth = Math.Max(1, right - left);
        var boxHeight = Math.Max(1, bottom - top);

        var fit = Math.Min((double)boxWidth / sourceWidth, (double)boxHeight / sourceHeight);
        var width = Math.Clamp(RoundToPixel(sourceWidth * fit), 1, boxWidth);
        var height = Math.Clamp(RoundToPixel(sourceHeight * fit), 1, boxHeight);

        // Centred on the rounded box; the clamp only guards a half-pixel rounding push outward.
        var x = left + RoundToPixel((boxWidth - width) / 2.0);
        var y = top + RoundToPixel((boxHeight - height) / 2.0);
        x = Math.Clamp(x, left, left + boxWidth - width);
        y = Math.Clamp(y, top, top + boxHeight - height);

        return new PhysicalRect(x, y, width, height);
    }

    /// <summary>
    /// One rounding rule for every edge and every source dimension: halves go away from zero, so
    /// a 0.5-pixel edge at 200 % does not depend on the sign of the coordinate. <c>Math.Round</c>'s
    /// default (banker's rounding) would send two equal boxes on opposite sides of the screen to
    /// different pixel counts.
    /// </summary>
    private static int RoundToPixel(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
