namespace EyeCtrl.Core.Processing;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// Handles gap detection and interpolation for gaze data.
/// All positions are in physical screen pixels.
/// </summary>
public sealed class GapInterpolator
{
    private readonly IGazeFilter _filter;
    private readonly long _maxGapMicroseconds;
    private PhysicalPoint _lastValidPosition = new(0, 0);
    private long _lastValidTimestamp = -1;

    // INVARIANT: any sample that returns a HELD position reports IsInGap: true. The sole consumer of the
    // flag is a suppression gate, and a held position carries no evidence about where the user is looking,
    // so the two must not disagree. The pre-first-valid case has no exception: there is no held
    // measurement there either — the position is the (0,0) placeholder, not data — and reporting it
    // in-gap is what keeps a cold-start invalid stream from growing a phantom fixation at (0,0)
    // (ticket 3, item 4).

    /// <summary>
    /// Initializes a new instance of the <see cref="GapInterpolator"/> class.
    /// </summary>
    /// <param name="filter">The gaze filter used to smooth valid samples.</param>
    /// <param name="maxGapMs">Maximum gap duration in milliseconds before the sequence is considered broken.</param>
    public GapInterpolator(IGazeFilter filter, int maxGapMs)
    {
        _filter = filter;
        _maxGapMicroseconds = maxGapMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Process a gaze point, returning filtered position in physical screen pixels.
    /// </summary>
    public (PhysicalPoint Position, bool IsInGap) Process(GazePoint rawPoint)
    {
        if (rawPoint.IsValid)
        {
            var filtered = _filter.Filter(rawPoint);
            _lastValidPosition = filtered;
            _lastValidTimestamp = rawPoint.TimestampMicroseconds;
            return (filtered, false);
        }

        // No prior valid sample: the position is the (0,0) placeholder, not a held measurement, but the
        // sample still carries no evidence about where the user is looking — so it is in gap.
        if (_lastValidTimestamp < 0)
            return (_lastValidPosition, true);

        long gap = rawPoint.TimestampMicroseconds - _lastValidTimestamp;

        if (gap >= _maxGapMicroseconds)
        {
            // Gap too long — treat as new sequence, reset filter. The position handed back is still the
            // held one, so this sample is in-gap too (per the invariant above): "start a new sequence" is
            // a statement about the FILTER, not evidence that the user is looking anywhere.
            _filter.Reset();
            _lastValidTimestamp = -1;
            return (_lastValidPosition, true);
        }

        // Short gap (blink) — hold position, suppress fixation/dwell
        return (_lastValidPosition, true);
    }

    /// <summary>Copies internal state (last position and timestamp) from another interpolator without
    /// locking.</summary>
    /// <param name="other">The source interpolator to copy state from.</param>
    public void CopyStateFrom(GapInterpolator other)
    {
        _lastValidPosition = other._lastValidPosition;
        _lastValidTimestamp = other._lastValidTimestamp;

        // Learned filter state is carried across the rebuild for the same reason the position is: a
        // settings change does not make the tracker quieter or noisier, and a fresh One-Euro would judge
        // the very next sample against its bare jump floor.
        //
        // Deliberately expressed against IGazeFilterStateAdopter and NOT against a concrete filter type.
        // A type test for OneEuroFilter reads correctly today and breaks the day the pipeline gains a
        // decorator — which the Stage 3 median prefilter is planned as — because the decorator is not a
        // OneEuroFilter and the test simply goes false. Nothing throws, nothing fails, and the fix this
        // line exists to deliver quietly stops happening. See IGazeFilterStateAdopter for the forwarding
        // contract decorators owe, and GazeFilterCompositionTests for the guard that enforces it.
        //
        // A mode switch (EMA or Direct on either side) has nothing to hand over and correctly starts clean.
        if (_filter is IGazeFilterStateAdopter incoming)
            incoming.AdoptState((other._filter as IGazeFilterStateAdopter)?.CaptureState());
    }

    /// <summary>
    /// Primes the (freshly constructed) filter with the current carried-over position so the first
    /// real sample after a rebuild is smoothed relative to it rather than snapping to the raw sample
    /// (design R1 — no cursor jump on reconfiguration). No-op if no valid position is known yet.
    /// </summary>
    public void WarmStartFromCurrentState()
    {
        if (_lastValidTimestamp < 0) return;
        _filter.Filter(new GazePoint(_lastValidPosition.X, _lastValidPosition.Y, _lastValidTimestamp, true));
    }

    /// <summary>Resets the interpolator and its underlying filter to initial state.</summary>
    public void Reset()
    {
        _filter.Reset();
        _lastValidPosition = new(0, 0);
        _lastValidTimestamp = -1;
    }
}
