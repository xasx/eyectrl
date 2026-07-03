namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ManualSelectionGazeIndicatorViewModelTests
{
    [Fact]
    public void Receive_EndManualSelectionMessage_HidesIndicator()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new EndManualSelectionMessage());

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Receive_ManualSelectionHiddenMessage_HidesIndicatorToo()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new ManualSelectionHiddenMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.FixationProgress).IsEqualTo(0.0);
    }
}
