namespace EyeCtrl.ViewModels.Messages;

using EyeCtrl.Core.Calibration;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;

// Core -> ViewModel

/// <summary>
/// Sent to show the fixation feedback. Position is in physical screen pixels: the pin, i.e. the
/// running centroid the rule published at onset.
/// </summary>
/// <param name="FixationRadiusPixels">
/// The LIVE rule's fixation radius in physical pixels, read from the detector at onset — the
/// adaptation-effective value, not the raw setting. The halo is sized from it, so the drawn hold
/// radius always marks the radius the dwell is actually counted within.
/// </param>
public record ShowActivationButtonMessage(PhysicalPoint Position, double FixationRadiusPixels);

public record HideActivationButtonMessage;

/// <summary>
/// Sent when Core's fixation rule completes an activation: the valid-time age reached the onset age plus
/// the effective activation dwell. The activation VM consumes it (it never computes age itself), then
/// publishes the dwell-completion cue and <see cref="ActivationCompletedMessage"/>. TimestampMicroseconds
/// and CreditedAgeUs are Core's ledger values, passed through for consumers that record them.
/// </summary>
public record FixationActivatedMessage(long TimestampMicroseconds, long CreditedAgeUs);

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
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsPreciseTarget = false, IReadOnlySet<ModifierKey>? Modifiers = null);

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

/// <summary>
/// Sent by ManualSelectionDwellService when the dwell anchor seats (<see cref="Anchor"/> is the
/// held point) or is released (<see cref="Anchor"/> is null). The anchor is what the dwell commits
/// to; the gaze dot may drift from it, and drawing the two as distinct marks is what shows that
/// drift-vs-intent gap before the commit. Physical screen pixels.
/// </summary>
public record ManualSelectionAnchorChangedMessage(PhysicalPoint? Anchor);

// ViewModel -> Core
public record ActivationCompletedMessage;
public record ActionSelectedMessage(InteractionAction Action, IReadOnlySet<ModifierKey>? Modifiers = null);
public record BackSelectedMessage;

/// <summary>
/// Toggles the action names on the ring's targets. Sent by the Help button on the right-edge bar.
///
/// A toggle rather than a momentary reveal: you cannot read a label while dwelling something
/// else, so a "names while held" gesture would show them only when they are least useful. Scoped
/// to the menu session — <see cref="HideActionMenuMessage"/> clears it.
/// </summary>
public record ToggleActionMenuLabelsMessage;

/// <summary>
/// Toggles one action-menu modifier's latch (left-edge bar). The latch lives in
/// <see cref="ActionMenuViewModel"/> and dies with the menu session.
/// </summary>
public record ToggleActionMenuModifierMessage(ModifierKey Modifier);

/// <summary>
/// The user dismissed the action menu without choosing anything — the right-edge Close target.
/// Distinct from <see cref="HideActionMenuMessage"/>, which only tears down the overlay: this one
/// goes to <c>InteractionManager.OnActionMenuDismissed</c> so the state machine leaves the menu
/// too, and <see cref="HideActionMenuMessage"/> follows from there.
/// </summary>
public record ActionMenuDismissedMessage;

public record PauseToggledMessage;
public record SelectionModeChangedMessage(SelectionMode Mode);
public record KeyboardToggledMessage;

/// <summary>Show the gaze keyboard (idempotent — never hides). Sent by the action-menu ring's
/// Keyboard action; handled with the toolbar toggle's suppression semantics (spec ledger #1-#2).</summary>
public record KeyboardShowRequestedMessage;
/// <summary>Hide the gaze keyboard and release fixation suppression. Sent by the keyboard edge
/// bar's Close button (spec ledger #13) — NOT the text-field seam message.</summary>
public record KeyboardHideRequestedMessage;
/// <summary>Flip the keyboard between the Top and Bottom screen edges. Sent by the keyboard edge
/// bar's Flip button (spec ledger #15).</summary>
public record KeyboardEdgeToggledMessage;

public record SettingsRequestedMessage;
public record KeyActivatedMessage(string Key, IReadOnlySet<ModifierKey>? Modifiers = null);
public record PredictionSelectedMessage(string Word, int DeleteCount = 0);
public record SpecialKeyMessage(string KeyName, IReadOnlySet<ModifierKey>? Modifiers = null);

/// <summary>Sent by the toolbar reset button and tray item to clear all struggle-adaptation back to
/// baseline — the always-reachable escape (design §8.6). Handled by <c>AdaptationResetHandler</c>.</summary>
public record ResetAdaptationMessage;

/// <summary>Clears ONLY the Phase-2 learned offsets (settings-page action, §10 <c>ResetLearnedBaseline</c>);
/// momentary adaptation is untouched — unlike <see cref="ResetAdaptationMessage"/>, the full §8.6 escape.</summary>
public record ResetLearnedOffsetsMessage;

/// <summary>Sent to show the glass backdrop. HideButtonCenter is in physical screen pixels.</summary>
public record ShowBackdropMessage(PhysicalPoint HideButtonCenter);
public record HideBackdropMessage;
public record BackdropDismissMessage;

public record CalibrationRequestedMessage;

/// <summary>
/// Starts the measure-only bias probe: a calibration-shaped, five-target run that records raw gaze
/// offset into the self-contained sidecar and applies nothing.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="CalibrationRequestedMessage"/> rather than a flag on it, so
/// the probe can only be started by its own deliberate entry point and never by "Calibrate Now" — the
/// two do opposite things to the user's gaze mapping, and a mis-click that silently skipped the
/// transform would be indistinguishable from a calibration that did not work.
/// </remarks>
public record MeasureOnlyCalibrationRequestedMessage;
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

/// <summary>Sent by the repeat-mode stop button (edge bar) or the Escape hatch to end locked repeat mode.</summary>
public record ExitRepeatModeMessage;

/// <summary>Sent by the Modes menu row (or any future entry point) to start locked-action dwell-to-act mode with the chosen click action.</summary>
public record EnterRepeatModeMessage(InteractionAction Action);

/// <summary>Dwell progress (0..1) of the armed locked-action dwell — drives the contracting repeat cursor.</summary>
public record RepeatDwellProgressChangedMessage(double Progress);

/// <summary>
/// The action locked in dwell-to-act mode, or null when the mode is off. Sent by the repeat
/// controller on mode enter/exit; consumed by the toolbar to swap the Modes button's icon (D30).
/// </summary>
public record RepeatActionChangedMessage(InteractionAction? Action);

/// <summary>Sent by the toolbar Modes button to toggle the modes menu overlay.</summary>
public record ModesMenuToggledMessage;

// Task Switcher messages
/// <summary>Sent by the toolbar Task Switcher button to toggle the overlay.</summary>
public record TaskSwitcherToggledMessage;

/// <summary>Sent by a Task Switcher tile when the user selects a window.</summary>
/// <param name="Hwnd">Handle of the window to bring to front.</param>
public record TaskSwitcherSelectionMessage(IntPtr Hwnd);

/// <summary>
/// Published after a dwell activation's effect has run on an EyeCtrl surface (the overlay VM base
/// class, the settings window, the emoji category bar, the eye-care banner, notes, and manual
/// selection — spec 2026-09-23 §Mechanism). Consumed by the App's DwellCueService for the
/// dwell-completion sound. Post-effect and detector-event-boundary are load-bearing invariants;
/// empty payload by design (ledger D5).
/// </summary>
public record DwellActivationMessage;
