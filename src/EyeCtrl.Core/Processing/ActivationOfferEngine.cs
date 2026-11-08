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
/// <param name="Style">How the offer was placed. Under <see cref="OfferPlacementStyle.AtRestPoint"/>
/// there is no second target: <see cref="Button"/> is the rest centre (the RAW settle point, where
/// the fill is counted), <see cref="HitRadiusPx"/> is the rest area H, and <see cref="Point"/> is the
/// completion point (P′ when the settle was clamped on-screen, otherwise the same point).</param>
public sealed record ActivationOffer(
    long Id,
    PhysicalPoint Point,
    PhysicalPoint Button,
    double HitRadiusPx,
    long ExpiresAtUs,
    OfferPlacementStyle Style = OfferPlacementStyle.Button);

/// <summary>Where an offer's dwell target sits (rest-activation plan, 2026-11-08).</summary>
public enum OfferPlacementStyle
{
    /// <summary>The fallback: a button B beside the ring, placed by
    /// <see cref="ActivationButtonPlacement"/>, dwelt within its hit radius.</summary>
    Button,

    /// <summary>The rest fill: the dwell is counted at the resting point itself, within
    /// <see cref="ActivationOfferEngine.RestAreaFactor"/> × R; the ring fills there.</summary>
    AtRestPoint,
}

/// <summary>What an accepted settle did to the offer — the engine's answer to
/// <see cref="ActivationOfferEngine.NotifyFixationSettled"/>, used by the replay's counts.</summary>
public enum SettleOutcome
{
    /// <summary>No offer: P lay in a chrome zone, no side of the ring was placeable, or the settle
    /// arrived while a fired offer was still locked.</summary>
    NoOffer,

    /// <summary>There was no live offer; one was made for this settle.</summary>
    Created,

    /// <summary>The settle was outside the live offer's no-move zone: a new offer replaced it.</summary>
    Replaced,

    /// <summary>A near miss (nearer B than P, outside the hit area): the live offer's expiry moved.</summary>
    Extended,

    /// <summary>On P's side of the zone: swallowed and remembered as the pending re-offer.</summary>
    Pending,

    /// <summary>Inside B's hit area: the user is on the button, and the dwell owns it.</summary>
    OnButton,

    /// <summary>A fired offer was in flight: the settle changed nothing.</summary>
    Locked,
}

/// <summary>Why no offer was made for an accepted settle (corner fix 2026-10-17, F3).</summary>
public enum ActivationOfferRefusalReason
{
    /// <summary>P is outside the screen beyond <see cref="ActivationOfferEngine.CornerClampTolerancePx"/>
    /// (or in the docked toolbar's summon band, which is never clamped).</summary>
    OffScreen,

    /// <summary>P — or its clamped P′ — lies inside a chrome zone.</summary>
    InChrome,

    /// <summary>Neither side of the ring could hold the button: both intersect chrome, or the
    /// surviving one was clamped onto P (the drop guard).</summary>
    NoSidePlaceable,

    /// <summary>EyeCtrl's own modal surfaces are up: nothing is offered at all.</summary>
    Suspended,

    /// <summary>The rest style's re-arm latch: the last rest offer here fired or was closed by a
    /// command, and the gaze has not left the spot (by more than the rest area) since.</summary>
    Rearm,
}

/// <summary>One refused settle: the RAW settle point and why no offer followed.</summary>
public readonly record struct ActivationOfferRefusal(PhysicalPoint Point, ActivationOfferRefusalReason Reason);

/// <summary>Which screen edge a docked toolbar summons from.</summary>
public enum ActivationSummonEdge
{
    /// <summary>The toolbar is docked at the top edge; the band sits above it.</summary>
    Top,

    /// <summary>The toolbar is docked at the bottom edge; the band sits below it.</summary>
    Bottom,

    /// <summary>The toolbar is docked at the left edge; the band sits to its left.</summary>
    Left,

    /// <summary>The toolbar is docked at the right edge; the band sits to its right.</summary>
    Right,
}

/// <summary>
/// The docked toolbar's summon band (corner fix 2026-10-17, F1's one exception): the off-screen
/// strip past the toolbar's docked edge. A settle there is how the toolbar is summoned, so it is
/// never clamped — a clamped offer would flash before the toolbar slides in. It spans the WHOLE
/// edge, not the toolbar's span along it (fixup 01 R-F1): the auto-hide controller's summon gate
/// fires at any coordinate past the docked edge. Plain data, pushed by the App the way the chrome
/// zones are.
/// </summary>
/// <param name="Edge">The docked edge the band sits past.</param>
public readonly record struct ActivationSummonBand(ActivationSummonEdge Edge)
{
    /// <summary>
    /// The band for a docked edge — <c>ToolbarSettings.DockedEdge</c>'s own vocabulary
    /// ("Top"/"Bottom"/"Left"/"Right", case-insensitive). Null for an unknown edge: there is never
    /// an exception without a real docked toolbar.
    /// </summary>
    public static ActivationSummonBand? FromDockedEdge(string? edge)
    {
        if (edge is null) return null;
        return edge.ToLowerInvariant() switch
        {
            "top" => new(ActivationSummonEdge.Top),
            "bottom" => new(ActivationSummonEdge.Bottom),
            "left" => new(ActivationSummonEdge.Left),
            "right" => new(ActivationSummonEdge.Right),
            _ => null,
        };
    }
}

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
/// offer's B is at <c>ring/2 + 55 DIP</c> from P, on the diagonal toward the screen centre,
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
/// <b>The rest style</b> (<see cref="OfferPlacementStyle.AtRestPoint"/>, the owner's decision of
/// 2026-11-08) keeps creation's chrome, summon-band, suspension, clamp and refusal rules but has no
/// second target: the dwell is counted at the RAW settle point within H =
/// <see cref="RestAreaFactor"/> × R, the completion lands at P (P′ when clamped), and both the pause
/// budget and the expiry are <see cref="RestFillGraceMs"/> after the last in-area sample — nothing
/// else extends it, so there is no linger. A settle within H of the live offer is the same rest and
/// changes nothing; one beyond H replaces it. There is no near miss, no pending and no re-offer. A
/// fired offer, or one closed by a command rather than by looking away (<see cref="Clear"/>,
/// <see cref="Decline"/>, suspension, chrome re-validation, or a rebuild through
/// <see cref="SeedRearmLatch"/>), sets the re-arm latch: a settle within H of that spot is refused
/// (<see cref="ActivationOfferRefusalReason.Rearm"/>) until a valid sample has left it by more than H —
/// a sample the wiring took in FreeLook or Fixation; the magnifier's, menus' and modes' samples never
/// clear it (see <see cref="Update"/>).
/// </para>
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
    /// <summary>How long an offer survives after its latest activity, on the sample clock, by default.
    /// Settable per engine (review B's linger lever); the replay walks this lever.</summary>
    public const int DefaultOfferExpiryMs = 1000;

    /// <summary>The linger the product runs with: decision 3's 0.75 s, set by the user on 2026-10-01
    /// from the ticket 7 replay report. The wiring (<c>GazeEngine.ConfigureActivationOffers</c>) sets
    /// it on every engine it builds; the default stays at 1 s so the replay's lever table keeps its
    /// documented baseline.</summary>
    public const int ProductOfferExpiryMs = 750;

    /// <summary>How long a valid excursion outside the hit area may pause the dwell before it resets —
    /// the same 500 ms budget as ticket 3's gap rule. 0 means any outside sample resets (the replay's
    /// comparison semantics).</summary>
    public const int DefaultOutsidePauseBudgetMs = FixationDetector.GapAbortMs;

    /// <summary>
    /// The corner fix's clamp tolerance T, in physical px (2026-10-17): at the screen corners the
    /// tracker reports the settle up to ~190 px past the bezel (the investigation's maximum was 187),
    /// so a P within T outside the screen is clamped on-screen instead of refused. Beyond T refusal
    /// stands — no completion may land far off-screen (review B F4's intent).
    /// </summary>
    public const double CornerClampTolerancePx = 200.0;

    /// <summary>The rest style's area H as a multiple of the engine's R: the gaze may wander up to
    /// 1.5 R from the resting point while the ring fills (the owner's Q3 answer, 2026-11-08).</summary>
    public const double RestAreaFactor = 1.5;

    /// <summary>The rest style's grace G in ms: a blink or a glance away pauses the fill for up to
    /// this long, and the offer expires this long after its last in-area sample (the owner's Q2
    /// answer, 2026-11-08).</summary>
    public const int RestFillGraceMs = 500;

    private readonly PhysicalRect _screenBoundsPx;
    private readonly double _scale;
    private readonly double _ringSizeDip;
    private readonly double _hitRadiusPx;
    private double _fixationRadiusPx;

    private PhysicalRect[] _chromeZones = Array.Empty<PhysicalRect>();

    // The docked toolbar's summon band, or null (floating toolbar, no toolbar): the one off-screen
    // strip a settle is still refused in (corner fix F1).
    private ActivationSummonBand? _summonBand;

    // The last refusal reason raised, or null since the last offer was made: one OfferRefused per
    // change of reason, never per settle (corner fix F3).
    private ActivationOfferRefusalReason? _lastRefusalReason;

    private long _nextOfferId = 1;
    private ActivationOffer? _offer;
    private bool _fired;
    private long _dwellUs;
    // The closest the live offer's dwell ever got, across the resets decision 9 allows. The expiry
    // report needs it: by the time an untouched offer expires, the last look-away has always reset
    // the CURRENT dwell to 0 (the reset budget is shorter than the linger), so the current value
    // would report every expired attempt as zero.
    private long _peakDwellUs;
    private long _lastSampleUs;

    // The longest current stretch with no valid sample inside B's hit area: invalid samples and valid
    // samples elsewhere both count. Null while the gaze is inside.
    private long? _missSinceUs;

    private bool _runActive;
    private bool _nearMissRunActive;
    private PhysicalPoint? _pending;
    private RunRole _runRole = RunRole.None;
    private int _offerExpiryMs = DefaultOfferExpiryMs;

    // The rest style's re-arm latch L, or null: the rest centre of the last rest offer that fired or
    // was closed by a command. Cleared by the first valid sample STRICTLY farther than H from it: the
    // spot is inclusive (a sample or a settle at exactly H is still on it), like the fill's own area.
    private PhysicalPoint? _rearmLatch;

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
    /// <param name="hitRadiusPx">The dwell's hit radius in physical pixels (Button style only; the
    /// rest style's area is <see cref="RestAreaFactor"/> × R).</param>
    /// <param name="style">Where the dwell target sits. <see cref="OfferPlacementStyle.AtRestPoint"/>
    /// also sets <see cref="OutsidePauseBudgetMs"/> and <see cref="OfferExpiryMs"/> to
    /// <see cref="RestFillGraceMs"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for a non-positive scale, radius or dwell,
    /// or for a non-finite geometry value.</exception>
    public ActivationOfferEngine(
        PhysicalRect screenBoundsPx,
        double scale,
        double ringSizeDip,
        int dwellMs,
        double fixationRadiusPx,
        double hitRadiusPx = LayoutConstants.ActivationButtonHitRadiusPx,
        OfferPlacementStyle style = OfferPlacementStyle.Button)
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
        Style = style;
        if (style == OfferPlacementStyle.AtRestPoint)
        {
            // The grace is both the pause budget and the expiry: past it the fill is lost and the
            // offer closes (= cancel). No linger.
            OutsidePauseBudgetMs = RestFillGraceMs;
            _offerExpiryMs = RestFillGraceMs;
        }
    }

    /// <summary>Where the dwell target sits, fixed for the engine's lifetime: a style change builds a
    /// new engine, as a ring-size change does.</summary>
    public OfferPlacementStyle Style { get; }

    /// <summary>The rest style's area H in physical px: <see cref="RestAreaFactor"/> × the live R.</summary>
    public double RestAreaRadiusPx => RestAreaFactor * _fixationRadiusPx;

    /// <summary>The re-arm latch's point L, or null when none is set (always null in Button style).</summary>
    public PhysicalPoint? RearmLatch => _rearmLatch;

    private bool IsRestStyle => Style == OfferPlacementStyle.AtRestPoint;

    /// <summary>The live offer, or null when none is up. Read by the ViewModel side (through the
    /// wiring's own messages) and by the replay.</summary>
    public ActivationOffer? CurrentOffer => _offer;

    /// <summary>The primary screen's bounds in physical pixels the engine was built for. Exposed for
    /// the wiring's configuration log and for tests (review A, A9).</summary>
    public PhysicalRect ScreenBoundsPx => _screenBoundsPx;

    /// <summary>The live DPI scale the engine was built for. Exposed for the configuration log.</summary>
    public double Scale => _scale;

    /// <summary>The cursor ring size in DIP the placement follows. Exposed for the configuration log.</summary>
    public double RingSizeDip => _ringSizeDip;

    /// <summary>
    /// Whether offers are suspended entirely (EyeCtrl's own modal surfaces — Settings, the task
    /// switcher, the quick-actions grid, the eye-care banner — are open; the user decided on
    /// 2026-10-02 that nothing may be offered over them). Gaze-thread state: the wiring requests a
    /// change and the gaze thread applies it through <see cref="SetOffersSuspended"/>.
    /// </summary>
    public bool OffersSuspended { get; private set; }

    /// <summary>The effective activation dwell in ms. Settable in place: adaptation's dwell knob must
    /// reach a live offer without rebuilding the engine.</summary>
    public int DwellMs { get; set; }

    /// <summary>How long an outside stretch may pause the dwell. 0 = reset on any outside sample; the
    /// replay's reset-semantics comparison sets it to 0.</summary>
    public int OutsidePauseBudgetMs { get; set; } = DefaultOutsidePauseBudgetMs;

    /// <summary>
    /// The live fixation radius R in physical pixels: the no-move zone is hit radius + this around B.
    /// Settable in place like <see cref="DwellMs"/>, because R is a live setting and an adaptation
    /// knob — ticket 8's wiring must not have to rebuild the engine (and drop a live offer) to follow
    /// it (review A, ticket-8 nit).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive and finite.</exception>
    public double FixationRadiusPx
    {
        get => _fixationRadiusPx;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Fixation radius must be positive and finite.");
            _fixationRadiusPx = value;
            // The rest area follows R in place: a live rest offer's counted area is H for the R in force.
            if (IsRestStyle && _offer is not null)
                _offer = _offer with { HitRadiusPx = RestAreaRadiusPx };
        }
    }

    /// <summary>
    /// How long an offer survives after its latest activity, in ms (decision 3's linger). Settable like
    /// <see cref="DwellMs"/>: the replay's lever table moves it, and the live value may move with the
    /// user's read-out.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int OfferExpiryMs
    {
        get => _offerExpiryMs;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Expiry must not be negative.");
            _offerExpiryMs = value;
        }
    }

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

    // The offer's closest approach to completion, across the pause resets: what <see cref="OfferExpired"/>
    // reports. A fired offer reads 1.0.
    private double PeakDwellProgress
    {
        get
        {
            if (_fired) return 1.0;
            if (DwellMs <= 0) return 0.0;
            return Math.Min(1.0, (double)_peakDwellUs / (DwellMs * 1000L));
        }
    }

    /// <summary>Whether the live offer's dwell has completed and it is waiting for accept or decline.</summary>
    public bool IsFired => _fired;

    /// <summary>Fired once per offer when its dwell completes, carrying the fired offer (id and P for
    /// the completion snapshot, B and the hit radius for the drawn feedback). Handlers run on the
    /// caller's thread (the gaze thread once wired).</summary>
    public event Action<ActivationOffer>? OfferFired;

    /// <summary>
    /// Fired when an offer's deadline passed with the dwell unfinished — never for an accepted or
    /// declined one, and never for a teardown's <see cref="Clear"/>. Carries the offer that died, the
    /// sample timestamp that found it dead, and the closest dwell fraction the offer ever reached
    /// (its peak across the pause resets — by expiry time the current dwell has always been reset to
    /// 0, since the reset budget is shorter than the linger). This is the "offers that expired
    /// unused" half of ticket 8's button-dwell telemetry. Handlers run on the caller's thread.</summary>
    public event Action<ActivationOffer, long, double>? OfferExpired;

    /// <summary>
    /// Raised when an accepted settle got no offer, once per CHANGE of
    /// <see cref="ActivationOfferRefusalReason"/> — an offer made in between resets the latch, so
    /// the next refusal of any reason raises again. Never per settle and never per sample: the
    /// App's adapter logs it as the corner investigation's "Offer refused at P=…: reason" line.
    /// Carries the RAW settle point. Handlers run on the caller's thread (the gaze thread once
    /// wired).
    /// </summary>
    public event Action<ActivationOfferRefusal>? OfferRefused;

    /// <summary>
    /// The EyeCtrl chrome zones, in physical pixels: the offer is never made over them, and a button
    /// whose hit area would intersect one is flipped once, then dropped. Plain data, pushed by the app
    /// on every chrome change (toolbar, keyboard, prediction bar, menus) — the same pattern as
    /// <c>SetRepeatSuppressionZones</c>.
    /// </summary>
    public void SetChromeZones(params PhysicalRect[] zones)
        => _chromeZones = zones ?? Array.Empty<PhysicalRect>();

    /// <summary>
    /// Publishes the docked toolbar's summon band as plain data, the <see cref="SetChromeZones"/>
    /// pattern: the App pushes it whenever the zone composition runs, and the wiring re-applies it
    /// across an engine rebuild the way the zones are re-applied. Null clears it.
    /// </summary>
    public void SetSummonBand(ActivationSummonBand? band) => _summonBand = band;

    /// <summary>
    /// An accepted settle: creates, replaces, extends or remembers the offer, per the rules in the
    /// class doc. Call it for every settle the InteractionManager accepts (state Fixation), after
    /// <see cref="Update"/> for the same instant.
    /// </summary>
    /// <returns>What the settle did, for the replay's counts (and the wiring's telemetry later).</returns>
    public SettleOutcome NotifyFixationSettled(PhysicalPoint point, long timestampUs)
    {
        _runActive = true;
        _nearMissRunActive = false;
        if (timestampUs > _lastSampleUs)
            _lastSampleUs = timestampUs;   // the settle sample is a sample: a later gap is measured from it

        if (_fired)
        {
            // The fired lock: not replaced, not extended, not re-offered over. The run still exists
            // (its end must be recorded), but it changes nothing about the offer.
            _runRole = RunRole.None;
            return SettleOutcome.Locked;
        }

        if (IsRestStyle)
        {
            // No second target: a settle within H of the rest centre is the same rest — the fill owns
            // it, and it must neither re-pin the ring nor restart the fill (OnButton, since B is the
            // rest point). Beyond H it replaces the offer below.
            if (_offer is not null && point.DistanceTo(_offer.Button) <= _offer.HitRadiusPx)
            {
                _runRole = RunRole.None;
                return SettleOutcome.OnButton;
            }
        }
        else if (_offer is not null)
        {
            double distanceToButton = point.DistanceTo(_offer.Button);
            if (distanceToButton <= _offer.HitRadiusPx)
            {
                // The user is on the button: the dwell owns it, and it must never be remembered — a
                // declined on-button dwell would otherwise re-offer at the old button's own spot (R1).
                _runRole = RunRole.None;
                return SettleOutcome.OnButton;
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
                    return SettleOutcome.Extended;
                }

                // On P's side, inside the no-move zone: swallowed. Remembered so the user is not
                // stranded next to an expiring button (decision 7).
                _runRole = RunRole.Pending;
                _pending = point;
                return SettleOutcome.Pending;
            }

            // Outside the no-move zone: a fresh settle elsewhere replaces the live offer.
        }

        bool hadOffer = _offer is not null;
        return MakeOffer(point, timestampUs)
            ? (hadOffer ? SettleOutcome.Replaced : SettleOutcome.Created)
            : SettleOutcome.NoOffer;
    }

    /// <summary>
    /// The current run left Fixation. Extends the live offer's expiry when the run was the offered or
    /// a near-miss run, and clears the pending re-offer (a re-offer needs its run still holding).
    /// </summary>
    public void NotifyFixationEnded(long timestampUs)
    {
        // The rest style's expiry moves only with in-area samples: the run's end does not extend it.
        if (!IsRestStyle && (_runRole is RunRole.Offered or RunRole.NearMiss) && _offer is not null && !_fired)
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
    /// <param name="point">The filtered point.</param>
    /// <param name="timestampUs">The sample's timestamp.</param>
    /// <param name="isValid">The raw sample's validity.</param>
    /// <param name="mayClearRearmLatch">Whether this sample may clear the rest style's re-arm latch.
    /// The wiring passes false outside FreeLook and Fixation: in the magnifier, a menu or a mode the
    /// gaze roams far from P without having left it as a resting spot, and a latch cleared there would
    /// let the same rest refill the moment the overlay closes.</param>
    public void Update(PhysicalPoint point, long timestampUs, bool isValid, bool mayClearRearmLatch = true)
    {
        // The re-arm latch lets go once the gaze has really left the spot: one valid sample strictly
        // past H (exactly H is still on the spot — the refusal below uses the same inclusive bound).
        if (_rearmLatch is { } latch && mayClearRearmLatch && isValid && point.DistanceTo(latch) > RestAreaRadiusPx)
            _rearmLatch = null;

        if (_offer is null)
        {
            _lastSampleUs = timestampUs;
            return;
        }

        if (!_fired && timestampUs > _offer.ExpiresAtUs)
        {
            // Past the deadline: the offer dies before this sample is allowed to touch it. A pending
            // re-offer whose run still holds takes its place at once (decision 7). The progress is
            // read BEFORE the death clears the dossier, and the event carries the dead offer by value
            // so a re-offer in the same call cannot alias it.
            var expired = _offer;
            double progressAtExpiry = PeakDwellProgress;
            ExpireOrDecline(timestampUs, closedByCommand: false);
            OfferExpired?.Invoke(expired, timestampUs, progressAtExpiry);
            _lastSampleUs = timestampUs;
            if (_offer is null) return;
        }

        if (!_fired)
        {
            if (isValid && point.DistanceTo(_offer.Button) <= _offer.HitRadiusPx)
            {
                // The offered run's samples extend the deadline while it holds (the in-hit sample
                // itself extends it too, below).
                ExtendExpiry(timestampUs);

                // A silence longer than the pause budget (and longer than a mere frame) is the same
                // evidence as invalid samples: the eyes were not there, so it aborts the dwell and —
                // as in the detector's new run — the resuming sample credits nothing (review A F5 and
                // R2-3's companion nit).
                long silenceUs = timestampUs - _lastSampleUs;
                _missSinceUs = null;
                if (silenceUs > Math.Max(FixationDetector.AgeCreditCapMs * 1000L, OutsidePauseBudgetMs * 1000L))
                    _dwellUs = 0;
                else
                    CreditSample(timestampUs);

                if (_dwellUs >= DwellMs * 1000L)
                {
                    _fired = true;
                    // A fired rest offer latches its spot at once: whatever closes it later (accept,
                    // decline, teardown), the same rest must not refill until the gaze has left.
                    if (IsRestStyle)
                        _rearmLatch = _offer.Button;
                    OfferFired?.Invoke(_offer);
                }
            }
            else
            {
                if (isValid && _nearMissRunActive)
                    ExtendExpiry(timestampUs);

                // The offered run's samples extend the deadline while it holds, even off the button.
                // Not in the rest style: only in-area samples extend it there (expiry = G).
                if (_runRole == RunRole.Offered && !IsRestStyle)
                    ExtendExpiry(timestampUs);

                PauseDwell(timestampUs);
            }
        }

        _lastSampleUs = timestampUs;
    }

    /// <summary>
    /// Accepts the completion for <paramref name="offerId"/>. It acts only on that offer: a stale id (the
    /// offer it names is already gone) does NOTHING — a rejection must never decline whatever happens to
    /// be live now (review A, F2). A matching id is accepted when the offer is fired, and declined when
    /// it is not (a protocol violation; a rejected completion counts as a decline, R3).
    /// </summary>
    public void Accept(long offerId)
    {
        if (_offer is null || _offer.Id != offerId)
            return;

        if (!_fired)
        {
            Decline(offerId);
            return;
        }

        ClearOfferState();
        _pending = null;   // the activation completed: nothing re-offers for this session
        _runRole = RunRole.None;
    }

    /// <summary>
    /// Declines only the offer named by <paramref name="offerId"/>: a stale id does nothing, so a late
    /// decline for an older offer cannot clear the live one (review A, F2). Ticket 8's
    /// <c>DeclineOffer(snapshot)</c> uses this shape; teardowns use <see cref="Clear"/>.
    /// </summary>
    public void Decline(long offerId)
    {
        if (_offer is null || _offer.Id != offerId)
            return;
        ExpireOrDecline(_lastSampleUs, closedByCommand: true);
    }

    /// <summary>
    /// A teardown (pause, keyboard, repeat mode, external dwell, calibration, reset): clears the offer
    /// — even a locked one — the pending, and the run bookkeeping. Mutating this from the dispatcher
    /// while the gaze thread is feeding it is the caller's problem: the production wiring defers it
    /// onto the gaze thread (ticket 8's volatile-request pattern).
    /// </summary>
    public void Clear()
    {
        LatchClosedRestOffer();
        ClearOfferState();
        _pending = null;
        _runRole = RunRole.None;
        _runActive = false;
        _nearMissRunActive = false;
    }

    /// <summary>
    /// Applies the offers-suspended state (gaze thread). While suspended no offer is made, and turning
    /// it on clears a live UNFIRED offer at once — a fired offer is mid-completion and is left for its
    /// accept or decline. See <see cref="OffersSuspended"/>.
    /// </summary>
    public void SetOffersSuspended(bool suspended)
    {
        if (suspended == OffersSuspended)
            return;
        OffersSuspended = suspended;
        if (suspended && _offer is not null && !_fired)
            ExpireOrDecline(_lastSampleUs, closedByCommand: true);
    }

    /// <summary>
    /// Re-checks a live unfired offer against the current chrome zones (gaze thread): chrome that
    /// appeared over a live offer — the text-field keyboard auto-show, the toolbar summon — must take
    /// it away, exactly as a settle there would not have made one. The offer dies through the ordinary
    /// dead-offer path, so a pending re-offer is still re-offered only if its own point survives the
    /// chrome check in <see cref="MakeOffer"/>. A fired offer is left alone: its completion is in
    /// flight.
    /// </summary>
    public void RevalidateChromeZones()
    {
        if (_offer is null || _fired)
            return;
        // The rest style re-checks exactly what creation checked: P′ outside chrome. Its area H has
        // no button to keep clear of chrome, and testing it would close every rest near the toolbar
        // on each chrome publish.
        if (InChrome(_offer.Point)
            || (!IsRestStyle
                && ActivationButtonPlacement.IntersectsAnyZone(_offer.Button, _offer.HitRadiusPx, _chromeZones)))
            ExpireOrDecline(_lastSampleUs, closedByCommand: true);
    }

    /// <summary>
    /// Sets the rest style's re-arm latch at <paramref name="point"/> (gaze thread): the wiring hands
    /// a rebuilt engine the rest centre of the offer the old engine dropped, since a rebuild is a
    /// close by command. Ignored in Button style, which has no latch.
    /// </summary>
    public void SeedRearmLatch(PhysicalPoint point)
    {
        if (IsRestStyle)
            _rearmLatch = point;
    }

    /// <returns>True when an offer was placed; false when offers are suspended, P is off screen, or P
    /// or both placement sides intersect chrome.</returns>
    private bool MakeOffer(PhysicalPoint point, long timestampUs)
    {
        _pending = null;
        _nearMissRunActive = false;
        _runRole = RunRole.None;

        // EyeCtrl's own modal surfaces: nothing at all is offered over them (the user's decision,
        // 2026-10-02), and no offer is made for a P outside the primary screen by more than the
        // corner clamp's tolerance — a far off-screen P must not get a completion that lands (click
        // or zoom) off-screen (review B F4).
        if (OffersSuspended)
            return Refuse(point, ActivationOfferRefusalReason.Suspended);

        // The corner fix (2026-10-17): a settle up to T px past an edge is clamped ON-screen, and
        // the clamped P′ becomes the offer's Point — placement, the no-move zone and the completion
        // all use it. The one exception is the docked toolbar's summon band, where a clamped offer
        // would flash on the summon gaze before the toolbar slides in. The ring's own pin stays at
        // the raw P, which the wiring guarantees (the engine never sees it).
        var target = point;
        if (!_screenBoundsPx.Contains(point.X, point.Y))
        {
            if (!IsWithinClampTolerance(point) || IsInSummonBand(point))
                return Refuse(point, ActivationOfferRefusalReason.OffScreen);
            target = ClampToScreen(point);
        }

        if (IsRestStyle)
            return MakeRestOffer(point, target, timestampUs);

        if (InChrome(target))
            return Refuse(point, ActivationOfferRefusalReason.InChrome);

        var placement = ActivationButtonPlacement.Place(
            target, _ringSizeDip, LayoutConstants.ActivationButtonOffset, _scale, _hitRadiusPx,
            _fixationRadiusPx, _screenBoundsPx, _chromeZones);
        if (!placement.Placed)
            return Refuse(point, ActivationOfferRefusalReason.NoSidePlaceable);

        _offer = new ActivationOffer(
            _nextOfferId++, target, placement.ButtonCenter, _hitRadiusPx,
            timestampUs + (OfferExpiryMs * 1000L));
        _fired = false;
        _dwellUs = 0;
        _peakDwellUs = 0;
        _missSinceUs = null;
        _runRole = RunRole.Offered;
        _lastRefusalReason = null;   // an offer was made: the next refusal raises again (F3)
        return true;
    }

    // The rest style's half of MakeOffer, after suspension and the off-screen clamp. The rest centre
    // (B, where the fill is counted, the same-rest test and the latch) is the RAW settle point — at a
    // corner the gaze stays near it, up to T past the bezel, and would never reach an area round P′
    // (orchestrator decision, 2026-11-08). P′ stays the completion point and the chrome check.
    private bool MakeRestOffer(PhysicalPoint point, PhysicalPoint target, long timestampUs)
    {
        if (_rearmLatch is { } latch && point.DistanceTo(latch) <= RestAreaRadiusPx)
            return Refuse(point, ActivationOfferRefusalReason.Rearm);

        if (InChrome(target))
            return Refuse(point, ActivationOfferRefusalReason.InChrome);

        _offer = new ActivationOffer(
            _nextOfferId++, target, point, RestAreaRadiusPx,
            timestampUs + (OfferExpiryMs * 1000L), OfferPlacementStyle.AtRestPoint);
        _fired = false;
        _dwellUs = 0;
        _peakDwellUs = 0;
        _missSinceUs = null;
        _runRole = RunRole.Offered;
        _lastRefusalReason = null;
        return true;
    }

    // A rest offer closed by a command (not by looking away) latches its rest centre: the same rest
    // must not refill until the gaze has left it. Button style has no latch.
    private void LatchClosedRestOffer()
    {
        if (IsRestStyle && _offer is not null)
            _rearmLatch = _offer.Button;
    }

    /// <summary>Records a refusal and answers false, raising <see cref="OfferRefused"/> only when
    /// the reason changed since the last one. <paramref name="point"/> is always the RAW settle
    /// point — the diagnosis names where the eye was.</summary>
    private bool Refuse(PhysicalPoint point, ActivationOfferRefusalReason reason)
    {
        if (_lastRefusalReason != reason)
        {
            _lastRefusalReason = reason;
            OfferRefused?.Invoke(new ActivationOfferRefusal(point, reason));
        }
        return false;
    }

    // The offer is gone: either the deadline passed or the caller declined. The pending re-offer takes
    // its place only while its run still holds. A close by command latches a rest offer's spot; an
    // expiry (the user looked away) does not.
    private void ExpireOrDecline(long timestampUs, bool closedByCommand)
    {
        if (_offer is null)
            return;
        if (closedByCommand)
            LatchClosedRestOffer();
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
        _peakDwellUs = 0;
        _missSinceUs = null;
    }

    private void ExtendExpiry(long timestampUs)
    {
        long deadline = timestampUs + (OfferExpiryMs * 1000L);
        if (deadline > _offer!.ExpiresAtUs)
            _offer = _offer with { ExpiresAtUs = deadline };
    }

    private void CreditSample(long timestampUs)
    {
        long dt = timestampUs - _lastSampleUs;
        if (dt <= 0)
            return;
        _dwellUs += Math.Min(dt, FixationDetector.AgeCreditCapMs * 1000L);
        if (_dwellUs > _peakDwellUs)
            _peakDwellUs = _dwellUs;
    }

    private void PauseDwell(long timestampUs)
    {
        if (_missSinceUs is null)
        {
            // A miss that STARTS after a silence is dated from the last sample, so the silence counts
            // as time away — otherwise the ~400 ms before the first outside sample is lost and a gaze
            // away for over the budget does not reset (review A, R2-3 / probe P11). The same frame
            // floor as the in-hit reset keeps ordinary 30 ms frames out of it.
            _missSinceUs = timestampUs - _lastSampleUs > FixationDetector.AgeCreditCapMs * 1000L
                ? _lastSampleUs
                : timestampUs;
        }

        // Budget 0 is the reset-semantics comparison: any single outside sample resets (review B F8's
        // label), not the second one.
        if (OutsidePauseBudgetMs <= 0 || timestampUs - _missSinceUs > OutsidePauseBudgetMs * 1000L)
            _dwellUs = 0;
    }

    /// <summary>Whether P is outside the screen but inside it inflated by
    /// <see cref="CornerClampTolerancePx"/> (per axis): the clamp's domain. Exactly T counts — the
    /// boundary the corner tests pin.</summary>
    private bool IsWithinClampTolerance(PhysicalPoint point) =>
        point.X >= _screenBoundsPx.X - CornerClampTolerancePx
        && point.X <= _screenBoundsPx.X + _screenBoundsPx.Width + CornerClampTolerancePx
        && point.Y >= _screenBoundsPx.Y - CornerClampTolerancePx
        && point.Y <= _screenBoundsPx.Y + _screenBoundsPx.Height + CornerClampTolerancePx;

    /// <summary>P clamped to the screen's pixel domain, [X, X+W−1] × [Y, Y+H−1].</summary>
    private PhysicalPoint ClampToScreen(PhysicalPoint point) => new(
        Math.Clamp(point.X, _screenBoundsPx.X, _screenBoundsPx.X + _screenBoundsPx.Width - 1),
        Math.Clamp(point.Y, _screenBoundsPx.Y, _screenBoundsPx.Y + _screenBoundsPx.Height - 1));

    /// <summary>Whether P lies past the docked toolbar's edge — the WHOLE edge (fixup 01 R-F1;
    /// the summon gate has no span test). Such a settle is summoning the toolbar, never clamped.</summary>
    private bool IsInSummonBand(PhysicalPoint point)
    {
        if (_summonBand is not { } band)
            return false;
        return band.Edge switch
        {
            ActivationSummonEdge.Top => point.Y < _screenBoundsPx.Y,
            ActivationSummonEdge.Bottom => point.Y > _screenBoundsPx.Y + _screenBoundsPx.Height,
            ActivationSummonEdge.Left => point.X < _screenBoundsPx.X,
            ActivationSummonEdge.Right => point.X > _screenBoundsPx.X + _screenBoundsPx.Width,
            _ => false,
        };
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
