namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

public class ScrollOverlayViewModelTests
{
    private static ScrollOverlayViewModel NewVm() =>
        new(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());

    private static readonly PhysicalPoint Anchor = new(1000, 800);

    [Fact]
    public void GazeAtAnchor_DotIsCentered_NoScrollVelocity()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor);
        vm.Receive(new GazeCursorMovedMessage(Anchor, 0));
        Check.That(vm.AnchorDotOffsetX).IsCloseTo(0, 1e-3);
        Check.That(vm.AnchorDotOffsetY).IsCloseTo(0, 1e-3);
        Check.That(vm.ScrollVelocityX).IsCloseTo(0, 1e-6);
        Check.That(vm.ScrollVelocityY).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void TargetVelocityChanged_FiresWithComputedVelocity()
    {
        var vm = NewVm();
        double cvx = double.NaN, cvy = double.NaN;
        vm.TargetVelocityChanged += (x, y) => { cvx = x; cvy = y; };
        vm.EnterScrollMode(Anchor);
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 600), 0));
        Check.That(cvy).IsStrictlyGreaterThan(0);
        Check.That(cvx).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void EnterScrollMode_SetsAnchor()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor);
        Check.That(vm.Anchor).IsEqualTo(Anchor);
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExitScrollMode_ClearsState()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor);
        vm.ExitScrollMode();
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void EnterScrollMode_NoLongerOwnsStopButton()
    {
        var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), new ScrollSettings(), new DwellSettings());

        vm.EnterScrollMode(new PhysicalPoint(100, 100));

        Check.That(vm.StopButtonCenter).IsEqualTo(new PhysicalPoint(0, 0));
    }
}
