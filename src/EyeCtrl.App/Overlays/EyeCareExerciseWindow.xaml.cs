namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

public partial class EyeCareExerciseWindow : OverlayWindow
{
    private TaskCompletionSource<bool>? _runTcs;
    private DispatcherTimer? _frameTimer;
    private long _startUs;
    private long _durationUs;
    private Func<long, PhysicalPoint>? _getTargetAt;  // ELAPSED µs → target position
    private Action<PhysicalPoint, long>? _recordGaze;  // (gaze point, ELAPSED µs)

    public EyeCareExerciseWindow()
    {
        InitializeComponent();
        IsClickThrough = true;                 // set here, not in XAML (see banner note)
        SkipButton.Click += (_, _) => FinishRun();
    }

    // ONE wall clock for both the visible animation and gaze recording, so the dot the user
    // follows and the target used for scoring share a single 0-based elapsed timeline.
    private static long NowMicroseconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * MathConstants.MicrosecondsPerMillisecond;

    /// <summary>Runs the moving-target animation for the duration; resolves when time elapses or
    /// the user skips. Scoring is the caller's job (via the recordGaze callback + IExercise.Complete).
    /// <paramref name="getTargetAt"/> takes ELAPSED µs (0-based) — exercises assume t starts at 0.</summary>
    public Task RunAsync(string exerciseName, long durationMicroseconds,
        Func<long, PhysicalPoint> getTargetAt, Action<PhysicalPoint, long> recordGaze)
    {
        _getTargetAt = getTargetAt;
        _recordGaze = recordGaze;
        _durationUs = durationMicroseconds;
        _startUs = NowMicroseconds();
        ExerciseName.Text = exerciseName;
        SummaryCard.Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Visible;
        Show();

        _runTcs = new TaskCompletionSource<bool>();
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var elapsed = NowMicroseconds() - _startUs;        // 0-based elapsed, NOT absolute
            if (elapsed >= _durationUs) { FinishRun(); return; }

            var target = getTargetAt(elapsed);
            Canvas.SetLeft(TargetDot, target.X - TargetDot.Width / 2);
            Canvas.SetTop(TargetDot, target.Y - TargetDot.Height / 2);

            var remain = Math.Max(0, _durationUs - elapsed);
            Countdown.Text = $"00:{(int)(remain / 1_000_000):D2} / 00:{(int)(_durationUs / 1_000_000):D2}";
            Progress.Value = 100.0 * elapsed / _durationUs;
        }, Dispatcher);
        _frameTimer.Start();
        return _runTcs.Task;
    }

    /// <summary>Live gaze: move the green ring and record the sample against the target at the SAME
    /// wall-clock elapsed the animation uses. (The gaze stream's own timestamp is a different clock
    /// domain — tracker vs wall — so we recompute elapsed here rather than trust the incoming µs.)</summary>
    public void OnGaze(PhysicalPoint gaze, long timestampUs)
    {
        Canvas.SetLeft(GazeRing, gaze.X - GazeRing.Width / 2);
        Canvas.SetTop(GazeRing, gaze.Y - GazeRing.Height / 2);
        if (_runTcs is { Task.IsCompleted: false })
            _recordGaze?.Invoke(gaze, NowMicroseconds() - _startUs);
    }

    private void FinishRun()
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        Stage.Visibility = Visibility.Collapsed;
        // Hand control back to EyeCareService, which scores (exercise.Complete) then calls ShowSummaryAsync.
        _runTcs?.TrySetResult(true);
        _runTcs = null;
    }

    /// <summary>Shows the summary card with the computed metrics for ~3s, then hides the window.</summary>
    public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
    {
        SummaryName.Text = $"{outcome.ExerciseName} complete";
        SummaryAccuracy.Text = $"{outcome.AccuracyPercent:0}%";
        SummarySteadiness.Text = $"{outcome.Steadiness0To5:0.0}";
        SummaryEncourage.Text = outcome.EncouragementLine;
        SummaryCard.Visibility = Visibility.Visible;

        var tcs = new TaskCompletionSource<bool>();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Normal, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            SummaryCard.Visibility = Visibility.Collapsed;
            Hide();
            tcs.TrySetResult(true);
        }, Dispatcher);
        timer.Start();
        return tcs.Task;
    }
}
