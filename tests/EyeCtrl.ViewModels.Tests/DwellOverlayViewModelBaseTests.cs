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

    /// <summary>
    /// Sends real (non-gap) samples at 30 ms cadence from just after <paramref name="afterUs"/>
    /// through <paramref name="throughUs"/>. Each pre-clamp test sent two samples far apart, which
    /// modelled a hold only because elapsed time was unbounded; the clamp restarts a dwell when a
    /// single step reaches its bound, so a realistic cadence is required.
    /// </summary>
    private static void Drive(IMessenger messenger, PhysicalPoint at, long afterUs, long throughUs)
    {
        for (var t = afterUs + 30_000; t <= throughUs; t += 30_000)
            messenger.Send(new GazeCursorMovedMessage(at, t));
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
        Drive(messenger, center, 1_000_000, 1_000_000 + dwellUs);

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
        Drive(messenger, center, 1_000_000, 1_300_000);   // half-way: progress ≈ 0.5
        double beforeGap = vm.LastProgress;

        messenger.Send(Held(center, 1_400_000));

        Check.That(beforeGap).IsStrictlyGreaterThan(0.0);
        Check.That(beforeGap).IsStrictlyLessThan(1.0);
        Check.That(vm.LastProgress).IsEqualTo(beforeGap);   // unchanged by the held sample
    }

    /// <summary>
    /// Flipped from the test that pinned the defect: its comment read *"The dwell survives the
    /// blink: the target is not abandoned, so the first real sample after the gap carries on
    /// rather than starting over."* A 400 ms silence completing a 600 ms dwell was asserted as
    /// correct — and on the prediction bar that fires a suggestion into the persisted dictionary
    /// with no gaze-reachable repair.
    /// </summary>
    [Fact]
    public void SupraThresholdGap_RestartsTheDwell()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(Held(center, 1_100_000));
        messenger.Send(Held(center, 1_200_000));
        Check.That(vm.Activated).IsNull();

        messenger.Send(new GazeCursorMovedMessage(center, 1_600_000));   // 400 ms of silence

        Check.WithCustomMessage(
                "A 400 ms silence completed a 600 ms dwell. Gap-held samples are skipped, not "
                + "paused: the whole silence was credited to the first real sample after it.")
            .That(vm.Activated).IsNull();

        // The restarted dwell still completes on a continuous stream — the target was not abandoned.
        Drive(messenger, center, 1_600_000, 2_200_000);
        Check.That(vm.Activated).IsEqualTo("t1");
    }

    /// <summary>
    /// The anti-over-clamp guard, kept deliberately green: a silence under the bound resumes
    /// rather than restarting. A clamp that restarts on any dropped frame would make every dwell
    /// surface in the app harder to use — the toolbar receives samples routinely.
    /// </summary>
    [Fact]
    public void SubThresholdGap_ResumesTheDwell()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new TestOverlayVm(messenger);
        var center = new PhysicalPoint(500, 500);
        vm.AddTestTarget(center, 100, 600);
        vm.IsVisible = true;

        messenger.Send(new GazeCursorMovedMessage(center, 1_000_000));
        messenger.Send(new GazeCursorMovedMessage(center, 1_140_000));   // 140 ms, under the bound

        Check.WithCustomMessage(
                "A 140 ms silence — under the 150 ms bound — did not resume: progress was reset. "
                + "That is the over-clamp this row exists to prevent.")
            .That(vm.LastProgress).IsCloseTo(140.0 / 600.0, 0.02);

        Drive(messenger, center, 1_140_000, 1_620_000);
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
