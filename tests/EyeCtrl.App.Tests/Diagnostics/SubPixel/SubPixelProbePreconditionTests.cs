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
    public void Paused_IsRefusedWithItsOwnReason()
    {
        // "Resume first" is a distinct remedy from "leave drag/scroll/repeat" (review 03 F6).
        Check.That(Refusal(InteractionState.Paused)).IsEqualTo(ProbeRefusal.Paused);
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

        // Paused is read from the real InteractionManager.
        world.Engine.InteractionManager.TogglePause();
        Check.That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.WithCustomMessage("GetBlockReason must consult the live interaction state")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
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
