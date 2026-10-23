namespace EyeCtrl.EyeCare.Tests;

using CommunityToolkit.Mvvm.Messaging;
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
        public string? BannerMessage;
        public Task<bool> ShowBannerAsync(string message) { BannerCalls++; BannerMessage = message; return Task.FromResult(false); }
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

    /// <summary>
    /// T08 fixup R1 (mutant M19): the banner's message is the eye-care surface's key, and nothing
    /// pinned its English — swapping it for the subtext ("You've been fixating for a while") stayed
    /// green. The fake hooks capture what the banner would show.
    /// </summary>
    [Fact]
    public void BreakBanner_ShowsTheKeyedEnglishMessage()
    {
        var settings = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var hooks = new FakeHooks();
        var svc = NewService(settings, hooks);

        for (long t = 0; t <= 70_000_000; t += 1_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(hooks.BannerCalls).IsStrictlyGreaterThan(0);
        Check.That(hooks.BannerMessage).IsEqualTo("Time for an eye break");
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

    // ── Fatigue staleness (Fix 3) ──

    [Fact]
    public void TryGetFatigueScore_ReturnsFalse_ForASampleItNeverProcessed()
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        Check.That(sut.TryGetFatigueScore(12_345, out _)).IsFalse();
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsTrue_ForTheSampleItJustProcessed()
    {
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        sut.OnGazeSample(new GazePoint(960, 540, 5_000_000, IsValid: true), isPaused: false);

        Check.That(sut.TryGetFatigueScore(5_000_000, out _)).IsTrue();
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsFalse_ForALaterSample_SoAFrozenScoreCannotLeak()
    {
        // The regression that matters: eye care processed sample N, then stopped (a fault). A consumer
        // asking about sample N+1 must be told the score is stale, not handed the old one.
        var settings = new EyeCareSettings { Enabled = true, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        sut.OnGazeSample(new GazePoint(960, 540, 5_000_000, IsValid: true), isPaused: false);

        Check.That(sut.TryGetFatigueScore(5_033_333, out var score)).IsFalse();
        Check.That(score).IsEqualTo(0.0);
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsFalse_WhenEyeCareIsDisabled()
    {
        // A disabled service never stamps, so even the sample it just received is reported stale —
        // which is how a consumer substitutes 0 instead of reading a frozen score.
        var settings = new EyeCareSettings { Enabled = false, FatigueMonitorEnabled = true };
        var sut = NewService(settings, new FakeHooks());

        sut.OnGazeSample(new GazePoint(960, 540, 5_000_000, IsValid: true), isPaused: false);

        Check.That(sut.TryGetFatigueScore(5_000_000, out var score)).IsFalse();
        Check.That(score).IsEqualTo(0.0);
    }

    [Fact]
    public void TryGetFatigueScore_ReturnsTheLiveScore_ForTheSampleItJustProcessed()
    {
        // The fresh path must carry the monitor's actual value, not just "fresh = true" — an
        // implementation returning (true, 0.0) would otherwise pass every staleness test.
        var settings = new EyeCareSettings
        {
            Enabled = true,
            FatigueMonitorEnabled = true,
            IntervalMinutes = 10,
            NaturalBreakSeconds = 30,
            SnoozeMinutes = 10,
        };
        var sut = NewService(settings, new FakeHooks());

        // Accumulate fixation (low gaze spread) to drive the score up, mirroring
        // Disabling_ResetsFatigueScore. canInterrupt: false keeps the fatigue-driven escalation
        // from firing (and resetting the monitor via its break flow).
        long last = 0;
        for (long t = 0; t < 5_000_000; t += 100_000)
        {
            last = t;
            sut.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false, canInterrupt: false);
        }

        Check.That(sut.TryGetFatigueScore(last, out var score)).IsTrue();
        Check.That(score).IsEqualTo(sut.FatigueScore);
        Check.That(score).IsStrictlyPositive();
    }

    /// <summary>
    /// P2 D1 (review-02 B-1a / review-03 n-2): the exercise-active flag is sent ONCE, (true) before
    /// the run and (false) in the finally after the summary. The gap between FinishRun and
    /// ShowSummaryAsync — where the full-screen window is still up — must be covered, so the flag
    /// must still read true AT SUMMARY START. The hooks complete synchronously, so "at summary
    /// start" is observable from inside ShowSummaryAsync.
    /// </summary>
    [Fact]
    public void TheExerciseActiveFlag_SpansTheRunAndTheSummary()
    {
        var settings = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var hooks = new AcceptingHooks();
        var messenger = new WeakReferenceMessenger();
        var runner = new ExerciseRunner(new IExercise[] { new StubExercise() }, seed: 1);
        var svc = new EyeCareService(settings, runner, hooks, messenger: messenger);
        messenger.Register<object, EyeCareExerciseActiveMessage>(this, (_, m) => hooks.ActiveFlag = m.Active);

        for (long t = 0; t <= 70_000_000; t += 1_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(hooks.BannerCalls).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage("the flag must still read true when the summary starts — the "
                + "full-screen window is still shown between the run and the summary")
            .That(hooks.ActiveFlagAtSummaryStart).IsTrue();
        Check.WithCustomMessage("the finally must close the flag after the summary")
            .That(hooks.ActiveFlag).IsFalse();
    }

    [Fact]
    public void AFaultedExercise_StillLowersTheFlag()
    {
        // The finally, not the happy path, owns the (false): a faulting exercise (or a CancelActive
        // ending the awaits) must leave the note gate closed behind no stuck suspender.
        var settings = new EyeCareSettings
        {
            Enabled = true, IntervalMinutes = 1, NaturalBreakSeconds = 30, SnoozeMinutes = 1
        };
        var hooks = new FaultingHooks();
        var messenger = new WeakReferenceMessenger();
        var runner = new ExerciseRunner(new IExercise[] { new StubExercise() }, seed: 1);
        var svc = new EyeCareService(settings, runner, hooks, messenger: messenger);
        messenger.Register<object, EyeCareExerciseActiveMessage>(this, (_, m) =>
        {
            hooks.ActiveFlag = m.Active;
            if (m.Active) hooks.SawTrue = true;
        });

        for (long t = 0; t <= 70_000_000; t += 1_000_000)
            svc.OnGazeSample(new GazePoint(500, 500, t, IsValid: true), isPaused: false);

        Check.That(hooks.SawTrue).IsTrue();
        Check.That(hooks.ActiveFlag).IsFalse();
    }

    /// <summary>Accepting hooks: the banner is dwelled Start, the exercise and summary complete
    /// synchronously, and the flag value is captured at summary start.</summary>
    private sealed class AcceptingHooks : IEyeCareHooks
    {
        public int BannerCalls;
        public bool ActiveFlag;
        public bool ActiveFlagAtSummaryStart;

        public Task<bool> ShowBannerAsync(string message) { BannerCalls++; return Task.FromResult(true); }

        public Task ShowExerciseAsync(string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze) => Task.CompletedTask;

        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
        {
            ActiveFlagAtSummaryStart = ActiveFlag;
            return Task.CompletedTask;
        }

        public void CancelActive() { }
    }

    private sealed class FaultingHooks : IEyeCareHooks
    {
        public bool ActiveFlag;
        public bool SawTrue;

        public Task<bool> ShowBannerAsync(string message) => Task.FromResult(true);

        public Task ShowExerciseAsync(string exerciseName, long durationMicroseconds,
            Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze)
            => Task.FromException(new InvalidOperationException("exercise failed"));

        public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome) => Task.CompletedTask;

        public void CancelActive() { }
    }
}
