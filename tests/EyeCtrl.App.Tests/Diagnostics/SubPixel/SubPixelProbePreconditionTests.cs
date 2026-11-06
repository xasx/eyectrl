namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Models;

/// <summary>
/// W8 — the start precondition (build plan W8). The decision is a pure static so each term can be
/// killed in isolation; the last two tests pin that the coordinator reads the live inputs through
/// it.
/// </summary>
public class SubPixelProbePreconditionTests
{
    private static ProbeRefusal? Refusal(
        InteractionState state = InteractionState.FreeLook,
        bool menus = false,
        bool eyeCare = false,
        bool adaptation = false,
        bool settingsOpen = false,
        bool sessionLive = false,
        bool calibration = false) =>
        SubPixelProbeCoordinator.RefusalFor(
            state, menus, eyeCare, adaptation, settingsOpen, sessionLive, calibration);

    [Theory]
    [InlineData(InteractionState.FreeLook)]
    [InlineData(InteractionState.Fixation)]
    public void FreeLookAndFixation_AreAllowed(InteractionState state) =>
        Check.That(Refusal(state)).IsNull();

    [Fact]
    public void Paused_IsStillTheQueuedStartsOwnRefusal()
    {
        // F5: the BUTTON no longer refuses Paused (it resumes first), but the queued start's full
        // precondition still has the term — a start must never run while the app is still paused.
        // "Resume first" is a distinct remedy from "leave drag/scroll/repeat" (review 03 F6).
        Check.That(Refusal(InteractionState.Paused)).IsEqualTo(ProbeRefusal.Paused);
    }

    [Fact]
    public void TheButtonsPausedTerm_AllowsTheRequest_ButOtherTermsStillRefuse()
    {
        static ProbeRefusal? Button(
            InteractionState state = InteractionState.Paused,
            bool menus = false,
            bool eyeCare = false,
            bool adaptation = false,
            bool sessionLive = false,
            bool calibration = false) =>
            SubPixelProbeCoordinator.ButtonRefusalFor(
                state, menus, eyeCare, adaptation, sessionLive, calibration);

        Check.WithCustomMessage("The button must let a paused dwell request the resume-then-start")
            .That(Button()).IsNull();
        Check.WithCustomMessage("a menu still blocks the button while paused")
            .That(Button(menus: true)).IsEqualTo(ProbeRefusal.MenusOpen);
        Check.WithCustomMessage("eye care still blocks the button while paused")
            .That(Button(eyeCare: true)).IsEqualTo(ProbeRefusal.EyeCareOn);
        Check.WithCustomMessage("adaptation still blocks the button while paused")
            .That(Button(adaptation: true)).IsEqualTo(ProbeRefusal.AdaptationOn);
        Check.WithCustomMessage("a live run still blocks the button while paused")
            .That(Button(sessionLive: true)).IsEqualTo(ProbeRefusal.AlreadyRunning);
        Check.WithCustomMessage("calibration still blocks the button while paused")
            .That(Button(calibration: true)).IsEqualTo(ProbeRefusal.CalibrationActive);
        Check.WithCustomMessage("a non-resumable state still refuses")
            .That(Button(InteractionState.RepeatMode)).IsEqualTo(ProbeRefusal.NotAllowedState);
    }

    [Theory]
    [InlineData(InteractionState.RepeatMode)]
    [InlineData(InteractionState.Dragging)]
    [InlineData(InteractionState.ScrollMode)]
    [InlineData(InteractionState.ActionMenuVisible)]
    [InlineData(InteractionState.ManualSelectionActive)]
    public void ActingStates_AreRefused(InteractionState state) =>
        Check.That(Refusal(state)).IsEqualTo(ProbeRefusal.NotAllowedState);

    [Fact]
    public void EachPreconditionTerm_RefusesOnItsOwn()
    {
        Check.WithCustomMessage("the three dwell-live menus (review 03 F6)")
            .That(Refusal(menus: true)).IsEqualTo(ProbeRefusal.MenusOpen);
        Check.WithCustomMessage("eye care can raise its own topmost window")
            .That(Refusal(eyeCare: true)).IsEqualTo(ProbeRefusal.EyeCareOn);
        Check.WithCustomMessage("adaptation can raise its own topmost window")
            .That(Refusal(adaptation: true)).IsEqualTo(ProbeRefusal.AdaptationOn);
        Check.WithCustomMessage("a queued start is refused while Settings is open (review 04 N3)")
            .That(Refusal(settingsOpen: true)).IsEqualTo(ProbeRefusal.SettingsOpen);
        Check.WithCustomMessage("a second run while one is live")
            .That(Refusal(sessionLive: true)).IsEqualTo(ProbeRefusal.AlreadyRunning);
        Check.WithCustomMessage("calibration owns the screen")
            .That(Refusal(calibration: true)).IsEqualTo(ProbeRefusal.CalibrationActive);
    }

    [Fact]
    public void TheCoordinatorReadsTheLiveInputs_ThroughTheSamePredicate()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);

        Check.That(world.Coordinator.GetBlockReason()).IsNull();   // FreeLook, all clean

        // Paused is read from the real InteractionManager. F5: the button allows it — the request
        // resumes first — while the queued start's full precondition keeps the term.
        world.Engine.InteractionManager.TogglePause();
        Check.That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.WithCustomMessage("GetBlockReason still refuses the paused button; the request resumes instead")
            .That(world.Coordinator.GetBlockReason()).IsNull();
        Check.That(SubPixelProbeCoordinator.RefusalFor(
                InteractionState.Paused, false, false, false, false, false, false))
            .IsEqualTo(ProbeRefusal.Paused);
        world.Engine.InteractionManager.TogglePause();

        // A visible menu is read from the real VMs.
        world.Context.QuickActionsGridVM.IsVisible = true;
        Check.WithCustomMessage("GetBlockReason must consult the menu VMs")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
        world.Context.QuickActionsGridVM.IsVisible = false;

        // A live session is read from the session slot.
        var session = world.StartShown();
        Check.WithCustomMessage("GetBlockReason must refuse while a run is live")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
        world.Coordinator.Restore(session);
        Check.That(world.Coordinator.GetBlockReason()).IsNull();

        // N4: the C10 button path reads eye care, adaptation and calibration too — with one of
        // them dropped the button shows no reason, closes Settings, and BeginStart refuses silently.
        world.Settings.EyeCare.Enabled = true;
        Check.WithCustomMessage("the C10 reason does not read eye care")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
        world.Settings.EyeCare.Enabled = false;

        world.Settings.Adaptation.Enabled = true;
        Check.WithCustomMessage("the C10 reason does not read adaptation")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
        world.Settings.Adaptation.Enabled = false;

        world.Context.CalibrationService.StartSession(4, new PhysicalPoint(1920, 1080));
        Check.WithCustomMessage("the C10 reason does not read calibration")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
        world.Context.CalibrationService.CancelSession();
        Check.That(world.Coordinator.GetBlockReason()).IsNull();
    }
}
