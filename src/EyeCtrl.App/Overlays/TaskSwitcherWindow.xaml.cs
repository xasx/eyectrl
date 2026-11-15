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

        // Pin the hint row to the height TaskSwitcherLayout reserves for it. An Auto row measures
        // the same today (the 100 DIP disc plus its 12 DIP top/bottom margins), but the tile area's
        // row capacity is computed against this constant — a font or template change that grew the
        // row would otherwise silently clip the bottom full row the geometry promised.
        HintRow.Height = TaskSwitcherLayout.HintRowHeight;
    }

    public void Configure(TaskSwitcherViewModel viewModel, IAppIconProvider iconProvider)
    {
        _viewModel = viewModel;
        _iconProvider = iconProvider;
        DataContext = viewModel;
    }

    /// <summary>
    /// Resolves icons for each tile, covers the primary screen, then — after layout —
    /// pushes each tile's real on-screen rectangle (clamped to its visible face) into the
    /// VM, which creates the dwell target there. The VM has already set IsVisible (that is
    /// what triggered this call); creating targets only post-layout keeps them off the
    /// (0,0) placeholder during the layout gap. The tile viewport is laid clear of the docked
    /// toolbar band and the screen edges by <see cref="TaskSwitcherLayout"/> before the show.
    /// </summary>
    public void ShowFullScreen(ToolbarKeepOut? toolbarKeepOut)
    {
        // Resolve icons for each tile (View-side concern; the tile VM stores only ProcessId)
        if (_iconProvider != null && _viewModel != null)
        {
            foreach (var tile in _viewModel.Tiles)
                tile.Icon = _iconProvider.GetIcon(tile.ProcessId);
        }

        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        // The keep-out arrives in physical pixels (IToolbarOverlay.GetPhysicalBounds); the pure
        // helper works in DIP and converts once through the live scale. Applying the layout as
        // margins around the tile viewport is what makes the grid's drawn geometry — and therefore
        // the dwell targets registered from it — match the computed area.
        ApplyLayout(
            ComputeLayout(toolbarKeepOut, screenWidth, screenHeight,
                DpiHelper.ScaleX, DpiHelper.ScaleY, _viewModel?.Tiles.Count ?? 0),
            screenWidth);

        // Cover the primary screen
        Width = screenWidth;
        Height = screenHeight;
        Left = 0;
        Top = 0;
        Show();

        // After layout, push real tile positions into the VM (which creates the dwell targets).
        Dispatcher.BeginInvoke(UpdateTilePositions, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Composes the physical toolbar keep-out into the pure DIP layout. Split out so the
    /// physical-to-DIP conversion is testable headlessly at DPI 1.0 and 2.0 without touching the
    /// process-wide <see cref="DpiHelper"/> state.
    /// </summary>
    internal static TaskSwitcherLayout.Result ComputeLayout(
        ToolbarKeepOut? toolbarKeepOut,
        double screenWidthDip, double screenHeightDip,
        double scaleX, double scaleY, int tileCount)
        => TaskSwitcherLayout.Compute(
            screenWidthDip, screenHeightDip,
            toolbarKeepOut is { } keepOut
                ? TaskSwitcherLayout.ToDip(keepOut.Bounds, scaleX, scaleY)
                : null,
            toolbarKeepOut?.DockedEdge,
            tileCount);

    /// <summary>
    /// Applies a computed layout to the window's elements: the tile viewport's margins (which
    /// define the area the helper measured), the block width/height (full columns and shown rows),
    /// and the hint row's clearance above a bottom-docked toolbar band. The hint row's own height
    /// is pinned in the constructor, so its layout slot is exactly
    /// <see cref="TaskSwitcherLayout.HintRowHeight"/> plus that clearance. Split from
    /// <see cref="ShowFullScreen"/> so it is testable without showing the window.
    /// </summary>
    internal void ApplyLayout(TaskSwitcherLayout.Result layout, double screenWidthDip)
    {
        TilesScroll.Margin = new Thickness(
            layout.Area.X, layout.Area.Y, screenWidthDip - layout.Area.Right, 0);
        TilesScroll.Width = layout.Block.Width;
        TilesScroll.Height = layout.Block.Height;
        HintRow.Margin = new Thickness(0, 0, 0, layout.HintBottomMargin);
    }

    /// <summary>
    /// For each tile, compute its real on-screen rectangle (the drawn tile face) and push its
    /// VISIBLE part — the intersection with the ScrollViewer's viewport — into the VM, so the
    /// DwellDetector hit-tests exactly what is drawn: not a circle that ignores the corners,
    /// and not the hidden remainder of a row the viewport clips. Tiles with no visible area
    /// get NO target: gaze resting on the bottom band (the hint row, the close disc) must
    /// never silently switch windows through a tile that is not on screen.
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

            // Register only the VISIBLE face: the intersection with the viewport. The
            // ScrollContentPresenter clips tiles at the viewport edge, so for a partially
            // clipped row this intersection IS what the user sees drawn; registering the
            // full face would keep its hidden part a live target under the hint row and
            // beside the close disc, silently switching windows when gaze rests there.
            var visibleRect = VisibleTileRect(faceRect, viewportRect);
            if (visibleRect.Width <= 0 || visibleRect.Height <= 0)
                continue;   // fully clipped (or edge-touching): no target, no invisible accumulation

            _viewModel.UpdateTilePosition(tile.Hwnd, visibleRect);
        }

        // Register the close disc as a dwell target too (same physical-pixel convention).
        var closeCenter = CloseButton.PointToScreen(
            new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
        _viewModel.UpdateCloseButtonPosition(new PhysicalPoint(closeCenter.X, closeCenter.Y));
    }

    /// <summary>
    /// The part of a tile's on-screen face that lies inside the ScrollViewer's viewport — its
    /// VISIBLE face, the only part that may carry a dwell target. An overlap of zero area
    /// (fully clipped, or merely touching the viewport edge — the same strictness as
    /// <see cref="PhysicalRect.IntersectsWith"/>) yields a non-positive width or height,
    /// which the caller treats as "no target": a tile whose only contact with the viewport is
    /// a shared edge has zero visible pixels, and a live dwell target there is exactly the
    /// hazard this clamp exists for. <c>internal static</c> pure helper so the geometry
    /// unit-tests headlessly (ForegroundSwitchDiagnosis.Classify precedent).
    /// </summary>
    internal static PhysicalRect VisibleTileRect(PhysicalRect face, PhysicalRect viewport)
    {
        var left = Math.Max(face.X, viewport.X);
        var top = Math.Max(face.Y, viewport.Y);
        var right = Math.Min(face.X + face.Width, viewport.X + viewport.Width);
        var bottom = Math.Min(face.Y + face.Height, viewport.Y + viewport.Height);
        return new PhysicalRect(left, top, right - left, bottom - top);
    }

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
