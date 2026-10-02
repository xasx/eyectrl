namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

/// <summary>
/// What a button-dwell completion did, returned by
/// <see cref="InteractionManager.OnActivationButtonDwelled(ActivationOffer)"/> so the caller can log
/// the truth rather than assume an activation happened (ticket 8 fixup 02, review B).
/// </summary>
public enum ActivationCompletionResult
{
    /// <summary>The named offer was live and the state allowed it: the activation proceeded.</summary>
    Accepted,

    /// <summary>The named offer was live but the state was wrong (a Pause landed during the resolve,
    /// for example): the named offer was declined.</summary>
    Declined,

    /// <summary>The named offer was already gone: nothing happened.</summary>
    Stale,
}

/// <summary>
/// Manages the interaction state machine for gaze-based interactions.
/// All point coordinates are in physical screen pixels.
/// </summary>
public class InteractionManager
{
    /// <summary>
    /// Current state of the interaction state machine.
    /// <para>
    /// Written from the dispatcher thread (<see cref="TogglePause"/>, <see cref="OnActionSelected"/>) as
    /// well as from the gaze thread, and read from both. A reader that needs more than one decision from
    /// the state must snapshot it ONCE into a local and decide from that snapshot — re-reading per
    /// decision lets a write land between the reads and makes a single unit of work take mutually
    /// inconsistent decisions (see <c>GazeEngine.OnGazePoint</c>).
    /// </para>
    /// </summary>
    public InteractionState CurrentState { get; private set; } = InteractionState.FreeLook;
    /// <summary>Active selection mode that drives the activation-button branch.</summary>
    public SelectionMode SelectionMode { get; set; } = SelectionMode.None;
    /// <summary>Initial fixation point in physical screen pixels.</summary>
    public PhysicalPoint? FixationPoint { get; private set; }
    /// <summary>Precise ManualSelection dwell point in physical screen pixels.</summary>
    public PhysicalPoint? ManualSelectionDwellPoint { get; private set; }
    /// <summary>
    /// Control resolved for Auto Selection at the current fixation, or null when none was found.
    /// Set externally (by the platform adapter) before <see cref="OnActivationButtonDwelled(ActivationOffer)"/>,
    /// and cleared in <see cref="ResetToFreeLook"/> to prevent stale reuse.
    /// </summary>
    public ControlInfo? AutoSelectionTarget { get; set; }
    /// <summary>Anchor point for the active gaze-scroll session, or null when not scrolling.</summary>
    public PhysicalPoint? ScrollAnchor { get; private set; }
    /// <summary>The action locked for the active repeat session, or null outside <see cref="InteractionState.RepeatMode"/>.</summary>
    public InteractionAction? RepeatAction { get; private set; }

    /// <summary>Raised when the interaction state changes.</summary>
    public event Action<InteractionState>? StateChanged;
    /// <summary>
    /// Raised when a fixation onset is accepted — i.e. exactly when the state machine enters
    /// <see cref="InteractionState.Fixation"/> from FreeLook. This is the explicit onset signal the
    /// replay parity observes; the activation button's show/hide are display events and stop coinciding
    /// with the detector's onset/end once the button's lifetime is decoupled (ticket 7, critique B3).
    /// Carries the onset point (the running centroid at onset, in physical pixels).
    /// </summary>
    public event Action<PhysicalPoint>? FixationAccepted;
    /// <summary>
    /// Raised when the <see cref="InteractionState.Fixation"/> state ends, whatever the reason
    /// (gaze left, the eyes were lost, a pause, a keyboard toggle, or an accepted activation). Raised
    /// before <see cref="StateChanged"/> for the same transition.
    /// </summary>
    public event Action? FixationEnded;
    /// <summary>
    /// Raised to pin the ring at a point (physical pixels), always while the state is
    /// <see cref="InteractionState.Fixation"/>. Two sources: an activation offer appearing (its P —
    /// created or replaced by a settle, or a pending re-offer taking over a run that still holds), and
    /// an accepted settle that made no offer at all (chrome, an unplaceable side, no engine yet),
    /// deliberately including settles over EyeCtrl's own controls: the ring says "you're holding".
    /// A settle that left a live offer as it was (on the button, a near miss, swallowed, locked) does
    /// NOT raise it: while an offer lives the ring belongs to its P (live check 2026-10-02). It is
    /// therefore not a settle signal; <see cref="FixationSettled"/> is.
    /// </summary>
    public event Action<PhysicalPoint>? PinRing;
    /// <summary>
    /// Raised when the ring goes back to following the gaze. On an exit from
    /// <see cref="InteractionState.Fixation"/> — keyed on the state transition, not the detector's end
    /// event: the keyboard toggle calls <see cref="OnFixationEnded"/> directly after setting
    /// <c>GazeEngine.SuppressFixation</c>, so the detector never raises an end (re-check N2) — EXCEPT an
    /// exit to FreeLook while the ring is pinned at a live offer's P: that pin is released by the
    /// offer's close instead, or by an offer closing while a later, unpinned run is forming.
    /// </summary>
    public event Action? ReleaseRing;
    /// <summary>
    /// Raised on every accepted settle (state <see cref="InteractionState.Fixation"/>), carrying the
    /// running centroid at settle, before the offer engine classifies it. The replay's settle parity
    /// observes this; <see cref="PinRing"/> stopped being one-per-settle when the ring began following
    /// the offer.
    /// </summary>
    public event Action<PhysicalPoint>? FixationSettled;
    /// <summary>Raised when the user selects an action (click, scroll, etc.) at a specific point. Modifiers are opaque payload from the action menu's latch; repeat ticks carry null.</summary>
    public event Action<InteractionAction, PhysicalPoint, IReadOnlySet<ModifierKey>?>? ActionRequested;

    /// <summary>
    /// Raised when a live activation offer appears or moves: show the button at
    /// <see cref="ActivationOffer.Button"/>, sized for <see cref="ActivationOffer.HitRadiusPx"/>. The
    /// button's lifetime follows the OFFER since ticket 8, never the fixation's onset or end: the
    /// fixation can end while the button lingers, and a settle elsewhere replaces it (critique B1).
    /// </summary>
    public event Action<ActivationOffer>? ActivationOfferShown;
    /// <summary>Raised when the live offer goes: the linger expired, a teardown cleared it, or it was
    /// accepted or declined. Hide the button.</summary>
    public event Action? ActivationOfferClosed;
    /// <summary>Raised once per offer when its button dwell completes, carrying the fired offer and the
    /// sample timestamp. The completion path (ticket 8) resolves at the offer's P and accepts or
    /// declines by its id.</summary>
    public event Action<ActivationOffer, long>? ActivationOfferFired;
    /// <summary>Raised when an offer expired unused: the button-dwell telemetry source for offers the
    /// user looked at but never completed. Carries the dwell fraction reached.</summary>
    public event Action<ActivationOffer, long, double>? ActivationOfferExpired;

    /// <summary>Raised to show the action menu at a given point.</summary>
    public event Action<PhysicalPoint>? ShowActionMenu;
    /// <summary>Raised to hide the action menu.</summary>
    public event Action? HideActionMenu;
    /// <summary>Raised to start manual (zoom-to-point) selection at a given point (captures a fresh screenshot).</summary>
    public event Action<PhysicalPoint>? StartManualSelection;
    /// <summary>Raised to re-show the manual-selection view when navigating BACK from the action menu,
    /// reusing the existing screenshot instead of re-capturing (the screen is dimmed by the backdrop).</summary>
    public event Action<PhysicalPoint>? RestoreManualSelectionView;
    /// <summary>Raised to end manual selection.</summary>
    public event Action? EndManualSelection;
    /// <summary>Raised to hide the manual selection overlay.</summary>
    public event Action? HideManualSelection;
    /// <summary>Fired when a Drag action is selected. Carries the resolved grab origin point.</summary>
    public event Action<PhysicalPoint>? DragStarted;

    // Locked-action dwell-to-act (design D23–D28). Timing is sample-clock driven: a fixation
    // ARMS the dwell at its start point; only VALID gaze samples advance it; one fire per
    // fixation. Liveness also comes from raw validity (review PR #139 finding 1 — every filtered
    // view of a lost-eyes gap looks like a valid frozen point), so NoteGazeSample is the one
    // place "eyes actually present" is still observable.
    private const long LostGazeExitThresholdUs = 2_000_000; // sustained loss ends the mode
    private long _lastSampleTimestampUs = long.MinValue;
    private long _lastValidGazeTimestampUs = long.MinValue;
    private PhysicalRect[] _repeatSuppressionZones = Array.Empty<PhysicalRect>();
    private PhysicalPoint? _repeatDwellPoint;
    private long _repeatDwellStartedUs;
    private bool _repeatDwellFired;

    /// <summary>
    /// How long gaze must hold (ms) before the locked action fires at the fixated point. Set by
    /// the app-side repeat controller from <c>RepeatSettings.DwellMs</c> at mode entry.
    /// </summary>
    public int RepeatDwellMs { get; set; } = 600;

    /// <summary>Raised with 0..1 progress of the armed locked-action dwell (drives the contracting cursor).</summary>
    public event Action<double>? RepeatDwellProgressChanged;

    // ── The activation offer (ticket 8): gaze-thread state, driven here beside ticket 3's rule. ──
    //
    // The engine is attached by GazeEngine (which owns the geometry and the settings) and every
    // mutation below happens on the gaze thread. Dispatcher-thread teardowns and completions set the
    // volatile/Interlocked requests; ApplyPendingActivationOfferRequests (called at the top of the
    // gaze pipeline) is the one place they are applied.
    private const long NoActivationOfferRequest = -1;
    private ActivationOfferEngine? _activationOffers;
    private PhysicalRect[] _activationChromeZones = Array.Empty<PhysicalRect>();
    // The id of the offer the button currently shows; 0 = none. Written on the gaze thread only (the
    // offer lifecycle is published from here, never from a dispatcher thread), with a full fence, and
    // ALSO read by SetState, which runs on the dispatcher for the keyboard toggle, DeclineOffer and
    // repeat-mode entry (review F3). SetState fences between its CurrentState write and this read, and
    // the publish fences between this write and its CurrentState read, so one of the two always sees
    // the other's write and a ring pinned at an offer is never left with nobody releasing it.
    private long _publishedActivationOfferId;
    // Where the ring's last pin came from. Both are written on the gaze thread only (every pin is
    // raised there). _ringPinnedAtOffer: the pin is a live offer's P, so a fixation exit to FreeLook
    // leaves it to the offer's close; a pin at a no-offer settle (chrome, say) is released at its run's
    // end like any other (review F2). _ringPinnedThisRun: the current run pinned the ring, so an offer
    // closing mid-run leaves the user's own hold alone; an offer closing during a run that has not
    // pinned yet frees the old P's pin at once.
    private volatile bool _ringPinnedAtOffer;
    private bool _ringPinnedThisRun;
    // Every request slot is Interlocked: set on any thread, drained on the gaze thread. A plain
    // volatile bool for the clear used to allow a set landing between the drain's read and its
    // write-back of false to be lost (review A F2), which could strand the fired lock.
    private int _activationOfferClearRequested;
    private long _activationOfferAcceptRequested = NoActivationOfferRequest;
    private long _activationOfferDeclineRequested = NoActivationOfferRequest;
    // The run end is a request too (review A F1): SetState runs on the dispatcher for pause, the
    // keyboard toggle, the Modes menu, DeclineOffer and completed activations, and the engine's run
    // bookkeeping must not be mutated from there.
    private long _activationRunEndRequestedUs = NoActivationOfferRequest;
    // Chrome publishes request a gaze-thread re-validation of the live offer (review A F3), and the
    // modal-surface suspension is applied there as well.
    private int _activationChromeRevalidationRequested;
    private int _activationOffersSuspendedRequested = -1;
    // The applied suspension state, held HERE and not only in the engine (review A R2-1): the
    // settings window's ring-size slider rebuilds the engine, and a rebuild that forgot the
    // suspension would offer buttons over Settings again.
    private bool _activationOffersSuspended;

    /// <summary>The live offer engine, or null before the app has configured a geometry (tests, and
    /// the first samples of startup). Read on the gaze thread; null means no offers at all.</summary>
    public ActivationOfferEngine? ActivationOffers => _activationOffers;

    /// <summary>
    /// Attaches the offer engine (gaze thread; GazeEngine applies its configure request here). The
    /// chrome zones and the suspension state the app already published are re-applied, and the button
    /// state is re-published so a rebuild drops whatever the previous engine was showing.
    /// </summary>
    public void AttachActivationOffers(ActivationOfferEngine engine)
    {
        if (_activationOffers is { } previous)
        {
            previous.OfferFired -= OnActivationOfferFired;
            previous.OfferExpired -= OnActivationOfferExpired;
        }
        _activationOffers = engine;
        engine.SetChromeZones(_activationChromeZones);
        engine.SetOffersSuspended(_activationOffersSuspended);
        engine.OfferFired += OnActivationOfferFired;
        engine.OfferExpired += OnActivationOfferExpired;
        PublishActivationOfferState();
    }

    /// <summary>
    /// Publishes EyeCtrl's chrome zones to the offer engine as plain data — the
    /// <see cref="SetRepeatSuppressionZones"/> pattern. Called from the dispatcher on every chrome
    /// change; the array reference swap is atomic and the engine only reads it on the gaze thread.
    /// Zones already published survive an engine rebuild through <see cref="AttachActivationOffers"/>.
    /// </summary>
    public void SetActivationChromeZones(params PhysicalRect[] zones)
    {
        _activationChromeZones = zones ?? Array.Empty<PhysicalRect>();
        _activationOffers?.SetChromeZones(_activationChromeZones);
        // Chrome that appeared over a live offer must take it away (review A F3): the text-field
        // keyboard auto-shows without suppression, and the toolbar can be summoned over an offer's
        // area. The re-validation runs on the gaze thread with the other requests.
        Interlocked.Exchange(ref _activationChromeRevalidationRequested, 1);
    }

    /// <summary>
    /// Sets whether offers are suspended entirely (the settings window, the task switcher, the
    /// quick-actions grid or the eye-care banner is open — the user's decision of 2026-10-02). A rise
    /// clears a live unfired offer; the state is applied by the gaze thread with the other requests.
    /// </summary>
    public void SetActivationOffersSuspended(bool suspended)
        => Interlocked.Exchange(ref _activationOffersSuspendedRequested, suspended ? 1 : 0);

    /// <summary>
    /// Feeds one non-gap sample to the offer engine (gaze thread). <paramref name="point"/> is the
    /// filtered point, <paramref name="isValid"/> the raw sample's validity: a sample the tracker
    /// reported invalid pauses the button dwell even though its held position looks valid.
    /// </summary>
    public void NoteActivationGazeSample(PhysicalPoint point, long timestampUs, bool isValid)
    {
        if (_activationOffers is null) return;
        _activationOffers.Update(point, timestampUs, isValid);
        PublishActivationOfferState();
    }

    /// <summary>
    /// Applies the pending offer requests (teardowns, accepts, declines) on the gaze thread, then
    /// publishes the resulting button state. Called at the top of the gaze pipeline before any sample
    /// is fed, so a teardown lands before the next dwell step (re-check N3, the volatile-request
    /// pattern).
    /// </summary>
    public void ApplyPendingActivationOfferRequests()
    {
        // Drained even when no engine is attached, so a request made before the first configure can
        // never sit until a later engine's ids happen to match it.
        long runEndUs = Interlocked.Exchange(ref _activationRunEndRequestedUs, NoActivationOfferRequest);
        int suspended = Interlocked.Exchange(ref _activationOffersSuspendedRequested, -1);
        bool chromeRevalidate = Interlocked.Exchange(ref _activationChromeRevalidationRequested, 0) != 0;
        bool clearRequested = Interlocked.Exchange(ref _activationOfferClearRequested, 0) != 0;
        long acceptId = Interlocked.Exchange(ref _activationOfferAcceptRequested, NoActivationOfferRequest);
        long declineId = Interlocked.Exchange(ref _activationOfferDeclineRequested, NoActivationOfferRequest);

        // The applied suspension is latched even with no engine attached, so an engine that arrives
        // later (or replaces one) inherits it (review A R2-1).
        if (suspended >= 0)
            _activationOffersSuspended = suspended != 0;

        if (_activationOffers is null)
            return;

        // Order: the run end first (review A F1 — it runs before the sample's settle can happen, the
        // same ordering SetState provided synchronously), then the two chrome-shaped teardowns, then
        // the explicit teardown, then the completion slots.
        if (runEndUs != NoActivationOfferRequest)
            _activationOffers.NotifyFixationEnded(runEndUs);
        if (suspended >= 0)
            _activationOffers.SetOffersSuspended(_activationOffersSuspended);
        if (chromeRevalidate)
            _activationOffers.RevalidateChromeZones();

        // A teardown outranks a pending accept or decline: R3 says a teardown clears even a locked
        // offer, so when both are in flight the user's exit wins over a completion racing it.
        if (clearRequested)
            _activationOffers.Clear();
        if (acceptId != NoActivationOfferRequest)
            _activationOffers.Accept(acceptId);
        if (declineId != NoActivationOfferRequest)
            _activationOffers.Decline(declineId);

        PublishActivationOfferState();
    }

    /// <summary>
    /// Whether the offer named by <paramref name="offerId"/> is still the live one, read from the
    /// completion path (dispatcher). A pending teardown counts as gone so a completion racing a
    /// teardown is a no-op. Reference and flag reads only — the offer itself is never mutated here.
    /// </summary>
    public bool IsActivationOfferLive(long offerId)
        => Volatile.Read(ref _activationOfferClearRequested) == 0
           && _activationOffers?.CurrentOffer is { } offer
           && offer.Id == offerId;

    /// <summary>Requests a teardown of the offer (pause, keyboard, repeat mode, external dwell,
    /// calibration, reset). Applied by the gaze thread on its next sample.</summary>
    public void RequestActivationOfferClear()
        => Interlocked.Exchange(ref _activationOfferClearRequested, 1);

    /// <summary>
    /// Requests that the current fixation run's end be recorded by the engine, at
    /// <paramref name="timestampUs"/>. SetState calls this on every exit from Fixation; the gaze
    /// thread applies it first among the pending requests, before the next sample's settle (review A
    /// F1).
    /// </summary>
    public void RequestActivationRunEnd(long timestampUs)
        => Interlocked.Exchange(ref _activationRunEndRequestedUs, timestampUs);

    /// <summary>Requests that the offer named by <paramref name="offerId"/> be accepted. Applied by
    /// the gaze thread; a stale id is a no-op there (review A F2).</summary>
    public void RequestActivationOfferAccept(long offerId)
        => Interlocked.Exchange(ref _activationOfferAcceptRequested, offerId);

    /// <summary>Requests that the offer named by <paramref name="offerId"/> be declined. Applied by the
    /// gaze thread; a stale id is a no-op there.</summary>
    public void RequestActivationOfferDecline(long offerId)
        => Interlocked.Exchange(ref _activationOfferDeclineRequested, offerId);

    /// <summary>
    /// The AutoSelection fallback's way out (ticket 8): declines the named offer and ends the fixation,
    /// replacing the old <c>OnFixationEnded</c> call — which only left the state, leaving the offer
    /// alive (and, from ticket 8, the button up).
    /// </summary>
    public void DeclineOffer(long offerId)
    {
        RequestActivationOfferDecline(offerId);
        OnFixationEnded();
    }

    // The engine's fire and expiry reach the app through these, so one subscription site wires the
    // whole offer lifecycle into the adapter.
    private void OnActivationOfferFired(ActivationOffer offer)
        => ActivationOfferFired?.Invoke(offer, _lastSampleTimestampUs);

    private void OnActivationOfferExpired(ActivationOffer offer, long timestampUs, double progressAtExpiry)
        => ActivationOfferExpired?.Invoke(offer, timestampUs, progressAtExpiry);

    // The one publisher of the button's lifetime, called after every engine mutation. An id change is
    // a close followed by a show when a replacement took the old offer's place, so the window never
    // shows stale geometry.
    private void PublishActivationOfferState()
    {
        var offer = _activationOffers?.CurrentOffer;
        long id = offer?.Id ?? 0;
        if (id == _publishedActivationOfferId) return;
        // The full fence pairs with SetState's (see the field): the CurrentState reads below must see
        // a dispatcher-side exit that raced this publish.
        bool hadOffer = Interlocked.Exchange(ref _publishedActivationOfferId, id) != 0;
        if (hadOffer)
            ActivationOfferClosed?.Invoke();
        if (offer is not null)
        {
            ActivationOfferShown?.Invoke(offer);
            // The ring follows the offer, not the fixation state: it pins at the offer's P as the
            // offer appears (a settle's creation or replacement, or a pending re-offer taking over a
            // run that still holds) and stays there while the user looks at the button.
            if (CurrentState == InteractionState.Fixation)
                RaisePinRing(offer.Point, atOffer: true);
        }
        else if (hadOffer && (CurrentState != InteractionState.Fixation || !_ringPinnedThisRun))
        {
            // The offer is gone: the ring goes back to the gaze (snap out) — unless the run that is
            // holding now pinned it (the offer's own run, cleared by chrome or the keyboard mid-hold,
            // or a chrome settle): that is the user's hold, released at the run's end (SetState). A
            // run that has not pinned yet frees the old P at once.
            ReleaseRing?.Invoke();
        }
    }

    private void RaisePinRing(PhysicalPoint point, bool atOffer)
    {
        _ringPinnedAtOffer = atOffer;
        _ringPinnedThisRun = true;
        PinRing?.Invoke(point);
    }

    private void SetState(InteractionState newState)
    {
        var previous = CurrentState;
        CurrentState = newState;
        // Pairs with the publish's fence (see _publishedActivationOfferId): either this read sees the
        // gaze thread's close, or the close's CurrentState read sees this exit.
        Interlocked.MemoryBarrier();
        // Every exit from Fixation, through whatever path, is one place: the explicit end signal the
        // replay parity reads, and the ring release (re-check N2). The keyboard toggle is the proof
        // case: it ends the fixation by calling OnFixationEnded directly while suppression stops the
        // feed, so a detector-keyed release would never fire.
        if (previous == InteractionState.Fixation && newState != InteractionState.Fixation)
        {
            // The offer survives the fixation's end (that is the linger), but the engine still has to
            // learn the run ended: its expiry moves to the run end's deadline, and a pending re-offer
            // from this run is cleared (it needs its run still holding). That mutation is a REQUEST —
            // SetState runs on the dispatcher for pause, the keyboard toggle, the Modes menu,
            // DeclineOffer and completed activations, and the engine is gaze-thread state (review A F1).
            RequestActivationRunEnd(_lastSampleTimestampUs);
            FixationEnded?.Invoke();
            // The ring stays pinned at P for as long as the offer lives (live check 2026-10-02: the
            // run flips FreeLook/Fixation every ~0.3 s while the user holds the button, and a ring that
            // released and re-pinned on every flip danced over the button). The release then comes
            // from the offer's close (PublishActivationOfferState). It comes here when no offer is
            // alive, when the pin is not the offer's (a chrome settle during the linger, review F2),
            // and on every exit that is not to FreeLook.
            bool keepOfferPin = newState == InteractionState.FreeLook
                && _ringPinnedAtOffer
                && Volatile.Read(ref _publishedActivationOfferId) != 0;
            if (!keepOfferPin)
                ReleaseRing?.Invoke();
        }
        StateChanged?.Invoke(newState);
    }

    /// <summary>
    /// Called when fixation starts.
    /// </summary>
    /// <param name="point">Fixation point in physical screen pixels</param>
    public void OnFixationStarted(PhysicalPoint point)
    {
        if (CurrentState == InteractionState.RepeatMode)
        {
            // D25: the fixation arms the dwell at its start point; valid samples advance it in
            // NoteGazeSample; it fires once and re-arms only on the NEXT fixation. Fixations on
            // EyeCtrl chrome (suppression zones, D26) never arm — nothing will fire there.
            if (!IsInAnyRepeatSuppressionZone(point))
            {
                _repeatDwellPoint = point;
                _repeatDwellStartedUs = _lastSampleTimestampUs;
                _repeatDwellFired = false;
            }
            return;
        }

        if (CurrentState != InteractionState.FreeLook) return;
        FixationPoint = point;
        _ringPinnedThisRun = false;
        SetState(InteractionState.Fixation);
        // After SetState, so a listener that also watches StateChanged sees the state already moved.
        // No offer is made at onset: the button appears at the accepted settle (ticket 8), so resting
        // at P can no longer activate.
        FixationAccepted?.Invoke(point);
    }

    /// <summary>
    /// Called when the fixation detector reports a settle (its valid-time age reached
    /// <c>max(onset, FixationSettleMs)</c>), carrying the running centroid at settle.
    /// <para>
    /// The settle is accepted only in <see cref="InteractionState.Fixation"/> — which is exactly "this
    /// run's onset was accepted", because the state enters Fixation only on an accepted onset and leaves
    /// it on the run's end. A settle that arrives after the state moved on (a pause, a menu, an
    /// activation) is ignored. Acceptance raises <see cref="FixationSettled"/> and hands the point to
    /// the offer engine; <see cref="PinRing"/> follows from what the engine made of it (see there).
    /// </para>
    /// </summary>
    /// <param name="point">The running centroid at settle, in physical screen pixels.</param>
    public void OnFixationSettled(PhysicalPoint point)
    {
        if (CurrentState != InteractionState.Fixation) return;
        FixationSettled?.Invoke(point);
        // The accepted settle is the offer's creation point (ticket 8): the engine classifies it
        // (create / replace / extend / swallow) and the button's show and hide follow from there.
        var outcome = _activationOffers?.NotifyFixationSettled(point, _lastSampleTimestampUs);
        PublishActivationOfferState();   // a new offer pins the ring at its P (below)

        // A settle that changed nothing about a live offer — on the button, a near miss next to it, a
        // swallowed one on P's side, or one arriving while a fired offer is locked — must NOT move the
        // ring: it belongs to the offer's P until the offer closes, and re-pinning on the user's own
        // button dwell is exactly the dance the live check found. Everything else pins here: a created
        // or replaced offer (already pinned by the publish), and a settle with no offer at all (chrome,
        // an unplaceable side, no engine yet), which keeps the old pin-at-settle behaviour.
        if (outcome is null or SettleOutcome.NoOffer)
            RaisePinRing(point, atOffer: false);
    }

    /// <summary>Called when the current fixation ends (gaze moves away from the fixation point).</summary>
    public void OnFixationEnded()
    {
        if (CurrentState == InteractionState.RepeatMode)
        {
            // Gaze moved off the target: the armed dwell cancels (D25) — the action must be
            // re-aimed with a fresh fixation, never inherited from a stale one.
            _repeatDwellPoint = null;
            _repeatDwellFired = false;
            RepeatDwellProgressChanged?.Invoke(0);
            return;
        }

        if (CurrentState == InteractionState.Fixation)
        {
            // Deliberately no HideActivationButton here: the button's lifetime is the offer's, and
            // the fixation ending is exactly what starts the linger. The engine learns the end in
            // SetState's exit edge.
            SetState(InteractionState.FreeLook);
        }
    }

    /// <summary>
    /// The activation offer's completed dwell (ticket 8). The offer's liveness is checked first: a
    /// stale id — the offer is already gone — is a <b>no-op</b>, because a rejection must never act on
    /// whatever happens to be live now (review A F2). A wrong state (anything but
    /// <see cref="InteractionState.Fixation"/> or <see cref="InteractionState.FreeLook"/>: the
    /// fixation that formed on the button may already have ended, B4) declines the NAMED offer. The
    /// accepted offer is released to the gaze thread, and the effect lands at the offer's P.
    /// </summary>
    /// <param name="snapshot">The fired offer's snapshot: its id and P.</param>
    /// <returns>What the completion did, so the caller can log the truth (ticket 8 fixup 02,
    /// review B): a stale id and a wrong state do NOT activate.</returns>
    public ActivationCompletionResult OnActivationButtonDwelled(ActivationOffer snapshot)
    {
        if (!IsActivationOfferLive(snapshot.Id))
            return ActivationCompletionResult.Stale;

        if (CurrentState is not (InteractionState.Fixation or InteractionState.FreeLook))
        {
            RequestActivationOfferDecline(snapshot.Id);
            return ActivationCompletionResult.Declined;
        }

        RequestActivationOfferAccept(snapshot.Id);
        CompleteActivation(snapshot.Point);
        return ActivationCompletionResult.Accepted;
    }

    /// <summary>
    /// Completes the activation at the current <see cref="FixationPoint"/>, with no offer bookkeeping.
    /// The in-Fixation state-machine transition, for callers that already hold the run (and the
    /// state-transition tests); the product goes through
    /// <see cref="OnActivationButtonDwelled(ActivationOffer)"/>, which adds the offer's liveness
    /// check, its accept, and lands at the offer's P.
    /// <para>
    /// INTERNAL since ticket 8's fixup (review A F6): it was public with no production caller, and it
    /// bypasses the offer — a live offer survives it, so wiring it back would strand the fired lock.
    /// The tests reach it through <c>InternalsVisibleTo</c>.
    /// </para>
    /// </summary>
    internal void OnActivationButtonDwelled()
    {
        if (CurrentState != InteractionState.Fixation) return;
        CompleteActivation(FixationPoint!.Value);
    }

    /// <summary>The state-machine half: lands the action at <paramref name="point"/> and branches on
    /// <see cref="SelectionMode"/> — Manual starts the zoom overlay; Auto shows the action menu at the
    /// resolved <see cref="AutoSelectionTarget"/> (or the point when none was found); None shows the
    /// action menu at the point.</summary>
    private void CompleteActivation(PhysicalPoint point)
    {
        FixationPoint = point;

        switch (SelectionMode)
        {
            case SelectionMode.ManualSelection:
                StartManualSelection?.Invoke(point);
                SetState(InteractionState.ManualSelectionActive);
                break;

            case SelectionMode.AutoSelection:
                if (AutoSelectionTarget != null)
                    ShowActionMenu?.Invoke(AutoSelectionTarget.CenterPoint);
                else
                    ShowActionMenu?.Invoke(point);
                SetState(InteractionState.ActionMenuVisible);
                break;

            default: // None
                ShowActionMenu?.Invoke(point);
                SetState(InteractionState.ActionMenuVisible);
                break;
        }
    }

    /// <summary>
    /// Called when ManualSelection dwell completes.
    /// </summary>
    /// <param name="precisePoint">Precise dwell point in physical screen pixels</param>
    public void OnManualSelectionDwell(PhysicalPoint precisePoint)
    {
        if (CurrentState != InteractionState.ManualSelectionActive) return;
        ManualSelectionDwellPoint = precisePoint;
        HideManualSelection?.Invoke();
        ShowActionMenu?.Invoke(precisePoint);
        SetState(InteractionState.ManualSelectionActionMenuVisible);
    }

    /// <summary>
    /// Fires the chosen action at the resolved target point. In Auto Selection the click uses the
    /// snapped <see cref="AutoSelectionTarget"/> center (falling back to the fixation point when no
    /// control was resolved), so the action lands on the control rather than the raw gaze position.
    /// </summary>
    public void OnActionSelected(InteractionAction action, IReadOnlySet<ModifierKey>? modifiers = null)
    {
        PhysicalPoint? targetPoint = CurrentState switch
        {
            InteractionState.ActionMenuVisible
                when SelectionMode == SelectionMode.AutoSelection && AutoSelectionTarget != null
                    => AutoSelectionTarget.CenterPoint,
            InteractionState.ActionMenuVisible
                    => FixationPoint,
            InteractionState.ManualSelectionActionMenuVisible
                    => ManualSelectionDwellPoint,
            _       => null
        };

        if (targetPoint == null) return;

        if (action == InteractionAction.ScrollMode)
        {
            HideActionMenu?.Invoke();
            if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            ScrollAnchor = targetPoint;
            SetState(InteractionState.ScrollMode);
            return;
        }

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();

        if (action == InteractionAction.Drag)
        {
            SetState(InteractionState.Dragging);
            DragStarted?.Invoke(targetPoint.Value);
            return;
        }

        ActionRequested?.Invoke(action, targetPoint.Value, modifiers);
        ResetToFreeLook();
    }

    /// <summary>Navigates back from the action menu to manual selection, or closes manual selection to free look.</summary>
    public void OnManualSelectionBack()
    {
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
        {
            // Back from action view → return to ManualSelection selection. Reuse the existing
            // capture (do NOT re-capture — the backdrop is up, and the shot is unchanged).
            HideActionMenu?.Invoke();
            ManualSelectionDwellPoint = null;
            SetState(InteractionState.ManualSelectionActive);
            RestoreManualSelectionView?.Invoke(FixationPoint!.Value);
        }
        else if (CurrentState == InteractionState.ManualSelectionActive)
        {
            // Close from ManualSelection view → return to free look
            EndManualSelection?.Invoke();
            ResetToFreeLook();
        }
    }

    /// <summary>
    /// Dismisses the action menu and returns to free look.
    ///
    /// Covers the ManualSelection path too, since 2026-08-26. It guarded on
    /// <see cref="InteractionState.ActionMenuVisible"/> alone while nothing could dismiss the menu
    /// from the ManualSelection path — the ring's Back returns to the magnifier
    /// (<see cref="OnManualSelectionBack"/>), which is a step back rather than a way out. The
    /// right-edge Close is a way out from either state, so the magnified view has to be torn down
    /// as well; <see cref="TogglePause"/> does the same pairing.
    /// </summary>
    public void OnActionMenuDismissed()
    {
        if (CurrentState is not (InteractionState.ActionMenuVisible
                              or InteractionState.ManualSelectionActionMenuVisible)) return;

        HideActionMenu?.Invoke();
        if (CurrentState == InteractionState.ManualSelectionActionMenuVisible)
            EndManualSelection?.Invoke();
        ResetToFreeLook();
    }

    /// <summary>Toggles gaze tracking between paused and active states.</summary>
    /// <remarks>Blocked during <see cref="InteractionState.ScrollMode"/>, <see cref="InteractionState.Dragging"/> and <see cref="InteractionState.RepeatMode"/> — use the stop/cancel affordances to exit those modes.</remarks>
    public void TogglePause()
    {
        if (CurrentState == InteractionState.Paused)
        {
            SetState(InteractionState.FreeLook);
        }
        else if (CurrentState is InteractionState.ScrollMode or InteractionState.Dragging or InteractionState.RepeatMode)
        {
            return;
        }
        else
        {
            // Pausing is a teardown: the offer is requested clear on the gaze thread, so a linger
            // cannot outlive the pause and complete while the user is elsewhere (M5).
            RequestActivationOfferClear();
            HideActionMenu?.Invoke();
            if (CurrentState is InteractionState.ManualSelectionActive or InteractionState.ManualSelectionActionMenuVisible)
                EndManualSelection?.Invoke();
            SetState(InteractionState.Paused);
        }
    }

    /// <summary>Exits gaze-scroll mode and returns to free look.</summary>
    public void ExitScrollMode()
    {
        if (CurrentState != InteractionState.ScrollMode) return;
        ScrollAnchor = null;
        ResetToFreeLook();
    }

    /// <summary>Ends the drag operation and returns to FreeLook.</summary>
    public void CompleteDrag()
    {
        if (CurrentState != InteractionState.Dragging) return;
        ResetToFreeLook();
    }

    /// <summary>
    /// Enters repeat mode directly — the toolbar Modes-menu path (D17/D1′); no action menu is
    /// involved. Guarded to <see cref="InteractionState.FreeLook"/>: modes are mutually
    /// exclusive, and the toolbar button is dwellable in every state, so silently trampling a
    /// visible menu, a drag, scroll mode, or Paused would strand their UI. Callers that need
    /// entry from another state must end that state first.
    /// </summary>
    public void EnterRepeatMode(InteractionAction action)
    {
        if (CurrentState != InteractionState.FreeLook) return;
        // The Modes menu offers the click family only (D24) — anything else is a wiring bug,
        // not a user choice, and is refused rather than guessed at.
        if (!action.IsRepeatableClick()) return;
        // Repeat mode is a teardown: its own dwell owns the screen's targeting, so a lingering offer
        // must go (M5).
        RequestActivationOfferClear();
        RepeatAction = action;
        SetState(InteractionState.RepeatMode);
    }

    /// <summary>Exits repeat mode and returns to free look.</summary>
    public void ExitRepeatMode()
    {
        if (CurrentState != InteractionState.RepeatMode) return;
        ResetToFreeLook();
    }

    /// <summary>
    /// Latches raw gaze-stream liveness: called by <see cref="GazeEngine"/> for EVERY sample,
    /// before calibration or gap interpolation — every downstream view of a lost-eyes gap looks
    /// like a valid frozen point, so this is the only spot where "eyes actually present" is
    /// still observable. Drives the locked-action dwell: only VALID samples advance it, and
    /// sustained lost gaze (past <see cref="LostGazeExitThresholdUs"/>) ends the mode — the
    /// user cannot dwell the stop button while their eyes are away.
    /// </summary>
    public void NoteGazeSample(bool isValid, long timestampUs)
    {
        if (timestampUs > _lastSampleTimestampUs) _lastSampleTimestampUs = timestampUs;
        if (isValid && timestampUs > _lastValidGazeTimestampUs) _lastValidGazeTimestampUs = timestampUs;

        if (CurrentState != InteractionState.RepeatMode) return;

        if (_lastSampleTimestampUs > _lastValidGazeTimestampUs
            && _lastSampleTimestampUs - _lastValidGazeTimestampUs > LostGazeExitThresholdUs)
        {
            ExitRepeatMode();
            return;
        }

        // D25: advance the armed dwell on valid samples only — invalid gaze freezes the dwell
        // (and its cursor feedback) rather than firing at a stale point.
        if (!isValid || _repeatDwellPoint is not { } target || _repeatDwellFired) return;

        var dwellUs = RepeatDwellMs * 1000L;
        var elapsedUs = timestampUs - _repeatDwellStartedUs;
        if (elapsedUs >= dwellUs)
        {
            _repeatDwellFired = true;   // one fire per fixation; re-arm needs a new fixation
            RepeatDwellProgressChanged?.Invoke(1);
            ActionRequested?.Invoke(RepeatAction!.Value, target, null);
            // The cursor STAYS contracted after the fire (review round 3 finding 6): a held
            // stare cannot re-fire, so snapping straight back to full size would read as
            // "ready" when it is not. It resets on fixation end / the next arm. The gaze
            // stream keeps invoking 1 — harmless, it is already there. The else-branch is
            // skipped once fired.
        }
        else
        {
            RepeatDwellProgressChanged?.Invoke(Math.Clamp((double)elapsedUs / dwellUs, 0, 1));
        }
    }

    /// <summary>
    /// Chrome rects the locked-action dwell must never fire inside (D26): the toolbar band (incl.
    /// the modes-menu area) and the stop-button zone. Set by the app-side repeat controller from
    /// screen geometry; Core keeps them as plain data so the gaze thread never calls back into UI.
    /// </summary>
    public void SetRepeatSuppressionZones(params PhysicalRect[] zones)
        => _repeatSuppressionZones = zones ?? Array.Empty<PhysicalRect>();

    private bool IsInAnyRepeatSuppressionZone(PhysicalPoint point)
    {
        foreach (var z in _repeatSuppressionZones)
        {
            if (point.X >= z.X && point.X <= z.X + z.Width
                && point.Y >= z.Y && point.Y <= z.Y + z.Height)
                return true;
        }
        return false;
    }

    private void ResetToFreeLook()
    {
        // A reset out of any overlay path is a teardown: no offer may survive it (M5). Requested, not
        // cleared — the engine is gaze-thread state.
        RequestActivationOfferClear();
        FixationPoint = null;
        ManualSelectionDwellPoint = null;
        AutoSelectionTarget = null;
        ScrollAnchor = null;
        RepeatAction = null;
        _repeatDwellPoint = null;
        _repeatDwellFired = false;
        SetState(InteractionState.FreeLook);
    }
}
