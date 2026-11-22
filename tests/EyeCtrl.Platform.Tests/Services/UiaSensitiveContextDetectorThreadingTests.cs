namespace EyeCtrl.Platform.Tests.Services;

using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Threading;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// F3 of the 18.11 exit-hang ticket: the UIA focus hook must be added on a non-UI thread, and
/// removed on the same thread, per Microsoft's UI-Automation threading rule — the removal used to
/// run on the WPF UI thread at exit and was the leading suspect for the shutdown that never
/// finished. <see cref="UiaSensitiveContextDetector.Stop"/> must also stay bounded when the removal
/// itself hangs (the fake's <c>Remove</c> blocks, like a stuck native <c>UiaRemoveEvent</c>).
/// </summary>
public class UiaSensitiveContextDetectorThreadingTests
{
    [Fact]
    public void StopBudgetRatchet_TheRemovalBudgetStaysAtTwoSeconds()
    {
        // The 20.11 sitting measured 621 ms of hook removal; 1 s left too little headroom on a
        // slow box, and the VALUE must be pinned so a revert to 1 s cannot slide through while
        // the hung-remove test merely tracks whatever the constant says (fixup-01 F5 / r6).
        Check.That(UiaSensitiveContextDetector.StopBudget >= TimeSpan.FromSeconds(2)).IsTrue();
    }

    [Fact]
    public void Start_AddsTheHookOnADedicatedMtaBackgroundThread_AndStopRemovesItOnThatSameThread()
    {
        OnStaThreadWithDispatcher(() =>
        {
            var hook = new RecordingFocusHook();
            int caller = Environment.CurrentManagedThreadId;
            using var detector = new UiaSensitiveContextDetector(
                NullLogger<UiaSensitiveContextDetector>.Instance, hook);

            detector.Start();
            Check.That(hook.Added.Wait(TestTimeouts.PositiveWaitMs)).IsTrue();

            Check.That(hook.AddThreadId).IsNotEqualTo(caller);
            Check.That(hook.AddApartment).IsEqualTo(ApartmentState.MTA);
            Check.That(hook.AddWasBackground).IsTrue();

            detector.Stop();
            Check.That(hook.Removed.Wait(TestTimeouts.PositiveWaitMs)).IsTrue();

            Check.That(hook.RemoveThreadId).IsEqualTo(hook.AddThreadId);
        });
    }

    [Fact]
    public void Stop_WithAHungRemove_ReturnsWithinTheBudget_InsteadOfHangingTheCaller()
    {
        OnStaThreadWithDispatcher(() =>
        {
            var hook = new HungRemoveFocusHook();
            using var detector = new UiaSensitiveContextDetector(
                NullLogger<UiaSensitiveContextDetector>.Instance, hook);
            detector.Start();
            Check.That(hook.Added.Wait(TestTimeouts.PositiveWaitMs)).IsTrue();

            // Run Stop on a dedicated background thread, not the thread pool: a Task.Run here also
            // measured the wait for a pool thread, and on a loaded, coverage-instrumented CI host that
            // wait alone can outlast the guard (run 45059573422 missed the 5 s guard while Stop itself
            // was never slow). The stopwatch is started and stopped inside that thread, immediately
            // around detector.Stop(), so only Stop's own duration is measured. The thread is background
            // and the event is not disposed (the blocked caller still sets it after the release below),
            // so a Stop that never returns fails the backstop without crashing or hanging the run.
            var elapsed = new Stopwatch();
            var stopped = new ManualResetEventSlim(false);
            Exception? stopFailure = null;
            var stopThread = new Thread(() =>
            {
                try
                {
                    elapsed.Start();
                    detector.Stop();
                    elapsed.Stop();
                }
                catch (Exception ex)
                {
                    stopFailure = ex;
                }
                finally
                {
                    stopped.Set();
                }
            })
            {
                IsBackground = true,
                Name = "UIA detector Stop caller",
            };
            stopThread.Start();

            try
            {
                // The 5 s backstop is what turns the mutant (Remove called inline on this thread) and
                // an unbounded Stop into RED tests instead of hung runs: the thread never completes,
                // so this fails.
                Check.That(stopped.Wait(TimeSpan.FromSeconds(5))).IsTrue();
                if (stopFailure is not null)
                    ExceptionDispatchInfo.Capture(stopFailure).Throw();

                Check.That(hook.EnteredRemove.Wait(TimeSpan.FromSeconds(5))).IsTrue();
                // The detector abandons the (background) hook thread at StopBudget, so Stop must
                // return by then plus scheduling allowance for a loaded, coverage-instrumented CI
                // host — 2.69 s was measured there on main (186b9f22) against this test's old 2.5 s
                // limit. The dedicated caller thread and the stopwatch inside it remove thread-pool
                // scheduling from the measurement; what remains is Stop itself plus the hook thread
                // being scheduled. 2 s budget (pinned by the ratchet test above) + 1.5 s slack =
                // 3.5 s, written ABSOLUTE rather than derived from StopBudget on purpose: raising the
                // budget is a longer exit hang and must go red here (a 4 s budget runs ~4 s, past
                // 3.5 s, while the 5 s backstop above still passes) instead of quietly stretching the
                // allowance with it. A deliberate budget raise must consciously widen this number
                // too. Still well under the 5 s guard, which remains the backstop for a Stop that
                // never returns at all.
                Check.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(3.5));
            }
            finally
            {
                // Let the abandoned hook thread end instead of leaving it blocked for the whole run.
                hook.ReleaseRemove.Set();
            }
        });
    }

    [Fact]
    public void Stop_BeforeStart_DoesNotTouchTheHookApi()
    {
        var hook = new RecordingFocusHook();
        using var detector = new UiaSensitiveContextDetector(
            NullLogger<UiaSensitiveContextDetector>.Instance, hook);

        detector.Stop();
        detector.Dispose();

        Check.That(hook.AddCount).IsEqualTo(0);
        Check.That(hook.RemoveCount).IsEqualTo(0);
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread that has a WPF dispatcher (the caller
    /// shape the real exit path has: OnStartup/OnExit run on the dispatcher thread). The loop is not
    /// pumped — the detector must never need it, and a version that marshalled to it would hang.
    /// </summary>
    private static void OnStaThreadWithDispatcher(Action body)
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                _ = Dispatcher.CurrentDispatcher;
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                finished.Set();
            }
        })
        {
            IsBackground = true,
            Name = "UIA detector test STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Check.That(finished.Wait(TestTimeouts.PositiveWaitMs)).IsTrue();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class RecordingFocusHook : IFocusHookApi
    {
        public ManualResetEventSlim Added { get; } = new(false);

        public ManualResetEventSlim Removed { get; } = new(false);

        public int AddThreadId { get; private set; } = -1;

        public int RemoveThreadId { get; private set; } = -1;

        public ApartmentState AddApartment { get; private set; } = ApartmentState.Unknown;

        public bool AddWasBackground { get; private set; }

        public int AddCount { get; private set; }

        public int RemoveCount { get; private set; }

        public void Add(AutomationFocusChangedEventHandler handler)
        {
            AddCount++;
            AddThreadId = Environment.CurrentManagedThreadId;
            AddApartment = Thread.CurrentThread.GetApartmentState();
            AddWasBackground = Thread.CurrentThread.IsBackground;
            Added.Set();
        }

        public void Remove(AutomationFocusChangedEventHandler handler)
        {
            RemoveCount++;
            RemoveThreadId = Environment.CurrentManagedThreadId;
            Removed.Set();
        }
    }

    private sealed class HungRemoveFocusHook : IFocusHookApi
    {
        public ManualResetEventSlim Added { get; } = new(false);

        public ManualResetEventSlim EnteredRemove { get; } = new(false);

        public ManualResetEventSlim ReleaseRemove { get; } = new(false);

        public void Add(AutomationFocusChangedEventHandler handler) => Added.Set();

        public void Remove(AutomationFocusChangedEventHandler handler)
        {
            EnteredRemove.Set();
            ReleaseRemove.Wait(); // stands in for a native UiaRemoveEvent that never returns
        }
    }
}
