namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Status;

/// <summary>
/// B5's status cards: one card on the FIRST entry into Paused, TrackingLost and NoTracker per
/// session; a queued card whose state ended is dropped when its turn comes; the drop on a LIVE
/// refresh hides the window (B4 review-02 L3); cards and drift notices take FIFO turns.
///
/// <para>
/// Every card window here is prepared with Opacity 0 AND a DWM cloak, so even a mutant that writes
/// the window's own Opacity paints nothing; the card is click-through by construction. The routing
/// tests use a counting slot that never calls ShowInSlot.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class StatusCardPresenterTests
{
    internal sealed class FakeStatus : IControlStatus
    {
        public ControlStatus Current { get; private set; }

        public event Action<ControlStatus>? Changed;

        /// <summary>Changes the state and raises Changed, as the live service does.</summary>
        public void Enter(ControlState state)
        {
            Current = new ControlStatus(state, Disconnected: false);
            Changed?.Invoke(Current);
        }

        /// <summary>Changes the current state without a Changed (a change still in flight).</summary>
        public void SetSilently(ControlState state) => Current = new ControlStatus(state, Disconnected: false);
    }

    /// <summary>Counts enqueues, never shows anything.</summary>
    private sealed class CountingSlot : IBannerSlot
    {
        public List<IBannerSlotOccupant> Enqueued { get; } = new();
        public List<IBannerSlotOccupant> Preempted { get; } = new();
        public void Enqueue(IBannerSlotOccupant banner) => Enqueued.Add(banner);
        public void Preempt(IBannerSlotOccupant banner) => Preempted.Add(banner);
        public void Withdraw(IBannerSlotOccupant banner) { }
        public bool IsLive(IBannerSlotOccupant banner) => false;
        public bool TryGetVisibleBounds(out PhysicalRect bounds) { bounds = default; return false; }
        public IReadOnlyList<PhysicalRect> LiveBoundsOrEmpty() => Array.Empty<PhysicalRect>();
    }

    /// <summary>A pre-emptor (the caregiver alert's role) that is "on screen" until it closes.</summary>
    private sealed class FakeAlert : IBannerSlotOccupant
    {
        public event EventHandler? Closed;
        public void ShowInSlot() { }
        public void HideFromSlot() => Closed?.Invoke(this, EventArgs.Empty);
        public void Close() => Closed?.Invoke(this, EventArgs.Empty);
        public bool TryGetVisibleBounds(out PhysicalRect bounds) { bounds = new PhysicalRect(0, 0, 10, 10); return true; }
    }

    /// <summary>A card window that paints nothing whatever its own code does: Opacity 0 and cloaked.</summary>
    internal static StatusCardWindow HiddenCardWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        return InvisibleTestWindow.PrepareCloaked(InvisibleTestWindow.Prepare(new StatusCardWindow()));
    }

    [StaFact]
    public void TheFirstEntryIntoEachState_QueuesOneCard_AndARepeatQueuesNone()
    {
        var window = HiddenCardWindow();
        var slot = new CountingSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        try
        {
            presenter.Start(); // Normal: no card

            Check.That(slot.Enqueued).IsEmpty();

            status.Enter(ControlState.Paused);
            status.Enter(ControlState.Normal);
            status.Enter(ControlState.Paused); // a repeat in the same session
            Check.WithCustomMessage("Paused must queue exactly one card per session")
                .That(slot.Enqueued.Count).IsEqualTo(1);
            Check.That(presenter.PendingState).IsEqualTo(ControlState.Paused);

            status.Enter(ControlState.TrackingLost);
            status.Enter(ControlState.Normal);
            status.Enter(ControlState.TrackingLost);
            Check.WithCustomMessage("TrackingLost must queue exactly one card per session")
                .That(slot.Enqueued.Count).IsEqualTo(2);

            status.Enter(ControlState.NoTracker);
            status.Enter(ControlState.Normal);
            status.Enter(ControlState.NoTracker);
            Check.WithCustomMessage("NoTracker must queue exactly one card per session")
                .That(slot.Enqueued.Count).IsEqualTo(3);
            Check.That(presenter.PendingState).IsEqualTo(ControlState.NoTracker);

            status.Enter(ControlState.Blocked);
            status.Enter(ControlState.Normal);
            Check.WithCustomMessage("Normal and Blocked have no card (Blocked's card waits for Blocked to exist)")
                .That(slot.Enqueued.Count).IsEqualTo(3);
            Check.WithCustomMessage("a card goes through the FIFO, never the pre-empt")
                .That(slot.Preempted).IsEmpty();
            Check.That(window.IsVisible).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheStateTheSessionStartsIn_GetsItsCard_Once()
    {
        var window = HiddenCardWindow();
        var slot = new CountingSlot();
        var status = new FakeStatus();
        status.SetSilently(ControlState.NoTracker);
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        try
        {
            presenter.Start();

            // Review-01 L5: Start() itself counts the current state — a synchronous status hook would
            // never re-deliver it, so the card must already be queued here.
            Check.WithCustomMessage("Start() must queue the card for the state the session starts in")
                .That(slot.Enqueued.Count).IsEqualTo(1);

            status.Enter(ControlState.NoTracker); // the status service's own initial publish

            Check.WithCustomMessage("a session that starts without a tracker gets ONE NoTracker card")
                .That(slot.Enqueued.Count).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    // Review-01 L2: "No eye tracker found" is false when the mouse was chosen on purpose.
    [StaTheory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void TheNoTrackerCard_ShowsOnlyOnAFallback(bool noTrackerIsFallback, int cards)
    {
        var window = HiddenCardWindow();
        var slot = new CountingSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback);
        try
        {
            presenter.Start();
            status.Enter(ControlState.NoTracker);

            Check.That(slot.Enqueued.Count).IsEqualTo(cards);

            status.Enter(ControlState.Paused); // the other cards are unaffected
            Check.That(slot.Enqueued.Count).IsEqualTo(cards + 1);
        }
        finally { window.Close(); }
    }

    // Review-01 M1: the owner pauses, then resumes 2 s later — the Paused card must leave AT ONCE,
    // not stay up for the rest of its 8 s hold while gaze control is live again.
    [StaFact]
    public void ALiveCard_LeavesAtOnce_WhenItsStateEnds()
    {
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            presenter.Start();
            status.Enter(ControlState.Paused);
            Check.That(window.IsVisible).IsTrue();
            Check.That(slot.IsLive(presenter)).IsTrue();

            status.Enter(ControlState.Normal); // resumed (2 s later; the card's 8 s hold is still running)

            Check.WithCustomMessage("the Paused card must leave the screen when the pause ends")
                .That(window.IsVisible).IsFalse();
            Check.WithCustomMessage("the slot must be free again").That(slot.Live).IsNull();
            Check.That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ALiveCard_GivesWayToTheNextStatesCard()
    {
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        try
        {
            presenter.Start();
            status.Enter(ControlState.Paused);
            status.Enter(ControlState.TrackingLost); // first entry: its own card replaces the Paused one

            Check.That(window.IsVisible).IsTrue();
            Check.That(slot.IsLive(presenter)).IsTrue();
            Check.That(window.Message.Text).IsEqualTo(
                StatusCardWindow.MessageFor(EyeCtrl.Core.Localization.StringTable.Current, ControlState.TrackingLost));

            status.Enter(ControlState.Paused); // a repeat: no card, and the TrackingLost card leaves
            Check.That(window.IsVisible).IsFalse();
            Check.That(slot.Live).IsNull();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ACardQueuedBehindAnAlert_WhoseStateEnded_IsDropped()
    {
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        var alert = new FakeAlert();
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            presenter.Start();
            slot.Preempt(alert);
            status.Enter(ControlState.Paused); // queued behind the alert
            Check.That(window.IsVisible).IsFalse();

            status.Enter(ControlState.Normal); // the pause ends while the alert is still up
            alert.Close();                     // the slot reaches the card now

            Check.WithCustomMessage("a card whose state already ended must be dropped, not shown late")
                .That(window.IsVisible).IsFalse();
            Check.That(slot.Live).IsNull();
            Check.That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void ACardQueuedBehindAnAlert_WhoseStateHolds_ShowsWhenTheAlertCloses()
    {
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        var alert = new FakeAlert();
        try
        {
            presenter.Start();
            slot.Preempt(alert);
            status.Enter(ControlState.TrackingLost);
            Check.That(window.IsVisible).IsFalse();

            alert.Close();

            Check.WithCustomMessage("the positive control: a still-current card shows on its turn")
                .That(window.IsVisible).IsTrue();
            Check.That(slot.IsLive(presenter)).IsTrue();
            Check.That(window.Message.Text).IsEqualTo(
                StatusCardWindow.MessageFor(EyeCtrl.Core.Localization.StringTable.Current, ControlState.TrackingLost));
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void AFailedRevalidation_OnALiveRefresh_HidesTheWindow()
    {
        // B4 review-02 L3: the slot refreshes its live occupant by calling ShowInSlot again. If the
        // card's state has ended by then, raising Closed alone would free the slot with the card
        // still on screen and no longer tracked by anything.
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            presenter.Start();
            status.Enter(ControlState.Paused);
            Check.That(window.IsVisible).IsTrue();
            Check.That(slot.IsLive(presenter)).IsTrue();

            status.SetSilently(ControlState.Normal);
            slot.Enqueue(presenter); // a refresh of the live card

            Check.WithCustomMessage("the drop path on a live refresh must take the card off the screen")
                .That(window.IsVisible).IsFalse();
            Check.WithCustomMessage("the slot must be free again").That(slot.Live).IsNull();
            Check.WithCustomMessage("Closed exactly once").That(closed).IsEqualTo(1);
            Check.That(window.TryGetVisibleBounds(out _)).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void CardsAndDriftNotices_TakeFifoTurns_NeverOverlapping()
    {
        var card = HiddenCardWindow();
        var drift = InvisibleTestWindow.PrepareCloaked(InvisibleTestWindow.Prepare(new DriftNoticeWindow()));
        var slot = new BannerSlot();
        var status = new FakeStatus();
        var driftPresenter = new SlotDriftNoticePresenter(slot, drift, canShow: static () => true);
        using var presenter = new StatusCardPresenter(slot, card, status, noTrackerIsFallback: true);
        try
        {
            presenter.Start();
            driftPresenter.ShowDriftNotice();
            status.Enter(ControlState.Paused);

            Check.WithCustomMessage("the card waits for the live drift notice")
                .That(card.IsVisible).IsFalse();
            Check.That(drift.IsVisible).IsTrue();

            driftPresenter.ShowDriftNotice(); // a refresh of the live drift notice, not a queue jump
            drift.HideDriftNotice();          // the drift notice's own hold/fade ends

            Check.WithCustomMessage("the card takes the next turn").That(card.IsVisible).IsTrue();
            Check.That(drift.IsVisible).IsFalse();

            driftPresenter.ShowDriftNotice(); // a later drift notice queues behind the card
            Check.That(drift.IsVisible).IsFalse();

            card.OnFadeCompleted(null, EventArgs.Empty);

            Check.That(card.IsVisible).IsFalse();
            Check.WithCustomMessage("the drift notice follows the card").That(drift.IsVisible).IsTrue();
        }
        finally
        {
            card.Close();
            drift.Close();
        }
    }

    // ── census terms (b) and (c), agreed with the sticky owner ──

    [StaFact]
    public void HideFromSlot_IsAnImmediateHide_ThatRaisesClosed()
    {
        var window = HiddenCardWindow();
        var slot = new BannerSlot();
        var status = new FakeStatus();
        using var presenter = new StatusCardPresenter(slot, window, status, noTrackerIsFallback: true);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            presenter.Start();
            status.Enter(ControlState.Paused);
            Check.That(window.IsVisible).IsTrue();

            presenter.HideFromSlot();

            Check.That(window.IsVisible).IsFalse();
            Check.That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheVisibleBounds_HoldForAsLongAsTheCardIsVisible_FadeTailIncluded()
    {
        var window = HiddenCardWindow();
        try
        {
            Check.That(window.TryGetVisibleBounds(out _)).IsFalse();

            window.ShowCard(ControlState.Paused);
            window.Banner.Opacity = 0.05; // deep in the fade tail, still on screen

            Check.WithCustomMessage("the fade tail still occludes").That(window.TryGetVisibleBounds(out var bounds)).IsTrue();
            Check.That(bounds.Width).IsStrictlyGreaterThan(0);

            window.OnFadeCompleted(null, EventArgs.Empty);
            Check.That(window.TryGetVisibleBounds(out _)).IsFalse();
        }
        finally { window.Close(); }
    }

    [StaFact]
    public void TheCardWindowsFadeCompletion_ReachesTheSlotAsClosed()
    {
        var window = HiddenCardWindow();
        using var presenter = new StatusCardPresenter(new BannerSlot(), window, new FakeStatus(), noTrackerIsFallback: true);
        var closed = 0;
        presenter.Closed += (_, _) => closed++;
        try
        {
            window.OnFadeCompleted(null, EventArgs.Empty);
            Check.That(closed).IsEqualTo(1);
        }
        finally { window.Close(); }
    }

    // ── census term (a): the window is reachable ONLY through the slot ──

    [Fact]
    public void TheCardWindow_IsReachableOnlyThroughTheSlot()
    {
        Check.WithCustomMessage("the window must not be a slot occupant itself (the presenter is)")
            .That(typeof(IBannerSlotOccupant).IsAssignableFrom(typeof(StatusCardWindow))).IsFalse();

        var boot = Source("src/EyeCtrl.App/Services/AppBootstrapper.cs");
        Check.WithCustomMessage("the card presenter is no longer built over the slot in the composition root")
            .That(Regex.IsMatch(boot,
                @"new\s+StatusCardPresenter\s*\(\s*sp\s*\.\s*GetRequiredService<IBannerSlot>\(\)\s*,\s*sp\s*\.\s*GetRequiredService<StatusCardWindow>\(\)")).IsTrue();

        // Every reference to the window type, and every call of its showing API, across src/: the
        // window's own files, the presenter, and the one registration that hands it to the presenter.
        var src = Path.Combine(SourceScanner.RepoRoot().FullName, "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        string Rel(string f) => f[(src.Length + 1)..].Replace('\\', '/');

        // Review-01 L4: the strictest form — no other source file even NAMES the window type, so no
        // DI-built class can take it as a constructor parameter and show it outside the slot.
        var mentions = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bStatusCardWindow\b")).Select(Rel)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        Check.WithCustomMessage("a source file outside the window, its presenter and the composition root names StatusCardWindow: "
                                + string.Join(", ", mentions))
            .That(mentions).ContainsExactly(
                "EyeCtrl.App/Overlays/StatusCardWindow.xaml.cs",
                "EyeCtrl.App/Services/AppBootstrapper.cs",
                "EyeCtrl.App/Services/StatusCardPresenter.cs");

        var showers = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"\.\s*ShowCard\s*\(")).Select(Rel).ToList();
        Check.WithCustomMessage("ShowCard is called outside the slot presenter: " + string.Join(", ", showers))
            .That(showers).ContainsExactly("EyeCtrl.App/Services/StatusCardPresenter.cs");

        var resolvers = files.Where(f => Regex.IsMatch(File.ReadAllText(f), @"GetRequiredService<StatusCardWindow>|GetService<StatusCardWindow>"))
            .Select(Rel).ToList();
        Check.WithCustomMessage("the card window is resolved outside the presenter registration: " + string.Join(", ", resolvers))
            .That(resolvers).ContainsExactly("EyeCtrl.App/Services/AppBootstrapper.cs");
        Check.WithCustomMessage("the card window is resolved more than once in the composition root")
            .That(Regex.Matches(boot, @"GetRequiredService<StatusCardWindow>").Count).IsEqualTo(1);
    }

    [Fact]
    public void TheApp_StartsTheCards_AfterTheControlStatus_AndNotInTestMode()
    {
        var app = Source("src/EyeCtrl.App/App.xaml.cs");
        var status = app.IndexOf("GetRequiredService<ControlStatusService>().Start()", StringComparison.Ordinal);
        var cards = app.IndexOf("GetRequiredService<StatusCardPresenter>().Start()", StringComparison.Ordinal);
        Check.WithCustomMessage("App.OnStartup no longer starts the status cards").That(cards).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the cards must start after the control status").That(cards).IsStrictlyGreaterThan(status);
        Check.That(Regex.IsMatch(app,
            @"if\s*\(\s*!IsTestMode\s*\)\s*_serviceProvider\s*\.\s*GetRequiredService<StatusCardPresenter>\(\)\s*\.\s*Start\(\)")).IsTrue();
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot().FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
}
