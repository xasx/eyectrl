namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Models;

/// <summary>
/// Pure screen-capture region math: a capture square centred on a requested point, the part of
/// that square which lies on the screen, and where that part lands inside the square.
///
/// <para>
/// The square is NEVER translated back onto the screen. Near an edge its origin is negative and
/// the off-screen part is simply absent from the source; the image is still centred on the
/// requested point, so a magnified view draws what was looked at at its centre. The old
/// translate-clamp kept the square on the screen by moving its centre away from the requested
/// point, which is exactly the "I see a part of the screen I wasn't looking at" defect
/// (magnifier-capture-place-investigation, H-A).
/// </para>
///
/// Kept pure so the placement is testable without GDI+ or WPF.
/// </summary>
public static class CaptureRegionMath
{
    /// <summary>
    /// A <see cref="Size"/>-square capture region centred on the requested point, plus the
    /// on-screen source rect and its destination offset inside the square.
    /// </summary>
    /// <param name="OriginX">The square's left edge in screen coordinates — may be NEGATIVE.</param>
    /// <param name="OriginY">The square's top edge in screen coordinates — may be NEGATIVE.</param>
    /// <param name="Size">The square's side in physical pixels.</param>
    /// <param name="SourceX">The on-screen source's left edge; always within [0, screenWidth).</param>
    /// <param name="SourceY">The on-screen source's top edge; always within [0, screenHeight).</param>
    /// <param name="SourceW">The on-screen source's width; always within the screen.</param>
    /// <param name="SourceH">The on-screen source's height; always within the screen.</param>
    /// <param name="DestX">Where the source's left edge lands inside the square (SourceX − OriginX).</param>
    /// <param name="DestY">Where the source's top edge lands inside the square (SourceY − OriginY).</param>
    internal readonly record struct CapturePlacement(
        int OriginX,
        int OriginY,
        int Size,
        int SourceX,
        int SourceY,
        int SourceW,
        int SourceH,
        int DestX,
        int DestY);

    /// <summary>
    /// Places a square of side <c>2 × radius</c> around <paramref name="centerX"/>,
    /// <paramref name="centerY"/>, and intersects it with the screen.
    /// </summary>
    /// <returns>
    /// The placement, or null when the radius is not positive or the square lies entirely off the
    /// screen. P is on the screen in production (<c>ActivationOfferEngine</c> clamps or refuses
    /// off-screen points), so an empty intersection is a defect and is treated as a failed capture.
    /// </returns>
    internal static CapturePlacement? Place(
        int centerX, int centerY, int radius, int screenWidth, int screenHeight)
    {
        var size = radius * 2;
        if (size <= 0) return null;

        var originX = centerX - radius;
        var originY = centerY - radius;

        var sourceX = Math.Max(0, originX);
        var sourceY = Math.Max(0, originY);
        var sourceRight = Math.Min(screenWidth, originX + size);
        var sourceBottom = Math.Min(screenHeight, originY + size);
        var sourceW = sourceRight - sourceX;
        var sourceH = sourceBottom - sourceY;
        if (sourceW <= 0 || sourceH <= 0) return null;

        return new CapturePlacement(
            originX,
            originY,
            size,
            sourceX,
            sourceY,
            sourceW,
            sourceH,
            sourceX - originX,
            sourceY - originY);
    }

    /// <summary>
    /// The centre of a capture square, in physical pixels. Single derivation for both the square a
    /// capture actually produced and one predicted before it runs. A centred square's centre is
    /// exactly the requested point (truncated to whole pixels by the capture).
    /// </summary>
    public static PhysicalPoint CenterOf((int x, int y, int size) region)
        => new(region.x + region.size / 2.0, region.y + region.size / 2.0);
}
