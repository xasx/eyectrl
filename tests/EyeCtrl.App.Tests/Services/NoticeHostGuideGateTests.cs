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
///
/// <para>
/// The Backstage menu's seam (<c>isBackstageOpen</c>) is pinned here too: it hides like Settings —
/// no replay on close, because the Backstage surface shows its own notices — while the lag button,
/// a session fixture, comes back.
/// </para>
/// </summary>
public class NoticeHostGuideGateTests
{
    private bool _guideOpen;
    private bool GuideOpen => _guideOpen;

    private bool _backstageOpen;
    private bool BackstageOpen => _backstageOpen;

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

    [Fact]
    public void TheGuideClosePoke_BeforeStart_IsANoOp_AndTheDeferredStartThenShowsTheOldRecord()
    {
        // The App subscribes GuideOpenChanged at construction but defers the watch's Start into
        // the guide-closed handler (S2-1) — so on the startup-guide close the close poke lands
        // on a NOT-YET-STARTED watch. There it must read nothing, show nothing and not finish
        // (an unstarted _startedAt would misread the old record as this session's and end the
        // watch before it began); Start's own immediate check then handles the record. The
        // record here is a PREVIOUS session's (its time is before this session's start), which
        // the immediate check shows without ending the watch (review 02 R5).
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var store = new CountingStore { Record = Record(start.AddMinutes(-5)) };
        var now = start;
        var scheduled = new List<Action>();
        var shown = new List<string>();

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

        // Production order on the startup-guide close: the open edge first, then the close
        // poke — both before the deferred Start, so both must leave the watch untouched.
        watch.OnGuideOpenChanged(true);
        watch.OnGuideOpenChanged(false);
        Check.That(store.Reads).IsEqualTo(0);
        Check.That(shown).IsEmpty();
        Check.That(watch.NoticeShown).IsFalse();
        Check.That(watch.IsDone).IsFalse();

        // The deferred Start: the immediate check shows the old record, and the watch stays
        // alive — not done, with its poll chain armed.
        watch.Start();
        Check.That(store.Reads).IsEqualTo(1);
        Check.That(shown).HasSize(1);
        Check.That(watch.NoticeShown).IsTrue();
        Check.That(watch.IsDone).IsFalse();
        Check.That(scheduled).HasSize(1);

        // The watch is live past all of it: a later file-change poke still reaches it (a read),
        // and it still runs.
        watch.Poke();
        Check.That(store.Reads).IsEqualTo(2);
        Check.That(watch.IsDone).IsFalse();
    }

    // ── the fullscreen Backstage menu owns the screen: the card stays down, the lag button returns ──

    [Fact]
    public void TheRestartCard_StaysDown_WhileBackstageOwnsTheScreen_AndIsTakenDownWhenItOpens()
    {
        var messenger = new WeakReferenceMessenger();
        var presenter = new FakeNoticePresenter();
        using var host = new RestartNoticeHost(
            messenger, presenter,
            isSettingsOpen: () => false, isProbeOpen: () => false,
            targetLanguage: () => EyeCtrl.Core.Enums.UiLanguage.English,
            postToUi: work => work(),
            isBackstageOpen: () => BackstageOpen);

        // No show while the menu holds the screen — the menu shows its own restart notices.
        _backstageOpen = true;
        messenger.Send(new RestartUnavailableMessage("the refusal"));
        Check.That(presenter.Notices).IsEmpty();

        // A card that is up when the menu opens is taken down, and closing does NOT replay it:
        // the Backstage surface owns the notice for that visit (the Settings precedent).
        _backstageOpen = false;
        messenger.Send(new RestartUnavailableMessage("the refusal"));
        Check.That(presenter.Notices).HasSize(1);
        _backstageOpen = true;
        host.OnBackstageOpenChanged(true);
        Check.That(presenter.IsShowing).IsFalse();
        _backstageOpen = false;
        host.OnBackstageOpenChanged(false);
        Check.WithCustomMessage("closing Backstage replayed a notice the menu already owned.")
            .That(presenter.Notices).HasSize(1);
    }

    [Fact]
    public void TheLagButton_HidesWhileBackstageOwnsTheScreen_AndReturnsWhenItCloses()
    {
        _backstageOpen = false;
        var (host, presenter) = BuildLagHost();
        using var _ = host;

        _backstageOpen = true;
        host.OnBackstageOpenChanged(true);
        Check.That(presenter.HideCalls).IsEqualTo(1);
        Check.That(presenter.ShowCalls).IsEqualTo(0);

        // F1 (review): CanShow() must still see Backstage when ANOTHER surface closes underneath
        // it. This is the real re-show path — an instrumented session with the menu open and
        // Settings/probe/guide closing behind it. Without the backstage term CanShow() returns
        // true here and the button re-shows OVER the fullscreen menu with a live dwell target.
        // Mutation-checked: drop the term and this assertion goes red.
        host.OnSettingsOpenChanged(false);
        Check.WithCustomMessage("a Settings close underneath Backstage re-showed the lag button.")
            .That(presenter.ShowCalls).IsEqualTo(0);

        _backstageOpen = false;
        host.OnBackstageOpenChanged(false);
        Check.That(presenter.ShowCalls).IsEqualTo(1);   // the session fixture comes back
        Check.That(presenter.IsShowing).IsTrue();
    }

    [Fact]
    public void TheRestartCard_EatsNoGaze_WhileBackstageOwnsTheScreen()
    {
        // F2 (review): SetupNoticeWatch shows through the presenter DIRECTLY
        // (App.xaml.cs's setup-notice path), bypassing the host's ScreenOwnedElsewhere() guard, so
        // a card really can be up over the menu while it polls for up to 90 s. Its invisible close
        // button must not accumulate dwell there. Mutation-checked: drop the backstage term from
        // the gaze guard and the first assertion goes red.
        var messenger = new WeakReferenceMessenger();
        var presenter = new FakeNoticePresenter();
        using var host = new RestartNoticeHost(
            messenger, presenter,
            isSettingsOpen: () => false, isProbeOpen: () => false,
            targetLanguage: () => EyeCtrl.Core.Enums.UiLanguage.English,
            postToUi: work => work(),
            isBackstageOpen: () => BackstageOpen);

        presenter.ShowNotice("the setup notice", "✓ OK", RestartNoticeHost.RefusedHold);

        _backstageOpen = true;
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 1_000));
        Check.WithCustomMessage("gaze was fed to a card covered by the Backstage menu.")
            .That(presenter.Gaze).IsEmpty();

        _backstageOpen = false;
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 2_000));
        Check.That(presenter.Gaze).HasSize(1);
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
            isGuideOpen: () => GuideOpen,
            isBackstageOpen: () => BackstageOpen);
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
