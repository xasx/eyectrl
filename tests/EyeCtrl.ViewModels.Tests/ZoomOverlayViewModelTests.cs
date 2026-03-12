namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ZoomOverlayViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsZoom_OnStartZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
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
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
        Assert.True(vm.IsVisible);

        _messenger.Send(new EndZoomMessage());
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ComputesSideButtonPositions_OnStartZoom()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));

        // Display radius = 150 * 2.0 = 300
        // Button X = 500 + 300 + 20 (gap) + 50 (button radius) = 870
        // Close Y = 500 - 50 - 10 = 440  (above center)
        // Recapture Y = 500 + 50 + 10 = 560  (below center)
        Assert.True(vm.CloseButtonCenter.X > 500, "Close button should be to the right of zoom center");
        Assert.True(vm.RecaptureButtonCenter.X > 500, "Recapture button should be to the right");
        Assert.True(vm.CloseButtonCenter.Y < 500, "Close button should be above center");
        Assert.True(vm.RecaptureButtonCenter.Y > 500, "Recapture button should be below center");
    }

    [Fact]
    public void CloseButton_Dwell_SendsZoomCloseMessage()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
        bool closeSent = false;
        object recipient = new();
        _messenger.Register<ZoomCloseMessage>(recipient, (_, _) => closeSent = true);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));

        // Simulate sustained gaze on close button for enough time
        var closePos = vm.CloseButtonCenter;
        for (int i = 0; i < 25; i++) // 25 x 33ms = 825ms > 600ms dwell
            _messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

        Assert.True(closeSent);
    }

    [Fact]
    public void RecaptureButton_Dwell_SendsZoomRecaptureMessage()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
        bool recaptureSent = false;
        object recipient = new();
        _messenger.Register<ZoomRecaptureMessage>(recipient, (_, _) => recaptureSent = true);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));

        var recapturePos = vm.RecaptureButtonCenter;
        for (int i = 0; i < 25; i++)
            _messenger.Send(new GazeCursorMovedMessage(recapturePos, i * 33000));

        Assert.True(recaptureSent);
    }

    [Fact]
    public void ClearsDwellTargets_OnEndZoom()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0));
        var closePos = vm.CloseButtonCenter;

        _messenger.Send(new EndZoomMessage());

        // After EndZoom, gaze on old button position should NOT trigger
        bool closeSent = false;
        object recipient = new();
        _messenger.Register<ZoomCloseMessage>(recipient, (_, _) => closeSent = true);

        for (int i = 0; i < 25; i++)
            _messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

        Assert.False(closeSent);
    }

    [Fact]
    public void HidesZoom_OnZoomHiddenMessage()
    {
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
        Assert.True(vm.IsVisible);

        _messenger.Send(new ZoomHiddenMessage());

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ReshowsZoom_AfterZoomHiddenThenStartZoom()
    {
        // Simulates: zoom shown → fixation detected (ZoomHidden) → Back pressed (StartZoom again)
        var vm = new ZoomOverlayViewModel(new DwellSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));
        _messenger.Send(new ZoomHiddenMessage());
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0));

        Assert.True(vm.IsVisible);
    }
}
