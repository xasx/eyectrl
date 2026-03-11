namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ZoomGazeIndicatorViewModel : ObservableObject,
    IRecipient<StartZoomMessage>,
    IRecipient<EndZoomMessage>
{
    private readonly ZoomSettings _zoomSettings;

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
    private ScreenPoint _zoomOriginalCenter = new(0, 0);
    private ScreenPoint _zoomWindowCenter = new(0, 0);
    private double _magnification = 1.0;

    public double CenterDotDiameter => _zoomSettings.GazeIndicatorCenterDotDiameter;
    public double StrokeThickness => _zoomSettings.GazeIndicatorStrokeThickness;
    public double Opacity => _zoomSettings.GazeIndicatorOpacity;

    public ZoomGazeIndicatorViewModel(ZoomSettings zoomSettings, IMessenger messenger)
    {
        _zoomSettings = zoomSettings;
        _outerCircleDiameter = zoomSettings.GazeIndicatorOuterDiameter;
        messenger.RegisterAll(this);
    }

    public void Receive(StartZoomMessage message)
    {
        _zoomOriginalCenter = message.Center;
        _magnification = message.Magnification;

        // The zoom window is centered on message.Center on screen
        _zoomWindowCenter = message.Center;

        _outerCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
        _fixationProgress = 0.0;
        IsVisible = true;
    }

    public void Receive(EndZoomMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
    }

    /// <summary>
    /// Called from App.xaml.cs with the current gaze screen point.
    /// Converts to magnified coordinates and updates position.
    /// </summary>
    public void UpdateGazePosition(ScreenPoint gazeScreenPoint)
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
        var maxDiam = _zoomSettings.GazeIndicatorOuterDiameter;
        var minDiam = _zoomSettings.GazeIndicatorCenterDotDiameter;
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
        // using the ResetAnimationMs duration. We set the target here.
        OuterCircleDiameter = _zoomSettings.GazeIndicatorOuterDiameter;
    }

    public int ResetAnimationMs => _zoomSettings.GazeIndicatorResetAnimationMs;
}
