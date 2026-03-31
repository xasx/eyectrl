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
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0, new ScreenPoint(500, 300)));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Center.X);
        Assert.Equal(300, vm.Center.Y);
        Assert.Equal(150, vm.Radius);
        Assert.Equal(2.0, vm.Magnification);
    }

    [Fact]
    public void HidesZoom_OnEndZoomMessage()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0, new ScreenPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new EndZoomMessage());
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ComputesSideButtonPositions_OnStartZoom()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0, new ScreenPoint(500, 500)));

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
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        bool closeSent = false;
        object recipient = new();
        _messenger.Register<ZoomCloseMessage>(recipient, (_, _) => closeSent = true);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0, new ScreenPoint(500, 500)));

        // Simulate sustained gaze on close button for enough time
        var closePos = vm.CloseButtonCenter;
        for (int i = 0; i < 25; i++) // 25 x 33ms = 825ms > 600ms dwell
            _messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

        Assert.True(closeSent);
    }

    [Fact]
    public void RecaptureButton_Dwell_SendsZoomRecaptureMessage()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        bool recaptureSent = false;
        object recipient = new();
        _messenger.Register<ZoomRecaptureMessage>(recipient, (_, _) => recaptureSent = true);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0, new ScreenPoint(500, 500)));

        var recapturePos = vm.RecaptureButtonCenter;
        for (int i = 0; i < 25; i++)
            _messenger.Send(new GazeCursorMovedMessage(recapturePos, i * 33000));

        Assert.True(recaptureSent);
    }

    [Fact]
    public void ClearsDwellTargets_OnEndZoom()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 500), 150, 2.0, new ScreenPoint(500, 500)));
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
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0, new ScreenPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new ZoomHiddenMessage());

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ReshowsZoom_AfterZoomHiddenThenStartZoom()
    {
        // Simulates: zoom shown → fixation detected (ZoomHidden) → Back pressed (StartZoom again)
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0, new ScreenPoint(500, 300)));
        _messenger.Send(new ZoomHiddenMessage());
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(500, 300), 150, 2.0, new ScreenPoint(500, 300)));

        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void StoresGazePoint_WhenProvided()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var gazePoint = new ScreenPoint(400, 200);

        _messenger.Send(new StartZoomMessage(new ScreenPoint(960, 540), 150, 2.0, gazePoint));

        Assert.Equal(gazePoint, vm.GazePoint);
        Assert.Equal(960, vm.Center.X); // Display center different from gaze
        Assert.Equal(540, vm.Center.Y);
    }

    [Fact]
    public void GazePoint_EqualsCenter_WhenInAtGazeMode()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var center = new ScreenPoint(500, 300);

        _messenger.Send(new StartZoomMessage(center, 150, 2.0, center));

        Assert.Equal(center, vm.GazePoint);
        Assert.Equal(center, vm.Center);
    }

    [Fact]
    public void SideButtons_PositionedRelativeToDisplayCenter_NotGazePoint()
    {
        var vm = new ZoomOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var displayCenter = new ScreenPoint(960, 540); // Screen center
        var gazePoint = new ScreenPoint(200, 100);     // Gaze at different location

        _messenger.Send(new StartZoomMessage(displayCenter, 150, 2.0, gazePoint));

        // Buttons should be positioned relative to display center (960, 540), not gaze (200, 100)
        Assert.True(vm.CloseButtonCenter.X > 960, "Close button X should be right of display center");
        Assert.True(vm.RecaptureButtonCenter.X > 960, "Recapture button X should be right of display center");

        // Not positioned relative to gaze point
        Assert.False(vm.CloseButtonCenter.X < 300, "Buttons should not be near gaze point");
    }
}
