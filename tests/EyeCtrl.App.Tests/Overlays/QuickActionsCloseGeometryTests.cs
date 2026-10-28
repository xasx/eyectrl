namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// B8: the Quick Actions close button is drawn to its r50 hit area, and every tile's hit circle
/// keeps the R4 dead gap of at least 20 DIP (hit radius ≥ 50 DIP and a ≥ 20 DIP gap between
/// different actions: the r50 close target used to sit on the first tile's r50 circle).
///
/// <para>
/// B8b adds the three geometry promises the review left open: the tile FACE is drawn to its own
/// r50 hit circle (a 100 DIP disc, not the old 120 DIP square), the tile count is capped so a
/// full grid never squeezes its rows into overlapping hit circles, and a re-show after the count
/// grew is placed by the freshly computed size rather than the hidden window's stale
/// <c>ActualWidth</c>/<c>ActualHeight</c>. The Contrast theme's 2 DIP border — the tightest chrome
/// — is measured too.
/// </para>
///
/// <para>
/// Measured on the real grid through <see cref="QuickActionsGridHost"/>: the XAML's
/// <c>ItemsSource="{Binding Tiles}"</c> populates the tiles, the window's own
/// <c>ResizeToContent</c> sizes it, and <c>SyncTilePositions</c> registers the dwell targets. The
/// harness models DPI 1 and 2 without touching layout (the geometry is DPI-independent) and
/// without showing a window.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class QuickActionsCloseGeometryTests
{
    /// <summary>R4's dead gap between two different actions, in DIP.</summary>
    private const double MinimumGapDip = 20.0;

    private static void WithDpi(double scale, System.Action body)
    {
        var (previousX, previousY) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            body();
        }
        finally
        {
            DpiHelper.Update(previousX, previousY);
        }
    }

    private static Point Centre(Rect rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheCloseDisc_IsTheHitCircle_AndKeepsTheDeadGapToEveryTile(double scale)
    {
        WithDpi(scale, () =>
        {
            using var grid = QuickActionsGridHost.Open(scale);
            var window = grid.Window;
            var vm = grid.Vm;

            var close = window.CloseButton;
            Check.WithCustomMessage(
                    $"at DPI {scale} the close disc is {close.ActualWidth} DIP across, but its "
                    + $"hit circle is {2 * UIConstants.ButtonRadius} DIP across.")
                .That(close.ActualWidth).IsEqualTo(2 * UIConstants.ButtonRadius);
            Check.That(close.ActualHeight).IsEqualTo(2 * UIConstants.ButtonRadius);

            window.SyncTilePositions();
            var targets = vm.DetectorForTest.Targets.ToDictionary(t => t.Id);
            var closeTarget = targets[QuickActionsGridViewModel.CloseTargetId];

            Check.WithCustomMessage("the close hit circle is not the drawn disc's radius")
                .That(closeTarget.Radius / scale).IsCloseTo(UIConstants.ButtonRadius, 1e-9);

            var closeCentrePhysical = grid.ToScreen(Centre(grid.InContent(close)));
            Check.WithCustomMessage("the close hit circle is not centred on the drawn disc")
                .That(closeTarget.Center.X).IsCloseTo(closeCentrePhysical.X, 1e-6);
            Check.WithCustomMessage("the close hit circle is not centred on the drawn disc")
                .That(closeTarget.Center.Y).IsCloseTo(closeCentrePhysical.Y, 1e-6);

            // The real grid: 5 tiles at the default 3 columns, so the tiled band is two rows.
            Check.That(vm.Tiles).HasSize(5);
            foreach (var tile in vm.Tiles)
            {
                var tileTarget = targets[tile.Action.Id];
                var gapDip = (closeTarget.Center.DistanceTo(tileTarget.Center)
                              - closeTarget.Radius - tileTarget.Radius) / scale;

                Check.WithCustomMessage(
                        $"at DPI {scale} the close hit circle leaves only {gapDip:0.##} DIP to "
                        + $"{tile.Label}'s hit circle; R4 requires {MinimumGapDip}. Move Close, "
                        + "not the tiles.")
                    .That(gapDip).IsGreaterOrEqualThan(MinimumGapDip);
            }
        });
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheCloseDisc_AndEveryTile_StayFullyOnScreen_OnEverySummonEdge(double scale)
    {
        WithDpi(scale, () =>
        {
            using var grid = QuickActionsGridHost.Open(scale);
            var window = grid.Window;
            var vm = grid.Vm;
            var area = SystemParameters.WorkArea;

            var elements = new List<(string What, Rect Rect)>
            {
                ("the close disc", grid.InContent(window.CloseButton)),
            };
            elements.AddRange(grid.TileContainers()
                .Select((container, i) => ($"tile '{vm.Tiles[i].Label}'", grid.InContent(container))));

            // Stay out of the corners: the panel is centred on its summon edge, so a close disc
            // fully inside the window (and the window inside the work area) is never a corner
            // target. Every edge the summon can resolve to is checked.
            foreach (var edge in new[] { "Left", "Right", "Top", "Bottom" })
            {
                window.PositionOnEdge(edge);
                foreach (var (what, rect) in elements)
                {
                    var left = window.Left + rect.Left;
                    var top = window.Top + rect.Top;

                    Check.WithCustomMessage(
                            $"edge {edge} at DPI {scale}: {what} "
                            + $"({left:0.##},{top:0.##} {rect.Width:0.##}×{rect.Height:0.##}) leaves "
                            + $"the work area {area}.")
                        .That(left >= area.Left && top >= area.Top
                              && left + rect.Width <= area.Right
                              && top + rect.Height <= area.Bottom)
                        .IsTrue();
                }
            }
        });
    }

    // ── B8b F2: drawn = hit ─────────────────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void EveryTileFace_IsTheDrawnHitCircle(double scale)
    {
        WithDpi(scale, () =>
        {
            using var grid = QuickActionsGridHost.Open(scale);
            var window = grid.Window;
            var vm = grid.Vm;

            window.SyncTilePositions();
            var targets = vm.DetectorForTest.Targets.ToDictionary(t => t.Id);

            var containers = grid.TileContainers();
            var faces = grid.TileFaces();
            Check.That(faces).HasSize(containers.Count);

            for (var i = 0; i < faces.Count; i++)
            {
                var tile = vm.Tiles[i];
                var face = grid.InContent(faces[i]);
                var cell = grid.InContent(containers[i]);
                var target = targets[tile.Action.Id];

                // The face is exactly the hit circle: square, 2 × r50 across, and rounded to a
                // disc so no drawn pixel (not even a corner) lies outside the hit area.
                Check.WithCustomMessage(
                        $"at DPI {scale} tile '{tile.Label}' is drawn {face.Width:0.##}×{face.Height:0.##} "
                        + $"DIP but hit-tested as a {2 * UIConstants.ButtonRadius} DIP circle.")
                    .That(face.Width).IsCloseTo(2 * UIConstants.ButtonRadius, 1e-6);
                Check.That(face.Height).IsCloseTo(face.Width, 1e-6);
                Check.WithCustomMessage(
                        $"tile '{tile.Label}' face is not the CircleCornerRadius disc, so its corners "
                        + "are drawn outside the r50 hit circle.")
                    .That(faces[i].CornerRadius).IsEqualTo(Tokens.CircleCornerRadius);

                // The point the production push registers as the target centre must be the drawn
                // face's centre, or the drawn = hit claim only holds approximately.
                var faceCentre = Centre(face);
                var cellCentre = Centre(cell);
                Check.WithCustomMessage($"tile '{tile.Label}' face is off its cell centre")
                    .That(faceCentre.X).IsCloseTo(cellCentre.X, 1e-6);
                Check.That(faceCentre.Y).IsCloseTo(cellCentre.Y, 1e-6);

                var screenCentre = grid.ToScreen(faceCentre);
                Check.WithCustomMessage($"tile '{tile.Label}' hit circle is not centred on its face")
                    .That(target.Center.X).IsCloseTo(screenCentre.X, 1e-6);
                Check.That(target.Center.Y).IsCloseTo(screenCentre.Y, 1e-6);
                Check.WithCustomMessage($"tile '{tile.Label}' hit circle is not r50")
                    .That(target.Radius / scale).IsCloseTo(UIConstants.ButtonRadius, 1e-9);
            }
        });
    }

    // ── B8b F3: at the maximum count, the cap keeps every face on screen and the gap intact ──

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AtTheMaximumTileCount_EveryFaceIsOnScreen_AndNoTwoHitCirclesTouch(double scale)
    {
        WithDpi(scale, () =>
        {
            // Far more actions than any work area can hold: ResizeToContent must cap the list
            // rather than let the window clamp and the UniformGrid squeeze the rows.
            using var grid = QuickActionsGridHost.Open(scale, tileCount: 500);
            var window = grid.Window;
            var vm = grid.Vm;
            var area = SystemParameters.WorkArea;

            // R3 (fixup-01): the cap must be EXACTLY the rows that fit at the full cell pitch. The
            // old assertion (0 < count < 500) let a cap one row short silently drop three of the
            // user's actions (review 01 M11). Derived from the window's own chrome/pitch maths, so
            // a real geometry change moves both sides together.
            var expectedRows = QuickActionsGridWindow.MaxRowsFor(area.Height, window.RootBorder.BorderThickness);
            Check.WithCustomMessage(
                    $"at DPI {scale} the cap kept {vm.Tiles.Count} tiles; {vm.GridColumns} columns x "
                    + $"{expectedRows} rows that fit the work area is {vm.GridColumns * expectedRows}.")
                .That(vm.Tiles.Count).IsEqualTo(vm.GridColumns * expectedRows);
            Check.That(expectedRows).IsStrictlyGreaterThan(0);

            Check.WithCustomMessage("the capped grid still overflows the work-area height")
                .That(window.Height).IsLessOrEqualThan(area.Height);
            Check.That(window.Width).IsLessOrEqualThan(area.Width);
            Check.WithCustomMessage("a full grid must be laid out at the full cell pitch")
                .That(window.Height).IsCloseTo((expectedRows * QuickActionsGridWindow.TileCell)
                    + QuickActionsGridWindow.ChromeHeight(window.RootBorder.BorderThickness), 1e-6);

            var faces = grid.TileFaces().Select(grid.InContent).ToList();
            for (var i = 0; i < faces.Count; i++)
            {
                var rect = faces[i];
                Check.WithCustomMessage(
                        $"at DPI {scale} tile '{vm.Tiles[i].Label}' ({rect}) leaves the "
                        + $"{window.Width:0.##}×{window.Height:0.##} window.")
                    .That(rect.Left >= -1e-6 && rect.Top >= -1e-6
                          && rect.Right <= window.Width + 1e-6
                          && rect.Bottom <= window.Height + 1e-6)
                    .IsTrue();
            }

            // Row centres are exactly TileCell apart — the pitch the R4 gap is derived from.
            var rowCentres = faces.Select(f => Centre(f).Y)
                .Select(y => System.Math.Round(y, 6))
                .Distinct()
                .OrderBy(y => y)
                .ToList();
            Check.WithCustomMessage($"at DPI {scale} the grid should have {expectedRows} rows")
                .That(rowCentres).HasSize(expectedRows);
            for (var i = 1; i < rowCentres.Count; i++)
            {
                Check.WithCustomMessage(
                        $"at DPI {scale} rows {i - 1} and {i} are {rowCentres[i] - rowCentres[i - 1]:0.###} "
                        + $"DIP apart; the R4 pitch is {QuickActionsGridWindow.TileCell}.")
                    .That(rowCentres[i] - rowCentres[i - 1])
                    .IsCloseTo(QuickActionsGridWindow.TileCell, 1e-6);
            }

            // R4: at least 20 DIP of dead space between any two different hit circles. Two tiles
            // are 128 DIP apart (their cells), so every pair clears 2 × r50 + 20 = 120 DIP.
            var centres = faces.Select(Centre).ToList();
            for (var i = 0; i < centres.Count; i++)
            {
                for (var j = i + 1; j < centres.Count; j++)
                {
                    var distance = (centres[i] - centres[j]).Length;
                    Check.WithCustomMessage(
                            $"at DPI {scale} tiles '{vm.Tiles[i].Label}' and '{vm.Tiles[j].Label}' "
                            + $"have hit circles only {distance - 2 * UIConstants.ButtonRadius:0.##} "
                            + "DIP apart; R4 requires 20.")
                        .That(distance)
                        .IsGreaterOrEqualThan(2 * UIConstants.ButtonRadius + MinimumGapDip);
                }
            }
        });
    }

    // ── B8b F4: a re-show after the count grew uses the COMPUTED size ──────────────────────

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AfterTheCountGrew_AShowPositionsTheGridByItsComputedSize(double scale)
    {
        WithDpi(scale, () =>
        {
            using var grid = QuickActionsGridHost.Open(scale, tileCount: 3);
            var window = grid.Window;
            var area = SystemParameters.WorkArea;
            grid.Relayout();
            var smallHeight = window.Height;

            // The tile count grows between shows; ResizeToContent recomputes the size, and the
            // placement must be made from that fresh pair — on the Bottom edge a stale (smaller)
            // height is exactly what makes a grown grid overhang the work area.
            grid.SetTileCount(60);
            window.ResizeToContent();
            grid.Relayout();

            Check.WithCustomMessage("the grown grid did not grow its computed height")
                .That(window.Height).IsStrictlyGreaterThan(smallHeight);

            foreach (var edge in new[] { "Left", "Right", "Top", "Bottom" })
            {
                window.PositionOnEdge(edge);

                Check.WithCustomMessage(
                        $"edge {edge} at DPI {scale}: the re-shown grid "
                        + $"({window.Left:0.##},{window.Top:0.##} {window.Width:0.##}×{window.Height:0.##}) "
                        + $"leaves the work area {area}.")
                    .That(window.Left >= area.Left - 1e-6 && window.Top >= area.Top - 1e-6
                          && window.Left + window.Width <= area.Right + 1e-6
                          && window.Top + window.Height <= area.Bottom + 1e-6)
                    .IsTrue();
            }
        });
    }

    [Fact]
    public void PositionOnEdge_UsesTheComputedWidthAndHeight_NotTheStaleActualSize()
    {
        // The deterministic half of F4. A hidden window gets no layout pass, so ActualWidth/
        // ActualHeight still hold the PREVIOUS show's size — they cannot be made stale headlessly
        // (a Window arranged without its HwndSource keeps RenderSize 0), hence a source pin. The
        // body must place with the Width/Height ResizeToContent has just set. Mutant: restore the
        // Actual-first preference → red.
        var source = SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "QuickActionsGridWindow.xaml.cs")));

        var start = source.IndexOf("public void PositionOnEdge(string edge)", StringComparison.Ordinal);
        Check.WithCustomMessage("QuickActionsGridWindow.PositionOnEdge is gone")
            .That(start).IsStrictlyGreaterThan(-1);

        var bodyStart = source.IndexOf('{', start);
        var bodyEnd = source.IndexOf("\n    }", bodyStart, StringComparison.Ordinal);
        var body = System.Text.RegularExpressions.Regex.Replace(
            source[(bodyStart + 1)..bodyEnd], @"\s+", string.Empty);

        Check.WithCustomMessage(
                "PositionOnEdge must place the grid by the computed Width/Height that "
                + "ResizeToContent has just set — ActualWidth/ActualHeight are the hidden "
                + "window's previous show size.")
            .That(body.Contains("PlaceAtEdge(workArea,edge,Width,Height)")).IsTrue();
        Check.WithCustomMessage("PositionOnEdge still prefers the stale ActualWidth/ActualHeight")
            .That(body.Contains("ActualWidth") || body.Contains("ActualHeight")).IsFalse();
    }

    // ── B8b: the Contrast theme's 2 DIP border is the tightest chrome ───────────────────────

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AtTheContrastBorderThickness_TheCloseDiscKeepsTheDeadGapToEveryTile(double scale)
    {
        WithDpi(scale, () =>
        {
            // Only the window chrome's border is theme-dependent geometry here (the tile face's
            // border is drawn inside a fixed 100 DIP disc), so the Contrast theme's 2 DIP border
            // is modelled directly rather than activating a theme process-wide.
            using var grid = QuickActionsGridHost.Open(scale, borderThickness: 2);
            var window = grid.Window;
            var vm = grid.Vm;
            Check.That(window.RootBorder.BorderThickness.Left).IsEqualTo(2);

            // The chrome term keeps the inner pitch at exactly 128 DIP even at border 2, so the
            // face still fills its cell.
            foreach (var face in grid.TileFaces())
                Check.That(face.ActualWidth).IsCloseTo(QuickActionsGridWindow.TileSize, 1e-6);

            window.SyncTilePositions();
            var targets = vm.DetectorForTest.Targets.ToDictionary(t => t.Id);
            var closeTarget = targets[QuickActionsGridViewModel.CloseTargetId];

            foreach (var tile in vm.Tiles)
            {
                var tileTarget = targets[tile.Action.Id];
                var gapDip = (closeTarget.Center.DistanceTo(tileTarget.Center)
                              - closeTarget.Radius - tileTarget.Radius) / scale;

                Check.WithCustomMessage(
                        $"at DPI {scale} with the Contrast border (2 DIP) the close hit circle "
                        + $"leaves only {gapDip:0.##} DIP to {tile.Label}; R4 requires "
                        + $"{MinimumGapDip}.")
                    .That(gapDip).IsGreaterOrEqualThan(MinimumGapDip);
            }
        });
    }
}
