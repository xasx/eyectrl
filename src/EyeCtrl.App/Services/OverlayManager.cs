namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Input;
using Microsoft.Extensions.Logging;

/// <summary>
/// Manages the lifecycle of overlay windows in the application.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public class OverlayManager : IOverlayManager
{
    private readonly ILogger<OverlayManager> _logger;
    private readonly Dispatcher _dispatcher;

    public CursorCircleWindow CursorCircle { get; private set; } = null!;
    public ActivationButtonWindow ActivationButton { get; private set; } = null!;
    public ManualSelectionOverlayWindow ManualSelectionOverlay { get; private set; } = null!;
    public ManualSelectionSideButtonWindow ManualSelectionSideButtons { get; private set; } = null!;
    public ActionMenuWindow ActionMenu { get; private set; } = null!;
    public ToolbarWindow Toolbar { get; private set; } = null!;
    public KeyboardWindow Keyboard { get; private set; } = null!;
    public ManualSelectionGazeIndicatorWindow ManualSelectionIndicator { get; private set; } = null!;
    public GlassBackdropWindow GlassBackdrop { get; private set; } = null!;
    public CalibrationWindow Calibration { get; private set; } = null!;
    public TargetHighlightWindow TargetHighlight { get; private set; } = null!;

    public OverlayManager(Dispatcher dispatcher, ILogger<OverlayManager> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>
    /// Initializes all overlay windows with their ViewModels and settings.
    /// </summary>
    public void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType)
    {
        _logger.LogDebug("Initializing overlay windows");

        // Create overlay windows
        CursorCircle = new CursorCircleWindow();
        ActivationButton = new ActivationButtonWindow();
        ManualSelectionOverlay = new ManualSelectionOverlayWindow();
        ManualSelectionSideButtons = new ManualSelectionSideButtonWindow();
        ActionMenu = new ActionMenuWindow();
        Toolbar = new ToolbarWindow();
        Keyboard = new KeyboardWindow();
        ManualSelectionIndicator = new ManualSelectionGazeIndicatorWindow();
        GlassBackdrop = new GlassBackdropWindow();
        TargetHighlight = new TargetHighlightWindow();

        // Configure ManualSelection indicator
        ManualSelectionIndicator.Configure(
            settings.ManualSelection.GazeIndicatorOuterDiameter,
            settings.ManualSelection.GazeIndicatorCenterDotDiameter,
            settings.ManualSelection.GazeIndicatorStrokeThickness,
            settings.ManualSelection.GazeIndicatorOpacity);

        // Set DataContext for windows that use MVVM binding
        CursorCircle.DataContext = viewModels.CursorCircle;
        ActivationButton.DataContext = viewModels.ActivationButton;
        ManualSelectionOverlay.DataContext = viewModels.ManualSelectionOverlay;
        ActionMenu.DataContext = viewModels.ActionMenu;
        Toolbar.DataContext = viewModels.Toolbar;
        Keyboard.DataContext = viewModels.Keyboard;
        Keyboard.SetViewModel(viewModels.Keyboard);

        Calibration = new CalibrationWindow();
        Calibration.SetViewModel(viewModels.Calibration);

        // Set DataContext for TargetHighlight
        TargetHighlight.DataContext = viewModels.TargetHighlight;

        // Initialize overlays with their specific APIs
        CursorCircle.UpdateAppearance(settings.Cursor);
        Toolbar.Initialize(settings.Toolbar.Size, settings.Dwell.ToolbarButtonDwellMs);
        Toolbar.SetProviderIndicator(providerType);

        _logger.LogInformation("Overlay windows initialized successfully");
    }
}
