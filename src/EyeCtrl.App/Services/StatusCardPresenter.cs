namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Status;

/// <summary>
/// The status cards as a bottom-centre slot occupant (Gaze UX Wave B, B5). It listens to the B1
/// control status and, the FIRST time each of Paused, TrackingLost and NoTracker is entered in an
/// app session, ENQUEUES one card — so a card takes its FIFO turn with the drift notice and gives
/// way to a caregiver alert, and is never on screen outside the one banner slot.
///
/// <para>
/// "First" means the first card the owner could actually READ (B9 M1): a card withdrawn or dropped
/// before its hold completes is not used up, so a benign eye rest no longer spends TrackingLost for
/// the session and a later real outage still gets its card. That includes a QUEUED card replaced by
/// a newer first entry before it ever showed (C5 H2); only a completed hold spends the state.
/// </para>
///
/// <para>
/// A queued card may wait an unbounded time (a caregiver alert stays up as long as its condition
/// is sustained), so its turn RE-VALIDATES (the B4 lesson): a card whose state has already ended
/// is dropped, never shown late. When that check fails on a refresh of the LIVE card, the drop path
/// also hides the window (B4 review-02 L3) — raising Closed alone would leave a card on screen that
/// the slot no longer tracks.
/// </para>
///
/// <para>UI thread only, like the slot: the status's Changed is raised on the dispatcher.</para>
/// </summary>
internal sealed class StatusCardPresenter : IBannerSlotOccupant, IDisposable
{
    private readonly IBannerSlot _slot;
    private readonly StatusCardWindow _window;
    private readonly IControlStatus _status;
    private readonly HashSet<ControlState> _entered = new();
    private ControlState? _pending;
    private bool _holdCompleted;
    private readonly bool _noTrackerIsFallback;
    private bool _started;

    /// <summary>Wraps the card window for the slot.</summary>
    /// <param name="slot">The one bottom-centre slot the cards show through.</param>
    /// <param name="window">The card window (a DI singleton).</param>
    /// <param name="status">The B1 control status: its changes trigger cards, and its current
    /// value re-validates a card when the slot reaches it.</param>
    /// <param name="noTrackerIsFallback">Whether the mouse provider is a FALLBACK (the splash
    /// sentence's predicate, <see cref="SplashWindow.ShowsNoTrackerSentence"/>). The NoTracker card
    /// says "no eye tracker found", which is false when the mouse was chosen on purpose, so it
    /// shows only on a fallback (B5 review-01 L2).</param>
    public StatusCardPresenter(IBannerSlot slot, StatusCardWindow window, IControlStatus status, bool noTrackerIsFallback)
    {
        _slot = slot;
        _window = window;
        _status = status;
        _noTrackerIsFallback = noTrackerIsFallback;
        _window.Hidden += OnHidden;
    }

    /// <inheritdoc />
    public event EventHandler? Closed;

    /// <summary>The state whose card is queued or live, or null before the first card.</summary>
    internal ControlState? PendingState => _pending;

    /// <summary>
    /// Begins listening. Idempotent. The current status counts as an entry, so a session that starts
    /// paused or without a tracker gets its card; the status service's own initial publish then finds
    /// the state already entered.
    /// </summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _status.Changed += OnStatusChanged;
        OnStatusChanged(_status.Current);
    }

    /// <summary>Stops listening.</summary>
    public void Dispose()
    {
        _status.Changed -= OnStatusChanged;
        _window.Hidden -= OnHidden;
    }

    /// <summary>Whether a state has a card at all.</summary>
    internal static bool HasCard(ControlState state) =>
        state is ControlState.Paused or ControlState.TrackingLost or ControlState.NoTracker;

    /// <summary>
    /// A status change. A LIVE card whose state has just been left is withdrawn at once (B5
    /// review-01 M1): the card explains a state, and must not stay up for the rest of its hold
    /// after the state ended. Then the first entry into a carded state this session queues its card.
    /// </summary>
    internal void OnStatusChanged(ControlStatus status)
    {
        if (_pending is { } pending && status.State != pending && _slot.IsLive(this))
        {
            // B9 M1: the card leaves before its hold completes, so the state is NOT used up: the
            // owner never got the read (a pause rest withdraws Paused, and TrackingLost leaves on
            // the first valid sample). "First entry" means first COMPLETED card.
            _entered.Remove(pending);
            _slot.Withdraw(this); // an immediate hide; a merely queued card is dropped by CanShow instead
        }

        if (!HasCard(status.State) || !_entered.Add(status.State))
            return; // no card for this state, or it was already entered this session
        if (status.State == ControlState.NoTracker && !_noTrackerIsFallback)
            return; // the mouse was chosen on purpose: "no eye tracker found" would be false

        if (_pending is { } replaced && replaced != status.State && !_holdCompleted)
        {
            // C5 H2: the replaced card was QUEUED (a live one was withdrawn above) and never
            // completed its hold, so it was never read: it must not stay used up. The overwrite
            // case is another carded first entry (Paused waiting behind the drift notice, then
            // TrackingLost) — without this, Paused would get no card all session.
            _entered.Remove(replaced);
        }

        _pending = status.State;
        _holdCompleted = false;
        _slot.Enqueue(this); // a live card is refreshed with the new state; a queued one keeps its place
    }

    /// <summary>Whether the pending card may still be shown: its state is still the current one.</summary>
    internal bool CanShow => _pending is { } pending && _status.Current.State == pending;

    /// <inheritdoc />
    public void ShowInSlot()
    {
        if (!CanShow)
        {
            // B9 M1: the card is dropped before its hold completes (the state ended while it
            // queued, or a live refresh finds it stale), so the state is NOT used up. This covers
            // both branches below: a queued card that was never read, and a live one taken down.
            if (_pending is { } unheld)
                _entered.Remove(unheld);
            if (_window.IsVisible)
            {
                // L3: a refresh of the LIVE card whose state has ended. HideCard raises Hidden,
                // which raises Closed exactly once, so the slot moves on AND nothing stays up.
                _window.HideCard();
                return;
            }
            // A queued card whose state ended: drop it; Closed lets the slot's loop move on.
            Closed?.Invoke(this, EventArgs.Empty);
            return;
        }
        _window.ShowCard(_pending!.Value);
    }

    /// <inheritdoc />
    public void HideFromSlot() => _window.HideCard();

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds) => _window.TryGetVisibleBounds(out bounds);

    private void OnHidden(object? sender, EventArgs e)
    {
        // Only the card's OWN hold/fade completion spends the state; an immediate hide (withdrawal,
        // pre-emption, a stale drop) leaves it unread and therefore not used up (C5 H2).
        if (_window.HoldCompleted)
            _holdCompleted = true;
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
