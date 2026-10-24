namespace EyeCtrl.App.Overlays;

using EyeCtrl.Core;

/// <summary>
/// The gaze-off clock behind C1's un-yield (#244, owner decision 24.10): the keyboard may move
/// back toward its docked edge once the toolbar that forced the yield has gone, but only after the
/// gaze has been continuously OUTSIDE the whole keyboard window for <see cref="OffThresholdUs"/>.
/// A sample inside the window resets the run, and a silence longer than
/// <see cref="MaxContinuousSampleGapUs"/> restarts it: a missing sample is not evidence that the
/// gaze is away.
/// </summary>
internal sealed class KeyboardUnYieldGate
{
    // The owner's choice (24.10), not tuned on hardware: long enough that a glance off the
    // keyboard is not a move, short enough that the keyboard returns promptly once the gaze
    // settles away. Deliberately not a setting.
    internal const long OffThresholdUs = 300 * MathConstants.MicrosecondsPerMillisecond;

    // The same bound as KeyboardViewModel.MaxContinuousSampleGapUs, which credits a stall of
    // exactly 150 ms as held gaze and restarts beyond it. A blink or tracker dropout must not
    // count as "away", so the off-run restarts rather than covering the silence.
    internal const long MaxContinuousSampleGapUs = 150 * MathConstants.MicrosecondsPerMillisecond;

    // When the current unbroken outside-run began; long.MinValue means "no run".
    private long _offSinceUs = long.MinValue;
    private long _lastSampleUs = long.MinValue;

    /// <summary>
    /// Feed one real sample (the coordinator withholds gap-held ones). True once the gaze has been
    /// continuously outside the keyboard window for <see cref="OffThresholdUs"/>.
    /// </summary>
    internal bool Update(bool gazeOutside, long timestampUs)
    {
        if (!gazeOutside)
        {
            Reset();
            return false;
        }

        // A first sample, a silence beyond the continuous-sample bound, or a clock that went
        // backwards all start a fresh run: only a continuous stream measures "the gaze is away".
        if (_lastSampleUs == long.MinValue
            || timestampUs < _lastSampleUs
            || timestampUs - _lastSampleUs > MaxContinuousSampleGapUs)
        {
            _offSinceUs = timestampUs;
        }

        _lastSampleUs = timestampUs;
        return timestampUs - _offSinceUs >= OffThresholdUs;
    }

    /// <summary>Starts over: the next outside sample begins a fresh off-run.</summary>
    internal void Reset()
    {
        _offSinceUs = long.MinValue;
        _lastSampleUs = long.MinValue;
    }
}
