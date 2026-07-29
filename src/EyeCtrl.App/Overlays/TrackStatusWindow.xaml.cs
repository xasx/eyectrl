namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.ViewModels;

public partial class TrackStatusWindow : OverlayWindow
{
    private TrackStatusViewModel? _viewModel;
    private readonly DispatcherTimer _timer;

    public TrackStatusWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(66) // ~15 Hz
        };
        _timer.Tick += OnTimerTick;
    }

    public void Configure(TrackStatusViewModel viewModel)
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>Called by EventCoordinator when TrackStatusSettings.Enabled is toggled on.</summary>
    public void StartTracking()
    {
        if (_viewModel == null) return;
        _viewModel.Start();
        if (_viewModel.IsVisible)
        {
            PositionTopRight();
            Show();
            _timer.Start();
        }
    }

    /// <summary>Called by EventCoordinator when TrackStatusSettings.Enabled is toggled off.</summary>
    public void StopTracking()
    {
        _timer.Stop();
        _viewModel?.Stop();
        Hide();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Poll the provider's cache (thread-safe) and update VM properties.
        _viewModel?.PollLatest();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel == null) return;

        // React to validity changes too, not just X/Y: when an eye goes invalid (e.g. the
        // "no signal" clear on disconnect) while its coordinates are unchanged, [ObservableProperty]
        // suppresses the X/Y notification, so keying only on X/Y would leave a stale dot on screen.
        switch (e.PropertyName)
        {
            case nameof(_viewModel.LeftEyeX):
            case nameof(_viewModel.LeftEyeY):
            case nameof(_viewModel.LeftEyeValid):
                UpdateEyeDot(LeftEyeDot, _viewModel.LeftEyeValid, _viewModel.LeftEyeX, _viewModel.LeftEyeY);
                break;
            case nameof(_viewModel.RightEyeX):
            case nameof(_viewModel.RightEyeY):
            case nameof(_viewModel.RightEyeValid):
                UpdateEyeDot(RightEyeDot, _viewModel.RightEyeValid, _viewModel.RightEyeX, _viewModel.RightEyeY);
                break;
            case nameof(_viewModel.HeadDistanceMm):
            case nameof(_viewModel.HeadDistanceValid):
                DistanceText.Text = _viewModel.HeadDistanceValid
                    ? $"{_viewModel.HeadDistanceMm:F0} mm"
                    : "—";
                break;
            case nameof(_viewModel.DistanceQuality):
                QualityText.Text = _viewModel.DistanceQuality;
                // Quality is a semantic triad: success/warning/danger, carried by the palette
                // tokens rather than raw Brushes.
                QualityText.Foreground = _viewModel.DistanceQuality switch
                {
                    "Good" => Tokens.BrushAccentSuccess,
                    "Fair" => Tokens.BrushAccentWarning,
                    "Poor" => Tokens.BrushAccentDanger,
                    _ => Tokens.BrushTextDim
                };
                break;
        }
    }

    /// <summary>Positions an eye dot in the track-box canvas (0..1 → canvas coords), or hides it when invalid.</summary>
    private static void UpdateEyeDot(Ellipse dot, bool valid, double normalizedX, double normalizedY)
    {
        if (!valid)
        {
            dot.Visibility = Visibility.Collapsed;
            return;
        }
        Canvas.SetLeft(dot, normalizedX * 54);
        Canvas.SetTop(dot, normalizedY * 34);
        dot.Fill = Tokens.BrushAccentSuccess;
        dot.Visibility = Visibility.Visible;
    }

    /// <summary>Positions the window at the top-right corner of the working area.</summary>
    public void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Top + 10;
    }
}
