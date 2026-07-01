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
public record StartManualSelectionMessage(PhysicalPoint Center, int Radius, double Magnification, PhysicalPoint GazePoint);

public record EndManualSelectionMessage;

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
/// (that is, the point within the magnified overlay, not yet converted to the underlying real/unManualSelectioned screen coordinate).
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
/// Sent by InteractionManager (via EventCoordinator) to start a drag trail. All points are in
/// physical screen pixels. The right-edge cancel/back button centers are computed by the App layer
/// (which owns screen geometry) and passed in so the ViewModel stays framework-agnostic.
/// </summary>
public record StartDragTrailMessage(
    PhysicalPoint GrabOrigin,
    PhysicalPoint CancelButtonCenter,
    PhysicalPoint BackButtonCenter);

/// <summary>Sent by DragTrailViewModel when the drag should complete (drop at current cursor position).</summary>
public record DragDropMessage(PhysicalPoint DropPoint);

/// <summary>Sent by DragTrailViewModel when the drag should be cancelled (MouseUp at origin).</summary>
public record DragCancelMessage;

/// <summary>Sent by DragTrailViewModel when zoom should be dismissed (back to free drag).</summary>
public record DragDismissZoomMessage;
