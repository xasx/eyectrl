namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>
/// Overlay window that renders the Quick Actions grid. Binds to
/// <see cref="QuickActionsGridViewModel"/>, positions itself on the configured screen edge,
/// and pushes each laid-out tile's real screen centre back into the VM so the DwellDetector
/// hit-tests against true coordinates. The close button is a dwell target (not a WPF Button —
/// gaze produces no mouse clicks). Mirrors the TaskSwitcherWindow layout-sync pattern.
/// </summary>
public partial class QuickActionsGridWindow : OverlayWindow, IQuickActionsGridOverlay
{
    /// <summary>
    /// The close button's diameter: <b>2 × the shared hit radius</b>, declared in XAML as
    /// Width/Height 100. B8: the disc must be drawn to exactly the size the dwell detector
    /// hit-tests (<see cref="UIConstants.ButtonRadius"/> = 50 DIP), so a gaze that lands on the
    /// visible circle always lands in its hit area — the drawing was 44 while the target was
    /// r50, and a near miss outside the small disc still counted.
    ///
    /// <c>internal</c> rather than <c>private</c> so <c>GlyphSizeCouplingTests</c> can assert the
    /// coupling instead of restating the number: nothing else ties this const to the XAML
    /// attribute, so the two could drift silently and the glyph would stop filling its button.
    /// </summary>
    internal const double CloseButtonSize = UIConstants.ButtonRadius * 2;

    /// <summary>
    /// The action tile's DRAWN edge length, declared in the ItemTemplate as Width/Height 100.
    /// B8b F2: the tile is hit-tested as a circle of <see cref="UIConstants.ButtonRadius"/> = 50
    /// DIP, so the drawn face is exactly that circle (a 100 DIP disc) — the old 120 DIP square left
    /// its corners and edge band outside the hit area; a gaze there did nothing, with no feedback.
    /// <c>internal</c> for the same reason as <see cref="CloseButtonSize"/>.
    /// </summary>
    internal const double TileSize = UIConstants.ButtonRadius * 2;

    /// <summary>
    /// One tile's cell pitch: the 100 DIP face plus the dead space that keeps the R4 gap. Two r50
    /// hit circles 128 DIP apart leave a 28 DIP gap — the 20 DIP minimum with margin — so the
    /// pitch, not the face, is what the window sizes columns from (B8b F2/F3).
    /// </summary>
    internal const double TileCell = 128;

    /// <summary>The outer grid margin around the close row and the tile band (12 DIP per side).</summary>
    internal const double OuterPad = 24;

    /// <summary>
    /// Tile icon size: a ratio of the tile that bounds it (spec §8.5). Public and static
    /// because it is consumed by {x:Static} from inside the ItemTemplate, which cannot reach an
    /// instance member. It is the ONLY glyph size that needs this — see RawGeometryLiteralTests's
    /// allow-list, which admits public static doubles on OverlayWindow subclasses for exactly
    /// this one case.
    /// </summary>
    public static double TileIconSize => TileSize * ContainerRatio.Small;

    private QuickActionsGridViewModel? _vm;

    /// <summary>
    /// The shared gaze disc drawn into <c>CloseLayer</c> — the same <see cref="DwellArcButton"/>
    /// every edge-bar exit uses (bold <see cref="ActionIcon.Close"/> at
    /// <see cref="DwellArcButton.IconSize.Solo"/>, a 2 DIP rim with its dark halo, and the rim-arc
    /// progress). It replaced a hand-made Border + text "✕" + straight bottom bar, so the grid's
    /// ✕ finally matches the rim arc its own tiles already draw.
    /// </summary>
    private DwellArcButton? _closeDisc;

    /// <summary>
    /// The view model whose <see cref="QuickActionsGridViewModel.CloseDwellProgress"/> and
    /// <see cref="QuickActionsGridViewModel.CloseDwellPaused"/> drive the disc. Kept separately from
    /// <see cref="_vm"/> (which only <see cref="Configure"/> sets) because a test may bind a
    /// DataContext directly, and the disc must follow exactly the VM it is showing.
    /// </summary>
    private QuickActionsGridViewModel? _closeViewModel;

    /// <summary>Creates the window; overlay interaction flags are applied once the HWND exists.</summary>
    public QuickActionsGridWindow()
    {
        InitializeComponent();

        // The close disc is the shared DwellArcButton, drawn at this host's centre so the drawn
        // disc IS the registered r50 hit circle. The 100 DIP host only reserves the header slot;
        // its Canvas exists so the disc's halo can reach past the host without clipping.
        CreateCloseDisc();

        // A VM bound after construction (Configure, or a test's DataContext) drives the disc's
        // progress and held look.
        DataContextChanged += OnDataContextChanged;

        // Deliberately no SetOverlayInteraction call here: it would be a no-op. That method
        // early-returns while the HWND is still IntPtr.Zero, and the HWND is not created until
        // Show(). The flags are applied by OverlayWindow.OnSourceInitialized, which runs after
        // HWND creation and passes (clickThrough: IsClickThrough — false here, since this window
        // never sets it — and focusable: false).
        //
        // The focusable:false half is load-bearing, not cosmetic: it sets WS_EX_NOACTIVATE, so
        // showing the grid never steals the foreground. Any quick action that acts on "the window
        // the user was just in" resolves the foreground window and must skip windows owned by
        // EyeCtrl itself; if this grid could activate, those actions would target the grid and
        // silently do nothing. Keep OnSourceInitialized as the single place that applies the flags.
    }

    /// <summary>
    /// The disc this window draws for its close target. Exposed to the harness so the anatomy
    /// (icon, rim, halo, arc) is read off the object the window painted, never restated.
    /// </summary>
    internal DwellArcButton? CloseDisc => _closeDisc;

    /// <summary>
    /// Builds the close disc into <c>CloseLayer</c>: the shared escape shape of every edge exit —
    /// <see cref="ActionIcon.Close"/> at <see cref="DwellArcButton.IconSize.Solo"/>, the 2 DIP rim
    /// plus its dark halo, and the rim arc. Created at the host's centre with the shared r50
    /// radius, so the drawn disc and the registered hit circle are the same geometry (B8). The UIA
    /// name is read once from the string table, exactly like the old
    /// <c>{loc:T 'overlays.edge.close'}</c> glyph binding (restart-to-apply language model).
    /// </summary>
    private void CreateCloseDisc()
    {
        CloseLayer.Children.Clear();
        var centre = new Point(UIConstants.ButtonRadius, UIConstants.ButtonRadius);
        _closeDisc = DwellArcButton.Create(
            centre,
            UIConstants.ButtonRadius,
            symbol: null,
            icon: ActionIcons.For(ActionIcon.Close),
            iconSize: DwellArcButton.IconSize.Solo,
            accessibleName: StringTable.Current.T("overlays.edge.close"));
        _closeDisc.AddTo(CloseLayer);
        ApplyCloseDiscState();
    }

    /// <summary>
    /// Attaches the close disc to whichever view model the DataContext now holds. Detaching the
    /// previous VM first keeps a re-bind (or a re-Configure) from double-subscribing, and makes a
    /// newly bound VM's current progress/paused state the disc's initial state.
    /// </summary>
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_closeViewModel is not null)
            _closeViewModel.PropertyChanged -= OnCloseViewModelPropertyChanged;

        _closeViewModel = e.NewValue as QuickActionsGridViewModel;

        if (_closeViewModel is not null)
            _closeViewModel.PropertyChanged += OnCloseViewModelPropertyChanged;

        ApplyCloseDiscState();
    }

    /// <summary>
    /// The ONLY route from the view model's close-dwell state to the drawn arc. A value change is
    /// re-read from the VM rather than taken from the event, so an out-of-order notification can
    /// never leave the arc describing an older state.
    /// </summary>
    private void OnCloseViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(QuickActionsGridViewModel.CloseDwellProgress)
            or nameof(QuickActionsGridViewModel.CloseDwellPaused))
        {
            ApplyCloseDiscState();
        }
    }

    /// <summary>
    /// Pushes the bound VM's close-dwell state onto the disc: 0 (no arc) when nothing is dwelt,
    /// and the held look whenever the VM says the dwell is paused.
    /// </summary>
    private void ApplyCloseDiscState()
    {
        _closeDisc?.UpdateProgress(_closeViewModel?.CloseDwellProgress ?? 0);
        _closeDisc?.SetPaused(_closeViewModel?.CloseDwellPaused ?? false);
    }

    /// <summary>
    /// The disc follows the session's VM only while this window is alive. (The window itself is a
    /// DI singleton, but the close-disc subscription must not outlive it either.)
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        if (_closeViewModel is not null)
        {
            _closeViewModel.PropertyChanged -= OnCloseViewModelPropertyChanged;
            _closeViewModel = null;
        }

        base.OnClosed(e);
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
    /// Positions the grid window on the specified screen edge, centered on that edge.
    /// Call after <see cref="ResizeToContent"/> and before Show(). Unknown edge values fall back to top.
    /// </summary>
    /// <remarks>
    /// B8b F4: the COMPUTED <see cref="FrameworkElement.Width"/>/<see cref="FrameworkElement.Height"/>
    /// are used, never <c>ActualWidth</c>/<c>ActualHeight</c>. A hidden window gets no layout pass,
    /// so on a re-show after the tile or column count grew those two still hold the PREVIOUS show's
    /// size and a Right/Bottom grid would overhang the work area. <see cref="ResizeToContent"/>
    /// always sets Width/Height immediately before this call — they are the fresh truth.
    /// </remarks>
    /// <param name="edge">One of "Left", "Right", "Top", "Bottom". Unknown values fall back to top.</param>
    public void PositionOnEdge(string edge)
    {
        var workArea = SystemParameters.WorkArea; // logical DIPs
        (Left, Top) = OverlayPositioning.PlaceAtEdge(workArea, edge, Width, Height);
    }

    /// <summary>
    /// Sizes the window to fit every tile — columns from the VM, rows computed — plus the
    /// close-button row, capped so the whole grid always fits the work area. Gaze users can't
    /// scroll, so all tiles must be visible; the previous fixed 400×600 clipped 3+ column grids
    /// and pushed extra rows off-window.
    ///
    /// <para>
    /// B8b F3: past the work-area clamp the UniformGrid used to squeeze its rows, so the fixed
    /// 120 DIP tiles overlapped and their r50 hit circles drifted together — from about ten rows
    /// the R4 dead gap was gone. The height is no longer allowed to squeeze: the tile count is
    /// capped to the rows that fit at the full <see cref="TileCell"/> pitch, and the excess tiles
    /// are dropped (the grid has no scrolling, so the alternative is unreachable tiles under a
    /// squeezing layout).
    /// </para>
    /// <para>
    /// The window's own chrome (outer margin + the theme's border) is part of the size so the
    /// INNER content is exactly <c>cols × TileCell</c>. Without it the pitch shrank by
    /// <c>2·border/cols</c> — 127.3 DIP at the default 1 DIP border and 126.7 at Contrast's 2 DIP
    /// border — and the drawn tile face no longer filled its cell exactly.
    /// </para>
    /// <c>internal</c> so the headless geometry pin can lay the production content out at the
    /// exact size the show path uses, without showing a window (B8).
    /// </summary>
    internal void ResizeToContent()
    {
        if (_vm == null) return;

        var border = RootBorder.BorderThickness;
        double chromeW = OuterPad + border.Left + border.Right;
        double chromeH = ChromeHeight(border);

        int cols = Math.Max(1, _vm.GridColumns);

        var wa = SystemParameters.WorkArea; // logical DIPs
        int maxRows = MaxRowsFor(wa.Height, border);

        // R4 (fixup-01): the cap drops the tail of the user's configured actions — silently it
        // would read as "my action is missing". One line, only when it actually bites.
        var configured = _vm.Tiles.Count;
        _vm.CapVisibleTiles(cols * maxRows);
        if (_vm.Tiles.Count < configured)
        {
            Log.Information(
                "Quick Actions: the work area fits {Shown} of {Configured} enabled actions at "
                + "{Columns} columns x {Cell} DIP rows; the grid was capped to {Rows} row(s).",
                _vm.Tiles.Count, configured, cols, TileCell, maxRows);
        }

        int rows = _vm.Tiles.Count == 0 ? 0 : (int)Math.Ceiling((double)_vm.Tiles.Count / cols);

        Width = Math.Min(cols * TileCell + chromeW, wa.Width);
        Height = Math.Min(rows * TileCell + chromeH, wa.Height);
    }

    /// <summary>
    /// The window chrome's height in DIP: the close-button row (the disc plus the spacing above the
    /// tile band) plus the outer grid margin and the theme's border. <c>internal</c> so the
    /// max-count pin can derive the cap from the same expression the sizing uses, rather than
    /// restating 24 and 8.
    /// </summary>
    internal static double ChromeHeight(Thickness border) =>
        CloseButtonSize + 8 + OuterPad + border.Top + border.Bottom;

    /// <summary>
    /// How many tile rows the work area can hold at the full <see cref="TileCell"/> pitch — the
    /// cap's row count. Zero when not even one row fits (the grid then shows only Close).
    /// </summary>
    internal static int MaxRowsFor(double workAreaHeight, Thickness border) =>
        Math.Max(0, (int)Math.Floor((workAreaHeight - ChromeHeight(border)) / TileCell));

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
