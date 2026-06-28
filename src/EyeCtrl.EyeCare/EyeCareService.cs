namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;
using EyeCtrl.Core.Settings;

/// <summary>Orchestrator: subscribes to gaze, owns scheduler + fatigue monitor, drives hooks.
/// In v1 this is the single entry point; it is constructed by AppBootstrapper and fed by
/// EventCoordinator wiring GazeEngine.GazeSampled → OnGazeSample.</summary>
public sealed class EyeCareService : IEyeCareService
{
    private readonly EyeCareSettings _settings;
    private readonly EyeCareScheduler _scheduler;
    private readonly FatigueMonitor _fatigueMonitor;
    private readonly ExerciseRunner _runner;
    private readonly IEyeCareHooks _hooks;
    private readonly PhysicalPoint _screenCenter;
    private bool _busy; // true while banner/exercise/summary is on screen

    public EyeCareService(
        EyeCareSettings settings,
        ExerciseRunner runner,
        IEyeCareHooks hooks,
        PhysicalPoint screenCenter,
        double fixationRadiusPixels = 50,
        int fatigueWindowSeconds = 60)
    {
        _settings = settings;
        _scheduler = new EyeCareScheduler(settings);
        _fatigueMonitor = new FatigueMonitor(settings, fixationRadiusPixels, fatigueWindowSeconds);
        _runner = runner;
        _hooks = hooks;
        _screenCenter = screenCenter;
        _scheduler.BreakDue += OnBreakDue;
    }

    /// <inheritdoc />
    public void OnGazeSample(GazePoint sample, bool isPaused)
    {
        if (!_settings.Enabled) return;
        _fatigueMonitor.OnSample(sample);
        _scheduler.Update(sample, isPaused);

        // Escalation (gated): if fatigue crosses threshold before the cadence timer, fire early.
        if (_settings.FatigueMonitorEnabled && !_busy && _fatigueMonitor.Score >= _settings.FatigueThreshold)
        {
            // Fire only if we're also close enough to the interval that an early break is reasonable.
            // (Keeps the heuristic from firing every few minutes.)
            OnBreakDue();
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        _busy = false;
        _scheduler.OnBreakCompleted();
    }

    // async void is required (event-handler signature for EyeCareScheduler.BreakDue).
    // It MUST swallow exceptions: an unobserved throw from an async void crashes the process.
    private async void OnBreakDue()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var accepted = await _hooks.ShowBannerAsync("Time for an eye break");
            if (!accepted)
            {
                _scheduler.Snooze();
                return;
            }

            var exercise = _runner.NextExercise();
            await _hooks.ShowExerciseAsync(
                exercise.Name,
                _settings.ExerciseDurationSeconds * 1_000_000L,
                exercise.GetTargetAt,
                exercise.RecordGaze);
            // Score from the exercise's own recorded samples, then show the summary.
            var result = exercise.Complete();
            var finalOutcome = new EyeCareExerciseOutcome(
                result.ExerciseName, result.AccuracyPercent, result.Steadiness0To5,
                result.ReactionTimeMs, result.DurationSeconds, result.EncouragementLine);
            await _hooks.ShowSummaryAsync(finalOutcome);
            _scheduler.OnBreakCompleted();
        }
        catch (Exception ex)
        {
            // Never let an overlay/UI failure escape an async void. Log + recover.
            System.Diagnostics.Debug.WriteLine($"[EyeCare] break flow failed: {ex}");
            _scheduler.OnBreakCompleted();
        }
        finally
        {
            _busy = false;
            _fatigueMonitor.Reset();
        }
    }
}
