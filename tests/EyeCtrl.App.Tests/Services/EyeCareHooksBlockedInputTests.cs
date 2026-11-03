namespace EyeCtrl.App.Tests.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// C10 fixup-01 F3 (coordinator decision: blocked wins). A break due while the detector reports a
/// block is DEFERRED, not painted under the blocked banner's buttons; it shows when the block
/// clears. CancelBanner (a new block) and CancelActive (eye-care off) abandon the wait instead of
/// showing a stale prompt. No real window is involved.
/// </summary>
public class EyeCareHooksBlockedInputTests
{
    [StaFact]
    public void ABreakDueWhileBlocked_WaitsForTheClear_ThenShows()
    {
        var banner = new RecordingBanner();
        var gate = new BlockedInputGate();
        gate.Set(true);
        var hooks = NewHooks(banner, gate);

        var shown = hooks.ShowBannerAsync("break");
        Pump();
        Check.WithCustomMessage("the eye-care banner must not be painted while blocked.")
            .That(banner.ShowCount).IsEqualTo(0);
        Check.That(shown.IsCompleted).IsFalse();

        gate.Set(false);
        PumpUntil(() => shown.IsCompleted);

        Check.That(banner.ShowCount).IsEqualTo(1);
        Check.That(shown.Result).IsTrue();
    }

    [StaFact]
    public void CancelBanner_AbandonsTheWait_WithoutShowingTheStalePrompt()
    {
        var banner = new RecordingBanner();
        var gate = new BlockedInputGate();
        gate.Set(true);
        var hooks = NewHooks(banner, gate);

        var shown = hooks.ShowBannerAsync("break");
        Pump();
        Check.That(shown.IsCompleted).IsFalse();

        hooks.CancelBanner();
        PumpUntil(() => shown.IsCompleted);

        Check.That(banner.ShowCount).IsEqualTo(0);
        Check.WithCustomMessage("a cancelled deferred break must snooze, not run.")
            .That(shown.Result).IsFalse();
    }

    [StaFact]
    public void CancelActive_AlsoAbandonsTheWait()
    {
        var banner = new RecordingBanner();
        var gate = new BlockedInputGate();
        gate.Set(true);
        var hooks = NewHooks(banner, gate);

        var shown = hooks.ShowBannerAsync("break");
        Pump();

        hooks.CancelActive();
        PumpUntil(() => shown.IsCompleted);

        Check.That(banner.ShowCount).IsEqualTo(0);
        Check.That(shown.Result).IsFalse();
        Check.That(banner.CancelCount).IsGreaterOrEqualThan(1);
    }

    [StaFact]
    public void AnUnblockedBreak_ShowsImmediately()
    {
        var banner = new RecordingBanner();
        var hooks = NewHooks(banner, new BlockedInputGate());

        var shown = hooks.ShowBannerAsync("break");
        PumpUntil(() => shown.IsCompleted);

        Check.That(banner.ShowCount).IsEqualTo(1);
        Check.That(shown.Result).IsTrue();
    }

    private static EyeCareHooks NewHooks(IEyeCareBanner banner, BlockedInputGate gate) =>
        new(
            new WeakReferenceMessenger(),
            Dispatcher.CurrentDispatcher,
            banner,
            new StubExercise(),
            new PhysicalPoint(960, 540),
            bannerAutoSnoozeSeconds: 15,
            blockedInput: gate);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(_ => { frame.Continue = false; return null; }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!done() && Environment.TickCount64 < deadline)
            Pump();

        Check.WithCustomMessage("the deferred banner did not complete within 5 s.")
            .That(done()).IsTrue();
    }

    private sealed class RecordingBanner : IEyeCareBanner
    {
        public int ShowCount { get; private set; }
        public int CancelCount { get; private set; }

        public Task<bool> ShowAsync(string message, PhysicalPoint screenCenter, int autoSnoozeSeconds)
        {
            ShowCount++;
            return Task.FromResult(true);
        }

        public void OnGaze(PhysicalPoint gaze, long timestampUs) { }
        public void Cancel() => CancelCount++;
    }

    private sealed class StubExercise : IEyeCareExercise
    {
        public Task RunAsync(
            string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze) =>
            Task.CompletedTask;

        public void OnGaze(PhysicalPoint gaze, long timestampUs) { }

        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;

        public void Cancel() { }
    }
}
