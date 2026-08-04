namespace EyeCtrl.App.Services;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare;

/// <summary>
/// Feeds <see cref="GazeQualityAggregator"/> from the raw gaze stream, supplying the fixation jitter
/// and the fatigue score it cannot obtain itself.
/// </summary>
/// <remarks>
/// Exists as a named class rather than a lambda in App.OnStartup for two reasons. It makes the
/// staleness substitution testable — the case that matters is eye care having faulted, which no test
/// can reach through an inline closure. And once the gaze fan-out reports faults per handler, a named
/// handler means a fault log can say which feature failed.
/// <para>
/// Runs on the gaze thread. Must stay cheap: no I/O, no locks, no dispatcher-synchronous work.
/// </para>
/// </remarks>
public sealed class GazeQualityBridge
{
    private readonly GazeQualityAggregator _aggregator;
    private readonly IEyeCareService _eyeCare;
    private readonly Func<double> _fixationJitter;

    /// <summary>Initializes a new instance of the <see cref="GazeQualityBridge"/> class.</summary>
    /// <param name="aggregator">The aggregator that windows samples into periodic summaries.</param>
    /// <param name="eyeCare">Supplies the fatigue score, and whether it is fresh for this sample.</param>
    /// <param name="fixationJitter">Reads the engine's latched jitter for the last completed fixation.</param>
    public GazeQualityBridge(
        GazeQualityAggregator aggregator, IEyeCareService eyeCare, Func<double> fixationJitter)
    {
        _aggregator = aggregator;
        _eyeCare = eyeCare;
        _fixationJitter = fixationJitter;
    }

    /// <summary>Handles one raw gaze sample. Wire this to <c>GazeEngine.GazeSampled</c>.</summary>
    /// <param name="sample">The raw sample, valid or invalid.</param>
    public void OnGazeSample(GazePoint sample)
    {
        // Substitute 0 when eye care has not processed this sample. GazeQualitySummary already
        // documents fatigue as "0 when fatigue monitoring is disabled", so a stale reading lands on
        // an existing contract instead of feeding a frozen score into StruggleSignals.
        double fatigue = _eyeCare.TryGetFatigueScore(sample.TimestampMicroseconds, out var fresh)
            ? fresh
            : 0.0;
        _aggregator.OnSample(sample, _fixationJitter(), fatigue);
    }
}
