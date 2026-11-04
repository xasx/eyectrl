namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Setup;

/// <summary>
/// C9 S6f's one-time setup-last notice: the outcome routing (rolled back / degraded / the failed
/// family; the deliberate silent set), "exactly once" keyed by the record's <c>time</c> against
/// <c>setup-notice-shown.json</c> (the coordinator's ruling — setup-last.json is never touched),
/// and the crash-safety order: the marker is written BEFORE the card shows.
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
        SetupOutcome outcome, string? version = "9.9.9", DateTimeOffset? time = null) =>
        new()
        {
            Verb = SetupVerb.Update,
            Outcome = outcome,
            Version = version,
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
    public void RolledBack_ShowsTheRolledBackNotice_WithTheRestoredVersion()
    {
        var (service, store, shown) = Build(Record(SetupOutcome.RolledBack, "1.4.0"));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown).HasSize(1);
        Check.That(shown[0].Text).IsEqualTo("update.notice.rolledBack|1.4.0");
        Check.That(shown[0].CloseLabel).IsEqualTo("✓ OK");
        Check.That(store.Shown).IsEqualTo(RecordTime);
    }

    [Fact]
    public void Degraded_ShowsTheQ12DegradedNotice()
    {
        var (service, _, shown) = Build(Record(SetupOutcome.Degraded, "3.1.0"));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown.Single().Text).IsEqualTo("update.notice.degraded|3.1.0");
    }

    [Theory]
    [InlineData(SetupOutcome.AllFailed)]
    [InlineData(SetupOutcome.VerifyFailed)]
    [InlineData(SetupOutcome.NotApplied)]
    [InlineData(SetupOutcome.Failed)]
    public void TheFailedFamily_SharesTheFailedNotice(SetupOutcome outcome)
    {
        var (service, _, shown) = Build(Record(outcome, "2.0.0"));

        Check.That(service.CheckStartup()).IsTrue();

        Check.That(shown.Single().Text).IsEqualTo("update.notice.failed|2.0.0");
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
        var (service, store, shown) = Build(Record(SetupOutcome.RolledBack, "1.4.0"));

        Check.That(service.CheckStartup()).IsTrue();
        Check.That(service.CheckStartup()).IsFalse();

        Check.That(shown).HasSize(1);
        Check.That(store.Writes).IsEqualTo(1);
    }

    [Fact]
    public void ANewerRecord_ShowsAgain()
    {
        var record = Record(SetupOutcome.RolledBack, "1.4.0", RecordTime.AddHours(1));
        var (service, _, shown) = Build(record);
        // The marker already records an older record's time.
        service.CheckStartup();      // shows once, marks this time
        var secondRun = Build(Record(SetupOutcome.Degraded, "1.5.0", RecordTime.AddHours(2)));
        Check.That(secondRun.Service.CheckStartup()).IsTrue();

        Check.That(shown).HasSize(1);   // first build's card
        Check.That(secondRun.Shown).HasSize(1);
    }

    [Fact]
    public void AnAlreadyShownRecord_IsSilent_WithoutWritingAgain()
    {
        var record = Record(SetupOutcome.RolledBack, "1.4.0");
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
        var (service, _, _) = Build(Record(SetupOutcome.RolledBack), order);

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
