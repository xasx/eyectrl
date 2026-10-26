namespace EyeCtrl.App.Tests.Overlays;

using System.Linq;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Pin on the XAML's tile population. The grid's ItemsControl had no <c>ItemsSource</c>, so
/// <c>QuickActionsGridWindow.Configure</c> set the DataContext and nothing else: the control
/// rendered zero items for a non-empty <see cref="QuickActionsGridViewModel.Tiles"/>, and
/// <c>SyncTilePositions</c> therefore registered no tile targets — configured quick actions were
/// sized into the window but never shown or dwellable. B8's close gap is measured against these
/// tiles, so the meant layout has to be the real one.
///
/// <para>
/// This drives the REAL XAML through <c>Configure(vm)</c> only — nothing hand-sets ItemsSource —
/// and asserts both halves: the control renders one item per VM tile, and the production push
/// registers one dwell target per rendered tile (plus the close). A removed binding leaves both
/// at zero.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class QuickActionsGridTilesBindingTests
{
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ConfiguredTiles_RenderFromTheXamlBinding_AndRegisterTheirHitTargets(double scale)
    {
        var (previousX, previousY) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            using var grid = QuickActionsGridHost.Open(scale);
            var window = grid.Window;
            var vm = grid.Vm;

            Check.WithCustomMessage(
                    "TilesControl renders fewer items than the VM holds — the XAML's "
                    + "ItemsSource=\"{Binding Tiles}\" is missing or broken.")
                .That(window.TilesControl.Items).HasSize(vm.Tiles.Count);
            Check.That(vm.Tiles).HasSize(5);

            _ = grid.TileContainers();   // throws if a configured tile has no realised container

            // The production push registers one target per rendered tile, plus the close.
            window.SyncTilePositions();
            var targets = vm.DetectorForTest.Targets;
            Check.WithCustomMessage(
                    $"{targets.Count} targets registered for {vm.Tiles.Count} rendered tiles + close")
                .That(targets).HasSize(vm.Tiles.Count + 1);
            Check.WithCustomMessage("the close target is not registered")
                .That(targets.Any(t => t.Id == QuickActionsGridViewModel.CloseTargetId)).IsTrue();

            foreach (var tile in vm.Tiles)
            {
                var target = targets.SingleOrDefault(t => t.Id == tile.Action.Id);
                Check.WithCustomMessage($"tile '{tile.Label}' has no dwell target")
                    .That(target).IsNotNull();
                Check.WithCustomMessage($"tile '{tile.Label}' registered a hit circle that is not r50")
                    .That(target!.Radius / scale).IsCloseTo(UIConstants.ButtonRadius, 1e-9);
            }
        }
        finally
        {
            DpiHelper.Update(previousX, previousY);
        }
    }
}
