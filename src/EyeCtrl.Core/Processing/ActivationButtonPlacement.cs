namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// Where the activation offer's button centre B goes for a settle at P (activation plan, M8): just
/// outside the ring edge, diagonally toward the screen's centre, clamped so the hit area stays on
/// screen, and carrying the chrome flip — if B's hit area intersects an EyeCtrl chrome zone, the
/// opposite side of the ring is tried once, and if that also intersects, no button is placed.
///
/// <para>
/// Pure, in physical pixels: the caller supplies the live scale and the primary screen's bounds as
/// plain data, and this class never reads a settings object or a screen API. The one DIP input is the
/// shipped <see cref="LayoutConstants.ActivationButtonOffset"/>, converted here with the same scale the
/// settle point is divided by.
/// </para>
///
/// <para>
/// "Opposite side" is the negation of the diagonal direction: a point above and left of the screen's
/// centre has its button below and right of it, and the flip puts it above and left instead. Both
/// candidates are clamped identically, so the return value is always a real on-screen centre — the
/// chrome check happens on the clamped points, which is what the drawn button will be.
/// </para>
/// </summary>
public static class ActivationButtonPlacement
{
    /// <summary>The clearance the offset is floored at, beyond hit + R: a few px, since the drop guard
    /// is strict. Below the floor, the button stops tracking the ring edge and sits just outside the
    /// zone where a hold at P could reach it (review A, R2-1).</summary>
    public const double MinimumClearancePx = 30.0;

    /// <summary>Where the button went, and whether one could be placed at all.</summary>
    /// <param name="ButtonCenter">The clamped on-screen button centre B, in physical pixels.</param>
    /// <param name="Placed">False when no side of the ring is usable: both intersect chrome, or the one
    /// that does not was clamped so close to P that a plain hold at P would sit inside B's hit area —
    /// which would be activation by rest (review A, F1).</param>
    public readonly record struct Result(PhysicalPoint ButtonCenter, bool Placed);

    /// <summary>
    /// Computes B for a settle at <paramref name="point"/>.
    /// </summary>
    /// <param name="point">The settle point P, in physical screen pixels.</param>
    /// <param name="ringSizeDip">The cursor ring's size in DIP (<c>CursorSettings.CircleSize</c>).</param>
    /// <param name="offsetDip">The gap from the ring edge to B, in DIP (the shipped 55).</param>
    /// <param name="scale">The live DPI scale, applied to the DIP inputs.</param>
    /// <param name="buttonRadiusPx">The button's hit radius in physical pixels
    /// (<see cref="LayoutConstants.ActivationButtonHitRadiusPx"/>).</param>
    /// <param name="fixationRadiusPx">The live fixation radius R in physical pixels. A candidate B is
    /// unplaceable when it sits within hit + R of P: every sample of the held run would then be inside
    /// the hit area, and a plain hold would fire.</param>
    /// <param name="screenBoundsPx">The primary screen's bounds in physical pixels.</param>
    /// <param name="chromeZones">EyeCtrl's chrome zones, in physical pixels; may be null/empty.</param>
    public static Result Place(
        PhysicalPoint point,
        double ringSizeDip,
        double offsetDip,
        double scale,
        double buttonRadiusPx,
        double fixationRadiusPx,
        PhysicalRect screenBoundsPx,
        IReadOnlyList<PhysicalRect>? chromeZones = null)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive and finite.");
        if (!double.IsFinite(buttonRadiusPx) || buttonRadiusPx < 0)
            throw new ArgumentOutOfRangeException(nameof(buttonRadiusPx), buttonRadiusPx, "Button radius must not be negative.");
        if (!double.IsFinite(fixationRadiusPx) || fixationRadiusPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixationRadiusPx), fixationRadiusPx, "Fixation radius must be positive and finite.");

        double offsetPx = (Math.Max(0, ringSizeDip) / 2 + Math.Max(0, offsetDip)) * scale;
        double dropDistancePx = buttonRadiusPx + fixationRadiusPx;

        // Floor the offset so a button is always placeable: without this, a small ring or a low-DPI
        // display puts the candidate inside hit + R of P and every configuration offers nothing, so
        // activation would silently be impossible once ticket 8 wires the engine (review A, R2-1).
        // Above the floor the button keeps following the ring size.
        offsetPx = Math.Max(offsetPx, dropDistancePx + MinimumClearancePx);
        double axisPx = offsetPx / Math.Sqrt(2);

        // Toward the screen's centre, the old edge-aware direction (7a85d328). Compared in physical
        // pixels — the centre of the bounds, not of the origin — so a non-zero screen origin and any
        // scale stay exact.
        double dx = point.X < screenBoundsPx.X + (screenBoundsPx.Width / 2) ? 1.0 : -1.0;
        double dy = point.Y < screenBoundsPx.Y + (screenBoundsPx.Height / 2) ? 1.0 : -1.0;

        var preferred = Clamp(new PhysicalPoint(point.X + (dx * axisPx), point.Y + (dy * axisPx)),
            buttonRadiusPx, screenBoundsPx);
        if (IsUsable(preferred, point, dropDistancePx, buttonRadiusPx, chromeZones))
            return new Result(preferred, Placed: true);

        // The flip points toward the nearest corner; the clamp can pull it back onto P. It is only
        // usable when it is both chrome-free and far enough that the hold at P stays outside its hit
        // area — a button on top of P would be activation by rest (review A, F1).
        var flipped = Clamp(new PhysicalPoint(point.X - (dx * axisPx), point.Y - (dy * axisPx)),
            buttonRadiusPx, screenBoundsPx);
        return IsUsable(flipped, point, dropDistancePx, buttonRadiusPx, chromeZones)
            ? new Result(flipped, Placed: true)
            : new Result(flipped, Placed: false);
    }

    private static bool IsUsable(
        PhysicalPoint candidate,
        PhysicalPoint point,
        double dropDistancePx,
        double buttonRadiusPx,
        IReadOnlyList<PhysicalRect>? chromeZones)
        => candidate.DistanceTo(point) > dropDistancePx
           && !IntersectsChrome(candidate, buttonRadiusPx, chromeZones);

    /// <summary>
    /// Clamps a button centre so its whole hit circle stays inside the screen. A screen too small for
    /// the circle collapses to the centre rather than throwing (<c>Math.Clamp</c>'s minimum above its
    /// maximum).
    /// </summary>
    private static PhysicalPoint Clamp(PhysicalPoint button, double radius, PhysicalRect screen)
        => new(ClampAxis(button.X, screen.X, screen.Width, radius),
               ClampAxis(button.Y, screen.Y, screen.Height, radius));

    private static double ClampAxis(double value, double start, double length, double radius)
    {
        double min = start + radius;
        double max = start + length - radius;
        if (max < min)
            return start + (length / 2);
        return Math.Clamp(value, min, max);
    }

    private static bool IntersectsChrome(
        PhysicalPoint center, double radius, IReadOnlyList<PhysicalRect>? zones)
    {
        if (zones is null)
            return false;
        for (int i = 0; i < zones.Count; i++)
        {
            if (IntersectsZone(center, radius, zones[i]))
                return true;
        }
        return false;
    }

    /// <summary>Circle-rectangle intersection: the distance from the centre to the rect's closest point
    /// is within the radius. A centre inside the rect is distance zero.</summary>
    private static bool IntersectsZone(PhysicalPoint center, double radius, PhysicalRect zone)
    {
        double closestX = Math.Clamp(center.X, zone.X, zone.X + zone.Width);
        double closestY = Math.Clamp(center.Y, zone.Y, zone.Y + zone.Height);
        double dx = center.X - closestX;
        double dy = center.Y - closestY;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }
}
