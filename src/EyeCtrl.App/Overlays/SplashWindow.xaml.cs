namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using Serilog;

public partial class SplashWindow : OverlayWindow, ISplashWindow
{
    private bool _sentenceShown;

    private System.Diagnostics.Stopwatch _shownAt = System.Diagnostics.Stopwatch.StartNew();  // dead-man clock only
    private System.Diagnostics.Stopwatch? _exitArmedAt;
    private Storyboard? _baseFillStoryboard;          // the fill's ONE clock — BeginTime carries the 0.55 s
    private DispatcherTimer? _deadManTimer;
    private DispatcherTimer? _hardDeadlineTimer;
    private DispatcherTimer? _holdTimer;

    private readonly SplashExitController _controller = new();

    internal SplashBeat CurrentBeat => _controller.Beat;

    internal string? ClosedReason { get; private set; }

    /// <summary>Raised once after the exit finished; the LAUNCHER shuts the splash dispatcher down
    /// (the window never owns that decision — in tests nobody subscribes, so the test dispatcher
    /// survives). r4 self-review fix. Public as part of ISplashWindow (the source pins match
    /// call-shape text, not accessibility).</summary>
    public event Action? ExitFinished;

    public SplashWindow()
    {
        InitializeComponent();

        // The splash thread has no Application resources: theme-derived values come from Tokens'
        // frozen brushes (they freeze at source — ThemeBase.CachedBrush), pinned colors are XAML
        // literals (spec §8). Fonts/radius already ride x:Static, which reads Tokens.Current at
        // parse time — the theme is activated before this thread starts.
        CardBorder.Background = Tokens.BrushSurfaceDark;
        TitleText.Foreground = Tokens.BrushTextPrimary;
        NoTrackerSentence.Foreground = Tokens.BrushTextPrimary;

        IsClickThrough = true;                          // L13: the card must never swallow dwells
    }

    /// <summary>The order repair would run from the splash thread and touch main-thread windows
    /// (r2 #4): the splash never asks for it.</summary>
    protected override bool NeedsOrderRepairAfterBandFallback => false;

    /// <summary>Test/verification accessor for the band-fallback override: true when this window
    /// opted OUT of the post-fallback order repair (NeedsOrderRepairAfterBandFallback is false).</summary>
    internal bool DeclaresNoOrderRepair => !NeedsOrderRepairAfterBandFallback;

    /// <summary>Whether ShowNoTrackerSentence has been called.</summary>
    internal bool SentenceShown => _sentenceShown;

    /// <summary>
    /// Whether the splash says "no eye tracker found" (Wave B B5): only when the provider FELL BACK
    /// to the mouse — a tracker was asked for (Auto, or a Tobii type) and the mouse is what resolved.
    /// Never when the mouse was chosen explicitly, and never in test mode, whose provider is always
    /// the mouse.
    /// </summary>
    /// <param name="requested">The provider type the settings asked for.</param>
    /// <param name="resolved">The provider type actually in use (decorators unwrapped).</param>
    /// <param name="isTestMode">Whether the app runs in test mode.</param>
    /// <returns>True when the sentence should show.</returns>
    internal static bool ShowsNoTrackerSentence(GazeProviderType requested, GazeProviderType resolved, bool isTestMode) =>
        !isTestMode && requested != GazeProviderType.Mouse && resolved == GazeProviderType.Mouse;

    /// <summary>
    /// Fades the no-tracker sentence in inside its always-reserved row (spec §7). The fade runs
    /// only when the window is visible (production); on an unshown window — tests, headless — the
    /// opacity is set directly so the outcome is deterministic. Public as part of ISplashWindow.
    /// </summary>
    public void ShowNoTrackerSentence()
    {
        if (_sentenceShown) return;
        _sentenceShown = true;
        try
        {
            if (IsVisible)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250));
                NoTrackerSentence.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            else
            {
                NoTrackerSentence.Opacity = 1;
            }
        }
        catch (Exception ex)
        {
            NoTrackerSentence.Opacity = 1;              // containment: shown statically beats lost
            Log.Warning(ex, "Splash: sentence fade failed; shown statically");
        }
    }

    /// <summary>Called once on the splash thread after Show() (Task 6): entrance + the fill clock.
    /// Public as part of ISplashWindow.</summary>
    public void OnSplashShown()
    {
        SafeRun(() =>
        {
            _shownAt.Restart();
            _controller.AdvanceTo(SplashBeat.Entrance);
            StartEntrance();
            Log.Information("Splash: shown");
            ArmDeadMan();
            StartBaseFillClock();
        });
    }

    /// <summary>The readiness request — marshalled here by SplashThreadHandle (Task 6). Public as
    /// part of ISplashWindow.</summary>
    public void BeginExit()
    {
        SafeRun(() =>
        {
            var remaining = RemainingBaseCourse();
            if (!_controller.TryBeginExit(remaining, out var duration))
                return;                                     // tail already running, or dead-man closed

            _exitArmedAt ??= System.Diagnostics.Stopwatch.StartNew();   // armed only on the ACCEPTED request
            Log.Information("Splash: ready after {ShownElapsed} ms; catch-up {Duration} ms",
                _shownAt.ElapsedMilliseconds, duration.TotalMilliseconds);

            StopBaseFillClock();
            ArmHardDeadline();                          // every accepted request is backstopped — also the straight-to-title path (review Minor 2)
            if (duration <= TimeSpan.Zero)
            {
                RunTitle();                                 // remaining ≈ 0 (slow start): straight tail
                return;
            }
            StartCatchUp(duration);
        });
    }

    private void StartEntrance()
    {
        ((Storyboard)Resources["SbTrail"]).Begin(this, true);
        ((Storyboard)Resources["SbDotPop"]).Begin(this, true);
    }

    /// <summary>
    /// The fill is ONE storyboard clock with BeginTime = 0.55 s (spec §5 beat 3) — no wall-clock
    /// bookkeeping, no second timer (r4 #10). Its Completed handler parks the beat in Filled and
    /// WAITS; only BeginExit may start the tail (spec §6.1, r4 #5). Begun controllable against
    /// <c>this</c>: <see cref="Storyboard.Begin(FrameworkElement, bool)"/> is the path that BOTH
    /// creates the clock tree AND applies it to the targets (the bare <c>CreateClock()</c> only
    /// ticks — the DPs would never animate), and the saved clock is what GetCurrentTime/Stop read.
    /// </summary>
    private void StartBaseFillClock()
    {
        _baseFillStoryboard = BuildSweepFillStoryboard(SplashExitController.FillBaseCourse, fromCurrent: false);
        _baseFillStoryboard.BeginTime = TimeSpan.FromMilliseconds(550);
        _baseFillStoryboard.Completed += (_, _) =>
        {
            if (_controller.Beat is SplashBeat.Entrance)
                _controller.AdvanceTo(SplashBeat.Filled);   // wait for readiness — nothing else happens
        };
        _baseFillStoryboard.Begin(this, isControllable: true);
    }

    private void StopBaseFillClock()
    {
        try { _baseFillStoryboard?.Stop(this); }
        catch (Exception ex) { Log.Debug(ex, "Splash: base fill clock already stopped"); }
        _baseFillStoryboard = null;
    }

    private void StartCatchUp(TimeSpan duration)
    {
        _controller.AdvanceTo(SplashBeat.CatchUp);
        var catchUp = BuildSweepFillStoryboard(duration, fromCurrent: true);
        catchUp.Completed += (_, _) => RunTitle();
        catchUp.Begin(this, true);
    }

    private void RunTitle()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Title);
            var title = CloneBoard("SbTitle");
            title.Completed += (_, _) =>
            {
                _controller.AdvanceTo(SplashBeat.Hold);
                RunExitAfterHold();
            };
            title.Begin(this, true);
        });
    }

    private void RunExit()
    {
        SafeRun(() =>
        {
            _controller.AdvanceTo(SplashBeat.Exit);
            var exit = CloneBoard("SbExit");
            exit.Completed += (_, _) => FinishClose("closed");
            exit.Begin(this, true);
        });
    }

    /// <summary>
    /// SbTitle/SbExit live in <see cref="FrameworkElement.Resources"/> and re-beginning the shared
    /// instance would append another Completed handler each run (r4 #5): each run clones the
    /// resource instead — the clone carries the handler, so handlers never accumulate.
    /// </summary>
    private Storyboard CloneBoard(string key)
    {
        var clone = ((Storyboard)Resources[key]).Clone();
        return clone;
    }

    /// <summary>
    /// The sweep+fill board. <paramref name="fromCurrent"/> = the catch-up: From = the CURRENT
    /// animated values (captured here — a DP read returns the effective animated value), To = the
    /// finals, cubic ease-out — the arc continues from where it is, never restarting (r4 #3).
    /// Otherwise the base course: fixed From values, the spec's KeySpline via SplineDoubleKeyFrame.
    ///
    /// The rotate child targets the ARC ELEMENT (DwellArc) with the "RenderTransform.Angle"
    /// sub-path, not the RotateTransform itself: with the window as containing object, a clock on a
    /// direct Freezable target ticks (state, CurrentTime, Completed all run) but its values never
    /// reach the DP — probed headless (SplashClockProbe P6/D1–D3 vs E4/E5/F1/F2, probe file since
    /// deleted); the element target + sub-path applies in every environment, and the animation
    /// still lands on the same ArcRotate instance, so the captured-value read below stays valid.
    /// </summary>
    private Storyboard BuildSweepFillStoryboard(TimeSpan duration, bool fromCurrent)
    {
        var board = new Storyboard();
        var durationKf = new System.Windows.Duration(duration);

        // The capture reads the CURRENT animated values. That is only valid because BeginExit stops
        // the fill clock and begins the catch-up in the SAME synchronous call: ClockController.Stop()
        // defers the property reversion to the next tick (PendingInteractiveStop), so the DPs still
        // hold the fill's animated values here. BeginExit_MidFill pins this capture-order dependency
        // — a synchronous reversion would capture the rest values and restart the arc (r4 #3).
        var angleFrom = fromCurrent ? ArcRotate.Angle : -180.0;
        var offsetFrom = fromCurrent ? DwellArc.StrokeDashOffset : 24.36;
        var opacityFrom = fromCurrent ? DwellArc.Opacity : 0.0;

        if (fromCurrent)
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var rotate = new DoubleAnimation(angleFrom, 0, durationKf) { EasingFunction = easeOut };
            var fill = new DoubleAnimation(offsetFrom, 0, durationKf) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var gate = new DoubleAnimation(opacityFrom, 1, durationKf) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            board.Children.Add(rotate);
            board.Children.Add(fill);
            board.Children.Add(gate);
            Storyboard.SetTarget(rotate, DwellArc);
            Storyboard.SetTargetProperty(rotate, new PropertyPath("RenderTransform.Angle"));
            Storyboard.SetTarget(fill, DwellArc);
            Storyboard.SetTargetProperty(fill, new PropertyPath("StrokeDashOffset"));
            Storyboard.SetTarget(gate, DwellArc);
            Storyboard.SetTargetProperty(gate, new PropertyPath("Opacity"));
        }
        else
        {
            var rotate = new DoubleAnimationUsingKeyFrames();
            rotate.KeyFrames.Add(new LinearDoubleKeyFrame(-180, KeyTime.FromPercent(0)));
            rotate.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.3, 0.55, 0.3, 1)));
            var fill = new DoubleAnimationUsingKeyFrames();
            fill.KeyFrames.Add(new LinearDoubleKeyFrame(24.36, KeyTime.FromPercent(0)));
            fill.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromPercent(1), new KeySpline(0.3, 0.55, 0.3, 1)));
            var gate = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120));
            board.Children.Add(rotate);
            board.Children.Add(fill);
            board.Children.Add(gate);
            board.Duration = durationKf;
            Storyboard.SetTarget(rotate, DwellArc);
            Storyboard.SetTargetProperty(rotate, new PropertyPath("RenderTransform.Angle"));
            Storyboard.SetTarget(fill, DwellArc);
            Storyboard.SetTargetProperty(fill, new PropertyPath("StrokeDashOffset"));
            Storyboard.SetTarget(gate, DwellArc);
            Storyboard.SetTargetProperty(gate, new PropertyPath("Opacity"));
        }
        return board;
    }

    /// <summary>Remaining base course at BeginExit, from the fill clock itself — never a Stopwatch
    /// (r4 #10). GetCurrentTime is COURSE-LOCAL: null inside the 0.55 s BeginTime delay, then
    /// 0 → base course (clamped at the end while Filled), so the un-run remainder is
    /// FillBaseCourse − now, clamped at zero. The 550 ms offset must NOT be subtracted again
    /// (review fix: that made mid-fill remainders 550 ms too long — an L16 violation whenever the
    /// true remainder sat below the 1 s floor — and turned the Filled "0 remaining → straight to
    /// title" path into dead code).</summary>
    private TimeSpan RemainingBaseCourse()
    {
        var now = _baseFillStoryboard?.GetCurrentTime(this);
        if (now is null) return SplashExitController.FillBaseCourse;      // still inside the 0.55 s BeginTime delay
        var remaining = SplashExitController.FillBaseCourse - now.Value;  // course-local: 0 at course start
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>Dead-man backstop (L9): ONE self-stopping shot scheduled AT the deadline — the
    /// predicate is re-checked in the tick (review fix: a short poll fired once while the sequence
    /// was still young and never again, so a hung sequence had no rescue).</summary>
    private void ArmDeadMan()
    {
        _deadManTimer = NewTimer(SplashExitController.DeadManTimeout, () =>
        {
            if (_controller.ShouldDeadManClose(_shownAt.Elapsed)) FinishClose("dead-man");
        });
    }

    /// <summary>Hard-deadline backstop (L9): ONE self-stopping shot AT the deadline after the
    /// accepted request (review Minor 2: armed on EVERY accepted BeginExit — also the
    /// straight-to-title path); the predicate is re-checked in the tick.</summary>
    private void ArmHardDeadline()
    {
        _hardDeadlineTimer = NewTimer(SplashExitController.HardExitDeadline, () =>
        {
            if (_exitArmedAt is { } armed && _controller.ShouldHardClose(armed.Elapsed)) FinishClose("hard-deadline");
        });
    }

    /// <summary>The tail after the title: hold 0.20 s (self-stopping one-shot timer), then exit.</summary>
    private void RunExitAfterHold()
    {
        _holdTimer = NewTimer(TimeSpan.FromMilliseconds(200), RunExit);
    }

    /// <summary>One-shot self-stopping timer (r4 #2): DispatcherTimer repeats unless stopped —
    /// the timer stops itself before its tick runs. Registers exactly one Tick handler
    /// (review Minor 6: the constructor callback slot stays empty instead of holding a no-op).</summary>
    private DispatcherTimer NewTimer(TimeSpan due, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        timer.Interval = due;
        timer.Tick += (_, _) => { timer.Stop(); tick(); };
        timer.Start();
        return timer;
    }

    private void FinishClose(string reason)
    {
        if (ClosedReason is not null) return;               // idempotent: first close wins
        ClosedReason = reason;
        _controller.MarkClosed();
        StopAllTimers();
        Log.Information("Splash: closed after {ShownElapsed} ms ({Reason})", _shownAt.ElapsedMilliseconds, reason);
        ForceClose();
        ExitFinished?.Invoke();
    }

    /// <summary>The fatal-path close (App's catch path via the handle): same full teardown as any
    /// other close — controller marked closed, timers stopped, ExitFinished raised (r4 #11).
    /// Public as part of ISplashWindow.</summary>
    public void FatalClose() => SafeRun(() => FinishClose("fatal"));

    private void StopAllTimers()
    {
        _deadManTimer?.Stop();
        _hardDeadlineTimer?.Stop();
        _holdTimer?.Stop();
    }

    /// <summary>Public as part of ISplashWindow: the launcher's DPI report reads it on the splash
    /// thread.</summary>
    public bool TryReadDpiScale(out double scaleX, out double scaleY)
    {
        scaleX = scaleY = 0;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not { } target) return false;
            var m = target.TransformToDevice;
            scaleX = m.M11;
            scaleY = m.M22;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: DPI read failed");
            return false;
        }
    }

    /// <summary>Every public entry runs inside this: a fault degrades to a close, never a trap.</summary>
    private void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Warning(ex, "Splash: beat failed; closing");
            FinishClose("beat-failure");
        }
    }

    /// <summary>Exception-contained hard close used by the fatal path and the dead-man timers.
    /// Public as part of ISplashWindow.</summary>
    public void ForceClose()
    {
        try { Close(); }
        catch (Exception ex) { Log.Warning(ex, "Splash: hard close failed"); }
    }
}
