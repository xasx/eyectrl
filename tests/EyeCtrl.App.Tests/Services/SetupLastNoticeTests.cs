namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// C9 S6f's one-time setup-last notice: the outcome routing (rolled back names the RESTORED
/// version; degraded the running one; the failed family routes by verb and copes with a null
/// version; uninstall and repair failures stay silent), "exactly once" keyed by the record's
/// <c>time</c> against <c>setup-notice-shown.json</c> (the coordinator's ruling — setup-last.json
/// is never touched), and the crash-safety order: the marker is written BEFORE the card shows.
/// </summary>
public class SetupLastNoticeTests
{
    private static readonly DateTimeOffset RecordTime =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : ISetupNoticeStore
    {
        public SetupResult? Record { get; set; }
        public DateTimeOffset? Shown { get; set; }
        public bool ThrowOnRead { get; set; }
        public int Writes { get; private set; }
        public List<string>? Order { get; set; }

        public string RecordPath => "";
        public SetupResult? TryReadRecord() =>
            ThrowOnRead ? throw new IOException("unreadable") : Record;

        public DateTimeOffset? ReadShownTime() => Shown;

        public void WriteShownTime(DateTimeOffset time)
        {
            Writes++;
            Shown = time;
            Order?.Add("write");
        }
    }

    private sealed record Captured(string Text, string CloseLabel);

    private static SetupResult Record(
        SetupOutcome outcome,
        SetupVerb verb = SetupVerb.Update,
        string? version = "9.9.9",
        string? previous = null,
        DateTimeOffset? time = null) =>
        new()
        {
            Verb = verb,
            Outcome = outcome,
            Version = version,
            PreviousVersion = previous,
            Time = time ?? RecordTime,
        };

    private static (SetupLastNoticeService Service, FakeStore Store, List<Captured> Shown) Build(
        SetupResult? record, List<string>? order = null)
    {
        var shown = new List<Captured>();
        var store = new FakeStore { Record = record, Order = order };
        var service = new SetupLastNoticeService(
            store,
            (key, args) => args.Length == 0 ? key : key + "|" + string.Join(",", args),
            () => "✓ OK",
            (text, closeLabel) =>
            {
                shown.Add(new Captured(text, closeLabel));
                order?.Add("show");
            });
        return (service, store, shown);
    }

    [Fact]
    public void RolledBack_NamesTheRestoredVersion_NotTheFailedOne()
    {
        // Review 01 M3: Setup writes Finish(RolledBack, attempted, restored); Version is the
        // failed attempt, PreviousVersion the one now running — the sentence says "rolled back to".
        var (service, store, shown) = Build(Record(
            SetupOutcome.RolledBack, version: "2.0.0", previous: "1.4.0"));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown).HasSize(1);
        Check.That(shown[0].Text).IsEqualTo("update.notice.rolledBack|1.4.0");
        Check.That(shown[0].CloseLabel).IsEqualTo("✓ OK");
        Check.That(store.Shown).IsEqualTo(RecordTime);
    }

    [Fact]
    public void Degraded_NamesTheRunningVersion()
    {
        // Q12: the running version is the record's Version.
        var (service, _, shown) = Build(Record(SetupOutcome.Degraded, version: "3.1.0"));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown.Single().Text).IsEqualTo("update.notice.degraded|3.1.0");
    }

    [Theory]
    [InlineData(SetupOutcome.AllFailed, SetupVerb.Update, "2.0.0", "update.notice.failed|2.0.0")]
    [InlineData(SetupOutcome.VerifyFailed, SetupVerb.Update, null, "update.notice.failed.noVersion")]
    [InlineData(SetupOutcome.NotApplied, SetupVerb.Update, null, "update.notice.failed.noVersion")]
    [InlineData(SetupOutcome.Failed, SetupVerb.Update, "2.0.0", "update.notice.failed|2.0.0")]
    [InlineData(SetupOutcome.Failed, SetupVerb.Rollback, "2.0.0", "rollback.notice.failed")]
    [InlineData(SetupOutcome.AllFailed, SetupVerb.Rollback, null, "rollback.notice.failed")]
    public void TheFailedFamily_RoutesByVerb_AndCopesWithANullVersion(
        SetupOutcome outcome, SetupVerb verb, string? version, string expected)
    {
        // Review 01 N1: a refused record has Version=null (no "auf Version  ist" hole), and a
        // rollback failure must not read as an update.
        var (service, _, shown) = Build(Record(outcome, verb, version));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown.Single().Text).IsEqualTo(expected);
    }

    [Theory]
    [InlineData(SetupOutcome.Failed, SetupVerb.Uninstall)]
    [InlineData(SetupOutcome.AllFailed, SetupVerb.Repair)]
    public void AnUninstallOrRepairFailure_StaysSilent(SetupOutcome outcome, SetupVerb verb)
    {
        // S6e's own window shows an uninstall failure while EyeCtrl keeps running; the next start
        // must not announce a failed "update" that never happened.
        var (service, store, shown) = Build(Record(outcome, verb));

        Check.That(service.CheckStartup()).IsFalse();

        Check.That(shown).IsEmpty();
        Check.That(store.Writes).IsZero();
    }

    [Theory]
    [InlineData(SetupOutcome.Ok)]
    [InlineData(SetupOutcome.Uninstalled)]
    [InlineData(SetupOutcome.Repaired)]
    [InlineData(SetupOutcome.Cancelled)]
    [InlineData(SetupOutcome.Blocked)]
    [InlineData(SetupOutcome.HandedOff)]
    public void TheDeliberateSilentSet_ShowsNothing_AndWritesNoMarker(SetupOutcome outcome)
    {
        var (service, store, shown) = Build(Record(outcome));

        Check.That(service.CheckStartup()).IsFalse();

        Check.That(shown).IsEmpty();
        Check.That(store.Writes).IsZero();
    }

    [Fact]
    public void TheSameRecord_ShowsExactlyOnce()
    {
        var (service, store, shown) = Build(Record(SetupOutcome.RolledBack, previous: "1.4.0"));

        Check.That(service.CheckStartup()).IsTrue();
        Check.That(service.CheckStartup()).IsFalse();

        Check.That(shown).HasSize(1);
        Check.That(store.Writes).IsEqualTo(1);
    }

    [Fact]
    public void ANewerRecord_ShowsAgain_AgainstTheSameStore()
    {
        // Review 01 MUT1: an existing marker must not silence every later record — the rule is
        // strictly "newer time", not "any marker".
        var (service, store, shown) = Build(Record(SetupOutcome.RolledBack, previous: "1.4.0"));
        Check.That(service.CheckStartup()).IsTrue();

        store.Record = Record(
            SetupOutcome.Degraded, version: "1.5.0", time: RecordTime.AddHours(1));
        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown).HasSize(2);
        Check.That(shown[1].Text).IsEqualTo("update.notice.degraded|1.5.0");
        Check.That(store.Shown).IsEqualTo(RecordTime.AddHours(1));
    }

    [Fact]
    public void AnAlreadyShownRecord_IsSilent_WithoutWritingAgain()
    {
        var record = Record(SetupOutcome.RolledBack, previous: "1.4.0");
        var (service, store, shown) = Build(record);
        store.Shown = record.Time;   // a previous start already showed it

        Check.That(service.CheckStartup()).IsFalse();

        Check.That(shown).IsEmpty();
        Check.That(store.Writes).IsZero();
    }

    [Fact]
    public void TheMarkerIsWrittenBeforeTheCardShows()
    {
        // A crash while the card is up must never re-show it: mark first, present second.
        var order = new List<string>();
        var (service, _, _) = Build(Record(SetupOutcome.RolledBack, previous: "1.4.0"), order);

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(order).HasSize(2);
        Check.That(order[0]).IsEqualTo("write");
        Check.That(order[1]).IsEqualTo("show");
    }

    [Fact]
    public void NoRecord_IsSilent()
    {
        var (service, store, shown) = Build(record: null);

        Check.That(service.CheckStartup()).IsFalse();
        Check.That(shown).IsEmpty();
        Check.That(store.Writes).IsZero();
    }

    [Fact]
    public void AnUnreadableRecord_IsSilent_AndKeepsTheAppRunning()
    {
        var store = new FakeStore { ThrowOnRead = true };
        var service = new SetupLastNoticeService(
            store, (key, _) => key, () => "✓ OK", (_, _) => throw new InvalidOperationException("must not show"));

        Check.That(service.CheckStartup()).IsFalse();
    }
}
