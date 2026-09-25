namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class DragTrailViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    private static StartDragTrailMessage StartMsg(PhysicalPoint origin)
        => new(origin);

    private DragTrailViewModel CreateVm() => new(_settings, _messenger);

    [Fact]
    public void StartDrag_SetsGrabOrigin_AndIsDragActive()
    {
        var vm = CreateVm();
        var origin = new PhysicalPoint(500, 300);

        _messenger.Send(StartMsg(origin));

        Check.That(vm.IsDragActive).IsTrue();
        Check.That(vm.GrabOrigin).IsEqualTo(origin);
        Check.That(vm.DragState).IsEqualTo(DragSubState.FreeDrag);
    }

    [Fact]
    public void GazeUpdate_UpdatesCursorPosition()
    {
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), 0));

        Check.That(vm.CursorPosition).IsEqualTo(new PhysicalPoint(200, 200));
    }

    [Fact]
    public void Settle_ProgressFills_WhenGazeStops()
    {
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 500;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        var pos = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos, 250_000));

        Check.That(vm.SettleProgress).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void Settle_DoesNotProgressOnHeldSamples()
    {
        // The settle detector is a dwell surface too: a blink freezes the position, and letting it
        // settle would drop what the user is dragging where their eyes happened to close.
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 500;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        var pos = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos, 0));
        _messenger.Send(new GazeCursorMovedMessage(new GazeFrame(pos, 250_000, IsInGap: true)));

        Check.That(vm.SettleProgress).IsEqualTo(0);
        Check.That(vm.DragState).IsEqualTo(DragSubState.FreeDrag);
    }

    [Fact]
    public void Settle_CompletesAndSendsDragDrop()
    {
        _settings.Drag.SettleDwellMs = 200;
        _settings.Drag.SettleRadiusPixels = 40;
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        DragDropMessage? dropMsg = null;
        _messenger.Register<DragDropMessage>(_messenger, (_, msg) => dropMsg = msg);

        // Ticket 3's FixationDetector clocks VALID-TIME age: each inter-sample dt is capped at ~2 frames,
        // so one 300 ms jump no longer completes a 200 ms settle. Feed a realistic 30 Hz hold.
        var pos = new PhysicalPoint(300, 300);
        for (long t = 0; t <= 240_000; t += 30_000)
            _messenger.Send(new GazeCursorMovedMessage(pos, t));

        Check.That(dropMsg).IsNotNull();
        Check.That(vm.IsDragActive).IsFalse();
    }

    [Fact]
    public void GazeMove_ResetsSettleProgress()
    {
        var vm = CreateVm();
        _settings.Drag.SettleDwellMs = 500;
        _settings.Drag.SettleRadiusPixels = 40;
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        var pos1 = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos1, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos1, 200_000));
        Check.That(vm.SettleProgress).IsStrictlyGreaterThan(0);

        var pos2 = new PhysicalPoint(500, 500);
        _messenger.Send(new GazeCursorMovedMessage(pos2, 300_000));
        Check.That(vm.SettleProgress).IsEqualTo(0);
    }

    [Fact]
    public void ReleaseButton_DwellSendsDragDrop()
    {
        _settings.Dwell.DragReleaseButtonDwellMs = 200;
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 300), 0));

        DragDropMessage? dropMsg = null;
        _messenger.Register<DragDropMessage>(_messenger, (_, msg) => dropMsg = msg);

        var releasePos = vm.ReleaseButtonCenter;
        DwellDrive.Hold(_messenger, releasePos, 100_000, 400_000);

        Check.That(dropMsg).IsNotNull();
    }

    [Fact]
    public void ShowBack_IsFalse_InFreeDrag()
    {
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        Check.That(vm.ShowBack).IsFalse();
    }

    [Fact]
    public void Receive_DragCancelMessage_RunsCleanup()
    {
        var vm = CreateVm();
        vm.Receive(new StartDragTrailMessage(
            new PhysicalPoint(100, 100)));

        _messenger.Send(new DragCancelMessage());

        Check.That(vm.IsDragActive).IsFalse();
    }

    [Fact]
    public void Receive_DragDismissManualSelectionMessage_RunsCleanup()
    {
        var vm = CreateVm();
        _settings.SelectionMode = SelectionMode.ManualSelection;
        vm.Receive(new StartDragTrailMessage(
            new PhysicalPoint(100, 100)));

        _messenger.Send(new DragDismissManualSelectionMessage());

        Check.That(vm.DragState).IsEqualTo(DragSubState.FreeDrag);
    }
}
