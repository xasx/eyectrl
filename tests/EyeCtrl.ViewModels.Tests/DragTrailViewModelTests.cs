namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class DragTrailViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    // Right-edge button centers are computed by the App layer and passed in; tests supply fixed
    // values and assert the ViewModel echoes them.
    private static readonly PhysicalPoint TestCancelCenter = new(1890, 560);
    private static readonly PhysicalPoint TestBackCenter = new(1890, 500);

    private static StartDragTrailMessage StartMsg(PhysicalPoint origin)
        => new(origin, TestCancelCenter, TestBackCenter);

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
    public void Settle_CompletesAndSendsDragDrop()
    {
        _settings.Drag.SettleDwellMs = 200;
        _settings.Drag.SettleRadiusPixels = 40;
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        DragDropMessage? dropMsg = null;
        _messenger.Register<DragDropMessage>(_messenger, (_, msg) => dropMsg = msg);

        var pos = new PhysicalPoint(300, 300);
        _messenger.Send(new GazeCursorMovedMessage(pos, 0));
        _messenger.Send(new GazeCursorMovedMessage(pos, 300_000));

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
    public void CancelButton_DwellSendsDragCancel()
    {
        var vm = CreateVm();
        _settings.Dwell.DragRightEdgeButtonDwellMs = 200;
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        DragCancelMessage? cancelMsg = null;
        _messenger.Register<DragCancelMessage>(_messenger, (_, msg) => cancelMsg = msg);

        var cancelPos = vm.CancelButtonCenter;
        _messenger.Send(new GazeCursorMovedMessage(cancelPos, 0));
        _messenger.Send(new GazeCursorMovedMessage(cancelPos, 300_000));

        Check.That(cancelMsg).IsNotNull();
        Check.That(vm.IsDragActive).IsFalse();
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
        _messenger.Send(new GazeCursorMovedMessage(releasePos, 100_000));
        _messenger.Send(new GazeCursorMovedMessage(releasePos, 400_000));

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
    public void StartDrag_EchoesEdgeButtonCenters()
    {
        // The App layer computes the right-edge cancel/back centers and passes them in; the
        // ViewModel stores them verbatim (it no longer reads screen geometry itself).
        var vm = CreateVm();
        _messenger.Send(StartMsg(new PhysicalPoint(100, 100)));

        Check.That(vm.CancelButtonCenter).IsEqualTo(TestCancelCenter);
        Check.That(vm.BackButtonCenter).IsEqualTo(TestBackCenter);
    }
}
