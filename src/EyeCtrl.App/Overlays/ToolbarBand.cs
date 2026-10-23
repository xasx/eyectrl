namespace EyeCtrl.App.Overlays;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;
using SelectionMode = EyeCtrl.Core.Models.SelectionMode;

/// <summary>
/// The toolbar's row of dwell buttons: what they are, where they sit, and how each one is drawn.
///
/// <para>
/// Split out of <see cref="ToolbarWindow"/>, which had grown to ~800 lines by owning this
/// alongside four unrelated jobs — window docking and orientation, the gaze-provider indicator,
/// view-model binding, and dwell-target placement. The seam is ownership of
/// <see cref="ButtonCanvas"/>: everything drawn INSIDE the band lives here, and everything about
/// the window that contains it stays there. The indicator is the clearest case of the line — it
/// renders next to the band but deliberately is NOT part of it (it carries no fill, no rim and no
/// dwell target, precisely so it cannot read as a sixth button), so it stayed with the window.
/// </para>
///
/// <para>
/// This class knows nothing about gaze, dwell timing, or view-models. It is told what to draw and
/// what state to draw it in; <see cref="ToolbarWindow"/> decides when.
/// </para>
/// </summary>
internal sealed class ToolbarBand
{
    // 12, not the old 2: buttons carry inscribed hit circles (size/2), so a 2-px margin left adjacent
    // circles near-tangent and every slight aim error landed on a neighbour (2026-08-17 field report:
    // "I mis dwell often"). 12 per side = 24 px between circles, the same clearance the settings
    // window's 100-DIP dwell rows use between their 100-DIP hit circles.
    // internal, not private: EyeCtrl.App.Tests has InternalsVisibleTo (EyeCtrl.App.csproj:61) and
    // ToolbarButtonVisualTests derives its expected pitch/gap from this rather than keeping a
    // second, independently-driftable copy of the number.
    internal const int ButtonMargin = 12; // Margin between toolbar buttons

    /// <summary>
    /// The one unrecoverable action. Named because the layout, the rim, the arc and the icon
    /// brush all branch on it, and five scattered string literals is five chances to typo one
    /// into silently taking the ordinary-button branch. internal, not private, for the same
    /// reason as <see cref="ButtonMargin"/> — so tests reference this constant instead of adding
    /// a sixth scattered literal of their own.
    /// </summary>
    internal const string ResetId = "ResetAdaptation";

    /// <summary>
    /// The disc that carries the selection-mode tint. Named for the same reason as
    /// <see cref="ResetId"/>: before this constant existed, the id was four scattered string
    /// literals (<see cref="FillFor"/>, <see cref="SetSelectionMode"/>, and two checks in
    /// <c>ToolbarWindow.BindButton</c>), and a typo in any one of them would silently take the
    /// ordinary-button branch instead of carrying the mode tint.
    /// </summary>
    internal const string SelectionModeId = "SelectionMode";

    /// <summary>How long an activation confirmation is held, in milliseconds.</summary>
    private const int FlashHoldMs = 250;

    private readonly Canvas _canvas;
    private readonly Dictionary<string, DwellArcButton> _buttons = new();
    private readonly Dictionary<string, TextBlock> _anchors = new();

    private double _buttonSize;
    private bool _vertical;

    /// <summary>
    /// The selection-mode tint, mirrored in a field rather than re-derived from the view-model on
    /// every paint. Justified, not just convenient: <c>SettingsUiCoordinator.cs:172-174</c> calls
    /// <c>ToolbarWindow.SetSelectionModeState</c> BEFORE sending the
    /// <c>SelectionModeChangedMessage</c> that updates the button's <c>IconKey</c>, so the
    /// view-model is stale at that exact instant; and <c>ToolbarWindow.Initialize</c>
    /// (<c>SettingsUiCoordinator.cs:179</c>) can rebuild the whole band with no <c>DataContext</c>
    /// set at all. This field is what makes <see cref="SetSelectionMode"/> correct standalone,
    /// independent of whether a view-model is bound or current at the moment it's called.
    /// </summary>
    private Brush _selectionModeBrush = Tokens.BrushSurfaceMedium;

    /// <summary>
    /// Ids currently in the discrete "gaze is on this target" state. Consulted by
    /// <see cref="PaintFace"/> alongside <see cref="_selectionModeBrush"/> so acquisition and the
    /// mode tint can each be updated independently, in either order, and still converge on the
    /// same face.
    /// </summary>
    private readonly HashSet<string> _acquiredIds = new();

    /// <summary>
    /// Ids currently showing the success-coloured activation flash. Consulted by
    /// <see cref="PaintFace"/> alongside <see cref="_acquiredIds"/> and
    /// <see cref="_selectionModeBrush"/>, with the highest precedence of the three: a dwell that
    /// just fired must read as fired even on a button gaze is still resting on.
    /// </summary>
    private readonly HashSet<string> _flashingIds = new();

    /// <summary>
    /// One timer per currently-flashing id, so a second activation while the first flash is still
    /// live can stop and replace its own timer without disturbing any other button's.
    /// </summary>
    private readonly Dictionary<string, DispatcherTimer> _flashTimers = new();

    /// <summary>
    /// Ids currently carrying the D18 frozen-silent cue (an engagement is live but no panel is
    /// shown). Consulted by <see cref="PaintFace"/> between <see cref="_flashingIds"/> and
    /// <see cref="_acquiredIds"/>, so the cue is independent of call order with the flash and the
    /// acquisition — same pattern as those two sets. Deliberately NOT cleared by
    /// <see cref="Build"/>: an engagement survives a rebuild (only an absent Reset or a disarm
    /// ends it), and <see cref="AddButton"/>'s PaintFace call is what re-applies the doubled rim
    /// to the fresh Ellipse, the same way it re-applies acquired state.
    /// </summary>
    private readonly HashSet<string> _frozenSilentIds = new();

    /// <summary>
    /// The rim width <see cref="DwellArcButton.Create"/> draws every disc at: DwellArcButton's
    /// private <c>RimThickness</c> (DwellArcButton.cs), duplicated here because
    /// EyeCtrl.Controls grants no InternalsVisibleTo to this assembly and the cue must both
    /// double it and restore it. The two must move together — if DwellArcButton's rim ever
    /// changes, this constant (and the 4 DIP the cue derives from it) change with it.
    /// </summary>
    private const double RimThicknessDip = 2;   // DwellArcButton.RimThickness — keep in step

    internal ToolbarBand(Canvas canvas) => _canvas = canvas;

    /// <summary>Centre-to-centre spacing: the button plus its margin on both sides.</summary>
    private double Pitch => _buttonSize + ButtonMargin * 2;

    /// <summary>Extra clearance before the one unrecoverable action.</summary>
    internal double ResetGap => ButtonMargin * 2;

    /// <summary>Every button, for the caller that has to place a dwell target on each.</summary>
    internal IEnumerable<KeyValuePair<string, DwellArcButton>> Buttons => _buttons;

    /// <summary>Whether the band currently renders a button with this id.</summary>
    internal bool Contains(string id) => _buttons.ContainsKey(id);

    /// <summary>The drawn button behind one id. Throws if absent — an unknown id is a coding error.</summary>
    internal DwellArcButton this[string id] => _buttons[id];

    /// <summary>
    /// Clears and rebuilds the whole band at the given diameter and axis.
    ///
    /// <para>
    /// Every id here must exist in <c>ToolbarViewModel</c>'s button list and have a case in its
    /// <c>OnTargetActivated</c> switch; <c>ToolbarButtonDriftTests</c> enforces that against THIS
    /// file and records why the deliberately-absent ids are absent. That guard anchors on the
    /// <c>var buttons = new (string Id, string ...</c> prefix below and fails loudly if it moves,
    /// so it cannot silently start checking nothing.
    /// </para>
    ///
    /// <para>
    /// Reset is on the band only while adaptation is enabled (Task 7, D5/M5): the one
    /// unrecoverable action must not be offered when there is nothing to reset. The entry STAYS
    /// in the array below and the view-model's list and activation case stay unconditional
    /// (D11) — membership is a runtime filter, not a deletion, so the drift guard still reads
    /// the id and an Enabled flip is a rebuild, not a code path. Dropping the entry removes the
    /// separator and the <see cref="ResetGap"/> with it: <see cref="LayOut"/> adds both only for
    /// <see cref="ResetId"/>.
    /// </para>
    /// </summary>
    /// <param name="buttonSize">Button diameter in DIP.</param>
    /// <param name="vertical">Whether the band runs down the screen rather than across it.</param>
    /// <param name="includeReset">Whether the band carries the Reset button — the caller's
    /// <c>AdaptationSettings.Enabled</c>.</param>
    internal void Build(double buttonSize, bool vertical, bool includeReset)
    {
        _buttonSize = buttonSize;
        _vertical = vertical;

        _buttons.Clear();
        _anchors.Clear();
        _canvas.Children.Clear();

        var buttons = new (string Id, string IconKey)[]
        {
            ("Pause",           "Pause"),
            ("SelectionMode",   SelectionModeIcons.None),
            ("Keyboard",        "Keyboard"),
            ("Settings",        "Settings"),
            ("TaskSwitcher",    "TaskSwitcher"),
            ("Modes",           "Modes"),
            ("ResetAdaptation", "ResetAdaptation"),
        };

        // Filtering the declared array, not conditionally appending: the declaration above stays
        // whole, which is what ToolbarButtonDriftTests' prefix-anchored extraction reads. And by
        // the ResetId constant, not a re-typed literal — same reason the constant exists.
        if (!includeReset)
            buttons = Array.FindAll(buttons, b => b.Id != ResetId);

        LayOut(buttons);
    }

    /// <summary>
    /// Places each button along the docked axis and sizes the Canvas to hold them. A Canvas
    /// measures to zero, and the containing window is SizeToContent, so the size must be explicit
    /// or the strip collapses.
    /// </summary>
    private void LayOut((string Id, string IconKey)[] buttons)
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
        _canvas.Width = _vertical ? Pitch : extent;
        _canvas.Height = _vertical ? extent : Pitch;
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

        button.AddTo(_canvas);

        // The anchor exists so the button appears in the UIA tree at all. Not a transparency
        // effect (this comment used to say it was): Shape never overrides OnCreateAutomationPeer
        // and UIElement's default returns null, so the Ellipses and Paths a DwellArcButton is made
        // of have NO automation peer in any view, in any window. With symbol and label both null
        // above, its only peer-bearing element types are absent too. ActionMenuWindow sets no
        // AutomationIds and so needs no anchor; the toolbar's ids are consumed by AppFixture.
        //
        // Opacity 0 keeps it laid out and in the tree while drawing nothing, and costs it nothing
        // in the control view: for a code-created TextBlock, IsControlElement reduces to
        // UIElement.IsVisible, which depends on Visibility and a root PresentationSource and never
        // on Opacity. Derived from the framework source in
        // docs/design/toolbar-accessibility/FINDINGS.md item 4, which also records the one live
        // consequence: an auto-hidden toolbar (Window.Hide()) does drop these from the control
        // view, so anything driving them must show it first.
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
        AutomationProperties.SetName(anchor, AccessibleNameFor(id, iconKey));
        Canvas.SetLeft(anchor, center.X - _buttonSize / 2);
        Canvas.SetTop(anchor, center.Y - _buttonSize / 2);
        _canvas.Children.Add(anchor);

        _buttons[id] = button;
        _anchors[id] = anchor;

        // Reapplies acquired state (and the mode tint) after a fresh construction. SwapIcon
        // rebuilds via RemoveButton + AddButton, so this is what makes an acquired disc — or
        // SelectionMode's tint — survive that rebuild without a separate call site at every place
        // that can trigger one (the IconKey branch used to drop acquired state because nothing
        // else repainted the fresh Ellipse it hands back).
        PaintFace(id);
    }

    /// <summary>
    /// A hairline between the ordinary buttons and the destructive one. NOT protection — an
    /// accidental Reset is still unrecoverable; the confirm step is a separate follow-up plan.
    /// Do not describe this button as protected until that ships.
    ///
    /// <para>
    /// <see cref="Tokens.BrushBorderSubtle"/>, not <see cref="Tokens.BrushSurfaceMedium"/> (done
    /// alongside the indicator rule in <c>ToolbarWindow.AddIndicatorElement</c> — the two must
    /// move together, since at different weights in one 896 px strip they would read as a bug).
    /// SurfaceMedium on SurfaceDark measures 1.23 / 1.23 / 1.14 (Midnight / Ember / Contrast);
    /// BorderSubtle on the same ground measures 3.35 / 3.32 / 21.00 — the canonical figures
    /// already cited in <c>DwellToggleVisualTests.cs</c>, <c>SliderStepperContrastTests.cs</c> and
    /// <see cref="EyeCtrl.Controls.DwellSlider"/>, not re-derived here.
    /// <c>ToolbarButtonVisualTests.EveryButton_CarriesAVisibleRim</c> already asserts SurfaceMedium
    /// alone is invisible as a boundary; this line was painting a new boundary in exactly that
    /// pairing.
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

        _canvas.Children.Add(line);
    }

    /// <summary>Parses a key name. An unknown key is a coding error, not user input.</summary>
    private static ToolbarIconKey ParseIconKey(string key) => Enum.Parse<ToolbarIconKey>(key);

    /// <summary>
    /// The button's UIA name (D11). The id also names the automation id the test fixture finds, but
    /// an id is not text: the NAME is the overlays surface's word for the button, so "TaskSwitcher"
    /// announces as "Task switcher" and can be translated. Pause keys off its ICON as well, because
    /// its face follows the paused state — the disc shows Play while the name would otherwise keep
    /// saying "Pause". An id without a key keeps itself, the same fallback the ring uses for an
    /// unmapped action.
    /// </summary>
    private static string AccessibleNameFor(string id, string iconKey) => (id, iconKey) switch
    {
        ("Pause", "Play") => StringTable.Current.T("overlays.toolbar.resume"),
        ("Pause", _) => StringTable.Current.T("overlays.toolbar.pause"),
        (SelectionModeId, _) => StringTable.Current.T("overlays.toolbar.selectionMode"),
        ("Keyboard", _) => StringTable.Current.T("overlays.toolbar.keyboard"),
        ("Settings", _) => StringTable.Current.T("overlays.toolbar.settings"),
        ("TaskSwitcher", _) => StringTable.Current.T("overlays.toolbar.taskSwitcher"),
        ("Modes", _) => StringTable.Current.T("overlays.toolbar.modes"),
        (ResetId, _) => StringTable.Current.T("overlays.toolbar.resetAdaptation"),
        _ => id,
    };

    /// <summary>The at-rest rim. Only Reset differs.</summary>
    private static Brush RimFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushBorderSubtle;

    /// <summary>The progress arc. Reset's is warning-coloured so its dwell reads as different.</summary>
    private static Brush ArcBrushFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushAccent;

    /// <summary>The icon. Only Reset differs.</summary>
    private static Brush IconBrushFor(string id) =>
        id == ResetId ? Tokens.BrushAccentWarning : Tokens.BrushTextPrimary;

    /// <summary>The at-rest fill. SelectionMode overrides it to carry its mode.</summary>
    private Brush FillFor(string id) =>
        id == SelectionModeId ? _selectionModeBrush : Tokens.BrushSurfaceMedium;

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
    internal void SwapIcon(string id, string iconKey)
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
            button.RemoveFrom(_canvas);

        if (_anchors.Remove(id, out var anchor))
            _canvas.Children.Remove(anchor);
    }

    /// <summary>Collapses or advances one button's progress arc.</summary>
    internal void UpdateProgress(string id, double progress)
    {
        if (_buttons.TryGetValue(id, out var button))
            button.UpdateProgress(progress);
    }

    /// <summary>Dims or restores one button's progress arc while its dwell is held (display-only).</summary>
    internal void SetPaused(string id, bool paused)
    {
        if (_buttons.TryGetValue(id, out var button))
            button.SetPaused(paused);
    }

    /// <summary>
    /// Applies the selection-mode tint to the SelectionMode disc, caching it in
    /// <see cref="_selectionModeBrush"/> (see that field's doc for why a cache, not a live
    /// view-model read) and repainting through <see cref="PaintFace"/> so an already-acquired
    /// disc keeps its acquired fill rather than being clobbered back to the tint.
    /// </summary>
    internal void SetSelectionMode(SelectionMode mode)
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
    /// The discrete "gaze is on this target" state, distinct from the continuous arc. Fill and rim
    /// only — never a progress-proportional wash over the fill, which collapsed a keyboard key's
    /// label from 10.42:1 to 1.08:1 and is guarded by DwellWashCensusTests.
    /// </summary>
    internal void SetAcquired(string id, bool acquired)
    {
        if (acquired) _acquiredIds.Add(id);
        else _acquiredIds.Remove(id);
        PaintFace(id);
    }

    /// <summary>
    /// The only writer of a disc's fill, rim and rim width. Both <see cref="SetAcquired"/> (writes
    /// <see cref="_acquiredIds"/>) and <see cref="SetSelectionMode"/> (writes
    /// <see cref="_selectionModeBrush"/>) route through here rather than assigning
    /// <c>Background.Fill</c>/<c>Stroke</c> directly, and <see cref="AddButton"/> calls it after
    /// every (re)construction. That funnel is what makes the two independent of call order:
    /// previously each wrote the same two properties on its own, so whichever ran last on a given
    /// disc won outright, and reversing the order in <c>ToolbarWindow.BindButton</c> silently
    /// clobbered an acquired SelectionMode disc's fill back to the mode tint. Acquisition always
    /// wins over the tint here, stated once as the ternary condition instead of once per call site
    /// as a comment.
    ///
    /// <para>
    /// <see cref="_flashingIds"/> is a third input, ahead of both: a dwell that just fired must
    /// read as fired even on a button gaze is still resting on (the common case — the user is
    /// still looking at what they just activated) or one still carrying the mode tint. Precedence,
    /// highest first: flashing, then acquired, then the button's own rest colours
    /// (<see cref="FillFor"/>/<see cref="RimFor"/>, which is where the mode tint and Reset's
    /// warning rim live). Stated once here as the ternary chain rather than once per call site.
    /// </para>
    ///
    /// <para>
    /// <see cref="_frozenSilentIds"/> (D18) sits BETWEEN flashing and acquired: it contributes no
    /// colour — the freeze holds <c>IsHighlighted = true</c>, so the frozen face is the ACQUIRED
    /// face and <see cref="RimFor"/> never runs while frozen — only a doubled rim stroke, the one
    /// thing that distinguishes "reading mode, nothing will fire" from plain acquisition. The
    /// flash outranks it in full (width included), so the two loudest states never stack into one
    /// face. This is the only writer of <c>StrokeThickness</c>: <see cref="DwellArcButton.Create"/>
    /// sets it once at construction and the cue restores exactly that value
    /// (<see cref="RimThicknessDip"/>) on every non-cue paint.
    /// </para>
    /// </summary>
    private void PaintFace(string id)
    {
        if (!_buttons.TryGetValue(id, out var b)) return;

        var flashing = _flashingIds.Contains(id);
        var acquired = _acquiredIds.Contains(id);
        var frozenSilent = _frozenSilentIds.Contains(id);
        b.Background.Fill = flashing ? Tokens.BrushSurfacePrediction
                           : acquired ? Tokens.BrushSurfaceLight
                           : FillFor(id);
        b.Background.Stroke = flashing ? Tokens.BrushAccentSuccess
                             : acquired ? Tokens.BrushBorderFocus
                             : RimFor(id);
        b.Background.StrokeThickness =
            frozenSilent && !flashing ? RimThicknessDip * 2 : RimThicknessDip;
    }

    /// <summary>
    /// The D18 frozen-silent cue (R2-3): an engagement is live but no panel is shown, so the
    /// doubled rim is the only evidence the freeze happened. Adds/removes the id from
    /// <see cref="_frozenSilentIds"/> and repaints through <see cref="PaintFace"/> — the same
    /// set-plus-funnel shape <see cref="SetAcquired"/> uses, for the same reason: PaintFace stays
    /// the sole writer of the face, and this input stays independent of call order with every
    /// other one.
    /// </summary>
    internal void SetFrozenSilent(string id, bool on)
    {
        if (on) _frozenSilentIds.Add(id);
        else _frozenSilentIds.Remove(id);
        PaintFace(id);
    }

    /// <summary>
    /// The success-coloured flash confirming a dwell fired. Zeroing <c>DwellProgress</c> collapses
    /// the arc identically whether a dwell completed or was abandoned at 99% — for Settings and
    /// Windows the actual effect happens somewhere the user isn't looking, so this is the only
    /// thing on the toolbar that says "that worked".
    ///
    /// <para>
    /// Adds to <see cref="_flashingIds"/> and repaints through <see cref="PaintFace"/> rather than
    /// writing <c>Background.Fill</c>/<c>Stroke</c> here directly — <see cref="PaintFace"/> is the
    /// sole writer of those two properties (see its doc comment), and a second writer would
    /// reintroduce the call-order problem that funnel removed.
    /// </para>
    /// </summary>
    internal void Flash(string id)
    {
        if (!_buttons.ContainsKey(id))
            return;

        _flashingIds.Add(id);
        PaintFace(id);

        if (_flashTimers.TryGetValue(id, out var existing))
            existing.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FlashHoldMs) };
        timer.Tick += (_, _) => EndFlash(id);
        _flashTimers[id] = timer;
        timer.Start();
    }

    /// <summary>
    /// Ends one button's activation flash: stops and drops its timer, removes it from
    /// <see cref="_flashingIds"/>, and repaints. Internal, not private, for two reasons: it is
    /// this class's own <see cref="DispatcherTimer"/> tick callback, AND the test seam those tests
    /// drive directly (through <c>ToolbarWindow.EndFlash</c>) — a window built in a test never
    /// calls <c>Show()</c> and runs on an STA thread with no message pump, so the timer's
    /// <c>Tick</c> never actually fires there.
    ///
    /// <para>
    /// Only removes from <see cref="_flashingIds"/> — it must NOT touch <see cref="_acquiredIds"/>.
    /// If gaze is still on the button when the flash ends, <see cref="PaintFace"/> must fall
    /// through to the acquired colours, not rest; clearing acquisition here as a side effect would
    /// silently drop that state instead.
    /// </para>
    /// </summary>
    internal void EndFlash(string id)
    {
        if (_flashTimers.Remove(id, out var timer))
            timer.Stop();

        _flashingIds.Remove(id);
        PaintFace(id);
    }

    /// <summary>
    /// True once every button face has been through a layout pass, so its centre can be resolved.
    ///
    /// <para>
    /// A face that has never been arranged reports <c>ActualWidth == 0</c> AND carries no visual
    /// offset, so <c>PointToScreen</c> on it returns the canvas origin — the SAME point for every
    /// button. Publishing that collapses all six dwell targets onto one spot, and because
    /// <see cref="ToolbarViewModel.UpdateButtonScreenPosition"/> CREATES a target on its first
    /// call, the wrong coordinate is permanent: every later call only moves a target that was
    /// already created in the wrong place.
    /// </para>
    ///
    /// <para>
    /// This is not hypothetical — it shipped, and left no toolbar button dwellable at all.
    /// <c>App.ShowOverlays</c> ends with <c>BindToViewModel()</c>, which rebuilds every button via
    /// <see cref="SwapIcon"/> (RemoveButton + AddButton hand back fresh <c>Ellipse</c>es), and
    /// <c>gazeEngine.Start()</c> is the very next line — so the first gaze samples arrive ahead of
    /// the Render-priority pass that arranges those fresh elements.
    /// </para>
    /// </summary>
    internal bool FacesAreArranged()
    {
        // An empty band must never count as arranged: latching on it would report "placed" having
        // placed nothing, and the gaze route — which stops delivering once the window says it is
        // initialised — would never call back.
        if (_buttons.Count == 0)
            return false;

        foreach (var (_, button) in _buttons)
            if (button.Background.ActualWidth == 0)
                return false;

        return true;
    }
}
