namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>
/// Picks the screen point for the calibration window's gaze Cancel target.
/// <para>
/// The rule is the largest-empty-spot one: among all points at least one hit radius inside the
/// display, choose the one whose distance to the NEAREST calibration target is as large as
/// possible. A user reaching for it is therefore as far from every target as the screen allows, so
/// a glance towards Cancel cannot be mistaken for an attempt at a target — and the dwell's long
/// window (see <c>CalibrationWindow</c>) has the clearance to tell a deliberate arrival from a
/// glance on the way to one.
/// </para>
/// <para>
/// The point is computed from the SAME target list the collector gates on
/// (<see cref="CalibrationService.TargetPositions"/>), never from a private re-run of
/// <see cref="CalibrationTargetPositionHelper"/>: two independent computations are exactly how a
/// drawn target and an enforced target come to differ.
/// </para>
/// </summary>
public static class CalibrationCancelPlacement
{
    /// <summary>
    /// The Cancel target's hit radius in DIP. The shipped disc is 144 DIP across, so this is the
    /// disc's own circumscribed radius; the ticket's floor is 50 DIP, and the extra size is for a
    /// user who cannot hold a small area — the point is the way OUT of a session, so missing it is
    /// costly.
    /// </summary>
    public const double HitRadiusDip = 72.0;

    /// <summary>
    /// Clearance in physical pixels the hit circle keeps beyond its own radius from every
    /// calibration target. The maximin search already leaves far more on any ordinary display; this
    /// is the floor a test can assert without restating a particular screen's geometry.
    /// </summary>
    public const double MinimumTargetGapPx = 24.0;

    /// <summary>
    /// The Cancel target's centre in physical screen pixels: the point inside the radius-inset
    /// display whose distance to the nearest target is largest.
    /// </summary>
    /// <param name="targets">The session's target positions, in physical screen pixels. Empty means
    /// no session layout to avoid: the inset centre is returned.</param>
    /// <param name="screenSize">Display size in physical screen pixels.</param>
    /// <param name="radiusPx">The hit radius in physical pixels, which is also the inset from every
    /// screen edge.</param>
    /// <param name="keepOut">
    /// Rectangles the disc must not cover, in physical screen pixels — the calibration window's own
    /// progress card is the one that matters. Each is inflated by <paramref name="radiusPx"/> on
    /// every side (a conservative, axis-aligned clearance: the disc's centre must sit outside the
    /// inflated rectangle), and candidates inside are skipped. A display on which every candidate is
    /// blocked falls back to the unobstructed placement rather than returning no answer; the window
    /// is a fixed piece of chrome, not a reason to strand the user.
    /// </param>
    /// <returns>The chosen centre, in physical screen pixels.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targets"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radiusPx"/> is negative, NaN,
    /// or infinite.</exception>
    public static PhysicalPoint GetPosition(
        IReadOnlyList<PhysicalPoint> targets, PhysicalPoint screenSize, double radiusPx,
        IReadOnlyList<PhysicalRect>? keepOut = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (!double.IsFinite(radiusPx) || radiusPx < 0)
            throw new ArgumentOutOfRangeException(nameof(radiusPx), radiusPx,
                "The hit radius must be a finite, non-negative number of physical pixels.");

        var minX = radiusPx;
        var maxX = screenSize.X - radiusPx;
        var minY = radiusPx;
        var maxY = screenSize.Y - radiusPx;

        // A display too small to hold the inset box at all (a synthetic or clipped surface, never
        // this app's monitor): the centre is the one point that is at least as far from every edge
        // as the geometry allows.
        if (maxX < minX || maxY < minY)
            return new PhysicalPoint(screenSize.X / 2, screenSize.Y / 2);

        if (targets.Count == 0)
            return new PhysicalPoint((minX + maxX) / 2, (minY + maxY) / 2);

        // A maximin search over the inset rectangle. 128 samples per axis find the basin; two
        // refinement passes narrow it. The optimum's position is not a closed form for an arbitrary
        // target set, and this runs ONCE per session, so an explicit bounded search is the honest
        // implementation rather than a candidate list that could miss an interior optimum.
        const int samplesPerAxis = 128;
        const int refinements = 2;
        const int refinementSamples = 16;

        var stepX = (maxX - minX) / samplesPerAxis;
        var stepY = (maxY - minY) / samplesPerAxis;
        if (stepX <= 0) stepX = maxX - minX;
        if (stepY <= 0) stepY = maxY - minY;

        var best = new PhysicalPoint(minX, minY);
        var bestDistance = -1.0;

        void Consider(double x, double y)
        {
            if (IsInsideKeepOut(x, y, keepOut, radiusPx)) return;

            var distance = NearestTargetDistance(targets, x, y);
            // Strictly greater only, scanning y then x ascending: an exact tie keeps the point
            // encountered first, which makes the result deterministic (topmost, then leftmost).
            if (distance <= bestDistance + 1e-9) return;
            bestDistance = distance;
            best = new PhysicalPoint(x, y);
        }

        for (var j = 0; j <= samplesPerAxis; j++)
        {
            var y = minY + (stepY * j);
            if (y > maxY) y = maxY;
            for (var i = 0; i <= samplesPerAxis; i++)
            {
                var x = minX + (stepX * i);
                if (x > maxX) x = maxX;
                Consider(x, y);
            }
        }

        for (var pass = 0; pass < refinements; pass++)
        {
            var halfX = stepX;
            var halfY = stepY;
            stepX = (2 * halfX) / refinementSamples;
            stepY = (2 * halfY) / refinementSamples;

            var fromX = Math.Max(minX, best.X - halfX);
            var toX = Math.Min(maxX, best.X + halfX);
            var fromY = Math.Max(minY, best.Y - halfY);
            var toY = Math.Min(maxY, best.Y + halfY);

            for (var j = 0; j <= refinementSamples; j++)
            {
                var y = fromY + (stepY * j);
                if (y > toY) y = toY;
                for (var i = 0; i <= refinementSamples; i++)
                {
                    var x = fromX + (stepX * i);
                    if (x > toX) x = toX;
                    Consider(x, y);
                }
            }
        }

        // Every candidate was blocked (a keep-out covering the whole usable box, which no shipped
        // display does): the unobstructed rule is still a better answer than no answer — the way out
        // must exist even when the chrome is in the way.
        if (bestDistance < 0 && keepOut is { Count: > 0 })
            return GetPosition(targets, screenSize, radiusPx);

        return best;
    }

    /// <summary>
    /// Whether the disc's centre at (<paramref name="x"/>, <paramref name="y"/>) would sit inside a
    /// keep-out rectangle inflated by the disc's radius — the conservative axis-aligned rule: a
    /// point diagonally past a corner is also excluded, though the circle itself might clear it.
    /// </summary>
    private static bool IsInsideKeepOut(
        double x, double y, IReadOnlyList<PhysicalRect>? keepOut, double radiusPx)
    {
        if (keepOut is null) return false;
        for (var i = 0; i < keepOut.Count; i++)
        {
            var rect = keepOut[i];
            if (x >= rect.X - radiusPx && x <= rect.X + rect.Width + radiusPx
                && y >= rect.Y - radiusPx && y <= rect.Y + rect.Height + radiusPx)
                return true;
        }

        return false;
    }

    private static double NearestTargetDistance(IReadOnlyList<PhysicalPoint> targets, double x, double y)
    {
        var nearest = double.MaxValue;
        for (var i = 0; i < targets.Count; i++)
        {
            var dx = x - targets[i].X;
            var dy = y - targets[i].Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < nearest) nearest = distance;
        }

        return nearest;
    }
}
