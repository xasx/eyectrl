namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Models;

/// <summary>
/// One activation offer: the immutable record of "a button is offered at <see cref="Button"/>, and it
/// is good for a dwell at the point the user is holding" (activation plan rev 3.1). Swapped, never
/// mutated — the engine replaces it with a copy when a later sample extends its expiry — so any reader
/// sees a consistent offer.
/// </summary>
/// <param name="Id">The offer's identity. A completion carrying a stale id is rejected.
/// </param>
/// <param name="Point">P, the settle centroid: the point the click will land on, the point P and B's
/// zone geometry are measured from, and the point a refixation is "on P's side" of.</param>
/// <param name="Button">B, the button centre the dwell is counted at, in physical pixels.</param>
/// <param name="HitRadiusPx">The dwell's hit radius in physical pixels
/// (<see cref="LayoutConstants.ActivationButtonHitRadiusPx"/>).</param>
/// <param name="ExpiresAtUs">The sample-clock deadline: 1 s after the latest of the offered fixation's
/// end, the last valid sample inside the hit area, and the last sample of a near-miss fixation. Never
/// moves backwards.</param>
public sealed record ActivationOffer(
    long Id,
    PhysicalPoint Point,
    PhysicalPoint Button,
    double HitRadiusPx,
    long ExpiresAtUs);

/// <summary>
/// The activation offer's engine — pure, sample-driven, and deliberately NOT wired into
/// <see cref="InteractionManager"/> in ticket 7 (ticket 8 wires it at the accepted settle).
///
/// <para>
/// Everything it needs arrives as data: samples through <see cref="Update"/>, the accepted settle and
/// the run's end through <see cref="NotifyFixationSettled"/>/<see cref="NotifyFixationEnded"/>, the
/// completion through <see cref="Accept"/>/<see cref="Decline"/>, teardowns through
/// <see cref="Clear"/>. It owns no clock: time is the caller's sample timestamps, which is what makes
/// the dwell, the expiry and the replay deterministic.
/// </para>
///
/// <para>
/// The rules, in order of who wins:
/// </para>
/// <list type="bullet">
/// <item><b>Creation.</b> Only an accepted settle makes an offer, and only when P is outside every
/// chrome zone and the placement function finds a side whose hit area does not intersect one. The
/// offer's B is at <c>ring/2 + 55 DIP</c> from P, on the diagonal away from the screen centre,
/// clamped on screen (see <see cref="ActivationButtonPlacement"/>).</item>
/// <item><b>Replacement.</b> A settle outside the live offer's no-move zone (hit radius + R around B)
/// replaces it. Inside the zone, a settle is classified by where it lands: inside the hit area it is
/// the user on the button and changes nothing (the dwell owns it, and it is never remembered);
/// nearer B than P it is a near miss — it extends the live offer's expiry and never re-offers; on P's
/// side it is a swallowed settle, remembered as the pending re-offer.</item>
/// <item><b>Re-offer.</b> When the live offer expires or is declined, and the pending settle's run is
/// still in Fixation, a new offer is made at the pending point through the placement function and the
/// chrome rule as usual. A pending is cleared by its run's end, by a replacement, and by teardown. An
/// accepted offer does not re-offer it.</item>
/// <item><b>Dwell.</b> Every valid sample inside B's hit area credits valid time, each sample capped at
/// <see cref="FixationDetector.AgeCreditCapMs"/>; a stretch of up to
/// <see cref="OutsidePauseBudgetMs"/> ms outside the hit area (invalid, or valid but elsewhere) pauses
/// it, and a longer stretch resets it to 0. At <see cref="DwellMs"/> it fires once, on
/// <see cref="OfferFired"/>.</item>
/// <item><b>Expiry.</b> One second after the latest of the offered run's end, the last valid in-hit
/// sample, and the last sample of a near-miss run. A sample after the deadline cannot revive the
/// offer.</item>
/// <item><b>The fired lock.</b> Once fired, the offer can no longer be replaced, extended, expired or
/// re-offered over until it is accepted or declined. A rejected completion (a stale id, or nothing
/// fired) counts as a decline, and <see cref="Clear"/> clears even a locked offer, so a rejection can
/// never strand the session.</item>
/// </list>
///
/// <para>
/// Expected call order per sample: <see cref="Update"/> for the raw sample first, then
/// <see cref="NotifyFixationSettled"/> for the same instant if the detector settled on it — the
/// expiry is evaluated before the settle classification, exactly as the production wiring
/// (<c>InteractionManager.NoteGazeSample</c> then the detector's event) will present them.
/// </para>
/// </summary>
public sealed class ActivationOfferEngine
{
    /// <summary>How long an offer survives after its latest activity, on the sample clock.</summary>
    public const int OfferExpiryMs = 1000;

    /// <summary>How long a valid excursion outside the hit area may pause the dwell before it resets —
    /// the same 500 ms budget as ticket 3's gap rule. 0 means any outside sample resets (the replay's
    /// comparison semantics).</summary>
    public const int DefaultOutsidePauseBudgetMs = FixationDetector.GapAbortMs;

    private readonly PhysicalRect _screenBoundsPx;
    private readonly double _scale;
    private readonly double _ringSizeDip;
    private readonly double _fixationRadiusPx;
    private readonly double _hitRadiusPx;

    private PhysicalRect[] _chromeZones = Array.Empty<PhysicalRect>();

    private long _nextOfferId = 1;
    private ActivationOffer? _offer;
    private bool _fired;
    private long _dwellUs;
    private long _lastSampleUs;

    // The longest current stretch with no valid sample inside B's hit area: invalid samples and valid
    // samples elsewhere both count. Null while the gaze is inside.
    private long? _missSinceUs;

    private bool _runActive;
    private bool _nearMissRunActive;
    private PhysicalPoint? _pending;
    private RunRole _runRole = RunRole.None;

    /// <summary>What the current run is to the live offer — it decides what the run's end does to it.</summary>
    private enum RunRole
    {
        /// <summary>No role: the run's samples already extend the offer through the hit-area rule.</summary>
        None,
        /// <summary>The run whose settle created the live offer; its end extends the expiry.</summary>
        Offered,
        /// <summary>A run that settled nearer B than P; its samples extend the expiry.</summary>
        NearMiss,
        /// <summary>A run that settled on P's side; its end clears it, and only expiry/decline re-offers it.</summary>
        Pending,
    }

    /// <summary>
    /// Creates the engine for one geometry. The geometry is fixed for the engine's lifetime; the
    /// caller constructs a new one if the screen or the ring changes (ticket 8's wiring).
    /// </summary>
    /// <param name="screenBoundsPx">The primary screen's bounds in physical pixels.</param>
    /// <param name="scale">The live DPI scale.</param>
    /// <param name="ringSizeDip">The cursor ring's size in DIP.</param>
    /// <param name="dwellMs">The effective activation dwell, from the effective-dwell map.</param>
    /// <param name="fixationRadiusPx">The live fixation radius in physical pixels; the no-move zone is
    /// hit radius + this around B.</param>
    /// <param name="hitRadiusPx">The dwell's hit radius in physical pixels.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a non-positive scale, radius or dwell,
    /// or for a non-finite geometry value.</exception>
    public ActivationOfferEngine(
        PhysicalRect screenBoundsPx,
        double scale,
        double ringSizeDip,
        int dwellMs,
        double fixationRadiusPx,
        double hitRadiusPx = LayoutConstants.ActivationButtonHitRadiusPx)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive and finite.");
        if (!double.IsFinite(fixationRadiusPx) || fixationRadiusPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(fixationRadiusPx), fixationRadiusPx, "Fixation radius must be positive and finite.");
        if (!double.IsFinite(hitRadiusPx) || hitRadiusPx <= 0)
            throw new ArgumentOutOfRangeException(nameof(hitRadiusPx), hitRadiusPx, "Hit radius must be positive and finite.");
        if (dwellMs < 0)
            throw new ArgumentOutOfRangeException(nameof(dwellMs), dwellMs, "Dwell must not be negative.");

        _screenBoundsPx = screenBoundsPx;
        _scale = scale;
        _ringSizeDip = ringSizeDip;
        DwellMs = dwellMs;
        _fixationRadiusPx = fixationRadiusPx;
        _hitRadiusPx = hitRadiusPx;
    }

    /// <summary>The live offer, or null when none is up. Read by the ViewModel side (through the
    /// wiring's own messages) and by the replay.</summary>
    public ActivationOffer? CurrentOffer => _offer;

    /// <summary>The effective activation dwell in ms. Settable in place: adaptation's dwell knob must
    /// reach a live offer without rebuilding the engine.</summary>
    public int DwellMs { get; set; }

    /// <summary>How long an outside stretch may pause the dwell. 0 = reset on any outside sample; the
    /// replay's reset-semantics comparison sets it to 0.</summary>
    public int OutsidePauseBudgetMs { get; set; } = DefaultOutsidePauseBudgetMs;

    /// <summary>Dwell progress of the live offer, 0.0–1.0; 0 when none is up. Published for the
    /// halo (ticket 8 puts it on the GazeFrame) and read by the replay.</summary>
    public double DwellProgress
    {
        get
        {
            if (_offer is null) return 0.0;
            if (_fired) return 1.0;
            if (DwellMs <= 0) return 0.0;
            return Math.Min(1.0, (double)_dwellUs / (DwellMs * 1000L));
        }
    }

    /// <summary>Whether the live offer's dwell has completed and it is waiting for accept or decline.</summary>
    public bool IsFired => _fired;

    /// <summary>Fired once per offer when its dwell completes, carrying the fired offer (id and P for
    /// the completion snapshot, B and the hit radius for the drawn feedback). Handlers run on the
    /// caller's thread (the gaze thread once wired).</summary>
    public event Action<ActivationOffer>? OfferFired;

    /// <summary>
    /// The EyeCtrl chrome zones, in physical pixels: the offer is never made over them, and a button
    /// whose hit area would intersect one is flipped once, then dropped. Plain data, pushed by the app
    /// on every chrome change (toolbar, keyboard, prediction bar, menus) — the same pattern as
    /// <c>SetRepeatSuppressionZones</c>.
    /// </summary>
    public void SetChromeZones(params PhysicalRect[] zones)
        => _chromeZones = zones ?? Array.Empty<PhysicalRect>();

    /// <summary>
    /// An accepted settle: creates, replaces, extends or remembers the offer, per the rules in the
    /// class doc. Call it for every settle the InteractionManager accepts (state Fixation), after
    /// <see cref="Update"/> for the same instant.
    /// </summary>
    public void NotifyFixationSettled(PhysicalPoint point, long timestampUs)
    {
        _runActive = true;
        _nearMissRunActive = false;

        if (_fired)
        {
            // The fired lock: not replaced, not extended, not re-offered over. The run still exists
            // (its end must be recorded), but it changes nothing about the offer.
            _runRole = RunRole.None;
            return;
        }

        if (_offer is not null)
        {
            double distanceToButton = point.DistanceTo(_offer.Button);
            if (distanceToButton <= _offer.HitRadiusPx)
            {
                // The user is on the button: the dwell owns it, and it must never be remembered — a
                // declined on-button dwell would otherwise re-offer at the old button's own spot (R1).
                _runRole = RunRole.None;
                return;
            }

            if (distanceToButton <= _offer.HitRadiusPx + _fixationRadiusPx)
            {
                if (distanceToButton < point.DistanceTo(_offer.Point))
                {
                    // Nearer B than P: a near miss. It extends the live offer (its samples count
                    // toward expiry) and never becomes a pending re-offer (decision 10) — the button
                    // must not walk away from an attempt.
                    _runRole = RunRole.NearMiss;
                    _nearMissRunActive = true;
                    ExtendExpiry(timestampUs);
                }
                else
                {
                    // On P's side, inside the no-move zone: swallowed. Remembered so the user is not
                    // stranded next to an expiring button (decision 7).
                    _runRole = RunRole.Pending;
                    _pending = point;
                }
                return;
            }

            // Outside the no-move zone: a fresh settle elsewhere replaces the live offer.
        }

        MakeOffer(point, timestampUs);
    }

    /// <summary>
    /// The current run left Fixation. Extends the live offer's expiry when the run was the offered or
    /// a near-miss run, and clears the pending re-offer (a re-offer needs its run still holding).
    /// </summary>
    public void NotifyFixationEnded(long timestampUs)
    {
        if ((_runRole is RunRole.Offered or RunRole.NearMiss) && _offer is not null && !_fired)
            ExtendExpiry(timestampUs);

        if (_runRole == RunRole.Pending)
            _pending = null;

        _runRole = RunRole.None;
        _runActive = false;
        _nearMissRunActive = false;
    }

    /// <summary>
    /// One sample: raw validity and the filtered point. Drives the expiry, then the dwell. Feed every
    /// sample (including invalid ones) — the pause budget is measured across them.
    /// </summary>
    public void Update(PhysicalPoint point, long timestampUs, bool isValid)
    {
        if (_offer is null)
        {
            _lastSampleUs = timestampUs;
            return;
        }

        if (!_fired && timestampUs > _offer.ExpiresAtUs)
        {
            // Past the deadline: the offer dies before this sample is allowed to touch it. A pending
            // re-offer whose run still holds takes its place at once (decision 7).
            ExpireOrDecline(timestampUs);
            _lastSampleUs = timestampUs;
            if (_offer is null) return;
        }

        if (!_fired)
        {
            // The offered fixation is still running: its end has not happened yet, and the expiry is
            // 1 s after that end — so every sample of the run pushes the deadline out. Without this an
            // offer for a hold longer than 1 s would die underneath the user while they still hold.
            if (_runRole == RunRole.Offered)
                ExtendExpiry(timestampUs);

            if (isValid && point.DistanceTo(_offer.Button) <= _offer.HitRadiusPx)
            {
                ExtendExpiry(timestampUs);
                ResumeDwell(timestampUs);
                if (_dwellUs >= DwellMs * 1000L)
                {
                    _fired = true;
                    OfferFired?.Invoke(_offer);
                }
            }
            else
            {
                if (isValid && _nearMissRunActive)
                    ExtendExpiry(timestampUs);
                PauseDwell(timestampUs);
            }
        }

        _lastSampleUs = timestampUs;
    }

    /// <summary>
    /// Accepts the fired offer. A stale or unknown id — or no fired offer at all — is REJECTED, and a
    /// rejected completion counts as a decline: the fired lock must never strand the session (R3).
    /// </summary>
    public void Accept(long offerId)
    {
        if (_offer is null || !_fired || _offer.Id != offerId)
        {
            Decline();
            return;
        }

        ClearOfferState();
        _pending = null;   // the activation completed: nothing re-offers for this session
        _runRole = RunRole.None;
    }

    /// <summary>
    /// Declines the live offer (fired or not), and re-offers the pending settle at once when its run
    /// still holds.
    /// </summary>
    public void Decline() => ExpireOrDecline(_lastSampleUs);

    /// <summary>
    /// A teardown (pause, keyboard, repeat mode, external dwell, calibration, reset): clears the offer
    /// — even a locked one — the pending, and the run bookkeeping. Mutating this from the dispatcher
    /// while the gaze thread is feeding it is the caller's problem: the production wiring defers it
    /// onto the gaze thread (ticket 8's volatile-request pattern).
    /// </summary>
    public void Clear()
    {
        ClearOfferState();
        _pending = null;
        _runRole = RunRole.None;
        _runActive = false;
        _nearMissRunActive = false;
    }

    private void MakeOffer(PhysicalPoint point, long timestampUs)
    {
        _pending = null;
        _nearMissRunActive = false;
        _runRole = RunRole.None;

        if (InChrome(point))
            return;

        var placement = ActivationButtonPlacement.Place(
            point, _ringSizeDip, LayoutConstants.ActivationButtonOffset, _scale, _hitRadiusPx,
            _screenBoundsPx, _chromeZones);
        if (!placement.Placed)
            return;

        _offer = new ActivationOffer(
            _nextOfferId++, point, placement.ButtonCenter, _hitRadiusPx,
            timestampUs + (OfferExpiryMs * 1000L));
        _fired = false;
        _dwellUs = 0;
        _missSinceUs = null;
        _runRole = RunRole.Offered;
    }

    // The offer is gone: either the deadline passed or the caller declined. The pending re-offer takes
    // its place only while its run still holds.
    private void ExpireOrDecline(long timestampUs)
    {
        if (_offer is null)
            return;
        ClearOfferState();

        if (_pending is { } pending && _runActive)
        {
            _pending = null;
            MakeOffer(pending, timestampUs);
        }
    }

    private void ClearOfferState()
    {
        _offer = null;
        _fired = false;
        _dwellUs = 0;
        _missSinceUs = null;
    }

    private void ExtendExpiry(long timestampUs)
    {
        long deadline = timestampUs + (OfferExpiryMs * 1000L);
        if (deadline > _offer!.ExpiresAtUs)
            _offer = _offer with { ExpiresAtUs = deadline };
    }

    private void ResumeDwell(long timestampUs)
    {
        long dt = timestampUs - _lastSampleUs;
        _missSinceUs = null;
        if (dt > 0)
            _dwellUs += Math.Min(dt, FixationDetector.AgeCreditCapMs * 1000L);
    }

    private void PauseDwell(long timestampUs)
    {
        _missSinceUs ??= timestampUs;
        if (timestampUs - _missSinceUs > OutsidePauseBudgetMs * 1000L)
            _dwellUs = 0;
    }

    private bool InChrome(PhysicalPoint point)
    {
        for (int i = 0; i < _chromeZones.Length; i++)
        {
            if (_chromeZones[i].Contains(point.X, point.Y))
                return true;
        }
        return false;
    }
}
