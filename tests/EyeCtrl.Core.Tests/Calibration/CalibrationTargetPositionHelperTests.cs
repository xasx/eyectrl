namespace EyeCtrl.Core.Tests.Calibration;

using System.Linq;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;

public class CalibrationTargetPositionHelperTests
{
    // The display this actually runs on, and the one the coverage complaint came from.
    private static readonly PhysicalPoint HighDpiScreen = new(3840, 2400);
    private static readonly PhysicalPoint StandardScreen = new(1920, 1080);

    // ── coverage ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Margin_OnTheUsersDisplay_IsFivePercent_NotTwenty()
    {
        // The headline of the complaint: at Margin 0.2 the corner targets on this panel sat 768 px /
        // 480 px in from the edges, so the outer fifth of the screen was never calibrated and the
        // transform was extrapolated over it.
        var margin = CalibrationTargetPositionHelper.MarginPx(HighDpiScreen);

        Check.That(margin).IsEqualTo(120.0);                       // 0.05 x 2400 (the shorter side)
        Check.That(margin / HighDpiScreen.Y).IsEqualTo(0.05);
        Check.That(margin / HighDpiScreen.X).IsStrictlyLessThan(0.05);   // same pixels on every edge
    }

    [Fact]
    public void Margin_IsTheSamePixelCountOnEveryEdge_NotAPerAxisFraction()
    {
        // A per-axis fraction gives a 16:10 display a border almost twice as wide as it is tall, for no
        // reason anyone could state. Scaling by the shorter side matches CalibrationGeometry.BandRadiusPx.
        var positions = CalibrationTargetPositionHelper.GetPositions(9, HighDpiScreen);
        var margin = CalibrationTargetPositionHelper.MarginPx(HighDpiScreen);

        Check.That(positions.Min(p => p.X)).IsCloseTo(margin, 0.01);
        Check.That(positions.Min(p => p.Y)).IsCloseTo(margin, 0.01);
        Check.That(HighDpiScreen.X - positions.Max(p => p.X)).IsCloseTo(margin, 0.01);
        Check.That(HighDpiScreen.Y - positions.Max(p => p.Y)).IsCloseTo(margin, 0.01);
    }

    [Fact]
    public void Margin_HasAPixelFloor_SoATargetIsNotDrawnUnderTheTaskbar()
    {
        // The floor only binds below ~1600 px on the shorter side. At 1080p the fraction alone would
        // give 54 px, which puts the bottom edge of a 20 px-radius dot 34 px from the screen edge —
        // inside a default 48 px taskbar.
        Check.That(CalibrationTargetPositionHelper.MarginPx(StandardScreen)).IsEqualTo(80.0);
        Check.That(CalibrationTargetPositionHelper.MarginPx(HighDpiScreen)).IsEqualTo(120.0);

        // …and the dot clears a 48 px taskbar at 1080p with room to spare.
        const double taskbarPx = 48, dotRadiusPx = 20;
        var lowestDotEdge = StandardScreen.Y - CalibrationTargetPositionHelper.MarginPx(StandardScreen) + dotRadiusPx;
        Check.That(StandardScreen.Y - lowestDotEdge).IsStrictlyGreaterThan(taskbarPx);
    }

    [Fact]
    public void Margin_OnATinyDisplay_CannotCollapseTheUsableBox()
    {
        // The floor must not win on a surface too small to afford it, or every target lands on top of
        // every other and the fit is singular.
        var tiny = new PhysicalPoint(320, 240);
        var margin = CalibrationTargetPositionHelper.MarginPx(tiny);

        Check.That(margin).IsEqualTo(48.0);                 // capped at 0.2 x 240
        Check.That(margin * 2).IsStrictlyLessThan(tiny.Y);  // a usable box remains
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(16)]
    public void GetPositions_PutsTargetsInTheOuterBand_WhichTheOldMarginNeverDid(int count)
    {
        // The acceptance criterion stated as coverage rather than as a constant: on the user's display
        // the outermost targets must sit within 5 % of the edges. Under the old 0.2 margin the nearest
        // target to any edge was 480 px away; it is now 120.
        var positions = CalibrationTargetPositionHelper.GetPositions(count, HighDpiScreen);
        var band = 0.05 * HighDpiScreen.Y;

        Check.That(positions.Min(p => p.Y)).IsLessOrEqualThan(band + 0.01);
        Check.That(HighDpiScreen.Y - positions.Max(p => p.Y)).IsLessOrEqualThan(band + 0.01);
        Check.That(positions.Min(p => p.X)).IsLessOrEqualThan(band + 0.01);
        Check.That(HighDpiScreen.X - positions.Max(p => p.X)).IsLessOrEqualThan(band + 0.01);
    }

    [Fact]
    public void GetPositions_AllStayInsideTheMargin_AndOnScreen()
    {
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            var margin = CalibrationTargetPositionHelper.MarginPx(screen);
            for (int count = 3; count <= 16; count++)
            {
                foreach (var p in CalibrationTargetPositionHelper.GetPositions(count, screen))
                {
                    Check.WithCustomMessage($"count={count} left").That(p.X).IsGreaterOrEqualThan(margin - 0.01);
                    Check.WithCustomMessage($"count={count} right").That(p.X).IsLessOrEqualThan(screen.X - margin + 0.01);
                    Check.WithCustomMessage($"count={count} top").That(p.Y).IsGreaterOrEqualThan(margin - 0.01);
                    Check.WithCustomMessage($"count={count} bottom").That(p.Y).IsLessOrEqualThan(screen.Y - margin + 0.01);
                }
            }
        }
    }

    [Fact]
    public void GetPositions_AreAlwaysDistinct()
    {
        for (int count = 3; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, StandardScreen);
            Check.WithCustomMessage($"count={count}").That(positions.Distinct().Count()).IsEqualTo(count);
        }
    }

    [Fact]
    public void GetPositions_5To16Points_AlwaysCoverAllFourScreenExtremes()
    {
        var screen = StandardScreen;
        var margin = CalibrationTargetPositionHelper.MarginPx(screen);
        double minX = margin, maxX = screen.X - margin, minY = margin, maxY = screen.Y - margin;

        for (int count = 5; count <= 16; count++)
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);

            Check.WithCustomMessage($"count={count}: top row missing")
                .That(positions.Any(p => Math.Abs(p.Y - minY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: bottom row missing")
                .That(positions.Any(p => Math.Abs(p.Y - maxY) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: left column missing")
                .That(positions.Any(p => Math.Abs(p.X - minX) < 1)).IsTrue();
            Check.WithCustomMessage($"count={count}: right column missing")
                .That(positions.Any(p => Math.Abs(p.X - maxX) < 1)).IsTrue();
        }
    }

    // ── ordering ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void GetPositions_LongestStepIsTheBestAnyOrderingCouldAchieve()
    {
        // THE ordering criterion, stated as the strongest form available rather than as a hand-derived
        // bound: the longest consecutive step equals the minimum achievable over EVERY permutation,
        // checked exhaustively. Counts 3-8 only, because 8! is 40 320 and 9! is not.
        //
        // An earlier version of this test asserted an absolute bound — one diagonal "grid step" derived
        // from the returned coordinates — and it was wrong for the sparse layouts. With 5 points on a
        // 16:10 display the graph of edges within that bound is a star centred on the middle target
        // (every corner-to-corner move is longer), and a star with four leaves has no Hamiltonian path
        // at all. The 2160 px step the helper produces there is not a defect; it is optimal, and no
        // ordering can avoid it. Brute force says so without anyone having to derive it.
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            for (int count = 3; count <= 8; count++)
            {
                var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var achieved = LongestStep(positions);
                var best = BestAchievableLongestStep(positions);

                Check.WithCustomMessage($"{screen.X}x{screen.Y} count={count}: longest step {achieved:F0} px, "
                                        + $"but {best:F0} px was achievable")
                    .That(achieved).IsLessOrEqualThan(best + 1.0);
            }
        }
    }

    [Fact]
    public void GetPositions_AmongEquallyShortestLongestSteps_TakesTheShortestTotalTravel()
    {
        // The secondary objective, and it was unguarded: dropping the total-distance tie-break from the
        // solver left every test green, because minimising the LONGEST step says nothing about the
        // other n-2 of them. Total eye travel is what the user actually spends, so it is worth pinning —
        // a path can be optimal on the headline metric and still wander.
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            for (int count = 3; count <= 8; count++)
            {
                var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var achievedLongest = LongestStep(positions);
                var achievedTotal = TotalTravel(positions);
                var bestTotal = BestTotalAmongOptimalPaths(positions, achievedLongest);

                Check.WithCustomMessage($"{screen.X}x{screen.Y} count={count}: travelled {achievedTotal:F0} px, "
                                        + $"but {bestTotal:F0} px was available at the same longest step")
                    .That(achievedTotal).IsLessOrEqualThan(bestTotal + 1.0);
            }
        }
    }

    [Fact]
    public void GetPositions_AmongEquallyOptimalPaths_StartsAtTheMostTopLeftTarget()
    {
        // The third objective, and the one that was claimed but not delivered: the preference used to be
        // applied to the finished candidate paths, which cannot work because the solver has already
        // committed to one predecessor chain per state — only the chains that happened to survive were
        // available to choose between. Deleting the preference changed no output at all, while the
        // remarks said it was honoured. It is now a key inside the search, and this test fails without it.
        //
        // The oracle applies the same three objectives in the same order, by brute force: best longest
        // step, then least total travel, then the most top-left start.
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            for (int count = 3; count <= 8; count++)
            {
                var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var expected = BestStartAmongOptimalPaths(positions);

                Check.WithCustomMessage(
                        $"{screen.X}x{screen.Y} count={count}: started at "
                        + $"({positions[0].X:F0},{positions[0].Y:F0}), but ({expected.X:F0},{expected.Y:F0}) "
                        + "was available at the same longest step and total travel")
                    .That(positions[0]).IsEqualTo(expected);
            }
        }
    }

    [Fact]
    public void GetPositions_LongestStepBeatsTheOldInterleavedOrdering()
    {
        // The regression this ticket exists to prevent, measured against the thing it replaced. The old
        // ordering sorted farthest-from-centre first and then interleaved front and back, which for 5
        // points gave top-left → centre → top-right → bottom-right → bottom-left. Covers 9-16 too,
        // where brute force cannot reach.
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            for (int count = 4; count <= 16; count++)
            {
                var positions = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var achieved = LongestStep(positions);
                var old = LongestStep(OldInterleavedOrder(positions, screen));

                Check.WithCustomMessage($"{screen.X}x{screen.Y} count={count}: new {achieved:F0} px vs old {old:F0} px")
                    .That(achieved).IsStrictlyLessThan(old);
            }
        }
    }

    [Fact]
    public void GetPositions_StartAtACorner_ThoughNotNecessarilyTheTopLeft()
    {
        // The start is deliberately NOT pinned to the top-left: doing so costs optimality (7 targets on
        // 3840x2400 need 2160 px from the top-left where 2099 px is reachable from elsewhere), and
        // ticket 2 makes the whole set visible from the start, so there is nothing left to buy with a
        // fixed first target. What IS worth pinning is that a walk never opens in the middle of the
        // screen — an optimal path over a grid always terminates at extremes.
        foreach (var count in new[] { 3, 4, 5, 9, 16 })
        {
            var positions = CalibrationTargetPositionHelper.GetPositions(count, StandardScreen);
            var margin = CalibrationTargetPositionHelper.MarginPx(StandardScreen);

            bool OnPerimeter(PhysicalPoint p) =>
                Math.Abs(p.X - margin) < 1 || Math.Abs(p.X - (StandardScreen.X - margin)) < 1
                || Math.Abs(p.Y - margin) < 1 || Math.Abs(p.Y - (StandardScreen.Y - margin)) < 1;

            Check.WithCustomMessage($"count={count} started at ({positions[0].X:F0},{positions[0].Y:F0})")
                .That(OnPerimeter(positions[0])).IsTrue();
        }
    }

    [Fact]
    public void GetPositions_3Points_NeverTraversesTheFullWidthTopEdge()
    {
        // Three points are maximally spread by construction, so one edge of the triangle is the whole
        // screen width. A path visits two of the three edges, so it must be the two diagonals.
        var positions = CalibrationTargetPositionHelper.GetPositions(3, StandardScreen);
        var margin = CalibrationTargetPositionHelper.MarginPx(StandardScreen);
        var topEdge = StandardScreen.X - (2 * margin);

        Check.That(positions).HasSize(3);
        Check.That(Dist(positions[0], positions[1])).IsStrictlyLessThan(topEdge);
        Check.That(Dist(positions[1], positions[2])).IsStrictlyLessThan(topEdge);
    }

    [Fact]
    public void GetPositions_5Points_RouteThroughTheCentre_WhichIsWhyAPerimeterWalkWasRejected()
    {
        // The case that decided the algorithm. Five points are four corners plus the centre; the corner
        // cycle has TWO full-width edges, so any perimeter-first walk keeps one 1760 px screen crossing.
        // Going corner → corner down one side, through the centre, and up the other keeps every step
        // inside one grid diagonal. The centre must therefore NOT be last.
        var positions = CalibrationTargetPositionHelper.GetPositions(5, StandardScreen);
        var centre = new PhysicalPoint(StandardScreen.X * 0.5, StandardScreen.Y * 0.5);

        var centreIndex = positions.ToList().FindIndex(p => Dist(p, centre) < 1);
        Check.That(centreIndex).IsStrictlyGreaterThan(0);
        Check.That(centreIndex).IsStrictlyLessThan(positions.Count - 1);

        // …and the corners either side of it are on OPPOSITE sides of the screen, i.e. the centre is
        // genuinely being used as the bridge rather than just visited early.
        Check.That(Math.Sign(positions[centreIndex - 1].X - centre.X))
            .IsNotEqualTo(Math.Sign(positions[centreIndex + 1].X - centre.X));
    }

    [Fact]
    public void GetPositions_9Points_EveryStepIsOneGridEdge_NoDiagonalsNoSkips()
    {
        // A full 3x3 should walk cell to cell. Stated as "no step is longer than the larger grid
        // spacing" — stronger than the general bound, which also allows a diagonal.
        var positions = CalibrationTargetPositionHelper.GetPositions(9, StandardScreen);
        var margin = CalibrationTargetPositionHelper.MarginPx(StandardScreen);
        var stepX = (StandardScreen.X - (2 * margin)) / 2;
        var stepY = (StandardScreen.Y - (2 * margin)) / 2;
        var longestEdge = Math.Max(stepX, stepY);

        for (int i = 1; i < positions.Count; i++)
            Check.WithCustomMessage($"step {i - 1}->{i}")
                .That(Dist(positions[i - 1], positions[i])).IsLessOrEqualThan(longestEdge + 1.0);
    }

    [Fact]
    public void GetPositions_16Points_EveryStepIsOneGridEdge()
    {
        var positions = CalibrationTargetPositionHelper.GetPositions(16, StandardScreen);
        var margin = CalibrationTargetPositionHelper.MarginPx(StandardScreen);
        var longestEdge = Math.Max(
            (StandardScreen.X - (2 * margin)) / 3,
            (StandardScreen.Y - (2 * margin)) / 3);

        for (int i = 1; i < positions.Count; i++)
            Check.WithCustomMessage($"step {i - 1}->{i}")
                .That(Dist(positions[i - 1], positions[i])).IsLessOrEqualThan(longestEdge + 1.0);
    }

    [Fact]
    public void GetPositions_IsDeterministic()
    {
        foreach (var screen in new[] { StandardScreen, HighDpiScreen })
        {
            for (int count = 3; count <= 16; count++)
            {
                var a = CalibrationTargetPositionHelper.GetPositions(count, screen);
                var b = CalibrationTargetPositionHelper.GetPositions(count, screen);
                Check.WithCustomMessage($"count={count} not deterministic")
                    .That(a.SequenceEqual(b)).IsTrue();
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(17)]
    public void GetPositions_RejectsUnsupportedCounts(int count)
    {
        Check.ThatCode(() => CalibrationTargetPositionHelper.GetPositions(count, StandardScreen))
             .Throws<ArgumentOutOfRangeException>();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static double LongestStep(IReadOnlyList<PhysicalPoint> path) =>
        Enumerable.Range(1, path.Count - 1).Max(i => Dist(path[i - 1], path[i]));

    private static double TotalTravel(IReadOnlyList<PhysicalPoint> path) =>
        Enumerable.Range(1, path.Count - 1).Sum(i => Dist(path[i - 1], path[i]));

    /// <summary>
    /// The shortest total travel among permutations whose longest step is no worse than
    /// <paramref name="longestStep"/> — i.e. the best secondary objective available without giving up
    /// the primary one. Brute force, for the same reason as <see cref="BestAchievableLongestStep"/>.
    /// </summary>
    private static double BestTotalAmongOptimalPaths(IReadOnlyList<PhysicalPoint> points, double longestStep)
    {
        double best = double.MaxValue;
        foreach (var permutation in Permute(Enumerable.Range(0, points.Count).ToArray(), 0))
        {
            double longest = 0, total = 0;
            for (int i = 1; i < permutation.Length; i++)
            {
                var d = Dist(points[permutation[i - 1]], points[permutation[i]]);
                longest = Math.Max(longest, d);
                total += d;
            }
            if (longest <= longestStep + 1.0 && total < best) best = total;
        }
        return best;
    }

    /// <summary>
    /// The smallest possible "longest step" over every permutation of <paramref name="points"/>, by
    /// exhaustive search. Deliberately brute force: the production helper solves this with a subset DP,
    /// and an oracle that shares its cleverness proves nothing.
    /// </summary>
    private static double BestAchievableLongestStep(IReadOnlyList<PhysicalPoint> points)
    {
        var indices = Enumerable.Range(0, points.Count).ToArray();
        double best = double.MaxValue;

        foreach (var permutation in Permute(indices, 0))
        {
            double longest = 0;
            for (int i = 1; i < permutation.Length; i++)
                longest = Math.Max(longest, Dist(points[permutation[i - 1]], points[permutation[i]]));
            if (longest < best) best = longest;
        }
        return best;
    }

    private static IEnumerable<int[]> Permute(int[] items, int from)
    {
        if (from == items.Length - 1) { yield return (int[])items.Clone(); yield break; }
        for (int i = from; i < items.Length; i++)
        {
            (items[from], items[i]) = (items[i], items[from]);
            foreach (var p in Permute(items, from + 1)) yield return p;
            (items[from], items[i]) = (items[i], items[from]);
        }
    }

    /// <summary>
    /// The most top-left start among permutations that are optimal on BOTH prior objectives — least
    /// longest step, then least total travel. Brute force, mirroring the solver's three keys without
    /// borrowing its method.
    /// </summary>
    private static PhysicalPoint BestStartAmongOptimalPaths(IReadOnlyList<PhysicalPoint> points)
    {
        var all = Permute(Enumerable.Range(0, points.Count).ToArray(), 0).ToList();

        double bestLongest = double.MaxValue, bestTotal = double.MaxValue;
        foreach (var permutation in all)
        {
            var (longest, total) = Measure(points, permutation);
            if (longest < bestLongest - 1e-9) { bestLongest = longest; bestTotal = total; }
            else if (longest <= bestLongest + 1e-9 && total < bestTotal - 1e-9) bestTotal = total;
        }

        PhysicalPoint best = default;
        bool found = false;
        foreach (var permutation in all)
        {
            var (longest, total) = Measure(points, permutation);
            if (longest > bestLongest + 1e-9 || total > bestTotal + 1e-9) continue;

            var start = points[permutation[0]];
            if (!found || start.Y < best.Y - 1e-6
                || (Math.Abs(start.Y - best.Y) <= 1e-6 && start.X < best.X - 1e-6))
            {
                best = start;
                found = true;
            }
        }
        return best;
    }

    private static (double Longest, double Total) Measure(IReadOnlyList<PhysicalPoint> points, int[] permutation)
    {
        double longest = 0, total = 0;
        for (int i = 1; i < permutation.Length; i++)
        {
            var d = Dist(points[permutation[i - 1]], points[permutation[i]]);
            longest = Math.Max(longest, d);
            total += d;
        }
        return (longest, total);
    }

    /// <summary>
    /// The ordering this ticket replaced, reimplemented so the improvement can be measured rather than
    /// asserted: sort farthest-from-SCREEN-CENTRE first, then interleave the front and back of that list.
    /// </summary>
    /// <remarks>
    /// The centre is the screen's, matching the original, which took <c>w * 0.5, h * 0.5</c>. An earlier
    /// version of this helper used the centroid of the selected points instead; the two coincide for the
    /// symmetric layouts and the comparison's conclusion was unaffected, but a reimplementation that is
    /// almost the original is worth less than one that is.
    /// </remarks>
    private static List<PhysicalPoint> OldInterleavedOrder(
        IReadOnlyList<PhysicalPoint> positions, PhysicalPoint screen)
    {
        var cx = screen.X * 0.5;
        var cy = screen.Y * 0.5;
        var sorted = positions
            .OrderByDescending(p => ((p.X - cx) * (p.X - cx)) + ((p.Y - cy) * (p.Y - cy)))
            .ToList();

        var ordered = new List<PhysicalPoint>(sorted.Count);
        while (sorted.Count > 0)
        {
            ordered.Add(sorted[0]);
            sorted.RemoveAt(0);
            if (sorted.Count > 0)
            {
                ordered.Add(sorted[^1]);
                sorted.RemoveAt(sorted.Count - 1);
            }
        }
        return ordered;
    }

    private static double Dist(PhysicalPoint a, PhysicalPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
