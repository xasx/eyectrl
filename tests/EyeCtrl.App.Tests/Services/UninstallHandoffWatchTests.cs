namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Text.Json;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// C9 S6e fixup-08: the watch over one uninstall hand-off. The hand-off crosses three processes
/// (installed launcher → run copy → elevated child), so the end is read from Setup's one result
/// record and its single-session mutex. These tests drive a fake clock and a captured schedule:
/// the record is written before Setup's result screen and the session ends after it, so the watch
/// must wait for both; and every way the wait can go wrong must still end — never strand the
/// owner.
/// </summary>
public class UninstallHandoffWatchTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 11, 6, 21, 24, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Vanish = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(10);

    private sealed class Harness
    {
        public SetupResult? Record { get; set; }
        public bool SessionLive { get; set; }
        public DateTimeOffset Now { get; set; } = Anchor;
        public List<(TimeSpan Delay, Action Tick)> Scheduled { get; } = [];
        public List<(UninstallHandoffEnd End, SetupOutcome? Outcome)> Ends { get; } = [];
        public int DisposedHandles { get; private set; }
        public int ReadCalls { get; private set; }
        public bool ThrowOnRead { get; set; }
        public bool ThrowOnNow { get; set; }
        public bool ThrowOnSchedule { get; set; }

        public UninstallHandoffWatch Create() => new(
            () =>
            {
                ReadCalls++;
                return ThrowOnRead ? throw new InvalidOperationException("record read failed") : Record;
            },
            () => SessionLive,
            (delay, tick) =>
            {
                if (ThrowOnSchedule)
                    throw new InvalidOperationException("schedule failed");
                Scheduled.Add((delay, tick));
                return new ActionDisposable(() => DisposedHandles++);
            },
            () => ThrowOnNow ? throw new InvalidOperationException("clock failed") : Now,
            startGrace: Grace,
            maximumWatch: Cap,
            vanishGrace: Vanish);

        public void Tick()
        {
            var (_, tick) = Scheduled[^1];
            tick();
        }

        public UninstallHandoffWatch Begin()
        {
            var watch = Create();
            watch.Start(Anchor, (end, outcome) => Ends.Add((end, outcome)));
            return watch;
        }

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _dispose;
            public ActionDisposable(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }

    private static SetupResult Uninstall(SetupOutcome outcome, DateTimeOffset? time = null) =>
        new()
        {
            Verb = SetupVerb.Uninstall,
            Outcome = outcome,
            Time = time ?? Anchor.AddSeconds(90),
        };

    [Fact]
    public void ALiveSessionAndNoRecord_KeepsPolling()
    {
        var harness = new Harness { SessionLive = true };

        harness.Begin();

        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.Scheduled).HasSize(1);
        Check.That(harness.Scheduled[0].Delay).IsEqualTo(UninstallHandoffWatch.PollInterval);
    }

    [Fact]
    public void AStaleRecordFromThePreviousRun_IsIgnored()
    {
        // The 05.11 sitting left a Cancelled uninstall record behind; a new hand-off must not read
        // it as its own end.
        var harness = new Harness
        {
            SessionLive = true,
            Record = Uninstall(SetupOutcome.Cancelled, Anchor.AddMinutes(-30)),
        };

        harness.Begin();

        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.Scheduled).HasSize(1);
    }

    [Theory]
    [InlineData(SetupVerb.Update, SetupOutcome.Cancelled)]
    [InlineData(SetupVerb.Rollback, SetupOutcome.Failed)]
    [InlineData(SetupVerb.Repair, SetupOutcome.AllFailed)]
    public void ARecordForAnotherVerb_IsIgnored(SetupVerb verb, SetupOutcome outcome)
    {
        var harness = new Harness
        {
            SessionLive = true,
            Record = new SetupResult { Verb = verb, Outcome = outcome, Time = Anchor.AddMinutes(5) },
        };

        harness.Begin();

        Check.That(harness.Ends).IsEmpty();
    }

    [Fact]
    public void TheTransientHandedOffOutcome_IsNeverTerminal()
    {
        var harness = new Harness
        {
            SessionLive = true,
            Record = Uninstall(SetupOutcome.HandedOff, Anchor.AddSeconds(2)),
        };

        harness.Begin();

        Check.That(harness.Ends).IsEmpty();
    }

    [Fact]
    public void ACancelledRecord_WhileSetupStillShowsItsResult_WaitsForTheSessionToEnd()
    {
        // Setup records the result, then blocks until its result window closes (10 s for a
        // cancel). Restoring Settings at the record write would cover that window — exactly the
        // failure-text problem fixup-08 must not reintroduce.
        var harness = new Harness { SessionLive = true };
        harness.Begin();

        harness.Now = Anchor.AddSeconds(60);
        harness.Record = Uninstall(SetupOutcome.Cancelled, harness.Now);
        harness.Tick();

        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.Scheduled).HasSize(2);

        harness.Now = harness.Now.AddSeconds(10);
        harness.SessionLive = false;
        harness.Tick();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.NotUninstalled);
        Check.That(harness.Ends[0].Outcome).IsEqualTo(SetupOutcome.Cancelled);
        Check.That(harness.Scheduled).HasSize(2);
    }

    [Theory]
    [InlineData(SetupOutcome.Cancelled)]
    [InlineData(SetupOutcome.Blocked)]
    [InlineData(SetupOutcome.Failed)]
    [InlineData(SetupOutcome.NotApplied)]
    [InlineData(SetupOutcome.VerifyFailed)]
    [InlineData(SetupOutcome.AllFailed)]
    public void EveryNonUninstalledRecord_EndsAsNotUninstalled(SetupOutcome outcome)
    {
        var harness = new Harness { Record = Uninstall(outcome, Anchor.AddSeconds(5)) };

        harness.Begin();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.NotUninstalled);
        Check.That(harness.Ends[0].Outcome).IsEqualTo(outcome);
    }

    [Fact]
    public void AnUninstalledRecord_EndsAsUninstalled_WhenTheSessionGoes()
    {
        // Setup stops the App in the same run; Settings must never be raised over the removal.
        var harness = new Harness
        {
            SessionLive = true,
            Record = Uninstall(SetupOutcome.Uninstalled, Anchor.AddSeconds(5)),
        };

        harness.Begin();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = Anchor.AddSeconds(6);
        harness.SessionLive = false;
        harness.Tick();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Uninstalled);
        Check.That(harness.Ends[0].Outcome).IsEqualTo(SetupOutcome.Uninstalled);
    }

    [Fact]
    public void TheDecision_AnUninstalledRecord_EndsEvenWhileTheSessionLingers()
    {
        // Reached through the cap: the loop reads the record at the cap even if the mutex is
        // stuck, and a completed uninstall must still keep Settings hidden.
        var verdict = UninstallHandoffOutcome.Evaluate(
            Anchor, Anchor.AddSeconds(5),
            Uninstall(SetupOutcome.Uninstalled, Anchor.AddSeconds(4)),
            sessionLive: true, sessionSeen: true, sessionGoneSince: null,
            startGrace: Grace, vanishGrace: Vanish, maximumWatch: Cap);

        Check.That(verdict.Done).IsTrue();
        Check.That(verdict.End).IsEqualTo(UninstallHandoffEnd.Uninstalled);
        Check.That(verdict.Outcome).IsEqualTo(SetupOutcome.Uninstalled);
    }

    [Fact]
    public void ASessionThatVanishesWithoutARecord_EndsAsUnknown_AfterTheVanishWindow()
    {
        // A crashed Setup must still restore Settings — but only after the launcher → run-copy
        // takeover window, because during that window the missing session is normal.
        var harness = new Harness { SessionLive = true };
        harness.Begin();

        harness.Now = Anchor.AddSeconds(5);
        harness.SessionLive = false;
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = harness.Now.Add(Vanish).AddSeconds(-1);
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = harness.Now.AddSeconds(1);
        harness.Tick();
        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
        Check.That(harness.Ends[0].Outcome).IsNull();
    }

    [Fact]
    public void TheLauncherRunCopyGap_DoesNotEndTheWatch()
    {
        // Review-08 H1, the exact F4 regression: the installed launcher starts the run copy and
        // returns at once, so a poll can see the session gone for a moment. The run copy takes it
        // again; the record (a cancel here) is the end, not the gap.
        var harness = new Harness { SessionLive = true };
        harness.Begin();                       // the launcher holds the session

        harness.Now = Anchor.AddSeconds(1);
        harness.SessionLive = false;           // launcher exited, run copy still starting
        harness.Tick();
        Check.WithCustomMessage(
                "The watch ended in the launcher → run-copy gap: Settings would come back over Q8 (F4).")
            .That(harness.Ends).IsEmpty();

        harness.Now = Anchor.AddSeconds(2);
        harness.SessionLive = true;            // the run copy is now the session
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        // The owner cancels: the record is written, then the result screen holds the session for
        // 10 s, then the session ends.
        harness.Now = Anchor.AddMinutes(1);
        harness.Record = Uninstall(SetupOutcome.Cancelled, harness.Now);
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = harness.Now.AddSeconds(10);
        harness.SessionLive = false;
        harness.Tick();
        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.NotUninstalled);
        Check.That(harness.Ends[0].Outcome).IsEqualTo(SetupOutcome.Cancelled);
    }

    [Fact]
    public void AReappearingSession_RestartsTheVanishWindow()
    {
        var harness = new Harness { SessionLive = true };
        harness.Begin();

        harness.Now = Anchor.AddSeconds(1);
        harness.SessionLive = false;           // first gap
        harness.Tick();

        harness.Now = Anchor.AddSeconds(10);
        harness.SessionLive = true;            // back: the run copy took over
        harness.Tick();

        harness.Now = Anchor.AddSeconds(11);
        harness.SessionLive = false;           // a second gap starts here
        harness.Tick();

        // 14 s after the SECOND disappearance (25 s after the first) is still inside the window.
        harness.Now = Anchor.AddSeconds(11).Add(Vanish).AddSeconds(-1);
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = harness.Now.AddSeconds(1);
        harness.Tick();
        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
    }

    [Fact]
    public void TheFirstPoll_WithAStaleRecordAndNoSession_DoesNotEndTheWatch()
    {
        // Review-08 M1: production's first poll runs before the launcher takes its session, so the
        // record IS read. The 05.11 sitting left a Cancelled record behind; the time check is what
        // keeps it from ending a new hand-off at once.
        var harness = new Harness
        {
            SessionLive = false,
            Record = Uninstall(SetupOutcome.Cancelled, Anchor.AddMinutes(-30)),
        };

        harness.Begin();

        Check.That(harness.ReadCalls).IsEqualTo(1);
        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.Scheduled).HasSize(1);

        // …and the run is still alive: a live session keeps it waiting.
        harness.Now = Anchor.AddSeconds(2);
        harness.SessionLive = true;
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();
    }

    [Fact]
    public void NoSessionEverSeen_WaitsForTheGrace_ThenEndsAsUnknown()
    {
        var harness = new Harness();
        harness.Begin();
        Check.That(harness.Ends).IsEmpty();

        // One second short of the grace: still waiting (a slow ShellExecute start).
        harness.Now = Anchor.Add(Grace).AddSeconds(-1);
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = Anchor.Add(Grace);
        harness.Tick();
        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
    }

    [Fact]
    public void ASessionStillHeldAtTheCap_EndsAsUnknown()
    {
        // Never strand: even a stuck session (or a probe that cannot answer) must not hold
        // Settings hidden forever.
        var harness = new Harness { SessionLive = true };
        harness.Begin();

        harness.Now = Anchor.Add(Cap).AddSeconds(-1);
        harness.Tick();
        Check.That(harness.Ends).IsEmpty();

        harness.Now = Anchor.Add(Cap);
        harness.Tick();
        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
    }

    [Fact]
    public void AThrowingRecordRead_IsTreatedAsNoRecordYet_AndKeepsPolling()
    {
        // A transient share violation while Setup writes the record must not end the watch.
        var harness = new Harness { SessionLive = true, ThrowOnRead = true };

        harness.Begin();

        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.Scheduled).HasSize(1);

        harness.ThrowOnRead = false;
        harness.Now = Anchor.AddSeconds(5);
        harness.Record = Uninstall(SetupOutcome.Cancelled, harness.Now);
        harness.SessionLive = false;
        harness.Tick();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.NotUninstalled);
    }

    [Fact]
    public void AFaultInTheWatchItself_EndsAsUnknown_RatherThanWaiting()
    {
        // Never strand: a watch that cannot even read its clock has to fail closed into a restore.
        var harness = new Harness { SessionLive = true, ThrowOnNow = true };

        harness.Begin();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
    }

    [Fact]
    public void AWatchThatCannotScheduleTheNextPoll_EndsAsUnknown()
    {
        // No tick will ever arrive, so waiting would strand Settings off-screen.
        var harness = new Harness { SessionLive = true, ThrowOnSchedule = true };

        harness.Begin();

        Check.That(harness.Ends).HasSize(1);
        Check.That(harness.Ends[0].End).IsEqualTo(UninstallHandoffEnd.Unknown);
    }

    [Fact]
    public void TheEndIsDeliveredExactlyOnce_AndAStragglerTickDoesNothing()
    {
        var harness = new Harness { Record = Uninstall(SetupOutcome.Cancelled, Anchor.AddSeconds(2)) };
        var watch = harness.Begin();

        Check.That(harness.Ends).HasSize(1);
        var pendingCount = harness.Scheduled.Count;

        // A tick from an already-disposed timer (or a second Anchor) must not call back again.
        watch.Start(Anchor, (end, outcome) => harness.Ends.Add((end, outcome)));
        if (harness.Scheduled.Count > pendingCount)
            harness.Tick();

        Check.That(harness.Ends).HasSize(1);
    }

    [Fact]
    public void DisposeBeforeTheEnd_CallsNothing()
    {
        var harness = new Harness { SessionLive = true };
        var watch = harness.Begin();

        watch.Dispose();
        harness.Now = Anchor.AddSeconds(5);
        harness.SessionLive = false;
        harness.Tick();

        Check.That(harness.Ends).IsEmpty();
        Check.That(harness.DisposedHandles).IsGreaterOrEqualThan(1);
    }

    [Fact]
    public void TheRecordIsNotReadWhileSetupStillHoldsItsSession()
    {
        // Setup writes the record while it holds the session mutex, so a poll during the session
        // must not open the file: a concurrent read can make Setup's replace fail, and its
        // recorder only logs — the outcome would be lost.
        var harness = new Harness { SessionLive = true };
        harness.Begin();

        Check.That(harness.ReadCalls).IsEqualTo(0);

        harness.Now = Anchor.AddSeconds(5);
        harness.Tick();
        Check.That(harness.ReadCalls).IsEqualTo(0);

        harness.SessionLive = false;
        harness.Tick();
        Check.That(harness.ReadCalls).IsGreaterOrEqualThan(1);
    }

    [Fact]
    public void ReadRecord_ParsesTheShapeSetupWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "eyectrl_f08_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "setup-last.json");
            var written = new SetupResult
            {
                Verb = SetupVerb.Uninstall,
                Outcome = SetupOutcome.Cancelled,
                Time = Anchor,
                Detail = "cancelled",
            };
            File.WriteAllText(path, JsonSerializer.Serialize(written, SetupJson.Options));

            var read = UninstallHandoffWatchFactory.ReadRecord(path);

            Check.That(read).IsNotNull();
            Check.That(read!.Verb).IsEqualTo(SetupVerb.Uninstall);
            Check.That(read.Outcome).IsEqualTo(SetupOutcome.Cancelled);
            Check.That(read.Time).IsEqualTo(Anchor);
            Check.That(read.Detail).IsEqualTo("cancelled");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { /* best effort */ }
        }
    }

    [Fact]
    public void ReadingAMissingRecord_ReturnsNull()
    {
        Check.That(UninstallHandoffWatchFactory.ReadRecord(
            Path.Combine(Path.GetTempPath(), "eyectrl_f08_missing_" + Guid.NewGuid().ToString("N") + ".json")))
            .IsNull();
    }
}
