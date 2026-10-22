namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using EyeCtrl.Supervisor.Supervision;
using NFluent;
using Xunit;

/// <summary>
/// The FallenBack dialog's one automatic retry (gaze-UX ticket A3), driven headless against
/// synthetic timestamps: the countdown fires the retry path exactly once and not before its
/// deadline, the per-session latch keeps a second FallenBack waiting for a click, and a manual
/// Retry or Close cancels the countdown. Mutation-proven: each behaviour below goes red on the
/// obvious mutant (fire twice, ignore the latch, ignore Cancel).
/// </summary>
public class FallenBackAutoRetryTests
{
    private static readonly DateTime T0 = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FireCounter
    {
        public int Fires;
        public void Retry() => Fires++;
    }

    [Fact]
    public void TheCountdown_IsSixtySeconds()
    {
        Check.That(FallenBackAutoRetry.Delay).IsEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void TheCountdown_FiresTheRetryExactlyOnce_AtItsDeadline()
    {
        var counter = new FireCounter();
        var retry = new FallenBackAutoRetry();

        Check.That(retry.TryArm(T0, counter.Retry)).IsTrue();
        Check.That(retry.IsCountdownArmed).IsTrue();

        retry.Tick(T0.AddSeconds(59));   // just below: nothing fires
        Check.That(counter.Fires).IsEqualTo(0);

        retry.Tick(T0.AddSeconds(60));   // at the deadline
        Check.That(counter.Fires).IsEqualTo(1);
        Check.That(retry.IsCountdownArmed).IsFalse();

        retry.Tick(T0.AddSeconds(61));   // a later tick must not fire it again
        retry.Tick(T0.AddSeconds(120));
        Check.That(counter.Fires).IsEqualTo(1);
    }

    [Fact]
    public void ASecondFallenBack_DoesNotAutoRetry()
    {
        var counter = new FireCounter();
        var retry = new FallenBackAutoRetry();

        Check.That(retry.TryArm(T0, counter.Retry)).IsTrue();
        retry.Tick(T0.AddSeconds(60));
        Check.That(counter.Fires).IsEqualTo(1);

        // The retry fell back again: the re-showed dialog waits for a click (no second countdown).
        Check.That(retry.TryArm(T0.AddSeconds(70), counter.Retry)).IsFalse();
        Check.That(retry.IsCountdownArmed).IsFalse();
        retry.Tick(T0.AddSeconds(200));
        Check.That(counter.Fires).IsEqualTo(1);
    }

    [Fact]
    public void AManualRetryOrClose_CancelsTheCountdown()
    {
        var counter = new FireCounter();
        var retry = new FallenBackAutoRetry();

        Check.That(retry.TryArm(T0, counter.Retry)).IsTrue();
        retry.Cancel();
        Check.That(retry.IsCountdownArmed).IsFalse();

        retry.Tick(T0.AddSeconds(60));
        retry.Tick(T0.AddSeconds(600));
        Check.That(counter.Fires).IsEqualTo(0);
    }

    [Fact]
    public void AManualRetry_ThenASecondFallenBack_DoesNotAutoRetry()
    {
        // The session's one countdown belongs to its FIRST episode even when a click got there
        // first: "a second FallenBack does not auto-retry" holds in every sequence.
        var counter = new FireCounter();
        var retry = new FallenBackAutoRetry();

        Check.That(retry.TryArm(T0, counter.Retry)).IsTrue();
        retry.Cancel();   // the user clicked Retry or Close before the countdown fired
        Check.That(retry.TryArm(T0.AddSeconds(70), counter.Retry)).IsFalse();
        retry.Tick(T0.AddSeconds(200));
        Check.That(counter.Fires).IsEqualTo(0);
    }

    [Fact]
    public void ARepeatArm_KeepsTheOriginalDeadline_AndRetargetsTheRetry()
    {
        // The dialog re-showing over an open countdown (not reachable from the machine's
        // transitions today) must not extend the wait, but must fire the dialog now on screen.
        var first = new FireCounter();
        var second = new FireCounter();
        var retry = new FallenBackAutoRetry();

        Check.That(retry.TryArm(T0, first.Retry)).IsTrue();
        Check.That(retry.TryArm(T0.AddSeconds(30), second.Retry)).IsTrue();

        retry.Tick(T0.AddSeconds(60));
        Check.That(first.Fires).IsEqualTo(0);
        Check.That(second.Fires).IsEqualTo(1);
    }

    [Fact]
    public void WithoutAnArm_NothingEverFires()
    {
        var counter = new FireCounter();
        var retry = new FallenBackAutoRetry();

        retry.Tick(T0);
        retry.Tick(T0.AddSeconds(600));
        Check.That(counter.Fires).IsEqualTo(0);
        Check.That(retry.IsCountdownArmed).IsFalse();
    }
}
