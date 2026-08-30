namespace EyeCtrl.App.Overlays;

using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
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

public partial class ToolbarWindow : OverlayWindow
{
    // UI Layout Constants
    // 12, not the old 2: buttons carry inscribed hit circles (size/2), so a 2-px margin left adjacent
    // circles near-tangent and every slight aim error landed on a neighbour (2026-08-17 field report:
    // "I mis dwell often"). 12 per side = 24 px between circles, the same clearance the settings
    // window's 100-DIP dwell rows use between their 100-DIP hit circles.
    private const int ButtonMargin = 12; // Margin between toolbar buttons

    /// <summary>
    /// The one unrecoverable action. Named because the layout, the rim, the arc and the icon
    /// brush all branch on it, and five scattered string literals is five chances to typo one
    /// into silently taking the ordinary-button branch.
    /// </summary>
    private const string ResetId = "ResetAdaptation";
    // The local 0.4 symbol ratio moved to ContainerRatio.Large, which this file's 0.4 defined.
    // No longer read here: Task 6 moved the provider indicator's glyph off an emoji TextBlock at
    // Large onto a drawn icon Path at Small (beside its own hairline rule), so Large founded the
    // bucket but this file no longer consumes it — see ContainerRatioCensusTests.

    /// <summary>
    /// The disc that carries the selection-mode tint. Named for the same reason as
    /// <see cref="ResetId"/>: before this constant existed, the id was four scattered string
    /// literals (<see cref="FillFor"/>, <see cref="SetSelectionModeState"/>, and two checks in
    /// <see cref="BindButton"/>), and a typo in any one of them would silently take the ordinary-
    /// button branch instead of carrying the mode tint.
    /// </summary>
    private const string SelectionModeId = "SelectionMode";

    /// <summary>Centre-to-centre spacing: the button plus its margin on both sides.</summary>
    private double Pitch => _buttonSize + ButtonMargin * 2;

    /// <summary>Extra clearance before the one unrecoverable action. See Task 7.</summary>
    private double ResetGap => ButtonMargin * 2;

    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly Dictionary<string, TextBlock> _anchors = new();
    private double _buttonSize;
    private bool _targetsInitialized;
    private bool _vertical;
    private GazeProviderType? _providerType;

    /// <summary>
    /// The selection-mode tint, mirrored in a field rather than re-derived from the view-model on
    /// every paint. Justified, not just convenient: <c>SettingsUiCoordinator.cs:172-174</c> calls
    /// <see cref="SetSelectionModeState"/> BEFORE sending the <c>SelectionModeChangedMessage</c>
    /// that updates the button's <c>IconKey</c>, so the view-model is stale at that exact instant;
    /// and <see cref="Initialize"/> (<c>SettingsUiCoordinator.cs:179</c>) can rebuild the whole band
    /// with no <c>DataContext</c> set at all. This field is what makes
    /// <see cref="SetSelectionModeState"/> correct standalone, independent of whether a view-model
    /// is bound or current at the moment it's called.
    /// </summary>
    private Brush _selectionModeBrush = Tokens.BrushSurfaceMedium;

    /// <summary>
    /// Ids currently in the discrete "gaze is on this target" state (Task 5). Consulted by
    /// <see cref="PaintFace"/> alongside <see cref="_selectionModeBrush"/> so acquisition and the
    /// mode tint can each be updated independently, in either order, and still converge on the
    /// same face.
    /// </summary>
    private readonly HashSet<string> _acquiredIds = new();

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
        _buttons.Clear();
        _anchors.Clear();
        ButtonCanvas.Children.Clear();
        // No IndicatorPanel.Children.Clear() here: AddIndicatorElement below clears it itself
        // before adding, and when _providerType is null nothing has ever added to the panel in
        // the first place (SetProviderIndicator is IndicatorPanel's only other writer, and it
        // sets _providerType before calling AddIndicatorElement). A clear here would be provably
        // dead, not just redundant.
        _targetsInitialized = false;

        // Every id here must exist in ToolbarViewModel's button list and have a case in its
        // OnTargetActivated switch; ToolbarButtonDriftTests enforces that and records why the
        // deliberately-absent ids are absent.
        var buttons = new (string Id, string IconKey)[]
        {
            ("Pause",           "Pause"),
            ("SelectionMode",   SelectionModeIcons.None),
            ("Keyboard",        "Keyboard"),
            ("Settings",        "Settings"),
            ("TaskSwitcher",    "TaskSwitcher"),
            ("ResetAdaptation", "ResetAdaptation"),
        };

        LayOutBand(buttons);

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
    /// Places each button along the docked axis and sizes the Canvas to hold them. A Canvas
    /// measures to zero, and this window is SizeToContent, so the size must be explicit or the
    /// strip collapses.
    /// </summary>
    private void LayOutBand((string Id, string IconKey)[] buttons)
    {
        var along = ButtonMargin + _buttonSize / 2;   // centre of the first slot
        var across = Pitch / 2;
        double? previousEdge = null;

        foreach (var (id, iconKey) in buttons)
        {
            if (id == ResetId)
            {
                // Widen the gap FIRST, then place the rule halfway across it. Deriving the rule's
                // position from the pre-gap `along` puts it 48 px right of centre — straight across
                // the warning-rimmed disc it is supposed to fence off.
                along += ResetGap;
                if (previousEdge is { } edge)
                    AddSeparator((edge + along - _buttonSize / 2) / 2);
            }

            AddButton(id, iconKey, _vertical ? new Point(across, along) : new Point(along, across));
            previousEdge = along + _buttonSize / 2;
            along += Pitch;
        }

        // `along` is one Pitch past the last centre when the loop exits; back that off, then add
        // the last button's half-diameter and the trailing margin.
        var lastCentre = along - Pitch;
        var extent = lastCentre + _buttonSize / 2 + ButtonMargin;
        ButtonCanvas.Width = _vertical ? Pitch : extent;
        ButtonCanvas.Height = _vertical ? extent : Pitch;
    }

    private void AddButton(string id, string iconKey, Point center)
    {
        var button = DwellArcButton.Create(
            center,
            _buttonSize / 2,
            symbol: null,
            fill: FillFor(id),
            stroke: RimFor(id),
            arcBrush: ArcBrushFor(id),
            symbolBrush: IconBrushFor(id),
            icon: ToolbarIcons.For(ParseIconKey(iconKey)),
            label: null);

        button.AddTo(ButtonCanvas);

        // The anchor exists so the button appears in the UIA tree at all: WPF's UIA control view
        // does not expose Ellipse or Path from a transparent overlay window. ActionMenuWindow sets
        // no AutomationIds and so needs no anchor; the toolbar's ids are consumed by AppFixture.
        // Opacity 0 keeps it laid out and in the tree while drawing nothing.
        //
        // Width/Height are the button's, NOT left to the text: an unsized TextBlock in a Canvas
        // measures to its own text, so the published BoundingRectangle would be a ~16px-tall
        // sliver at the disc's top-left whose width varies with the length of the id. Dwell
        // simulation aims at that rectangle's centre (AppFixture.SimulateDwellOn), which for the
        // shorter ids lands outside the 60px hit circle entirely. Sized here, the anchor's rect
        // IS the disc's box. Pinned by ToolbarButtonVisualTests.
        var anchor = new TextBlock
        {
            Text = id,
            Opacity = 0,
            IsHitTestVisible = false,
            Width = _buttonSize,
            Height = _buttonSize,
        };
        AutomationProperties.SetAutomationId(anchor, $"Toolbar_Button_{id}");
        AutomationProperties.SetName(anchor, id);
        Canvas.SetLeft(anchor, center.X - _buttonSize / 2);
        Canvas.SetTop(anchor, center.Y - _buttonSize / 2);
        ButtonCanvas.Children.Add(anchor);

        _buttons[id] = button;
        _anchors[id] = anchor;

        // Reapplies acquired state (and the mode tint) after a fresh construction. SwapIcon
        // rebuilds via RemoveButton + AddButton, so this is what makes an acquired disc — or
        // SelectionMode's tint — survive that rebuild without a separate call site at every place
        // that can trigger one (item 2 of the round-2 review: the IconKey branch used to drop
        // acquired state because nothing else repainted the fresh Ellipse it hands back).
        PaintFace(id);
    }

    /// <summary>
    /// A hairline between the ordinary buttons and the destructive one. NOT protection — an
    /// accidental Reset is still unrecoverable; the confirm step is a separate follow-up plan.
    /// Do not describe this button as protected until that ships.
    ///
    /// <para>
    /// <see cref="Tokens.BrushBorderSubtle"/>, not <see cref="Tokens.BrushSurfaceMedium"/> (a
    /// Task 6 fix, done alongside the indicator rule in <see cref="AddIndicatorElement"/> — the
    /// two must move together, since at different weights in one 896 px strip they would read as
    /// a bug). SurfaceMedium on SurfaceDark measures 1.23 / 1.23 / 1.14 (Midnight / Ember /
    /// Contrast); BorderSubtle on the same ground measures 3.35 / 3.32 / 21.00 — the canonical
    /// figures already cited in <c>DwellToggleVisualTests.cs</c>,
    /// <c>SliderStepperContrastTests.cs</c> and <see cref="EyeCtrl.Controls.DwellSlider"/>, not
    /// re-derived here. <see cref="ToolbarButtonVisualTests.EveryButton_CarriesAVisibleRim"/>
    /// already asserts SurfaceMedium alone is invisible as a boundary; this line was painting a
    /// new boundary in exactly that pairing.
    /// </para>
    /// </summary>
    private void AddSeparator(double along)
    {
        var half = _buttonSize * ContainerRatio.Dominant / 2;
        var across = Pitch / 2;

        var line = new Line
        {
            Stroke = Tokens.BrushBorderSubtle,
            StrokeThickness = Tokens.BorderThicknessStandard,
            IsHitTestVisible = false,
            X1 = _vertical ? across - half : along,
            X2 = _vertical ? across + half : along,
            Y1 = _vertical ? along : across - half,
            Y2 = _vertical ? along : across + half,
        };

        ButtonCanvas.Children.Add(line);
    }

    /// <summary>Parses a key name. An unknown key is a coding error, not user input.</summary>
    private static ToolbarIconKey ParseIconKey(string key) => Enum.Parse<ToolbarIconKey>(key);

    /// <summary>The at-rest rim. Only Reset differs — see Task 7.</summary>
    private static Brush RimFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushBorderSubtle;

    /// <summary>The progress arc. Reset's is warning-coloured so its dwell reads as different.</summary>
    private static Brush ArcBrushFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushAccent;

    /// <summary>The icon. Only Reset differs.</summary>
    private static Brush IconBrushFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushTextPrimary;

    /// <summary>
    /// Rebuilds one button with a new icon. DwellArcButton builds its icon Paths in Create and
    /// has no icon setter, so a swap is a rebuild — cheap, and only Pause and SelectionMode ever
    /// swap. Preserves the button's centre so no dwell target moves.
    ///
    /// <para>
    /// The rebuilt elements land at the END of the Canvas's child list rather than back in their
    /// old slots. That was checked rather than assumed, and is a non-issue: WITHIN a button
    /// <c>AddTo</c> re-runs in its fixed order, so halo-under-accent still holds; ACROSS buttons
    /// nothing can overlap, since a RimHalo reaches only radius+4 while adjacent disc edges are
    /// 24 DIP apart. Nothing hit-tests this canvas and no test reads child order.
    /// </para>
    /// </summary>
    private void SwapIcon(string id, string iconKey)
    {
        if (!_buttons.TryGetValue(id, out var old))
            return;

        var center = new Point(Canvas.GetLeft(old.Background) + _buttonSize / 2,
                               Canvas.GetTop(old.Background) + _buttonSize / 2);
        RemoveButton(id);
        AddButton(id, iconKey, center);
    }

    private void RemoveButton(string id)
    {
        // DwellArcButton.RemoveFrom, not a local element list: the list is DwellArcButton's to
        // know, and a copy of it here would go stale the moment AddTo gains a layer.
        if (_buttons.Remove(id, out var button))
            button.RemoveFrom(ButtonCanvas);

        if (_anchors.Remove(id, out var anchor))
            ButtonCanvas.Children.Remove(anchor);
    }

    /// <summary>
    /// Draws the gaze-provider indicator: a small glyph (eye/mouse) plus a hairline rule, sized
    /// and coloured as chrome rather than as a sixth button — it carries no fill, no rim and no
    /// dwell target, so it cannot be mistaken for a broken button the way the old full-size
    /// <c>Border</c>+emoji <c>TextBlock</c> could.
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
            Margin = _vertical ? new Thickness(0, ButtonMargin, 0, 0) : new Thickness(ButtonMargin, 0, 0, 0),
        };

        // BrushBorderSubtle, not BrushSurfaceMedium — same reason as AddSeparator's Line, see
        // that method's doc comment for the contrast figures; the two moved together.
        var rule = new Border
        {
            Width = _vertical ? _buttonSize * ContainerRatio.Dominant : Tokens.BorderThicknessStandard,
            Height = _vertical ? Tokens.BorderThicknessStandard : _buttonSize * ContainerRatio.Dominant,
            Background = Tokens.BrushBorderSubtle,
            IsHitTestVisible = false,
            Margin = _vertical ? new Thickness(0, ButtonMargin, 0, 0)
                               : new Thickness(ButtonMargin, 0, 0, 0),
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

    private void UpdateTargetPositions()
    {
        if (DataContext is not ToolbarViewModel vm) return;

        foreach (var (id, button) in _buttons)
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

        // Don't latch until every face has been laid out — a gaze dispatch at Normal priority can
        // outrank the Render layout pass, collapsing all targets onto (0,0).
        foreach (var (_, button) in _buttons)
            if (button.Background.ActualWidth == 0)
                return;

        _targetsInitialized = true;
    }

    public void SetPausedState(bool isPaused) => SwapIcon("Pause", isPaused ? "Play" : "Pause");

    /// <summary>The at-rest fill. SelectionMode overrides it to carry its mode.</summary>
    private Brush FillFor(string id) =>
        id == SelectionModeId ? _selectionModeBrush : Tokens.BrushSurfaceMedium;

    /// <summary>
    /// Applies the selection-mode tint to the SelectionMode disc, caching it in
    /// <see cref="_selectionModeBrush"/> (see that field's doc for why a cache, not a live
    /// view-model read) and repainting through <see cref="PaintFace"/> so an already-acquired
    /// disc keeps its acquired fill rather than being clobbered back to the tint.
    /// </summary>
    public void SetSelectionModeState(SelectionMode mode)
    {
        _selectionModeBrush = mode switch
        {
            SelectionMode.ManualSelection => Tokens.BrushToggleOn,
            SelectionMode.AutoSelection => Tokens.BrushSurfaceKeySwitch,
            _ => Tokens.BrushSurfaceMedium
        };

        PaintFace(SelectionModeId);
    }

    /// <summary>
    /// Subscribes each button view-model to its visuals. Safe to call repeatedly, which
    /// RebuildBand needs: every dock/axis/size change rebuilds the whole band and then rebinds.
    ///
    /// Dropping the old subscriptions is NOT about dead visuals — the handler resolves
    /// _buttons at event time, so it keeps working across a rebuild. It is about not
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
    /// button from <c>_buttons</c> at event time, so it stays correct after Initialize rebuilds
    /// the band (size/dock/edge saves).
    /// </summary>
    private void BindButton(ToolbarButtonViewModel btn)
    {
        var id = btn.Id;

        // Apply the current icon so the initial face matches the actual mode (the ViewModel may
        // already have received the startup SelectionModeChangedMessage before BindToViewModel
        // is called). This block re-running is what fixes a rebuilt band (SetMode's axis flip,
        // or a plain size change) showing constructor-default icons/tint instead of live state.
        if (_buttons.TryGetValue(id, out _))
        {
            // SwapIcon rebuilds the button (DwellArcButton.Create has no icon setter), which
            // hands back fresh Halo/ProgressPath elements defaulted to Visibility.Visible — WPF's
            // default, not a collapsed rest state. UpdateProgress must run AFTER SwapIcon so it
            // collapses the arc the rebuild just (re)exposed, rather than being clobbered by it.
            // The re-lookup after SwapIcon cannot fail: SwapIcon replaces _buttons[id] rather
            // than removing it (RemoveButton followed immediately by AddButton for the same id).
            SwapIcon(id, btn.IconKey);
            if (id == SelectionModeId)
                SetSelectionModeState(SelectionModeIcons.ToMode(btn.IconKey));

            _buttons[id].UpdateProgress(btn.DwellProgress);
            // IsHighlighted is a third property with the same "unchanged value repairs nothing"
            // gap this block's leading comment already names for icon/mode-tint — a rebuild while
            // gaze is still on a button (the toolbar keeps feeding gaze while the settings window
            // is open, so a settings-triggered rebuild is reachable mid-highlight) must not drop
            // the acquired state until the dwell happens to end or activate. Unlike the two calls
            // above, this one is order-independent: PaintFace recomputes the face from
            // _acquiredIds/_selectionModeBrush each time either is touched, so it converges to the
            // same result whichever of SetSelectionModeState/ApplyAcquiredState runs last.
            ApplyAcquiredState(id, btn.IsHighlighted);
        }

        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (!_buttons.ContainsKey(id))
                return;

            if (e.PropertyName == nameof(ToolbarButtonViewModel.DwellProgress))
            {
                if (_buttons.TryGetValue(id, out var b)) b.UpdateProgress(btn.DwellProgress);
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IsHighlighted))
            {
                ApplyAcquiredState(id, btn.IsHighlighted);   // Task 5
            }
            else if (e.PropertyName == nameof(ToolbarButtonViewModel.IconKey))
            {
                // SwapIcon's rebuild (RemoveButton + AddButton) now repaints via PaintFace inside
                // AddButton, so an acquired disc's fill/rim survives this branch without a
                // separate ApplyAcquiredState call here (round-2 review item 2). UpdateProgress
                // still needs reapplying for the same reason the initial-apply block above does:
                // the rebuild hands back fresh Halo/ProgressPath elements defaulted to Visible.
                SwapIcon(id, btn.IconKey);
                if (id == SelectionModeId)
                    SetSelectionModeState(SelectionModeIcons.ToMode(btn.IconKey));
                _buttons[id].UpdateProgress(btn.DwellProgress);
            }
        };

        btn.PropertyChanged += handler;
        _buttonSubs.Add((btn, handler));
    }

    /// <summary>For tests: the DwellArcButton behind one id.</summary>
    internal DwellArcButton ButtonForTest(string id) => _buttons[id];

    /// <summary>
    /// The discrete "gaze is on this target" state, distinct from the continuous arc. Fill and rim
    /// only — never a progress-proportional wash over the fill, which collapsed a keyboard key's
    /// label from 10.42:1 to 1.08:1 and is guarded by DwellWashCensusTests.
    /// </summary>
    private void ApplyAcquiredState(string id, bool acquired)
    {
        if (acquired) _acquiredIds.Add(id);
        else _acquiredIds.Remove(id);
        PaintFace(id);
    }

    /// <summary>
    /// The only writer of a disc's fill and rim. Both <see cref="ApplyAcquiredState"/> (writes
    /// <see cref="_acquiredIds"/>) and <see cref="SetSelectionModeState"/> (writes
    /// <see cref="_selectionModeBrush"/>) route through here rather than assigning
    /// <c>Background.Fill</c>/<c>Stroke</c> directly, and <see cref="AddButton"/> calls it after
    /// every (re)construction. That funnel is what makes the two independent of call order:
    /// previously each wrote the same two properties on its own, so whichever ran last on a given
    /// disc won outright, and reversing the order in <see cref="BindButton"/> silently clobbered an
    /// acquired SelectionMode disc's fill back to the mode tint (round-2 review item 3). Acquisition
    /// always wins over the tint here, stated once as the ternary condition instead of once per
    /// call site as a comment.
    /// </summary>
    private void PaintFace(string id)
    {
        if (!_buttons.TryGetValue(id, out var b)) return;

        var acquired = _acquiredIds.Contains(id);
        b.Background.Fill = acquired ? Tokens.BrushSurfaceLight : FillFor(id);
        b.Background.Stroke = acquired ? Tokens.BrushBorderFocus : RimFor(id);
    }

    /// <summary>The success-coloured flash confirming a dwell fired. Task 8 fills this in.</summary>
    private void FlashActivated(string id) { }
}
