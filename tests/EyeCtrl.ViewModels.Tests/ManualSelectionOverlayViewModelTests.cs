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
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Center.X).IsEqualTo(500);
        Check.That(vm.Center.Y).IsEqualTo(300);
        Check.That(vm.Radius).IsEqualTo(150);
        Check.That(vm.Magnification).IsEqualTo(2.0);
    }

    [Fact]
    public void HidesManualSelection_OnEndManualSelectionMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new EndManualSelectionMessage());
        Check.That(vm.IsVisible).IsFalse();
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
        Check.That(vm.CloseButtonCenter.X > 500).IsTrue();
        Check.That(vm.RecaptureButtonCenter.X > 500).IsTrue();
        Check.That(vm.CloseButtonCenter.Y < 500).IsTrue();
        Check.That(vm.RecaptureButtonCenter.Y > 500).IsTrue();
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

        Check.That(closeSent).IsTrue();
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

        Check.That(recaptureSent).IsTrue();
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

        Check.That(closeSent).IsFalse();
    }

    [Fact]
    public void HidesManualSelection_OnManualSelectionHiddenMessage()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new ManualSelectionHiddenMessage());

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void ReshowsManualSelection_AfterManualSelectionHiddenThenStartManualSelection()
    {
        // Simulates: ManualSelection shown → fixation detected (ManualSelectionHidden) → Back pressed (StartManualSelection again)
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));
        _messenger.Send(new ManualSelectionHiddenMessage());
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(500, 300), 150, 2.0, new PhysicalPoint(500, 300)));

        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void StoresGazePoint_WhenProvided()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var gazePoint = new PhysicalPoint(400, 200);

        _messenger.Send(new StartManualSelectionMessage(new PhysicalPoint(960, 540), 150, 2.0, gazePoint));

        Check.That(vm.GazePoint).IsEqualTo(gazePoint);
        Check.That(vm.Center.X).IsEqualTo(960); // Display center different from gaze
        Check.That(vm.Center.Y).IsEqualTo(540);
    }

    [Fact]
    public void GazePoint_EqualsCenter_WhenInAtGazeMode()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var center = new PhysicalPoint(500, 300);

        _messenger.Send(new StartManualSelectionMessage(center, 150, 2.0, center));

        Check.That(vm.GazePoint).IsEqualTo(center);
        Check.That(vm.Center).IsEqualTo(center);
    }

    [Fact]
    public void SideButtons_PositionedRelativeToDisplayCenter_NotGazePoint()
    {
        var vm = new ManualSelectionOverlayViewModel(new EyeCtrlSettings(), _messenger);
        var displayCenter = new PhysicalPoint(960, 540); // Screen center
        var gazePoint = new PhysicalPoint(200, 100);     // Gaze at different location

        _messenger.Send(new StartManualSelectionMessage(displayCenter, 150, 2.0, gazePoint));

        // Buttons should be positioned relative to display center (960, 540), not gaze (200, 100)
        Check.That(vm.CloseButtonCenter.X > 960).IsTrue();
        Check.That(vm.RecaptureButtonCenter.X > 960).IsTrue();

        // Not positioned relative to gaze point
        Check.That(vm.CloseButtonCenter.X < 300).IsFalse();
    }
}
