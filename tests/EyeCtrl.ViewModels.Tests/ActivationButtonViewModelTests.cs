namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The activation feedback is a pure consumer since ticket 3: Core's fixation rule owns the valid-time
/// clock and publishes both facts — per-sample progress on the frame, completion as
/// <see cref="FixationActivatedMessage"/>. These pin the consumer contract, including the one-completion
/// latch that has to survive the activation/end dispatcher races.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ActivationButtonViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    private static GazeCursorMovedMessage Progress(double progress) =>
        new(new GazeFrame(new PhysicalPoint(500, 300), 0, IsInGap: false, ActivationProgress: progress));

    [Fact]
    public void ShowsButton_OnShowMessage()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(500);
        Check.That(vm.Position.Y).IsEqualTo(300);
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void HidesButton_OnHideMessage_AndZeroesProgress()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(Progress(0.6));
        Check.That(vm.DwellProgress).IsEqualTo(0.6);

        _messenger.Send(new HideActivationButtonMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void Progress_ComesFromCoreFrame_WhileVisible()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));

        _messenger.Send(Progress(0.35));

        Check.That(vm.DwellProgress).IsEqualTo(0.35);
    }

    [Fact]
    public void Progress_IsIgnored_Hidden_And_OnGapFrames()
    {
        var vm = new ActivationButtonViewModel(_messenger);

        // Hidden: nothing to show.
        _messenger.Send(Progress(0.8));
        Check.That(vm.DwellProgress).IsEqualTo(0);

        // Visible but gap-held: the frame carries a stale value and must not move the arc.
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(Progress(0.5));
        _messenger.Send(new GazeCursorMovedMessage(
            new GazeFrame(new PhysicalPoint(500, 300), 0, IsInGap: true, ActivationProgress: 0.95)));
        Check.That(vm.DwellProgress).IsEqualTo(0.5);
    }

    [Fact]
    public void Activation_CompletesOnce_AndPublishesTheCueAfterTheCompletion()
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

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(new FixationActivatedMessage(1_000_000, 1_000_000));
        _messenger.Send(new FixationActivatedMessage(1_000_000, 1_000_000)); // a duplicate must not re-complete

        Check.That(completions).IsEqualTo(1);
        Check.That(cues).IsEqualTo(1);
        Check.That(cueSawCompletion).IsTrue();  // effect first, then the cue (D8 ordering preserved)
        Check.That(vm.DwellProgress).IsEqualTo(1);
    }

    [Fact]
    public void Activation_AfterHide_IsIgnored()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        int cues = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);
        _messenger.Register<DwellActivationMessage>(_messenger, (_, _) => cues++);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(new HideActivationButtonMessage());

        // The end won the dispatcher race: a late activation for the dead run must do nothing — no
        // completion, no cue, and no progress on a button that is down.
        _messenger.Send(new FixationActivatedMessage(1_000_000, 1_000_000));

        Check.That(completions).IsEqualTo(0);
        Check.That(cues).IsEqualTo(0);
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void ActivationThenImmediateEnd_AndANewFixation_EachCompleteExactlyOnce()
    {
        var vm = new ActivationButtonViewModel(_messenger);
        int completions = 0;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => completions++);

        // Fixation 1: activation then immediate end.
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(new FixationActivatedMessage(1_000_000, 1_000_000));
        _messenger.Send(new HideActivationButtonMessage());
        Check.That(completions).IsEqualTo(1);

        // Fixation 2 at a new place: the latch was reset by the show, so it completes again — and a late
        // message from the first run's tail cannot ride it (it would have to arrive while hidden).
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(700, 500)));
        _messenger.Send(new FixationActivatedMessage(4_000_000, 1_000_000));
        Check.That(completions).IsEqualTo(2);
    }
}
