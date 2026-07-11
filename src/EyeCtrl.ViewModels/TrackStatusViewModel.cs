namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;

/// <summary>
/// ViewModel for the Track Status box. Pure data holder — no DispatcherTimer
/// (that lives in <c>TrackStatusWindow</c>, since ViewModels targets plain net10.0).
/// The window calls <see cref="PollLatest"/> on each timer tick (~15 Hz).
/// </summary>
public partial class TrackStatusViewModel : ObservableObject, IDisposable
{
    private readonly IGazeProvider _gazeProvider;

    [ObservableProperty] private double _leftEyeX;
    [ObservableProperty] private double _leftEyeY;
    [ObservableProperty] private bool _leftEyeValid;
    [ObservableProperty] private double _rightEyeX;
    [ObservableProperty] private double _rightEyeY;
    [ObservableProperty] private bool _rightEyeValid;
    [ObservableProperty] private bool _userPresent;
    [ObservableProperty] private double _headDistanceMm;
    [ObservableProperty] private bool _headDistanceValid;
    [ObservableProperty] private string _distanceQuality = "Unknown";
    [ObservableProperty] private bool _isVisible;

    public TrackStatusViewModel(IGazeProvider gazeProvider)
    {
        _gazeProvider = gazeProvider;
    }

    /// <summary>
    /// Returns true if the provider can supply track-status data.
    /// The window checks this before starting its timer.
    /// </summary>
    public bool HasCapability => _gazeProvider.Capabilities != GazeProviderCapabilities.None;

    /// <summary>Shows the overlay. The window starts its timer after this returns true.</summary>
    public void Start()
    {
        IsVisible = HasCapability;
    }

    /// <summary>Hides the overlay. The window stops its timer.</summary>
    public void Stop()
    {
        IsVisible = false;
    }

    /// <summary>
    /// Called by the window's DispatcherTimer (~15 Hz). Reads the latest cached
    /// sample from the provider (thread-safe) and updates observable properties.
    /// </summary>
    public void PollLatest()
    {
        if (_gazeProvider.TryGetLatestStatus(out var sample))
            UpdateFromSample(sample);
    }

    private void UpdateFromSample(TrackStatusSample sample)
    {
        LeftEyeX = sample.Left.X;
        LeftEyeY = sample.Left.Y;
        LeftEyeValid = sample.Left.IsValid;
        RightEyeX = sample.Right.X;
        RightEyeY = sample.Right.Y;
        RightEyeValid = sample.Right.IsValid;
        UserPresent = sample.UserPresent;
        HeadDistanceMm = sample.HeadDistanceMm;
        HeadDistanceValid = sample.HeadDistanceValid;
        DistanceQuality = ComputeDistanceQuality(sample.HeadDistanceMm, sample.HeadDistanceValid);
    }

    /// <summary>
    /// Maps head distance (mm) to a quality label.
    /// PCEye 5 optimal range ~450–750 mm. Thresholds TBD against real data at impl time.
    /// </summary>
    private static string ComputeDistanceQuality(double distanceMm, bool valid)
    {
        if (!valid) return "Unknown";
        if (distanceMm >= 450 && distanceMm <= 750) return "Good";
        if (distanceMm >= 350 && distanceMm <= 900) return "Fair";
        return "Poor";
    }

    public void Dispose()
    {
        // No timer to dispose — the window owns it.
    }
}
