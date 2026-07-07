using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare;
using EyeCtrl.EyeCare.Exercises;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — the full O(window) fatigue scan wired into the real GazeEngine pipeline. This is
/// the one eye-care-pipeline variant that CANNOT share GazePipelineBenchmarks: it runs the
/// FatigueMonitor scan every sample, so BDN's multi-invocation model would grow the window
/// unboundedly under fixed-ts replay (same flaw as Layer 5). invocationCount:1 + a per-iteration
/// RebuildWithEyeCare (which pre-fills + converges Fixation) makes each measured op one clean
/// 1000-sample replay over a full, evicting window on a continuous timestamp axis.
/// </summary>
/// <remarks>
/// Disabled / Enabled_NoFatigue live in GazePipelineBenchmarks instead — they early-return
/// before the queue, so they tolerate multi-invocation and can share the class with the 4
/// originals (which need many invocations to converge FreeLook -> Fixation).
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0, invocationCount: 1)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineWithFatigueBenchmarks
{
    private const int WindowSeconds = 60;
    private const int SamplesPerSec = 30;
    private const int SteadyTail = 1000;
    private const int PrefillSamples = WindowSeconds * SamplesPerSec + 2;

    private GazePoint[] _fixationTimed = null!;
    private GazePoint[] _fixationPrefill = null!;

    private FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;
    private EyeCareService _service = null!;

    [GlobalSetup]
    public void Setup()
    {
        var fixationLong = GazeStreamGenerator.GenerateFixation(length: PrefillSamples + SteadyTail);
        _fixationPrefill = fixationLong[..PrefillSamples];
        _fixationTimed = fixationLong[PrefillSamples..];
    }

    [IterationSetup]
    public void IterationSetup() => RebuildWithEyeCare();

    [IterationCleanup]
    public void IterationCleanup()
    {
        _engine.Dispose();
        _service?.Dispose();
    }

    [Benchmark]
    public void Pipeline_OneEuro_Fixation_WithEyeCare_WithFatigue() => Replay(_fixationTimed);

    private void Replay(GazePoint[] stream)
    {
        for (int i = 0; i < stream.Length; i++)
            _provider.SimulateGaze(stream[i]);
    }

    private void RebuildWithEyeCare()
    {
        _provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();
        settings.Cursor.FilterMode = GazeFilterMode.OneEuro;
        settings.EyeCare.Enabled = true;
        settings.EyeCare.FatigueMonitorEnabled = true;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _service = new EyeCareService(
            settings.EyeCare,
            new ExerciseRunner(EyeCareBenchmarkFakes.ExercisePool, seed: 0),
            EyeCareBenchmarkFakes.Hooks);
        _engine.GazeSampled += sample =>
            _service.OnGazeSample(sample, isPaused: false, canInterrupt: false);
        _engine.Start();
        for (int i = 0; i < _fixationPrefill.Length; i++)
            _provider.SimulateGaze(_fixationPrefill[i]);
    }

    private sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    private sealed class FakeInputSimulator : IInputSimulator
    {
        public void MoveCursor(PhysicalPoint p) { }
        public void LeftClick(PhysicalPoint p) { }
        public void RightClick(PhysicalPoint p) { }
        public void DoubleClick(PhysicalPoint p) { }
        public void MouseDown(PhysicalPoint p) { }
        public void MouseUp(PhysicalPoint p) { }
        public void Scroll(PhysicalPoint p, int dx, int dy) { }
        public void SendKeyPress(ushort vk) { }
        public void SendKeyDown(ushort vk) { }
        public void SendKeyUp(ushort vk) { }
        public void SendText(string text) { }
        public void SendModifiedKeyPress(IReadOnlySet<ModifierKey> modifiers, ushort virtualKeyCode) { }
    }
}
