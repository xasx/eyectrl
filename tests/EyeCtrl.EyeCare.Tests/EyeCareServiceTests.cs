namespace EyeCtrl.EyeCare.Tests;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;

public class EyeCareServiceTests
{
    private sealed class FakeHooks : IEyeCareHooks
    {
        public int CancelActiveCalls;
        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(false);
        public Task ShowExerciseAsync(string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze) => Task.CompletedTask;
        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;
        public void CancelActive() => CancelActiveCalls++;
    }

    private sealed class StubExercise : ExerciseBase
    {
        public override string Name => "Stub";
        public override double OnTargetRadiusPixels => 10;
        protected override string EncouragementLine => "ok";
        public override PhysicalPoint GetTargetAt(long timestampMicroseconds) => new(0, 0);
        public override ExerciseResult Complete() => BuildResult();
    }

    private static EyeCareService NewService(EyeCareSettings settings, FakeHooks hooks)
    {
        var runner = new ExerciseRunner(new IExercise[] { new StubExercise() }, seed: 1);
        return new EyeCareService(settings, runner, hooks, new PhysicalPoint(0, 0));
    }

    [Fact]
    public void DisablingMidSession_CancelsActiveOverlay()
    {
        var settings = new EyeCareSettings { Enabled = true };
        var hooks = new FakeHooks();
        _ = NewService(settings, hooks);

        settings.Enabled = false;

        Check.That(hooks.CancelActiveCalls).IsEqualTo(1);
    }

    [Fact]
    public void EnablingOrOtherChanges_DoNotCancel()
    {
        var settings = new EyeCareSettings { Enabled = false };
        var hooks = new FakeHooks();
        _ = NewService(settings, hooks);

        settings.Enabled = true;            // enabling must not cancel
        settings.IntervalMinutes = 42;      // unrelated change must not cancel

        Check.That(hooks.CancelActiveCalls).IsEqualTo(0);
    }
}
