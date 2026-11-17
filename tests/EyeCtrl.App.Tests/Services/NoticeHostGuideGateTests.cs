namespace EyeCtrl.App.Tests.Services;

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Setup;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T11's reviewer-recommended guide pins (folded into Task 12's wiring, where the hosts gain
/// their <c>isGuideOpen</c> seam): the restart notices defer a show behind the guide (the newest
/// wins), the lag button hides and returns, and the setup-notice watch neither checks nor expires
/// behind the guide — its close pokes once. The guide's state is a mutable flag behind a
/// <c>() =&gt; guideOpen</c> predicate — the same shape the App wiring passes
/// (<c>() =&gt; _eventCoordinator.IsGuideOpen</c>).
/// </summary>
public class NoticeHostGuideGateTests
{
    private bool _guideOpen;
    private bool GuideOpen => _guideOpen;

    // ── RestartNoticeHost: a show during the guide is deferred, the newest replays on close ──

    private sealed record Shown(string Message, string CloseLabel, TimeSpan Hold);

    private sealed class FakeNoticePresenter : IRestartNoticePresenter
    {
        public List<Shown> Notices { get; } = [];
        public List<PhysicalPoint> Gaze { get; } = [];
        public int HideCalls { get; private set; }
        public bool IsShowing { get; set; }

        public void ShowNotice(string message, string closeLabel, TimeSpan hold)
        {
            Notices.Add(new Shown(message, closeLabel, hold));
            IsShowing = true;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds) => Gaze.Add(gaze);

        public void HideNotice()
        {
            HideCalls++;
            IsShowing = false;
        }
    }

    [Fact]
    public void TwoShowsWhileTheGuideIsOpen_ReplayOnlyTheNewest_WhenTheGuideCloses()
    {
        var messenger = new WeakReferenceMessenger();
        var presenter = new FakeNoticePresenter();
        using var host = new RestartNoticeHost(
            messenger, presenter,
            isSettingsOpen: () => false, isProbeOpen: () => false,
            targetLanguage: () => EyeCtrl.Core.Enums.UiLanguage.English,
            postToUi: work => work(),
            isGuideOpen: () => GuideOpen);
        _guideOpen = true;

        // Two notices arrive while the guide holds the screen: neither shows, both defer.
        messenger.Send(new RestartInProgressMessage());
        messenger.Send(new RestartUnavailableMessage("the refusal reason"));
        Check.That(presenter.Notices).IsEmpty();
        Check.That(presenter.HideCalls).IsEqualTo(0);

        // The guide closes: exactly ONE replay — the newest. An older notice is stale.
        _guideOpen = false;
        host.OnGuideOpenChanged(false);

        Check.That(presenter.Notices).HasSize(1);
        Check.That(presenter.Notices[0].Message).IsEqualTo("the refusal reason");

        // A second close poke does not replay anything again.
        host.OnGuideOpenChanged(false);
        Check.That(presenter.Notices).HasSize(1);
    }

    [Fact]
    public void TheGuideOpening_TakesAShowingNoticeDown()
    {
        var messenger = new WeakReferenceMessenger();
        var presenter = new FakeNoticePresenter();
        using var host = new RestartNoticeHost(
            messenger, presenter,
            isSettingsOpen: () => false, isProbeOpen: () => false,
            targetLanguage: () => EyeCtrl.Core.Enums.UiLanguage.English,
            postToUi: work => work(),
            isGuideOpen: () => GuideOpen);
        messenger.Send(new RestartInProgressMessage());
        Check.That(presenter.IsShowing).IsTrue();

        _guideOpen = true;
        host.OnGuideOpenChanged(true);

        Check.That(presenter.IsShowing).IsFalse();
        Check.That(presenter.HideCalls).IsEqualTo(1);
    }

    // ── SetupNoticeWatch: no check (no burn) and no expiry behind the guide; the close pokes ──

    private sealed class CountingStore : ISetupNoticeStore
    {
        public SetupResult? Record { get; set; }
        public int Reads { get; private set; }

        public string RecordPath => "";
        public SetupResult? TryReadRecord()
        {
            Reads++;
            return Record;
        }
        public DateTimeOffset? ReadShownTime() => null;
        public void WriteShownTime(DateTimeOffset time) { }
    }

    private static SetupResult Record(DateTimeOffset time) => new()
    {
        Verb = SetupVerb.Update,
        Outcome = SetupOutcome.Degraded,
        Version = "1.5.0",
        Time = time,
    };

    [Fact]
    public void WhileTheGuideIsOpen_TheWatchNeitherChecksNorExpires_TheClosePokeShows()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new CountingStore { Record = Record(start.AddSeconds(25)) };
        var now = start;
        var scheduled = new List<Action>();
        var shown = new List<string>();
        _guideOpen = true;

        var watch = new SetupNoticeWatch(
            new SetupLastNoticeService(
                store,
                (key, args) => args.Length == 0 ? key : key + "|" + string.Join(",", args),
                () => "✓ OK",
                (text, _) => shown.Add(text)),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(2),
            schedule: (_, tick) => scheduled.Add(tick),
            now: () => now,
            fileChange: _ => null,
            isGuideOpen: () => GuideOpen);

        // The immediate check skips behind the guide: no read, no burn, no show.
        watch.Start();
        Check.That(store.Reads).IsEqualTo(0);
        Check.That(shown).IsEmpty();
        Check.That(scheduled).HasSize(1);

        // Polls past the whole 90 s window: still no check and no expiry — the window must not
        // run out behind the guide.
        for (var i = 0; i < 50; i++)
        {
            now = now.AddSeconds(2);
            scheduled[^1]();
        }
        Check.That(store.Reads).IsEqualTo(0);
        Check.That(shown).IsEmpty();
        Check.That(watch.IsDone).IsFalse();

        // The guide closes: the poke checks once, shows this session's record and finishes.
        _guideOpen = false;
        watch.OnGuideOpenChanged(false);

        Check.That(store.Reads).IsEqualTo(1);
        Check.That(shown).HasSize(1);
        Check.That(watch.IsDone).IsTrue();
        Check.That(watch.NoticeShown).IsTrue();
    }

    // ── LagMarkHost: the button hides on open and returns on close only when allowed ──

    private sealed class FakeLagPresenter : ILagMarkPresenter
    {
        public int ShowCalls { get; private set; }
        public int HideCalls { get; private set; }
        public bool IsShowing { get; private set; }
        public List<(PhysicalPoint Gaze, long Timestamp)> Gaze { get; } = [];

        public event Action<long>? Marked;
        public event Action? Moved;

        public PhysicalRect HitBounds { get; set; } = new(3280, 1872, 512, 384);
        public System.Windows.Rect ShownRectDip { get; set; } = new(1640, 936, 256, 192);

        public void ShowButton()
        {
            ShowCalls++;
            IsShowing = true;
        }

        public void HideButton()
        {
            HideCalls++;
            IsShowing = false;
        }

        public void OnGaze(PhysicalPoint gaze, long timestampMicroseconds) => Gaze.Add((gaze, timestampMicroseconds));
    }

    private (LagMarkHost Host, FakeLagPresenter Presenter) BuildLagHost(bool instrumentOn = true)
    {
        var presenter = new FakeLagPresenter();
        var host = new LagMarkHost(
            new WeakReferenceMessenger(), presenter, new DispatchLatencyProbe(),
            isInstrumentOn: () => instrumentOn,
            isSettingsOpen: () => false, isProbeOpen: () => false,
            now: () => DateTime.Now,
            postToUi: work => work(),
            log: _ => { },
            isGuideOpen: () => GuideOpen);
        return (host, presenter);
    }

    [Fact]
    public void TheLagButton_HidesWhenTheGuideOpens_AndReturnsWhenItCloses()
    {
        _guideOpen = false;
        var (host, presenter) = BuildLagHost();

        _guideOpen = true;
        host.OnGuideOpenChanged(true);
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.ShowCalls).IsEqualTo(0);

        _guideOpen = false;
        host.OnGuideOpenChanged(false);
        Check.That(presenter.ShowCalls).IsEqualTo(1);   // the session fixture comes back
        Check.That(presenter.IsShowing).IsTrue();
    }

    [Fact]
    public void TheLagButton_CloseWithTheInstrumentOff_DoesNotBringItBack()
    {
        _guideOpen = true;
        var (host, presenter) = BuildLagHost(instrumentOn: false);
        Check.That(presenter.ShowCalls).IsEqualTo(0);

        host.OnGuideOpenChanged(true);
        Check.That(presenter.HideCalls).IsEqualTo(1);

        _guideOpen = false;
        host.OnGuideOpenChanged(false);
        Check.That(presenter.ShowCalls).IsEqualTo(0);   // CanShow is false: the gate holds
    }
}
