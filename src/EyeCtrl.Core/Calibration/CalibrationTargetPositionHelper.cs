namespace EyeCtrl.Core.Calibration;

using EyeCtrl.Core.Models;

/// <summary>Generates screen positions for calibration targets, distributed across the screen with margins.</summary>
public static class CalibrationTargetPositionHelper
{
    /// <summary>
    /// Margin as a fraction of the display's SHORTER side, above <see cref="MinimumMarginPx"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Was 0.2, which left the outer fifth of the display uncalibrated: on the 3840x2400 panel this runs
    /// on, the corner targets sat 768 px / 480 px in from the edges and the user reported, correctly,
    /// that "the calibration is not using the whole screen". A transform fitted only over the middle
    /// 60 % is being extrapolated everywhere else.
    /// </para>
    /// <para>
    /// Scaled by the shorter side rather than per-axis, so the margin is the same number of PIXELS on
    /// all four edges — a wide display should not get a wider left/right border. Same convention as
    /// <see cref="CalibrationGeometry.BandRadiusPx"/>, and for the same reason.
    /// </para>
    /// </remarks>
    public const double MarginScreenFraction = 0.05;

    /// <summary>
    /// Floor for the margin in physical pixels, so a target is never drawn under a reserved screen area.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The overlay is a full-screen <c>Topmost</c> window sized to the whole display (not the working
    /// area) and is click-through, so a target over the taskbar would still be DRAWN and could still be
    /// looked at. But z-order between two topmost windows is not guaranteed, and a calibration target
    /// the user cannot see is a target they cannot complete — which now fails the session rather than
    /// silently averaging garbage. This floor keeps the dot clear rather than relying on that z-order.
    /// </para>
    /// <para>
    /// 80 px is chosen against the worst realistic case on each side of the DPI range. A default Windows
    /// taskbar is 48 logical px: 48 physical at 100 % scaling, where 80 px clears it with the 20 px
    /// target dot fully above it; 96 physical at 200 % scaling, where the fraction dominates anyway
    /// (0.05 x 2400 = 120 px on this display) and clears it by the same margin. It only binds below
    /// ~1600 px on the shorter side.
    /// </para>
    /// <para>
    /// It does NOT know where the taskbar actually is — Core is framework-agnostic and is handed only a
    /// screen size. A user with a tall, top-docked or auto-hidden taskbar is outside what this can
    /// reason about; the failure mode is visible (a dot in an awkward place) rather than silent.
    /// </para>
    /// </remarks>
    public const double MinimumMarginPx = 80.0;

    /// <summary>
    /// Ceiling for the margin as a fraction of the shorter side, so a small display cannot collapse the
    /// usable box. Without it, <see cref="MinimumMarginPx"/> on a 320x240 test surface would leave a
    /// 160x80 area and stack targets on top of each other.
    /// </summary>
    private const double MaximumMarginScreenFraction = 0.2;

    /// <summary>The margin in physical pixels applied to all four edges for this display.</summary>
    /// <param name="screenSize">Display size in physical pixels.</param>
    public static double MarginPx(PhysicalPoint screenSize)
    {
        var shorter = Math.Min(screenSize.X, screenSize.Y);
        return Math.Min(
            Math.Max(MinimumMarginPx, MarginScreenFraction * shorter),
            MaximumMarginScreenFraction * shorter);
    }

    /// <summary>
    /// Generates a list of calibration target positions in physical screen pixels, ordered as a
    /// neighbour-to-neighbour walk so the next target is always close to the last.
    /// </summary>
    /// <param name="count">Number of calibration points (3–16).</param>
    /// <param name="screenSize">Screen dimensions in physical pixels.</param>
    /// <returns>Ordered list of target positions in physical screen pixels.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="count"/> is outside 3–16.</exception>
    public static IReadOnlyList<PhysicalPoint> GetPositions(int count, PhysicalPoint screenSize)
    {
        if (count < 3 || count > 16)
            throw new ArgumentOutOfRangeException(nameof(count), count,
                "Point count must be between 3 and 16.");

        var w = screenSize.X;
        var h = screenSize.Y;
        var margin = MarginPx(screenSize);
        var minX = margin;
        var maxX = w - margin;
        var minY = margin;
        var maxY = h - margin;
        var cx = w * 0.5;
        var cy = h * 0.5;

        List<PhysicalPoint> all;

        if (count == 3)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY),
                new(maxX, minY),
                new(cx, maxY),
            };
        }
        else if (count == 4)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else if (count == 5)
        {
            all = new List<PhysicalPoint>
            {
                new(minX, minY), new(maxX, minY),
                new(cx, cy),
                new(minX, maxY), new(maxX, maxY),
            };
        }
        else
        {
            int cols, rows;
            if (count <= 9) { cols = 3; rows = 3; }
            else { cols = 4; rows = 4; }

            all = new List<PhysicalPoint>();
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var x = cols == 1 ? cx : minX + (maxX - minX) * c / (cols - 1);
                var y = rows == 1 ? cy : minY + (maxY - minY) * r / (rows - 1);
                all.Add(new PhysicalPoint(x, y));
            }
        }

        List<PhysicalPoint> selected;
        if (count >= all.Count)
        {
            selected = new List<PhysicalPoint>(all);
        }
        else
        {
            selected = new List<PhysicalPoint>(count);
            var seeds = new[]
            {
                new PhysicalPoint(minX, minY), new PhysicalPoint(maxX, minY),
                new PhysicalPoint(minX, maxY), new PhysicalPoint(maxX, maxY),
                new PhysicalPoint(cx, cy),
            };
            foreach (var seed in seeds)
            {
                if (selected.Count >= count) break;
                PhysicalPoint nearest = default;
                double nearestDist = double.MaxValue;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = DistSq(p, seed);
                    if (d < nearestDist) { nearestDist = d; nearest = p; }
                }
                selected.Add(nearest);
            }
            while (selected.Count < count)
            {
                PhysicalPoint farthest = default;
                double farthestDist = -1;
                foreach (var p in all)
                {
                    if (selected.Contains(p)) continue;
                    double d = double.MaxValue;
                    foreach (var s in selected) d = Math.Min(d, DistSq(p, s));
                    if (d > farthestDist) { farthestDist = d; farthest = p; }
                }
                selected.Add(farthest);
            }
        }

        return OrderAsNeighbourWalk(selected);
    }

    /// <summary>
    /// Orders the selected targets as a nearest-neighbour walk from the top-left, so every advance is a
    /// short hop to a target the user can already see.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this replaces.</b> The previous ordering sorted farthest-from-centre first and then
    /// interleaved the front and back of that list, which for 5 points produced
    /// top-left → centre → top-right → bottom-right → bottom-left: a deliberate maximum-distance
    /// alternation. For a user with poor gaze control, every advance meant searching the screen for a
    /// dot that had teleported to the far side. That is the complaint this ticket exists to fix.
    /// </para>
    /// <para>
    /// <b>Why not the "clockwise around the perimeter, centre last" shape the ticket sketches.</b> It was
    /// implemented first and does not survive the sparse layouts. With 5 points the perimeter is four
    /// corners, and that cycle has TWO full-width edges (top and bottom); a cyclic walk can skip only its
    /// closing edge, so one screen-crossing jump always remains — 1760 px on a 1920-wide display where
    /// 993 px is achievable. The only way to cross the screen cheaply there is to route THROUGH the
    /// centre, which a perimeter-first rule forbids by construction. What ships instead routes through
    /// it: at 1920x1080 the 5-point order is top-left → bottom-left → centre → bottom-right → top-right,
    /// every step ≤ 993 px.
    /// </para>
    /// <para>
    /// <b>Exact, not greedy — and greedy was tried second.</b> A nearest-neighbour walk strands a target:
    /// for 6 points it takes the bait of the short hop along the top row (top-left → top-centre →
    /// centre → top-right → bottom-right) and leaves bottom-left for last, reachable only by a 1760 px
    /// crossing. The shipped order for the same 6 points is top-left → bottom-left → centre →
    /// top-centre → top-right → bottom-right, whose longest step is 993 px. So the order is solved
    /// properly, by dynamic programming over subsets.
    /// </para>
    /// <para>
    /// Affordable because the problem is tiny and bounded: at most 16 targets, solved once when a
    /// session starts, well before the first target is shown. The table is 2^n x n and only reaches its
    /// full ~1M cells at the maximum count.
    /// </para>
    /// <para>
    /// <b>The start is free, not pinned to the top-left.</b> Pinning it was tried and it costs
    /// optimality: with 7 targets on a 3840x2400 display the best path starting top-left has a 2160 px
    /// longest step where 2099 px is achievable from elsewhere. Since ticket 2 shows the whole target
    /// set for the entire session, the first target is discoverable wherever it is, and there is nothing
    /// left to buy with a fixed start. Among paths that are optimal on BOTH prior objectives the one
    /// beginning nearest the top-left is chosen — as the third key of phase two below, not as a filter
    /// over finished candidates, which is a distinction that turned out to matter: applied afterwards it
    /// was inert, because the search has already committed to one predecessor chain per state, and the
    /// common 3/5/6/9-target layouts were starting bottom-left while the documentation claimed
    /// otherwise. All four now start at the top-left corner on a 1920x1080 layout.
    /// </para>
    /// </remarks>
    private static List<PhysicalPoint> OrderAsNeighbourWalk(List<PhysicalPoint> selected)
    {
        int n = selected.Count;
        if (n <= 2) return new List<PhysicalPoint>(selected);

        var dist = new double[n, n];
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            dist[i, j] = Math.Sqrt(DistSq(selected[i], selected[j]));

        // Rank of each target as a starting point: 0 is the most top-left. This is the THIRD key of the
        // DP, and it has to be a key rather than a filter applied afterwards — see below.
        var startRankOf = new int[n];
        var byTopLeft = Enumerable.Range(0, n)
            .OrderBy(i => Math.Round(selected[i].Y, 3))
            .ThenBy(i => Math.Round(selected[i].X, 3))
            .ToList();
        for (int r = 0; r < n; r++) startRankOf[byTopLeft[r]] = r;

        // TWO PHASES, and they cannot be collapsed into one.
        //
        // The obvious single pass — keep the lexicographically best (longest step, total, start) per
        // state — is UNSOUND, because the first key is a running MAX and the second a running SUM. A
        // prefix with a smaller max but a larger total displaces the one that would have won overall:
        // at 6 targets on 3840x2400 a prefix reaching the same state with max 2099/total 5998 evicts
        // max 2160/total 5339, and then a later 2160 step makes the maxes equal while the totals never
        // recover. The evicted prefix was the optimum. That version passed every optimality test here by
        // luck of geometry and was only caught by the start-preference check.
        //
        // Each phase on its own is a valid dominance order: a running max is monotone under extension,
        // and once the max is FIXED at B* a running sum is additive, so lexicographic (total, start)
        // is sound — a strictly smaller prefix total stays strictly smaller after any completion, and
        // the start rank is constant along a path and only breaks exact ties.
        int full = (1 << n) - 1;
        double bestBottleneck = MinimaxBottleneck(dist, n, full);
        if (double.IsPositiveInfinity(bestBottleneck)) return new List<PhysicalPoint>(selected);

        return ShortestPathWithin(selected, dist, n, full, bestBottleneck, startRankOf);
    }

    /// <summary>
    /// Phase one: the smallest achievable LONGEST step over all Hamiltonian paths. Single key, so
    /// keeping one best value per (visited set, endpoint) is sound.
    /// </summary>
    private static double MinimaxBottleneck(double[,] dist, int n, int full)
    {
        int states = full + 1;
        var bottleneck = new double[states, n];
        for (int m = 0; m < states; m++)
        for (int j = 0; j < n; j++)
            bottleneck[m, j] = double.PositiveInfinity;

        // Every singleton is seeded, which leaves the starting point free at no extra cost.
        for (int i = 0; i < n; i++) bottleneck[1 << i, i] = 0;

        for (int mask = 0; mask < states; mask++)
        for (int j = 0; j < n; j++)
        {
            if ((mask & (1 << j)) == 0) continue;
            double bj = bottleneck[mask, j];
            if (double.IsPositiveInfinity(bj)) continue;

            for (int k = 0; k < n; k++)
            {
                if ((mask & (1 << k)) != 0) continue;
                double candidate = Math.Max(bj, dist[j, k]);
                int next = mask | (1 << k);
                if (candidate < bottleneck[next, k]) bottleneck[next, k] = candidate;
            }
        }

        double best = double.PositiveInfinity;
        for (int j = 0; j < n; j++) best = Math.Min(best, bottleneck[full, j]);
        return best;
    }

    /// <summary>
    /// Phase two: among paths whose every step is within <paramref name="bottleneckLimit"/>, the one with
    /// the least total travel — and, among those, the one starting at the most top-left target.
    /// </summary>
    private static List<PhysicalPoint> ShortestPathWithin(
        List<PhysicalPoint> selected, double[,] dist, int n, int full,
        double bottleneckLimit, int[] startRankOf)
    {
        int states = full + 1;
        var total = new double[states, n];
        var startRank = new int[states, n];
        var parent = new sbyte[states, n];
        for (int m = 0; m < states; m++)
        for (int j = 0; j < n; j++)
        {
            total[m, j] = double.PositiveInfinity;
            startRank[m, j] = int.MaxValue;
            parent[m, j] = -1;
        }
        for (int i = 0; i < n; i++)
        {
            total[1 << i, i] = 0;
            startRank[1 << i, i] = startRankOf[i];
        }

        double limit = bottleneckLimit + 1e-9;
        for (int mask = 0; mask < states; mask++)
        for (int j = 0; j < n; j++)
        {
            if ((mask & (1 << j)) == 0) continue;
            double tj = total[mask, j];
            if (double.IsPositiveInfinity(tj)) continue;

            for (int k = 0; k < n; k++)
            {
                if ((mask & (1 << k)) != 0) continue;
                if (dist[j, k] > limit) continue;        // the step would break the phase-one optimum

                int next = mask | (1 << k);
                double candidateTotal = tj + dist[j, k];
                int candidateStart = startRank[mask, j];

                if (candidateTotal < total[next, k] - 1e-9
                    || (candidateTotal <= total[next, k] + 1e-9 && candidateStart < startRank[next, k]))
                {
                    total[next, k] = candidateTotal;
                    startRank[next, k] = candidateStart;
                    parent[next, k] = (sbyte)j;
                }
            }
        }

        int end = -1;
        for (int j = 0; j < n; j++)
        {
            if (double.IsPositiveInfinity(total[full, j])) continue;
            if (end < 0
                || total[full, j] < total[full, end] - 1e-9
                || (total[full, j] <= total[full, end] + 1e-9 && startRank[full, j] < startRank[full, end]))
            {
                end = j;
            }
        }

        // Unreachable — phase one proved a path within this limit exists — but returning nothing would
        // be far worse than returning an unordered set, so fall back rather than throw.
        if (end < 0) return new List<PhysicalPoint>(selected);

        return Reconstruct(selected, parent, full, end);
    }

    private static List<PhysicalPoint> Reconstruct(
        List<PhysicalPoint> selected, sbyte[,] parent, int full, int end)
    {
        var reversed = new List<PhysicalPoint>(selected.Count);
        for (int mask = full, j = end; j >= 0;)
        {
            reversed.Add(selected[j]);
            int prev = parent[mask, j];
            mask &= ~(1 << j);
            j = prev;
        }
        reversed.Reverse();
        return reversed;
    }

    private static double DistSq(PhysicalPoint a, PhysicalPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
