namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class CursorCircleViewModel : MessengerViewModelBase,
    IRecipient<GazeCursorMovedMessage>,
    IRecipient<StateChangedMessage>,
    IRecipient<ExternalDwellActiveMessage>,
    IRecipient<PinRingMessage>,
    IRecipient<ReleaseRingMessage>,
    IRecipient<ShowActivationButtonMessage>,
    IRecipient<HideActivationButtonMessage>
{
    private readonly EyeCtrlSettings _settings;

    /// <summary>The hybrid's grace: the engine's own outside-pause budget (500 ms), so the hold lives
    /// exactly as long as the button dwell would survive an excursion ("The hybrid, precisely").</summary>
    public const int HybridFreezeGraceMs = 500;

    /// <summary>In-hit samples after which the hold has proven itself: three (90 ms at 30 ms frames).</summary>
    public const int HybridFreezeProvenSamples = 3;

    /// <summary>The nominal proven time the startup line prints: three 30 ms samples. The RULE is the
    /// sample count above, not elapsed time.</summary>
    public const int HybridFreezeProvenMs = 90;

    /// <summary>The going-for-it keep's time limit T: how long a run-end release is deferred while the
    /// eye is on its way to the button (inside the P-B corridor or B's no-move zone). It equals the
    /// hold's grace and the engine's outside-pause budget, so the ring waits exactly as long as the
    /// button's dwell survives an excursion. (Named for the approach window it widens.)</summary>
    public const int HybridFreezeApproachMs = 500;

    /// <summary>
    /// The ring hold policy (<c>--ring-arm</c> sets it once at startup; never a setting). The class
    /// default stays <see cref="RingHoldPolicy.None"/>, the offer-following behaviour bit for bit; the
    /// App's launch composition selects the arm (the hybrid, unless <c>--ring-arm</c> says otherwise).
    /// </summary>
    public RingHoldPolicy RingHoldPolicy { get; set; } = RingHoldPolicy.None;

    /// <summary>One record per <see cref="PinRingMessage"/> processed, applied or refused; the look
    /// build's event log (App side) pairs each with Core's pin origin.</summary>
    public event Action<RingPinProcessedEvent>? RingPinProcessed;

    /// <summary>One record per <see cref="ReleaseRingMessage"/> processed.</summary>
    public event Action<RingReleaseProcessedEvent>? RingReleaseProcessed;

    /// <summary>A physical release that did not happen at a release message: a hold's end, a hiding
    /// state's clear, or the hybrid's deferred run-end resolution.</summary>
    public event Action<RingReleaseEvent>? RingReleased;

    /// <summary>A hybrid hold started.</summary>
    public event Action<RingHoldStartEvent>? RingHoldStarted;

    /// <summary>A hybrid hold ended.</summary>
    public event Action<RingHoldEndEvent>? RingHoldEnded;

    /// <summary>The going-for-it keep began: the run-end pin at P is kept while the gaze is on its way
    /// to B.</summary>
    public event Action<RingGoStartEvent>? RingGoStarted;

    /// <summary>The going-for-it keep ended (hit entry, left the corridor, timeout, offer closed, hidden).</summary>
    public event Action<RingGoEndEvent>? RingGoEnded;

    // Effective visibility = the interaction state allows a cursor AND no external-dwell target
    // is currently active. Two independent inputs are combined into the single IsVisible so there
    // is exactly one owner of the property (no writer races between state changes and external dwell).
    private bool _stateAllowsCursor = true;
    private bool _externalDwellActive;

    // The latest sample position, tracked even while pinned: the release snaps the ring back to gaze
    // immediately, and while pinned the gaze channel deliberately does not move Position.
    private PhysicalPoint _latestGaze;

    // The last state the machine reported. A pin is only accepted in Fixation, which makes "a pinned
    // ring implies Fixation" structural: an exit posted by the dispatcher can land between the gaze
    // thread's state check and its pin post (review A, F4), and without this the ring would stay
    // frozen in FreeLook or Paused — with no later release on the keyboard path.
    private InteractionState _lastState = InteractionState.FreeLook;

    // The glide (ticket 7): the ring eases from where it was to the settle centroid over
    // RingGlide.DurationMs, driven by the sample clock (the frames the gaze channel already delivers),
    // never by a WPF animation. _glideStartUs is -1 until the first frame after the pin, so the glide
    // is measured from the first sample at or after the pin edge.
    private bool _gliding;
    private PhysicalPoint _glideFrom;
    private PhysicalPoint _glideTo;
    private long _glideStartUs = -1;

    // ── The hybrid hold (RingHoldPolicy.HybridFreeze): replay 2's "The hybrid, precisely" ──
    // All touched on the UI thread with the rest of this VM. The offer's B and hit radius come from
    // the existing ShowActivationButtonMessage / HideActivationButtonMessage; the rules mirror the
    // replay's RingModel, which the arm-B oracle compares sample for sample.
    // Review 01's X2/X3, accepted near-equivalences: "proven" is a SAMPLE COUNT (equal to elapsed
    // time on regular 30 ms frames), and _drawn ignoring glide frames only matters for a freeze
    // during a glide-in, which the freeze's own entry hold pre-empts first.
    private bool _holding;
    private long _holdStartUs;
    private int _inHitCount;
    private long? _outsideSinceUs;
    private long? _approachUntilUs;
    private long _goStartUs;
    private bool _pendingRunEndRelease;
    private bool _offerLive;
    private PhysicalPoint _offerCenter;
    private double _offerHitRadiusPx;
    // The offer's P and the engine's effective fixation radius R, both from the show message; null when
    // the sender did not carry them (then D never fires, and the zone falls back to the baseline setting).
    private PhysicalPoint? _offerPoint;
    private double? _offerFixationRadiusPx;
    private long _lastFrameUs;
    // The last point actually drawn, across BOTH channels (Position and the glide's MoveTo): the
    // freeze stops the ring here, before the current sample's follow applies.
    private PhysicalPoint _drawn;

    /// <summary>One call to the overlay: move the ring to <see cref="Target"/>, and whether this write
    /// is a glide frame (instrument 2 skips glide frames — their cadence is the animation's, not the
    /// ring's). The record carries both at once so the window never sees a position before it knows it
    /// is a pin (critique M3/N10).</summary>
    public readonly record struct RingMove(PhysicalPoint Target, bool Glide);

    [ObservableProperty] private PhysicalPoint _position = new(0, 0);
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>
    /// The one position channel for pin edges and glide frames, forwarded by the binding coordinator to
    /// <c>ICursorCircleOverlay.MoveTo</c>. <see cref="Position"/> stays the channel for ordinary
    /// gaze-following writes and its existing readers; during a glide only this property moves, so the
    /// glide's frames never reach instrument 2 through the Position path.
    /// </summary>
    [ObservableProperty] private RingMove _moveTo;

    /// <summary>
    /// True from Core's <c>PinRing</c> (the accepted settle, or an offer appearing) until Core's
    /// <c>ReleaseRing</c>: the fixation's end, or — while an activation offer is alive — the offer's
    /// close, so the ring does not move while the user looks at the button. While pinned the ring does
    /// not follow gaze: it eases to the pin and then sits literally still there. That stillness is the
    /// "you're holding" signal the user asked for ("the circle almost never stands still"). The VM
    /// itself is keyed on Core's two messages, never on the button's show/hide.
    /// </summary>
    [ObservableProperty] private bool _isPinned;

    public double Size => _settings.Cursor.CircleSize;
    public string CircleColor => _settings.Cursor.CircleColor;
    public double Opacity => _settings.Cursor.CircleOpacity;
    public double Thickness => _settings.Cursor.CircleThickness;

    public CursorCircleViewModel(EyeCtrlSettings settings, IMessenger messenger)
        : base(messenger)
    {
        _settings = settings;
    }

    public void Receive(GazeCursorMovedMessage message)
    {
        // The hybrid's hold runs BEFORE the follow and before _latestGaze moves: the freeze stops the
        // ring on the drawn point, and a release resolved here snaps to the previous sample — the same
        // order the replay model applies (its cursor step is last).
        if (RingHoldPolicy == RingHoldPolicy.HybridFreeze)
            AdvanceHybridHold(message);

        _latestGaze = message.Position;

        if (_gliding)
        {
            AdvanceGlide(message.Frame.TimestampMicroseconds);
            return;
        }

        // Pinned means the ring stays where the fixation settled; a gaze move inside the hold radius
        // must not nudge it. Hidden states (Paused included) do not follow either: the IsVisible
        // gate below stops Position updates while the window is away, so the ring reappears
        // exactly where UpdateVisibility's reappear snap put it.
        if (IsVisible && !IsPinned)
            Position = message.Position;
    }

    /// <summary>
    /// Pins the ring on the settle centroid Core published on the accepted settle. The glide starts
    /// from the ring's current position — the point it was following a moment ago — and ends exactly on
    /// the pin; the first frame after the pin starts the clock.
    /// <para>
    /// Under <see cref="RingHoldPolicy.HybridFreeze"/> a pin while holding or approaching is ignored:
    /// "while holding, settles and run ends are ignored, so there is no re-pin and no snap". It is
    /// likewise ignored while an offer is live and the point is a near miss: inside its no-move zone
    /// and nearer B than P.
    /// </para>
    /// </summary>
    public void Receive(PinRingMessage message)
    {
        // Only a fixation may pin. The check is against the last state the machine REPORTED, so a
        // release/exit that arrives before this message (F4's race) refuses the pin outright.
        bool holdSuppressed = RingHoldPolicy == RingHoldPolicy.HybridFreeze
            && (_holding || _approachUntilUs is not null);
        // A NEAR-MISS settle (inside a live offer's no-move zone AND strictly nearer B than P: the
        // engine's own NearMiss rule, ActivationOfferEngine.cs:452) must not glide the ring toward it;
        // the ring keeps following or holding. A settle on P's side of the zone is the engine's
        // "swallowed" pending re-offer (the owner looking back at the target): it pins as before.
        bool nearOffer = RingHoldPolicy == RingHoldPolicy.HybridFreeze
            && _offerLive
            && _offerPoint is { } offerPoint
            && message.Position.DistanceTo(_offerCenter) <= NoMoveZonePx
            && message.Position.DistanceTo(_offerCenter) < message.Position.DistanceTo(offerPoint);
        if (holdSuppressed || nearOffer || _lastState != InteractionState.Fixation)
        {
            // Refused, but still one record: the look's log pairs every Core pin with its message.
            // D is named as the reason only where it is what decided: a hold or a non-fixation state
            // would have refused the pin anyway.
            bool decidedByNearOffer = nearOffer && !holdSuppressed && _lastState == InteractionState.Fixation;
            RingPinProcessed?.Invoke(new RingPinProcessedEvent(message.Position, 0, Applied: false,
                IgnoredNearOffer: decidedByNearOffer));
            return;
        }

        double dist = _drawn.DistanceTo(message.Position);
        _glideFrom = Position;
        _glideTo = message.Position;
        _glideStartUs = -1;
        _gliding = true;
        IsPinned = true;
        RingPinProcessed?.Invoke(new RingPinProcessedEvent(message.Position, dist, Applied: true));
    }

    /// <summary>
    /// Releases the pin and snaps to gaze — the next drawn position is the latest sample, applied
    /// directly, with no glide sweeping behind the eye after it has already landed (decision 2). Also
    /// cancels a glide that was still in flight, so nothing keeps interpolating toward a stale pin.
    /// <para>
    /// Under <see cref="RingHoldPolicy.HybridFreeze"/> the release is deferred to the next sample
    /// (where the landing gaze decides between keeping the pin, the approach window and the release),
    /// and is ignored entirely while holding or approaching — no snap under a hold.
    /// </para>
    /// </summary>
    public void Receive(ReleaseRingMessage message)
    {
        if (RingHoldPolicy == RingHoldPolicy.HybridFreeze)
        {
            if (!_holding && _approachUntilUs is null)
                _pendingRunEndRelease = true;
            RingReleaseProcessed?.Invoke(new RingReleaseProcessedEvent(ReleasedNow: false, Dist: 0));
            return;
        }

        bool wasPinned = IsPinned;
        double dist = _drawn.DistanceTo(_latestGaze);
        ReleaseNow();
        RingReleaseProcessed?.Invoke(new RingReleaseProcessedEvent(wasPinned, dist));
    }

    public void Receive(StateChangedMessage message)
    {
        _lastState = message.NewState;
        // Paused hides the ring, as every state other than FreeLook and Fixation does (the same
        // set _stateAllowsCursor below admits): the PauseIndicator strip is the pause feedback,
        // and resume needs no cursor — the past-edge summon carries no pause gate and the toolbar
        // band is deliberately dwellable while paused (ToolbarViewModel.DwellActiveWhilePaused).
        // RepeatMode hides the normal circle too: the contracting repeat cursor replaces it there.
        // Belt and braces for the release: a pinned ring implies Fixation or FreeLook — FreeLook only
        // because the ring stays at the offer's P while the user looks at the button, and Core's
        // ReleaseRing (offer closed, or the run ended with no offer) is what frees it there. Any
        // other state cancels the pin (and an in-flight glide) structurally, even if the release
        // message is ever lost. This is also the Paused rule that predates the pin.
        if (message.NewState is not (InteractionState.Fixation or InteractionState.FreeLook))
        {
            bool wasPinned = IsPinned;
            double dist = _drawn.DistanceTo(_latestGaze);
            ReleaseNow();
            if (wasPinned)
                RingReleased?.Invoke(new RingReleaseEvent(RingReleaseCause.HidingState, dist));
        }
        _stateAllowsCursor = message.NewState
            is InteractionState.FreeLook or InteractionState.Fixation;
        UpdateVisibility();
    }

    public void Receive(ExternalDwellActiveMessage message)
    {
        _externalDwellActive = message.Active;
        UpdateVisibility();
    }

    /// <summary>The offer's B and hit radius (physical px), for the hybrid's freeze and its hold
    /// exits; the button's show/hide are NOT pin edges in any policy (the pin's lifetime is Core's).</summary>
    public void Receive(ShowActivationButtonMessage message)
    {
        _offerLive = true;
        _offerCenter = message.ButtonCenter;
        _offerHitRadiusPx = message.HitRadiusPx;
        _offerPoint = message.OfferPoint;
        _offerFixationRadiusPx = message.FixationRadiusPx;
    }

    /// <summary>The live offer's no-move zone around B, in physical px: the hit radius plus the
    /// engine's effective fixation radius R from the show message (the engine's own zone,
    /// ActivationOfferEngine.cs:450), or the baseline setting where the message did not carry it.</summary>
    private double NoMoveZonePx => _offerHitRadiusPx + ZoneRadiusPx;

    /// <summary>The engine's effective fixation radius R from the show message (the baseline setting where
    /// the message did not carry it): the no-move zone's margin and the corridor's half-width.</summary>
    private double ZoneRadiusPx => _offerFixationRadiusPx ?? _settings.Dwell.FixationRadiusPixels;

    /// <summary>The distance from the gaze to the segment P-B, or null where the show message carried
    /// no P (the corridor then does not exist).</summary>
    private double? AxisOffsetPx(PhysicalPoint gaze) =>
        _offerPoint is { } p ? DistanceToSegment(gaze, p, _offerCenter) : null;

    /// <summary>Whether the gaze is on its way to B: inside B's no-move zone, or within R of the segment
    /// P-B (a capsule of half-width R). Without P it is today's zone test.</summary>
    private bool InGo(PhysicalPoint gaze) =>
        gaze.DistanceTo(_offerCenter) <= NoMoveZonePx
        || (AxisOffsetPx(gaze) is { } off && off <= ZoneRadiusPx);

    private static double DistanceToSegment(PhysicalPoint g, PhysicalPoint a, PhysicalPoint b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, wx = g.X - a.X, wy = g.Y - a.Y;
        double len2 = (vx * vx) + (vy * vy);
        double t = len2 <= 0 ? 0 : Math.Clamp(((wx * vx) + (wy * vy)) / len2, 0, 1);
        double dx = wx - (t * vx), dy = wy - (t * vy);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>The keep begins: the pin at P stays while the gaze is on its way to B, for at most T.</summary>
    private void GoStart(PhysicalPoint gaze, long ts)
    {
        _approachUntilUs = ts + (HybridFreezeApproachMs * 1000L);
        _goStartUs = ts;
        RingGoStarted?.Invoke(new RingGoStartEvent(_drawn.DistanceTo(gaze), AxisOffsetPx(gaze)));
    }

    /// <summary>The keep ends, whatever follows (the hold's start, or a release).</summary>
    private void GoEnd(RingGoEndReason reason, PhysicalPoint gaze, long ts)
    {
        _approachUntilUs = null;
        RingGoEnded?.Invoke(new RingGoEndEvent(reason, (ts - _goStartUs) / 1000.0, AxisOffsetPx(gaze)));
    }

    /// <summary>The live offer went: a hold ends with its release (the <c>offer-closed</c> hold end),
    /// and an approach in flight is cleared and released.</summary>
    public void Receive(HideActivationButtonMessage message)
    {
        _offerLive = false;
        if (RingHoldPolicy != RingHoldPolicy.HybridFreeze)
            return;

        if (_holding)
            EndHold(RingHoldEndReason.OfferClosed, release: true);
        else if (_approachUntilUs is not null)
        {
            GoEnd(RingGoEndReason.OfferClosed, _latestGaze, _lastFrameUs);
            ReleaseNowWithLog(RingReleaseCause.OfferClosed);
        }
    }

    /// <summary>
    /// The hybrid's per-sample rules, in the ticket's order a–d, applied to the drawn ring before the
    /// ordinary follow (e in <see cref="Receive(GazeCursorMovedMessage)"/>). Distances are physical
    /// px; R is the engine's <see cref="DwellSettings.FixationRadiusPixels"/>, as the engine itself
    /// uses it for the no-move zone.
    /// </summary>
    private void AdvanceHybridHold(GazeCursorMovedMessage message)
    {
        var gaze = message.Position;
        long ts = message.Frame.TimestampMicroseconds;
        _lastFrameUs = ts;
        double noMoveZonePx = NoMoveZonePx;

        if (_pendingRunEndRelease)
        {
            // a. The deferred run-end release: the landing gaze decides. Into the hit area it keeps
            // the pin as the hold's beginning; on its way to B (the P-B corridor or B's no-move zone)
            // it waits out the going-for-it window T; anywhere else (no offer, or nothing pinned) the
            // release happens.
            _pendingRunEndRelease = false;
            if (_offerLive && IsPinned && gaze.DistanceTo(_offerCenter) <= _offerHitRadiusPx)
                StartHold(ts, RingHoldStartKind.Keep, inPlace: false);
            else if (_offerLive && IsPinned && InGo(gaze))
                GoStart(gaze, ts);
            else
                ReleaseNowWithLog(RingReleaseCause.RunEnd);
        }
        else if (_approachUntilUs is { } until && _offerLive)
        {
            // b. The going-for-it window: entering the hit area starts the hold with the ring still
            // where it was pinned; leaving the corridor (and the zone) releases on that frame, and so
            // does running out of window T (a snap).
            double d = gaze.DistanceTo(_offerCenter);
            if (d <= _offerHitRadiusPx)
            {
                GoEnd(RingGoEndReason.Hit, gaze, ts);
                StartHold(ts, RingHoldStartKind.Approach, inPlace: false);
            }
            else if (!InGo(gaze) || ts >= until)
            {
                GoEnd(InGo(gaze) ? RingGoEndReason.Timeout : RingGoEndReason.Left, gaze, ts);
                ReleaseNowWithLog(RingReleaseCause.RunEnd);
            }
        }

        if (_offerLive && IsVisible)
        {
            // c. Holding bookkeeping, then the freeze-before-follow for any other entry.
            double d = gaze.DistanceTo(_offerCenter);
            if (_holding)
            {
                if (d <= _offerHitRadiusPx)
                {
                    _outsideSinceUs = null;
                }
                else
                {
                    _outsideSinceUs ??= ts;
                    bool proven = _inHitCount >= HybridFreezeProvenSamples;
                    if (!proven && d > noMoveZonePx)
                        // A fly-through leaving the no-move zone lets go at once; a slip that stays
                        // inside it is tolerated like a proven hold's excursion (the button's own
                        // dwell only pauses there).
                        EndHold(RingHoldEndReason.Unproven, release: true);
                    else if (ts - _outsideSinceUs.Value >= HybridFreezeGraceMs * 1000L)
                        EndHold(proven ? RingHoldEndReason.Left : RingHoldEndReason.Unproven, release: true);
                }
            }
            else if (_approachUntilUs is null && d <= _offerHitRadiusPx)
            {
                StartHold(ts, RingHoldStartKind.Freeze, inPlace: true);
            }
        }

        // d. The proven counter: a hold starts at 0 and counts its start sample.
        if (_holding && _offerLive && gaze.DistanceTo(_offerCenter) <= _offerHitRadiusPx)
            _inHitCount++;
    }

    private void StartHold(long ts, RingHoldStartKind kind, bool inPlace)
    {
        _holding = true;
        _approachUntilUs = null;
        _outsideSinceUs = null;
        _holdStartUs = ts;
        _inHitCount = 0;
        if (inPlace)
        {
            // The freeze: cancel any glide and stay exactly on the drawn point.
            _gliding = false;
            IsPinned = true;
            Position = _drawn;
        }

        RingHoldStarted?.Invoke(new RingHoldStartEvent(kind));
    }

    private void EndHold(RingHoldEndReason reason, bool release)
    {
        _holding = false;
        _outsideSinceUs = null;
        RingHoldEnded?.Invoke(new RingHoldEndEvent(reason, (_lastFrameUs - _holdStartUs) / 1000.0));
        if (release)
            ReleaseNowWithLog(RingReleaseCause.HoldEnd);
    }

    /// <summary>The release itself: cancel any glide and snap to the latest gaze.</summary>
    private void ReleaseNow()
    {
        _gliding = false;
        IsPinned = false;
        Position = _latestGaze;
    }

    /// <summary>A release the look's log reports, if a pin was actually up, with the drawn-to-gaze
    /// distance it snapped.</summary>
    private void ReleaseNowWithLog(RingReleaseCause cause)
    {
        bool wasPinned = IsPinned;
        double dist = _drawn.DistanceTo(_latestGaze);
        ReleaseNow();
        if (wasPinned)
            RingReleased?.Invoke(new RingReleaseEvent(cause, dist));
    }

    // The drawn point tracks BOTH channels: the freeze must stop where the ring actually is, and
    // mid-glide that is the last MoveTo frame, not Position.
    partial void OnPositionChanged(PhysicalPoint value) => _drawn = value;
    partial void OnMoveToChanged(RingMove value) => _drawn = value.Target;

    private void AdvanceGlide(long timestampUs)
    {
        if (_glideStartUs < 0)
        {
            // The first sample at or after the pin: it starts the clock and draws the glide's start.
            _glideStartUs = timestampUs;
            MoveTo = new RingMove(_glideFrom, Glide: true);
            return;
        }

        double elapsedMs = (timestampUs - _glideStartUs) / 1000.0;
        if (elapsedMs >= RingGlide.DurationMs)
        {
            // Landed: the ordinary Position channel draws the exact pin (and is instrumented).
            _gliding = false;
            Position = _glideTo;
            return;
        }

        MoveTo = new RingMove(RingGlide.At(_glideFrom, _glideTo, elapsedMs), Glide: true);
    }

    private void UpdateVisibility()
    {
        var wasVisible = IsVisible;
        var nowVisible = _stateAllowsCursor && !_externalDwellActive;

        // A ring that reappears after a hidden stretch draws where the gaze is NOW. The hidden
        // window keeps its last drawn Left/Top, so without this the first shown frame presents
        // the position from before the stretch — minutes old when the stretch was a pause. The
        // pin is exempt: a pinned ring must stay exactly where it is until Core releases it.
        // Position is written BEFORE IsVisible so the coordinator's Show lands on a window that
        // is already at the live gaze — same order as the activation halo (position, then show),
        // and for the same reason: the other order presents the previous position for a frame.
        if (!wasVisible && nowVisible && !IsPinned)
            Position = _latestGaze;

        IsVisible = nowVisible;

        // A hidden ring cannot hold: the hybrid drops the deferred run-end and the approach, and the
        // hold ends. A hiding STATE has already snapped the pin above, so the owed release below is a
        // no-op there; an EXTERNAL DWELL's hide changes no state, so without this release the pin
        // strands at the old hold point until Core's next release (fixup 01 F2).
        if (!nowVisible && RingHoldPolicy == RingHoldPolicy.HybridFreeze)
        {
            bool releaseOwed = _holding || _approachUntilUs is not null || _pendingRunEndRelease;
            bool hadHold = _holding;
            _pendingRunEndRelease = false;
            if (_approachUntilUs is not null)
                GoEnd(RingGoEndReason.Hidden, _latestGaze, _lastFrameUs);
            if (hadHold)
                EndHold(RingHoldEndReason.Hidden, release: true);
            else if (releaseOwed)
                ReleaseNowWithLog(RingReleaseCause.RunEnd);
        }
    }
}
