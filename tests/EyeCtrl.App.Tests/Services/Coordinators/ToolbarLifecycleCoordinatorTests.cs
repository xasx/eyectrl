namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.IO;
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
            paused: true, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_WhenModalStateOrWindowClosed()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: false, keyboardIsVisible: true, isInGap: false)).IsFalse();
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: false, isInGap: false)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsFalse_ForGapHeldSamples()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: true)).IsFalse();
    }

    [Fact]
    public void ShouldFeedKeyboardSample_IsTrue_OnlyWhenEveryGateIsOpen()
    {
        Check.That(ToolbarLifecycleCoordinator.ShouldFeedKeyboardSample(
            paused: false, modalKeyboardVisible: true, keyboardIsVisible: true, isInGap: false)).IsTrue();
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
            .That(coordinator.Contains("SetPauseIndicator(paused);")).IsTrue();

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

    private static ToolbarAutoHideController NewAutoHide() => new(
        enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
        toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
        hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
}
