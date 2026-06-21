namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActivationButtonViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void ShowsButton_OnShowMessage()
    {
        var vm = new ActivationButtonViewModel(_settings, _messenger);
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(500);
        Check.That(vm.Position.Y).IsEqualTo(300);
    }

    [Fact]
    public void HidesButton_OnHideMessage()
    {
        var vm = new ActivationButtonViewModel(_settings, _messenger);
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new HideActivationButtonMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.DwellProgress).IsEqualTo(0);
    }

    [Fact]
    public void AccumulatesDwell_OnGazeWithinRadius()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 1000;
        var vm = new ActivationButtonViewModel(settings, _messenger);

        // Show button at (500, 300)
        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));

        // Send gaze within radius for half the dwell time
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 500_000)); // 500ms

        Check.That(vm.DwellProgress > 0.4).IsTrue();
        Check.That(vm.DwellProgress < 0.6).IsTrue();
    }

    [Fact]
    public void SendsActivationCompleted_OnDwellComplete()
    {
        var settings = new EyeCtrlSettings();
        settings.Dwell.ActivationButtonDwellMs = 200; // Minimum valid value
        var vm = new ActivationButtonViewModel(settings, _messenger);

        bool activated = false;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => activated = true);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(500, 300)));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(500, 300), 200_000)); // 200ms > 100ms dwell

        Check.That(activated).IsTrue();
    }
}
