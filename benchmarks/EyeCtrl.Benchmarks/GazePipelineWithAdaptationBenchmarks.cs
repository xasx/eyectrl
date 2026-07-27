using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — the real GazeEngine pipeline with the struggle-detection telemetry wiring attached, measured
/// three ways so the two costs can be told apart: the pipeline alone (what shipped before), the pipeline
/// with Phase-1a telemetry attached but adaptation OFF (the default shipping state — 1a's aggregation is
/// deliberately ungated, so this cost is always paid), and with adaptation ON (opted in, so the effective
/// -settings read and the deferred tolerance apply are live).
/// </summary>
/// <remarks>
/// CURSOR-ADVANCE DISCIPLINE (mirroring GazePipelineWithFatigueBenchmarks): the adaptation path is
/// timestamp-stateful — GazeQualityAggregator windows on the sample clock and treats a backward jump as a
/// device reconnect (resetting the window and counting a ClockRestart). Replaying the same array would
/// therefore measure the clock-restart branch instead of the steady-state one, so each iteration advances a
/// monotonic cursor through a large batch with invocationCount:1.
///
/// SCOPE: this measures the GAZE-THREAD cost, which is what a lagging cursor would come from. Two pieces sit
/// outside it by construction. (1) The App's DispatcherTelemetrySink marshals reports onto the UI thread; it
/// is not referenced here, and the aggregator's summaries cross it at ~2 Hz. (2) AdaptationCoordinator's
/// per-sample contribution is a single Volatile.Write of the timestamp (App layer, likewise unreferenced).
/// The monitor/controller tick cost is measured separately in AdaptationBenchmarks — it runs on the
/// dispatcher at 4 Hz, not per sample.
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineWithAdaptationBenchmarks
{
    private const int SamplesPerBatch = 100_000;
    private const int PrefillSamples = 128; // enough to settle the filter and reach a fixation

    private GazePoint[] _fixation = null!;

    private EyeCareBenchmarkFakes.FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup() =>
        _fixation = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SamplesPerBatch + 16);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_NoAdaptationWiring))]
    public void SetupBare() => Rebuild(attachTelemetry: false, adaptationEnabled: false);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Telemetry_AdaptationDisabled))]
    public void SetupDisabled() => Rebuild(attachTelemetry: true, adaptationEnabled: false);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Telemetry_AdaptationEnabled))]
    public void SetupEnabled() => Rebuild(attachTelemetry: true, adaptationEnabled: true);

    [IterationCleanup]
    public void IterationCleanup() => _engine.Dispose();

    /// <summary>The pipeline as it stood before Phase 1a — the reference the other two are read against.</summary>
    [Benchmark(Baseline = true)]
    public void Pipeline_OneEuro_NoAdaptationWiring() => Replay();

    /// <summary>
    /// Phase-1a telemetry attached, adaptation off: the aggregator's O(1) per-sample update plus the
    /// centroid tracker's jitter accumulation. This is what every user pays by default.
    /// </summary>
    [Benchmark]
    public void Pipeline_OneEuro_Telemetry_AdaptationDisabled() => Replay();

    /// <summary>
    /// Adaptation opted in: as above, plus GazeEngine's effective-tolerance read path and the two deferred
    /// -reconfiguration flag checks at the top of OnGazePoint.
    /// </summary>
    [Benchmark]
    public void Pipeline_OneEuro_Telemetry_AdaptationEnabled() => Replay();

    private void Replay()
    {
        var provider = _provider;
        var fixation = _fixation;
        for (int k = 0; k < SamplesPerBatch; k++)
            provider.SimulateGaze(fixation[_cursor++]);
    }

    private void Rebuild(bool attachTelemetry, bool adaptationEnabled)
    {
        _provider = new EyeCareBenchmarkFakes.FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        settings.Adaptation.Enabled = adaptationEnabled;

        // The provider is passed only when adaptation is wired at all; with it null GazeEngine reads the
        // baseline tolerance directly, which is the pre-Phase-1b path.
        var effective = attachTelemetry ? new EffectiveSettingsProvider(settings.Adaptation) : null;
        _engine = new GazeEngine(
            _provider, new EyeCareBenchmarkFakes.FakeInputSimulator(), settings, effectiveSettings: effective);

        if (attachTelemetry)
        {
            // Mirrors App.xaml.cs's 1a wiring, minus the dispatcher hop (see the SCOPE note above): the
            // aggregator reads the engine's latched fixation jitter on the gaze thread and pushes 2 Hz
            // summaries into the buffering sink.
            var aggregator = new GazeQualityAggregator(new BufferingTelemetrySink());
            _engine.GazeSampled += sample => aggregator.OnSample(sample, _engine.LastFixationJitter, 0.0);
        }

        _engine.Start();
        for (int i = 0; i < PrefillSamples; i++)
            _provider.SimulateGaze(_fixation[i]);
        _cursor = PrefillSamples;
    }
}
