namespace EyeCtrl.App.Tests.Overlays;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// B8: the Quick Actions close button is drawn to its r50 hit area, and every tile's hit circle
/// keeps the R4 dead gap of at least 20 DIP (hit radius ≥ 50 DIP and a ≥ 20 DIP gap between
/// different actions: the r50 close target used to sit on the first tile's r50 circle).
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
}
