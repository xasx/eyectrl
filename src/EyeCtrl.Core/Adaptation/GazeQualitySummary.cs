namespace EyeCtrl.Core.Adaptation;

/// <summary>
/// A periodic snapshot of gaze quality over a short window, aggregated on the gaze thread and
/// pushed through the telemetry sink for struggle detection. A value type (allocation-free).
/// </summary>
/// <param name="TimestampMicroseconds">Timestamp of the window end, in microseconds.</param>
/// <param name="InvalidFraction">Fraction [0..1] of samples in the window that were invalid (tracking lost).</param>
/// <param name="JitterStdDev">Gaze-speed standard deviation (px/s) during fixation — a shakiness proxy — snapshot at window end.</param>
/// <param name="FatigueScore">EyeCare fatigue score [0..1]; 0 when fatigue monitoring is disabled.</param>
/// <param name="SampleCount">Number of samples aggregated in the window.</param>
public readonly record struct GazeQualitySummary(
    long TimestampMicroseconds,
    double InvalidFraction,
    double JitterStdDev,
    double FatigueScore,
    int SampleCount);
