namespace EyeCtrl.App.Overlays;

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

public partial class CalibrationWindow : OverlayWindow
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
        Canvas.SetLeft(TargetDot, logical.X - TargetDot.Width / 2);
        Canvas.SetTop(TargetDot, logical.Y - TargetDot.Height / 2);
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
