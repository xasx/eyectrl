namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// Review 01 M2: Setup writes <c>setup-last.json</c> at least 20 s after it starts the App, so the
/// watch must keep looking for a newer record after startup. A fake clock and a captured schedule
/// pin: a record arriving 25 s late shows exactly once; the window closes with nothing when no
/// record arrives; the immediate check still shows an unseen record from the previous session;
/// and the file-change path can show without waiting for the next poll.
/// </summary>
public class SetupNoticeWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : ISetupNoticeStore
    {
        public SetupResult? Record { get; set; }
        public DateTimeOffset? Shown { get; set; }

        public string RecordPath => "";
        public SetupResult? TryReadRecord() => Record;
        public DateTimeOffset? ReadShownTime() => Shown;
        public void WriteShownTime(DateTimeOffset time) => Shown = time;
    }

    private sealed class Harness
    {
        public FakeStore Store { get; } = new();
        public DateTimeOffset Now { get; set; } = Start;
        public List<(TimeSpan Delay, Action Tick)> Scheduled { get; } = [];
        public List<string> Shown { get; } = [];
        public Action? FileChange { get; private set; }
        public bool FileWatchDisposed { get; private set; }

        public SetupNoticeWatch Create() => new(
            new SetupLastNoticeService(
                Store,
                (key, args) => args.Length == 0 ? key : key + "|" + string.Join(",", args),
                () => "✓ OK",
                (text, _) => Shown.Add(text)),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(2),
            schedule: (delay, tick) => Scheduled.Add((delay, tick)),
            now: () => Now,
            fileChange: onChange =>
            {
                FileChange = onChange;
                return new ActionDisposable(() => FileWatchDisposed = true);
            });

        private sealed class ActionDisposable : IDisposable
        {
            private readonly Action _dispose;
            public ActionDisposable(Action dispose) => _dispose = dispose;
            public void Dispose() => _dispose();
        }
    }

    private static SetupResult Record(string? version = "1.5.0", DateTimeOffset? time = null) =>
        new()
        {
            Verb = SetupVerb.Update,
            Outcome = SetupOutcome.Degraded,
            Version = version,
            Time = time ?? Start,
        };

    [Fact]
    public void ARecordArriving25SecondsLate_ShowsExactlyOnce()
    {
        var harness = new Harness();
        harness.Store.Record = null;   // nothing at startup
        var watch = harness.Create();

        watch.Start();
        Check.That(watch.NoticeShown).IsFalse();
        Check.That(harness.Scheduled).HasSize(1);

        // 25 s later Setup writes the terminal record, and the poll fires.
        harness.Now = Start.AddSeconds(25);
        harness.Store.Record = Record(time: harness.Now);
        harness.Scheduled[^1].Tick();

        Check.That(watch.NoticeShown).IsTrue();
        Check.That(watch.IsDone).IsTrue();
        Check.That(harness.Shown).HasSize(1);
        Check.That(harness.Shown[0]).IsEqualTo("update.notice.degraded|1.5.0");
        Check.That(harness.Scheduled).HasSize(1);   // no further poll after the show

        // A later tick (a straggler or a file-change race) shows nothing twice.
        watch.Poll();
        Check.That(harness.Shown).HasSize(1);
    }

    [Fact]
    public void AnUnseenPreviousSessionRecord_ShowsAtStart_AndTheWatchStillWaitsForThisSession()
    {
        // Review 02 R5: an old notice-worthy record surfaced at the immediate check must not end
        // the watch — this session's own record is still to come, and the once-per-time rule makes
        // a second show of the SAME record impossible.
        var harness = new Harness();
        harness.Store.Record = Record(time: Start.AddMinutes(-30));

        var watch = harness.Create();
        watch.Start();

        Check.That(watch.NoticeShown).IsTrue();
        Check.That(watch.IsDone).IsFalse();
        Check.That(harness.Shown).HasSize(1);
        Check.That(harness.Scheduled).HasSize(1);

        // 25 s later this session's record lands; the poll shows it once and ends the watch.
        harness.Now = Start.AddSeconds(25);
        harness.Store.Record = Record(time: harness.Now);
        harness.Scheduled[^1].Tick();

        Check.That(harness.Shown).HasSize(2);
        Check.That(harness.Shown[1]).IsEqualTo("update.notice.degraded|1.5.0");
        Check.That(watch.IsDone).IsTrue();
        Check.That(watch.NoticeShown).IsTrue();
    }

    [Fact]
    public void TheFileChangePath_ShowsWithoutWaitingForTheNextPoll()
    {
        var harness = new Harness();
        var watch = harness.Create();
        watch.Start();
        Check.That(harness.FileChange).IsNotNull();

        harness.Now = Start.AddSeconds(28);
        harness.Store.Record = Record(time: harness.Now);
        harness.FileChange!.Invoke();

        Check.That(watch.NoticeShown).IsTrue();
        Check.That(harness.Shown).HasSize(1);
        Check.That(watch.IsDone).IsTrue();
    }

    [Fact]
    public void TheWindowCloses_WithNothing_WhenNoRecordArrives()
    {
        var harness = new Harness();
        var watch = harness.Create();
        watch.Start();
        Check.That(harness.Scheduled).HasSize(1);

        // Walk the poll chain past the window.
        for (var i = 0; i < 50 && !watch.IsDone; i++)
        {
            harness.Now = harness.Now.AddSeconds(2);
            var pending = harness.Scheduled[^1];
            pending.Tick();
        }

        Check.That(watch.IsDone).IsTrue();
        Check.That(watch.NoticeShown).IsFalse();
        Check.That(harness.Shown).IsEmpty();
        Check.That(harness.Now - Start >= TimeSpan.FromSeconds(90)).IsTrue();
        Check.That(harness.FileWatchDisposed).IsTrue();
    }
}
