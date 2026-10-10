namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Diagnostics.SubPixel;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// The dispatcher-backed <see cref="TimeProvider"/> C5 runs on (SP-3 review 01 deviation 2; build
/// plan C5/C7). W-tests: every callback runs with <c>CheckAccess() == true</c>, Change and Dispose
/// cancel an already-queued tick, four timers work, and the adapter never falls back to
/// <c>TimeProvider.System</c>.
/// </summary>
public class DispatcherTimeProviderTests
{
    /// <summary>Pumps the dispatcher for <paramref name="duration"/> of REAL time.</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var wake = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = duration,
        };
        wake.Tick += (_, _) =>
        {
            wake.Stop();
            frame.Continue = false;
        };
        wake.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void TimerCallback_RunsOnTheDispatcherThread()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        bool ran = false;
        bool checkAccess = false;
        int callbackThread = -1;
        int testThread = Environment.CurrentManagedThreadId;

        using var timer = provider.CreateTimer(_ =>
        {
            ran = true;
            checkAccess = Dispatcher.CurrentDispatcher.CheckAccess();
            callbackThread = Environment.CurrentManagedThreadId;
        }, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

        PumpFor(TimeSpan.FromMilliseconds(300));

        Check.That(ran).IsTrue();
        Check.WithCustomMessage("A C5 timer callback ran off the UI thread (review 05 F4b)")
            .That(checkAccess).IsTrue();
        Check.That(callbackThread).IsEqualTo(testThread);
    }

    [Fact]
    public void Change_CancelsAnAlreadyQueuedTick_AndReArms()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var provider = new DispatcherTimeProvider(dispatcher);
        int fires = 0;

        var timer = provider.CreateTimer(
            _ => fires++, null, TimeSpan.FromMilliseconds(40), Timeout.InfiniteTimeSpan);

        // Hold the dispatcher at Normal pace PAST the due time, so the timer's tick is genuinely
        // queued behind the blocker; Change inside the blocker must invalidate that queued tick.
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Thread.Sleep(150);
            timer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        });
        PumpFor(TimeSpan.FromMilliseconds(300));
        Check.WithCustomMessage("Change did not cancel the already-queued tick").That(fires).IsEqualTo(0);

        timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        PumpFor(TimeSpan.FromMilliseconds(200));
        Check.That(fires).IsEqualTo(1);
        timer.Dispose();
    }

    [Fact]
    public void Dispose_CancelsAnAlreadyQueuedTick()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var provider = new DispatcherTimeProvider(dispatcher);
        int fires = 0;

        var timer = provider.CreateTimer(
            _ => fires++, null, TimeSpan.FromMilliseconds(40), Timeout.InfiniteTimeSpan);

        // The tick is queued when Dispose runs: a Normal blocker holds the dispatcher past the due
        // time, then disposes the timer; the queued tick must not reach the callback.
        dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Thread.Sleep(150);
            timer.Dispose();
        });
        PumpFor(TimeSpan.FromMilliseconds(300));

        Check.WithCustomMessage("A tick queued before Dispose reached a torn-down run")
            .That(fires).IsEqualTo(0);
    }

    [Fact]
    public void ProviderDispose_CancelsEveryTimerItCreated()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        int fires = 0;
        for (int i = 0; i < 4; i++)
            provider.CreateTimer(
                _ => fires++, null, TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);

        provider.Dispose();
        PumpFor(TimeSpan.FromMilliseconds(250));

        Check.That(fires).IsEqualTo(0);
    }

    [Fact]
    public void FourTimers_EachFireOnTheDispatcherThread()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        var fired = new List<bool>();
        var accesses = new List<bool>();

        var timers = new List<ITimer>();
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            timers.Add(provider.CreateTimer(_ =>
            {
                fired.Add(true);
                accesses.Add(Dispatcher.CurrentDispatcher.CheckAccess());
            }, null, TimeSpan.FromMilliseconds(1 + index), Timeout.InfiniteTimeSpan));
        }

        PumpFor(TimeSpan.FromMilliseconds(300));

        Check.WithCustomMessage("C5 creates four timers who one provider; each must fire")
            .That(fired).HasSize(4);
        Check.WithCustomMessage("Every C5 callback must run with CheckAccess true")
            .That(accesses).ContainsOnlyElementsThatMatch(a => a);

        foreach (var timer in timers) timer.Dispose();
    }

    [Fact]
    public void TheAdapter_NeverFallsBackToTimeProviderSystem()
    {
        // SP-3 review 01: a thread-pool fallback would break C5's single-thread contract, so the
        // adapter's CODE must not mention it. Comments are stripped first: the file's own doc
        // comment names the forbidden fallback to explain why it is absent.
        var path = Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Diagnostics", "SubPixel", "DispatcherTimeProvider.cs");
        var code = SourceScanner.StripComments(File.ReadAllText(path));
        Check.WithCustomMessage("The adapter must never fall back to TimeProvider.System")
            .That(code.Contains("TimeProvider.System")).IsFalse();
    }

    [Fact]
    public void ARealC5_ReportsItsPhaseChangesOnTheDispatcherThread()
    {
        // The end-to-end half of the contract: a real C5 run on this provider must have every
        // event raised on the UI thread. The shortest observable step is the 3 s scripted interval.
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        var settings = new EyeCtrlSettings();
        using var vm = new SubPixelProbeViewModel(
            new WeakReferenceMessenger(),
            provider,
            SubPixelAbSchedule.Create(seed: 3, new PhysicalRect(0, 0, 1920, 1080)),
            settings.Dwell,
            () => true);

        var advancedOnDispatcher = new List<bool>();
        vm.PhaseAdvanced += () =>
            advancedOnDispatcher.Add(Dispatcher.CurrentDispatcher.CheckAccess());
        vm.Start();
        vm.SimulateActivated(SubPixelProbeViewModel.IntroStartTargetId);   // Intro -> interval 1

        PumpFor(TimeSpan.FromMilliseconds(3_400));

        Check.WithCustomMessage("The interval timer never fired on the dispatcher")
            .That(vm.Phase).IsEqualTo(SubPixelProbePhase.Gap);
        Check.WithCustomMessage("A C5 phase event ran off the UI thread")
            .That(advancedOnDispatcher).ContainsOnlyElementsThatMatch(a => a);
    }
}
