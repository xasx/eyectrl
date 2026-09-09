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
    /// Gets the action menu overlay window.
    /// </summary>
    ActionMenuWindow ActionMenu { get; }

    /// <summary>
    /// Gets the toolbar overlay window.
    /// </summary>
    ToolbarWindow Toolbar { get; }

    /// <summary>Thin edge-bar pause indicator; shown when toolbar is hidden + paused.</summary>
    PauseIndicatorWindow PauseIndicator { get; }

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

    /// <summary>Gets the context-driven right-edge button bar window.</summary>
    /// <summary>
    /// The screen-edge button bar, as a seam rather than the window.
    /// </summary>
    /// <remarks>
    /// Narrowed from the concrete <c>EdgeButtonBarWindow</c> to <see cref="IEdgeButtonBar"/>. The
    /// concrete type forced every test that touched a coordinator reaching this property to stand
    /// up a real WPF window — an STA thread, an <c>Application</c> singleton and the app resource
    /// dictionary — which is why <c>DragCoordinator</c>'s success path had no headless coverage and
    /// a mouse button left pressed on exit went unnoticed. Every other member of this interface is
    /// still the concrete window; this one was the one that cost coverage.
    /// </remarks>
    IEdgeButtonBar EdgeButtonBar { get; }

    /// <summary>Gets the track status overlay window (eye position, distance, presence).</summary>
    TrackStatusWindow TrackStatus { get; }

    /// <summary>Gets the fullscreen Task Switcher overlay window.</summary>
    TaskSwitcherWindow TaskSwitcher { get; }

    /// <summary>Gets the quick-actions grid overlay window.</summary>
    QuickActionsGridWindow QuickActionsGrid { get; }

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
    TargetHighlightOverlayViewModel TargetHighlight,
    EdgeButtonBarViewModel EdgeButtonBar,
    TrackStatusViewModel TrackStatus,
    TaskSwitcherViewModel TaskSwitcher,
    QuickActionsGridViewModel QuickActionsGrid);
