namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// W3 — request → queued start. The same initialised <c>SettingsUiCoordinator</c> and real-object
/// host as W2; the test pumps dispatcher frames itself (build plan W3, cases (a)-(f)).
/// </summary>
public class SubPixelProbeQueuedStartTests
{
    /// <summary>Drives the real close path exactly as a real SettingsWindow.Close() would.</summary>
    private static Action Close(ProbeTestWorld world) =>
        () => world.SettingsUi!.OnSettingsWindowClosed(null, EventArgs.Empty);

    [Fact]
    public void RequestThenClose_StartsNothingUntilTheDrain_ThenArmsEverything()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);
        Check.That(world.QuickActions!.IsSummonSuspended).IsFalse();

        // Settings is really open — the button is pressed from inside it (fixup 01 B1). C10's
        // reason must therefore be null, or every press would say "Close Settings first".
        world.Modal.SettingsOpen = true;
        world.Modal.SettingsOpenChanged?.Invoke(true);
        Check.WithCustomMessage("C10's reason refused while Settings is open (the button's own host)")
            .That(world.Coordinator.GetBlockReason()).IsNull();

        world.Coordinator.RequestStart(Close(world));

        // The close has returned; BeginStart is queued at Background and must not have run.
        Check.WithCustomMessage("The start ran inside the close handling (review 05 F1)")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
        Check.That(world.QuickActions.IsSummonSuspended).IsFalse();

        world.Pump();

        Check.That(world.Modal.ProbeOpen).IsTrue();
        Check.That(world.Host.CountOf("HideRing")).IsEqualTo(1);
        Check.That(world.Host.CountOf("AcquireProbeSuppression")).IsEqualTo(1);
        Check.That(world.Host.CountOf("DeactivateToolbar")).IsEqualTo(1);
        Check.That(world.Host.CountOf("HideKeyboard")).IsEqualTo(1);
        Check.That(world.QuickActions.IsSummonSuspended).IsTrue();
        Check.That(world.Host.Windows).HasSize(1);
        Check.That(world.Host.Windows[0].IsShown).IsTrue();
    }

    [Fact]
    public void AnOrdinarySettingsClose_StartsNothing()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);

        world.SettingsUi!.OnSettingsWindowClosed(null, EventArgs.Empty);
        world.Pump();

        Check.WithCustomMessage("A close C10 never requested armed the probe")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
    }

    [Fact]
    public void TheC10Reason_IgnoresSettingsOpen_ButBeginStartStillRefuses()
    {
        // B1: the button lives inside Settings, so its reason must not include SettingsOpen; the
        // queued start keeps the full precondition, because by then the close has run and a
        // re-opened Settings window must still refuse (review 04 N3).
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);
        world.Modal.SettingsOpen = true;

        Check.WithCustomMessage("C10's reason must ignore SettingsOpen")
            .That(world.Coordinator.GetBlockReason()).IsNull();

        world.Coordinator.StartForTest();

        Check.WithCustomMessage("BeginStart must keep the SettingsOpen term")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
    }

    [Fact]
    public void ReopeningSettingsBeforeTheDrain_CancelsTheQueuedStart()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);

        world.Coordinator.RequestStart(Close(world));

        // Settings reopens and closes again with no new request: the posted start is now stale.
        world.Modal.SettingsOpen = true;
        world.Modal.SettingsOpenChanged?.Invoke(true);
        world.Modal.SettingsOpen = false;
        world.Modal.SettingsOpenChanged?.Invoke(false);

        world.Pump();

        Check.WithCustomMessage("The generation check is gone: a cancelled start still ran")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
    }

    [Fact]
    public void ForceShowToolbarBeforeTheDrain_CancelsTheQueuedStart()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);

        world.Coordinator.RequestStart(Close(world));

        // Tray "Show toolbar" arrives before the posted start runs. Its bump is the only guard.
        world.Coordinator.InterlockForceShowToolbar(() => { });

        world.Pump();

        Check.WithCustomMessage("The ForceShowToolbar interlock did not bump the generation")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
    }

    [Fact]
    public void WhenTheCloseThrows_PendingStartIsCleared_AndALaterCloseStartsNothing()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);

        Check.ThatCode(() => world.Coordinator.RequestStart(
                () => throw new InvalidOperationException("close failed")))
            .Throws<InvalidOperationException>();

        // A later, ordinary Settings close must not start the probe the failed request left armed.
        world.SettingsUi!.OnSettingsWindowClosed(null, EventArgs.Empty);
        world.Pump();

        Check.WithCustomMessage("PendingStart survived the close's throw (the finally is gone)")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.CountOf("SetProbeOpen")).IsEqualTo(0);
    }
}

/// <summary>
/// W9 — the mid-test interlocks (build plan W9). C7 is initialised after
/// <c>SettingsUiCoordinator</c> in the world, matching production's registration order; each
/// interlock runs synchronously inside its handler, which is the actual ordering mechanism
/// (review 04 N7).
/// </summary>
public class SubPixelProbeInterlockTests
{
    [Fact]
    public void SettingsRequested_EndsTheRunSynchronously_BeforeSettingsCouldOpen()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);
        world.StartShown();
        world.Host.ClearLog();

        world.Messenger.Send(new SettingsRequestedMessage());

        Check.WithCustomMessage(
                "The interlock posted its Restore instead of running it synchronously: Settings' "
                + "posted ShowSettingsWindow could then open over a live probe")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.Names).Contains("ReleaseProbeSuppression");
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        // Nothing pumped: Settings' own handler only posts, so no real window is built here.
    }

    [Fact]
    public void ForceShowToolbar_EndsTheRunBeforeTheForcedShow()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);
        world.StartShown();
        world.Host.ClearLog();

        int hostCallsWhenContinuationRan = -1;
        world.Coordinator.InterlockForceShowToolbar(
            () => hostCallsWhenContinuationRan = world.Host.Names.Count);

        Check.WithCustomMessage("The forced show ran before (or without) the run's Restore")
            .That(hostCallsWhenContinuationRan).IsStrictlyGreaterThan(0);
        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Host.Names).Contains("ReleaseProbeSuppression");
    }

    [Fact]
    public void Paused_EndsTheRun_ButOtherStatesDoNot()
    {
        using var world = new ProbeTestWorld(withQuickActions: true, withSettingsUi: true);
        world.StartShown();

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.WithCustomMessage("A non-Paused state change ended the run")
            .That(world.Modal.ProbeOpen).IsTrue();

        world.Messenger.Send(new StateChangedMessage(InteractionState.Paused));

        Check.WithCustomMessage("The Paused interlock is gone: the pause strip would appear over the probe")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();
    }
}
