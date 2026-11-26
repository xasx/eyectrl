namespace EyeCtrl.Core.Tests.Keyboard;

using System;
using System.Collections.Concurrent;
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

        /// <summary>Runs after every line is recorded; lets a test act at the moment the line exists.</summary>
        public Action<string>? OnLine;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_gate) _lines.Add((logLevel, message));
            OnLine?.Invoke(message);
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
    public async Task ACompletedShowCheck_FreesItsSlotBeforeTheLine_SoTheFollowingHideIsNotBusy()
    {
        // The false `busy` from the 12th sitting: the show check's slot release rode its own
        // continuation on the read, so the show line could be logged while the slot was still 1.
        // The seam holds that read-owned release for as long as the test runs; only a release made
        // inline — before Report — can free the slot for the hide. Deterministic: whether the
        // continuation runs early or late, it never releases, and the hide must not report busy.
        var log = new Capture();
        var heldReleases = new ConcurrentQueue<Action>();
        var poll = new FakePoll { Read = () => false };
        var check = New(log, poll);
        check.ReleaseReadSlotForTest = heldReleases.Enqueue;

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", Counts));
            await Done(check.Begin("hide", evt: false, "msedge.exe", Counts));

            Check.WithCustomMessage("the hide must start its own read, not find the show's slot still held")
                .That(poll.Reads).IsEqualTo(2);
            Check.That(log.Messages()).ContainsExactly(
                "Sensitive check at show: event=false poll=false proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3",
                "Sensitive check at hide: event=false poll=false proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
            Check.WithCustomMessage("a completed check must never log busy")
                .That(log.Messages().Any(l => l.Contains("poll=busy", StringComparison.Ordinal))).IsFalse();
        }
        finally
        {
            check.ReleaseReadSlotForTest = null;
            while (heldReleases.TryDequeue(out var release))
                release();
        }
    }

    [Fact]
    public async Task ALateReleaseFromTheFinishedFirstRead_CannotFreeTheSlotOfTheReadThatReplacedIt()
    {
        // The trap in the fix: read 1 has finished, but its read-owned release continuation can
        // still run AFTER read 2 has claimed the slot. A plain Exchange(0) there would free read 2's
        // slot and let a third check start a second UIA read beside it. The seam holds read 1's
        // release until read 2 is demonstrably in flight, then fires it — a real late release, not
        // one that merely has not been scheduled yet.
        var log = new Capture();
        var heldReleases = new ConcurrentQueue<Action>();
        using var read2Gate = new ManualResetEventSlim(false);
        using var read2Started = new ManualResetEventSlim(false);
        var reads = 0;
        var poll = new FakePoll
        {
            Read = () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    return false;                       // read 1 answers at once; its check completes
                read2Started.Set();
                read2Gate.Wait(TimeSpan.FromSeconds(60));
                return true;                            // read 2 stays in flight while it holds the slot
            },
        };
        var check = New(log, poll, timeBoxMs: 60_000);
        check.ReleaseReadSlotForTest = heldReleases.Enqueue;
        Task? hide = null;

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", Counts)); // read 1 done, release held

            // Wait for the read-owned release to be queued, so the release below is genuinely late.
            Check.WithCustomMessage("read 1's release continuation never ran")
                .That(SpinWait.SpinUntil(() => !heldReleases.IsEmpty, TimeSpan.FromSeconds(30))).IsTrue();

            hide = check.Begin("hide", evt: false, "msedge.exe", Counts);       // read 2 claims the slot
            Check.WithCustomMessage("read 2 never claimed the freed slot")
                .That(read2Started.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
                .IsTrue();

            Check.That(heldReleases.TryDequeue(out var lateRelease)).IsTrue();
            lateRelease!();                             // read 1 releases late — after read 2's claim

            // Owner-checked: the late release cannot free read 2's slot, so a third check is busy and
            // starts no second read (busy is reported synchronously; no need to wait for read 2).
            Check.WithCustomMessage("read 1's late release freed read 2's slot")
                .That(check.Begin("show", evt: false, "msedge.exe", Counts).IsCompleted).IsTrue();
            Check.That(log.Messages()).Contains(
                "Sensitive check at show: event=false poll=busy proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
            Check.WithCustomMessage("a third read started beside read 2")
                .That(poll.Reads).IsEqualTo(2);

            read2Gate.Set();
            await Done(hide);
            Check.That(log.Messages()).Contains(
                "Sensitive check at hide: event=false poll=true proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
        }
        finally
        {
            check.ReleaseReadSlotForTest = null;
            read2Gate.Set();
            if (hide is not null)
            {
                try { await hide.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
                catch (Exception) { /* only unwinds a failed assertion's in-flight read */ }
            }
        }
    }

    [Fact]
    public async Task TheShowLine_IsLoggedWithTheSlotAlreadyFree()
    {
        // F1: a hide started FROM the show line — the earliest moment a reader of the day file can act
        // — must find the slot free. It is started synchronously inside Log, so a release placed after
        // Report cannot hide behind the Begin task's completion. The read-owned releases are held by
        // the seam, so the line's own check cannot free the slot later.
        var log = new Capture();
        var heldReleases = new ConcurrentQueue<Action>();
        var poll = new FakePoll { Read = () => false };
        var check = New(log, poll, timeBoxMs: 60_000);
        check.ReleaseReadSlotForTest = heldReleases.Enqueue;
        Task? hide = null;
        log.OnLine = line =>
        {
            if (hide is null && line.StartsWith("Sensitive check at show", StringComparison.Ordinal))
                hide = check.Begin("hide", evt: false, "msedge.exe", Counts);
        };

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", Counts));
            Check.WithCustomMessage("the show line never started the hide").That(hide).IsNotNull();
            await Done(hide!);

            Check.WithCustomMessage("the hide started from the show line found the slot still held")
                .That(log.Messages().Any(l => l.Contains("poll=busy", StringComparison.Ordinal))).IsFalse();
            Check.That(poll.Reads).IsEqualTo(2);
            Check.That(log.Messages()).ContainsExactly(
                "Sensitive check at show: event=false poll=false proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3",
                "Sensitive check at hide: event=false poll=false proc=msedge.exe"
                + " events=12 password=1 failures=2 own=3");
        }
        finally
        {
            check.ReleaseReadSlotForTest = null;
            while (heldReleases.TryDequeue(out var release))
                release();
        }
    }

    [Fact]
    public async Task AfterAnAbandonedReadEnds_TheSlotRecovers()
    {
        // F2: owner attribution made recovery load-bearing — a release that never runs, or one whose
        // generation was overwritten, would leave every later check in the sitting `busy` and blank the
        // PW1 evidence. Seam-free on purpose: the production read-owned release must free the slot once
        // the hung read really ends. The poll until read #2 is a hang guard, not a timing assertion.
        var log = new Capture();
        using var gate = new ManualResetEventSlim(false);
        var reads = 0;
        var poll = new FakePoll
        {
            Read = () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    gate.Wait(TimeSpan.FromSeconds(60));
                return false;
            },
        };
        var check = New(log, poll, timeBoxMs: 50);

        try
        {
            await Done(check.Begin("show", evt: false, "msedge.exe", Counts)); // times out, read 1 hangs
            await Done(check.Begin("hide", evt: false, "msedge.exe", Counts)); // must not start a second read
            Check.That(log.Messages().Last()).Contains("poll=busy");
            Check.That(poll.Reads).IsEqualTo(1);

            gate.Set();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref poll.Reads) < 2 && DateTime.UtcNow < deadline)
            {
                await Done(check.Begin("show", evt: false, "msedge.exe", Counts));
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Check.WithCustomMessage("the slot never recovered after the abandoned read ended")
                .That(Volatile.Read(ref poll.Reads)).IsEqualTo(2);
        }
        finally
        {
            gate.Set();
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
