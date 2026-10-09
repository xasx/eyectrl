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

        coord.SetSummonSuspended(true);
        Check.That(coord.IsSummonSuspended).IsTrue();

        coord.SetSummonSuspended(false);
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
}
