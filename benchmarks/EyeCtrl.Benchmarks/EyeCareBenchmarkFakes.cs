using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;

namespace EyeCtrl.Benchmarks;

/// <summary>Shared fakes for the gaze-pipeline benchmarks. GazePipelineBenchmarks (the 4
/// originals + Disabled/NoFatigue) and GazePipelineWithFatigueBenchmarks both construct a
/// GazeEngine from <see cref="FakeGazeProvider"/>/<see cref="FakeInputSimulator"/>; the
/// eye-care-wired variants additionally construct an EyeCareService, which requires Hooks
/// and ExercisePool. Neither Hooks nor ExercisePool is ever invoked during a timed loop —
/// escalation is suppressed via canInterrupt:false, so OnBreakDue never fires.</summary>
internal static class EyeCareBenchmarkFakes
{
    public static IEyeCareHooks Hooks { get; } = new FakeEyeCareHooks();
    public static IExercise[] ExercisePool { get; } = new IExercise[] { new FakeExercise() };

    /// <summary>Provider whose <see cref="SimulateGaze"/> raises
    /// <see cref="GazePointReceived"/> synchronously once per sample, driving the GazeEngine
    /// that subscribed in its constructor. Callers instantiate fresh per iteration.</summary>
    public sealed class FakeGazeProvider : IGazeProvider
    {
        public event Action<GazePoint>? GazePointReceived;
        public bool IsConnected { get; private set; }
        public void Start() => IsConnected = true;
        public void Stop() => IsConnected = false;
        public void Dispose() => Stop();
        public void SimulateGaze(GazePoint point) => GazePointReceived?.Invoke(point);
    }

    /// <summary>Records nothing during the timed loop — avoids contaminating allocation numbers.</summary>
    public sealed class FakeInputSimulator : IInputSimulator
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

    private sealed class FakeEyeCareHooks : IEyeCareHooks
    {
        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(false);
        public Task ShowExerciseAsync(
            string exerciseName,
            long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt,
            Action<PhysicalPoint, long> recordGaze) => Task.CompletedTask;
        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;
        public void CancelActive() { }
    }

    private sealed class FakeExercise : IExercise
    {
        public string Name => "Fake";
        public double OnTargetRadiusPixels => 50.0;
        public PhysicalPoint GetTargetAt(long timestampMicroseconds) => new(960, 540);
        public void RecordGaze(PhysicalPoint gaze, long timestampMicroseconds) { }
        public ExerciseResult Complete() => new("Fake", 0, 0, null, 0, "");
        public void Reset() { }
    }
}
