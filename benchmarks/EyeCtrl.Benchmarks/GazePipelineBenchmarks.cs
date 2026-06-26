using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

namespace EyeCtrl.Benchmarks;

/// <summary>
/// Layer 4 — end-to-end pipeline benchmarks. A real GazeEngine wired to private nested fakes
/// (mirroring tests/EyeCtrl.Core.Tests/GazeEngineTests.cs). The provider's GazePointReceived is
/// raised synchronously once per sample inside the timed loop.
/// </summary>
/// <remarks>
/// PER-ITERATION REBUILD: each benchmark gets a fresh provider+engine pair via a targeted
/// [IterationSetup], for two reasons: (a) InteractionManager starts in FreeLook and leaves it once
/// the Fixation scenario triggers, so reusing an engine would bias later iterations; (b) the
/// GazeEngine constructor subscribes to the provider's GazePointReceived event, so reusing a
/// provider across engines would stack subscriptions. [IterationCleanup] disposes the engine.
/// (BenchmarkDotNet runs many invocations per iteration on the one engine, so after fixation
/// triggers the state converges to Fixation and stays there — the engine drives no dwell — so the
/// reported number is steady-state-fixation cost. That is the intended apples-to-apples basis:
/// filter mode is the only variable across the four benchmarks.)
/// </remarks>
[SimpleJob(RuntimeMoniker.Net10_0)]
[MemoryDiagnoser]
[RankColumn]
public class GazePipelineBenchmarks
{
    private GazePoint[] _fixation = null!;
    private GazePoint[] _noisyFixation = null!;

    // Built fresh per iteration by the targeted IterationSetup methods below — never reused across
    // iterations, so each measured run starts in FreeLook with no stacked event subscriptions.
    private FakeGazeProvider _provider = null!;
    private GazeEngine _engine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixation = GazeStreamGenerator.GenerateFixation();
        _noisyFixation = GazeStreamGenerator.GenerateNoisyFixation();
    }

    [IterationSetup(Target = nameof(Pipeline_Direct_Fixation))]
    public void SetupDirect() => Rebuild(GazeFilterMode.Direct);

    [IterationSetup(Target = nameof(Pipeline_Smoothed_Fixation))]
    public void SetupSmoothed() => Rebuild(GazeFilterMode.Smoothed);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_Fixation))]
    public void SetupOneEuroFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationSetup(Target = nameof(Pipeline_OneEuro_NoisyFixation))]
    public void SetupOneEuroNoisyFixation() => Rebuild(GazeFilterMode.OneEuro);

    [IterationCleanup]
    public void IterationCleanup() => _engine.Dispose();

    [Benchmark]
    public void Pipeline_Direct_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_Smoothed_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_Fixation() => Replay(_fixation);

    [Benchmark]
    public void Pipeline_OneEuro_NoisyFixation() => Replay(_noisyFixation);

    private void Replay(GazePoint[] stream)
    {
        for (int i = 0; i < stream.Length; i++)
            _provider.SimulateGaze(stream[i]);
    }

    // Builds the fresh provider+engine pair for the upcoming iteration. The engine subscribes to
    // _provider in its constructor, so SimulateGaze on _provider drives exactly this engine.
    private void Rebuild(GazeFilterMode mode)
    {
        _provider = new FakeGazeProvider();
        var settings = new EyeCtrlSettings();           // SelectionMode defaults to None
        settings.Cursor.FilterMode = mode;
        _engine = new GazeEngine(_provider, new FakeInputSimulator(), settings);
        _engine.Start();
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
        // Records nothing during the timed loop — avoids contaminating allocation numbers.
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
