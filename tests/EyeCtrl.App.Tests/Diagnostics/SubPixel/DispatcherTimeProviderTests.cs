namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Diagnostics;
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
///
/// <para>
/// The cancellation tests place Change and Dispose with dispatcher hooks, never with sleeps. A
/// <c>DispatcherOperation</c> invoke promotes every due timer first, and a promoted tick keeps the
/// queue position it was posted at, so a wall-clock arrangement ("block past the due time, then
/// cancel") lets the tick overtake the blocker whenever any other operation (such as the Send no-op
/// an earlier <c>frame.Continue = false</c> leaves queued) runs late enough. That is how
/// <c>fires == 1</c> flaked on CI. On the fast path the old arrangement aborted the tick while still
/// inactive, so the provider's stale-tick guards were never reached.
/// </para>
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

    /// <summary>
    /// Pumps the dispatcher until <paramref name="done"/> holds, or <paramref name="timeout"/> of
    /// REAL time passes. The timeout is a hang guard only; no assertion depends on how long it is.
    /// </summary>
    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var elapsed = Stopwatch.StartNew();
        var poll = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(5),
        };
        poll.Tick += (_, _) =>
        {
            if (!done() && elapsed.Elapsed < timeout) return;
            poll.Stop();
            frame.Continue = false;
        };
        poll.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// Creates a one-shot timer and returns the dispatcher operation that will deliver its tick.
    /// <c>DispatcherTimer.Start</c> posts that operation at <c>Inactive</c> priority; when the due
    /// time passes, the dispatcher promotes it to the timer's priority, and from then on it is a
    /// queued tick waiting its turn in the run queue.
    /// </summary>
    private static (ITimer Timer, DispatcherOperation Tick) CreateTimerCapturingItsTick(
        DispatcherTimeProvider provider, TimerCallback callback, TimeSpan dueTime)
    {
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        var posted = new List<DispatcherOperation>();
        DispatcherHookEventHandler onPosted = (_, e) =>
        {
            if (e.Operation.Priority == DispatcherPriority.Inactive) posted.Add(e.Operation);
        };

        hooks.OperationPosted += onPosted;
        ITimer timer;
        try
        {
            timer = provider.CreateTimer(callback, null, dueTime, Timeout.InfiniteTimeSpan);
        }
        finally
        {
            hooks.OperationPosted -= onPosted;
        }

        Check.WithCustomMessage("Arming a timer must post exactly one (inactive) tick operation")
            .That(posted).HasSize(1);
        return (timer, posted[0]);
    }

    /// <summary>
    /// Runs <paramref name="cancel"/> at the moment the timer's tick becomes QUEUED (the dispatcher
    /// promotes it out of <c>Inactive</c>), pumps until the tick has left the queue, and returns
    /// whether <paramref name="cancel"/> ran. Deterministic: no wall-clock race decides the order.
    /// </summary>
    private static bool CancelWhenQueued(DispatcherOperation tick, Action cancel) =>
        CancelOnHook(tick, cancel, (hooks, handler, add) =>
        {
            if (add) hooks.OperationPriorityChanged += handler;
            else hooks.OperationPriorityChanged -= handler;
        });

    /// <summary>
    /// Runs <paramref name="cancel"/> after the dispatcher has DEQUEUED the timer's tick and just
    /// before it invokes it (<c>OperationStarted</c>). <c>DispatcherTimer.Stop()</c> can no longer
    /// abort the tick there, so only the provider's own stale-tick guards stand between it and
    /// the callback. Returns whether <paramref name="cancel"/> ran.
    /// </summary>
    private static bool CancelWhenDispatching(DispatcherOperation tick, Action cancel) =>
        CancelOnHook(tick, cancel, (hooks, handler, add) =>
        {
            if (add) hooks.OperationStarted += handler;
            else hooks.OperationStarted -= handler;
        });

    private static bool CancelOnHook(
        DispatcherOperation tick, Action cancel, Action<DispatcherHooks, DispatcherHookEventHandler, bool> subscribe)
    {
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        bool cancelled = false;
        DispatcherHookEventHandler handler = (_, e) =>
        {
            if (cancelled || !ReferenceEquals(e.Operation, tick)) return;
            cancelled = true;
            cancel();
        };

        subscribe(hooks, handler, true);
        try
        {
            PumpUntil(() => tick.Status is not DispatcherOperationStatus.Pending, TimeSpan.FromSeconds(10));
        }
        finally
        {
            subscribe(hooks, handler, false);
        }

        return cancelled;
    }

    [Fact]
    public void Change_CancelsAnAlreadyQueuedTick_AndReArms()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        int fires = 0;
        var (timer, tick) = CreateTimerCapturingItsTick(provider, _ => fires++, TimeSpan.FromMilliseconds(1));

        bool changed = CancelWhenQueued(tick,
            () => timer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan));

        Check.WithCustomMessage("Arrangement: the tick was never queued, so Change was never tested")
            .That(changed).IsTrue();
        Check.WithCustomMessage("Change did not cancel the already-queued tick").That(fires).IsEqualTo(0);

        timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        PumpUntil(() => fires > 0, TimeSpan.FromSeconds(10));
        Check.WithCustomMessage("Change must re-arm after cancelling").That(fires).IsEqualTo(1);
        timer.Dispose();
    }

    [Fact]
    public void Dispose_CancelsAnAlreadyQueuedTick()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        int fires = 0;
        var (timer, tick) = CreateTimerCapturingItsTick(provider, _ => fires++, TimeSpan.FromMilliseconds(1));

        bool disposed = CancelWhenQueued(tick, timer.Dispose);

        Check.WithCustomMessage("Arrangement: the tick was never queued, so Dispose was never tested")
            .That(disposed).IsTrue();
        Check.WithCustomMessage("A tick queued before Dispose reached a torn-down run")
            .That(fires).IsEqualTo(0);
    }

    [Fact]
    public void Change_DropsATickWhoseDispatchHasAlreadyBegun()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        int fires = 0;
        var (timer, tick) = CreateTimerCapturingItsTick(provider, _ => fires++, TimeSpan.FromMilliseconds(1));

        bool changed = CancelWhenDispatching(tick,
            () => timer.Change(TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan));

        Check.WithCustomMessage("Arrangement: Change never ran while the tick was being dispatched")
            .That(changed).IsTrue();
        Check.WithCustomMessage("Arrangement: the stale tick must actually reach the provider")
            .That(tick.Status).IsEqualTo(DispatcherOperationStatus.Completed);
        Check.WithCustomMessage("A tick from the replaced arm reached the callback (token guard)")
            .That(fires).IsEqualTo(0);

        timer.Change(TimeSpan.FromMilliseconds(1), Timeout.InfiniteTimeSpan);
        PumpUntil(() => fires > 0, TimeSpan.FromSeconds(10));
        Check.WithCustomMessage("Change must re-arm after dropping the stale tick").That(fires).IsEqualTo(1);
        timer.Dispose();
    }

    [Fact]
    public void Dispose_DropsATickWhoseDispatchHasAlreadyBegun()
    {
        var provider = new DispatcherTimeProvider(Dispatcher.CurrentDispatcher);
        int fires = 0;
        var (timer, tick) = CreateTimerCapturingItsTick(provider, _ => fires++, TimeSpan.FromMilliseconds(1));

        bool disposed = CancelWhenDispatching(tick, timer.Dispose);

        Check.WithCustomMessage("Arrangement: Dispose never ran while the tick was being dispatched")
            .That(disposed).IsTrue();
        Check.WithCustomMessage("Arrangement: the stale tick must actually reach the provider")
            .That(tick.Status).IsEqualTo(DispatcherOperationStatus.Completed);
        Check.WithCustomMessage("A tick dispatched across Dispose reached a torn-down run")
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
        // event raised on the UI thread. The shortest observable step is the 5 s scripted interval
        // (A1: 3 s -> 5 s).
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

        // Pump for ≥ 6700 ms: the interval is 5000 ms, so 6700 leaves a third of the interval as
        // slack instead of the 13 % that flaked under load (HR11).
        PumpFor(TimeSpan.FromMilliseconds(6_700));

        Check.WithCustomMessage("The interval timer never fired on the dispatcher")
            .That(vm.Phase is SubPixelProbePhase.Gap or SubPixelProbePhase.Interval2).IsTrue();
        Check.WithCustomMessage("A C5 phase event ran off the UI thread")
            .That(advancedOnDispatcher).ContainsOnlyElementsThatMatch(a => a);
    }
}
