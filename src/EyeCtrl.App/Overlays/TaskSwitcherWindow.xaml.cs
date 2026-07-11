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
    /// updates tile positions and activates the VM. Activation (IsVisible=true) happens
    /// only after positions are real, not at the (0,0) placeholder (H3 fix).
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

        // After layout, update tile positions for dwell hit-testing, THEN activate the VM
        // (H3 fix: IsVisible is set only after positions are real, not at the (0,0) placeholder).
        Dispatcher.BeginInvoke(() =>
        {
            UpdateTilePositions();
            _viewModel?.ActivateAfterLayout();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// For each tile, compute its screen center from the realized container and push it
    /// back into the VM so the DwellDetector hit-tests against real coordinates.
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

            // Transform the container's center to window coordinates, then to physical pixels.
            var center = fe.TransformToAncestor(this).Transform(new Point(fe.ActualWidth / 2, fe.ActualHeight / 2));
            var physical = new PhysicalPoint(
                DpiHelper.LogicalToPhysicalX(center.X),
                DpiHelper.LogicalToPhysicalY(center.Y));
            _viewModel.UpdateTilePosition(tile.Hwnd, physical);
        }
    }

    /// <summary>Called by the coordinator when the VM hides (selection or second toggle).</summary>
    public void HideOverlay()
    {
        Hide();
    }
}
