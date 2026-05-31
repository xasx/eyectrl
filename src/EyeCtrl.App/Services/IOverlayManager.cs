namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Manages the lifecycle of overlay windows in the application.
/// Extracted from App.xaml.cs to improve Single Responsibility Principle compliance.
/// </summary>
public interface IOverlayManager
{
    /// <summary>
    /// Gets the cursor circle overlay window.
    /// </summary>
    CursorCircleWindow CursorCircle { get; }

    /// <summary>
    /// Gets the activation button overlay window.
    /// </summary>
    ActivationButtonWindow ActivationButton { get; }

    /// <summary>
    /// Gets the ManualSelection overlay window.
    /// </summary>
    ManualSelectionOverlayWindow ManualSelectionOverlay { get; }

    /// <summary>
    /// Gets the ManualSelection side buttons overlay window.
    /// </summary>
    ManualSelectionSideButtonWindow ManualSelectionSideButtons { get; }

    /// <summary>
    /// Gets the action menu overlay window.
    /// </summary>
    ActionMenuWindow ActionMenu { get; }

    /// <summary>
    /// Gets the toolbar overlay window.
    /// </summary>
    ToolbarWindow Toolbar { get; }

    /// <summary>
    /// Gets the keyboard overlay window.
    /// </summary>
    KeyboardWindow Keyboard { get; }

    /// <summary>
    /// Gets the ManualSelection gaze indicator overlay window.
    /// </summary>
    ManualSelectionGazeIndicatorWindow ManualSelectionIndicator { get; }

    /// <summary>
    /// Gets the fullscreen glass backdrop overlay window.
    /// </summary>
    GlassBackdropWindow GlassBackdrop { get; }

    /// <summary>
    /// Gets the calibration overlay window.
    /// </summary>
    CalibrationWindow Calibration { get; }

    /// <summary>
    /// Gets the target highlight overlay window.
    /// </summary>
    TargetHighlightWindow TargetHighlight { get; }

    /// <summary>
    /// Initializes all overlay windows with their ViewModels and settings.
    /// </summary>
    /// <param name="viewModels">Container with all ViewModels</param>
    /// <param name="settings">Application settings</param>
    /// <param name="providerType">Active gaze provider type for toolbar indicator</param>
    void Initialize(OverlayViewModels viewModels, EyeCtrlSettings settings, GazeProviderType providerType);
}

/// <summary>
/// Container for all overlay ViewModels to simplify parameter passing.
/// </summary>
public record OverlayViewModels(
    CursorCircleViewModel CursorCircle,
    ActivationButtonViewModel ActivationButton,
    ManualSelectionOverlayViewModel ManualSelectionOverlay,
    ActionMenuViewModel ActionMenu,
    ToolbarViewModel Toolbar,
    KeyboardViewModel Keyboard,
    ManualSelectionGazeIndicatorViewModel ManualSelectionIndicator,
    CalibrationViewModel Calibration,
    TargetHighlightOverlayViewModel TargetHighlight);
