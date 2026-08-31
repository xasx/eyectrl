namespace EyeCtrl.App.Overlays;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
public partial class ToolbarWindow : OverlayWindow
{
    private readonly ToolbarBand _band;

    private double _buttonSize;

    // Written on the UI thread (UpdateTargetPositions / RebuildBand / SetMode), read on the gaze
    // thread through TargetsInitialized. Volatile for the reason ModalInteractionState's flags are:
    // a one-frame-stale read costs at most one extra dispatch.
    private volatile bool _targetsInitialized;
    private bool _vertical;
    private GazeProviderType? _providerType;

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
    public void Initialize(double buttonSize, int dwellTimeMs)
    {
        _buttonSize = buttonSize;
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

        _band.Build(_buttonSize, _vertical);

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

    public void SetPausedState(bool isPaused) => _band.SwapIcon("Pause", isPaused ? "Play" : "Pause");

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
}
