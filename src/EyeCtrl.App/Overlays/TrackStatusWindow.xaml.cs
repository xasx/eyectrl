namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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

        // Position eye dots in the track box canvas (0..1 → canvas coords)
        if (e.PropertyName is nameof(_viewModel.LeftEyeX) or nameof(_viewModel.LeftEyeY))
        {
            if (_viewModel.LeftEyeValid)
            {
                Canvas.SetLeft(LeftEyeDot, _viewModel.LeftEyeX * 54);
                Canvas.SetTop(LeftEyeDot, _viewModel.LeftEyeY * 34);
                LeftEyeDot.Fill = Brushes.LimeGreen;
                LeftEyeDot.Visibility = Visibility.Visible;
            }
            else
                LeftEyeDot.Visibility = Visibility.Collapsed;
        }
        if (e.PropertyName is nameof(_viewModel.RightEyeX) or nameof(_viewModel.RightEyeY))
        {
            if (_viewModel.RightEyeValid)
            {
                Canvas.SetLeft(RightEyeDot, _viewModel.RightEyeX * 54);
                Canvas.SetTop(RightEyeDot, _viewModel.RightEyeY * 34);
                RightEyeDot.Fill = Brushes.LimeGreen;
                RightEyeDot.Visibility = Visibility.Visible;
            }
            else
                RightEyeDot.Visibility = Visibility.Collapsed;
        }
        if (e.PropertyName == nameof(_viewModel.HeadDistanceMm))
            DistanceText.Text = _viewModel.HeadDistanceValid
                ? $"{_viewModel.HeadDistanceMm:F0} mm"
                : "—";
        if (e.PropertyName == nameof(_viewModel.DistanceQuality))
        {
            QualityText.Text = _viewModel.DistanceQuality;
            QualityText.Foreground = _viewModel.DistanceQuality switch
            {
                "Good" => Brushes.LimeGreen,
                "Fair" => Brushes.Orange,
                "Poor" => Brushes.Red,
                _ => Brushes.Gray
            };
        }
    }

    /// <summary>Positions the window at the top-right corner of the working area.</summary>
    public void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Top + 10;
    }
}
