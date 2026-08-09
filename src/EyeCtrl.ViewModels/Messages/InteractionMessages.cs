namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

// Core -> ViewModel

/// <summary>
/// Sent to show activation button.
/// Position is in physical screen pixels.
/// </summary>
public record ShowActivationButtonMessage(PhysicalPoint Position);

public record HideActivationButtonMessage;

/// <summary>
/// Sent to show action menu.
/// All position coordinates are in physical screen pixels.
/// </summary>
public record ShowActionMenuMessage(PhysicalPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, PhysicalPoint GazePoint, int ManualSelectionDisplayRadius = 0, double CircleSize = 250)
{
    /// <summary>Physical screen pixel center of the ManualSelection circle (ManualSelection entry point). Null for non-ManualSelection paths.</summary>
    public PhysicalPoint? ManualSelectionCenter { get; init; }
    /// <summary>Logical/DIP radius of the ManualSelection circle (CircleRadiusPixels). Null for non-ManualSelection paths.</summary>
    public int? ManualSelectionRadius { get; init; }
    /// <summary>Resolved control info in AutoSelection mode. Null otherwise.</summary>
    public ControlInfo? ResolvedControl { get; init; }
}

public record HideActionMenuMessage;

public record StateChangedMessage(InteractionState NewState);

/// <summary>
/// Sent to start ManualSelection mode.
/// Center and GazePoint are in physical screen pixels.
/// </summary>
public record StartManualSelectionMessage(PhysicalPoint Center, int Radius, double Magnification, PhysicalPoint GazePoint, bool Recapture = true);

/// <summary>
/// Sent when a manual selection completes normally (an action was chosen or the user closed it).
/// Overlays tear down their manual-selection visuals. Distinct from
/// <see cref="ManualSelectionHiddenMessage"/>, which is the abnormal/forced-dismissal path
/// (e.g. state machine leaves ManualSelection without user completion); both must hide
/// the overlay, hence the intentionally identical handler bodies.
/// </summary>
public record EndManualSelectionMessage;

/// <summary>
/// Sent when the manual selection overlay is force-hidden without a completed selection.
/// See <see cref="EndManualSelectionMessage"/> for the normal-completion counterpart.
/// </summary>
public record ManualSelectionHiddenMessage;

public record ManualSelectionCloseMessage;

public record ManualSelectionRecaptureMessage;

/// <summary>
/// Sent when an action is requested.
/// Point is in physical screen pixels.
/// </summary>
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsPreciseTarget = false);

// ManualSelection Dwell Service Messages
/// <summary>
/// Sent by ManualSelectionDwellService when user completes dwell within ManualSelection.
/// Contains the precise on-screen physical point where the dwell occurred while the ManualSelection overlay is shown
/// (that is, the point within the magnified overlay, not yet converted to the underlying real (unmagnified) screen coordinate).
/// </summary>
public record ManualSelectionDwellCompletedMessage(PhysicalPoint PrecisePoint);

/// <summary>
/// Sent by ManualSelectionDwellService to update the ManualSelection gaze indicator position.
/// Published at ~30Hz during ManualSelection mode.
/// GazePoint is in physical screen pixels.
/// </summary>
public record UpdateManualSelectionIndicatorPositionMessage(PhysicalPoint GazePoint);

/// <summary>
/// Sent by ManualSelectionDwellService to update dwell progress on the indicator.
/// Progress ranges from 0.0 to 1.0.
/// </summary>
public record UpdateManualSelectionIndicatorProgressMessage(double Progress);

/// <summary>
/// Sent by ManualSelectionDwellService to reset the ManualSelection indicator (abort dwell).
/// </summary>
public record ResetManualSelectionIndicatorMessage;

// ViewModel -> Core
public record ActivationCompletedMessage;
public record ActionSelectedMessage(InteractionAction Action);
public record BackSelectedMessage;
public record PauseToggledMessage;
public record SelectionModeChangedMessage(SelectionMode Mode);
public record KeyboardToggledMessage;
public record SettingsRequestedMessage;
public record KeyActivatedMessage(string Key, IReadOnlySet<ModifierKey>? Modifiers = null);
public record PredictionSelectedMessage(string Word, int DeleteCount = 0);
public record SpecialKeyMessage(string KeyName, IReadOnlySet<ModifierKey>? Modifiers = null);
public record SettingsChangedMessage;

/// <summary>Sent by the toolbar reset button and tray item to clear all struggle-adaptation back to
/// baseline — the always-reachable escape (design §8.6). Handled by <c>AdaptationResetHandler</c>.</summary>
public record ResetAdaptationMessage;

/// <summary>Sent to show the glass backdrop. HideButtonCenter is in physical screen pixels.</summary>
public record ShowBackdropMessage(PhysicalPoint HideButtonCenter);
public record HideBackdropMessage;
public record BackdropDismissMessage;

public record CalibrationRequestedMessage;
public record CalibrationCompletedMessage(bool Success, double? MeanErrorPixels);
public record CalibrationCancelledMessage;
public record CalibrationStateChangedMessage(CalibrationState State, double ProgressPercent);
public record CalibrationTargetPositionMessage(PhysicalPoint Position, int TargetIndex, int TotalTargets);
public record CalibrationClearRequestedMessage;

// Drag trail messages
/// <summary>
/// Sent by InteractionManager (via EventCoordinator) to start a drag trail.
/// GrabOrigin is in physical screen pixels.
/// </summary>
public record StartDragTrailMessage(PhysicalPoint GrabOrigin);

/// <summary>Sent by DragTrailViewModel when the drag should complete (drop at current cursor position).</summary>
public record DragDropMessage(PhysicalPoint DropPoint);

/// <summary>Sent by the edge bar's cancel button (or the Escape key) to cancel the drag (MouseUp at origin).</summary>
public record DragCancelMessage;

/// <summary>Sent by the edge bar's back button to dismiss mid-drag manual selection (back to free drag).</summary>
public record DragDismissManualSelectionMessage;

/// <summary>Sent when the user dwells on the scroll stop button (edge bar) to exit scroll mode.</summary>
public record ExitScrollModeMessage;

// Task Switcher messages
/// <summary>Sent by the toolbar Task Switcher button to toggle the overlay.</summary>
public record TaskSwitcherToggledMessage;

/// <summary>Sent by a Task Switcher tile when the user selects a window.</summary>
/// <param name="Hwnd">Handle of the window to bring to front.</param>
public record TaskSwitcherSelectionMessage(IntPtr Hwnd);
