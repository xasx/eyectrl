namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// F5 — the owner's 06.11 decision: dwelling „Subpixel-Sichttest starten" while EyeCtrl is paused
/// resumes through the normal path and then starts the probe, instead of refusing. The resume's
/// posted <c>StateChangedMessage</c> is the gate: the probe starts only after it lands, never
/// straight after the resume call, and a pending start that never lands is abandoned (and
/// generation-bumped) rather than firing later.
/// </summary>
public class SubPixelProbePausedStartTests
{
    /// <summary>Closes Settings as the real window's handler does, without needing the coordinator.</summary>
    private static Action ManualClose(ProbeTestWorld world) =>
        () =>
        {
            world.Modal.SettingsOpen = false;
            world.Modal.SettingsOpenChanged?.Invoke(false);
        };

    [Fact]
    public void ThePausedButton_IsNotBlocked_SoTheDwellRequestsTheStart()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();

        Check.That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.WithCustomMessage(
                "A paused button still shows a refusal; the owner's decision is resume-then-start (F5)")
            .That(world.Coordinator.GetBlockReason()).IsNull();
    }

    [Fact]
    public void ThePausedButton_StillShowsEveryOtherRefusal()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();
        world.Settings.EyeCare.Enabled = true;

        Check.WithCustomMessage("The other refusals must be unchanged: eye care still blocks the button")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
    }

    [Fact]
    public void AStateThatCannotBeLeftFromSettings_StillShowsARefusal()
    {
        // Repeat mode cannot be left from Settings: the owner can only exit it by looking away.
        // The resume path must not swallow that refusal (F5: "resume impossible → refusal shown").
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.EnterRepeatMode(InteractionAction.LeftClick);

        Check.That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.RepeatMode);
        Check.WithCustomMessage("A non-resumable state no longer refuses the button")
            .That(world.Coordinator.GetBlockReason()).IsNotNull();
    }

    [Fact]
    public void PausedRequest_ResumesThroughTheNormalPath_ThenStartsOnlyWhenTheStateChangeLands()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();
        int stateChanges = 0;
        world.Engine.InteractionManager.StateChanged += _ => stateChanges++;

        bool closed = false;
        world.Coordinator.RequestStart(() =>
        {
            closed = true;
            world.Modal.SettingsOpen = false;
            world.Modal.SettingsOpenChanged?.Invoke(false);
        });

        Check.WithCustomMessage("The request did not resume through the normal state-machine path")
            .That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(stateChanges).IsEqualTo(1);
        Check.That(closed).IsTrue();

        Check.WithCustomMessage("The probe started straight after the resume call, before the state change landed")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.WithCustomMessage("The posted resume state change did not start the probe")
            .That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Coordinator.LiveSessionForTest).IsNotNull();

        world.Pump();
        Check.WithCustomMessage("The probe started twice (queued start plus the resume gate)")
            .That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(1);
        Check.That(world.Host.Windows).HasSize(1);
    }

    [Fact]
    public void PausedRequest_WaitsThroughTheQueuedStartDrain_AndStartsOnceOnTheStateChange()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();

        world.Coordinator.RequestStart(ManualClose(world));

        // The Settings close posted BeginStart at Background. Draining it must not start the probe
        // while the posted resume state change has not landed.
        world.Pump();
        Check.WithCustomMessage("The queued start ran while the resume's state change had not landed")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The resume gate started the probe more than once")
            .That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(1);
        Check.That(world.Coordinator.LiveSessionForTest).IsNotNull();
    }

    [Fact]
    public void PausedRequest_WhenTheResumeDoesNotLand_GivesUpAndNeverStartsLater()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;

        world.Coordinator.RequestStart(ManualClose(world));

        // The resume only "lands" through its posted message. Without it, the gate times out.
        var resumeTimer = world.Timers.Created
            .Single(c => c.Interval == SubPixelProbeCoordinator.ResumeLandingTimeout);
        resumeTimer.Tick();

        Check.WithCustomMessage("A resume that never landed must report the paused refusal")
            .That(refused).ContainsExactly(ProbeRefusal.Paused);
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        // The queued start from the Settings close is still posted. A late state change must not
        // resurrect a start the owner was never told about.
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The abandoned start fired later")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }

    [Fact]
    public void PausedAgainBeforeTheStateChangeLands_GivesUp()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;

        world.Coordinator.RequestStart(ManualClose(world));

        // Something pauses again before the posted FreeLook message is delivered.
        world.Engine.InteractionManager.TogglePause();
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.WithCustomMessage("The stale resume message started a probe over a still-paused app")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(refused).ContainsExactly(ProbeRefusal.Paused);

        world.Pump();
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void PausedRequest_ReChecksTheOtherGatesAfterTheResume()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;

        world.Coordinator.RequestStart(ManualClose(world));

        // A gate that was clear on activation becomes live before the resume lands.
        world.Settings.EyeCare.Enabled = true;
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The post-resume start skipped the full precondition")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.WithCustomMessage("The skipped gate was not reported")
            .That(refused).ContainsExactly(ProbeRefusal.EyeCareOn);
    }

    [Fact]
    public void PausedRequest_PreservesTheSession2ResumeRoute()
    {
        using var world = new ProbeTestWorld();
        WriteSeedVerdict(world.ResultsDir, SubPixelAbOutcome.Convicts);
        world.Engine.InteractionManager.TogglePause();

        world.Coordinator.RequestStartAuto(ManualClose(world));
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The resume gate dropped the session-2 run mode")
            .That(world.Session.StartMode).IsEqualTo(SubPixelProbeStartMode.ResumeSession2);
        Check.That(world.Session.ResumedSession1Verdict!.Outcome).IsEqualTo(SubPixelAbOutcome.Convicts);
    }

    [Fact]
    public void NotPaused_IsUnchanged_NoResumeGateAndTheQueuedStartStillRuns()
    {
        using var world = new ProbeTestWorld();

        world.Coordinator.RequestStart(ManualClose(world));

        Check.WithCustomMessage("A not-paused request armed a resume gate")
            .That(world.Timers.Created).IsEmpty();

        world.Pump();

        Check.That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(1);
    }

    [Fact]
    public void PausedRequest_WhenTheCloseThrows_DoesNotResumeAndLeavesNothingArmed()
    {
        using var world = new ProbeTestWorld();
        world.Engine.InteractionManager.TogglePause();

        Check.ThatCode(() => world.Coordinator.RequestStart(
                () => throw new InvalidOperationException("close failed")))
            .Throws<InvalidOperationException>();

        Check.WithCustomMessage("A failed close still resumed the app")
            .That(world.Engine.InteractionManager.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.That(world.Timers.Created).IsEmpty();

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    /// <summary>Drops a persisted session-1 verdict into the world's results directory.</summary>
    private static void WriteSeedVerdict(string directory, SubPixelAbOutcome outcome)
    {
        var writer = new SubPixelAbResultWriter(
            System.IO.Path.Combine(directory, $"subpixel-ab-seed-{Guid.NewGuid():N}.json"),
            new SubPixelAbRunInfo(
                Seed: 1,
                Ring: new SubPixelAbRingInfo("WeightedSandwich", 250, 3, 0.7, "#00FF00"),
                AnswerDwellMs: 700,
                DpiScale: 2.0,
                ScreenBoundsPx: new PhysicalRect(0, 0, 2560, 1440)));
        writer.WriteSession(
            new SubPixelProbeSessionOutcome(
                SubPixelProbeSessionKind.Session1, SubPixelProbeEndReason.BlocksCompleted, false, []),
            new SubPixelAbSession1Verdict(outcome, SubPixelAbStaticReadout.Skipped, "seed " + outcome),
            null);
    }
}
