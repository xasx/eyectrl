namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
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
    // H2 fix: guard so LayoutUpdated syncs tile positions once per show, not every layout pass.
    private bool _pendingTileSync;

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
                PositionOnEdge(edge);
                Show();
                // Arm a one-shot sync: LayoutUpdated fires after the tiles get real positions.
                _pendingTileSync = true;
            }
            else
            {
                Hide();
            }
        };

        // SyncTilePositions MUST run after layout so containers have real screen coords.
        // Without it, Reload() created zero dwell targets and the grid was non-dwellable.
        TilesControl.LayoutUpdated += OnTilesLayoutUpdated;
    }

    private void OnTilesLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_pendingTileSync) return;
        _pendingTileSync = false;
        SyncTilePositions();
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
