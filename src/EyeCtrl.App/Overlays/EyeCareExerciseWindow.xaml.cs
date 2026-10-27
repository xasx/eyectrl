namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class EyeCareExerciseWindow : OverlayWindow, IEyeCareExercise
{
    /// <summary>
    /// The Skip exit's dwell id. <c>nameof</c> rather than a quoted id, like CalibrationWindow's
    /// Cancel: the id is not UI text, and the literal ratchet treats a string in
    /// <c>new DwellTarget(...)</c> as a text sink.
    /// </summary>
    internal const string SkipTargetId = nameof(SkipButton);

    /// <summary>
    /// The Skip exit's hit radius in DIP — the R4 size floor (≥ 50 DIP, about 1°). The drawn disc
    /// is the same 100 DIP diameter (XAML's SkipTarget Width/Height), so what the user aims at is
    /// the circle gaze is evaluated against.
    /// </summary>
    internal const double SkipHitRadiusDip = 50;

    /// <summary>
    /// Continuous time the gaze must be OUTSIDE the Skip target before it can arm, in ms — the same
    /// 150 ms NoteManager uses. The exercise is shown under wherever the gaze already rests, so a
    /// target that armed on its first sample would end the exercise by itself. One jitter sample
    /// past the edge does not count; a real look-away does.
    /// </summary>
    internal const int SkipLeaveBeforeArmMs = 150;

    private TaskCompletionSource<bool>? _runTcs;
    private DispatcherTimer? _frameTimer;
    private long _startUs;
    private long _durationUs;
    private Action<PhysicalPoint, long>? _recordGaze;  // (gaze point, ELAPSED µs)

    private readonly DwellSettings _dwellSettings;
    private readonly ToolbarSettings _toolbarSettings;
    private readonly IMessenger _messenger;
    private readonly DwellDetector _skipDwell;
    private readonly DwellOutlineOverlay _skipOutline;
    private bool _skipArmed;
    private long? _skipOutsideSinceUs;

    // The detector's filter while the target is disarmed: every target is ineligible, so nothing
    // accumulates and no progress reaches the outline. A static lambda, so the per-sample path
    // allocates nothing.
    private static readonly Func<DwellTarget, bool> SkipDisarmed = _ => false;

    public EyeCareExerciseWindow(DwellSettings? dwellSettings = null, IMessenger? messenger = null,
        IStruggleTelemetrySink? sink = null, IDwellDetectorRegistry? dwellRegistry = null,
        ToolbarSettings? toolbarSettings = null)
    {
        _dwellSettings = dwellSettings ?? new DwellSettings();
        _toolbarSettings = toolbarSettings ?? new ToolbarSettings();
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        InitializeComponent();
        IsClickThrough = true;                 // set here, not in XAML (see banner note)

        // The same outline every key and button shows its dwell progress with. Attached to the
        // disc's own Border so the accent traces the circle (CircleCornerRadius), not a square.
        var label = SkipButton;
        SkipTarget.Child = null;
        _skipOutline = DwellOutlineOverlay.Attach(SkipTarget, label);

        // A1's forgiving dwell: a blink mid-dwell must not restart the Skip hold. The grace is
        // re-read at every run entry (RunAsync), so a live DwellPauseGraceMs change reaches it.
        _skipDwell = new DwellDetector(cooldownMs: 0, context: DwellContext.EyeCare, sink: sink,
            pauseGraceMs: _dwellSettings.DwellPauseGraceMs)
        {
            OtherTargetReleasesHold = true
        };
        _skipDwell.TargetActivated += OnSkipDwellActivated;
        _skipDwell.TargetProgress += (_, progress) => _skipOutline.SetProgress(progress);
        dwellRegistry?.Register(this, DwellRoutingRegistration.ForDetector(
            _skipDwell, DwellContext.EyeCare,
            AdaptationDwellRanges.StandardMinDwellMs, AdaptationDwellRanges.StandardMaxDwellMs));

        SkipButton.Click += (_, _) => FinishRun();
    }

    /// <summary>
    /// The Skip target's hit circle in physical pixels: mid-right edge, vertically centred — or
    /// mid-left when the toolbar is docked Right, so the exit never sits on the toolbar's own
    /// buttons. Fully on-screen, mid-edge and never a corner; away from the bottom-centre band the
    /// keyboard and the banners share. Pure, so the headless tests pin every dock and DPI 1/2.
    /// </summary>
    /// <param name="screenDipWidth">The screen's width in DIP.</param>
    /// <param name="screenDipHeight">The screen's height in DIP.</param>
    /// <param name="toolbar">The live toolbar settings (dock and edge).</param>
    /// <param name="scaleX">Physical pixels per DIP, horizontally.</param>
    /// <param name="scaleY">Physical pixels per DIP, vertically.</param>
    /// <returns>The hit circle's centre (physical px) and radius (physical px).</returns>
    public static (PhysicalPoint Center, double Radius) ComputeSkipGeometry(
        double screenDipWidth, double screenDipHeight, ToolbarSettings toolbar,
        double scaleX, double scaleY) =>
        (EyeCarePlacement.SkipCenter(screenDipWidth, screenDipHeight, toolbar, scaleX, scaleY),
         SkipHitRadiusDip * scaleX);

    /// <summary>The Skip exit's detector — the test seam for its target, grace and activation.</summary>
    internal DwellDetector SkipDwellDetector => _skipDwell;

    /// <summary>The Skip exit's dwell outline — the test seam for visible progress.</summary>
    internal DwellOutlineOverlay SkipDwellOutline => _skipOutline;

    /// <summary>Whether the Skip target has armed (the gaze has been away since the run started).</summary>
    internal bool SkipArmedForTest => _skipArmed;

    /// <summary>The live toolbar settings object — pinning the composition root's wiring.</summary>
    internal ToolbarSettings ToolbarSettingsForTest => _toolbarSettings;

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
        _recordGaze = recordGaze;
        _durationUs = durationMicroseconds;
        _startUs = NowMicroseconds();
        ExerciseName.Text = exerciseName;
        SummaryCard.Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Visible;
        Show();

        BeginSkipTarget();

        _runTcs = new TaskCompletionSource<bool>();
        _frameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var elapsed = NowMicroseconds() - _startUs;        // 0-based elapsed, NOT absolute
            if (elapsed >= _durationUs) { FinishRun(); return; }

            var target = getTargetAt(elapsed);
            Canvas.SetLeft(TargetDot, DpiHelper.PhysicalToLogicalX(target.X) - TargetDot.Width / 2);
            Canvas.SetTop(TargetDot, DpiHelper.PhysicalToLogicalY(target.Y) - TargetDot.Height / 2);

            var remain = Math.Max(0, _durationUs - elapsed);
            // Countdown numbers follow the eye-care surface's language (T02 fixup-01 F4 widened
            // the specifier gate to every numeric specifier, :D2 included).
            Countdown.Text = string.Create(
                EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("eye-care"),
                $"00:{(int)(remain / 1_000_000):D2} / 00:{(int)(_durationUs / 1_000_000):D2}");
            Progress.Value = 100.0 * elapsed / _durationUs;
        }, Dispatcher);
        _frameTimer.Start();
        return _runTcs.Task;
    }

    /// <summary>
    /// Registration-time half of the Skip exit: the target exists only while a run is in flight, is
    /// disarmed until the gaze has looked away, and carries the current settings (the window is a
    /// startup singleton, so the constructor's values would be stale for a live edit).
    /// </summary>
    private void BeginSkipTarget()
    {
        _skipDwell.SetPauseGrace(_dwellSettings.DwellPauseGraceMs);

        var screenWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.PrimaryScreenWidth;
        var screenHeight = ActualHeight > 0 ? ActualHeight : SystemParameters.PrimaryScreenHeight;
        var (center, radius) = ComputeSkipGeometry(
            screenWidth, screenHeight, _toolbarSettings, DpiHelper.ScaleX, DpiHelper.ScaleY);

        // The drawn disc follows the same centre the target is registered at (F3: mid-right, or
        // mid-left for a right-docked toolbar), so a dock edit between runs moves both together.
        Canvas.SetLeft(SkipTarget,
            DpiHelper.PhysicalToLogicalX(center.X) - SkipHitRadiusDip);
        Canvas.SetTop(SkipTarget,
            DpiHelper.PhysicalToLogicalY(center.Y) - SkipHitRadiusDip);
        SkipTarget.Visibility = Visibility.Visible;

        _skipDwell.ClearTargets();
        _skipDwell.AddTarget(new DwellTarget(
            SkipTargetId, center, radius, _dwellSettings.ActivationButtonDwellMs));
        _skipOutline.SetProgress(0);
        _skipArmed = false;
        _skipOutsideSinceUs = null;
    }

    /// <summary>Live gaze: move the green ring and record the sample against the target at the SAME
    /// wall-clock elapsed the animation uses. (The gaze stream's own timestamp is a different clock
    /// domain — tracker vs wall — so we recompute elapsed here rather than trust the incoming µs.)
    /// The same sample also feeds the Skip dwell, gated by the leave-before-arm rule.</summary>
    public void OnGaze(PhysicalPoint gaze, long timestampUs)
    {
        Canvas.SetLeft(GazeRing, DpiHelper.PhysicalToLogicalX(gaze.X) - GazeRing.Width / 2);
        Canvas.SetTop(GazeRing, DpiHelper.PhysicalToLogicalY(gaze.Y) - GazeRing.Height / 2);
        if (_runTcs is not { Task.IsCompleted: false })
            return;

        _recordGaze?.Invoke(gaze, NowMicroseconds() - _startUs);

        if (!_skipArmed)
            UpdateSkipArm(gaze, timestampUs);
        _skipDwell.Update(gaze, timestampUs, _skipArmed ? null : SkipDisarmed);
    }

    /// <summary>
    /// The leave-before-arm latch: a Skip target registered under a resting gaze must not count
    /// that rest. It arms only after <see cref="SkipLeaveBeforeArmMs"/> of continuous samples
    /// outside its hit circle — one jitter sample past the edge is noise, a look-away is not.
    /// </summary>
    private void UpdateSkipArm(PhysicalPoint gaze, long timestampUs)
    {
        if (_skipDwell.HitTest(SkipTargetId, gaze))
        {
            _skipOutsideSinceUs = null;
            return;
        }

        _skipOutsideSinceUs ??= timestampUs;
        if (timestampUs - _skipOutsideSinceUs.Value
            >= SkipLeaveBeforeArmMs * MathConstants.MicrosecondsPerMillisecond)
        {
            _skipArmed = true;
        }
    }

    private void OnSkipDwellActivated(string id) => FinishRun(cue: true);

    /// <param name="cue">True only for the Skip dwell's own path: the dwell surface publishes the
    /// post-effect cue (spec D8), while a click or the timer stays silent.</param>
    private void FinishRun(bool cue = false)
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        Stage.Visibility = Visibility.Collapsed;
        // F5: no dead disc during the 3 s summary — a gaze user must not rest on an exit that no
        // longer responds. RunAsync shows it again for the next run.
        SkipTarget.Visibility = Visibility.Collapsed;
        _skipDwell.ClearTargets();
        _skipOutline.SetProgress(0);
        // Hand control back to EyeCareService, which scores (exercise.Complete) then calls ShowSummaryAsync.
        _runTcs?.TrySetResult(true);
        _runTcs = null;

        if (cue)
            _messenger.Send(new DwellActivationMessage());
    }

    /// <summary>Force-aborts a running exercise / visible summary (e.g. eye-care toggled off).
    /// No-op when nothing is showing.</summary>
    public void Cancel()
    {
        _frameTimer?.Stop();
        _frameTimer = null;
        _recordGaze = null;
        SkipTarget.Visibility = Visibility.Collapsed;
        _skipDwell.ClearTargets();
        _skipOutline.SetProgress(0);
        SummaryCard.Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Collapsed;
        Hide();
        _runTcs?.TrySetResult(false);
        _runTcs = null;
    }

    /// <summary>Shows the summary card with the computed metrics for ~3s, then hides the window.</summary>
    public Task ShowSummaryAsync(EyeCareExerciseOutcome outcome)
    {
        // Every number follows the eye-care surface's language (T02): invariant while it is
        // English, de-DE once its German is effective. Never CultureInfo.Current. The provider is
        // inline at each site so the format scan sees the surface id at the call site.
        SummaryName.Text = StringTable.Current.T("eye-care.summary.complete", outcome.ExerciseName);
        SummaryAccuracy.Text = string.Create(
            EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("eye-care"),
            $"{outcome.AccuracyPercent:0}%");
        SummarySteadiness.Text = string.Create(
            EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("eye-care"),
            $"{outcome.Steadiness0To5:0.0}");
        if (outcome.ReactionTimeMs is { } rt)
        {
            SummaryReactionTime.Text = string.Create(
                EyeCtrl.Core.Localization.StringTable.Current.FormatProviderFor("eye-care"),
                $"{rt:0}");
            ReactionTimePanel.Visibility = Visibility.Visible;
        }
        else
        {
            ReactionTimePanel.Visibility = Visibility.Collapsed;
        }
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
