namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

/// <summary>
/// Overlay window that renders the Quick Actions grid. Binds to
/// <see cref="QuickActionsGridViewModel"/>, positions itself on the configured screen edge,
/// and pushes each laid-out tile's real screen centre back into the VM so the DwellDetector
/// hit-tests against true coordinates. The close button is a dwell target (not a WPF Button —
/// gaze produces no mouse clicks). Mirrors the TaskSwitcherWindow layout-sync pattern.
/// </summary>
public partial class QuickActionsGridWindow : OverlayWindow
{
    private QuickActionsGridViewModel? _vm;

    /// <summary>Creates the window and applies non-click-through, non-focusable overlay flags.</summary>
    public QuickActionsGridWindow()
    {
        InitializeComponent();
        SetOverlayInteraction(clickThrough: false, focusable: false);
    }

    /// <summary>
    /// Wires the window to its ViewModel: sets the DataContext, reacts to IsVisible changes
    /// (show/hide + edge positioning), and arms a one-shot post-layout tile-position sync so
    /// dwell targets get real screen coordinates instead of (0,0) placeholders.
    /// </summary>
    /// <param name="vm">The grid ViewModel this window displays and reports positions to.</param>
    public void Configure(QuickActionsGridViewModel vm)
    {
        _vm = vm;
        DataContext = vm;   // TilesControl inherits DataContext — no separate assignment needed.

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(QuickActionsGridViewModel.IsVisible)) return;
            if (vm.IsVisible)
            {
                // Use the resolved edge (EffectiveSummonEdge) so the grid anchors where the user
                // actually summoned it — ComputeActualSummonEdge may flip the configured edge when
                // it collides with the toolbar dock. Fall back to the configured edge if unset.
                var edge = string.IsNullOrEmpty(vm.EffectiveSummonEdge) ? vm.SummonEdge : vm.EffectiveSummonEdge;
                ResizeToContent();   // size to columns×rows so no tile is clipped or pushed off-window
                PositionOnEdge(edge);
                Show();
                // Push real tile centres into the VM once layout completes. DispatcherPriority.Loaded
                // fires after arrange (containers have real sizes/positions) — the same one-shot
                // pattern as TaskSwitcherWindow. A permanent LayoutUpdated handler was both chattier
                // (fires every layout pass for the window's life) and could run before the first arrange.
                Dispatcher.BeginInvoke(new Action(SyncTilePositions), DispatcherPriority.Loaded);
            }
            else
            {
                Hide();
            }
        };
    }

    /// <summary>
    /// Positions the grid window on the specified screen edge, vertically centered.
    /// Call before Show() when IsVisible changes to true.
    /// </summary>
    /// <param name="edge">One of "Left", "Right", "Top", "Bottom". Unknown values leave the position unchanged.</param>
    public void PositionOnEdge(string edge)
    {
        var workArea = SystemParameters.WorkArea; // logical DIPs
        double gridW = ActualWidth > 0 ? ActualWidth : Width;
        double gridH = ActualHeight > 0 ? ActualHeight : Height;

        switch (edge)
        {
            case "Left":
                Left = 0;
                Top = (workArea.Height - gridH) / 2 + workArea.Top;
                break;
            case "Right":
                Left = workArea.Width - gridW;
                Top = (workArea.Height - gridH) / 2 + workArea.Top;
                break;
            case "Top":
                Left = (workArea.Width - gridW) / 2 + workArea.Left;
                Top = 0;
                break;
            case "Bottom":
                Left = (workArea.Width - gridW) / 2 + workArea.Left;
                Top = workArea.Height - gridH;
                break;
        }
    }

    /// <summary>
    /// Sizes the window to fit every tile — columns from the VM, rows computed — plus the
    /// close-button row, clamped to the work area. Gaze users can't scroll, so all tiles must be
    /// visible; the previous fixed 400×600 clipped 3+ column grids and pushed extra rows off-window.
    /// </summary>
    private void ResizeToContent()
    {
        if (_vm == null) return;

        const double cell = 128;      // 120 tile + 4+4 tile margin
        const double outerPad = 24;   // outer Grid Margin (12 per side)
        const double closeRow = 52;   // 44 close button + spacing

        int cols = Math.Max(1, _vm.GridColumns);
        int rows = _vm.Tiles.Count == 0 ? 0 : (int)Math.Ceiling((double)_vm.Tiles.Count / cols);

        var wa = SystemParameters.WorkArea; // logical DIPs
        Width = Math.Min(cols * cell + outerPad, wa.Width);
        Height = Math.Min(rows * cell + closeRow + outerPad, wa.Height);
    }

    /// <summary>
    /// Reads each laid-out tile container's screen centre and pushes it into the VM,
    /// which creates/refreshes the corresponding dwell target. Mirrors
    /// TaskSwitcherWindow's pattern. Without this call the grid has NO dwellable tiles
    /// (Reload deliberately does not seed (0,0) targets — see QuickActionsGridViewModel.Reload).
    /// </summary>
    public void SyncTilePositions()
    {
        if (_vm == null) return;

        foreach (var item in TilesControl.Items)
        {
            if (item is not QuickActionTileViewModel tile) continue;
            var container = TilesControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container == null) continue;

            // Visual.PointToScreen already returns PHYSICAL device pixels — do NOT multiply by
            // ScaleX/ScaleY (that double-scales dwell targets on >100% DPI, breaking all dwell).
            var center = container.PointToScreen(new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            _vm.UpdateTilePosition(tile.Action.Id, new PhysicalPoint(center.X, center.Y));
        }

        // Register the close button as a dwell target too (same physical-pixel convention).
        var closeCenter = CloseButton.PointToScreen(
            new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
        _vm.UpdateCloseButtonPosition(new PhysicalPoint(closeCenter.X, closeCenter.Y));
    }
}
