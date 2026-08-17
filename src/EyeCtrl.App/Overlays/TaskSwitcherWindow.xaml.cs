namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

/// <summary>
/// Fullscreen Task Switcher overlay. Shows a grid of tiles (one per open window)
/// and resolves each tile's process icon via <see cref="IAppIconProvider"/> (the tile
/// VM stores only the ProcessId to stay WPF-free). NOT click-through — it must receive
/// dwell. Positions are computed after layout and pushed back into the VM's DwellDetector
/// so gaze hit-testing uses real screen coordinates (H3 fix).
/// </summary>
public partial class TaskSwitcherWindow : OverlayWindow
{
    private TaskSwitcherViewModel? _viewModel;
    private IAppIconProvider? _iconProvider;

    public TaskSwitcherWindow()
    {
        InitializeComponent();
    }

    public void Configure(TaskSwitcherViewModel viewModel, IAppIconProvider iconProvider)
    {
        _viewModel = viewModel;
        _iconProvider = iconProvider;
        DataContext = viewModel;
    }

    /// <summary>
    /// Resolves icons for each tile, covers the primary screen, then — after layout —
    /// pushes each tile's real screen center into the VM, which creates the dwell target
    /// there. The VM has already set IsVisible (that is what triggered this call); creating
    /// targets only post-layout keeps them off the (0,0) placeholder during the layout gap.
    /// </summary>
    public void ShowFullScreen()
    {
        // Resolve icons for each tile (View-side concern; the tile VM stores only ProcessId)
        if (_iconProvider != null && _viewModel != null)
        {
            foreach (var tile in _viewModel.Tiles)
                tile.Icon = _iconProvider.GetIcon(tile.ProcessId);
        }

        // Cover the primary screen
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;
        Show();

        // After layout, push real tile positions into the VM (which creates the dwell targets).
        Dispatcher.BeginInvoke(UpdateTilePositions, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// For each tile, compute its real on-screen center and push it into the VM so the
    /// DwellDetector hit-tests against true coordinates.
    /// </summary>
    private void UpdateTilePositions()
    {
        if (_viewModel == null) return;

        foreach (var item in TilesPanel.Items)
        {
            if (item is not TaskSwitcherTileViewModel tile)
                continue;

            var container = TilesPanel.ItemContainerGenerator.ContainerFromItem(tile);
            if (container is not FrameworkElement fe)
                continue;

            // PointToScreen, like every other overlay (Keyboard, Toolbar, QuickActions,
            // Settings): it returns PHYSICAL device pixels and folds in Windows' DPI
            // virtualization. This window used to compute the center via
            // TransformToAncestor + DpiHelper.LogicalToPhysicalX — the one overlay not using
            // PointToScreen — and under a DPI-virtualized process (ScaleX telling only half
            // the story) the targets landed far from the tiles' true screen positions: tiles
            // visible, gaze never inside any hit circle, nothing dwellable (2026-08-17
            // field report). See QuickActionsGridWindow's own "do NOT multiply by DpiHelper".
            var center = fe.PointToScreen(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
            _viewModel.UpdateTilePosition(tile.Hwnd, new PhysicalPoint(center.X, center.Y));
        }
    }

    /// <summary>Called by the coordinator when the VM hides (selection or second toggle).</summary>
    public void HideOverlay()
    {
        Hide();
    }
}
