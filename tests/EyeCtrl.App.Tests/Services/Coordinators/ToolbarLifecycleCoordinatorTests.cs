namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using Xunit;

public class ToolbarLifecycleCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new ToolbarLifecycleCoordinator(new ModalInteractionState());
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new ToolbarLifecycleCoordinator(new ModalInteractionState()).Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new ToolbarLifecycleCoordinator(new ModalInteractionState());
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void Ctor_RejectsNullModalState()
    {
        Check.ThatCode(() => new ToolbarLifecycleCoordinator(null!)).Throws<ArgumentNullException>();
    }

    // The keyboard-feed gate (OS text injection). Review found the inline condition had zero
    // coverage — neutralizing the pause term broke no test. These pin every term.
    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_WhilePaused()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: true, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false, probeOpen: false)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_WhenModalStateOrWindowClosed()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: false, keyboardIsVisible: true, isInGap: false, probeOpen: false)).IsFalse();
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: false, isInGap: false, probeOpen: false)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_ForGapHeldSamples()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: true, probeOpen: false)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsTrue_OnlyWhenEveryGateIsOpen()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false, probeOpen: false)).IsTrue();
    }

    // W5(a) (build plan): the probe term in isolation. With every other gate open, an open probe
    // must withhold the sample — the keyboard is OS text injection, and a sample fed from under the
    // probe's opaque window would type into whatever the user cannot see. Mutation: drop the
    // `!probeOpen` term → the first assertion goes red.
    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_WhileProbeOpen()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false, probeOpen: true)).IsFalse();
        // Negative control: the probe term is the only difference, so the gate is open without it.
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false, probeOpen: false)).IsTrue();
    }

    // W4 (build plan): the REAL auto-hide controller driven through the production gate. While the
    // probe is open, > 5 s off the toolbar and then ≥ 400 ms past the docked edge do nothing; once it
    // closes, the same streams hide and re-summon, so the gate — not a dead controller — is what
    // suppressed. Mutation: drop the `probeOpen` term from AutoHideGateClosed → the off-toolbar run
    // hides → red. Accepted residual (plan; review 03 N5a): the production construction site's wiring
    // that passes _modal.ProbeOpen is checked by review, not by this test.
    [Fact]
    public void AutoHide_IsGated_WhileTheProbeIsOpen()
    {
        var modal = new ModalInteractionState { ProbeOpen = true };
        var visibility = new RecordingToolbarVisibility();
        var controller = new ToolbarAutoHideController(
            enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
            toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50,
            calibrationActive: () => ToolbarLifecycleCoordinator.AutoHideGateClosed(
                modal.ProbeOpen, calibrationActive: false));
        controller.Attach(visibility);
        // Attach positions the pause indicator (wiring, not a hide/summon) — start the record clean.
        visibility.Calls.Clear();

        var offToolbar = new GazePoint(500, 500, 0, true);
        for (long t = 0; t <= 6_000_000; t += 33_000)
            controller.Feed(offToolbar, t);
        Check.WithCustomMessage("while the probe is open an off-toolbar run must not hide the toolbar")
            .That(visibility.Calls).IsEmpty();

        // The state machine is forced Hidden directly, so a probe-time past-edge sample can also be
        // shown to not summon a band that would come up underneath the probe window.
        controller.HideForTest(visibility);
        visibility.Calls.Clear();
        var pastEdge = new GazePoint(500, 1200, 0, true);
        for (long t = 7_000_000; t <= 7_500_000; t += 33_000)
            controller.Feed(pastEdge, t);
        Check.WithCustomMessage("while the probe is open a past-edge run must not summon the toolbar")
            .That(visibility.Calls).IsEmpty();

        // Negative control: with the probe closed the same streams act.
        modal.ProbeOpen = false;
        controller.ResetToVisible();
        for (long t = 8_000_000; t <= 14_000_000; t += 33_000)
            controller.Feed(offToolbar, t);
        Check.WithCustomMessage("once the probe is closed the off-toolbar run must hide the toolbar")
            .That(visibility.HideCount).IsEqualTo(1);

        for (long t = 15_000_000; t <= 15_500_000; t += 33_000)
            controller.Feed(pastEdge, t);
        Check.WithCustomMessage("once the probe is closed a past-edge run must summon the toolbar")
            .That(visibility.ShowCount).IsEqualTo(1);
    }

    [Fact]
    public void SetAutoHideSuspended_ForwardsToTheController()
    {
        // The seam Keyboard/Settings call when a modal opens. Pinned directly because the fan-out now
        // crosses an object boundary that a `?.` on the wrong field would silently swallow.
        var c = new ToolbarLifecycleCoordinator(new ModalInteractionState());
        c.InjectAutoHideForTesting(NewAutoHide());

        c.SetAutoHideSuspended(true);
        Check.That(c.IsAutoHideSuspended).IsTrue();

        c.SetAutoHideSuspended(false);
        Check.That(c.IsAutoHideSuspended).IsFalse();
    }

    [Fact]
    public void ControllerSeams_BeforeInitialize_AreNoOps()
    {
        // The tray fallback (App → EventCoordinator.ForceShowToolbar → ForceShow) and the calibration
        // handler reach these before/independently of SetupToolbarAutoHide, and EventCoordinator.Dispose
        // nulls its reference — so every seam must tolerate a null controller/adapter rather than throw
        // into the gaze or tray path.
        var c = new ToolbarLifecycleCoordinator(new ModalInteractionState());

        c.SetAutoHideSuspended(true);
        c.SetActive(false);
        c.ForceShow();
        c.UpdateEnabled(true);
        c.UpdateEdge("bottom");
        c.ReassertVisibility();
        c.PositionIndicator("bottom");
        c.RefreshToolbarRect();
    }

    [Fact]
    public void PauseIndicatorWiring_MirrorCommandsStrip_AndStartupPushStaysAfterShowOverlays()
    {
        var root = SourceScanner.RepoRoot().FullName;

        var coordinator = File.ReadAllText(Path.Combine(root,
            "src", "EyeCtrl.App", "Services", "Coordinators", "ToolbarLifecycleCoordinator.cs"));
        Check.WithCustomMessage(
                "The StateChangedMessage mirror no longer commands the pause strip. Without it, "
                + "an unpause with the band visible never dismisses the strip (spec H1).")
            .That(SourceScanner.StripComments(coordinator).Contains("SetPauseIndicator(paused);")).IsTrue();

        var app = File.ReadAllText(Path.Combine(root, "src", "EyeCtrl.App", "App.xaml.cs"));
        var showOverlays = app.IndexOf("ShowOverlays(splash, cursorCircle, toolbar, settings);",
            StringComparison.Ordinal);
        // The startup pause push was extracted into PauseLaunchProvenance.Apply (which logs the
        // paused source AND calls TogglePause). The marker moved with it; the O4 ordering pin is
        // unchanged — the push must still land after ShowOverlays.
        var togglePause = app.IndexOf("PauseLaunchProvenance.Apply(", StringComparison.Ordinal);
        // Each marker is asserted present BEFORE the ordering comparison: with only one marker
        // missing, IndexOf yields -1 and `N > -1` is TRUE — the ordering check would pass
        // vacuously and silently disarm the O4 pin. (-1 > -1 is false, but that covers only
        // the both-missing case.)
        Check.WithCustomMessage(
                "The ShowOverlays(splash, cursorCircle, toolbar, settings); marker is gone from "
                + "App.xaml.cs. If the call's argument list changed, update the literal here — "
                + "do not leave the ordering check comparing against -1.")
            .That(showOverlays).IsGreaterOrEqualThan(0);
        Check.WithCustomMessage(
                "The PauseLaunchProvenance.Apply( marker is gone from App.xaml.cs. If the startup "
                + "pause push moved or was renamed, update the literal here — do not leave the "
                + "ordering check comparing against -1.")
            .That(togglePause).IsGreaterOrEqualThan(0);
        Check.WithCustomMessage(
                "The startup pause push must stay AFTER ShowOverlays (spec O4): the startup band "
                + "show bypasses the adapter, so on --paused/startPaused launches the strip's "
                + "show+raise lands in the TogglePause call — moving it before ShowOverlays "
                + "hides the strip under the band.")
            .That(togglePause).IsStrictlyGreaterThan(showOverlays);
    }

    [Fact]
    public void AutoHideWiring_PinsTheTunablesTheUserClearsBy0To6Pixels()
    {
        // The independent review's M17: every existing test builds its own controller with its own
        // 50 px margin, so the PRODUCTION wiring is unpinned — offScreenMarginPx 50 → 60 here would
        // leave the whole suite green. The user's only observed paused summon cleared the threshold
        // by 0–6 px (raw y=−50/−52/−56 against −50), so drift at this literal silently removes the
        // only gaze way out of Paused. Pin all three tunables at the wiring site.
        //
        // Word-boundary regexes, not Contains (review R-a): a substring match stays green for
        // offScreenMarginPx: 500, hideDelayMs: 50000 and summonDelayMs: 4000 — drifts of exactly
        // the kind this test exists to catch.
        var root = SourceScanner.RepoRoot().FullName;
        var text = SourceScanner.StripComments(File.ReadAllText(Path.Combine(root,
            "src", "EyeCtrl.App", "Services", "Coordinators", "ToolbarLifecycleCoordinator.cs")));

        Check.WithCustomMessage("ToolbarAutoHideController is no longer wired with hideDelayMs: 5000.")
            .That(Regex.IsMatch(text, @"hideDelayMs:\s*5000\b")).IsTrue();
        Check.WithCustomMessage("ToolbarAutoHideController is no longer wired with summonDelayMs: 400.")
            .That(Regex.IsMatch(text, @"summonDelayMs:\s*400\b")).IsTrue();
        Check.WithCustomMessage(
                "ToolbarAutoHideController is no longer wired with offScreenMarginPx: 50. The user's "
                + "only observed paused summon cleared the threshold by 0–6 px — drift at this "
                + "literal removes the only gaze way out of Paused.")
            .That(Regex.IsMatch(text, @"offScreenMarginPx:\s*50\b")).IsTrue();
    }

    [Fact]
    public void ProbeGateWiring_IsPinnedAtTheSource()
    {
        // Review 01 (optional part): neither probe term in this file is reachable by the tests that
        // build the real objects — SetupToolbarAutoHide needs the live window/geometry path — so
        // mutants that revert either wiring survived the full suite (R10: the calibrationActive
        // lambda drops the AutoHideGateClosed call; R12: the ShouldFeedKeyboardSample call site
        // passes false for probeOpen). Comment-stripped, whitespace-collapsed source, the same
        // discipline as the pins above.
        var source = Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "ToolbarLifecycleCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("The auto-hide controller no longer reads _modal.ProbeOpen through AutoHideGateClosed.")
            .That(source.Contains(
                "calibrationActive:()=>AutoHideGateClosed(_modal.ProbeOpen,Context.CalibrationService.IsSessionActive),")).IsTrue();
        Check.WithCustomMessage("SetupGazeRouting no longer passes _modal.ProbeOpen into ShouldFeedKeyboardSample.")
            .That(source.Contains(
                "ShouldFeedKeyboardSample(keyboardGated,_modal.KeyboardVisible,keyboard.IsVisible,frame.IsInGap,_modal.ProbeOpen)")).IsTrue();
    }

    private static ToolbarAutoHideController NewAutoHide() => new(
        enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
        toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
        hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);

    /// <summary>Records toolbar-visibility calls for W4's real-controller drive.</summary>
    private sealed class RecordingToolbarVisibility : IToolbarVisibility
    {
        public List<string> Calls { get; } = new();
        public int HideCount => Calls.Count(c => c == "HideToolbar");
        public int ShowCount => Calls.Count(c => c == "ShowToolbar");

        public void ShowToolbar() => Calls.Add("ShowToolbar");
        public void HideToolbar(bool paused) => Calls.Add("HideToolbar");
        public void ForceShow() => Calls.Add("ForceShow");
        public void SetPauseIndicator(bool visible) => Calls.Add("SetPauseIndicator");
        public void PositionIndicator(string edge) => Calls.Add("PositionIndicator");
        public void SetActive(bool active) => Calls.Add("SetActive");
    }
}
