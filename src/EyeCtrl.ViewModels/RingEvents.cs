namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// The ring hold policy of <see cref="CursorCircleViewModel"/> (the ring-arm look build,
/// spec: ring-stutter-investigation/replay-2, "Where the arms live"). Set once by the App from the
/// <c>--ring-arm</c> launch argument; never a setting.
/// </summary>
public enum RingHoldPolicy
{
    /// <summary>The original behaviour: the ring sits at whatever Core's pin put it on and follows
    /// gaze again on Core's release. Called "today" by the look; since the owner's look, the launch
    /// default is <see cref="HybridFreeze"/> and this is a diagnostic arm
    /// (<c>--ring-arm=today</c>). The class default stays None — the App's launch composition is
    /// the only place an arm is selected.</summary>
    None,

    /// <summary>The look's hybrid — the launch default since the owner's look picked it (2026-10-16):
    /// while a button is up, a run's pin at P STAYS while the gaze is on, near or on its way to the
    /// button (the stay zone, 5th sitting 2026-11-09), and lets go only after the gaze has been clearly
    /// elsewhere for the grace, on the offer's close, or on a settle elsewhere; a FOLLOWING ring that
    /// reaches the hit area stops where it is drawn and holds (the freeze), ignoring settles and run
    /// ends, until its own grace (an unproven hold also on leaving the no-move zone), the offer's close
    /// or a hiding state. See the constants on <see cref="CursorCircleViewModel"/>.</summary>
    HybridFreeze,
}

/// <summary>Which hold start fired. Since the stay zone (F2), the run's pin at P is kept by the stay
/// (<see cref="RingStayStartEvent"/>), so the only hold left is the freeze.</summary>
public enum RingHoldStartKind
{
    /// <summary>A following ring's gaze entered the hit area: the ring froze where it was drawn.</summary>
    Freeze,
}

/// <summary>Why a hybrid hold ended.</summary>
public enum RingHoldEndReason
{
    /// <summary>The gaze sat outside the hit area for the grace, having proven itself first.</summary>
    Left,

    /// <summary>An unproven hold (&lt; 3 in-hit samples) that left the no-move zone at once, or sat outside the hit area for the grace.</summary>
    Unproven,

    /// <summary>The live offer closed.</summary>
    OfferClosed,

    /// <summary>A hiding state (or the external dwell) took the ring away.</summary>
    Hidden,
}

/// <summary>What caused a ring release, for the look's readout (<c>run-end</c> etc. in the log).</summary>
public enum RingReleaseCause
{
    /// <summary>The run's end (or, in the hybrid, its deferred resolution: at the next sample, or when
    /// the stay ends because the gaze left the zone or the ring was hidden).</summary>
    RunEnd,

    /// <summary>The live offer closed (the stay's end; the shipped offer-following release).</summary>
    OfferClosed,

    /// <summary>A hybrid hold ended with a release (grace expiry, unproven exit, offer close).</summary>
    HoldEnd,

    /// <summary>A hiding state cleared the pin.</summary>
    HidingState,
}

/// <summary>
/// One processed <c>PinRingMessage</c>, applied or refused — the look build's event log uses the
/// applied ones (with <see cref="Dist"/>) and maps the pair's Core-side origin from its own
/// subscription. <see cref="Dist"/> is unused when not applied. <see cref="IgnoredNearOffer"/> is set
/// only when the pin was refused because it is a near-miss settle of a live offer (the VM's refusal D),
/// not for a hold-suppressed or non-fixation refusal.
/// </summary>
public readonly record struct RingPinProcessedEvent(PhysicalPoint Target, double Dist, bool Applied,
    bool IgnoredNearOffer = false);

/// <summary>
/// One processed <c>ReleaseRingMessage</c>. In the shipped hold the release happens at the message
/// (<see cref="ReleasedNow"/> = whether a pin was up); in the hybrid the message only sets the
/// deferred run-end release and the physical release follows at a later sample
/// (<see cref="RingReleaseEvent"/>, <see cref="RingReleaseCause.RunEnd"/>).
/// </summary>
public readonly record struct RingReleaseProcessedEvent(bool ReleasedNow, double Dist);

/// <summary>A physical ring release that did not happen at a <c>ReleaseRingMessage</c>.</summary>
public readonly record struct RingReleaseEvent(RingReleaseCause Cause, double Dist);

/// <summary>A hybrid hold started.</summary>
public readonly record struct RingHoldStartEvent(RingHoldStartKind Kind);

/// <summary>Why a stay ended (the run-end pin at P kept while a button is up and the gaze is in its
/// stay zone: on or near the button, or on the way between word and button).</summary>
public enum RingStayEndReason
{
    /// <summary>The gaze sat outside the stay zone for the grace (<c>HybridStayGraceMs</c>): the ring
    /// follows.</summary>
    Left,

    /// <summary>The live offer closed (fired, expired, declined, replaced or taken away).</summary>
    OfferClosed,

    /// <summary>A hiding state (or the external dwell) took the ring away.</summary>
    Hidden,

    /// <summary>The eye settled outside the stay zone: the pin there is applied.</summary>
    Settled,
}

/// <summary>A stay began. <see cref="Dist"/> is the drawn ring to the gaze; <see cref="Off"/> is the
/// gaze's distance to the segment P-B (null where the offer's P is unknown).</summary>
public readonly record struct RingStayStartEvent(double Dist, double? Off);

/// <summary>A stay ended, <see cref="Ms"/> after it began (the message clock), with the gaze's distance
/// to the segment P-B at the end.</summary>
public readonly record struct RingStayEndEvent(RingStayEndReason Reason, double Ms, double? Off);

/// <summary>A hybrid hold ended, with how long it lasted (the message clock; end-of-run ends use the
/// last delivered sample).</summary>
public readonly record struct RingHoldEndEvent(RingHoldEndReason Reason, double HeldMs);
