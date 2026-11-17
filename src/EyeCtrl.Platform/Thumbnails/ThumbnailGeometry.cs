namespace EyeCtrl.Platform.Thumbnails;

using EyeCtrl.Core.Models;

/// <summary>
/// The Task Switcher's preview geometry (S4-C), pure and headless: one PHYSICAL box + the source
/// window's physical size → the destination rectangle a DWM thumbnail is drawn into.
///
/// <para>
/// The caller derives the box with <c>PointToScreen</c> — the same convention as the switcher's
/// dwell targets, which folds in Windows' DPI virtualization — and passes the physical edges here;
/// no DPI scale is reconstructed by hand (the 2026-08-17 double-scale lesson). This helper only
/// rounds and fits.
/// </para>
///
/// <para>
/// All four box edges are rounded once, away from zero, FIRST, and the fitted source rectangle is
/// computed from those rounded edges. That is what keeps the picture inside the drawn box: rounding
/// the size separately and adding it to a rounded origin can land one pixel outside. The fit keeps
/// the source's aspect ratio (the smaller of the width and height ratios) and centres the result,
/// so a wide, tall or square window is never stretched and never touches the box edge on its long
/// axis.
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
    /// The fitted destination rectangle of a thumbnail inside one preview box, both in physical
    /// pixels relative to the destination window's client area.
    /// </summary>
    /// <param name="boxLeft">The box's left edge, physical pixels.</param>
    /// <param name="boxTop">The box's top edge, physical pixels.</param>
    /// <param name="boxWidth">The box width, physical pixels (336 at S4-C's design at 200 %).</param>
    /// <param name="boxHeight">The box height, physical pixels (188 at 200 %).</param>
    /// <param name="sourceWidth">The source window's width in physical pixels.</param>
    /// <param name="sourceHeight">The source window's height in physical pixels.</param>
    /// <returns>
    /// The physical rectangle, aspect-fitted and centred inside the box. Always at least 1×1 and
    /// always inside the box.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The box is empty or a source dimension is zero or negative. The caller checks the source
    /// size through <see cref="IWindowThumbnailService.TryGetSourceSize"/> first, so this is a
    /// programming error rather than a runtime failure path.
    /// </exception>
    public static PhysicalRect FitDestination(
        double boxLeft,
        double boxTop,
        double boxWidth,
        double boxHeight,
        int sourceWidth,
        int sourceHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(boxHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        // Every EDGE is rounded once. (Rounding the width and adding it to a rounded origin is the
        // same trap as the 2026-08-17 double-scaled dwell targets: two roundings of one edge can
        // differ by a pixel, which puts the picture over the box's border.)
        var left = RoundToPixel(boxLeft);
        var top = RoundToPixel(boxTop);
        var right = RoundToPixel(boxLeft + boxWidth);
        var bottom = RoundToPixel(boxTop + boxHeight);

        var pixelWidth = Math.Max(1, right - left);
        var pixelHeight = Math.Max(1, bottom - top);

        var fit = Math.Min((double)pixelWidth / sourceWidth, (double)pixelHeight / sourceHeight);
        var width = Math.Clamp(RoundToPixel(sourceWidth * fit), 1, pixelWidth);
        var height = Math.Clamp(RoundToPixel(sourceHeight * fit), 1, pixelHeight);

        // Centred on the rounded box; the clamp only guards a half-pixel rounding push outward.
        var x = left + RoundToPixel((pixelWidth - width) / 2.0);
        var y = top + RoundToPixel((pixelHeight - height) / 2.0);
        x = Math.Clamp(x, left, left + pixelWidth - width);
        y = Math.Clamp(y, top, top + pixelHeight - height);

        return new PhysicalRect(x, y, width, height);
    }

    /// <summary>
    /// One rounding rule for every edge and every source dimension: halves go away from zero, so a
    /// 0.5-pixel edge at 200 % does not depend on the sign of the coordinate. <c>Math.Round</c>'s
    /// default (banker's rounding) would send two equal boxes on opposite sides of the screen to
    /// different pixel counts.
    /// </summary>
    private static int RoundToPixel(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
