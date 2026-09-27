namespace EyeCtrl.Core.Processing;
using EyeCtrl.Core.Models;

/// <summary>One completed activation: the sample time, the valid-time age credited at that sample, and the
/// running centroid the activation point was drawn from.</summary>
/// <param name="TimestampUs">Timestamp of the sample on which the activation clock completed.</param>
/// <param name="CreditedAgeUs">Valid-time age accrued from the run's first sample to that sample.</param>
/// <param name="Point">The running centroid at completion, in physical screen pixels.</param>
public readonly record struct FixationActivation(long TimestampUs, long CreditedAgeUs, PhysicalPoint Point);

/// <summary>Why a run was released.</summary>
public enum FixationEndReason
{
    /// <summary>Gaze moved outside the radius: the ordinary end of a fixation.</summary>
    GazeLeft,
    /// <summary>The eyes were lost for longer than the gap budget.</summary>
    EyesLostPastGapBudget,
    /// <summary>The stream ended while the run was still open.</summary>
    StreamEnded,
    /// <summary>The app took the interaction over — the suppression release ended a run whose onset had
    /// been reported. The run is over like any other, but the user did not give up on it: it is not a
    /// <see cref="GazeLeft"/>, and telemetry must not report it as an abandonment.</summary>
    Suppressed,
}

/// <summary>What the detector reports when it releases a run whose onset it had reported.</summary>
/// <param name="LastCreditedUs">Timestamp of the last sample the run credited.</param>
/// <param name="CreditedAgeUs">Valid-time age accrued from the run's first sample to that sample.</param>
/// <param name="Point">The running centroid at the last credited sample, in physical screen pixels.</param>
/// <param name="Reason">Why the run was released.</param>
/// <param name="Activated">True when this run already completed its activation, so it is a success, not
/// an abandonment — the same distinction the legacy <c>DwellDetector</c> drew by nulling the target's
/// entry on activation.</param>
public readonly record struct FixationRelease(
    long LastCreditedUs,
    long CreditedAgeUs,
    PhysicalPoint Point,
    FixationEndReason Reason,
    bool Activated)
{
    /// <summary>True when gaze left (or the eyes were lost past the gap budget); false when the stream
    /// ended while the run was still open.</summary>
    public bool EndedByLeave => Reason != FixationEndReason.StreamEnded;
}

/// <summary>
/// Detects when gaze remains stable within a radius for a duration, by ticket 3's running-centroid rule
/// (final synthesis, step 1). All coordinates are in physical screen pixels and <see cref="RadiusPixels"/>
/// is never DPI-converted.
/// <para>
/// The rule, in one paragraph. The fixation centre is the running mean of the in-window samples; each new
/// sample is tested against the mean BEFORE it is added, and a sample outside the radius ends the run and
/// starts a fresh one. Age is the sum of valid inter-sample dt, each capped at
/// <see cref="AgeCreditCapMs"/>, and it includes the <see cref="FixationTimeMs"/> onset window: onset is
/// reported when age reaches <see cref="FixationTimeMs"/> (the button's first appearance), activation when
/// it reaches <see cref="FixationTimeMs"/> + <see cref="ActivationDwellMs"/>. A gap of at most
/// <see cref="GapAbortMs"/> pauses the age (the paused sample credits only the per-sample cap, never the
/// gap); a longer gap aborts the run and starts a new one at the current sample. The
/// <c>GapInterpolator</c>'s 150 ms long-gap reset is deliberately NOT an abort signal: 3 of 33 real blinks
/// fall between 150 and 500 ms, and aborting on them would discard real fixations.
/// </para>
/// <para>
/// The detector is fed only NON-GAP samples by <c>GazeEngine</c> (the interpolator holds the frozen
/// position instead), so gap frames never reach <see cref="Update(PhysicalPoint, long)"/>. The
/// <see cref="Update(PhysicalPoint, long, bool)"/> overload's <c>isValid</c> parameter is a second line of
/// defence for callers that feed raw validity: an invalid sample changes no state at all, so a cold-start
/// invalid stream cannot grow a phantom fixation at (0,0) even if a caller hands one in.
/// </para>
/// </summary>
public class FixationDetector
{
    /// <summary>Per-sample credit cap for the valid-time age: about two frames at the recording's 33 Hz.
    /// A dropout can never credit more than a sample or two of age.</summary>
    public const int AgeCreditCapMs = 66;

    /// <summary>A gap longer than this aborts the fixation; a shorter one only pauses it. Pinned by the
    /// debate's skeptic conditions (300 ms + a 100 ms blink + 700 ms fires; 300 ms + 5 s lost does not).</summary>
    public const int GapAbortMs = 500;

    private readonly long _ageCreditCapUs = AgeCreditCapMs * MathConstants.MicrosecondsPerMillisecond;
    private readonly long _gapAbortUs = GapAbortMs * MathConstants.MicrosecondsPerMillisecond;

    private double _radiusPixels;
    private long _fixationTimeMicroseconds;
    private long _activationDwellMicroseconds;
    private long _cooldownMicroseconds;

    // The run's running centroid (Welford mean), or null when no run is open.
    private PhysicalPoint? _centroid;
    private long _centroidCount;

    private long _lastFedUs;
    private long _creditedUs;
    private long _lastCreditedUs;
    private bool _onsetFired;
    private bool _activated;

    // Last activation across runs, for the cooldown. Survives ClearRun.
    private long? _lastActivationUs;

    /// <summary>Fired when fixation starts. Point is the running centroid at onset, in physical pixels.</summary>
    public event Action<PhysicalPoint>? FixationStarted;

    /// <summary>Fired when the current fixation ends because gaze moved outside the radius or the eyes were
    /// lost past the gap budget.</summary>
    public event Action? FixationEnded;

    /// <summary>Fired once per run when the valid-time age reaches <see cref="FixationTimeMs"/> +
    /// <see cref="ActivationDwellMs"/> (and the cooldown allows it), carrying the activation time, the
    /// credited age at that time and the running centroid. This is the Core-published activation signal
    /// the ViewModels consume; the age is never recomputed downstream.</summary>
    public event Action<FixationActivation>? FixationActivated;

    /// <summary>Fired when a run whose onset was reported is released, with the ledger the harness and
    /// struggle telemetry need: the last credited sample, its valid-time age, and whether gaze left
    /// (true) or the stream ended (false). Fired alongside <see cref="FixationEnded"/> on a leave, and
    /// alone from <see cref="CompleteStream"/>.</summary>
    public event Action<FixationRelease>? FixationReleased;

    /// <summary>Whether the detector currently considers gaze to be fixating (its onset has been reported).</summary>
    public bool IsFixating => _onsetFired;

    /// <summary>Current fixation point in physical screen pixels (the running centroid), if fixating.</summary>
    public PhysicalPoint? FixationPoint => _onsetFired ? _centroid : null;

    /// <summary>The current run's valid-time age in microseconds; 0 when no run is open.</summary>
    public long CreditedAgeUs => _creditedUs;

    /// <summary>
    /// Dwell progress after onset, 0.0–1.0: the fraction of <see cref="ActivationDwellMs"/> the run has
    /// accrued since onset. 0 before onset (and while no run is open); 1.0 at activation. This is the
    /// scalar Core publishes per sample for the activation feedback, so the ViewModel never computes age.
    /// </summary>
    public double DwellProgress
    {
        get
        {
            if (!_onsetFired || _activationDwellMicroseconds <= 0)
                return _onsetFired ? 1.0 : 0.0;
            long sinceOnset = _creditedUs - _fixationTimeMicroseconds;
            if (sinceOnset <= 0) return 0.0;
            return Math.Min(1.0, (double)sinceOnset / _activationDwellMicroseconds);
        }
    }

    /// <summary>
    /// Maximum distance in physical pixels from the running centroid for gaze to still be considered
    /// fixating. Settable at runtime so a tolerance change — a manual <c>DwellSettings</c> edit or an
    /// adaptation delta — applies to the LIVE detector, in place. Rebuilding the detector instead would
    /// drop the in-progress fixation, and the adaptation controller steps tolerance on every tick while
    /// relaxing or recovering, so a rebuild-per-change starves fixation entirely whenever the tick
    /// interval is shorter than <see cref="FixationTimeMs"/> (design §5, §9.1 "never per tick").
    /// <para>
    /// State-preserving, mirroring <see cref="DwellDetector.UpdateTargetGeometry"/>: enlarging keeps an
    /// in-progress fixation (gaze already inside the old radius is inside the new one), and shrinking
    /// such that gaze now falls outside ends it on the next <see cref="Update(PhysicalPoint, long)"/> as
    /// an ordinary gaze-left — a real transition rather than a synthetic teardown. Assigning the current
    /// value is a no-op, so a controller may write it every tick.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when the value is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public double RadiusPixels
    {
        get => _radiusPixels;
        set
        {
            ValidateRadius(value, nameof(value));
            _radiusPixels = value;
        }
    }

    /// <summary>
    /// Minimum valid-time age (ms) a run must accrue to report its onset. Settable at runtime for the same
    /// reason as <see cref="RadiusPixels"/>, and likewise state-preserving: shortening it below the
    /// already-accrued age lets the next <see cref="Update(PhysicalPoint, long)"/> fire
    /// <see cref="FixationStarted"/>, and lengthening it leaves an established onset intact (only a
    /// gaze-left or a gap abort ends a run).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public int FixationTimeMs
    {
        get => (int)(_fixationTimeMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        set
        {
            ValidateTime(value, nameof(value));
            _fixationTimeMicroseconds = value * MathConstants.MicrosecondsPerMillisecond;
        }
    }

    /// <summary>
    /// After the onset age, how much further valid-time age completes the activation (ms). This is the
    /// effective activation-button dwell — <c>DwellSettings.ActivationButtonDwellMs</c> as composed by
    /// the effective-dwell map — so adaptation's dwell knob keeps reaching activation. Settable in place,
    /// like the other tolerances. 0 is legal (activation fires with the onset sample); negative is not.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int ActivationDwellMs
    {
        get => (int)(_activationDwellMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Activation dwell must not be negative.");
            _activationDwellMicroseconds = value * MathConstants.MicrosecondsPerMillisecond;
        }
    }

    /// <summary>Cooldown (ms) after an activation during which no new activation may fire. 0 is legal.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int CooldownMs
    {
        get => (int)(_cooldownMicroseconds / MathConstants.MicrosecondsPerMillisecond);
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value), value, "Cooldown must not be negative.");
            _cooldownMicroseconds = value * MathConstants.MicrosecondsPerMillisecond;
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="FixationDetector"/> class.
    /// </summary>
    /// <param name="fixationRadiusPixels">Maximum distance in physical pixels from the running centroid for gaze to still be considered fixating.</param>
    /// <param name="fixationTimeMs">Minimum valid-time age in milliseconds to report the fixation onset.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="fixationRadiusPixels"/> is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when parameters are non-positive.</exception>
    public FixationDetector(double fixationRadiusPixels, int fixationTimeMs)
    {
        // Validated through the shared helpers rather than the property setters so the thrown
        // ArgumentException carries the CONSTRUCTOR's parameter name (callers — and
        // FixationDetectorValidationTests — assert on ParamName), not the setter's "value".
        ValidateRadius(fixationRadiusPixels, nameof(fixationRadiusPixels));
        ValidateTime(fixationTimeMs, nameof(fixationTimeMs));

        _radiusPixels = fixationRadiusPixels;
        _fixationTimeMicroseconds = fixationTimeMs * MathConstants.MicrosecondsPerMillisecond;
    }

    private static void ValidateRadius(double value, string paramName)
    {
        if (!double.IsFinite(value))
            throw new ArgumentException("Fixation radius must be a finite number.", paramName);
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Fixation radius must be positive.");
    }

    private static void ValidateTime(int value, string paramName)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(paramName, value, "Fixation time must be positive.");
    }

    /// <summary>
    /// Update with the current gaze point in physical screen pixels, assumed valid.
    /// </summary>
    /// <param name="currentPoint">Filtered gaze position in physical screen pixels.</param>
    /// <param name="timestampMicroseconds">Sample timestamp in microseconds.</param>
    public void Update(PhysicalPoint currentPoint, long timestampMicroseconds)
        => Update(currentPoint, timestampMicroseconds, isValid: true);

    /// <summary>
    /// Update with the current gaze point and its raw validity. An invalid sample changes no state —
    /// it neither starts nor grows a run, so a cold-start invalid stream cannot form the (0,0) phantom.
    /// </summary>
    /// <param name="currentPoint">Filtered gaze position in physical screen pixels.</param>
    /// <param name="timestampMicroseconds">Sample timestamp in microseconds.</param>
    /// <param name="isValid">The raw sample's validity flag.</param>
    public void Update(PhysicalPoint currentPoint, long timestampMicroseconds, bool isValid)
    {
        if (!isValid)
            return;

        if (_centroid is null)
        {
            StartRun(currentPoint, timestampMicroseconds);
            return;
        }

        long dt = timestampMicroseconds - _lastFedUs;
        if (dt > _gapAbortUs)
        {
            // Eyes were lost past the pause budget: the run is dead; this sample starts a fresh one.
            EndRun(FixationEndReason.EyesLostPastGapBudget);
            StartRun(currentPoint, timestampMicroseconds);
            return;
        }

        // Tested BEFORE it is added: the mean must not drift toward a sample that is already outside R.
        if (currentPoint.DistanceTo(_centroid.Value) > _radiusPixels)
        {
            EndRun(FixationEndReason.GazeLeft);
            StartRun(currentPoint, timestampMicroseconds);
            return;
        }

        AddToCentroid(currentPoint);
        if (dt > 0)
        {
            _creditedUs += Math.Min(dt, _ageCreditCapUs);
            _lastCreditedUs = timestampMicroseconds;
            _lastFedUs = timestampMicroseconds;
        }

        FireOnsetIfDue();
        FireActivationIfDue(timestampMicroseconds);
    }

    /// <summary>
    /// Returns progress toward the onset requirement as a 0.0–1.0 ratio of credited valid-time age to
    /// <see cref="FixationTimeMs"/>. Returns 0.0 when no run is open.
    /// </summary>
    public double GetProgress()
    {
        if (_centroid is null || _fixationTimeMicroseconds <= 0)
            return 0.0;
        return Math.Min(1.0, (double)_creditedUs / _fixationTimeMicroseconds);
    }

    /// <summary>
    /// Zeroes the run's credited age without ending the run and WITHOUT touching its onset or activation
    /// latches. This is the reset applied on every FreeLook entry: age the eyes accrued before the state
    /// moved must not complete an activation the moment it comes back (skeptic condition 2), while the
    /// run's geometry and latches survive, because the gaze is usually still where it was (an overlay
    /// exit, a menu dismissal). A gaze-left or a gap abort still ends the run as before.
    /// </summary>
    public void ResetAge()
    {
        _creditedUs = 0;
        _lastCreditedUs = _lastFedUs;
    }

    /// <summary>
    /// Re-arms the detector so held gaze can report a fresh onset after a re-arm edge. The re-arm is
    /// deliberately narrow: only <c>Paused → FreeLook</c> and the <c>GazeEngine.SuppressFixation</c>
    /// true→false edge use it.
    /// <para>
    /// Clearing the latches on EVERY FreeLook entry is wrong (fixup-02 R2-1): a fixed-position exit
    /// button — the edge-bar Close at <c>RightEdgeAnchor</c> — sits under an unmoved gaze, so the
    /// latched run re-onsets at +300 ms, reactivates at +1000 ms and re-opens the overlay about every
    /// 1.6 s, five times in an 8 s stare. Real exits leave the gaze beyond R of what was under it, so a
    /// new target starts a fresh run and the latch never mattered for it.
    /// </para>
    /// </summary>
    /// <param name="endReportedRun">
    /// True on the <c>SuppressFixation</c> release edge. The run's onset was reported, so the
    /// interaction state is still <c>Fixation</c>; ending the run here — raising
    /// <see cref="FixationEnded"/> and the release ledger, exactly as a gaze-left does — is what returns
    /// the state to FreeLook. Without it the cleared latch means the run's later end raises nothing, the
    /// state strands in Fixation, and the re-onset is rejected because
    /// <c>InteractionManager.OnFixationStarted</c> only accepts from FreeLook (fixup-02 N1). False on
    /// the <c>Paused → FreeLook</c> edge: the state already left Fixation, so an end there would be a
    /// spurious transition (and would read as an abandonment in telemetry).
    /// </param>
    public void Rearm(bool endReportedRun)
    {
        if (endReportedRun && _onsetFired)
        {
            EndRun(FixationEndReason.Suppressed);
            return;
        }
        ResetAge();
        _onsetFired = false;
        _activated = false;
    }

    /// <summary>
    /// Ends the stream, releasing a run still open at the last fed sample with
    /// <see cref="FixationRelease.EndedByLeave"/> false. No <see cref="FixationEnded"/> is raised: the
    /// stream ending is not gaze leaving.
    /// </summary>
    public void CompleteStream()
    {
        if (_onsetFired)
            FixationReleased?.Invoke(new FixationRelease(
                _lastCreditedUs, _creditedUs, _centroid!.Value, FixationEndReason.StreamEnded, _activated));
        ClearRun();
    }

    /// <summary>Resets the detector to its initial state (no fixation active), silently — no events.
    /// For the reset paths that must not be mistaken for gaze leaving.</summary>
    public void Reset()
    {
        ClearRun();
    }

    private void StartRun(PhysicalPoint point, long timestampMicroseconds)
    {
        _centroid = point;
        _centroidCount = 1;
        _lastFedUs = timestampMicroseconds;
        _creditedUs = 0;
        _lastCreditedUs = timestampMicroseconds;
        _onsetFired = false;
        _activated = false;
    }

    private void AddToCentroid(PhysicalPoint point)
    {
        _centroidCount++;
        var mean = _centroid!.Value;
        _centroid = new PhysicalPoint(
            mean.X + ((point.X - mean.X) / _centroidCount),
            mean.Y + ((point.Y - mean.Y) / _centroidCount));
    }

    // Ends the open run. A real gaze transition raises FixationEnded before the release ledger;
    // a stream end has its own release shape (CompleteStream) and never reaches here.
    private void EndRun(FixationEndReason reason)
    {
        if (_onsetFired)
        {
            FixationEnded?.Invoke();
            FixationReleased?.Invoke(new FixationRelease(
                _lastCreditedUs, _creditedUs, _centroid!.Value, reason, _activated));
        }
        ClearRun();
    }

    private void ClearRun()
    {
        _centroid = null;
        _centroidCount = 0;
        _lastFedUs = 0;
        _creditedUs = 0;
        _lastCreditedUs = 0;
        _onsetFired = false;
        _activated = false;
    }

    private void FireOnsetIfDue()
    {
        if (_onsetFired || _creditedUs < _fixationTimeMicroseconds)
            return;
        _onsetFired = true;
        // The anchor is the running centroid at onset — never the run's first sample, which sits behind
        // the saccade (median 37 px off in the recording).
        FixationStarted?.Invoke(_centroid!.Value);
    }

    private void FireActivationIfDue(long timestampMicroseconds)
    {
        if (!_onsetFired || _activated)
            return;
        if (_creditedUs < _fixationTimeMicroseconds + _activationDwellMicroseconds)
            return;
        if (_lastActivationUs is { } last
            && timestampMicroseconds - last < _cooldownMicroseconds)
            return;
        _activated = true;
        _lastActivationUs = timestampMicroseconds;
        FixationActivated?.Invoke(new FixationActivation(timestampMicroseconds, _creditedUs, _centroid!.Value));
    }
}
