namespace EyeCtrl.EyeCare;

using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.EyeCare.Exercises;
using EyeCtrl.EyeCare.Scoring;
using EyeCtrl.Core.Settings;

/// <summary>Orchestrator: subscribes to gaze, owns scheduler + fatigue monitor, drives hooks.
/// In v1 this is the single entry point; it is constructed by AppBootstrapper and fed by
/// EventCoordinator wiring GazeEngine.GazeSampled → OnGazeSample.</summary>
public sealed class EyeCareService : IEyeCareService, IDisposable
{
    private readonly EyeCareSettings _settings;
    private readonly EyeCareScheduler _scheduler;
    private readonly FatigueMonitor _fatigueMonitor;
    private readonly ExerciseRunner _runner;
    private readonly IEyeCareHooks _hooks;

    // Touched from the gaze background thread and from async-void continuations (thread-pool).
    // volatile gives the re-entrancy guard cross-thread visibility.
    private volatile bool _busy; // true while banner/exercise/summary is on screen

    // Timestamp of the last sample actually fed to the fatigue monitor. Written and read on the gaze
    // thread only (both this service's handler and the gaze-quality bridge run there), so a plain
    // field is sufficient. Long.MinValue means "nothing processed yet" and cannot collide with a real
    // tracker timestamp.
    private long _lastFatigueSampleUs = long.MinValue;

    public EyeCareService(
        EyeCareSettings settings,
        ExerciseRunner runner,
        IEyeCareHooks hooks,
        double fixationRadiusPixels = 50,
        int fatigueWindowSeconds = 60)
    {
        _settings = settings;
        _scheduler = new EyeCareScheduler(settings);
        _fatigueMonitor = new FatigueMonitor(settings, fixationRadiusPixels, fatigueWindowSeconds);
        _runner = runner;
        _hooks = hooks;
        _scheduler.BreakDue += OnBreakDue;
        // Toggling eye-care off mid-session must immediately dismiss any visible overlay.
        _settings.PropertyChanged += OnSettingsChanged;
    }

    /// <inheritdoc />
    public void OnGazeSample(GazePoint sample, bool isPaused, bool canInterrupt = true)
    {
        _scheduler.Update(sample, isPaused, canInterrupt);
        if (!_settings.Enabled) return;
        _fatigueMonitor.OnSample(sample);
        _lastFatigueSampleUs = sample.TimestampMicroseconds;

        // Escalation (gated, off by default): if fatigue crosses the threshold, request an early
        // break — but only when it is safe to interrupt (FreeLook) and no break is already running.
        if (_settings.FatigueMonitorEnabled && !_busy && canInterrupt
            && _fatigueMonitor.Score >= _settings.FatigueThreshold)
        {
            OnBreakDue();
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EyeCareSettings.Enabled) && !_settings.Enabled)
        {
            _hooks.CancelActive();
            Cancel();
            // OnGazeSample stops feeding the monitor once eye-care is disabled, so without a reset
            // it would hold the last score and report stale fatigue. GazeQualitySummary documents
            // fatigue as "0 when fatigue monitoring is disabled" — clear it on the toggle.
            _fatigueMonitor.Reset();
        }
    }

    /// <inheritdoc />
    public void Cancel()
    {
        _busy = false;
        _scheduler.OnBreakCompleted();
    }

    /// <inheritdoc />
    public double FatigueScore => _fatigueMonitor.Score;

    /// <inheritdoc />
    public bool TryGetFatigueScore(long sampleTimestampUs, out double score)
    {
        if (_lastFatigueSampleUs != sampleTimestampUs)
        {
            score = 0.0;
            return false;
        }
        score = _fatigueMonitor.Score;
        return true;
    }

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        _scheduler.BreakDue -= OnBreakDue;
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
            exercise.Reset(); // exercises are reused singletons — clear prior-run samples/timestamps
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
