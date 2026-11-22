namespace EyeCtrl.Core.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using Microsoft.Extensions.Logging;

/// <summary>
/// D4 of the password-detector diagnostics: the read-only IsPassword side poll at the keyboard's show/hide
/// moment. The properties that matter: one line per check at Information (the day file starts there) with the
/// event verdict, the poll, the live focus-event counters and the process name; the poll never waits for a
/// hanging read; and no field content (or exception message) can reach a line.
/// </summary>
public class KeyboardSensitiveCheckTests
{
    private const string Marker = "Zebrafink-Marker";

    private static readonly FocusEventCounts Counts = new(12, 1, 2, 3);

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

    private static KeyboardSensitiveCheck New(Capture log, IFocusedPasswordPoll? poll, int timeBoxMs = 300) =>
        new(log, poll, TimeSpan.FromMilliseconds(timeBoxMs));

    // Hang guard only, never a timing assertion: the poll runs on the thread pool, and a loaded suite can
    // delay a continuation. Every ordering below is proved causally instead.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static async Task Done(Task task) =>
        await task.WaitAsync(HangGuard, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ACheck_LogsAtInformation_TheEventVerdictThePollTheProcessAndTheLiveCounts()
    {
        var log = new Capture();

        await Done(New(log, new FakePoll { Read = () => true }).Begin("show", evt: true, "msedge.exe", Counts));

        Check.That(log.Messages()).ContainsExactly(
            "Sensitive check at show: event=true poll=true proc=msedge.exe"
            + " events=12 password=1 failures=2 own=3");
        Check.WithCustomMessage("The day file starts at Information: a Debug line is a no-show at the sitting.")
            .That(log.Snapshot().All(l => l.Level == LogLevel.Information)).IsTrue();
    }

    [Fact]
    public async Task ThePollAndTheEventVerdict_AreLoggedSideBySide_WhenTheyDisagree()
    {
        // The PW1 failure shape: the event-driven verdict says not sensitive, the direct poll says password.
        var log = new Capture();

        await Done(New(log, new FakePoll { Read = () => true }).Begin("show", evt: false, "msedge.exe", Counts));

        Check.That(log.Messages()).ContainsExactly(
            "Sensitive check at show: event=false poll=true proc=msedge.exe"
            + " events=12 password=1 failures=2 own=3");
    }

    [Fact]
    public async Task NoReadableForeignElement_IsNone()
    {
        var log = new Capture();

        await Done(New(log, new FakePoll { Read = () => null }).Begin("hide", evt: false, "msedge.exe", Counts));

        Check.That(log.Messages()).ContainsExactly(
            "Sensitive check at hide: event=false poll=none proc=msedge.exe"
            + " events=12 password=1 failures=2 own=3");
    }

    [Fact]
    public async Task UnknownCounts_RenderAsDashes_NeverAsAZeroThatWouldReadAsNoEvents()
    {
        var log = new Capture();

        await Done(New(log, new FakePoll { Read = () => false })
            .Begin("show", evt: false, "msedge.exe", FocusEventCounts.Unknown));

        Check.That(log.Messages()).ContainsExactly(
            "Sensitive check at show: event=false poll=false proc=msedge.exe"
            + " events=- password=- failures=- own=-");
    }

    [Fact]
    public async Task AnError_LogsOnlyTheType_NeverTheMessage()
    {
        var log = new Capture();
        var poll = new FakePoll { Read = () => throw new InvalidOperationException(Marker) };

        await Done(New(log, poll).Begin("show", evt: false, "msedge.exe", Counts));

        Check.That(log.Messages()).ContainsExactly(
            "Sensitive check at show: event=false poll=error:InvalidOperationException proc=msedge.exe"
            + " events=12 password=1 failures=2 own=3");
        Check.That(log.Messages().Any(l => l.Contains(Marker, StringComparison.Ordinal))).IsFalse();
    }

    [Fact]
    public async Task AHangingRead_IsLoggedTimeout_WhileTheReadIsStillRunning()
    {
        // CAUSAL, not wall-clock: the read cannot end before `release` is set, so a line that arrives while it
        // is unset came from the time box.
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var readEnded = 0;
        var poll = new FakePoll
        {
            Read = () =>
            {
                release.Wait(TimeSpan.FromSeconds(60));
                Volatile.Write(ref readEnded, 1);
                return true;
            },
        };

        try
        {
            await Done(New(log, poll, timeBoxMs: 100).Begin("show", evt: false, "msedge.exe", Counts));

            Check.WithCustomMessage("the verdict must not wait for the hanging read")
                .That(Volatile.Read(ref readEnded)).IsEqualTo(0);
            Check.That(log.Messages()).ContainsExactly(
                "Sensitive check at show: event=false poll=timeout proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task WhileAnAbandonedReadIsStillRunning_TheNextCheckIsBusy_AndStartsNoSecondRead()
    {
        var log = new Capture();
        using var release = new ManualResetEventSlim(false);
        var poll = new FakePoll { Read = () => { release.Wait(TimeSpan.FromSeconds(60)); return null; } };
        var check = New(log, poll, timeBoxMs: 50);

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", Counts));   // times out, read still hanging
            await Done(check.Begin("hide", evt: false, "msedge.exe", Counts));   // must not pile on a thread

            Check.That(poll.Reads).IsEqualTo(1);
            Check.That(log.Messages()).ContainsExactly(
                "Sensitive check at show: event=false poll=timeout proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3",
                "Sensitive check at hide: event=false poll=busy proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task NoPoll_WritesNothing()
    {
        var log = new Capture();

        await Done(New(log, poll: null).Begin("show", evt: true, "msedge.exe", Counts));

        Check.That(log.Snapshot()).IsEmpty();
    }

    [Fact]
    public void TheDefaultTimeBox_IsAtMostThreeHundredMilliseconds()
    {
        // D4's bound is part of the ticket: the poll is abandoned within 300 ms, never later.
        Check.That(KeyboardSensitiveCheck.DefaultTimeBox <= TimeSpan.FromMilliseconds(300)).IsTrue();
    }
}
