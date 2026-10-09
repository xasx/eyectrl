namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;
using static EyeCtrl.App.Tests.Fixtures.ToolbarTestHelpers;
using SelectionMode = EyeCtrl.Core.Models.SelectionMode;

/// <summary>
/// The toolbar's dwell target is a circle of radius Size/2. These tests pin the properties that
/// make the drawn button honest about that — it is an Ellipse of exactly that size, it carries a
/// rim visible against the strip, and its progress is an arc rather than a 4 px edge bar.
///
/// Layout is measured against the hosting Canvas, never with TranslatePoint against the Window:
/// a Window's content is not visually connected until Show(), so TranslatePoint returns (0,0) for
/// every element and any "nothing moved" assertion built on it is vacuously green.
///
/// Nothing calls Show(). Constructing the window parses its XAML, which is all these need.
/// </summary>
[Collection("WpfApplication")]
public class ToolbarButtonVisualTests
{
    private static ToolbarWindow NewInitialisedToolbar()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        window.Initialize(ButtonSize, 700);
        return window;
    }

    private static Canvas ButtonCanvas(ToolbarWindow w) => w.ButtonCanvasForTest;

    // Filtered on Fill, not on Width: a DwellArcButton contributes two Ellipses, and the one to
    // exclude is the RimHalo, which is a stroke-only ring with no fill. A `Width == ButtonSize`
    // filter would also work today, but only because RimHaloOffset happens to be non-zero — it
    // would silently start matching both ellipses if that offset ever went to 0.
    private static List<Ellipse> Faces(ToolbarWindow w) =>
        ButtonCanvas(w).Children.OfType<Ellipse>().Where(e => e.Fill is not null).ToList();

    [StaFact]
    public void EveryButton_IsAnEllipseOfExactlyTheDwellDiameter()
    {
        var faces = Faces(NewInitialisedToolbar());

        Check.WithCustomMessage("Seven rendered buttons expected.").That(faces).HasSize(7);
        foreach (var f in faces)
        {
            // A square tile hid ~21.5% dead area outside the inscribed hit circle. An Ellipse of
            // the dwell diameter makes the drawing and the hit test the same shape.
            Check.That(f.Width).IsEqualTo(ButtonSize);
            Check.That(f.Height).IsEqualTo(ButtonSize);
        }
    }

    [StaFact]
    public void EveryButton_CarriesAVisibleRim()
    {
        // SurfaceMedium on SurfaceDark computes to 1.23:1 — without a rim the boundary between
        // adjacent targets is invisible. DwellArcButton's default stroke is BrushBorderSubtle.
        foreach (var f in Faces(NewInitialisedToolbar()))
        {
            var rim = ((SolidColorBrush)f.Stroke).Color;
            Check.That(rim == Tokens.BorderSubtle || rim == Tokens.AccentWarning).IsTrue();
            Check.That(f.StrokeThickness).IsStrictlyGreaterThan(0.0);
        }
    }

    [StaFact]
    public void EveryButton_DrawsAnIcon_AndNoTextOnItsFace()
    {
        var canvas = ButtonCanvas(NewInitialisedToolbar());

        // Only the ScaleTransform-carrying Paths, not every Path: DwellArcButton contributes a
        // HaloPath and a ProgressPath to each button whether or not it was given an icon, so a
        // bare Path count reaches 12 here even with all six icons missing — a bar of 5 that
        // nothing can fail. The icon layers are the ones scaled from the 24-unit authoring grid
        // (DwellArcButton.Create's BuildLayer), and they are the only ones.
        //
        // Counted as DISTINCT Canvas.Left values rather than as layers, because an icon may have
        // a fill layer, a stroke layer or both, and all of a button's layers share one left edge
        // (DwellArcButton.cs:303). Six distinct lefts means six buttons drew an icon; a raw layer
        // count could be reached by five two-layer icons and one missing one.
        var iconLefts = canvas.Children.OfType<Path>()
            .Where(p => p.RenderTransform is ScaleTransform)
            .Select(Canvas.GetLeft)
            .Distinct()
            .ToList();

        Check.WithCustomMessage("A button fell back to a glyph — no icon paths.")
            .That(iconLefts).HasSize(7);

        var visibleText = canvas.Children.OfType<TextBlock>().Where(t => t.Opacity > 0).ToList();
        Check.WithCustomMessage("This design has no text on the button faces.")
            .That(visibleText).IsEmpty();
    }

    [StaFact]
    public void EveryButton_KeepsATextBlockAutomationAnchor()
    {
        // WPF's UIA control view does not expose Ellipse or Path from a transparent overlay
        // window, so the AutomationId has to live on a TextBlock. Losing it silently deletes
        // Toolbar_Button_<Id> from the UIA tree, which is how AppFixture finds these buttons
        // (AppFixture.cs:193-196, ToolbarTests.cs:35).
        var ids = ButtonCanvas(NewInitialisedToolbar()).Children.OfType<TextBlock>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        Check.That(ids).Contains("Toolbar_Button_Pause", "Toolbar_Button_SelectionMode",
            "Toolbar_Button_Keyboard", "Toolbar_Button_Settings",
            "Toolbar_Button_TaskSwitcher", "Toolbar_Button_ResetAdaptation");
    }

    /// <summary>
    /// D11 (T08): the id is only the AutomationId AppFixture aims at — the NAME is what a screen
    /// reader announces, and ids like "TaskSwitcher" and "ResetAdaptation" are not words. Every
    /// anchor publishes the overlays surface's real name for its button.
    /// </summary>
    [StaFact]
    public void EveryButton_PublishesARealUiaName_NotItsId()
    {
        var names = ButtonCanvas(NewInitialisedToolbar()).Children.OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(t)))
            .ToDictionary(AutomationProperties.GetAutomationId, AutomationProperties.GetName);

        Check.That(names).HasSize(7);
        Check.That(names["Toolbar_Button_Pause"]).IsEqualTo("Pause");
        Check.That(names["Toolbar_Button_SelectionMode"]).IsEqualTo("Selection mode");
        Check.That(names["Toolbar_Button_Keyboard"]).IsEqualTo("Keyboard");
        Check.That(names["Toolbar_Button_Settings"]).IsEqualTo("Settings");
        Check.That(names["Toolbar_Button_TaskSwitcher"]).IsEqualTo("Task switcher");
        Check.That(names["Toolbar_Button_Modes"]).IsEqualTo("Modes");
        Check.That(names["Toolbar_Button_ResetAdaptation"]).IsEqualTo("Reset adaptation");
    }

    /// <summary>
    /// The anchor's box must be the disc's box, because the anchor is what publishes the button's
    /// BoundingRectangle to UIA and dwell simulation aims at that rectangle's centre
    /// (AppFixture.SimulateDwellOn, AppFixture.cs:259-265). An unsized TextBlock in a Canvas
    /// measures to its own text, so the rect became a ~16px sliver at the disc's top-left whose
    /// width tracked the length of the id — putting the aim point 54-69 px from the true centre
    /// against a dwell radius of 60, i.e. outside the target for the three shortest ids. Those
    /// tests are Category=UI, so the headless suite never saw it.
    ///
    /// Still unproven here, and deliberately not claimed: that UIA actually EXPOSES an
    /// Opacity = 0 TextBlock from a transparent overlay window. WPF's automation peers key off
    /// Visibility/IsVisible rather than Opacity, and a non-empty Text satisfies
    /// IsControlElementCore — but that is reasoning, not evidence, and only the real tracker can
    /// settle it. Task 10 carries the confirmation.
    /// </summary>
    [StaFact]
    public void EveryAutomationAnchor_CoversItsOwnDisc()
    {
        var window = NewInitialisedToolbar();
        var canvas = ButtonCanvas(window);

        var anchors = canvas.Children.OfType<TextBlock>()
            .Where(t => !string.IsNullOrEmpty(AutomationProperties.GetAutomationId(t)))
            .OrderBy(Canvas.GetLeft)
            .ToList();
        Check.That(anchors).HasSize(7);

        foreach (var anchor in anchors)
        {
            var id = AutomationProperties.GetAutomationId(anchor);

            Check.WithCustomMessage($"{id} is sized to its text, not to its button.")
                .That(anchor.Width).IsEqualTo(ButtonSize);
            Check.That(anchor.Height).IsEqualTo(ButtonSize);
        }

        // Paired left-to-right against the discs, NOT "is centred on some disc": membership lets
        // a permutation through, and a permutation is precisely the defect that would make dwell
        // simulation activate a button other than the one the id names. Pinning the id sequence
        // is what makes the pairing — and this test's name — mean anything.
        Check.That(anchors.Select(AutomationProperties.GetAutomationId))
            .ContainsExactly("Toolbar_Button_Pause", "Toolbar_Button_SelectionMode",
                "Toolbar_Button_Keyboard", "Toolbar_Button_Settings",
                "Toolbar_Button_TaskSwitcher", "Toolbar_Button_Modes",
                "Toolbar_Button_ResetAdaptation");

        foreach (var (anchor, face) in anchors.Zip(Faces(window).OrderBy(Canvas.GetLeft)))
        {
            var id = AutomationProperties.GetAutomationId(anchor);
            var anchorCentre = new Point(Canvas.GetLeft(anchor) + anchor.Width / 2,
                                         Canvas.GetTop(anchor) + anchor.Height / 2);
            var discCentre = new Point(Canvas.GetLeft(face) + ButtonSize / 2,
                                       Canvas.GetTop(face) + ButtonSize / 2);

            Check.WithCustomMessage($"{id}'s anchor is not centred on its OWN disc, so a dwell "
                    + "aimed at its published rectangle would land on a different button.")
                .That(anchorCentre).IsEqualTo(discCentre);
        }
    }

    [StaFact]
    public void ButtonCentres_AndCanvasSize_AreExactlyAsLaidOut()
    {
        var window = NewInitialisedToolbar();
        var canvas = ButtonCanvas(window);
        var lefts = Faces(window).Select(Canvas.GetLeft).OrderBy(x => x).ToList();

        // Pinned exactly rather than as "at most two distinct pitches": that weaker form passes
        // if every gap is uniformly wrong, and passes if the deliberate extra gap before Reset
        // vanishes entirely. At ButtonSize 120 the arithmetic is fully determined — margin 12,
        // pitch 144, and a 24 px ResetGap that makes the last step 168.
        Check.That(lefts).ContainsExactly(12.0, 156.0, 300.0, 444.0, 588.0, 732.0, 900.0);
        Check.That(canvas.Width).IsEqualTo(1032.0);
        Check.That(canvas.Height).IsEqualTo(144.0);
    }

    /// <summary>
    /// Reset is the one adjacency where an overshoot is unrecoverable (see
    /// <see cref="ToolbarBand.ResetId"/>'s doc comment). Four properties must all hold at once:
    /// the face at that position IS the Reset disc, by identity, not merely "the last one by
    /// position" (a future reorder would otherwise fail silently at the wrong assertion); its rim
    /// is the warning hue and ONLY that disc's rim is (the complement — otherwise this test would
    /// stay green even if every rim, or none, went warning-coloured); the gap that precedes it is
    /// wider than every other gap by exactly <see cref="ToolbarBand.ResetGap"/>, not merely wider
    /// (a bare inequality would pass on a one-pixel widening); and the separator <see cref="Line"/>
    /// drawn in that gap sits at its true midpoint, not at the pre-gap centre — an earlier draft
    /// derived the rule from `along` before <c>ResetGap</c> was added and put it 48 px inside the
    /// Reset disc, which a count-only assertion could not see.
    /// </summary>
    [StaFact]
    public void Reset_CarriesTheWarningHue_ClearanceAndSeparator()
    {
        var window = NewInitialisedToolbar();
        var faces = Faces(window).OrderBy(Canvas.GetLeft).ToList();
        var reset = faces.Last();

        // Identity, not position: EveryAutomationAnchor_CoversItsOwnDisc pins that Reset is last
        // by id order today, but that is a cross-test dependency invisible from here. This makes a
        // future reorder fail on identity rather than surface as a confusing rim-colour mismatch.
        Check.WithCustomMessage("The last face by position is not the Reset disc.")
            .That(reset).IsSameReferenceAs(window.ButtonForTest(ToolbarBand.ResetId).Background);

        Check.That(((SolidColorBrush)reset.Stroke).Color).IsEqualTo(Tokens.AccentWarning);

        // The complement: EveryButton_CarriesAVisibleRim above accepts BorderSubtle OR
        // AccentWarning for every face, so it stays green even if none, or all six, carried the
        // warning hue. This is what actually pins that Reset is the ONLY one.
        foreach (var f in faces.Take(faces.Count - 1))
            Check.WithCustomMessage("An ordinary button carries the warning rim meant only for Reset.")
                .That(((SolidColorBrush)f.Stroke).Color).IsEqualTo(Tokens.BorderSubtle);

        var lefts = faces.Select(Canvas.GetLeft).ToList();
        var gaps = lefts.Zip(lefts.Skip(1), (a, b) => b - a).ToList();

        // Fully determined by the layout constants — internal, not private, on ToolbarWindow
        // specifically so this can reference them rather than keep a second, independently-
        // driftable copy of 12 (EyeCtrl.App.csproj:61 already grants EyeCtrl.App.Tests
        // InternalsVisibleTo). Not a bare "wider than" (that would pass on a one-pixel widening).
        var ordinaryPitch = ButtonSize + ToolbarBand.ButtonMargin * 2;
        var resetGap = ToolbarBand.ButtonMargin * 2;

        foreach (var gap in gaps.Take(gaps.Count - 1))
            Check.WithCustomMessage("An ordinary gap is not the plain button pitch.")
                .That(gap).IsEqualTo(ordinaryPitch);
        Check.WithCustomMessage("The gap before Reset is not exactly one ResetGap wider than the others.")
            .That(gaps.Last()).IsEqualTo(ordinaryPitch + resetGap);

        var rule = ButtonCanvas(window).Children.OfType<Line>().Single();
        var prevEdge = Canvas.GetLeft(faces[^2]) + ButtonSize;
        var resetEdge = Canvas.GetLeft(reset);

        // Position, not just existence: deriving the rule from the pre-gap centre put it 48 px
        // inside the Reset disc, and a count-only assertion was blind to it.
        Check.WithCustomMessage($"The rule is at {rule.X1}, outside the gap {prevEdge}..{resetEdge}.")
            .That(rule.X1 > prevEdge && rule.X1 < resetEdge).IsTrue();
        Check.That(rule.X1).IsCloseTo((prevEdge + resetEdge) / 2, 0.5);
    }

    /// <summary>
    /// <c>ToolbarBand.ArcBrushFor</c> and <c>ToolbarBand.IconBrushFor</c> both special-case
    /// Reset the same way <c>ToolbarBand.RimFor</c> does, and nothing pinned either before this
    /// test. The Reset icon (<c>ToolbarIcons.cs</c>'s <c>ResetAdaptation</c> entry) is stroke-only
    /// (<c>IconGeometry.FromPathData</c> called with a single argument), so its warning colour
    /// shows up on <see cref="DwellArcButton.IconStroke"/>, never <see cref="DwellArcButton.IconFill"/>.
    /// </summary>
    [StaFact]
    public void Reset_ArcAndIconBrushes_AlsoCarryTheWarningHue()
    {
        var window = NewInitialisedToolbar();
        var reset = window.ButtonForTest(ToolbarBand.ResetId);

        Check.WithCustomMessage("Reset's progress arc is not warning-coloured.")
            .That(((SolidColorBrush)reset.ProgressPath.Stroke).Color).IsEqualTo(Tokens.AccentWarning);

        Check.WithCustomMessage("Reset's icon has no stroke layer to carry the warning hue.")
            .That(reset.IconStroke).IsNotNull();
        Check.WithCustomMessage("Reset's icon is not warning-coloured.")
            .That(((SolidColorBrush)reset.IconStroke!.Stroke).Color).IsEqualTo(Tokens.AccentWarning);

        // Complement: an ordinary button (Pause) must NOT have picked up the warning hue either —
        // otherwise ArcBrushFor/IconBrushFor could have gone unconditionally warning and this test
        // would still be green. Pause's icon (ToolbarIcons.cs's Pause entry) is FILL-only
        // (FromPathData called with a null stroke), the opposite layer from Reset's, so its colour
        // shows up on IconFill, not IconStroke.
        var pause = window.ButtonForTest("Pause");
        Check.WithCustomMessage("Pause's progress arc picked up Reset's warning colour.")
            .That(((SolidColorBrush)pause.ProgressPath.Stroke).Color).IsEqualTo(Tokens.Accent);
        Check.WithCustomMessage("Pause's icon has no fill layer to check.")
            .That(pause.IconFill).IsNotNull();
        Check.WithCustomMessage("Pause's icon picked up Reset's warning colour.")
            .That(((SolidColorBrush)pause.IconFill!.Fill).Color).IsEqualTo(Tokens.TextPrimary);
    }

    [StaFact]
    public void DwellProgress_DrivesTheArc_AndIsInvisibleAtRest()
    {
        var (window, vm) = NewBoundToolbar();

        // Seven buttons x (halo + progress arc) = 14 Paths, ALL collapsed at rest. Exact, not
        // "at least one": a 1-of-14 bar would still pass if the wiring collapsed only whichever
        // button happened to run last, leaving the other six arcs visible at zero length.
        var arcs = ButtonCanvas(window).Children.OfType<Path>()
            .Where(p => p.Visibility == Visibility.Collapsed).ToList();
        Check.WithCustomMessage("All fourteen halo/progress Paths must be collapsed at rest.")
            .That(arcs).HasSize(14);

        var pause = window.ButtonForTest("Pause");
        var keyboard = window.ButtonForTest("Keyboard");
        vm.SimulateTargetProgress("Pause", 0.5);

        Check.WithCustomMessage("Dwell progress did not reach the arc.")
            .That(pause.ProgressPath.Visibility).IsEqualTo(Visibility.Visible);
        Check.WithCustomMessage("Dwell progress did not reach the halo.")
            .That(pause.HaloPath.Visibility).IsEqualTo(Visibility.Visible);
        Check.WithCustomMessage("A sibling button's arc moved along with Pause's.")
            .That(keyboard.ProgressPath.Visibility).IsEqualTo(Visibility.Collapsed);
    }

    /// <summary>
    /// Pins the outcome Task 4 owns beyond its literal steps: a rebuild must reapply live
    /// view-model state, not constructor defaults. RebuildBand now calls BindToViewModel itself
    /// (its trailing comment explains why: for an unchanged IconKey/pause state, nothing else
    /// would ever repair the rebuilt band), and SetMode's axis flip is one caller of RebuildBand.
    /// This drives that exact path — a real axis-flipping SetMode call, not a synthetic rebind —
    /// and checks both halves of the regression the plan named: the Pause icon (via geometry
    /// reference-identity) and the SelectionMode tint.
    /// </summary>
    [StaFact]
    public void BindToViewModel_ReappliesLiveState_AfterSetModeRebuildsTheBand()
    {
        var (window, vm) = NewBoundToolbar();

        // Drive the ViewModel to a non-default state BEFORE the rebuild: paused (Pause -> Play
        // icon) and a non-None selection mode (a distinct fill on the SelectionMode disc).
        vm.Receive(new StateChangedMessage(InteractionState.Paused));
        vm.Receive(new SelectionModeChangedMessage(SelectionMode.ManualSelection));

        // SetMode's axis flip (docked left) rebuilds the whole band via RebuildBand(), which now
        // rebinds itself. If that rebind reapplied constructor defaults instead of live state
        // (the old _boundIds no-op bug), the rebuilt band would keep the neutral Pause icon and
        // neutral SelectionMode fill until the next PropertyChanged fired — which is the "Pause
        // icon on a paused app" bug this task fixes.
        window.SetMode(isDocked: true, edge: "left");

        Check.WithCustomMessage("SelectionMode disc kept its neutral fill after a rebuild+rebind; "
                + "live state (ManualSelection) was not reapplied.")
            .That(((SolidColorBrush)window.ButtonForTest("SelectionMode").Background.Fill).Color)
            .IsEqualTo(Tokens.ToggleOn);

        // The icon IS directly assertable: ToolbarIcons.For returns a shared, frozen
        // IconGeometry, and DwellArcButton.Create's BuildLayer assigns it to Path.Data BY
        // REFERENCE (DwellArcButton.cs:290-309), never cloning it. So the rebuilt Pause button
        // carries the Play fill geometry by reference if — and only if — the rebind re-ran
        // BindButton's initial-apply SwapIcon with the live (paused) IconKey.
        Check.WithCustomMessage("Pause kept its Pause glyph after a rebuild+rebind; the app is paused.")
            .That(ButtonCanvas(window).Children.OfType<Path>()
                .Any(p => ReferenceEquals(p.Data, ToolbarIcons.For(ToolbarIconKey.Play).Fill))).IsTrue();
    }

    /// <summary>
    /// Pins the OTHER path into RebuildBand: a plain size change, which never touches SetMode's
    /// axis flip (_vertical stays false throughout). SettingsUiCoordinator.cs:179 re-calls
    /// Initialize on every toolbar-size save, docked or not — before RebuildBand rebound itself,
    /// that call rebuilt the band from constructor defaults and nothing downstream ever repaired
    /// it, since IconKey re-assigning its already-current value raises no PropertyChanged.
    /// </summary>
    [StaFact]
    public void BindToViewModel_ReappliesLiveState_AfterASizeChangeRebuildsTheBand()
    {
        var (window, vm) = NewBoundToolbar();

        vm.Receive(new StateChangedMessage(InteractionState.Paused));

        // A size save re-calls Initialize (SettingsUiCoordinator.cs:179), which rebuilds the band
        // via RebuildBand without going through SetMode's axis-flip branch at all.
        window.Initialize(ButtonSize + 10, 700);

        Check.WithCustomMessage("Pause showed its Pause glyph after a size-change rebuild although "
                + "the app is paused — RebuildBand did not reapply live state.")
            .That(ButtonCanvas(window).Children.OfType<Path>()
                .Any(p => ReferenceEquals(p.Data, ToolbarIcons.For(ToolbarIconKey.Play).Fill))).IsTrue();
    }

    [StaFact]
    public void AcquiredState_LiftsTheFillAndTheRim()
    {
        var (window, vm) = NewBoundToolbar();

        var button = vm.Buttons.Single(b => b.Id == "Pause");
        // Identified by name, not by position: the other three tests in this group all use
        // ButtonForTest, and Faces(...).OrderBy(Canvas.GetLeft).First() would silently start
        // asserting about a different button if a future task (e.g. Task 7) reorders the band.
        var face = window.ButtonForTest("Pause").Background;

        Check.WithCustomMessage("Pause's face was not at rest (SurfaceMedium) before any gaze.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);

        button.IsHighlighted = true;
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceLight);
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderFocus);

        button.IsHighlighted = false;
        Check.That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceMedium);
    }

    // Reset's rim is the one visual marker on the only unrecoverable action (Task 7). Acquiring
    // and releasing it must not leave the ordinary BorderSubtle rim behind — that would silently
    // erase the warning colour the moment a user's gaze passes over the button and away again.
    [StaFact]
    public void AcquiredState_OnReset_RestoresTheWarningRimNotTheOrdinaryOne()
    {
        var (window, vm) = NewBoundToolbar();

        var button = vm.Buttons.Single(b => b.Id == "ResetAdaptation");
        var face = window.ButtonForTest("ResetAdaptation").Background;

        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentWarning);

        button.IsHighlighted = true;
        Check.That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderFocus);

        button.IsHighlighted = false;
        Check.WithCustomMessage("Reset's warning rim did not return after the acquired state was released.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.AccentWarning);
    }

    // Task 3/5 interaction: SetSelectionModeState is called BEFORE Initialize on a size-change
    // save (SettingsUiCoordinator.cs:172-179). Passing fill: FillFor(id) at construction time is
    // what makes the mode tint survive that rebuild regardless of call order, rather than relying
    // solely on BindToViewModel's rebind racing the same rebuild.
    [StaFact]
    public void SelectionModeTint_SurvivesAReinitializeCalledBeforeTheNextBind()
    {
        var window = NewInitialisedToolbar();

        window.SetSelectionModeState(SelectionMode.ManualSelection);
        window.Initialize(ButtonSize, 700);

        Check.WithCustomMessage("SelectionMode disc lost its mode tint after a bare reinitialize.")
            .That(((SolidColorBrush)window.ButtonForTest("SelectionMode").Background.Fill).Color)
            .IsEqualTo(Tokens.ToggleOn);
    }

    // A rebuild (axis flip or size-change save) replaces the button's Ellipse via SwapIcon, which
    // starts fresh from FillFor/RimFor (rest state). Nothing else repairs IsHighlighted afterward:
    // the next non-zero TargetProgress assigns IsHighlighted = true when it is already true, and
    // [ObservableProperty] raises nothing for an unchanged value, so the disc would stay at rest
    // for the remainder of that dwell. BindButton's initial-apply block must reapply it, the same
    // way it already reapplies IconKey/mode-tint and DwellProgress.
    [StaFact]
    public void AcquiredState_SurvivesARebuild()
    {
        var (window, vm) = NewBoundToolbar();

        var button = vm.Buttons.Single(b => b.Id == "Pause");
        button.IsHighlighted = true;

        // A plain size-change rebuild (SettingsUiCoordinator.cs:179 path), not the axis flip.
        window.Initialize(ButtonSize, 700);

        var face = window.ButtonForTest("Pause").Background;
        Check.WithCustomMessage("Pause lost its acquired fill across a rebuild although gaze never left it.")
            .That(((SolidColorBrush)face.Fill).Color).IsEqualTo(Tokens.SurfaceLight);
        Check.WithCustomMessage("Pause lost its acquired rim across a rebuild although gaze never left it.")
            .That(((SolidColorBrush)face.Stroke).Color).IsEqualTo(Tokens.BorderFocus);
    }

    // Ordering guard: ApplyAcquiredState must run AFTER SetSelectionModeState in BindButton's
    // initial-apply block, because SetSelectionModeState writes Background.Fill unconditionally.
    // Reversed, an acquired SelectionMode button's SurfaceLight fill would be clobbered back to
    // the mode tint on every rebuild.
    [StaFact]
    public void AcquiredState_OnSelectionMode_SurvivesARebuild_NotClobberedByTheModeTint()
    {
        var (window, vm) = NewBoundToolbar();

        vm.Receive(new SelectionModeChangedMessage(SelectionMode.ManualSelection));
        var button = vm.Buttons.Single(b => b.Id == "SelectionMode");
        button.IsHighlighted = true;

        window.Initialize(ButtonSize, 700);

        Check.WithCustomMessage("SelectionMode's acquired fill was clobbered by the mode tint on rebuild — "
                + "ApplyAcquiredState must run after SetSelectionModeState in BindButton.")
            .That(((SolidColorBrush)window.ButtonForTest("SelectionMode").Background.Fill).Color)
            .IsEqualTo(Tokens.SurfaceLight);
    }

    // Task 6: the provider indicator (an eye/mouse glyph showing where gaze comes from) must not
    // read as a dwellable button, and setting it twice must not stack a second one. Not a live
    // reproduction today — OverlayManager.cs:107-108 is the one real caller, and it calls
    // Initialize once then SetProviderIndicator once, never SetProviderIndicator twice on the
    // same window — this exercises the guard directly, as a code property.
    [StaFact]
    public void TheProviderIndicator_IsNotInTheBand_AndNeverDuplicates()
    {
        var window = NewInitialisedToolbar();

        window.SetProviderIndicator(GazeProviderType.StreamEngine);
        Check.WithCustomMessage("The indicator must not add a face to the button band.")
            .That(Faces(window)).HasSize(7);
        var afterOne = window.IndicatorPanel.Children.Count;
        Check.WithCustomMessage("The indicator drew nothing at all — this test would pass even "
                + "if AddIndicatorElement were a no-op.")
            .That(afterOne).IsStrictlyGreaterThan(0);

        window.SetProviderIndicator(GazeProviderType.Mouse);
        Check.WithCustomMessage("Setting the indicator twice appended a second one.")
            .That(window.IndicatorPanel.Children.Count).IsEqualTo(afterOne);
    }

    // The indicator must never masquerade as a dwell target: no Ellipse, ever, inside
    // IndicatorPanel — Faces(window) staying at 6 (asserted above too) is the band-side half of
    // this; this is the indicator-side half.
    [StaFact]
    public void TheIndicator_ContainsNoEllipse()
    {
        var window = NewInitialisedToolbar();
        window.SetProviderIndicator(GazeProviderType.StreamEngine);

        Check.WithCustomMessage("The indicator drew something that looks like a dwell target.")
            .That(window.IndicatorPanel.Children.OfType<Ellipse>()).IsEmpty();
    }

    /// <summary>
    /// The switch that maps a <see cref="GazeProviderType"/> to a <see cref="ToolbarIconKey"/> is
    /// the only logic in <c>AddIndicatorElement</c>, and Task 6 rewrote it (an emoji string per
    /// case became an icon-key lookup). <see cref="ToolbarIcons.For"/> returns a geometry from a
    /// static, shared dictionary, so the drawn <see cref="Path.Data"/> is the SAME
    /// <see cref="Geometry"/> instance as <see cref="ToolbarIcons.For"/> for the expected key —
    /// reference equality proves which arm of the switch actually ran, rather than merely that
    /// SOME icon was drawn.
    /// </summary>
    [StaFact]
    public void TheProviderIndicator_DrawsTheIconMatchingItsProviderType()
    {
        var window = NewInitialisedToolbar();

        window.SetProviderIndicator(GazeProviderType.Mouse);
        Check.WithCustomMessage("Mouse must draw the mouse icon, not the eye.")
            .That(window.IndicatorPanel.Children.OfType<Path>().Single().Data)
            .IsSameReferenceAs(ToolbarIcons.For(ToolbarIconKey.ProviderMouse).Stroke);

        window.SetProviderIndicator(GazeProviderType.StreamEngine);
        Check.WithCustomMessage("StreamEngine must draw the eye icon, not the mouse.")
            .That(window.IndicatorPanel.Children.OfType<Path>().Single().Data)
            .IsSameReferenceAs(ToolbarIcons.For(ToolbarIconKey.ProviderEye).Stroke);
    }

    // The structural property this task exists to protect: the band's own geometry (button
    // centres and Canvas extent) is computed entirely in LayOutBand from _buttonSize/ButtonMargin
    // and does not depend on IndicatorPanel at all. Compared against a second, indicator-free
    // window rather than re-pinning the same constants as ButtonCentres_AndCanvasSize_
    // AreExactlyAsLaidOut: a shared-constants copy would fail identically on, say, a ButtonMargin
    // edit, and neither run would point at the indicator as the thing under test.
    [StaFact]
    public void ButtonCentres_AndCanvasSize_AreUnaffectedByTheIndicator()
    {
        var withoutIndicator = NewInitialisedToolbar();
        var withIndicator = NewInitialisedToolbar();
        withIndicator.SetProviderIndicator(GazeProviderType.StreamEngine);

        var leftsWithout = Faces(withoutIndicator).Select(Canvas.GetLeft).OrderBy(x => x).ToList();
        var leftsWith = Faces(withIndicator).Select(Canvas.GetLeft).OrderBy(x => x).ToList();

        Check.WithCustomMessage("Setting the provider indicator moved the button band.")
            .That(leftsWith).ContainsExactly(leftsWithout);
        Check.That(ButtonCanvas(withIndicator).Width).IsEqualTo(ButtonCanvas(withoutIndicator).Width);
        Check.That(ButtonCanvas(withIndicator).Height).IsEqualTo(ButtonCanvas(withoutIndicator).Height);
    }

    /// <summary>
    /// Task 3 gave IndicatorPanel a Margin, which offset the whole strip even with no provider
    /// set (an empty StackPanel's CONTENT measures to zero, but a Margin on the panel itself does
    /// not). Fixed by moving the spacing onto the indicator's own children, so an absent indicator
    /// is genuinely zero-cost: left inset should be Border.Padding.Left (4) + IndicatorPanel's own
    /// footprint (0, empty) + the band's ButtonMargin (12) = 16, matching the 16 on the right
    /// (Border.Padding.Right + ButtonMargin).
    ///
    /// Deliberately <see cref="UIElement.DesiredSize"/>, NOT <see cref="FrameworkElement.ActualWidth"/>:
    /// ActualWidth is RenderSize, which EXCLUDES Margin, so a regression that restores
    /// IndicatorPanel's old Margin="8,0,4,0" would still measure ActualWidth as 0 and this test
    /// would report the strip symmetric while it visibly is not — the exact defect this test
    /// exists to catch. DesiredSize.Width DOES include Margin (0 clean, 12 with that Margin
    /// restored), which is what makes the assertion below actually fail on that regression.
    ///
    /// Read via DesiredSize/Canvas.GetLeft after a manual Measure/Arrange pass on the root Border
    /// — NOT via TranslatePoint against the Window itself, which returns (0,0) pre-Show() per this
    /// class's header comment. Measuring a descendant's own layout properties (unlike transforming
    /// between two points via the unconnected Window) works fine headless, as
    /// SettingsContentFitTests already relies on.
    /// </summary>
    [StaFact]
    public void StripInsets_AreSymmetric_WhenNoProviderIsSet()
    {
        var window = NewInitialisedToolbar();
        // window.Content is the clipping Grid that carries the slide transform; the strip itself
        // (and its Padding, which the inset arithmetic below needs) is the named Border inside it.
        var root = window.StripRoot;
        root.Measure(new Size(4000, 4000));
        root.Arrange(new Rect(0, 0, 4000, 4000));
        root.UpdateLayout();

        var canvas = ButtonCanvas(window);
        var faces = Faces(window).OrderBy(Canvas.GetLeft).ToList();

        var leftInset = root.Padding.Left + window.IndicatorPanel.DesiredSize.Width + Canvas.GetLeft(faces.First());
        var rightInset = root.Padding.Right + (canvas.Width - (Canvas.GetLeft(faces.Last()) + ButtonSize));

        Check.WithCustomMessage($"Left inset {leftInset} != right inset {rightInset} — an empty "
                + "IndicatorPanel is not genuinely zero-cost.")
            .That(leftInset).IsEqualTo(rightInset);
    }

    // Orientation: the rule between the indicator's glyph and the band must read as a hairline
    // ACROSS the axis of travel, not along it — thin-and-tall when the strip runs horizontally,
    // wide-and-thin once SetMode docks it to a side edge and the strip turns vertical. Driving
    // SetMode headlessly is not awkward here: BindToViewModel_ReappliesLiveState_AfterSetMode...
    // above already exercises the same real axis-flipping call with no Show() involved.
    [StaFact]
    public void TheIndicatorRule_SwapsDimensions_WhenDockedVertically()
    {
        // Both values are named constants, not "whichever is bigger": an ordering assertion alone
        // would pass on any (small, large) pair, e.g. (2, 1), and miss a regression that shrank or
        // grew the wrong dimension by an arbitrary amount as long as the order held.
        var thin = Tokens.BorderThicknessStandard;
        var wide = ButtonSize * ContainerRatio.Dominant;

        var window = NewInitialisedToolbar();
        window.SetProviderIndicator(GazeProviderType.StreamEngine);

        var ruleHorizontal = window.IndicatorPanel.Children.OfType<Border>().Single();
        Check.WithCustomMessage("The rule should be thin-and-tall while the strip is horizontal.")
            .That(ruleHorizontal.Width).IsEqualTo(thin);
        Check.That(ruleHorizontal.Height).IsEqualTo(wide);

        window.SetMode(isDocked: true, edge: "left");

        var ruleVertical = window.IndicatorPanel.Children.OfType<Border>().Single();
        Check.WithCustomMessage("The rule should be wide-and-thin once the strip is vertical.")
            .That(ruleVertical.Width).IsEqualTo(wide);
        Check.That(ruleVertical.Height).IsEqualTo(thin);
    }

    /// <summary>
    /// Both hairlines — the indicator's rule and the Reset separator's <see cref="Line"/> — must
    /// use <see cref="Tokens.BrushBorderSubtle"/>, never <see cref="Tokens.BrushSurfaceMedium"/>.
    /// The canonical contrast figures for this pair on <see cref="Tokens.SurfaceDark"/> — 1.23 /
    /// 1.23 / 1.14 for SurfaceMedium, 3.35 / 3.32 / 21.00 for BorderSubtle (Midnight / Ember /
    /// Contrast) — already live in <c>DwellToggleVisualTests.cs</c>,
    /// <c>SliderStepperContrastTests.cs</c> and <see cref="EyeCtrl.Controls.DwellSlider"/>; not
    /// repeated here. <see cref="EveryButton_CarriesAVisibleRim"/> above already asserts
    /// SurfaceMedium alone is invisible as a boundary; without this guard, a future "the divider
    /// looks too loud" cleanup could revert either site back to it with nothing to object.
    ///
    /// A token-identity check, not a live per-theme contrast measurement: the floor itself is
    /// already gated outside this suite, by <c>tools/palette_validate.py:358</c> ("GATE 1 —
    /// layering"), which checks <c>BorderSubtle</c> against the worst composited surface in every
    /// theme and runs in CI. What that gate cannot see is which BRUSH this control reaches for —
    /// it would pass unchanged if this method used SurfaceMedium instead — and that choice is
    /// exactly what this test pins.
    /// </summary>
    [StaFact]
    public void BothHairlines_UseBorderSubtle_NotSurfaceMedium()
    {
        var window = NewInitialisedToolbar();
        window.SetProviderIndicator(GazeProviderType.StreamEngine);

        var separator = ButtonCanvas(window).Children.OfType<Line>().Single();
        Check.WithCustomMessage("The Reset separator must use BorderSubtle, not SurfaceMedium.")
            .That(((SolidColorBrush)separator.Stroke).Color).IsEqualTo(Tokens.BorderSubtle);

        var rule = window.IndicatorPanel.Children.OfType<Border>().Single();
        Check.WithCustomMessage("The indicator's rule must use BorderSubtle, not SurfaceMedium.")
            .That(((SolidColorBrush)rule.Background).Color).IsEqualTo(Tokens.BorderSubtle);
    }
}
