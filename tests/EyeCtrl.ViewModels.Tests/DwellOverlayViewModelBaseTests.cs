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

    // ── Gap-held samples ──
    // The engine keeps publishing during a blink, with the frozen last valid position. Feeding those
    // to the detector is what let a blink complete a dwell; the fix skips them entirely.

    private static GazeCursorMovedMessage Held(PhysicalPoint at, long ts) =>
        new(new GazeFrame(at, ts, IsInGap: true));

    [Fact]
    public void Gaze_InGap_DoesNotActivate()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));   // enters the target
        messenger.Send(Held(center, 1_000_000 + dwellUs));               // eyes closed, position held

        Check.That(vm.Activated).IsNull();
    }

    [Fact]
    public void Gaze_InGap_DoesNotEmitProgress()
    {
        // Freeze, not reset: the held sample must produce no progress report at all — neither an
        // advance nor the 0 that a gaze-left would emit.
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_300_000));   // half-way: progress ≈ 0.5
        double beforeGap = vm.LastProgress;

        messenger.Send(Held(center, 1_400_000));

        Check.That(beforeGap).IsStrictlyGreaterThan(0.0);
        Check.That(beforeGap).IsStrictlyLessThan(1.0);
        Check.That(vm.LastProgress).IsEqualTo(beforeGap);   // unchanged by the held sample
    }

    [Fact]
    public void Gaze_AfterGap_ResumesWithoutLosingTheTarget()
    {
        // The dwell survives the blink: the target is not abandoned, so the first real sample after
        // the gap carries on rather than starting over.
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        long dwellUs = 600 * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(Held(center, 1_100_000));
        messenger.Send(Held(center, 1_200_000));
        Check.That(vm.Activated).IsNull();

        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000 + dwellUs));

        Check.That(vm.Activated).IsEqualTo("t1");
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
