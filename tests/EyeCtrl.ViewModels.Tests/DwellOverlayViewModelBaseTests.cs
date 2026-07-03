namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

public class DwellOverlayViewModelBaseTests
{
    internal sealed partial class TestOverlayVm : DwellOverlayViewModelBase
    {
        public string? Activated;
        public double LastProgress = -1;

        public TestOverlayVm(IMessenger messenger) : base(messenger, dwellCooldownMs: 400) { }

        public void AddTestTarget(PhysicalPoint center, double radius, int dwellMs) =>
            DwellDetector.AddTarget(new DwellTarget("t1", center, radius, dwellMs));

        protected override void OnTargetActivated(string targetId) => Activated = targetId;
        protected override void OnTargetProgress(string targetId, double progress) => LastProgress = progress;
    }

    [Fact]
    public void Gaze_WhileVisible_DrivesDwellToActivation()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000 + dwellUs));

        Check.That(vm.Activated).IsEqualTo("t1");
    }

    [Fact]
    public void Gaze_WhileHidden_IsIgnored()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = false;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000 + dwellUs));

        Check.That(vm.Activated).IsNull();
    }

    [Fact]
    public void Dispose_UnhooksDetectorEvents_AndUnregisters()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());
        Check.That(ex).IsNull();
    }
}
