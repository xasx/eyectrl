namespace EyeCtrl.App.Tests.Services.Coordinators;

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

    private static ToolbarAutoHideController NewAutoHide() => new(
        enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
        toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
        hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
}
