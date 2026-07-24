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
        public int BannerCalls;
        public Task<bool> ShowBannerAsync(string message) { BannerCalls++; return Task.FromResult(false); }
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
        return new EyeCareService(settings, runner, hooks);
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
    public void DisabledGap_DoesNotTriggerBreak_OnReEnable()
    {
        var settings = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        for (long t = 0; t <= 30_000_000; t += 1_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        settings.Enabled = false;
        for (long t = 31_000_000; t <= 1_530_000_000; t += 60_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        settings.Enabled = true;
        svc.OnGazeSample(new GazePoint(500, 500, 1_531_000_000, IsValid: true), isPaused: false);

        Check.That(hooks.BannerCalls).IsEqualTo(0);
    }

    [Fact]
    public void Dispose_UnsubscribesFromSettings()
    {
        var settings = new EyeCareSettings { Enabled = true };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        svc.Dispose();
        settings.Enabled = false;

        Check.That(hooks.CancelActiveCalls).IsEqualTo(0);
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

    [Fact]
    public void Disabling_ResetsFatigueScore()
    {
        // OnGazeSample stops updating fatigue when eye-care is disabled, so without a reset the
        // monitor holds the last score and reports stale fatigue. The GazeQualitySummary contract
        // documents fatigue as "0 when fatigue monitoring is disabled" — disabling must zero it.
        var settings = new EyeCareSettings
        {
            Enabled = true,
            FatigueMonitorEnabled = true,
            IntervalMinutes = 10,
            NaturalBreakSeconds = 30,
            SnoozeMinutes = 10,
        };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        // Accumulate fixation (low gaze spread) to drive the score up. canInterrupt: false keeps
        // the fatigue-driven escalation from firing (and resetting the monitor via its break flow),
        // so the score is observable.
        for (long t = 0; t < 5_000_000; t += 100_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false, canInterrupt: false);

        Check.That(svc.FatigueScore > 0.0).IsTrue();

        settings.Enabled = false;

        Check.That(svc.FatigueScore).IsEqualTo(0.0);
    }
}
