namespace EyeCtrl.App.Tests.Services;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// The activation-outcome tracker (salvaged from #322's R3, round-button B1) — exactly ONE
/// Information line per accepted activation, resolved at the session's end. Accept→action,
/// accept→close-without-action (the unintended-open count), accept→Pause; an EXPIRED offer writes no
/// outcome line (its <c>Offer expired unused</c> line already counts it); two accepts in a row
/// produce two lines with the right ids. There is one activation style again, so no line carries a
/// <c>style=</c> field.
/// <para>
/// #322 holistic fixup-01 F2: every session opens UNARMED and arms only on its own entry state
/// (<see cref="InteractionState.ActionMenuVisible"/> / <see cref="InteractionState.ManualSelectionActive"/>),
/// exactly as the real flow delivers them — the tests below send the entry state right after
/// <c>NoteAccepted</c>, and one test pins that a state change queued BEFORE the accept (the F2
/// race's stale FreeLook) is ignored. The exact line format is pinned in
/// <see cref="TheOutcomeLine_HasTheExactFormatTheToolParses"/>; the adapter-side accept note and
/// the race itself are pinned in <see cref="GazeEngineAdapterActivationOutcomeTests"/>.
/// </para>
/// </summary>
public class ActivationOutcomeLogTests
{
    /// <summary>A deterministic clock the tracker reads: start + an offset the test advances.</summary>
    private sealed class FakeClock
    {
        public long Now;
        public long Tick() => Now;
    }

    private sealed record Harness(
        ActivationOutcomeLog Tracker, List<string> Log, FakeClock Clock, WeakReferenceMessenger Messenger);

    private static Harness Make()
    {
        var lines = new List<string>();
        var clock = new FakeClock { Now = 10_000 };
        var messenger = new WeakReferenceMessenger();
        var tracker = new ActivationOutcomeLog(messenger, lines.Add, clock.Tick);
        return new Harness(tracker, lines, clock, messenger);
    }

    /// <summary>The session's own entry state as the real flow posts it: the accept's
    /// <c>SetState</c> for the selection mode the accept ran under.</summary>
    private static void Arm(Harness h, SelectionMode mode) =>
        h.Messenger.Send(new StateChangedMessage(
            mode == SelectionMode.ManualSelection
                ? InteractionState.ManualSelectionActive
                : InteractionState.ActionMenuVisible));

    [Fact]
    public void TheOutcomeLine_HasTheExactFormatTheToolParses()
    {
        Check.That(ActivationOutcomeLog.OutcomeLine(12, "ManualSelection", "closed", 1500))
            .IsEqualTo("Activation outcome #12 mode=ManualSelection result=closed heldMs=1500");
        Check.That(ActivationOutcomeLog.OutcomeLine(3, "None", "action", 249.6))
            .IsEqualTo("Activation outcome #3 mode=None result=action heldMs=250");
        Check.That(ActivationOutcomeLog.OutcomeLine(7, "AutoSelection", "other", 61, "RepeatMode"))
            .IsEqualTo("Activation outcome #7 mode=AutoSelection result=other state=RepeatMode heldMs=61");
        // Ordinal, not IsEqualTo alone: NFluent's string equality forgives a trailing \r.
        Check.That(ActivationOutcomeLog.OutcomeLine(3, "None", "action", 250)
            .Equals("Activation outcome #3 mode=None result=action heldMs=250", StringComparison.Ordinal)).IsTrue();
    }

    [Fact]
    public void Accept_ThenAction_WritesExactlyOneActionResultLine()
    {
        var h = Make();

        h.Tracker.NoteAccepted(4, SelectionMode.ManualSelection);
        Arm(h, SelectionMode.ManualSelection);
        h.Clock.Now += 2_500;
        h.Messenger.Send(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(1, 1)));

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).IsEqualTo(
            "Activation outcome #4 mode=ManualSelection result=action heldMs=2500");

        // The FreeLook that follows the action must not write a second line.
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);
    }

    [Fact]
    public void AStateChangeBeforeTheOwnEntryState_IsIgnored_TheSessionResolvesAtItsRealEnd()
    {
        // Holistic fixup-01 F2: the gaze thread can post a run-end FreeLook between the fire and
        // the UI-thread accept, and the posted message is delivered AFTER NoteAccepted opened the
        // session. Without the arming gate that stale FreeLook resolves the fresh session as
        // `result=closed heldMs=0` and the real action finds no session left — a one-way bias on
        // the owner's unintended-open count.
        var h = Make();

        h.Tracker.NoteAccepted(9, SelectionMode.None);
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));   // queued BEFORE the accept
        h.Messenger.Send(new StateChangedMessage(InteractionState.Fixation));   // older world too
        h.Messenger.Send(new StateChangedMessage(InteractionState.Paused));     // even this must not land
        Check.WithCustomMessage(
                "nothing delivered before the session's own entry state may resolve it; logged: "
                + string.Join(" | ", h.Log))
            .That(h.Log).IsEmpty();

        h.Clock.Now += 800;
        Arm(h, SelectionMode.None);                                             // the accept's own entry state
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));   // the real end

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).IsEqualTo(
            "Activation outcome #9 mode=None result=closed heldMs=800");
    }

    [Fact]
    public void AnActionBeforeTheOwnEntryState_IsNotAttributedToTheSession()
    {
        // The belt to the arming gate's braces: an ActionRequested that predates the session's
        // surfaces (impossible in the real flow — its menu does not exist yet) must not resolve
        // the unarmed session either.
        var h = Make();

        h.Tracker.NoteAccepted(5, SelectionMode.None);
        h.Messenger.Send(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(1, 1)));
        Check.That(h.Log).IsEmpty();

        Arm(h, SelectionMode.None);
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).Contains("result=closed");
    }

    [Fact]
    public void Accept_ThenCloseWithoutAction_WritesExactlyOneClosedResultLine()
    {
        // The unintended-open count: Back, ✕, the timeout or looking away all end in the state
        // machine's return to FreeLook with no action on the way.
        var h = Make();

        h.Tracker.NoteAccepted(9, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Clock.Now += 800;
        h.Messenger.Send(new StateChangedMessage(InteractionState.ManualSelectionActionMenuVisible));  // interior
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).IsEqualTo(
            "Activation outcome #9 mode=None result=closed heldMs=800");
    }

    [Fact]
    public void Accept_ThenPause_WritesExactlyOnePausedResultLine()
    {
        var h = Make();

        h.Tracker.NoteAccepted(5, SelectionMode.AutoSelection);
        Arm(h, SelectionMode.AutoSelection);
        h.Messenger.Send(new StateChangedMessage(InteractionState.Paused));

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).Contains("result=paused");
        // Resume (Paused → FreeLook) is not a second end for the same session.
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);
    }

    [Fact]
    public void AnExpiryOrCloseOfTheOffer_WritesNoOutcomeLine()
    {
        // Only an ACCEPT notes a session. An expired (cancelled) offer — and the offer-closed
        // traffic that follows every offer death — must produce no outcome line; the
        // `Offer expired unused` line already counts cancels.
        var h = Make();

        h.Messenger.Send(new HideActivationButtonMessage());
        h.Messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(1, 1), 10));
        h.Messenger.Send(new HideActivationButtonMessage());
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.WithCustomMessage("no accept, no outcome line; logged: " + string.Join(" | ", h.Log))
            .That(h.Log).IsEmpty();

        // And the interior states of a live session do not resolve it early either.
        h.Tracker.NoteAccepted(2, SelectionMode.ManualSelection);
        Arm(h, SelectionMode.ManualSelection);
        foreach (var state in new[]
                 {
                     InteractionState.ManualSelectionActive,
                     InteractionState.ManualSelectionActionMenuVisible,
                     InteractionState.Fixation,
                     InteractionState.ActionMenuVisible,
                 })
            h.Messenger.Send(new StateChangedMessage(state));
        Check.WithCustomMessage("the session's own states never end it; logged: " + string.Join(" | ", h.Log))
            .That(h.Log).IsEmpty();

        // The offer's OWN close follows every accept (RequestActivationOfferAccept closes the
        // accepted offer, and the adapter marshals its hide): that hide must not end the session
        // either — only the activation's surfaces ending (FreeLook / an action / Pause) may.
        h.Messenger.Send(new HideActivationButtonMessage());
        Check.WithCustomMessage(
                "the accepted offer's own hide must not resolve the session; logged: "
                + string.Join(" | ", h.Log))
            .That(h.Log).IsEmpty();

        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);
    }

    [Fact]
    public void TwoAcceptsInARow_TwoLinesWithTheRightIds()
    {
        var h = Make();

        h.Tracker.NoteAccepted(11, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Clock.Now += 300;
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        h.Tracker.NoteAccepted(12, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Clock.Now += 500;
        h.Messenger.Send(new ActionRequestedMessage(InteractionAction.LeftClick, new PhysicalPoint(1, 1)));

        Check.That(h.Log).HasSize(2);
        Check.That(h.Log[0]).IsEqualTo(
            "Activation outcome #11 mode=None result=closed heldMs=300");
        Check.That(h.Log[1]).IsEqualTo(
            "Activation outcome #12 mode=None result=action heldMs=500");
    }

    [Fact]
    public void ADragOrScrollStart_IsAnActionResult()
    {
        var h = Make();

        h.Tracker.NoteAccepted(6, SelectionMode.ManualSelection);
        Arm(h, SelectionMode.ManualSelection);
        h.Messenger.Send(new StateChangedMessage(InteractionState.Dragging));
        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).Contains("result=action");

        h.Tracker.NoteAccepted(7, SelectionMode.ManualSelection);
        Arm(h, SelectionMode.ManualSelection);
        h.Messenger.Send(new StateChangedMessage(InteractionState.ScrollMode));
        Check.That(h.Log).HasSize(2);
        Check.That(h.Log[1]).Contains("result=action");

        // The drag's own end (back to FreeLook) resolves nothing further.
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(2);
    }

    [Fact]
    public void AnUnexpectedState_IsNamedAsOther()
    {
        var h = Make();

        h.Tracker.NoteAccepted(8, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));

        Check.That(h.Log).HasSize(1);
        Check.That(h.Log[0]).IsEqualTo(
            "Activation outcome #8 mode=None result=other state=RepeatMode heldMs=0");
    }

    [Fact]
    public void ASecondAcceptBeforeTheFirstEnds_ClosesTheFirstAsSuperseded()
    {
        // The state machine cannot accept twice without ending the first session; this pins the
        // invariant (one line per accept) against a future wiring change anyway.
        var h = Make();

        h.Tracker.NoteAccepted(1, SelectionMode.None);
        h.Clock.Now += 120;
        h.Tracker.NoteAccepted(2, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Clock.Now += 40;
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));

        Check.That(h.Log).HasSize(2);
        Check.That(h.Log[0]).Contains("#1").And.Contains("result=other state=superseded");
        Check.That(h.Log[1]).IsEqualTo(
            "Activation outcome #2 mode=None result=closed heldMs=40");
    }

    [Fact]
    public void Dispose_Unsubscribes_TheLogGoesSilent()
    {
        var h = Make();

        h.Tracker.NoteAccepted(3, SelectionMode.None);
        Arm(h, SelectionMode.None);
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);

        h.Tracker.Dispose();

        h.Tracker.NoteAccepted(4, SelectionMode.None);
        h.Messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(h.Log).HasSize(1);
    }
}
