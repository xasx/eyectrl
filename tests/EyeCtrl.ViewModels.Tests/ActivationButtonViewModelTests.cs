namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The activation feedback is a pure consumer: Core's offer engine owns the button's lifetime and its
/// button-dwell clock, publishing show/close messages, per-sample progress on the frame, and a fired
/// offer's snapshot. These pin the consumer contract, including the one-completion latch that has to
/// survive the fire/close dispatcher races — and, since ticket 8, that the retired age signal
/// (<see cref="FixationActivatedMessage"/>) completes nothing.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ActivationButtonViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    private static readonly PhysicalPoint ButtonCenter = new(500, 300);

    private static ActivationOffer Offer(long id, PhysicalPoint? point = null) =>
        new(id, point ?? new PhysicalPoint(200, 150), ButtonCenter, LayoutConstants.ActivationButtonHitRadiusPx,
            ExpiresAtUs: 1_000_000);

    private static GazeCursorMovedMessage Progress(double progress) =>
        new(new GazeFrame(new PhysicalPoint(200, 150), 0, IsInGap: false, ButtonDwellProgress: progress));

    private static ShowActivationButtonMessage Show(PhysicalPoint? center = null) =>
        new(center ?? ButtonCenter, LayoutConstants.ActivationButtonHitRadiusPx);

    [Fact]
    public void ShowsButton_OnShowMessage()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(Show());

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.ButtonCenter.X).IsEqualTo(500);
        Check.That(vm.ButtonCenter.Y).IsEqualTo(300);
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void Show_CarriesTheHitRadius_TheHaloIsSizedFrom()
    {
        // The offer's hit radius travels on the show message: the drawn halo's outer bound can never
        // exceed the area the dwell is counted within.
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(new ShowActivationButtonMessage(ButtonCenter, 150));
        Check.That(vm.HitRadiusPixels).IsEqualTo(150);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(700, 300), 175));
        Check.That(vm.HitRadiusPixels).IsEqualTo(175);
    }

    [Fact]
    public void ExternalDwellActive_HidesTheHalo_AndEndingItDoesNotReShow()
    {
        // A cooperating app draws its own feedback at the point it takes over; the button must not sit
        // frozen on top of it. Only a fresh offer may bring it back.
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(Show());
        _messenger.Send(Progress(0.4));
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.DwellProgress).IsEqualTo(0);

        _messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void SuppressionReleaseSequence_ReShowsAtTheFreshOffer_WithExactlyOneCompletion()
    {
        // External dwell hides the feedback; a late fire for the hidden offer is dropped, and the
        // fresh offer at B completes exactly once.
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);

        _messenger.Send(Show(new PhysicalPoint(100, 100)));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.ButtonCenter.X).IsEqualTo(100);

        // The cooperating app takes over; a late fire for the hidden offer must not complete.
        _messenger.Send(new ExternalDwellActiveMessage(true));
        _messenger.Send(new ActivationOfferFiredMessage(Offer(1)));
        Check.That(vm.IsVisible).IsFalse();
        Check.That(completions).IsEqualTo(0);

        // The external app's end is not an offer, so nothing re-shows until one arrives.
        _messenger.Send(new HideActivationButtonMessage());
        _messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsFalse();

        // The fresh offer re-shows at B, and its fire completes exactly once.
        _messenger.Send(Show(new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.ButtonCenter.X).IsEqualTo(500);
        Check.That(vm.ButtonCenter.Y).IsEqualTo(300);

        _messenger.Send(new ActivationOfferFiredMessage(Offer(2)));
        Check.That(completions).IsEqualTo(1);
    }

    [Fact]
    public void HidesButton_OnHideMessage_AndZeroesProgress()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(Show());
        _messenger.Send(Progress(0.6));
        Check.That(vm.DwellProgress).IsEqualTo(0.6);

        _messenger.Send(new HideActivationButtonMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void Progress_ComesFromTheButtonDwellFrame_WhileVisible()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(Show());

        _messenger.Send(Progress(0.35));

        Check.That(vm.DwellProgress).IsEqualTo(0.35);
    }

    [Fact]
    public void Progress_At250Ms_ComesFromTheButtonDwell_WhileTheAgeIsZero()
    {
        // The plan's halo row: at ~250 ms into the look the halo follows the button's progress (~0.36)
        // while ActivationProgress is 0. The VM reads the NEW frame field, never the age scalar.
        // Mutation: read Frame.ActivationProgress → this stays 0 and goes red.
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(Show());

        _messenger.Send(new GazeCursorMovedMessage(new GazeFrame(
            new PhysicalPoint(200, 150), 250_000, IsInGap: false,
            ActivationProgress: 0.0, ButtonDwellProgress: 250.0 / 700.0)));

        Check.That(vm.DwellProgress).IsCloseTo(0.357, 1e-3);
    }

    [Fact]
    public void Progress_IsIgnored_Hidden_And_OnGapFrames()
    {
        var vm = new ActivationButtonViewModel(_messenger);

        // Hidden: nothing to show.
        _messenger.Send(Progress(0.8));
        Check.That(vm.DwellProgress).IsEqualTo(0);

        // Visible but gap-held: the frame carries a stale value and must not move the arc.
        _messenger.Send(Show());
        _messenger.Send(Progress(0.5));
        _messenger.Send(new GazeCursorMovedMessage(
            new GazeFrame(new PhysicalPoint(200, 150), 0, IsInGap: true, ButtonDwellProgress: 0.95)));
        Check.That(vm.DwellProgress).IsEqualTo(0.5);
    }

    [Fact]
    public void Fire_CompletesOnce_AndPublishesTheCueAfterTheCompletion()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        int cues = 0;
        bool cueSawCompletion = false;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);
        _messenger.Register<DwellActivationMessage>(_messenger, (_, _) =>
        {
            cues++;
            cueSawCompletion = completions > 0;
        });

        var offer = Offer(7);
        _messenger.Send(Show());
        _messenger.Send(new ActivationOfferFiredMessage(offer));
        _messenger.Send(new ActivationOfferFiredMessage(offer)); // a duplicate must not re-complete

        Check.That(completions).IsEqualTo(1);
        Check.That(cues).IsEqualTo(1);
        Check.That(cueSawCompletion).IsTrue();  // effect first, then the cue (D8 ordering preserved)
        Check.That(vm.DwellProgress).IsEqualTo(1);
    }

    [Fact]
    public void Fire_CarriesTheOfferSnapshot_ThroughToTheCompletion()
    {
        // The completion hand-off must be for the offer that fired: a stale snapshot is what lets Core
        // reject it without touching whatever offer is live now (review A F2).
        var vm = new ActivationButtonViewModel(_messenger);
        ActivationCompletedMessage? completed = null;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, m) => completed = m);

        var offer = Offer(11, new PhysicalPoint(321, 654));
        _messenger.Send(Show());
        _messenger.Send(new ActivationOfferFiredMessage(offer));

        Check.That(completed).IsNotNull();
        Check.That(completed!.Offer.Id).IsEqualTo(11);
        Check.That(completed.Offer.Point.X).IsEqualTo(321);
    }

    [Fact]
    public void Fire_AfterHide_IsIgnored()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        int cues = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);
        _messenger.Register<DwellActivationMessage>(_messenger, (_, _) => cues++);

        _messenger.Send(Show());
        _messenger.Send(new HideActivationButtonMessage());

        // The close won the dispatcher race: a late fire for the dead offer must do nothing — no
        // completion, no cue, and no progress on a button that is down.
        _messenger.Send(new ActivationOfferFiredMessage(Offer(1)));

        Check.That(completions).IsEqualTo(0);
        Check.That(cues).IsEqualTo(0);
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void FireThenImmediateClose_AndANewOffer_EachCompleteExactlyOnce()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);

        // Offer 1: fire then immediate close.
        _messenger.Send(Show());
        _messenger.Send(new ActivationOfferFiredMessage(Offer(1)));
        _messenger.Send(new HideActivationButtonMessage());
        Check.That(completions).IsEqualTo(1);

        // Offer 2 at a new place: the latch was reset by the show, so it completes again — and a late
        // message from the first offer's tail cannot ride it (it would have to arrive while hidden).
        _messenger.Send(Show(new PhysicalPoint(700, 500)));
        _messenger.Send(new ActivationOfferFiredMessage(Offer(2)));
        Check.That(completions).IsEqualTo(2);
    }

    [Fact]
    public void AgeSignal_CompletesNothing()
    {
        // Ticket 8: the valid-time age is replay-only. A visible button plus any number of age
        // completions must produce no completion hand-off and no cue — "resting never activates" at
        // the VM level. Mutation: restore the VM's FixationActivatedMessage recipient (and its
        // completion send) → a completion arrives here and this goes red.
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        int cues = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);
        _messenger.Register<DwellActivationMessage>(_messenger, (_, _) => cues++);

        _messenger.Send(Show());
        _messenger.Send(new FixationActivatedMessage(1_000_000, 1_000_000));
        _messenger.Send(new FixationActivatedMessage(1_100_000, 1_100_000));

        Check.That(completions).IsEqualTo(0);
        Check.That(cues).IsEqualTo(0);
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }
}
