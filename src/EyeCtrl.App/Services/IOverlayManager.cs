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
    ICursorCircleOverlay CursorCircle { get; }

    /// <summary>
    /// Gets the activation button overlay window.
    /// </summary>
    IActivationButtonOverlay ActivationButton { get; }

    /// <summary>
    /// Gets the ManualSelection overlay window.
    /// </summary>
    IManualSelectionOverlay ManualSelectionOverlay { get; }

    /// <summary>
    /// Gets the action menu overlay window.
    /// </summary>
    IActionMenuOverlay ActionMenu { get; }

    /// <summary>
    /// Gets the toolbar overlay window.
    /// </summary>
    IToolbarOverlay Toolbar { get; }

    /// <summary>Thin edge-bar pause indicator; shown when toolbar is hidden + paused.</summary>
    IPauseIndicatorOverlay PauseIndicator { get; }

    /// <summary>
    /// Gets the keyboard overlay window.
    /// </summary>
    // Deliberately NOT narrowed. Its consumers use it as a window: LoadKeyboard drives seven
    // setup members (LoadLayout, SetEmojiData, SetOrchestrator, SetScreenEdge, SetSettings,
    // SetSkinTone, SetSoundService), so an interface would carry twelve members of pure
    // configuration that no test asserts on. That is a facade over the window, not a seam — it
    // would add indirection without removing the WPF dependency it exists to remove.
    KeyboardWindow Keyboard { get; }

    /// <summary>
    /// Gets the ManualSelection gaze indicator overlay window.
    /// </summary>
    IManualSelectionIndicatorOverlay ManualSelectionIndicator { get; }

    /// <summary>
    /// Gets the fullscreen glass backdrop overlay window.
    /// </summary>
    IGlassBackdropOverlay GlassBackdrop { get; }

    /// <summary>
    /// Gets the calibration overlay window.
    /// </summary>
    ICalibrationOverlay Calibration { get; }

    /// <summary>
    /// Gets the target highlight overlay window.
    /// </summary>
    ITargetHighlightOverlay TargetHighlight { get; }

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
    ITrackStatusOverlay TrackStatus { get; }

    /// <summary>Gets the fullscreen Task Switcher overlay window.</summary>
    ITaskSwitcherOverlay TaskSwitcher { get; }

    /// <summary>Gets the modes menu panel (toolbar-triggered input-mode switches).</summary>
    IModesMenuOverlay ModesMenu { get; }

    /// <summary>Gets the quick-actions grid overlay window.</summary>
    IQuickActionsGridOverlay QuickActionsGrid { get; }

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
    QuickActionsGridViewModel QuickActionsGrid,
    ModesMenuViewModel ModesMenu);
