namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Models;
using EyeCtrl.ViewModels;

public partial class CalibrationWindow : OverlayWindow, ICalibrationOverlay
{
    private CalibrationViewModel? _viewModel;
    private Storyboard? _pulseAnimation;
    private DispatcherTimer? _autoDismissTimer;

    public CalibrationWindow()
    {
        InitializeComponent();

        // Retry discards the current (poor) result and starts a fresh session.
        RetryButton.Click += (_, _) =>
        {
            RetryPanel.Visibility = Visibility.Collapsed;
            _viewModel?.Retry();
        };

        // Accept keeps the result and returns the VM to Idle so re-calibration works later.
        AcceptButton.Click += (_, _) => DismissAndReset();

        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var state = _viewModel?.State ?? CalibrationState.Idle;
        if (state is CalibrationState.Collecting or CalibrationState.Training)
            _viewModel?.Cancel();
        else
            DismissAndReset();
    }

    public void SetViewModel(CalibrationViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalibrationViewModel.CurrentTargetPosition):
                Dispatcher.Invoke(() => PositionTarget(_viewModel!.CurrentTargetPosition));
                break;
            case nameof(CalibrationViewModel.BandRadiusPixels):
                Dispatcher.Invoke(() => PositionTarget(_viewModel!.CurrentTargetPosition));
                break;
            case nameof(CalibrationViewModel.TargetProgress):
            case nameof(CalibrationViewModel.IsOnTarget):
                Dispatcher.Invoke(UpdateOnTargetFeedback);
                break;
            case nameof(CalibrationViewModel.State):
                Dispatcher.Invoke(() => UpdateVisualState(_viewModel!.State, _viewModel.ProgressPercent));
                break;
            case nameof(CalibrationViewModel.TargetColor):
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var brush = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString(_viewModel!.TargetColor));
                        TargetDot.Fill = brush;
                    }
                    catch { }
                });
                break;
        }
    }

    private void PositionTarget(PhysicalPoint physicalPoint)
    {
        var logical = physicalPoint.ToLogical();

        // The dot: the thing to aim at, from the setting that until now was read by nothing.
        var dotDiameterLogical = DiameterToLogical(_viewModel?.TargetRadiusPixels ?? 20);
        TargetDot.Width = dotDiameterLogical;
        TargetDot.Height = dotDiameterLogical;
        if (TargetDot.RenderTransform is ScaleTransform dotScale)
        {
            dotScale.CenterX = dotDiameterLogical / 2;
            dotScale.CenterY = dotDiameterLogical / 2;
        }
        Centre(TargetDot, logical, dotDiameterLogical);

        // The rings: the tolerance actually enforced. Converted from physical px through the same
        // DPI helper the dot uses, because the collector's radius is in physical pixels and this
        // canvas is in DIPs — drawing the raw number would be right only at 100 % scaling.
        var bandDiameterLogical = DiameterToLogical(_viewModel?.BandRadiusPixels ?? 0);
        foreach (var ring in new[] { ToleranceRing, ProgressRing })
        {
            ring.Width = bandDiameterLogical;
            ring.Height = bandDiameterLogical;
            Centre(ring, logical, bandDiameterLogical);
        }

        UpdateOnTargetFeedback();
    }

    private static void Centre(FrameworkElement element, LogicalPoint logicalCentre, double diameter)
    {
        Canvas.SetLeft(element, logicalCentre.X - (diameter / 2));
        Canvas.SetTop(element, logicalCentre.Y - (diameter / 2));
    }

    /// <summary>
    /// Physical-pixel RADIUS to logical DIAMETER, via the app's DPI scale.
    /// <para>
    /// Takes the X scale only, so a display whose horizontal and vertical DPI scaling differ would get
    /// a circle sized by its width. That is deliberate rather than overlooked: these are CIRCLES, and
    /// the band they represent is a radial distance — one number, equal on both axes by definition
    /// (see <c>CalibrationGeometry.BandRadiusPx</c>, which scales by the shorter side). Honouring two
    /// scales would mean drawing an ellipse for a tolerance that is not elliptical, which is a worse
    /// lie than a slightly wrong size. Windows does not expose independent per-axis scaling in any
    /// case, and the rest of this file's geometry (<c>ToLogical</c> on the target position) makes the
    /// same assumption.
    /// </para>
    /// </summary>
    private static double DiameterToLogical(double radiusPhysical)
    {
        var edge = new PhysicalPoint(radiusPhysical * 2, radiusPhysical * 2).ToLogical();
        return Math.Max(2, edge.X);
    }

    /// <summary>
    /// Makes "inside the ring" and "about to leave" legible without text. The progress ring brightens
    /// and thickens as in-band samples accumulate, and the whole ring dims the moment gaze leaves the
    /// band — so the user learns what advances the target by watching it, which is the only channel
    /// available to someone who cannot read a number and hold a fixation at the same time.
    /// </summary>
    private void UpdateOnTargetFeedback()
    {
        if (_viewModel is null) return;

        var progress = Math.Clamp(_viewModel.TargetProgress, 0, 1);
        ProgressRing.Opacity = progress <= 0 ? 0 : 0.35 + (0.65 * progress);
        ProgressRing.StrokeThickness = 4 + (8 * progress);

        // The tolerance ring itself is the on/off signal: solid while gaze is inside it, faint while
        // it is not. Deliberately a property of the RING rather than of the dot — the ring is the
        // thing whose meaning is being taught.
        ToleranceRing.Opacity = _viewModel.IsOnTarget ? 0.9 : 0.3;
        ToleranceRing.StrokeThickness = _viewModel.IsOnTarget ? 4 : 2;
    }

    public void UpdateVisualState(CalibrationState state, double progress)
    {
        StopAutoDismiss();

        switch (state)
        {
            case CalibrationState.Collecting:
                Backdrop.Visibility = Visibility.Visible;
                TargetCanvas.Visibility = Visibility.Visible;
                CompletionPanel.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Visible;
                CancelHint.Visibility = Visibility.Visible;
                Show();
                // Mouse passes through (never blocks the screen), but keep keyboard focus so Esc
                // cancels. MUST come after Show(): SetOverlayInteraction early-returns while the
                // HWND is IntPtr.Zero, and the window is only ever constructed — never shown — at
                // startup, so on the FIRST calibration the HWND is created by this very Show().
                // Called before it, this no-opped and OverlayWindow.OnSourceInitialized then
                // applied focusable:false, leaving the first run unfocusable: Activate() below was
                // blocked by WS_EX_NOACTIVATE and Esc did nothing, despite the visible hint.
                SetOverlayInteraction(clickThrough: true, focusable: true);
                Activate();
                var idx = _viewModel?.CurrentTargetIndex ?? 0;
                var total = _viewModel?.TotalTargets ?? 0;
                ProgressText.Text = $"Look at the dot \u00B7 {idx + 1} / {total}";
                StartPulse();
                break;

            case CalibrationState.Training:
                TargetCanvas.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                StopPulse();
                ProgressText.Text = "Computing...";
                break;

            case CalibrationState.Active:
                ShowCompletion(goodResult: true);
                break;

            case CalibrationState.Failed:
                ShowCompletion(goodResult: false);
                break;

            default:
                TargetCanvas.Visibility = Visibility.Collapsed;
                ProgressText.Visibility = Visibility.Collapsed;
                CancelHint.Visibility = Visibility.Collapsed;
                Backdrop.Visibility = Visibility.Collapsed;
                CompletionPanel.Visibility = Visibility.Collapsed;
                StopPulse();
                StopAutoDismiss();
                break;
        }
    }

    private void ShowCompletion(bool goodResult)
    {
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        CancelHint.Visibility = Visibility.Visible;
        Backdrop.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Visible;
        StopPulse();

        // Make the window opaque + focusable + hit-testable so Retry/Accept and Esc work.
        // After Show() for the same reason as the Collecting case above — Failed can be reached
        // without a preceding Collecting (a session that never starts), so the HWND is not
        // guaranteed to exist by the time we get here.
        Show();
        SetOverlayInteraction(clickThrough: false, focusable: true);
        Activate();
        Focus();

        var errorPx = _viewModel?.MeanErrorPixels ?? 0;
        // Quality % is computed in the VM against the physical-pixel screen diagonal
        // (CalibrationData.ComputeQualityPercent) \u2014 DPI-correct, single source of truth.
        var qualityPercent = _viewModel?.QualityPercent ?? 0;
        var isGood = qualityPercent < 2.0;

        if (goodResult && isGood)
        {
            CompletionSymbol.Text = "\u2713";
            CompletionSymbol.Foreground = Tokens.BrushAccentSuccess;
            CompletionTitle.Text = "Calibration complete";
            CompletionError.Text = $"Mean error: {errorPx:F1} px ({qualityPercent:F1}%)";
            CompletionHint.Visibility = Visibility.Visible;
            RetryPanel.Visibility = Visibility.Collapsed;
            StartAutoDismiss(3000, DismissAndReset);
        }
        else
        {
            CompletionSymbol.Text = "\u26A0";
            CompletionSymbol.Foreground = Tokens.BrushAccentWarning;
            CompletionTitle.Text = goodResult ? "Calibration complete" : "Calibration failed";
            CompletionError.Text = goodResult
                ? $"Mean error: {errorPx:F1} px ({qualityPercent:F1}%) \u2014 poor quality"
                : _viewModel?.ErrorText ?? "";
            CompletionHint.Visibility = Visibility.Collapsed;
            RetryPanel.Visibility = goodResult ? Visibility.Visible : Visibility.Collapsed;
            // Safety net: never trap the user on the overlay even if they can't actuate a button.
            StartAutoDismiss(12000, DismissAndReset);
        }
    }

    /// <summary>Dismisses the overlay and returns the VM to Idle so re-calibration is possible.</summary>
    private void DismissAndReset()
    {
        _viewModel?.Acknowledge();
        DismissCalibration();
    }

    public void DismissCalibration()
    {
        Backdrop.Visibility = Visibility.Collapsed;
        TargetCanvas.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
        CompletionPanel.Visibility = Visibility.Collapsed;
        CancelHint.Visibility = Visibility.Collapsed;
        RetryPanel.Visibility = Visibility.Collapsed;
        StopPulse();
        StopAutoDismiss();
        // Restore the default non-activating overlay style.
        SetOverlayInteraction(clickThrough: IsClickThrough, focusable: false);
        Hide();
    }

    private void StartPulse()
    {
        StopPulse();
        _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var duration = new Duration(TimeSpan.FromMilliseconds(600));

        var scaleX = new DoubleAnimation(0.8, 1.2, duration) { AutoReverse = true };
        Storyboard.SetTarget(scaleX, TargetDot);
        Storyboard.SetTargetProperty(scaleX,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        _pulseAnimation.Children.Add(scaleX);

        // Animate Y too so the dot pulses uniformly instead of stretching horizontally.
        var scaleY = new DoubleAnimation(0.8, 1.2, duration) { AutoReverse = true };
        Storyboard.SetTarget(scaleY, TargetDot);
        Storyboard.SetTargetProperty(scaleY,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        _pulseAnimation.Children.Add(scaleY);

        _pulseAnimation.Begin();
    }

    private void StopPulse()
    {
        _pulseAnimation?.Stop();
        _pulseAnimation = null;
    }

    private void StartAutoDismiss(int delayMs, Action onDismiss)
    {
        StopAutoDismiss();
        _autoDismissTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(delayMs), DispatcherPriority.Normal,
            (_, _) => { StopAutoDismiss(); onDismiss(); }, Dispatcher);
        _autoDismissTimer.Start();
    }

    private void StopAutoDismiss()
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }
}
