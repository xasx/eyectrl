namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class CursorCircleViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void UpdatesPosition_OnGazeCursorMovedMessage()
    {
        var settings = new CursorSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new GazeCursorMovedMessage(new ScreenPoint(100, 200), 0));

        Assert.Equal(100, vm.Position.X);
        Assert.Equal(200, vm.Position.Y);
    }

    [Fact]
    public void ReadsAppearance_FromSettings()
    {
        var settings = new CursorSettings { CircleSize = 50, CircleColor = "#FF0000", CircleOpacity = 0.8 };
        var vm = new CursorCircleViewModel(settings, _messenger);

        Assert.Equal(50, vm.Size);
        Assert.Equal("#FF0000", vm.CircleColor);
        Assert.Equal(0.8, vm.Opacity);
    }

    [Fact]
    public void HidesWhenPaused_ShowsWhenResumed()
    {
        var settings = new CursorSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Assert.True(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.False(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsVisible);
    }
}
