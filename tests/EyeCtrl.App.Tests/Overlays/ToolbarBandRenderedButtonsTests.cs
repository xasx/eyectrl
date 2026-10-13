namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Adaptation;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Task 8: the band→view-model seam and the D18 frozen-silent rim cue, driven through the real
/// <see cref="ToolbarWindow"/>.
///
/// <para>
/// The seam: <c>ToolbarWindow.RebuildBand</c> reports the ids the band now renders to
/// <see cref="ToolbarViewModel.SetRenderedButtons"/>, so an Enabled flip that drops Reset (Task 7)
/// also removes its dwell target, zeroes a held arc and closes any live preview engagement — the
/// view-model learns the band's membership from the only component that knows it. A stale target
/// at a former screen position could otherwise fire the one unrecoverable action from gaze merely
/// passing there (P7/D11).
/// </para>
///
/// <para>
/// The cue (R2-3/D18): while an engagement is live but no panel is shown, the Reset disc carries a
/// doubled rim. It is a THICKNESS cue, not a colour cue — the freeze holds
/// <c>IsHighlighted = true</c>, so the frozen face is the ACQUIRED face
/// (<see cref="Tokens.BrushBorderFocus"/> rim, <see cref="Tokens.BrushSurfaceLight"/> fill) and the
/// doubled stroke is the only thing that says "something happened".
/// </para>
///
/// <para>
/// Geometry: <see cref="ToolbarTestHelpers.ButtonSize"/> (120 DIP) at scale 1.0 gives a 60 px hit
/// radius. Reset sits at (500,500); Away (1000,300) is ~538 px out, beyond any grown radius. Every
/// gaze sample carries an explicit timestamp (P4) at a 30 ms cadence — the DwellDetector's gap
/// clamp restarts a dwell on a single step of 150 ms or more, so sparse samples do not accumulate.
/// </para>
///
/// <para>
/// Fixture policy (R2-M3): band membership is driven ONLY through Initialize's
/// <c>includeReset</c> flag, never <c>AdaptationSettings.Enabled</c> — in the launched app that
/// setting starts the whole adaptation runtime, which is not what a window test means to turn on.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ToolbarBandRenderedButtonsTests
{
    private const string ResetId = ResetPreviewEngine.ResetId;

    /// <summary>Where the tests place Reset's dwell target (the VM-level position report).</summary>
    private static readonly PhysicalPoint ResetCenter = new(500, 500);

    /// <summary>Far from every target, so a sample here accumulates nothing.</summary>
    private static readonly PhysicalPoint Away = new(1000, 300);

    /// <summary>
    /// The reported panel rect for the P16 flow: top-left corner near Reset, so the union is
    /// well-formed; its exact shape is not what that test pins.
    /// </summary>
    private static readonly PhysicalRect PanelRect = new(540, 470, 200, 130);

    /// <summary>
    /// As <see cref="ToolbarTestHelpers.NewBoundToolbar"/>, but in PRODUCTION order: DataContext
    /// BEFORE <see cref="ToolbarWindow.Initialize"/> (OverlayManager.cs sets the toolbar's
    /// DataContext ~line 131 and calls Initialize ~line 187). The order matters now — the seam
    /// lives in <c>RebuildBand</c>, which reads <c>DataContext</c>, so binding first is what lets
    /// even the initial build report. The shared helper inverts the two (it predates the seam);
    /// re-ordering it would silently change what every existing test's first build reports, so
    /// this local helper carries the production order instead.
    /// </summary>
    private static (ToolbarWindow Window, ToolbarViewModel Vm) BoundToolbar(
        WeakReferenceMessenger messenger, bool includeReset, Func<bool>? isFeedbackSuppressed = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ToolbarWindow();
        var vm = new ToolbarViewModel(new EyeCtrl.Core.Settings.EyeCtrlSettings(), messenger,
            isFeedbackSuppressed: isFeedbackSuppressed);
        window.DataContext = vm;
        // Initialize → RebuildBand → Build + BindToViewModel + the rendered-buttons report.
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset);
        return (window, vm);
    }

    /// <summary>
    /// Streams gaze at a 30 ms cadence, inclusive of <paramref name="toUs"/> (the same discipline
    /// as the ViewModels suite's DwellDrive, which is internal to that assembly).
    /// </summary>
    private static void Hold(ToolbarViewModel vm, PhysicalPoint at, long fromUs, long toUs)
    {
        for (var t = fromUs; t <= toUs; t += 30_000)
            vm.Receive(new GazeCursorMovedMessage(at, t));
    }

    private static ToolbarButtonViewModel ResetButton(ToolbarViewModel vm) =>
        vm.Buttons.Single(b => b.Id == ResetId);

    private static Color FillOf(System.Windows.Shapes.Ellipse face) =>
        ((SolidColorBrush)face.Fill).Color;

    private static Color StrokeOf(System.Windows.Shapes.Ellipse face) =>
        ((SolidColorBrush)face.Stroke).Color;

    // ────── The seam: removal + the dead-button regression ──────

    /// <summary>
    /// P7/D11, the dead-button regression: <c>SetRenderedButtons</c> must remove the target AND
    /// the positioned-set entry. If the entry survived the removal, the first position report
    /// after a re-enable would take the UpdateTargetCenter branch — which moves nothing, because
    /// the target is gone — and Reset would be a dead disc for the rest of the session: drawn,
    /// dwellable-looking, firing nothing. The re-dwell at the end is what proves the AddTarget
    /// branch ran.
    /// </summary>
    [StaFact]
    public void Disable_RemovesTargetAndSetEntry_ReenableAndReposition_DwellsAgain()
    {
        var messenger = new WeakReferenceMessenger();
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var (window, vm) = BoundToolbar(messenger, includeReset: true);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);

        // Mid-dwell, below the 0.40 freeze threshold (~0.3 at 210 of 700 ms) — sanity first that
        // it WAS dwelling, else the rest of the test proves nothing.
        Hold(vm, ResetCenter, 0, 210_000);
        Check.That(ResetButton(vm).DwellProgress).IsStrictlyGreaterThan(0.0);
        Check.That(ResetButton(vm).IsHighlighted).IsTrue();

        // The Enabled flip: a rebuild WITHOUT Reset reports the shorter band through the window's
        // seam (SettingsUiCoordinator re-calls Initialize with the live flag — Task 9's wiring;
        // here the flag is driven directly, fixture policy R2-M3).
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: false);

        Check.WithCustomMessage("The held arc survived the removal — SetRenderedButtons did not zero it.")
            .That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);
        Check.WithCustomMessage("The highlight survived the removal.")
            .That(ResetButton(vm).IsHighlighted).IsFalse();

        // The target is gone: resting exactly where Reset used to be for well past the full dwell
        // time fires nothing.
        Hold(vm, ResetCenter, 240_000, 1_410_000);
        Check.WithCustomMessage("Reset fired from a removed target — a stale target outlived the band.")
            .That(resets).IsEqualTo(0);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);

        // Re-enable: the rebuild reports the seven-button band, and the NEXT position report must
        // take the AddTarget branch (the set entry went with the target) — proven by the dwell
        // response below: without a recreated target, progress cannot move at all.
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: true);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);

        Hold(vm, ResetCenter, 1_500_000, 1_860_000);   // 360 ms ≥ the 0.40 freeze point

        var progress = ResetButton(vm).DwellProgress;
        Check.WithCustomMessage(
                $"Reset dwelled to {progress:F2} after the re-enable — expected a fresh freeze "
                + "engagement just past 0.40. Progress never moving means the position report "
                + "took the UpdateTargetCenter branch: the positioned-set entry outlived the "
                + "removed target and no new target was created (the dead-button bug).")
            .That(progress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(progress).IsStrictlyLessThan(0.60);   // frozen, not run to a fire
        Check.That(ResetButton(vm).IsHighlighted).IsTrue();
        Check.That(resets).IsEqualTo(0);                 // the freeze is not a fire
    }

    /// <summary>
    /// Stuck-arc clearing against the case that makes it non-trivial: while frozen, the engine
    /// HOLDS the arc against everything (P6 — even the leave run's progress-0 must not drop it,
    /// because the run may cancel back). A removal is the one thing that must cut through: an arc
    /// frozen at ~0.45 on a disc that is no longer rendered would hang over nothing forever.
    /// </summary>
    [StaFact]
    public void FrozenHeldArc_AndHighlight_AreZeroedByTheRemovalThroughTheSeam()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var (window, vm) = BoundToolbar(messenger, includeReset: true);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);

        Hold(vm, ResetCenter, 0, 350_000);   // freeze engages, panel requested
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true);
        var btn = ResetButton(vm);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.IsHighlighted).IsTrue();

        // The P6 contrast — one off-target sample's progress-0 does NOT drop the held arc…
        Hold(vm, Away, 380_000, 380_000);
        Check.That(btn.DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(btn.IsHighlighted).IsTrue();

        // …but the removal through the window's seam does, and it retracts the panel too.
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: false);

        Check.That(btn.DwellProgress).IsEqualTo(0.0);
        Check.That(btn.IsHighlighted).IsFalse();
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);
        Check.That(resets).IsEqualTo(0);     // closing the engagement is not a fire
    }

    // ────── The D18 rim cue, against the acquired face (R2-3) ──────

    /// <summary>
    /// R2-3, through the real path: the cue is driven by the engine's suppressed-at-engage branch
    /// (P15/L1) — no panel ever exists, so the doubled rim is the ONLY evidence the freeze
    /// happened, which is exactly the condition D18 exists for.
    ///
    /// <para>
    /// The assertions are against the ACQUIRED face, because that is what the user actually sees
    /// while frozen: same <see cref="Tokens.BrushBorderFocus"/> rim, same
    /// <see cref="Tokens.BrushSurfaceLight"/> fill — the ONLY difference is the stroke thickness,
    /// asserted as the actual values 4 vs 2 (a bare "differs" would pass on any regression that
    /// changed BOTH faces).
    /// </para>
    /// </summary>
    [StaFact]
    public void FrozenSilentCue_IsTheAcquiredFace_WithADoubledRim_RestoredOnCueEnd()
    {
        bool suppress = true;
        var messenger = new WeakReferenceMessenger();
        var (window, vm) = BoundToolbar(messenger, includeReset: true, isFeedbackSuppressed: () => suppress);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);
        var face = window.ButtonForTest(ResetId).Background;

        // Acquired, no cue: 90 ms of rest is ~0.13 progress — above zero (highlight on), well
        // below the 0.40 freeze threshold.
        Hold(vm, ResetCenter, 0, 90_000);
        Check.That(vm.ResetFrozenSilent).IsFalse();
        Check.That(FillOf(face)).IsEqualTo(Tokens.SurfaceLight);
        Check.That(StrokeOf(face)).IsEqualTo(Tokens.BorderFocus);
        Check.WithCustomMessage("Acquired-without-cue must draw the DwellArcButton rest rim width.")
            .That(face.StrokeThickness).IsEqualTo(2.0);

        // Past the threshold while suppressed: freeze engages, no panel, cue on. Same colours —
        // the frozen face IS the acquired face — with the doubled stroke as the only difference.
        Hold(vm, ResetCenter, 120_000, 350_000);
        Check.That(vm.ResetFrozenSilent).IsTrue();
        Check.WithCustomMessage("The cue must not change the fill — the frozen face is the acquired face.")
            .That(FillOf(face)).IsEqualTo(Tokens.SurfaceLight);
        Check.WithCustomMessage("The cue must not change the rim colour — the frozen face is the acquired face.")
            .That(StrokeOf(face)).IsEqualTo(Tokens.BorderFocus);
        Check.WithCustomMessage("The frozen-silent cue must double the rim stroke (4 DIP, not 2).")
            .That(face.StrokeThickness).IsEqualTo(4.0);

        // Cue end: suppression ending restores nothing (L1) — the leave runs normally, arms, and
        // the rim returns to its rest width while the face is still acquired (P6 holds the arc
        // until the post-arm zero)…
        suppress = false;
        Hold(vm, Away, 380_000, 520_000);
        Check.That(vm.ResetFrozenSilent).IsFalse();
        Check.That(StrokeOf(face)).IsEqualTo(Tokens.BorderFocus);
        Check.WithCustomMessage("The cue end must restore the DwellArcButton rest rim width.")
            .That(face.StrokeThickness).IsEqualTo(2.0);

        // …and once the post-arm zero lands, Reset returns to its own REST face — warning rim,
        // ordinary fill — still at the rest width.
        vm.SimulateTargetProgress(ResetId, 0.0);
        Check.That(ResetButton(vm).IsHighlighted).IsFalse();
        Check.That(StrokeOf(face)).IsEqualTo(Tokens.AccentWarning);
        Check.That(FillOf(face)).IsEqualTo(Tokens.SurfaceMedium);
        Check.That(face.StrokeThickness).IsEqualTo(2.0);
    }

    /// <summary>
    /// The cue's adequacy half. The doubled rim is a WIDTH cue, but a width cue only reads if the
    /// stroke itself is visible against both surfaces it straddles: the acquired fill inside
    /// (<see cref="ITheme.SurfaceLight"/>) and the strip behind it
    /// (<see cref="ITheme.SurfaceDark"/> — ToolbarWindow.xaml's StripRoot). Gates the same
    /// max-neighbour ratio shape <c>DwellToggleVisualTests</c> gates fill-plus-outline elements
    /// with, at its 3:1 non-text-component bar.
    ///
    /// <para>
    /// Deliberately in THIS suite, not <c>EyeCtrl.Controls.Tests</c>: nothing here needs Controls
    /// internals — the cue's brushes are App-side tokens, and the cross-theme convention the App
    /// suite already uses (<c>ToolbarFaceContrastTests</c>: read each
    /// <see cref="ITheme"/> via <see cref="ThemeRegistry.All"/>, never activate) is reachable
    /// as-is. Computed from the token colours, never pasted in as expected numbers, so a palette
    /// edit that regresses the pairing fails this test rather than silently invalidating a table.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(AllThemes))]
    public void TheFrozenSilentRim_IsPerceptibleAgainstBothItsNeighbours_InEveryTheme(ITheme theme)
    {
        var onFill = Contrast(theme.BorderFocus, theme.SurfaceLight);
        Check.WithCustomMessage(
                $"{theme.Name}: the cue rim (BorderFocus) reaches only {onFill:F2}:1 against the "
                + "acquired fill it strokes over. Below 3:1 the doubled rim is a wider version of "
                + "an invisible line — the cue says nothing.")
            .That(onFill).IsStrictlyGreaterThan(3.0);

        var onStrip = Contrast(theme.BorderFocus, theme.SurfaceDark);
        Check.WithCustomMessage(
                $"{theme.Name}: the cue rim reaches only {onStrip:F2}:1 against the strip surface "
                + "behind it (SurfaceDark). The rim straddles the disc's edge; its outer half is "
                + "drawn over the strip, and below 3:1 there the doubling reads as flicker, not a "
                + "cue.")
            .That(onStrip).IsStrictlyGreaterThan(3.0);
    }

    public static TheoryData<ITheme> AllThemes()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All)
            data.Add(theme);
        return data;
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// Precedence, at the band (the component that owns <c>PaintFace</c>'s ternary chain): a flash
    /// during a live cue must paint the FLASH face — colours AND width — exactly as it beats
    /// acquired. Asserted at the band because the window-driven path cannot produce the pair: an
    /// activation routes through the engine FIRST (<c>ToolbarViewModel.OnTargetActivated</c> calls
    /// <c>_previewEngine.OnActivated</c> before raising <c>ButtonActivated</c>), so by the time
    /// the flash paints, the disarm has already turned the cue off and any window-level test
    /// would pass vacuously. Driving the band's own two inputs is the only way to observe the
    /// chain's order.
    /// </summary>
    [StaFact]
    public void Flashing_BeatsTheFrozenSilentCue_AndEndFlash_ReturnsTheCue()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var band = new ToolbarBand(new Canvas());
        band.Build(ToolbarTestHelpers.ButtonSize, vertical: false, includeReset: true);
        var face = band[ResetId].Background;

        // The frozen-silent face: acquired colours (the freeze holds the highlight) + the cue.
        band.SetAcquired(ResetId, true);
        band.SetFrozenSilent(ResetId, true);
        Check.That(face.StrokeThickness).IsEqualTo(4.0);

        // The flash outranks the cue: the face is the flash face in full — success fill, success
        // rim, REST width. A flash that kept the doubled width would stack the two loudest states
        // into one face that reads as neither.
        band.Flash(ResetId);
        Check.WithCustomMessage("A flash during a live cue must paint the flash fill.")
            .That(FillOf(face)).IsEqualTo(Tokens.SurfacePrediction);
        Check.WithCustomMessage("A flash during a live cue must paint the flash rim.")
            .That(StrokeOf(face)).IsEqualTo(Tokens.AccentSuccess);
        Check.WithCustomMessage("The flash must suspend the cue's doubled width, not paint over it.")
            .That(face.StrokeThickness).IsEqualTo(2.0);

        // And the flash does not consume the cue: ending it falls back to the frozen face, not to
        // rest — the engagement is still live.
        band.EndFlash(ResetId);
        Check.WithCustomMessage("Ending the flash dropped the cue although the engagement is still live.")
            .That(face.StrokeThickness).IsEqualTo(4.0);
        Check.That(StrokeOf(face)).IsEqualTo(Tokens.BorderFocus);
        Check.That(FillOf(face)).IsEqualTo(Tokens.SurfaceLight);
    }

    /// <summary>
    /// A rebuild mid-cue (an axis flip or size change — both rebuild every Ellipse at
    /// DwellArcButton's defaults) must not drop the cue: the engagement survives rebuilds, and
    /// the reader needs the doubled rim to keep saying "reading mode" across it. This pins the
    /// re-apply path without which the fresh Ellipse would sit at the rest width until the cue
    /// happened to toggle (an unchanged <c>ResetFrozenSilent</c> raises no PropertyChanged).
    /// </summary>
    [StaFact]
    public void TheCue_SurvivesABandRebuild()
    {
        bool suppress = true;
        var messenger = new WeakReferenceMessenger();
        var (window, vm) = BoundToolbar(messenger, includeReset: true, isFeedbackSuppressed: () => suppress);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);

        Hold(vm, ResetCenter, 0, 350_000);
        Check.That(vm.ResetFrozenSilent).IsTrue();
        var before = window.ButtonForTest(ResetId).Background;
        Check.That(before.StrokeThickness).IsEqualTo(4.0);

        // A plain size-change rebuild — the Initialize path SettingsUiCoordinator takes on every
        // toolbar-size save. Membership unchanged (still includeReset: true).
        window.Initialize(ToolbarTestHelpers.ButtonSize + 10, 700, includeReset: true);

        var after = window.ButtonForTest(ResetId).Background;
        Check.WithCustomMessage("The rebuild did not recreate the Ellipse — this test proved nothing.")
            .That(ReferenceEquals(before, after)).IsFalse();
        Check.WithCustomMessage(
                "A mid-cue rebuild dropped the doubled rim: the fresh Ellipse sits at the rest "
                + "width with the engagement still live, so the freeze has silently stopped "
                + "reading as anything.")
            .That(after.StrokeThickness).IsEqualTo(4.0);
        Check.WithCustomMessage("The rebuilt frozen face lost its acquired colours.")
            .That(StrokeOf(after)).IsEqualTo(Tokens.BorderFocus);
    }

    // ────── P16: the whole disable/re-enable cycle, end to end ──────

    /// <summary>
    /// P16, the full flow across the seam: disable mid-dwell (no fire from the frozen arc, arc
    /// 0), re-enable (target recreated by its position report), rest (previews — freezes, never
    /// fires), leave (panel retracts, arc drops), return (fires, having risen from 0 the whole
    /// way). The mid-pass progress checkpoint is the "from 0" proof: a pass that had inherited the
    /// frozen ~0.45 accumulation would be past 0.75 by the 300 ms mark.
    /// </summary>
    [StaFact]
    public void P16_DisableMidDwell_NoFire_Reenable_Previews_LeaveReturnFiresFromZero()
    {
        var messenger = new WeakReferenceMessenger();
        var visibility = new List<AdaptationPreviewVisibilityMessage>();
        messenger.Register<AdaptationPreviewVisibilityMessage>(this, (_, m) => visibility.Add(m));
        int resets = 0;
        messenger.Register<ResetAdaptationMessage>(this, (_, _) => resets++);
        var (window, vm) = BoundToolbar(messenger, includeReset: true);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);

        // Rest on Reset → previews (freezes at ~0.45; engagement 1).
        Hold(vm, ResetCenter, 0, 350_000);
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true);
        Check.That(ResetButton(vm).DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(resets).IsEqualTo(0);

        // Disable MID-DWELL, from inside the frozen hold: no fire, arc 0, panel retracted.
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: false);
        Check.WithCustomMessage("The disable mid-dwell fired the action it was freezing.")
            .That(resets).IsEqualTo(0);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false);

        // …and the old position stays hot-proof: a full dwell-time rest exactly there fires
        // nothing (the target went with the button).
        Hold(vm, ResetCenter, 380_000, 1_410_000);
        Check.That(resets).IsEqualTo(0);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);

        // Re-enable → the position report recreates the target; at rest, arc 0.
        window.Initialize(ToolbarTestHelpers.ButtonSize, 700, includeReset: true);
        vm.UpdateButtonScreenPosition(ResetId, ResetCenter);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);

        // Rest → previews again: engagement 2, frozen past 0.40, still no fire.
        Hold(vm, ResetCenter, 1_500_000, 1_860_000);
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true);
        Check.That(visibility[2].EngagementId).IsEqualTo(2);
        Check.That(ResetButton(vm).DwellProgress).IsStrictlyGreaterThan(ResetPreviewEngine.Threshold);
        Check.That(resets).IsEqualTo(0);

        // Report the panel's bounds so the leave run can start (unknown bounds hold the
        // engagement Frozen — the reader may rest on a panel we cannot see yet).
        messenger.Send(new AdaptationPreviewBoundsMessage(2, PreviewBoundsKind.Rect, PanelRect));

        // Leave → armed, panel retracted, and the post-arm zero drops the held arc.
        Hold(vm, Away, 1_900_000, 2_070_000);
        Check.That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true, false);
        vm.SimulateTargetProgress(ResetId, 0.0);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);
        Check.That(ResetButton(vm).IsHighlighted).IsFalse();

        // Return within the 3 s arm window → the pass runs UNFROZEN from 0 all the way to the
        // fire. Mid-pass checkpoint at 300 of 700 ms: ~0.43, i.e. it rose from 0 — not resumed
        // from the frozen value.
        Hold(vm, ResetCenter, 2_100_000, 2_400_000);
        var midPass = ResetButton(vm).DwellProgress;
        Check.WithCustomMessage(
                $"Mid-pass progress is {midPass:F2}; a fresh pass at 300 of 700 ms reads ~0.43. "
                + "A value past 0.6 means the pass inherited the frozen accumulation — the arc "
                + "did not start from 0.")
            .That(midPass).IsStrictlyGreaterThan(0.0).And.IsStrictlyLessThan(0.60);

        Hold(vm, ResetCenter, 2_430_000, 2_820_000);   // the remaining 420 ms of dwell time
        Check.WithCustomMessage("The deliberate return after the leave did not fire.")
            .That(resets).IsEqualTo(1);
        Check.WithCustomMessage("A second preview opened mid-pass — the pass must propagate freely.")
            .That(visibility.Select(v => v.Visible)).ContainsExactly(true, false, true, false);
        Check.That(ResetButton(vm).DwellProgress).IsEqualTo(0.0);   // the fire zeroes the arc
    }
}
