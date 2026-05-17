namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ManualSelectionOverlayViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void ShowsManualSelection_OnStartManualSelectionMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));

        Assert.True(vm.IsVisible);
        Assert.Equal(500, vm.Center.X);
        Assert.Equal(300, vm.Center.Y);
        Assert.Equal(150, vm.Radius);
        Assert.Equal(2.0, vm.Magnification);
    }

    [Fact]
    public void HidesManualSelection_OnEndManualSelectionMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new EndManualSelectionMessage());
        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ComputesSideButtonPositions_OnStartManualSelection()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));

        // Display radius = 150 * 2.0 = 300
        // Button X = 500 + 300 + 20 (gap) + 50 (button radius) = 870
        // Close Y = 500 - 50 - 10 = 440  (above center)
        // Recapture Y = 500 + 50 + 10 = 560  (below center)
        Assert.True(vm.CloseButtonCenter.X > 500, "Close button should be to the right of ManualSelection center");
        Assert.True(vm.RecaptureButtonCenter.X > 500, "Recapture button should be to the right");
        Assert.True(vm.CloseButtonCenter.Y < 500, "Close button should be above center");
        Assert.True(vm.RecaptureButtonCenter.Y > 500, "Recapture button should be below center");
    }

    [Fact]
    public void CloseButton_Dwell_SendsManualSelectionCloseMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        bool closeSent = false;
        object recipient = new();
        _messenger.Register<ManualSelectionCloseMessage>(recipient, (_, _) => closeSent = true);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));

        // Simulate sustained gaze on close button for enough time
        var closePos = vm.CloseButtonCenter;
        for (int i = 0; i < 25; i++) // 25 x 33ms = 825ms > 600ms dwell
            _messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

        Assert.True(closeSent);
    }

    [Fact]
    public void RecaptureButton_Dwell_SendsManualSelectionRecaptureMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        bool recaptureSent = false;
        object recipient = new();
        _messenger.Register<ManualSelectionRecaptureMessage>(recipient, (_, _) => recaptureSent = true);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));

        var recapturePos = vm.RecaptureButtonCenter;
        for (int i = 0; i < 25; i++)
            _messenger.Send(new GazeCursorMovedMessage(recapturePos, i * 33000));

        Assert.True(recaptureSent);
    }

    [Fact]
    public void ClearsDwellTargets_OnEndManualSelection()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 500), 150, 2.0, new PhysicalPoint(500, 500)));
        var closePos = vm.CloseButtonCenter;

        _messenger.Send(new EndManualSelectionMessage());

        // After EndManualSelection, gaze on old button position should NOT trigger
        bool closeSent = false;
        object recipient = new();
        _messenger.Register<ManualSelectionCloseMessage>(recipient, (_, _) => closeSent = true);

        for (int i = 0; i < 25; i++)
            _messenger.Send(new GazeCursorMovedMessage(closePos, i * 33000));

        Assert.False(closeSent);
    }

    [Fact]
    public void HidesManualSelection_OnManualSelectionHiddenMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        Assert.True(vm.IsVisible);

        _messenger.Send(new ManualSelectionHiddenMessage());

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ReshowsManualSelection_AfterManualSelectionHiddenThenStartManualSelection()
    {
        // Simulates: ManualSelection shown → fixation detected (ManualSelectionHidden) → Back pressed (StartManualSelection again)
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        _messenger.Send(new ManualSelectionHiddenMessage());
        Assert.False(vm.IsVisible);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));

        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void StoresGazePoint_WhenProvided()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var gazePoint = new PhysicalPoint(400, 200);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(960, 540), 150, 2.0, gazePoint));

        Assert.Equal(gazePoint, vm.GazePoint);
        Assert.Equal(960, vm.Center.X); // Display center different from gaze
        Assert.Equal(540, vm.Center.Y);
    }

    [Fact]
    public void GazePoint_EqualsCenter_WhenInAtGazeMode()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var center = new PhysicalPoint(500, 300);

        _messenger.Send(new StartManualSelectionMessage(center, 150, 2.0, center));

        Assert.Equal(center, vm.GazePoint);
        Assert.Equal(center, vm.Center);
    }

    [Fact]
    public void SideButtons_PositionedRelativeToDisplayCenter_NotGazePoint()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var displayCenter = new PhysicalPoint(960, 540); // Screen center
        var gazePoint = new PhysicalPoint(200, 100);     // Gaze at different location

        _messenger.Send(new StartManualSelectionMessage(displayCenter, 150, 2.0, gazePoint));

        // Buttons should be positioned relative to display center (960, 540), not gaze (200, 100)
        Assert.True(vm.CloseButtonCenter.X > 960, "Close button X should be right of display center");
        Assert.True(vm.RecaptureButtonCenter.X > 960, "Recapture button X should be right of display center");

        // Not positioned relative to gaze point
        Assert.False(vm.CloseButtonCenter.X < 300, "Buttons should not be near gaze point");
    }
}
