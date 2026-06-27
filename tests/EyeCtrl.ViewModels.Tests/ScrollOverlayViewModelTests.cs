namespace EyeCtrl.ViewModels.Tests;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using CommunityToolkit.Mvvm.Messaging;

public class ScrollOverlayViewModelTests
{
    private static ScrollOverlayViewModel NewVm() =>
        new(new ScrollSettings(), new WeakReferenceMessenger());

    private static readonly PhysicalPoint Anchor = new(1000, 800);
    private static readonly PhysicalPoint StopCenter = new(2000, 600);

    [Fact]
    public void GazeAtAnchor_DotIsCentered_NoScrollVelocity()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
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
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 600), 0));
        Check.That(cvy).IsStrictlyGreaterThan(0);
        Check.That(cvx).IsCloseTo(0, 1e-6);
    }

    [Fact]
    public void StopButtonDwell_TriggersExit()
    {
        var vm = NewVm();
        bool exitCalled = false;
        vm.ExitRequested += () => exitCalled = true;

        vm.EnterScrollMode(Anchor, StopCenter);
        var stopPos = vm.StopButtonCenter;
        vm.Receive(new GazeCursorMovedMessage(stopPos, 0));
        vm.Receive(new GazeCursorMovedMessage(stopPos, 1_000_000));
        Check.That(exitCalled).IsTrue();
    }

    [Fact]
    public void EnterScrollMode_SetsAnchorAndStopCenter()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        Check.That(vm.Anchor).IsEqualTo(Anchor);
        Check.That(vm.StopButtonCenter).IsEqualTo(StopCenter);
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExitScrollMode_ClearsState()
    {
        var vm = NewVm();
        vm.EnterScrollMode(Anchor, StopCenter);
        vm.ExitScrollMode();
        Check.That(vm.IsVisible).IsFalse();
    }
}
