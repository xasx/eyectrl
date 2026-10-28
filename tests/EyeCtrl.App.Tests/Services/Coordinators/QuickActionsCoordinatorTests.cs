namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Xunit;

[Collection("DpiHelper Tests (App)")]
public class QuickActionsCoordinatorTests
{
    [Fact]
    public void Ctor_RejectsNullModalState()
    {
        Check.ThatCode(() => new QuickActionsCoordinator(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = new QuickActionsCoordinator(new ModalInteractionState());
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized
        // coordinator must still dispose cleanly (no NullRef on _context).
        new QuickActionsCoordinator(new ModalInteractionState()).Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = new QuickActionsCoordinator(new ModalInteractionState());
        c.Dispose(); c.Dispose();
    }

    [Fact]
    public void Enabled_Toggle_SuspendsAndResumesSummon()
    {
        var settings = new EyeCtrlSettings { QuickActions = { Enabled = false } };
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator(new ModalInteractionState());
        coord.InjectSummonForTesting(controller);           // bypass the GridVM/GazeEngine creation path
        coord.Initialize(TestFakes.MakeContext(settings));  // SetupQuickActionsSummon sees _summon != null
                                                            // → SetSuspended(!Enabled = true)
        Check.That(controller.IsSuspended).IsTrue();

        settings.QuickActions.Enabled = true;               // [ObservableProperty] raises PropertyChanged
                                                            // synchronously → the settings handler → SetSuspended(false)
        Check.That(controller.IsSuspended).IsFalse();
    }

    [Fact]
    public void SetSummonSuspended_ForwardsToTheController()
    {
        // The seam Keyboard/Settings call when a modal opens — the quick-actions half of the fan-out
        // asserted end-to-end in KeyboardCoordinatorTests.ApplyModalKeyboardState_FansOutTo...
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator(new ModalInteractionState());
        coord.InjectSummonForTesting(controller);

        coord.SetSummonSuspended("test", true);
        Check.That(coord.IsSummonSuspended).IsTrue();

        coord.SetSummonSuspended("test", false);
        Check.That(coord.IsSummonSuspended).IsFalse();
    }

    [Fact]
    public void SetSummonSuspended_NamedClaims_AreIndependent()
    {
        // B7 fixup-04 L1: the exercise's release must not lift the keyboard's/Settings' claim.
        var controller = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var coord = new QuickActionsCoordinator(new ModalInteractionState());
        coord.InjectSummonForTesting(controller);

        coord.SetSummonSuspended("keyboard", true);
        coord.SetSummonSuspended("exercise", true);
        coord.SetSummonSuspended("exercise", false);
        Check.WithCustomMessage("the exercise's release lifted the keyboard's summon claim")
            .That(coord.IsSummonSuspended).IsTrue();

        coord.SetSummonSuspended("keyboard", false);
        Check.That(coord.IsSummonSuspended).IsFalse();
    }

    [Fact]
    public void IsSummonSuspended_ReadsTrue_WhenNoControllerExists()
    {
        // Fail-closed default: with no controller (mouse mode, or QuickActions disabled) the summon is
        // effectively suspended, so the seam must not report "live".
        Check.That(new QuickActionsCoordinator(new ModalInteractionState()).IsSummonSuspended).IsTrue();
    }

    // W5(b) (build plan): the probe term on the hot-edge summon, driving the REAL controller through
    // the production predicate (same shape as W4's auto-hide drive). While the probe is open a
    // past-edge run must not summon: the grid would come up under the probe's opaque window,
    // invisible and undismissable. Mutation: drop the probeOpen term from SummonGateClosed → the
    // gated run summons → red. Accepted residual (review 03 N5b): the construction site's wiring
    // that passes _modal.ProbeOpen is checked by review, not by this test.
    [Fact]
    public void Summon_IsGated_WhileTheProbeIsOpen()
    {
        var modal = new ModalInteractionState { ProbeOpen = true };
        var controller = new QuickActionsSummonController(
            "left", 1920, 1080, summonThresholdMs: 400, dismissTimeoutMs: 5000,
            calibrationActive: () => QuickActionsCoordinator.SummonGateClosed(
                modal.ProbeOpen, calibrationActive: false));
        int summons = 0;
        controller.SummonRequested += () => summons++;

        var pastEdge = new GazePoint(-10, 500, 0, true);
        for (long t = 0; t <= 1_000_000; t += 33_000)
            controller.Feed(pastEdge, t);
        Check.WithCustomMessage("while the probe is open a past-edge run must not summon the grid")
            .That(controller.State).IsEqualTo(QuickActionsSummonController.SummonState.Idle);
        Check.That(summons).IsEqualTo(0);

        // Negative control: the same run summons once the probe is closed.
        modal.ProbeOpen = false;
        for (long t = 1_100_000; t <= 1_600_000; t += 33_000)
            controller.Feed(pastEdge, t);
        Check.WithCustomMessage("once the probe is closed the past-edge run must summon")
            .That(controller.State).IsEqualTo(QuickActionsSummonController.SummonState.Summoned);
        Check.That(summons).IsEqualTo(1);
    }

    [Fact]
    public void ProbeGateWiring_IsPinnedAtTheSource()
    {
        // Review 01 (optional part): the production construction site is not reachable by any test
        // that injects its own controller, so mutant R11 (revert the gate to the bare calibration
        // lambda) survived the full suite. The gate must read the SHARED ProbeOpen flag through
        // SummonGateClosed. Comment-stripped, whitespace-collapsed source, the EventCoordinatorTests
        // pin precedent.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "QuickActionsCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("The summon controller no longer reads _modal.ProbeOpen through SummonGateClosed.")
            .That(source.Contains(
                "calibrationActive:()=>SummonGateClosed(_modal.ProbeOpen,Context.CalibrationService.IsSessionActive)")).IsTrue();
    }

    [Fact]
    public void DismissHold_Wiring_IsPinnedAtTheSource()
    {
        // B8b F1: the production construction site is unreachable headlessly (it needs the live
        // grid VM and gaze engine), so the hold's wiring — the grid VM's mid-dwell flag into the
        // controller — is pinned at source, the ProbeGateWiring precedent. Mutant: drop the
        // holdDismiss argument (the grid then vanishes mid-dwell again) → red.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "Coordinators", "QuickActionsCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "The summon controller no longer holds its dismiss timer while a grid target dwells.")
            .That(source.Contains("holdDismiss:()=>gridVM.HasDwellProgress")).IsTrue();
    }

    /// <summary>
    /// B7 fixup-03: the hot-edge summon is App-side, not a <c>DwellOverlayViewModelBase</c> surface,
    /// so its exercise gate lives in the composition root's <c>EyeCareExerciseActiveMessage</c>
    /// handler (together with the shared dwell hold). Publicly traceable only at source: the
    /// coordinator's Initialize needs the live grid VM and gaze engine. Mutation: drop either call
    /// from the registration -> this goes red (and the summon would fire over the exercise, right
    /// over the Skip disc on a right-docked toolbar).
    /// </summary>
    [Fact]
    public void ExerciseHold_Wiring_IsPinnedInTheCompositionRoot()
    {
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "EventCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "EventCoordinator no longer publishes the shared exercise dwell hold — every overlay "
                + "beneath the exercise would stay dwellable.")
            .That(source.Contains(
                "context.Messenger.Send(newExerciseScreenHoldMessage(message.Active));")).IsTrue();
        Check.WithCustomMessage(
                "EventCoordinator no longer suspends the hot-edge Quick Actions summon during an exercise.")
            .That(source.Contains("_quickActions?.SetSummonSuspended(\"exercise\",message.Active);")).IsTrue();
    }
}
