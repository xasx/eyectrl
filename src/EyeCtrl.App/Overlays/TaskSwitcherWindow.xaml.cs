namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
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
public partial class TaskSwitcherWindow : OverlayWindow, ITaskSwitcherOverlay
{
    /// <summary>
    /// The close disc's diameter: <b>2 × the shared hit radius</b>, declared in XAML as
    /// Width/Height 100. B8: the disc must be drawn to exactly the size the dwell detector
    /// hit-tests (<see cref="UIConstants.ButtonRadius"/> = 50 DIP), so a gaze that lands on the
    /// visible circle always lands in its hit area — what is drawn IS what is dwell-tested.
    /// <c>internal</c> so a size-coupling pin can assert the XAML attribute against it instead
    /// of restating the number (QuickActionsGridWindow.CloseButtonSize precedent).
    /// </summary>
    internal const double CloseButtonSize = UIConstants.ButtonRadius * 2;

    private TaskSwitcherViewModel? _viewModel;
    private IAppIconProvider? _iconProvider;

    public TaskSwitcherWindow()
    {
        InitializeComponent();

        // The close glyph fills the whole disc, so it takes the Dominant ratio. Assigned here
        // rather than in XAML because XAML cannot multiply (QuickActionsGridWindow pattern).
        CloseGlyph.FontSize = CloseButtonSize * ContainerRatio.Dominant;
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
    /// For each tile, compute its real on-screen rectangle (the drawn tile face) and push it
    /// into the VM so the DwellDetector hit-tests the drawn face, not a circle that ignores
    /// the corners. Tiles that do not overlap the ScrollViewer's viewport get NO target: a
    /// fully clipped tile keeps no live dwell target at its on-screen position, where gaze
    /// resting on the bottom band would silently switch windows.
    /// </summary>
    private void UpdateTilePositions()
    {
        if (_viewModel == null) return;

        // With the scrollbar Disabled/Hidden there is no scrollbar chrome, so the
        // ScrollViewer's whole area IS its viewport. Its physical rect is the reachability
        // boundary — reachability is geometric (viewport intersection), not ordinal.
        var vpTopLeft = TilesScroll.PointToScreen(new Point(0, 0));
        var vpBottomRight = TilesScroll.PointToScreen(
            new Point(TilesScroll.ActualWidth, TilesScroll.ActualHeight));
        var viewportRect = new PhysicalRect(
            vpTopLeft.X, vpTopLeft.Y,
            vpBottomRight.X - vpTopLeft.X, vpBottomRight.Y - vpTopLeft.Y);

        foreach (var item in TilesPanel.Items)
        {
            if (item is not TaskSwitcherTileViewModel tile)
                continue;

            var container = TilesPanel.ItemContainerGenerator.ContainerFromItem(tile);
            if (container is not FrameworkElement fe)
                continue;

            // The dwell target is the tile's DRAWN face: the template's root Border (the
            // container is the generator's ContentPresenter around it). Its 8 DIP margin is
            // the inter-tile gap — outside the face, so it must stay dead.
            if (FindTemplateBorder(fe) is not Border border)
                continue;

            // PointToScreen, like every other overlay (Keyboard, Toolbar, QuickActions,
            // Settings): it returns PHYSICAL device pixels and folds in Windows'
            // DPI virtualization. This window used to compute the center via
            // TransformToAncestor + DpiHelper.LogicalToPhysicalX — the one overlay not using
            // PointToScreen — and under a DPI-virtualized process (ScaleX telling only half
            // the story) the targets landed far from the tiles' true screen positions: tiles
            // visible, gaze never inside any hit circle, nothing dwellable (2026-08-17
            // field report). Two calls (top-left, bottom-right of the Border) give the whole
            // rect; see QuickActionsGridWindow's own "do NOT multiply by DpiHelper".
            var topLeft = border.PointToScreen(new Point(0, 0));
            var bottomRight = border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight));
            var faceRect = new PhysicalRect(topLeft.X, topLeft.Y,
                bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            if (!TileOverlapsViewport(faceRect, viewportRect))
                continue;   // fully clipped: no target, no invisible accumulation

            _viewModel.UpdateTilePosition(tile.Hwnd, faceRect);
        }

        // Register the close disc as a dwell target too (same physical-pixel convention).
        var closeCenter = CloseButton.PointToScreen(
            new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
        _viewModel.UpdateCloseButtonPosition(new PhysicalPoint(closeCenter.X, closeCenter.Y));
    }

    /// <summary>
    /// Whether a tile's on-screen face overlaps the ScrollViewer's viewport. Boundary touch
    /// does NOT count (<see cref="PhysicalRect.IntersectsWith"/> is strict): a tile whose only
    /// contact with the viewport is a shared edge has zero visible pixels, and a live dwell
    /// target there is exactly the hazard this filter exists for. <c>internal static</c> pure
    /// helper so the decision unit-tests headlessly (ForegroundSwitchDiagnosis.Classify
    /// precedent).
    /// </summary>
    internal static bool TileOverlapsViewport(PhysicalRect tileRect, PhysicalRect viewportRect) =>
        tileRect.IntersectsWith(viewportRect);

    /// <summary>
    /// The tile DataTemplate's root Border: the first Border among the container's visual
    /// children (the generator wraps the template root in a ContentPresenter).
    /// </summary>
    private static Border? FindTemplateBorder(DependencyObject container)
    {
        var count = VisualTreeHelper.GetChildrenCount(container);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(container, i) is Border border)
                return border;
        }
        return null;
    }

    /// <summary>Called by the coordinator when the VM hides (selection or second toggle).</summary>
    public void HideOverlay()
    {
        Hide();
    }
}
