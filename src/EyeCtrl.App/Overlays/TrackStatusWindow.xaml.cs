namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class TrackStatusWindow : OverlayWindow, ITrackStatusOverlay
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
                // The number and its unit are one keyed template (T06), formatted with the
                // surface's provider: "{0:F0} mm" is identical in both languages, and the
                // provider keeps a future format specifier's separators following the language.
                // CultureInfo.Current is never read, so a de-DE machine shows "600 mm" in the
                // English UI.
                DistanceText.Text = _viewModel.HeadDistanceValid
                    ? StringTable.Current.T("track-status.distance.mm", _viewModel.HeadDistanceMm)
                    : "—";
                break;
            case nameof(_viewModel.DistanceQuality):
                QualityText.Text = QualityWord(StringTable.Current, _viewModel.DistanceQuality);
                // Quality is a semantic triad: success/warning/danger, carried by the palette
                // tokens rather than raw Brushes. Picked from the enum, never from the word.
                QualityText.Foreground = QualityBrush(_viewModel.DistanceQuality);
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

    /// <summary>
    /// The word shown for a quality, resolved from the given table. Deliberately separate from
    /// <see cref="QualityBrush"/>: this is the single place the quality's display text comes
    /// from, so translating the word cannot change the colour. The table is a parameter rather
    /// than the process global so the mapping is testable without activating one.
    /// </summary>
    /// <param name="strings">The table to resolve the word from.</param>
    /// <param name="quality">The quality to name.</param>
    /// <returns>The word shown beside "Quality:".</returns>
    internal static string QualityWord(StringTable strings, DistanceQuality quality) => quality switch
    {
        DistanceQuality.Good => strings.T("track-status.quality.good"),
        DistanceQuality.Fair => strings.T("track-status.quality.fair"),
        DistanceQuality.Poor => strings.T("track-status.quality.poor"),
        _ => strings.T("track-status.quality.unknown")
    };

    /// <summary>
    /// The brush a quality is shown in: the success/warning/danger triad from the palette
    /// tokens, dim for unknown. Keyed on the enum, never on <see cref="QualityWord"/>, so a
    /// translated word cannot turn every quality grey.
    /// </summary>
    /// <param name="quality">The quality to colour.</param>
    /// <returns>The palette brush for the quality.</returns>
    internal static SolidColorBrush QualityBrush(DistanceQuality quality) => quality switch
    {
        DistanceQuality.Good => Tokens.BrushAccentSuccess,
        DistanceQuality.Fair => Tokens.BrushAccentWarning,
        DistanceQuality.Poor => Tokens.BrushAccentDanger,
        _ => Tokens.BrushTextDim
    };

    /// <summary>Positions the window at the top-right corner of the working area.</summary>
    public void PositionTopRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 10;
        Top = workArea.Top + 10;
    }

    /// <inheritdoc />
    public bool TryGetVisibleBounds(out PhysicalRect bounds)
    {
        bounds = default;
        if (!IsVisible)
            return false;
        // The window's own rect in DIP, scaled to physical px (a length and a coordinate scale
        // identically — the helper just multiplies).
        bounds = new PhysicalRect(
            DpiHelper.LogicalToPhysicalX(Left),
            DpiHelper.LogicalToPhysicalY(Top),
            DpiHelper.LogicalToPhysicalX(Width),
            DpiHelper.LogicalToPhysicalY(Height));
        return true;
    }
}
