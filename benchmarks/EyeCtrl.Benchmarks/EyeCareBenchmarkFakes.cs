using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;

namespace EyeCtrl.Benchmarks;

/// <summary>Shared fakes for the eye-care-wired pipeline benchmarks. Both
/// GazePipelineBenchmarks (Disabled/NoFatigue) and GazePipelineWithFatigueBenchmarks
/// construct an EyeCareService, which requires these. Neither is ever invoked during a
/// timed loop — escalation is suppressed via canInterrupt:false, so OnBreakDue never fires.</summary>
internal static class EyeCareBenchmarkFakes
{
    public static IEyeCareHooks Hooks { get; } = new FakeEyeCareHooks();
    public static IExercise[] ExercisePool { get; } = new IExercise[] { new FakeExercise() };

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
