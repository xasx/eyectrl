namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using SelectionMode = EyeCtrl.Core.Models.SelectionMode;

/// <summary>
/// The toolbar window: where the strip sits on screen, what chrome sits beside the buttons, which
/// view-model drives them, and where each button's dwell target lands.
///
/// <para>
/// The buttons themselves — construction, layout, and every pixel of their drawn state — belong to
/// <see cref="ToolbarBand"/>. This class decides WHEN the band changes; the band decides HOW it
/// looks.
/// </para>
/// </summary>
public partial class ToolbarWindow : OverlayWindow, IToolbarOverlay
{
    private readonly ToolbarBand _band;

    private double _buttonSize;

    // Band membership for Reset, set by Initialize and consumed by RebuildBand. Stored because
    // SetMode's axis flip rebuilds the band WITHOUT going through Initialize — if the flag were
    // an Initialize-local, every dock change would rebuild at whatever default RebuildBand
    // picked, silently flipping membership along with the axis (P17). Initialize itself always
    // re-reads its own argument: both production callers pass the current
    // AdaptationSettings.Enabled.
    private bool _includeReset;

    // Written on the UI thread (UpdateTargetPositions / RebuildBand / SetMode), read on the gaze
    // thread through TargetsInitialized. Volatile for the reason ModalInteractionState's flags are:
    // a one-frame-stale read costs at most one extra dispatch.
    private volatile bool _targetsInitialized;
    private bool _vertical;
    private GazeProviderType? _providerType;

    // ── Slide transition ──
    // docs/superpowers/specs/2026-09-06-toolbar-slide-transition-design.md

    /// <summary>How long one slide takes. Matches TargetHighlightWindow's 200 ms fades.</summary>
    private const int SlideDurationMs = 200;

    /// <summary>
    /// The docked edge the band retracts into, lower-cased, or null when floating. Deliberately
    /// NOT derived from <see cref="_vertical"/>: SetMode normalises an undocked toolbar's edge to
    /// "top" for the strip's AXIS, which is the right answer there and the wrong one for a slide —
    /// a floating toolbar has no edge to retract into.
    /// </summary>
    private string? _slideEdge;

    /// <summary>
    /// Bumped on every slide start and every cancellation, so a superseded slide's completion is
    /// a no-op. Insurance rather than necessity — Timeline.Completed documents that stopping a
    /// timeline does not raise it — but the asymmetry of being wrong is a Hide() landing on a
    /// just-force-shown toolbar, which strands a gaze-only user with no controls.
    /// </summary>
    private int _slideGeneration;
    private Action? _pendingSlideCompletion;
    private int _pendingSlideGeneration;
    private bool _sliding;
    private bool _slidingOut;

    /// <summary>Whether the transform is in motion, in either direction.</summary>
    public bool IsSliding => _sliding;

    /// <summary>
    /// Whether the band is on its way to being hidden. <c>internal</c>, not <c>public</c>: no
    /// production code reads it — <see cref="IsOnScreen"/> is the surface callers are meant to use,
    /// and it reads <see cref="_slidingOut"/> directly. Narrowed deliberately, because a caller
    /// gating dwell arming on this property instead would bypass <see cref="IsOnScreen"/>'s
    /// <c>IsVisible</c> conjunct — exactly the distinction the two-flag design exists to enforce.
    /// <see cref="IsSliding"/> stays public: it has two production readers in
    /// <c>ToolbarVisibilityAdapter</c>'s transition logging.
    /// </summary>
    internal bool IsSlidingOut => _slidingOut;

    /// <summary>
    /// Visible and not on its way out — i.e. arming dwell against the published target coordinates
    /// is safe. TRUE during a slide-IN (dwell arms at its start by design), false during a
    /// slide-out, which ends in Hide().
    ///
    /// <para>
    /// <c>!IsSlidingOut</c>, not <c>!IsSliding</c>, and the difference is load-bearing. Because
    /// arming happens at the start of a slide-in, the slide-in's completion no longer repairs dwell
    /// state that something else cleared mid-slide. A gate that reported false during a slide-in
    /// would let a task-switcher open-then-close inside those 200 ms leave the toolbar visible, at
    /// rest, and dwellable by nothing.
    /// </para>
    /// </summary>
    public bool IsOnScreen => IsVisible && !_slidingOut;

    /// <summary>The toolbar's on-screen thickness in DIP — the extent the keyboard yields by at
    /// a shared edge (see <c>KeyboardCoordinator.ToolbarOccupancyFor</c>).</summary>
    public double ExtentDip => ActualHeight;

    /// <summary>Whether the docked edge slides along Y (top/bottom) rather than X (left/right).</summary>
    private bool SlideAxisIsY => _slideEdge is "top" or "bottom";

    /// <summary>
    /// The offset that puts the band fully clear of the window, or a zero vector when there is
    /// nothing to slide — floating, unrecognised edge, or a window never measured.
    /// </summary>
    private Vector SlideOffset() =>
        OverlayPositioning.SlideVectorFor(_slideEdge, ActualWidth, ActualHeight);

    /// <summary>
    /// The per-button dwell-progress/icon/highlight subscriptions this window owns, so they can
    /// be detached before the next rebind. Mirrors <c>EdgeButtonBarWindow._progressSubs</c> and
    /// <c>ActionMenuWindow._itemProgressSubs</c>, and exists for the same reason: a lambda you did
    /// not keep cannot be unsubscribed, and RebuildBand's rebind (via BindToViewModel) runs on
    /// every dock/axis/size change, not just once at startup.
    /// </summary>
    private readonly List<(ToolbarButtonViewModel Vm, PropertyChangedEventHandler Handler)> _buttonSubs = new();

    /// <summary>
    /// The view-model currently subscribed to <see cref="ToolbarViewModel.ButtonActivated"/>, or
    /// null if none. Tracked (rather than a one-shot bool) so a DataContext reassignment detaches
    /// the old view-model's handler instead of leaking it and leaving the new view-model unheard.
    /// </summary>
    private ToolbarViewModel? _activationHookedVm;

    /// <summary>
    /// The view-model whose <c>PropertyChanged</c> this window currently listens to for
    /// <see cref="ToolbarViewModel.ResetFrozenSilent"/>, or null if none. Same tracked-reference
    /// pattern as <see cref="_activationHookedVm"/> and for the same reason: this window has no
    /// Closed-time teardown of its view-model subscriptions — the rebind in
    /// <see cref="BindToViewModel"/> IS the teardown path (it runs on every rebuild), so a
    /// DataContext reassignment must detach the old handler there rather than leak it and leave
    /// the new view-model's cue unheard.
    /// </summary>
    private ToolbarViewModel? _frozenSilentHookedVm;

    /// <summary>The button band, for tests. Layout must be measured against this, not the Window.</summary>
    internal Canvas ButtonCanvasForTest => ButtonCanvas;

    public ToolbarWindow()
    {
        InitializeComponent();
        _band = new ToolbarBand(ButtonCanvas);
    }

    /// <summary>
    /// Captures the button metrics and builds the band. <paramref name="dwellTimeMs"/> is the
    /// ViewModel's business and is accepted only to keep this window's callers unchanged.
    /// </summary>
    /// <param name="buttonSize">Button diameter in DIP.</param>
    /// <param name="dwellTimeMs">Unused here; the ViewModel owns dwell timing.</param>
    /// <param name="includeReset">Whether the band carries the Reset button — the caller's
    /// <c>AdaptationSettings.Enabled</c> (Task 7, D5/M5). Stored, not just used once, so the
    /// axis-flip rebuild in <see cref="SetMode"/> keeps the membership the band was built with
    /// (P17).</param>
    public void Initialize(double buttonSize, int dwellTimeMs, bool includeReset)
    {
        _buttonSize = buttonSize;
        _includeReset = includeReset;
        RebuildBand();
    }

    /// <summary>
    /// Rebuilds the band from the captured metrics. Split out of <see cref="Initialize"/> so the
    /// axis flip in <see cref="SetMode"/> can re-lay the band without inventing a
    /// <c>dwellTimeMs</c> argument to hand to a parameter nothing reads.
    /// </summary>
    private void RebuildBand()
    {
        // No IndicatorPanel.Children.Clear() here: AddIndicatorElement below clears it itself
        // before adding, and when _providerType is null nothing has ever added to the panel in
        // the first place (SetProviderIndicator is IndicatorPanel's only other writer, and it
        // sets _providerType before calling AddIndicatorElement). A clear here would be provably
        // dead, not just redundant.
        _targetsInitialized = false;

        // The stored flag, not a parameter: this is the path SetMode's axis flip takes, and it
        // must rebuild at the membership the band already had (see _includeReset's doc).
        _band.Build(_buttonSize, _vertical, _includeReset);

        if (_providerType.HasValue)
            AddIndicatorElement(_providerType.Value);

        // Every rebuild drops the drawn state along with the old elements. Rebind so the new
        // buttons start from live view-model state rather than constructor defaults: for an
        // unchanged mode or pause state no PropertyChanged follows to repair it (IconKey is
        // [ObservableProperty], so re-assigning the same value raises nothing; SetPausedState
        // only fires on an IsPaused change). Both RebuildBand callers hit this — the axis flip in
        // SetMode below, and a plain size change via Initialize (SettingsUiCoordinator.cs:179),
        // which previously left the rebuilt band showing constructor defaults indefinitely.
        BindToViewModel();

        // The band→VM seam (Task 8, P7/D11): report what the band now renders. This is the only
        // route by which the view-model learns membership, so a rebuild that DROPS Reset (the
        // adaptation-Enabled flip through Initialize) also removes its dwell target, zeroes a
        // held arc and closes any live preview engagement — without it, a stale target at a
        // former screen position could fire the one unrecoverable action from gaze merely
        // passing there. AFTER BindToViewModel, and the order is safe in both directions because
        // the two never contend: SetRenderedButtons writes only the ABSENT ids' view-model
        // buttons, and the PropertyChanged it thereby raises for an absent id hits BindButton's
        // band-Contains guard and paints nothing. A null or non-ToolbarViewModel DataContext
        // (construction order varies across callers) simply skips — nothing is positioned yet,
        // so there is nothing to remove.
        if (DataContext is ToolbarViewModel vm)
            vm.SetRenderedButtons(_band.Buttons.Select(b => b.Key).ToArray());
    }

    /// <summary>
    /// Draws the gaze-provider indicator: a small glyph (eye/mouse) plus a hairline rule, sized
    /// and coloured as chrome rather than as a sixth button — it carries no fill, no rim and no
    /// dwell target, so it cannot be mistaken for a broken button the way the old full-size
    /// <c>Border</c>+emoji <c>TextBlock</c> could. That is also why it lives here rather than in
    /// <see cref="ToolbarBand"/>: it renders beside the band but is deliberately not part of it.
    ///
    /// <para>
    /// Clears <see cref="IndicatorPanel"/> before adding, which is what makes a second
    /// <see cref="SetProviderIndicator"/> call on a live window safe instead of doubling the
    /// indicator. <c>OverlayManager.cs:107-108</c> does not call it twice today — it calls
    /// <see cref="Initialize"/> once and <see cref="SetProviderIndicator"/> once, in that order —
    /// so nothing exercises this guard on the current caller, but it is one line away (e.g. a
    /// provider hot-swap) and there would be nothing else stopping
    /// <see cref="IndicatorPanel"/> from accumulating a second icon+rule pair if that line were
    /// added. Clearing unconditionally, rather than checking whether one already exists, also
    /// makes an axis flip (<see cref="SetMode"/>, via <see cref="RebuildBand"/>) free to rebuild
    /// the indicator in its new orientation without a separate "is this a rebuild or a fresh add"
    /// branch.
    /// </para>
    ///
    /// <para>
    /// Only <see cref="IconGeometry.Stroke"/> is drawn, not <see cref="IconGeometry.Fill"/>:
    /// <see cref="ToolbarIconKey.ProviderEye"/> and <see cref="ToolbarIconKey.ProviderMouse"/> are
    /// both stroke-only today (<c>ToolbarIcons.cs</c>), and <see cref="IndicatorPanel"/> is a
    /// <c>StackPanel</c>, which arranges children SIDE BY SIDE rather than stacking them — unlike
    /// <c>DwellArcButton</c>'s <c>Canvas</c>, a second layer added as a second panel child would
    /// not overlay the first, it would sit visibly beside it. If either geometry ever gains a
    /// <c>Fill</c> layer, drawing it here needs a small overlay container (e.g. a <c>Grid</c>)
    /// around both layers, not a second top-level child of this panel.
    /// </para>
    /// </summary>
    private void AddIndicatorElement(GazeProviderType providerType)
    {
        // Idempotent — see this method's doc comment above for what that guards against.
        IndicatorPanel.Children.Clear();

        // GazeProviderType also has Auto (0), which falls through to the eye here — but it never
        // actually reaches this switch: App.xaml.cs:160 calls
        // GazeProviderFactory.GetProviderType(gazeProvider) on the already-constructed provider
        // INSTANCE before handing the result to OverlayManager.Initialize (-> SetProviderIndicator
        // at OverlayManager.cs:108), and that lookup only ever returns StreamEngine or Mouse
        // (GazeProviderFactory.cs:50-56) — never Auto or the obsolete InteractionLib. The Auto
        // arm here is unreachable in production; it exists so this switch is still total if a
        // future caller ever passes the raw settings value instead.
        var key = providerType switch
        {
#pragma warning disable CS0618 // Type or member is obsolete
            GazeProviderType.InteractionLib => ToolbarIconKey.ProviderEye,
#pragma warning restore CS0618 // Type or member is obsolete
            GazeProviderType.Mouse => ToolbarIconKey.ProviderMouse,
            _ => ToolbarIconKey.ProviderEye,
        };

        var size = _buttonSize * ContainerRatio.Small;
        var geometry = ToolbarIcons.For(key);
        var scale = size / ActionIcons.GridSize;

        var icon = new Path
        {
            Data = geometry.Stroke,
            Stroke = Tokens.BrushTextDim,
            StrokeThickness = ActionIcons.GridStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            // Scale on RenderTransform, not Geometry.Transform — same reasoning as
            // DwellArcButton.Create's BuildLayer (see DwellArcButton.cs:267-269): RenderTransform
            // scales the stroke with the path, so GridStrokeThickness stays constant at every size.
            RenderTransform = new ScaleTransform(scale, scale),
            Width = size,
            Height = size,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            // The panel's own leading gap, now that IndicatorPanel carries no Margin of its own
            // (an empty StackPanel's content measures to zero, but its Margin would not, which is
            // what made the strip asymmetric with no provider set — see the XAML comment).
            Margin = _vertical ? new Thickness(0, ToolbarBand.ButtonMargin, 0, 0)
                               : new Thickness(ToolbarBand.ButtonMargin, 0, 0, 0),
        };

        // BrushBorderSubtle, not BrushSurfaceMedium — same reason as ToolbarBand.AddSeparator's
        // Line, see that method's doc comment for the contrast figures; the two moved together and
        // must stay together, since at different weights in one 896 px strip they would read as a
        // bug. That they now live in two files is exactly why this note names the other one.
        var rule = new Border
        {
            Width = _vertical ? _buttonSize * ContainerRatio.Dominant : Tokens.BorderThicknessStandard,
            Height = _vertical ? Tokens.BorderThicknessStandard : _buttonSize * ContainerRatio.Dominant,
            Background = Tokens.BrushBorderSubtle,
            IsHitTestVisible = false,
            Margin = _vertical ? new Thickness(0, ToolbarBand.ButtonMargin, 0, 0)
                               : new Thickness(ToolbarBand.ButtonMargin, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        IndicatorPanel.Children.Add(icon);
        IndicatorPanel.Children.Add(rule);
    }

    public void SetProviderIndicator(GazeProviderType providerType)
    {
        _providerType = providerType;
        AddIndicatorElement(providerType);
    }

    public void SetMode(bool isDocked, string edge)
    {
        // Null-tolerant, mirroring PlaceAtEdge's own `edge?.ToLowerInvariant()`. Undocked always
        // reads as "top", so an undock from a side edge returns the strip to horizontal.
        var key = isDocked ? edge?.ToLowerInvariant() ?? "top" : "top";
        var wantVertical = key is "left" or "right";

        // Null when floating: `key` is "top" there for the strip's axis, which must not be read
        // as an edge to retract into.
        _slideEdge = isDocked ? key : null;

        // Assigned in BOTH directions, and before UpdateLayout below. The old code set
        // Orientation only in the left/right cases and never reset it, so a left-dock followed by
        // an undock or a top-dock left the strip vertical until restart; and PlaceAtEdge centres
        // on ActualWidth/ActualHeight, which flip when the axis does, so a late assignment would
        // position the toolbar using the previous orientation's extents.
        if (wantVertical != _vertical)
        {
            _vertical = wantVertical;
            StripWrapper.Orientation = IndicatorPanel.Orientation =
                _vertical ? Orientation.Vertical : Orientation.Horizontal;
            // RebuildBand rebinds itself now (see its trailing comment), so no explicit
            // BindToViewModel call is needed here.
            RebuildBand();
        }

        UpdateLayout();   // SizeToContent does not re-measure until the next pass

        if (!isDocked) { _targetsInitialized = false; return; }

        var (left, top) = OverlayPositioning.PlaceAtEdge(
            SystemParameters.WorkArea, edge, ActualWidth, ActualHeight);
        Left = left;
        Top = top;

        _targetsInitialized = false;
    }

    public void UpdateGaze(PhysicalPoint screenPoint, long timestampMicroseconds)
    {
        if (!_targetsInitialized)
            UpdateTargetPositions();
    }

    /// <summary>
    /// Whether every button face has been laid out. Delegates to <see cref="ToolbarBand"/>, which
    /// owns the faces and documents why publishing a centre before this is true left no toolbar
    /// button dwellable at all.
    /// </summary>
    internal bool FacesAreArranged() => _band.FacesAreArranged();

    /// <summary>
    /// Whether every button's dwell target has been placed at its true on-screen centre. The gaze
    /// route reads this to decide whether it still needs to deliver samples here — it has to latch
    /// on placement having SUCCEEDED, not on one delivery having happened.
    /// </summary>
    public bool TargetsInitialized => _targetsInitialized;

    private void UpdateTargetPositions()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        // Not while the transform is displacing the faces. PointToScreen includes the render
        // transform, and UpdateButtonScreenPosition CREATES a target on its first call, so one bad
        // publish is permanent — the same permanence that made the collapsed-centre bug
        // unrecoverable short of a restart.
        //
        // Checked against the transform directly rather than `IsVisible && !IsSliding`: IsVisible
        // is only a PROXY for "the band is displaced", and a proxy can misread a
        // visible-but-displaced band — the transform's value IS the displacement, with no
        // inference in between. The transform form also covers the whole hidden period, not just
        // while a clock is live: the base value sits parked at the hidden offset for the entire
        // time the window is hidden, which an IsVisible-gated guard would miss.
        //
        // On a fresh, never-shown window this guard does not even fire — _sliding is false and
        // both axes read 0 — so the failure an IsVisible conjunct would have guarded against here
        // could not arise in that state regardless. (An earlier version of this comment argued for
        // the IsVisible conjunct's ABSENCE by claiming its presence would silently turn
        // ToolbarTargetPlacementTests.FreshBand_IsNotArranged_SoNothingIsPublished into a
        // tautology. That claim is false: that test asserts only TargetsInitialized == false
        // (ToolbarTargetPlacementTests.cs:31-44), and this method swallows PointToScreen's throw in
        // a per-button `catch { return; }` below, so _targetsInitialized never latches regardless
        // of which guard returned — the test has no teeth to lose either way.)
        if (_sliding || SlideTransform.X != 0 || SlideTransform.Y != 0)
            return;

        // Asked BEFORE anything is published, not after. The old order computed and published
        // every centre and only then asked whether the faces were laid out — too late, because the
        // targets it had already created at the bogus centres were the damage.
        if (!FacesAreArranged())
            return;

        foreach (var (id, button) in _band.Buttons)
        {
            try
            {
                var face = button.Background;
                var pos = face.PointToScreen(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
                vm.UpdateButtonScreenPosition(id, new PhysicalPoint(pos.X, pos.Y));
            }
            catch
            {
                return;   // not laid out yet
            }
        }

        _targetsInitialized = true;
    }

    // The pause state changes the ICON only (Pause ↔ Play). No pause-state-dependent element may
    // live in the band's flow: the dwell targets are published once and latched (see
    // ToolbarBand.FacesAreArranged and ToolbarLifecycleCoordinator's _toolbarInitialized), so a
    // re-flow would strand every button's target at its old centre. Reviewer B measured the gap a
    // Collapsed↔Visible resume label produced at 162 px, against a 60 px hit radius: the visible
    // Play button went dead. Pinned by ToolbarPausedTargetAlignmentTests at scale 2.0 on all four
    // dock edges.
    public void SetPausedState(bool isPaused) =>
        _band.SwapIcon("Pause", isPaused ? "Play" : "Pause");

    /// <inheritdoc />
    /// <remarks>
    /// Moved here from <c>ToolbarLifecycleCoordinator.CaptureToolbarRect</c>, which read this
    /// window's Left/Top/ActualWidth/ActualHeight and converted them itself. Those four reads were
    /// the only reason the coordinator needed the concrete window type, so exposing the answer
    /// instead of the inputs is what let <see cref="IToolbarOverlay"/> stay free of WPF. The
    /// conversion also belongs next to the layout values it converts.
    /// </remarks>
    public PhysicalRect GetPhysicalBounds() => new(
        Core.Services.DpiHelper.LogicalToPhysicalX(Left),
        Core.Services.DpiHelper.LogicalToPhysicalY(Top),
        Core.Services.DpiHelper.LogicalToPhysicalX(ActualWidth),
        Core.Services.DpiHelper.LogicalToPhysicalY(ActualHeight));

    /// <summary>Applies the selection-mode tint. See <see cref="ToolbarBand.SetSelectionMode"/>.</summary>
    public void SetSelectionModeState(SelectionMode mode) => _band.SetSelectionMode(mode);

    /// <summary>
    /// Subscribes each button view-model to its visuals. Safe to call repeatedly, which
    /// RebuildBand needs: every dock/axis/size change rebuilds the whole band and then rebinds.
    ///
    /// Dropping the old subscriptions is NOT about dead visuals — the handler resolves the band's
    /// buttons at event time, so it keeps working across a rebuild. It is about not
    /// double-subscribing once this method can be called more than once. The former
    /// _boundIds guard prevented that by making every call after the first a no-op, which
    /// also silently skipped the state re-application below.
    /// </summary>
    public void BindToViewModel()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        foreach (var (target, handler) in _buttonSubs)
            target.PropertyChanged -= handler;
        _buttonSubs.Clear();

        foreach (var btn in vm.Buttons)
            BindButton(btn);

        if (!ReferenceEquals(_activationHookedVm, vm))
        {
            if (_activationHookedVm is not null)
                _activationHookedVm.ButtonActivated -= FlashActivated;
            vm.ButtonActivated += FlashActivated;
            _activationHookedVm = vm;
        }

        // The D18 rim cue's subscription (Task 8): the engine's FrozenSilentChanged arrives on the
        // view-model as the ResetFrozenSilent observable, and this is the hand that carries it to
        // the band. Same tracked-reference swap as the activation hook above.
        if (!ReferenceEquals(_frozenSilentHookedVm, vm))
        {
            if (_frozenSilentHookedVm is not null)
                _frozenSilentHookedVm.PropertyChanged -= OnToolbarVmPropertyChanged;
            vm.PropertyChanged += OnToolbarVmPropertyChanged;
            _frozenSilentHookedVm = vm;
        }

        // Re-apply the CURRENT cue value, the same "unchanged value repairs nothing" gap
        // BindButton's initial-apply block covers for icon/mode-tint/highlight: a rebuild drops
        // the drawn rim along with the old elements, and an unchanged ResetFrozenSilent raises no
        // PropertyChanged to repair it. AddButton's PaintFace already re-applies the cue to the
        // fresh Ellipse (the funnel consults _frozenSilentIds, which Build does not clear), so
        // this line is defensive parity with that block rather than the load-bearing path — kept
        // so the subscription and the drawn state converge here in one place, whatever the band's
        // internals do.
        _band.SetFrozenSilent(ToolbarBand.ResetId, vm.ResetFrozenSilent);
    }

    /// <summary>
    /// Maps the view-model's frozen-silent observable to the band's D18 cue. Named, not a lambda,
    /// so the unsubscription in <see cref="BindToViewModel"/> pairs with it by target plus method.
    /// </summary>
    private void OnToolbarVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolbarViewModel.ResetFrozenSilent)) return;
        if (sender is not ToolbarViewModel vm) return;

        _band.SetFrozenSilent(ToolbarBand.ResetId, vm.ResetFrozenSilent);
    }

    /// <summary>
    /// Subscribes one button's PropertyChanged to its drawn visuals. The handler resolves the
    /// button from the band at event time, so it stays correct after Initialize rebuilds the band
    /// (size/dock/edge saves).
    /// </summary>
    private void BindButton(ToolbarButtonViewModel btn)
    {
        var id = btn.Id;

        // Apply the current icon so the initial face matches the actual mode (the ViewModel may
        // already have received the startup SelectionModeChangedMessage before BindToViewModel
        // is called). This block re-running is what fixes a rebuilt band (SetMode's axis flip,
        // or a plain size change) showing constructor-default icons/tint instead of live state.
        if (_band.Contains(id))
        {
            // SwapIcon rebuilds the button (DwellArcButton.Create has no icon setter), which
            // hands back fresh Halo/ProgressPath elements defaulted to Visibility.Visible — WPF's
            // default, not a collapsed rest state. UpdateProgress must run AFTER SwapIcon so it
            // collapses the arc the rebuild just (re)exposed, rather than being clobbered by it.
            _band.SwapIcon(id, btn.IconKey);
            if (id == ToolbarBand.SelectionModeId)
                _band.SetSelectionMode(SelectionModeIcons.ToMode(btn.IconKey));

            _band.UpdateProgress(id, btn.DwellProgress);
            _band.SetPaused(id, btn.IsDwellPaused);
            // IsHighlighted is a third property with the same "unchanged value repairs nothing"
            // gap this block's leading comment already names for icon/mode-tint — a rebuild while
            // gaze is still on a button (the toolbar keeps feeding gaze while the settings window
            // is open, so a settings-triggered rebuild is reachable mid-highlight) must not drop
            // the acquired state until the dwell happens to end or activate. Unlike the two calls
            // above, this one is order-independent: the band's PaintFace recomputes the face from
            // its acquired/mode state each time either is touched, so it converges to the same
            // result whichever of SetSelectionMode/SetAcquired runs last.
            _band.SetAcquired(id, btn.IsHighlighted);
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (!_band.Contains(id))
                return;

            if (e.PropertyName == nameof(ToolbarButtonViewModel.DwellProgress))
            {
                _band.UpdateProgress(id, btn.DwellProgress);
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IsDwellPaused))
            {
                _band.SetPaused(id, btn.IsDwellPaused);
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IsHighlighted))
            {
                _band.SetAcquired(id, btn.IsHighlighted);
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IconKey))
            {
                // SwapIcon's rebuild (RemoveButton + AddButton) repaints via PaintFace inside
                // AddButton, so an acquired disc's fill/rim survives this branch without a
                // separate SetAcquired call here. UpdateProgress still needs reapplying for the
                // same reason the initial-apply block above does: the rebuild hands back fresh
                // Halo/ProgressPath elements defaulted to Visible.
                _band.SwapIcon(id, btn.IconKey);
                if (id == ToolbarBand.SelectionModeId)
                    _band.SetSelectionMode(SelectionModeIcons.ToMode(btn.IconKey));
                _band.UpdateProgress(id, btn.DwellProgress);
                _band.SetPaused(id, btn.IsDwellPaused);
            }
        };

        btn.PropertyChanged += handler;
        _buttonSubs.Add((btn, handler));
    }

    /// <summary>
    /// The band's activation-flash entry point, kept as a named method rather than passing
    /// <c>_band.Flash</c> as a method group: <see cref="BindToViewModel"/> both subscribes and
    /// unsubscribes this, and delegate removal matches on target plus method, so a stable target
    /// (<c>this</c>) keeps that pairing obviously correct.
    /// </summary>
    private void FlashActivated(string id) => _band.Flash(id);

    /// <summary>For tests: the DwellArcButton behind one id.</summary>
    internal DwellArcButton ButtonForTest(string id) => _band[id];

    /// <summary>
    /// Ends one button's activation flash. Internal because it is the seam those tests drive
    /// directly — a window built in a test never calls <c>Show()</c> and runs on an STA thread
    /// with no message pump, so the band's flash timer never actually fires there.
    /// </summary>
    internal void EndFlash(string id) => _band.EndFlash(id);

    /// <summary>
    /// Stops any slide and returns the flags to a known state. Unconditional and idempotent.
    ///
    /// <para>
    /// Clears the sliding flags itself, and it MUST: the generation bump on the first line is
    /// exactly what stops a cancelled slide's completion from running, so completion cannot be the
    /// thing that clears them. Leave them set and ForceShow-mid-slide strands the user with a
    /// visible, at-rest toolbar whose re-arm gate reads false forever.
    /// </para>
    ///
    /// <para>
    /// Unconditional purely as simplification — under FillBehavior.Stop a finished clock has
    /// already stopped affecting the property, so there is no stale clock to remove. A condition
    /// here would be one more thing to get wrong.
    /// </para>
    /// </summary>
    internal void CancelSlide()
    {
        _slideGeneration++;
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _sliding = false;
        _slidingOut = false;
    }

    /// <summary>
    /// Cancels any slide, parks the band at rest, and shows the window with no animation. The
    /// recovery paths use this — tray fallback, modal suspend-recover, dock toggle — and every one
    /// exists because the user needs the toolbar now.
    ///
    /// <para>
    /// Zeroes BOTH axes. A slide only ever animates one, so an offset parked on the other by a
    /// previous docked edge is possible in principle; ordering happens to prevent it today
    /// (opening settings force-shows before SetMode changes the edge), which is a property of two
    /// unrelated call sites lining up rather than of this code.
    /// </para>
    /// </summary>
    public void ShowAtRest()
    {
        CancelSlide();
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
        Show();
    }

    /// <summary>
    /// Slides the band out along its docked edge, then hides the window and invokes
    /// <paramref name="onHidden"/>. Short-circuits to the completion effects, synchronously, when
    /// the window is already hidden or there is nothing to slide.
    /// </summary>
    public void SlideOutThenHide(Action onHidden)
    {
        var offset = SlideOffset();
        if (!IsVisible || offset == default)
        {
            CancelSlide();
            HideSafely();
            onHidden();
            return;
        }

        StartSlide(to: SlideAxisIsY ? offset.Y : offset.X, slidingOut: true, onDone: () =>
        {
            HideSafely();
            onHidden();
        });
    }

    /// <summary>
    /// Shows the window with the band sliding in from its docked edge. The clock is attached
    /// BEFORE Show(), which makes the off-edge first frame a property of the property's value
    /// rather than of dispatcher scheduling. Short-circuits to a plain Show when the toolbar is
    /// already on screen or there is nothing to slide.
    /// </summary>
    public void ShowThenSlideIn()
    {
        var offset = SlideOffset();
        if (IsOnScreen || offset == default)
        {
            CancelSlide();
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            Show();
            return;
        }

        // From the off-edge offset to rest. Note the base value written inside StartSlide is the
        // DESTINATION (0), not this starting value — see StartSlide.
        if (SlideAxisIsY) SlideTransform.Y = offset.Y; else SlideTransform.X = offset.X;
        StartSlide(to: 0, slidingOut: false, onDone: null);
        Show();
    }

    /// <summary>
    /// Runs one slide on the single axis the docked edge selects, from the transform's current
    /// value to <paramref name="to"/>.
    ///
    /// <para>
    /// The base value is written to the DESTINATION before the clock is attached, and the clock
    /// uses FillBehavior.Stop. That pairing is what makes the slide snap-free: Stop reverts the
    /// property to its base value when the clock detaches, and the base value is already where the
    /// slide ends. Writing the STARTING value as the base instead would snap the band back on the
    /// final tick. HoldEnd would avoid that snap and introduce a worse one — a held clock silently
    /// outranks every later direct assignment, so a subsequent write of 0 would be ignored and the
    /// toolbar would stay off-screen permanently with no visible cause.
    /// </para>
    /// </summary>
    private void StartSlide(double to, bool slidingOut, Action? onDone)
    {
        var axis = SlideAxisIsY ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        // Read the current (animated, if a clock is live) value BEFORE cancelling: removing a clock
        // reverts the property to its base value, which would lose a reversal's starting point.
        var from = SlideAxisIsY ? SlideTransform.Y : SlideTransform.X;

        CancelSlide();

        // The cross-axis is never animated; zero it so a previous edge cannot leave the band
        // displaced along it.
        if (SlideAxisIsY) SlideTransform.X = 0; else SlideTransform.Y = 0;

        // Base value == destination. See this method's doc comment.
        if (SlideAxisIsY) SlideTransform.Y = to; else SlideTransform.X = to;

        _sliding = true;
        _slidingOut = slidingOut;
        var generation = ++_slideGeneration;

        void Complete()
        {
            // Idempotent: a real Completed fires once, so production never re-enters this, but
            // CompleteSlideForTest can be called more than once by a test that does not know
            // better. Without this, a second call would still find the generations matching and
            // re-run HideSafely()/onHidden() — reachable only from the test seam, but a foot-gun
            // for the next test author, and the retained closure would otherwise keep pinning
            // onDone's captured state for no reason.
            _pendingSlideCompletion = null;
            _sliding = false;
            _slidingOut = false;
            onDone?.Invoke();
        }

        _pendingSlideCompletion = Complete;
        _pendingSlideGeneration = generation;

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(SlideDurationMs))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase
            {
                // Enter decelerating, leave accelerating.
                EasingMode = slidingOut ? EasingMode.EaseIn : EasingMode.EaseOut,
            },
        };
        anim.Completed += (_, _) => RunPendingSlideCompletion();

        SlideTransform.BeginAnimation(axis, anim);
    }

    /// <summary>
    /// The null-and-generation checks that gate running a slide's completion, shared by
    /// <c>anim.Completed</c> (above, in <see cref="StartSlide"/>) and
    /// <see cref="CompleteSlideForTest"/> — the only two callers of
    /// <see cref="_pendingSlideCompletion"/>. Before this was one method, production compared the
    /// closure-captured generation from its own <c>StartSlide</c> call while the test seam compared
    /// <see cref="_pendingSlideGeneration"/> against <see cref="_slideGeneration"/> — two separately
    /// written expressions of the same rule that happened to agree, which is exactly the kind of
    /// drift that lets one of them silently stop matching production. A test STA thread has no
    /// message pump, so an animation clock never advances and Completed never fires there — this
    /// is the seam that exists to let <see cref="CompleteSlideForTest"/> drive it by hand, the same
    /// reason <see cref="EndFlash"/> exists.
    ///
    /// <para>
    /// Driving completion by hand is also the only way the generation guard is observable at all:
    /// the guard protects against a Completed the framework documents will not raise on a removed
    /// clock, so a test that waited for a real clock could never tell "the guard worked" from
    /// "nothing fired".
    /// </para>
    /// </summary>
    private void RunPendingSlideCompletion()
    {
        if (_pendingSlideCompletion is null) return;
        if (_pendingSlideGeneration != _slideGeneration) return;
        _pendingSlideCompletion();
    }

    /// <summary>
    /// Test seam: runs the pending slide's completion exactly as the animation's Completed handler
    /// would. A window built in a test runs on an STA thread with no message pump, so the clock
    /// never advances and Completed never fires there. Mirrors <see cref="EndFlash"/>, which exists
    /// for the same reason.
    /// </summary>
    internal void CompleteSlideForTest() => RunPendingSlideCompletion();

    /// <summary>
    /// Window.Hide() guarded against the InvalidOperationException it is documented to throw on a
    /// window that is closing or has closed
    /// (learn.microsoft.com/dotnet/api/system.windows.window.hide). No boolean covers that case:
    /// IsVisible stays true through Closing and WPF exposes no public "is closing" flag, so
    /// catching the documented exception at the call is the only guard that matches the condition.
    /// Reachable only if a slide's completion outlives the window during shutdown, which is
    /// doubtful under ShutdownMode="OnExplicitShutdown" — hence a catch, not a lifecycle mechanism.
    /// </summary>
    private void HideSafely()
    {
        try { Hide(); }
        catch (InvalidOperationException) { }
    }
}
