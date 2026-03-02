namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ActivationButtonViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly DwellSettings _dwellSettings = new();

    [Fact]
    public void ShowsButton_OnShowMessage()
    {
        var vm = new ActivationButtonViewModel(_dwellSettings, _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Position.X);
        Assert.Equal(300, vm.Position.Y);
    }

    [Fact]
    public void HidesButton_OnHideMessage()
    {
        var vm = new ActivationButtonViewModel(_dwellSettings, _messenger);
        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new HideActivationButtonMessage());

        Assert.False(vm.IsVisible);
        Assert.Equal(0, vm.DwellProgress);
    }

    [Fact]
    public void AccumulatesDwell_OnGazeWithinRadius()
    {
        var settings = new DwellSettings { ActivationButtonDwellMs = 1000 };
        var vm = new ActivationButtonViewModel(settings, _messenger);

        // Show button at (500, 300)
        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));

        // Send gaze within radius for half the dwell time
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 500_000)); // 500ms

        Assert.True(vm.DwellProgress > 0.4);
        Assert.True(vm.DwellProgress < 0.6);
    }

    [Fact]
    public void SendsActivationCompleted_OnDwellComplete()
    {
        var settings = new DwellSettings { ActivationButtonDwellMs = 100 };
        var vm = new ActivationButtonViewModel(settings, _messenger);

        bool activated = false;
        _messenger.Register<ActivationCompletedMessage>(_messenger, (_, _) => activated = true);

        _messenger.Send(new ShowActivationButtonMessage(new ScreenPoint(500, 300)));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 0));
        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(500, 300), 200_000)); // 200ms > 100ms dwell

        Assert.True(activated);
    }
}
