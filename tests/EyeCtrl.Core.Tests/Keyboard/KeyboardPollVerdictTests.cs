namespace EyeCtrl.Core.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using Microsoft.Extensions.Logging;

/// <summary>
/// D3: the poll DECIDES. <see cref="KeyboardSensitiveCheck"/> turns its outcome into a
/// <see cref="KeyboardPollVerdict"/> — a password field is Sensitive, a timeout / busy slot / error is
/// Unknown (fail closed), "no password" and "no readable element" are NotSensitive — and a result is applied
/// only if its owner generation (the #396 slot-owner generation) is newer than the one already applied, so a
/// stale read can never clear a newer verdict.
/// </summary>
public class KeyboardPollVerdictTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private sealed class Capture : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _lines.Add((logLevel, formatter(state, exception)));
        }

        public (LogLevel Level, string Message)[] Snapshot() { lock (_gate) return _lines.ToArray(); }
        public string[] Messages() => Snapshot().Select(e => e.Message).ToArray();
    }

    private sealed class FakePoll : IFocusedPasswordPoll
    {
        public Func<bool?> Read = () => null;
        public int Reads;

        public bool? ReadIsPassword()
        {
            Interlocked.Increment(ref Reads);
            return Read();
        }
    }

    private static async Task Done(Task task) =>
        await task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

    private static (KeyboardSensitiveCheck Check, KeyboardPollVerdict Verdict, Capture Log) New(
        IFocusedPasswordPoll poll, int timeBoxMs = 300)
    {
        var log = new Capture();
        var verdict = new KeyboardPollVerdict();
        return (new KeyboardSensitiveCheck(log, poll, TimeSpan.FromMilliseconds(timeBoxMs), verdict), verdict, log);
    }

    // ---- the mapping ------------------------------------------------------------------------------

    [Fact]
    public async Task APollThatSawAPasswordField_IsSensitive()
    {
        var (check, verdict, _) = New(new FakePoll { Read = () => true });

        await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

        Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
        Check.That(verdict.IsSensitive).IsTrue();
        Check.That(verdict.IsUnknown).IsFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task APollThatFoundNoPasswordFieldOrNoElement_IsNotSensitive(bool? answer)
    {
        var (check, verdict, _) = New(new FakePoll { Read = () => answer });

        await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));

        Check.That(verdict.Current).IsEqualTo(PollOutcome.NotSensitive);
        Check.That(verdict.IsSensitive).IsFalse();
        Check.That(verdict.IsUnknown).IsFalse();
    }

    [Fact]
    public async Task ATimeout_IsUnknown_AndIsLoggedAsFailingClosed()
    {
        // MUTANT PIN (drop the unknown branch): Timeout must stay Unknown, not collapse to NotSensitive.
        using var release = new ManualResetEventSlim(false);
        var (check, verdict, log) = New(new FakePoll { Read = () => { release.Wait(HangGuard); return false; } }, 50);

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

            Check.That(verdict.Current).IsEqualTo(PollOutcome.Unknown);
            Check.That(verdict.IsUnknown).IsTrue();
            Check.WithCustomMessage("unknown must be logged as failing closed, at Information: "
                    + string.Join(" | ", log.Messages()))
                .That(log.Snapshot().Any(l => l.Level == LogLevel.Information
                    && l.Message == "Sensitive verdict by poll: unknown (timeout) at=show gen=1"
                    + " - suggestions, learning and the LM prompt fail closed")).IsTrue();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task AFaultingPoll_IsUnknown()
    {
        var (check, verdict, _) = New(new FakePoll { Read = () => throw new InvalidOperationException("boom") });

        await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

        Check.That(verdict.Current).IsEqualTo(PollOutcome.Unknown);
    }

    [Fact]
    public async Task ABusySlot_IsUnknown_AndStartsNoSecondRead()
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var poll = new FakePoll { Read = () => { started.Set(); release.Wait(HangGuard); return false; } };
        var (check, verdict, _) = New(poll, 5_000);

        try
        {
            var first = check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown);
            Check.That(started.Wait(HangGuard, TestContext.Current.CancellationToken)).IsTrue();

            await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));

            Check.That(poll.Reads).IsEqualTo(1);
            Check.WithCustomMessage("busy means the field is unverified: fail closed")
                .That(verdict.Current).IsEqualTo(PollOutcome.Unknown);
            release.Set();
            await Done(first);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ANewerNotSensitivePoll_ClearsAnUnknown_AndAPasswordVerdict()
    {
        var answers = new Queue<Func<bool?>>(new Func<bool?>[]
        {
            () => throw new InvalidOperationException("boom"),   // unknown
            () => true,                                           // password
            () => false,                                          // not a password
        });
        var (check, verdict, _) = New(new FakePoll { Read = () => answers.Dequeue()() });

        await Done(check.Begin("show", evt: false, "p", FocusEventCounts.Unknown));
        Check.That(verdict.Current).IsEqualTo(PollOutcome.Unknown);
        await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));
        Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
        await Done(check.Begin("commit", evt: false, "-", FocusEventCounts.Unknown));
        Check.That(verdict.Current).IsEqualTo(PollOutcome.NotSensitive);
    }

    // ---- staleness (#396 generation) ---------------------------------------------------------------

    [Fact]
    public void AStaleResult_CannotClearANewerVerdict()
    {
        // MUTANT PIN (drop the generation check): an older generation must be refused.
        var verdict = new KeyboardPollVerdict();

        Check.That(verdict.Apply(2, PollOutcome.Sensitive).Applied).IsTrue();
        var stale = verdict.Apply(1, PollOutcome.NotSensitive);

        Check.That(stale.Applied).IsFalse();
        Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
        Check.That(verdict.Generation).IsEqualTo(2);
        Check.WithCustomMessage("the SAME generation is not newer either").That(verdict.Apply(2, PollOutcome.NotSensitive).Applied).IsFalse();
        Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
    }

    [Fact]
    public async Task AFinishedReadThatIsHeldBackWhileANewerCheckDecided_CannotClearItsVerdict()
    {
        // Check 1 reads "not a password" but is HELD just before its outcome is applied; its slot is already
        // free, so check 2 runs to the end and decides "password". Releasing check 1 afterwards must not
        // clear it. Every ordering here is forced causally: nothing is timed.
        using var holdFirst = new ManualResetEventSlim(false);
        using var firstHeld = new ManualResetEventSlim(false);
        var answers = new Queue<bool?>(new bool?[] { false, true });
        var poll = new FakePoll { Read = () => { lock (answers) return answers.Dequeue(); } };
        var (check, verdict, _) = New(poll, 10_000);
        check.BeforeVerdictForTest = gen =>
        {
            if (gen != 1) return;
            firstHeld.Set();
            holdFirst.Wait(HangGuard);
        };

        try
        {
            var first = check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown);
            Check.That(firstHeld.Wait(HangGuard, TestContext.Current.CancellationToken)).IsTrue();

            await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));
            Check.WithCustomMessage("check 2 must have run its own read, not found the slot busy")
                .That(poll.Reads).IsEqualTo(2);
            Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);

            holdFirst.Set();
            await Done(first);

            Check.WithCustomMessage("the stale 'not a password' from check 1 must not clear check 2's verdict")
                .That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
            Check.That(verdict.Generation).IsEqualTo(2);
        }
        finally
        {
            holdFirst.Set();
        }
    }

    // ---- the log -----------------------------------------------------------------------------------

    [Fact]
    public async Task APollDecisionIsLoggedOnceWhenItChangesTheVerdict_AndNotAgainWhenItDoesNot()
    {
        var (check, _, log) = New(new FakePoll { Read = () => true });

        await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));
        await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));

        var decisions = log.Messages().Where(m => m.StartsWith("Sensitive verdict by poll:")).ToArray();
        Check.That(decisions).ContainsExactly(
            "Sensitive verdict by poll: sensitive at=show poll=true gen=1 (was notpolled)");
    }

    [Fact]
    public async Task TheWordAndCommitLines_AreDebugWhenNothingIsRemarkable_AndInformationOtherwise()
    {
        var answers = new Queue<bool?>(new bool?[] { false, true });
        var (check, _, log) = New(new FakePoll { Read = () => answers.Dequeue() });

        await Done(check.Begin("word", evt: false, "-", FocusEventCounts.Unknown));
        await Done(check.Begin("commit", evt: false, "-", FocusEventCounts.Unknown));

        var lines = log.Snapshot().Where(l => l.Message.StartsWith("Sensitive check at")).ToArray();
        Check.That(lines.Select(l => l.Level)).ContainsExactly(LogLevel.Debug, LogLevel.Information);
    }

    [Fact]
    public async Task ACheckWithNoLogger_StillAppliesTheVerdict()
    {
        // The verdict is a safety state, not a diagnostic: a missing logger must not switch it off.
        var verdict = new KeyboardPollVerdict();
        var check = new KeyboardSensitiveCheck(null, new FakePoll { Read = () => true }, TimeSpan.FromSeconds(5), verdict);

        await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

        Check.That(verdict.Current).IsEqualTo(PollOutcome.Sensitive);
    }

    [Fact]
    public async Task AnEvidenceOnlyCheck_WithNoVerdict_StillLogsAndNeverThrows()
    {
        var log = new Capture();
        var check = new KeyboardSensitiveCheck(log, new FakePoll { Read = () => true }, TimeSpan.FromSeconds(5));

        await Done(check.Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

        Check.That(log.Messages().Count(m => m.StartsWith("Sensitive check at show"))).IsEqualTo(1);
        Check.That(log.Messages().Any(m => m.StartsWith("Sensitive verdict by poll"))).IsFalse();
    }
}
