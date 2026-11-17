namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using EyeCtrl.App.Services;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Platform.Icons;
using EyeCtrl.Platform.Thumbnails;
using EyeCtrl.ViewModels;

/// <summary>
/// Fullscreen Task Switcher overlay. Shows a grid of tiles (one per open window)
/// and resolves each tile's app icon via <see cref="IAppIconProvider"/> (the tile
/// VM stores only the handle and process id to stay WPF-free). NOT click-through — it must receive
/// dwell. Positions are computed after layout and pushed back into the VM's DwellDetector
/// so gaze hit-testing uses real screen coordinates (H3 fix).
///
/// <para>
/// The window is deliberately NON-layered (<c>AllowsTransparency="False"</c>): DWM thumbnails do
/// not render into a layered window. Because a non-layered window can expose an unpainted first
/// frame, the show path cloaks the window through DWM until WPF has rendered it (the same shape
/// <see cref="GlassBackdropWindow"/> uses) and the XAML paints the opaque form of the theme's
/// SurfaceDark (a non-layered window composites partial alpha against black).
/// </para>
/// </summary>
public partial class TaskSwitcherWindow : OverlayWindow, ITaskSwitcherOverlay
{
    /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED: stops DWM's open/close animation for the window.</summary>
    private const int DwmwaTransitionsForceDisabled = 3;

    /// <summary>DWMWA_CLOAK: DWM skips composing the window entirely while true.</summary>
    private const int DwmwaCloak = 13;

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
    private IWindowThumbnailService? _thumbnailService;

    /// <summary>
    /// The live DWM thumbnails of THIS show, one entry per window tile that got a picture (S4-C).
    /// Every handle is unregistered in <see cref="UnregisterThumbnails"/>, which runs at the start
    /// of a show, in <see cref="HideOverlay"/> and on close — a handle may never survive a hide, or
    /// a session that re-opens fifty times grows DWM's thumbnail set fifty times.
    /// </summary>
    private readonly List<(TaskSwitcherTileViewModel Tile, IntPtr Handle)> _tileThumbnails = [];

    private bool _cloaked;
    private DispatcherTimer? _cloakFallback;
    private EventHandler? _uncloakOnRendering;

    /// <summary>
    /// One-way test seam. While set, <see cref="WriteCloak"/> records every DWM uncloak REQUEST
    /// and does not send it (a cloak request is always sent). Set once by the test fixture's
    /// <c>InvisibleTestWindow.PrepareCloaked</c>; there is deliberately no way to clear it, so a
    /// test can never turn test invisibility off. Never set in production.
    /// </summary>
    private bool _stayCloakedForTests;

    /// <summary>How many uncloak requests the one-way seam suppressed (asserted by the cloak gates).</summary>
    internal int SuppressedUncloakRequestsForTests { get; private set; }

    /// <summary>
    /// Fires when the one-way seam suppresses an uncloak request, so a gate can place that request
    /// in a recorded sequence (cloak → first frame → flush → request) without ever writing to DWM.
    /// </summary>
    internal event Action? UncloakSuppressedForTests;

    /// <summary>
    /// Fires at the end of every <see cref="UpdateTilePositions"/> pass (after the dwell targets
    /// were pushed into the VM), so the show-path gate can prove target registration precedes the
    /// uncloak request. A test hook; inert in production.
    /// </summary>
    internal event Action? TilePositionsUpdatedForTests;

    /// <summary>
    /// The one-way test seam: callers can only turn it ON. No setter and no clear path exist, so a
    /// test cannot switch test invisibility off.
    /// </summary>
    internal void KeepCloakedForTests() => _stayCloakedForTests = true;

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

        // DWM's open transition would animate a non-layered window in; the switcher must appear
        // in ONE finished frame (no flash on opening), so the transition is off for this HWND's
        // lifetime — the GlassBackdropWindow precedent.
        SourceInitialized += DisableTransitionsAtHandleCreation;
    }

    /// <summary>
    /// Wires the session's view model, the icon seam, and — S4-C — the live-preview seam. The
    /// thumbnail service is optional so a gate can build the window without registering anything
    /// real; production (<see cref="OverlayManager"/>) passes Platform's
    /// <see cref="Win32WindowThumbnailService"/>.
    /// </summary>
    public void Configure(
        TaskSwitcherViewModel viewModel,
        IAppIconProvider iconProvider,
        IWindowThumbnailService? thumbnailService = null)
    {
        _viewModel = viewModel;
        _iconProvider = iconProvider;
        _thumbnailService = thumbnailService;
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
        // Defensive: no handle may survive into a new show. HideOverlay already released them; a
        // path that reached here without a hide (a re-show of a still-visible window) must not
        // stack a second thumbnail per tile.
        UnregisterThumbnails();

        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        // S3-A capacity first: the "Show desktop" tile takes a visible slot, so it can cost the
        // last WINDOW its tile. Capacity is a property of the grid region alone (columns x rows),
        // never of the tile count, so measuring it with an empty count gives the same figure the
        // final layout will use. The VM trims the window tiles it cannot show; the overflow chip
        // then counts them, and the desktop tile keeps the last slot.
        var capacity = ComputeLayout(
            toolbarKeepOut, screenWidth, screenHeight,
            DpiHelper.ScaleX, DpiHelper.ScaleY, tileCount: 0).Capacity;
        _viewModel?.ApplyTileCapacity(capacity);

        // Resolve icons for each window tile (View-side concern; the tile VM stores only the handle
        // and process id). After the capacity trim, so a window that lost its slot never pays for
        // an icon lookup.
        ResolveIcons();

        // The keep-out arrives in physical pixels (IToolbarOverlay.GetPhysicalBounds); the pure
        // helper works in DIP and converts once through the live scale. Applying the layout as
        // margins around the tile viewport is what makes the grid's drawn geometry — and therefore
        // the dwell targets registered from it — match the computed area. The tile count is the
        // DRAWN panel: window tiles plus the desktop tile, when the session has one.
        ApplyLayout(
            ComputeLayout(toolbarKeepOut, screenWidth, screenHeight,
                DpiHelper.ScaleX, DpiHelper.ScaleY, _viewModel?.PanelItems.Count ?? 0),
            screenWidth);

        // Cover the primary screen
        Width = screenWidth;
        Height = screenHeight;
        Left = 0;
        Top = 0;

        // A non-layered window can present an unpainted first frame (black or white). DWM's cloak
        // hides that frame, so it must go up BEFORE the first visible frame — and after the HWND
        // exists, because a DWM write on a zero handle does nothing. A re-show of an already
        // visible window is NOT cloaked: it would blank a window that is on screen until the next
        // frame, and its content is already prepared.
        var alreadyVisible = IsVisible;
        if (!alreadyVisible)
        {
            _ = new WindowInteropHelper(this).EnsureHandle();
            Cloak();
        }

        Show();

        if (!alreadyVisible)
            UncloakAfterFirstRender();

        // After layout, push real tile positions into the VM (which creates the dwell targets).
        // Loaded priority runs before the Background callback that lifts the cloak, so the
        // targets exist by the time the first visible frame is composed.
        Dispatcher.BeginInvoke(UpdateTilePositions, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Cloaks the window through DWM (DWMWA_CLOAK) before it is shown. DWM keeps updating a cloaked
    /// window's surface, so the finished frame is composed while nothing is on screen; the cloak is
    /// lifted only after WPF has rendered (see <see cref="UncloakAfterFirstRender"/>).
    /// </summary>
    private void Cloak()
    {
        _cloaked = true;
        WriteCloak(true);
    }

    /// <summary>
    /// The ONE method through which every DWM attribute write goes. It applies the one-way
    /// stay-cloaked suppression by ATTRIBUTE VALUE (13), not by the caller's constant name: any
    /// uncloak request — a literal <c>13</c>, a renamed constant, or <see cref="WriteCloak"/> —
    /// is recorded and never sent while the seam is set. A cloak request is always sent.
    ///
    /// <para>
    /// Keeping the suppression here (rather than in the callers) means a future product or test
    /// path that writes the cloak attribute cannot bypass it by using another spelling. The source
    /// pin in <c>TaskSwitcherCloakTests</c> additionally proves the injectable delegate and the
    /// dwmapi P/Invoke are called only from this method, across every partial-class file.
    /// </para>
    /// </summary>
    private void WriteDwmAttribute(int attribute, bool value)
    {
        if (attribute == DwmwaCloak && !value && _stayCloakedForTests)
        {
            SuppressedUncloakRequestsForTests++;
            UncloakSuppressedForTests?.Invoke();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            _ = SetDwmAttribute(hwnd, attribute, value);
    }

    /// <summary>
    /// Cloaks or uncloaks through <see cref="WriteDwmAttribute"/>. Everything else — the show's
    /// cloak, the rendered-frame uncloak, the fallback and <see cref="HideOverlay"/> — goes through
    /// here, so the one-way test seam cannot be bypassed by a caller that forgot to check it.
    /// </summary>
    private void WriteCloak(bool cloaked) => WriteDwmAttribute(DwmwaCloak, cloaked);

    /// <summary>
    /// Test seam: when true, the rendered-frame trigger is not armed, so the 250 ms fallback is the
    /// only uncloak path. Set only on a window anchored by <see cref="KeepCloakedForTests"/> (it can
    /// only delay a test cloaked window's uncloak, never make one visible); inert in production.
    /// </summary>
    internal bool SkipRenderedFrameTriggerForTests { get; set; }

    /// <summary>
    /// Releases the cloak once WPF has rendered at least one frame: the static
    /// <c>CompositionTarget.Rendering</c> event fires before the UI-thread render commit, so the
    /// uncloak runs from a <see cref="DispatcherPriority.Background"/> callback (after the commit),
    /// and one <c>DwmFlush</c> presents the finished frame before the cloak lifts. A 250 ms
    /// fallback uncloaks anyway, so a window that never renders cannot stay cloaked.
    /// </summary>
    private void UncloakAfterFirstRender()
    {
        // Normal priority (NOT Send): the dispatcher does not dispatch Send-priority timer ticks, so
        // a Send fallback would never fire and a window that never rendered would stay cloaked.
        _cloakFallback?.Stop();
        _cloakFallback = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _cloakFallback.Tick += (_, _) => UncloakAndFlush();
        _cloakFallback.Start();

        if (SkipRenderedFrameTriggerForTests)
            return;

        RemoveUncloakHandler();
        _uncloakOnRendering = (_, _) =>
        {
            RemoveUncloakHandler();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UncloakAndFlush));
        };
        CompositionTarget.Rendering += _uncloakOnRendering;
    }

    /// <summary>
    /// Presents the finished frame and then lifts the cloak — in that order: the render thread
    /// presents asynchronously, so uncloaking first can compose the PRE-render surface (the
    /// unpainted first frame the cloak exists to hide). DWM keeps updating a cloaked window's
    /// surface, so the flush presents the finished frame while the window is still cloaked, and
    /// only then does the uncloak make it visible.
    /// </summary>
    private void UncloakAndFlush()
    {
        FlushDwm();
        Uncloak();
    }

    private void RemoveUncloakHandler()
    {
        if (_uncloakOnRendering is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _uncloakOnRendering = null;
        }
    }

    /// <summary>
    /// Cancels a pending uncloak (the Rendering subscription and the fallback timer) without
    /// touching the cloak bit — for <see cref="OnClosed"/>, where the handle is already gone so
    /// there is nothing left to uncloak, but a stray callback must not fire later.
    /// </summary>
    private void CancelPendingUncloak()
    {
        RemoveUncloakHandler();
        _cloakFallback?.Stop();
        _cloakFallback = null;
    }

    /// <summary>
    /// Releases the cloak, if it is still up, cancelling the pending uncloak first. Safe to call
    /// repeatedly; <see cref="HideOverlay"/> calls it so a hidden window can never stay cloaked.
    /// Every write goes through <see cref="WriteCloak"/>, so on a window prepared by the test
    /// fixture's <c>InvisibleTestWindow.PrepareCloaked</c> the request is recorded and suppressed
    /// and the fixture's cloak stays up.
    /// </summary>
    private void Uncloak()
    {
        CancelPendingUncloak();
        if (!_cloaked)
            return;
        _cloaked = false;
        WriteCloak(false);
    }

    private void DisableTransitionsAtHandleCreation(object? sender, EventArgs e)
    {
        WriteDwmAttribute(DwmwaTransitionsForceDisabled, true);
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterThumbnails();   // no DWM handle may outlive the window that owns it
        CancelPendingUncloak();   // no stray uncloak may fire on a closed window
        base.OnClosed(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    /// <summary>
    /// The DWM attribute writes (transitions disabled, cloak), injectable so the gates can record
    /// them. The default is the real dwmapi call.
    /// </summary>
    internal Func<IntPtr, int, bool, int> SetDwmAttribute { get; set; } = NativeSetDwmAttribute;

    /// <summary>
    /// <c>DwmFlush</c>, injectable so a gate can prove the ordering of the flush against the
    /// uncloak in ONE sequence. The default is the real dwmapi call.
    /// </summary>
    internal Action FlushDwm { get; set; } = NativeDwmFlush;

    private static void NativeDwmFlush() => _ = DwmFlush();

    private static int NativeSetDwmAttribute(IntPtr hwnd, int attribute, bool value)
    {
        var intValue = value ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, attribute, ref intValue, sizeof(int));
    }

    /// <summary>
    /// Resolves every WINDOW tile's icon through the seam, one call per window (S3-B: the icon is
    /// the window's, not its process's — every Store app shares <c>ApplicationFrameHost</c>). The
    /// "Show desktop" tile is not a window and keeps its own drawn glyph; <see cref="TaskSwitcherViewModel.Tiles"/>
    /// is the window-only list, so it is never asked for. Split out of <see cref="ShowFullScreen"/> so
    /// the wiring is testable without showing the window.
    /// </summary>
    internal void ResolveIcons()
    {
        if (_iconProvider == null || _viewModel == null)
            return;

        foreach (var tile in _viewModel.Tiles)
            tile.Icon = _iconProvider.GetIcon(tile.Hwnd, tile.ProcessId);
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
    /// For each panel tile, compute its real on-screen rectangle (the drawn tile face) and push its
    /// VISIBLE part — the intersection with the ScrollViewer's viewport — into the VM, so the
    /// DwellDetector hit-tests exactly what is drawn: not a circle that ignores the corners,
    /// and not the hidden remainder of a row the viewport clips. Tiles with no visible area
    /// get NO target: gaze resting on the bottom band (the hint row, the close disc) must
    /// never silently switch windows through a tile that is not on screen.
    /// <para>
    /// S3-A: the panel is the window tiles plus the session's desktop tile. The desktop tile
    /// registers through its own reserved-target call — same rectangle rule, but its id is not a
    /// window handle. It is always in a shown row (the VM trimmed the window list to make room),
    /// so it can never be the tile the viewport clips.
    /// </para>
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

        // Windows whose whole drawn face is inside the viewport, for the preview pass below. A
        // partially clipped tile keeps its clipped DWELL rect, but gets NO thumbnail: a DWM
        // thumbnail is composed above WPF and is not clipped by the ScrollViewer, so a picture on
        // a clipped face would paint over the hint row / close disc.
        var fullyVisibleWindows = new HashSet<IntPtr>();

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

            if (tile.IsDesktop)
                _viewModel.UpdateDesktopTilePosition(visibleRect);
            else
                _viewModel.UpdateTilePosition(tile.Hwnd, visibleRect);

            // S4-C: only a fully visible WINDOW tile can carry a picture, and a minimised window
            // never does (DWM has no live surface for it — its box keeps the icon + badge).
            if (!tile.IsDesktop && !tile.IsMinimized
                && visibleRect.Width >= faceRect.Width - 0.5
                && visibleRect.Height >= faceRect.Height - 0.5)
            {
                fullyVisibleWindows.Add(tile.Hwnd);
            }
        }

        // S4-C: live previews, registered in the SAME post-layout pass as the dwell targets and
        // BEFORE the close disc's call, which flushes the open line — so the pictures appear with
        // the first visible frame (`ShowFullScreen` lifts the cloak after this dispatcher callback)
        // and the log's previews=/previewFailures= describe that frame.
        var previews = RegisterTileThumbnails(this, fullyVisibleWindows);
        _viewModel.ReportPreviews(previews.Registered, previews.Failed);

        // Register the close disc as a dwell target too (same physical-pixel convention).
        var closeCenter = CloseButton.PointToScreen(
            new Point(CloseButton.ActualWidth / 2, CloseButton.ActualHeight / 2));
        _viewModel.UpdateCloseButtonPosition(new PhysicalPoint(closeCenter.X, closeCenter.Y));

        // Test hook: after every target above has been registered (the show-path gate asserts
        // this runs before the uncloak request). Inert in production.
        TilePositionsUpdatedForTests?.Invoke();
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

    /// <summary>
    /// S4-C: registers one live DWM thumbnail for every panel tile that may carry one, and returns
    /// the counts the session log records. Called from <see cref="UpdateTilePositions"/> — the same
    /// post-layout pass that registers the dwell targets — and callable headlessly with a detached
    /// content root, which is how the lifecycle tests drive it without an HWND or a real DWM call.
    ///
    /// <para>
    /// Only window tiles that are not minimised, carry a realised container and (in production) are
    /// wholly inside the viewport are attempted; the desktop tile, overflow-trimmed windows and
    /// minimised windows are skipped and are NOT failures. A registration that fails — a non-zero
    /// HRESULT, a zero source size, a window gone between enumeration and this pass — leaves that
    /// tile in its icon box and never throws.
    /// </para>
    ///
    /// <para>
    /// The pass starts by releasing every handle from the previous one, so a second pass (a re-show
    /// without a hide) replaces the pictures instead of stacking a second set.
    /// </para>
    /// </summary>
    /// <param name="windowRoot">
    /// The visual whose origin is the destination window's client-area origin. Production passes
    /// the window; a headless test passes the detached content root it measured.
    /// </param>
    /// <param name="visibleWindows">
    /// The hwnds whose whole drawn face is visible. <c>null</c> means "no viewport clamp" — the
    /// headless harness has already trimmed the panel to capacity, so every tile it holds is on
    /// screen.
    /// </param>
    internal (int Registered, int Failed) RegisterTileThumbnails(
        UIElement windowRoot, IReadOnlySet<IntPtr>? visibleWindows = null)
    {
        if (_thumbnailService is null || _viewModel is null)
            return (0, 0);

        UnregisterThumbnails();

        var registered = 0;
        var failed = 0;
        foreach (var item in TilesPanel.Items)
        {
            if (item is not TaskSwitcherTileViewModel tile || tile.IsDesktop || tile.IsMinimized)
                continue;
            if (visibleWindows is not null && !visibleWindows.Contains(tile.Hwnd))
                continue;
            if (TilesPanel.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                continue;

            try
            {
                if (TryRegisterTileThumbnail(tile, container, windowRoot))
                    registered++;
                else
                    failed++;
            }
            catch (Exception)
            {
                // A preview is decoration: it must never take the switcher — the owner's only
                // surface for switching a window — down with it.
                failed++;
            }
        }

        return (registered, failed);
    }

    /// <summary>
    /// One tile's registration: allocate the DWM thumbnail, read the source size (a zero size is a
    /// failure — the window is gone or not composited), fit the destination rect inside the
    /// tile's preview box, and show it. Every failure releases the handle it may have allocated.
    /// </summary>
    private bool TryRegisterTileThumbnail(
        TaskSwitcherTileViewModel tile, FrameworkElement container, UIElement windowRoot)
    {
        if (_thumbnailService is null)
            return false;

        var preview = FindDescendant<FrameworkElement>(container, "PreviewBox");
        if (preview is null || preview.ActualWidth <= 0 || preview.ActualHeight <= 0)
            return false;

        var handle = _thumbnailService.Register(
            new WindowInteropHelper(this).Handle, tile.Hwnd);
        if (handle == IntPtr.Zero)
            return false;

        if (!_thumbnailService.TryGetSourceSize(handle, out var sourceWidth, out var sourceHeight))
        {
            _thumbnailService.Unregister(handle);
            return false;
        }

        // The box's DIP position RELATIVE TO THE WINDOW (never the screen): rcDestination is in the
        // destination window's client coordinates, and the switcher covers the primary screen only
        // by coincidence — never depend on (0,0).
        var origin = preview.TranslatePoint(new Point(0, 0), windowRoot);
        var destination = ThumbnailGeometry.FitDestination(
            origin.X, origin.Y, preview.ActualWidth, preview.ActualHeight,
            DpiHelper.ScaleX, DpiHelper.ScaleY, sourceWidth, sourceHeight);

        if (!_thumbnailService.Update(handle, destination))
        {
            _thumbnailService.Unregister(handle);
            return false;
        }

        _tileThumbnails.Add((tile, handle));
        tile.HasPreview = true;
        return true;
    }

    /// <summary>
    /// Releases every live thumbnail of this show and puts the affected tiles back into icon mode.
    /// <see cref="HideOverlay"/> calls it before hiding, <see cref="OnClosed"/> on close, and
    /// <see cref="ShowFullScreen"/> / the registration pass at the start of a show — no handle may
    /// survive a hide, or a session re-opened fifty times grows DWM's set fifty times.
    /// </summary>
    private void UnregisterThumbnails()
    {
        foreach (var (tile, handle) in _tileThumbnails)
        {
            try
            {
                _thumbnailService?.Unregister(handle);
            }
            catch (Exception)
            {
                // A failing release must not keep the overlay on screen: HideOverlay calls this.
            }

            tile.HasPreview = false;
        }

        _tileThumbnails.Clear();
    }

    /// <summary>The first descendant of <paramref name="root"/> named <paramref name="name"/>.</summary>
    private static T? FindDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name)
            return match;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindDescendant<T>(VisualTreeHelper.GetChild(root, i), name) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>Called by the coordinator when the VM hides (selection or second toggle).</summary>
    public void HideOverlay()
    {
        // No live preview may survive a hide: the handles belong to THIS session's tiles, and the
        // next show registers its own set (after a new enumeration).
        UnregisterThumbnails();

        // A hidden window must never stay cloaked. On a test-prepared window the request is
        // recorded and suppressed at WriteCloak, so the fixture's cloak stays up for its whole life.
        Uncloak();
        Hide();
    }
}
