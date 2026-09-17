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

    [Fact]
    public void UpdateGazePosition_PlacesDotAtRawGaze_NotOffsetByOriginalFixationAndZoom()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());

        // Circle shown at screen centre; user originally fixated far away (300,200); 2x zoom.
        // The old mapping flung the dot to centre + (gaze - origin) * 2 (off the circle); the dot
        // must instead sit exactly under the current gaze — the screen point the dwell will select.
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(960, 540), 150, 2.0, new PhysicalPoint(300, 200)));

        vm.UpdateGazePosition(new PhysicalPoint(1000, 560));

        Check.That(vm.IndicatorX).IsEqualTo(1000d);
        Check.That(vm.IndicatorY).IsEqualTo(560d);
    }

    [Fact]
    public void Receive_ManualSelectionAnchorChangedMessage_ShowsTheAnchorMarkAtTheAnchor()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());

        messenger.Send(new ManualSelectionAnchorChangedMessage(new PhysicalPoint(720, 480)));

        Check.That(vm.IsAnchorVisible).IsTrue();
        Check.That(vm.AnchorX).IsEqualTo(720d);
        Check.That(vm.AnchorY).IsEqualTo(480d);
    }

    [Fact]
    public void Receive_ManualSelectionAnchorChangedMessage_WithNull_ReleasesTheAnchorMark()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new ManualSelectionAnchorChangedMessage(new PhysicalPoint(720, 480)));
        Check.That(vm.IsAnchorVisible).IsTrue();

        messenger.Send(new ManualSelectionAnchorChangedMessage(null));

        Check.That(vm.IsAnchorVisible).IsFalse();
    }

    [Fact]
    public void Receive_EndManualSelectionMessage_ReleasesTheAnchorMarkToo()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);
        vm.Configure(new EyeCtrlSettings());
        messenger.Send(new ManualSelectionAnchorChangedMessage(new PhysicalPoint(720, 480)));
        Check.That(vm.IsAnchorVisible).IsTrue();

        messenger.Send(new EndManualSelectionMessage());

        Check.That(vm.IsAnchorVisible).IsFalse();
    }
}
