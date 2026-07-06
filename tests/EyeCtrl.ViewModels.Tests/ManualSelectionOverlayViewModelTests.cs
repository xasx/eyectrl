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
        Check.That(vm.Center.X).IsEqualTo(960);
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
}
