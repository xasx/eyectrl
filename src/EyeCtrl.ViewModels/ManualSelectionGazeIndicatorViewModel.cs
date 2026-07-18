namespace EyeCtrl.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public partial class ManualSelectionGazeIndicatorViewModel : MessengerViewModelBase,
    IRecipient<StartManualSelectionMessage>,
    IRecipient<EndManualSelectionMessage>,
    IRecipient<ManualSelectionHiddenMessage>,
    IRecipient<UpdateManualSelectionIndicatorPositionMessage>,
    IRecipient<UpdateManualSelectionIndicatorProgressMessage>,
    IRecipient<ResetManualSelectionIndicatorMessage>
{
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

    public double CenterDotDiameter { get; private set; }
    public double StrokeThickness { get; private set; }
    public double Opacity { get; private set; }
    public int ResetAnimationMs { get; private set; }

    public ManualSelectionGazeIndicatorViewModel(IMessenger messenger)
        : base(messenger)
    {
    }

    /// <summary>
    /// Configure the indicator with settings. Called once at startup.
    /// </summary>
    public void Configure(EyeCtrlSettings settings)
    {
        _outerCircleDiameterSetting = settings.ManualSelection.GazeIndicatorOuterDiameter;
        _outerCircleDiameter = _outerCircleDiameterSetting;
        CenterDotDiameter = settings.ManualSelection.GazeIndicatorCenterDotDiameter;
        StrokeThickness = settings.ManualSelection.GazeIndicatorStrokeThickness;
        Opacity = settings.ManualSelection.GazeIndicatorOpacity;
        ResetAnimationMs = settings.ManualSelection.GazeIndicatorResetAnimationMs;
    }

    public void Receive(StartManualSelectionMessage message)
    {
        OuterCircleDiameter = _outerCircleDiameterSetting;
        FixationProgress = 0.0;
        IsVisible = true;
    }

    public void Receive(EndManualSelectionMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _outerCircleDiameterSetting;
    }

    public void Receive(ManualSelectionHiddenMessage message)
    {
        IsVisible = false;
        FixationProgress = 0.0;
        OuterCircleDiameter = _outerCircleDiameterSetting;
    }

    public void Receive(UpdateManualSelectionIndicatorPositionMessage message)
    {
        UpdateGazePosition(message.GazePoint);
    }

    public void Receive(UpdateManualSelectionIndicatorProgressMessage message)
    {
        UpdateFixationProgress(message.Progress);
    }

    public void Receive(ResetManualSelectionIndicatorMessage message)
    {
        ResetFixation();
    }

    /// <summary>
    /// Positions the indicator at the raw gaze point — exactly where the user is looking inside the
    /// already-zoomed manual-selection circle, which is the screen point the dwell will select.
    /// (Previously it was offset from the ORIGINAL fixation by the zoom factor, so as the eyes moved
    /// to the centred circle the dot drifted off it, leaving no usable "where am I aiming" marker.)
    /// </summary>
    public void UpdateGazePosition(PhysicalPoint gazeScreenPoint)
    {
        IndicatorX = gazeScreenPoint.X;
        IndicatorY = gazeScreenPoint.Y;
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

    protected override void OnDispose()
    {
    }
}
