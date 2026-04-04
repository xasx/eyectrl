namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomGazeIndicatorViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>,
    IRecipient<UpdateZoomIndicatorPositionMessage>,
    IRecipient<UpdateZoomIndicatorProgressMessage>,
    IRecipient<ResetZoomIndicatorMessage>,
    IDisposable
{
    private readonly IMessenger _messenger;
    private bool _disposed;

    // Stable configuration value from settings (never changes after Configure)
    private double _outerCircleDiameterSetting;

    // Position in screen coordinates (magnified space)
    [ObservableProperty] private double _indicatorX;
    [ObservableProperty] private double _indicatorY;

    // Outer circle diameter — animated from OuterDiameter → CenterDotDiameter during fixation
    [ObservableProperty] private double _outerCircleDiameter;

    // Whether the indicator is visible
    [ObservableProperty] private bool _isVisible;

    // Fixation progress 0.0 → 1.0 (raw, before easing)
    [ObservableProperty] private double _fixationProgress;

    // Zoom geometry needed for coordinate mapping
    private PhysicalPoint _zoomOriginalCenter = new(0, 0);  // Capture center (gaze point)
    private PhysicalPoint _zoomWindowCenter = new(0, 0);    // Display center (may be screen center)
    private double _magnification = 1.0;

    public double CenterDotDiameter { get; private set; }
    public double StrokeThickness { get; private set; }
    public double Opacity { get; private set; }
    public int ResetAnimationMs { get; private set; }

    public ZoomGazeIndicatorViewModel(IMessenger messenger)
    {
        _messenger = messenger;
        messenger.RegisterAll(this);
    }

    /// <summary>
    /// Configure the indicator with settings. Called once at startup.
    /// </summary>
    public void Configure(EyeCtrlSettings settings)
    {
        _outerCircleDiameterSetting = settings.Zoom.GazeIndicatorOuterDiameter;
        _outerCircleDiameter = _outerCircleDiameterSetting;
        CenterDotDiameter = settings.Zoom.GazeIndicatorCenterDotDiameter;
        StrokeThickness = settings.Zoom.GazeIndicatorStrokeThickness;
        Opacity = settings.Zoom.GazeIndicatorOpacity;
        ResetAnimationMs = settings.Zoom.GazeIndicatorResetAnimationMs;
    }

    public void Receive(StartZoomMessage message)
    {
        // The capture center is the gaze point (what we're zooming in on)
        _zoomOriginalCenter = message.GazePoint;
        _magnification = message.Magnification;

        // The zoom window center is where the overlay is displayed (gaze point in AtGaze mode, screen center in AtCenter mode)
        _zoomWindowCenter = message.Center;

        OuterCircleDiameter = _outerCircleDiameterSetting;
        FixationProgress = 0.0;
        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _outerCircleDiameterSetting;
    }

    public void Receive(UpdateZoomIndicatorPositionMessage message)
    {
        UpdateGazePosition(message.GazePoint);
    }

    public void Receive(UpdateZoomIndicatorProgressMessage message)
    {
        UpdateFixationProgress(message.Progress);
    }

    public void Receive(ResetZoomIndicatorMessage message)
    {
        ResetFixation();
    }

    /// <summary>
    /// Called from App.xaml.cs with the current gaze screen point.
    /// Converts to magnified coordinates and updates position.
    /// </summary>
    public void UpdateGazePosition(PhysicalPoint gazeScreenPoint)
    {
        // Convert screen gaze to magnified coordinates:
        // magnifiedPoint = zoomWindowCenter + (gazePoint - originalCenter) * magnification
        var magX = _zoomWindowCenter.X + (gazeScreenPoint.X - _zoomOriginalCenter.X) * _magnification;
        var magY = _zoomWindowCenter.Y + (gazeScreenPoint.Y - _zoomOriginalCenter.Y) * _magnification;

        IndicatorX = magX;
        IndicatorY = magY;
    }

    /// <summary>
    /// Called from App.xaml.cs with the fixation progress (0.0–1.0).
    /// Applies ease-in curve and updates outer circle diameter.
    /// </summary>
    public void UpdateFixationProgress(double progress)
    {
        FixationProgress = progress;

        // Apply quadratic ease-in: slow start, accelerates
        var eased = progress * progress;

        // Interpolate diameter: outerDiameter → centerDotDiameter
        var maxDiam = _outerCircleDiameterSetting;
        var minDiam = CenterDotDiameter;
        OuterCircleDiameter = maxDiam - (maxDiam - minDiam) * eased;
    }

    /// <summary>
    /// Called when fixation is aborted — triggers reset animation.
    /// The caller (window code-behind) uses ResetAnimationMs for timing.
    /// </summary>
    public void ResetFixation()
    {
        FixationProgress = 0.0;
        // OuterCircleDiameter will be animated back to full by the window code-behind
        // using the ResetAnimationMs duration. The animation will set the final value.
        // We don't set OuterCircleDiameter here to avoid bypassing the animation.
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _messenger.UnregisterAll(this);
        _disposed = true;
    }
}
