namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// N2 — #319 review-01 F2: every way a RESUMED start can fail after the resume used to be silent.
/// Settings is already closed and the app is resumed when the resume-landing timeout fires or a
/// gate refuses the queued start, so the owner was left with no probe and no word — only a log
/// line. These tests pin the notice: each failure shows the App notice card's one-line failure
/// notice EXACTLY once, through the coordinator's <see cref="SubPixelProbeCoordinator.StartFailedNoticeSink"/>
/// seam (App.xaml.cs attaches the real card). A re-pause is the owner's own choice — the pause
/// strip is the answer — so neither re-pause abandon path shows a notice, the success path shows
/// none, and the normal not-paused request path keeps its activation-time refusal text and stays
/// log-only.
/// </summary>
public class SubPixelProbeStartFailedNoticeTests
{
    /// <summary>Closes Settings as the real window's handler does, without needing the coordinator.</summary>
    private static Action ManualClose(ProbeTestWorld world) =>
        () =>
        {
            world.Modal.SettingsOpen = false;
            world.Modal.SettingsOpenChanged?.Invoke(false);
        };

    /// <summary>Arms a paused request in <paramref name="world"/> and returns the notice capture.</summary>
    private static List<(string Message, string CloseLabel)> StartPausedRequest(ProbeTestWorld world)
    {
        var notices = new List<(string Message, string CloseLabel)>();
        world.Coordinator.StartFailedNoticeSink = (message, closeLabel) => notices.Add((message, closeLabel));
        world.Engine.InteractionManager.TogglePause();
        world.Coordinator.RequestStart(ManualClose(world));
        return notices;
    }

    /// <summary>Arms a paused request with a caller-supplied notice sink.</summary>
    private static void StartPausedRequest(ProbeTestWorld world, Action<string, string> sink)
    {
        world.Coordinator.StartFailedNoticeSink = sink;
        world.Engine.InteractionManager.TogglePause();
        world.Coordinator.RequestStart(ManualClose(world));
    }

    [Fact]
    public void TheResumeTimeout_ShowsExactlyOneNotice()
    {
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = StartPausedRequest(world);

        // The resume only "lands" through its posted message. Without it, the gate times out.
        world.Timers.Created
            .Single(c => c.Interval == SubPixelProbeCoordinator.ResumeLandingTimeout)
            .Tick();

        Check.WithCustomMessage("A timed-out resume did not show exactly one start-failure notice")
            .That(notices).HasSize(1);
        Check.WithCustomMessage("The timeout must keep today's refusal log line")
            .That(refused).ContainsExactly(ProbeRefusal.Paused);
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        // A late state change must not resurrect the start — nor a second notice.
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The abandoned start fired later")
            .That(world.Modal.ProbeOpen).IsFalse();
        Check.That(notices).HasSize(1);
    }

    [Fact]
    public void ARefusalAfterTheResume_ShowsExactlyOneNotice()
    {
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = StartPausedRequest(world);

        // A gate that was clear on activation becomes live before the resume lands.
        world.Settings.EyeCare.Enabled = true;
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("A post-resume gate refusal did not show exactly one start-failure notice")
            .That(notices).HasSize(1);
        Check.WithCustomMessage("The refused gate must keep today's refusal log line")
            .That(refused).ContainsExactly(ProbeRefusal.EyeCareOn);
        Check.WithCustomMessage("The probe must not start over the refused gate")
            .That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void ARepauseMessageBeforeTheStateChangeLands_ShowsNoNotice()
    {
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = StartPausedRequest(world);

        // The owner re-paused; the Paused message abandons the pending start.
        world.Messenger.Send(new StateChangedMessage(InteractionState.Paused));

        Check.WithCustomMessage("The owner's own re-pause showed a start-failure notice")
            .That(notices).IsEmpty();
        Check.WithCustomMessage("The re-pause must keep today's refusal log line")
            .That(refused).ContainsExactly(ProbeRefusal.Paused);
    }

    [Fact]
    public void ARepauseThatBeatsTheStateChange_ShowsNoNotice()
    {
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = StartPausedRequest(world);

        // The owner re-paused before the posted FreeLook message was delivered: the resume
        // handler's live re-read finds Paused and abandons — again the owner's own choice.
        world.Engine.InteractionManager.TogglePause();
        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.WithCustomMessage("A re-pause that beats its state change showed a start-failure notice")
            .That(notices).IsEmpty();
        Check.That(refused).ContainsExactly(ProbeRefusal.Paused);

        world.Pump();
        Check.That(world.Modal.ProbeOpen).IsFalse();
        Check.That(notices).IsEmpty();
    }

    [Fact]
    public void TheSuccessPath_ShowsNoNotice()
    {
        using var world = new ProbeTestWorld();
        var notices = StartPausedRequest(world);

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();

        Check.WithCustomMessage("The probe did not start on the success path")
            .That(world.Modal.ProbeOpen).IsTrue();
        Check.WithCustomMessage("A successful resumed start showed a notice")
            .That(notices).IsEmpty();
    }

    [Fact]
    public void TheNormalNotPausedPath_Refusal_ShowsNoNotice()
    {
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = new List<(string Message, string CloseLabel)>();
        world.Coordinator.StartFailedNoticeSink = (message, closeLabel) => notices.Add((message, closeLabel));

        // The normal C10 path: no pause, so no resume gate — the queued start refuses on a gate
        // that went live after activation. Its refusal text belongs to the button's activation
        // check; the card stays out of it.
        world.Coordinator.RequestStart(ManualClose(world));
        world.Settings.EyeCare.Enabled = true;
        world.Pump();

        Check.WithCustomMessage("The normal path's queued refusal showed a start-failure notice")
            .That(notices).IsEmpty();
        Check.That(refused).ContainsExactly(ProbeRefusal.EyeCareOn);
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void AThrowingSink_DoesNotBreakTheAbandonment()
    {
        // fixup-01 F5 (review M-G): the notice is best-effort. A card that cannot show (its
        // presenter throws) must not break the abandonment that called it — the generation bump,
        // the refusal log line and the retired start all still happen, and no exception escapes
        // the timeout tick.
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        int sinkCalls = 0;
        StartPausedRequest(world, (_, _) =>
        {
            sinkCalls++;
            throw new InvalidOperationException("the notice card is down");
        });

        world.Timers.Created
            .Single(c => c.Interval == SubPixelProbeCoordinator.ResumeLandingTimeout)
            .Tick();

        Check.WithCustomMessage("the timeout did not call the throwing sink exactly once")
            .That(sinkCalls).IsEqualTo(1);
        Check.WithCustomMessage("a throwing sink broke the abandonment's refusal log line")
            .That(refused).ContainsExactly(ProbeRefusal.Paused);
        Check.That(world.Coordinator.LiveSessionForTest).IsNull();

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Pump();
        Check.WithCustomMessage("the abandoned start fired later")
            .That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void APausedRefusalOnTheResumedStart_ShowsNoNotice()
    {
        // fixup-01 F4: if the owner pauses again between the resume landing and the posted
        // start's run, that queued BeginStart (fromResumedStart: true) refuses with Paused — and
        // a Paused refusal IS the owner's own re-pause, so the code must match the comment and
        // show no notice, only the log line.
        using var world = new ProbeTestWorld();
        var refused = new List<ProbeRefusal>();
        world.Coordinator.RefusalLogger = refused.Add;
        var notices = StartPausedRequest(world);

        world.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        world.Engine.InteractionManager.TogglePause();
        world.Pump();

        Check.WithCustomMessage("the queued resumed start did not keep today's refusal log line")
            .That(refused).ContainsExactly(ProbeRefusal.Paused);
        Check.WithCustomMessage("a Paused refusal on the resumed path showed the start-failure notice")
            .That(notices).IsEmpty();
        Check.That(world.Modal.ProbeOpen).IsFalse();
    }

    [Fact]
    public void TheNotice_SpeaksTheRunningLanguage_AndTheCardsOkButton()
    {
        using var world = new ProbeTestWorld();
        var notices = StartPausedRequest(world);
        world.Timers.Created
            .Single(c => c.Interval == SubPixelProbeCoordinator.ResumeLandingTimeout)
            .Tick();

        Check.That(notices).HasSize(1);
        // English literals, per the RestartNoticeTests doctrine — T(key) on both sides would pass
        // on a raw key. The suite's StringTable.Current is the embedded English, so the captured
        // text must BE the shipped English sentence.
        Check.WithCustomMessage("The notice message is not the shipped English start-failure text")
            .That(notices[0].Message)
            .IsEqualTo("The sub-pixel test did not start. Open it again from Diagnostics.");
        Check.WithCustomMessage("The notice's close button is not the card's OK acknowledgement")
            .That(notices[0].CloseLabel).IsEqualTo("✓ OK");
    }

    [Fact]
    public void TheStartFailureKeys_HaveGermanEntries_ThatResolveGerman()
    {
        // GermanDraft builds a test table with every surface effective and captures the load
        // warnings, so a missing or fallback German entry shows here as English — no process
        // globals are touched.
        foreach (var key in new[]
                 {
                     "settings.diagnostics.subpixel.notice.start_failed",
                     "settings.diagnostics.subpixel.notice.ok",
                 })
        {
            var german = GermanDraft.TextOf(key, "");

            Check.WithCustomMessage($"{key} resolved its English in the German table: no German entry (or the surface fell back).")
                .That(GermanDraft.Table.T(key)).IsEqualTo(german);
            Check.That(german.Length).IsStrictlyGreaterThan(0);
        }

        Check.WithCustomMessage("settings.diagnostics.subpixel.notice.start_failed's German is its English text")
            .That(GermanDraft.TextOf("settings.diagnostics.subpixel.notice.start_failed", ""))
            .IsNotEqualTo("The sub-pixel test did not start. Open it again from Diagnostics.");
    }
}
