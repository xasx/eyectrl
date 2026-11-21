namespace EyeCtrl.App.Tests.Services;

using System.Collections.Concurrent;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Services;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// The exit watchdog (F1 of the 18.11 exit-hang ticket): a step that never returns must end the
/// process 15 s later with the step's name, and a completed exit must never be killed. Both drive a
/// <see cref="FakeTimeProvider"/>, so no test waits 15 real seconds.
/// </summary>
public class ExitWatchdogTests
{
    private const string BlockingStep = "stop password-field detector";

    [Fact]
    public void NeverCompletingStep_TerminatesOnceAfterTheBudget_WithTheStepName()
    {
        var time = new FakeTimeProvider();
        var calls = new ConcurrentQueue<string>();
        using var terminated = new ManualResetEventSlim(false);
        var watchdog = new ExitWatchdog(
            time,
            () => BlockingStep,
            step =>
            {
                calls.Enqueue(step);
                terminated.Set();
            });

        watchdog.Start();

        time.Advance(ExitWatchdog.DefaultBudget - TimeSpan.FromSeconds(1));
        Check.That(terminated.Wait(TimeSpan.FromMilliseconds(250))).IsFalse(); // never before the budget

        time.Advance(TimeSpan.FromSeconds(1));
        Check.That(terminated.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(calls.TryDequeue(out string? reported)).IsTrue();
        Check.That(reported).IsEqualTo(BlockingStep);

        // One-shot: more clock never produces a second terminate (FailFast would end the process on
        // the first, but a double call would be a bug in the seam the tests drive).
        time.Advance(TimeSpan.FromMinutes(5));
        Thread.Sleep(TimeSpan.FromMilliseconds(250));
        Check.That(calls).IsEmpty();
    }

    [Fact]
    public void CompletedBeforeTheBudget_NeverTerminates()
    {
        var time = new FakeTimeProvider();
        var calls = new ConcurrentQueue<string>();
        var watchdog = new ExitWatchdog(time, () => BlockingStep, step => calls.Enqueue(step));

        watchdog.Start();
        time.Advance(TimeSpan.FromSeconds(1));
        watchdog.Completed();

        time.Advance(TimeSpan.FromMinutes(5));
        Thread.Sleep(TimeSpan.FromMilliseconds(250));

        Check.That(calls).IsEmpty();
    }

    [Fact]
    public void Start_IsIdempotent_SoASecondCallAddsNoSecondDeadline()
    {
        var time = new FakeTimeProvider();
        var calls = new ConcurrentQueue<string>();
        using var terminated = new ManualResetEventSlim(false);
        var watchdog = new ExitWatchdog(
            time,
            () => BlockingStep,
            step =>
            {
                calls.Enqueue(step);
                terminated.Set();
            });

        watchdog.Start();
        watchdog.Start();

        time.Advance(ExitWatchdog.DefaultBudget + TimeSpan.FromSeconds(1));
        Check.That(terminated.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(calls).HasSize(1);
    }

    /// <summary>
    /// E3 budget ratchet: a stuck exit must still fit inside a restart replacement's mutex wait,
    /// even with a mid-size WER dump (the 21.11 holistic review's 3–7 s estimate, rounded up).
    /// With E1 the child no longer waits on the dump at all — the keeper releases BEFORE FailFast —
    /// but the sum stays pinned so a later budget raise cannot quietly recreate the 30 s collision.
    /// </summary>
    [Fact]
    public void BudgetRatchet_StuckExitStillFitsInsideTheRestartChildsMutexWait()
    {
        TimeSpan dumpAllowance = TimeSpan.FromSeconds(10);
        // ReleaseWait is on the child's path too (the keeper's bounded release), so a raise of it
        // must trip the ratchet as well (fixup-01 F5). The dump allowance stays as the belt-and-
        // braces guard: with E1 the child no longer waits on WER/dump at all.
        TimeSpan worstCase =
            ExitWatchdog.DefaultBudget
            + ExitStuckReport.LogFatalBudget
            + SingleInstanceGuard.ReleaseWait
            + dumpAllowance;

        Check.That(worstCase).IsLessThan(RestartHandoff.MutexWait);
    }
}
