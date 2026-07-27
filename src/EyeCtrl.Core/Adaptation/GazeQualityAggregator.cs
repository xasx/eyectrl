namespace EyeCtrl.Core.Adaptation;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// Aggregates raw gaze samples into periodic <see cref="GazeQualitySummary"/> snapshots on the gaze
/// thread and pushes them through an <see cref="IGazeQualitySink"/> (design §9.1: O(1) per sample,
/// summary marshaled by the sink). Timestamp-driven — no wall clock — so it is deterministic and
/// unit-testable. Not thread-safe: call <see cref="OnSample"/> from one thread (the gaze poll thread).
/// <para>
/// Gated at the source on <see cref="AdaptationSettings.Enabled"/> (design §9.1 "disabled = current
/// cost"), the same discipline as <c>FatigueMonitor</c>'s early return: while adaptation is off — the
/// default — <see cref="OnSample"/> does no aggregation and emits no summaries, so the gaze path is
/// exactly what it was before struggle detection existed.
/// </para>
/// </summary>
public sealed class GazeQualityAggregator
{
    private readonly IGazeQualitySink _sink;
    private readonly AdaptationSettings _adaptation;
    private readonly long _windowMicroseconds;

    private long _windowStart = long.MinValue;
    private int _total;
    private int _invalid;
    private double _lastJitter;
    private double _lastFatigue;

    /// <summary>Number of times the gaze clock moved backwards and the window was resynchronized.</summary>
    public long ClockRestarts { get; private set; }

    /// <summary>Initializes the aggregator.</summary>
    /// <param name="sink">Destination for window summaries.</param>
    /// <param name="adaptation">Adaptation settings; <see cref="AdaptationSettings.Enabled"/> gates all
    /// per-sample work. Required rather than optional so the gate cannot be bypassed by omission.</param>
    /// <param name="windowMs">Window length in milliseconds; must be positive. Default 500.</param>
    public GazeQualityAggregator(IGazeQualitySink sink, AdaptationSettings adaptation, int windowMs = 500)
    {
        if (windowMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowMs), windowMs, "Window must be positive.");
        _sink = sink;
        _adaptation = adaptation;
        _windowMicroseconds = windowMs * MathConstants.MicrosecondsPerMillisecond;
    }

    /// <summary>
    /// Feeds one raw gaze sample plus the current fixation jitter and fatigue score. Emits a summary
    /// (and resets the window) when the window duration has elapsed since the first sample of the window.
    /// </summary>
    /// <param name="sample">Raw gaze sample (valid or invalid).</param>
    /// <param name="fixationJitter">Current gaze-speed std-dev during fixation (from GazeEngine).</param>
    /// <param name="fatigueScore">Current fatigue score [0..1] (0 when disabled).</param>
    public void OnSample(GazePoint sample, double fixationJitter, double fatigueScore)
    {
        // The enable gate (§9.1). `Enabled` is written on the dispatcher and read here on the gaze thread;
        // a bool field is a single aligned byte, so the read cannot tear, and the worst a race costs is one
        // sample aggregated (or skipped) either side of the toggle. Same treatment as EyeCareService's
        // per-sample read of EyeCareSettings.Enabled.
        if (!_adaptation.Enabled)
        {
            // Discard any partial window so a re-enable starts clean rather than emitting a summary whose
            // counts straddle the disabled stretch — mirrors EyeCareService resetting the FatigueMonitor
            // when fatigue monitoring is switched off.
            if (_windowStart != long.MinValue)
            {
                _windowStart = long.MinValue;
                _total = 0;
                _invalid = 0;
                _lastJitter = 0.0;
                _lastFatigue = 0.0;
            }
            return;
        }

        if (_windowStart == long.MinValue)
        {
            _windowStart = sample.TimestampMicroseconds;
        }
        else if (sample.TimestampMicroseconds < _windowStart)
        {
            // The gaze clock moved backwards — the tracker clock is monotonic but has an arbitrary
            // epoch, so a device reconnect can restart it. Samples reach this aggregator in order on a
            // single thread, so this is never reordering. Without resyncing, _windowStart would sit
            // permanently in the future and no summary would ever be emitted again.
            ClockRestarts++;
            _windowStart = sample.TimestampMicroseconds;
            _total = 0;
            _invalid = 0;
        }

        _total++;
        if (!sample.IsValid) _invalid++;
        _lastJitter = fixationJitter;
        _lastFatigue = fatigueScore;

        if (sample.TimestampMicroseconds - _windowStart >= _windowMicroseconds)
        {
            _sink.ReportGazeQuality(new GazeQualitySummary(
                sample.TimestampMicroseconds,
                _total > 0 ? (double)_invalid / _total : 0.0,
                _lastJitter,
                _lastFatigue,
                _total));

            _windowStart = sample.TimestampMicroseconds;
            _total = 0;
            _invalid = 0;
        }
    }
}
