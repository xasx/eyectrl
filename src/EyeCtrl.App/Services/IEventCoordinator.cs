namespace EyeCtrl.App.Services;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Coordinates event handlers between ViewModels, overlay windows, and the core engine.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public interface IEventCoordinator
{
    /// <summary>
    /// Gets the word predictor instance used for keyboard predictions, if enabled.
    /// </summary>
    WordPredictor? WordPredictor { get; }

    /// <summary>
    /// Initializes event handlers and message subscriptions.
    /// </summary>
    /// <param name="context">The event coordination context containing all dependencies</param>
    void Initialize(EventCoordinationContext context);

    /// <summary>
    /// Loads keyboard layout and configuration.
    /// </summary>
    /// <param name="keyboard">The keyboard window to configure</param>
    /// <param name="settings">The application settings</param>
    /// <param name="inputSimulator">The input simulator</param>
    /// <param name="settingsService">The settings service</param>
    void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService);

    /// <summary>
    /// Cleans up all event subscriptions and handlers.
    /// </summary>
    void Dispose();
}

/// <summary>
/// Context object containing all dependencies needed for event coordination.
/// </summary>
public record EventCoordinationContext(
    // ViewModels
    CursorCircleViewModel CursorVM,
    ActivationButtonViewModel ActivationVM,
    ManualSelectionOverlayViewModel ManualSelectionVM,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicatorVM,
    ActionMenuViewModel ActionMenuVM,
    ToolbarViewModel ToolbarVM,
    KeyboardViewModel KeyboardVM,
    GlassBackdropViewModel BackdropVM,

    // Overlay Windows
    IOverlayManager OverlayManager,

    // Core
    GazeEngine GazeEngine,
    InteractionManager InteractionManager,

    // Services
    IMessenger Messenger,
    Dispatcher Dispatcher,
    SettingsService SettingsService,
    EyeCtrlSettings Settings,

    // Dependencies
    IInputSimulator InputSimulator,
    CalibrationService CalibrationService);
