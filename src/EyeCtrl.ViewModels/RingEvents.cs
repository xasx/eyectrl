namespace EyeCtrl.ViewModels;

using EyeCtrl.Core.Models;

/// <summary>
/// The ring hold policy of <see cref="CursorCircleViewModel"/> (the ring-arm look build,
/// spec: ring-stutter-investigation/replay-2, "Where the arms live"). Set once by the App from the
/// <c>--ring-arm</c> launch argument; never a setting.
/// </summary>
public enum RingHoldPolicy
{
    /// <summary>The shipped behaviour: the ring sits at whatever Core's pin put it on and follows
    /// gaze again on Core's release. Called "today" by the look.</summary>
    None,

    /// <summary>The look's hybrid: within a live offer's hit area the ring stops where it is drawn
    /// (or keeps the pin it was on) and holds, ignoring settles and run ends; it releases on the
    /// grace expiry once proven, on the first sample outside before that, on the offer's close, and
    /// on a hiding state. See the constants on <see cref="CursorCircleViewModel"/>.</summary>
    HybridFreeze,
}

/// <summary>Which of the hybrid's three hold starts fired.</summary>
public enum RingHoldStartKind
{
    /// <summary>The deferred run-end release landed inside the hit area: the hold kept the existing pin.</summary>
    Keep,

    /// <summary>The eye crossed into the hit area during the <c>HybridFreezeApproachMs</c> window.</summary>
    Approach,

    /// <summary>Any other entry of the eye into the hit area: the ring froze where it was drawn.</summary>
    Freeze,
}

/// <summary>Why a hybrid hold ended.</summary>
public enum RingHoldEndReason
{
    /// <summary>The gaze sat outside the hit area for the grace, having proven itself first.</summary>
    Left,

    /// <summary>The gaze left the hit area before the hold had proven itself (&lt; 3 samples).</summary>
    Unproven,

    /// <summary>The live offer closed.</summary>
    OfferClosed,

    /// <summary>A hiding state (or the external dwell) took the ring away.</summary>
    Hidden,
}

/// <summary>What caused a ring release, for the look's readout (<c>run-end</c> etc. in the log).</summary>
public enum RingReleaseCause
{
    /// <summary>The run's end (or, in the hybrid, its deferred resolution at the next sample).</summary>
    RunEnd,

    /// <summary>The live offer closed (an approach-window clear; the shipped offer-following release).</summary>
    OfferClosed,

    /// <summary>A hybrid hold ended with a release (grace expiry, unproven exit, offer close).</summary>
    HoldEnd,

    /// <summary>A hiding state cleared the pin.</summary>
    HidingState,
}

/// <summary>
/// One processed <c>PinRingMessage</c>, applied or refused — the look build's event log uses the
/// applied ones (with <see cref="Dist"/>) and maps the pair's Core-side origin from its own
/// subscription. <see cref="Dist"/> is unused when not applied.
/// </summary>
public readonly record struct RingPinProcessedEvent(PhysicalPoint Target, double Dist, bool Applied);

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

/// <summary>A hybrid hold ended, with how long it lasted (the message clock; end-of-run ends use the
/// last delivered sample).</summary>
public readonly record struct RingHoldEndEvent(RingHoldEndReason Reason, double HeldMs);
