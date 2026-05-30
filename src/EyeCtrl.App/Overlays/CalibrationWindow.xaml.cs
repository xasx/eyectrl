namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

        RetryButton.Click += (_, _) =>
        {
            RetryPanel.Visibility = Visibility.Collapsed;
            _viewModel?.Cancel();
        };

        AcceptButton.Click += (_, _) =>
        {
            DismissCalibration();
        };
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
        Canvas.SetLeft(TargetDot, logical.X - 20);
        Canvas.SetTop(TargetDot, logical.Y - 20);
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
        CancelHint.Visibility = Visibility.Collapsed;
        Backdrop.Visibility = Visibility.Visible;
        CompletionPanel.Visibility = Visibility.Visible;
        StopPulse();

        var errorPx = _viewModel?.MeanErrorPixels ?? 0;
        var diagonal = Math.Sqrt(
            SystemParameters.PrimaryScreenWidth * SystemParameters.PrimaryScreenWidth +
            SystemParameters.PrimaryScreenHeight * SystemParameters.PrimaryScreenHeight);
        var errorPercent = diagonal > 0 ? errorPx / diagonal * 100 : 0;
        var isGood = errorPercent < 2.0;

        if (goodResult && isGood)
        {
            CompletionSymbol.Text = "\u2713";
            CompletionSymbol.Foreground = new SolidColorBrush(Colors.Lime);
            CompletionTitle.Text = "Calibration complete";
            CompletionError.Text = $"Mean error: {errorPx:F1} px ({errorPercent:F1}%)";
            CompletionHint.Visibility = Visibility.Visible;
            RetryPanel.Visibility = Visibility.Collapsed;
            StartAutoDismiss(3000, DismissCalibration);
        }
        else
        {
            CompletionSymbol.Text = "\u26A0";
            CompletionSymbol.Foreground = new SolidColorBrush(Color.FromRgb(255, 170, 0));
            CompletionTitle.Text = goodResult ? "Calibration complete" : "Calibration failed";
            CompletionError.Text = goodResult
                ? $"Mean error: {errorPx:F1} px ({errorPercent:F1}%) \u2014 poor quality"
                : _viewModel?.ErrorText ?? "";
            CompletionHint.Visibility = Visibility.Collapsed;
            RetryPanel.Visibility = goodResult ? Visibility.Visible : Visibility.Collapsed;
        }
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
        Hide();
    }

    private void StartPulse()
    {
        StopPulse();
        _pulseAnimation = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var scaleAnim = new DoubleAnimation(0.8, 1.2, new Duration(TimeSpan.FromMilliseconds(600)))
        {
            AutoReverse = true
        };
        Storyboard.SetTarget(scaleAnim, TargetDot);
        Storyboard.SetTargetProperty(scaleAnim,
            new PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        _pulseAnimation.Children.Add(scaleAnim);
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
