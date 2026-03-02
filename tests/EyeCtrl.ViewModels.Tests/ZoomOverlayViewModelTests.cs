namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels.Messages;

public class ZoomOverlayViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsZoom_OnStartZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(_messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Center.X);
        Assert.Equal(300, vm.Center.Y);
        Assert.Equal(150, vm.Radius);
        Assert.Equal(2.0, vm.Magnification);
    }

    [Fact]
    public void HidesZoom_OnEndZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(_messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
        Assert.True(vm.IsVisible);

        _messenger.Send(new EndZoomMessage());
        Assert.False(vm.IsVisible);
    }
}
