namespace EyeCtrl.ViewModels.Messages;

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
public record ShowActionMenuMessage(PhysicalPoint Center, IReadOnlyList<InteractionAction> Actions, bool ShowBack, int DwellTimeMs, PhysicalPoint GazePoint, int ZoomDisplayRadius = 0, double CircleSize = 250)
{
    /// <summary>Physical screen pixel center of the zoom circle (zoom entry point). Null for non-zoom paths.</summary>
    public PhysicalPoint? ZoomCenter { get; init; }
    /// <summary>Logical/DIP radius of the zoom circle (CircleRadiusPixels). Null for non-zoom paths.</summary>
    public int? ZoomRadius { get; init; }
}

public record HideActionMenuMessage;

public record StateChangedMessage(InteractionState NewState);

/// <summary>
/// Sent to start zoom mode.
/// Center and GazePoint are in physical screen pixels.
/// </summary>
public record StartZoomMessage(PhysicalPoint Center, int Radius, double Magnification, PhysicalPoint GazePoint);

public record EndZoomMessage;

public record ZoomHiddenMessage;

public record ZoomCloseMessage;

public record ZoomRecaptureMessage;

/// <summary>
/// Sent when an action is requested.
/// Point is in physical screen pixels.
/// </summary>
public record ActionRequestedMessage(InteractionAction Action, PhysicalPoint Point, bool IsFromZoom = false);

// Zoom Dwell Service Messages
/// <summary>
/// Sent by ZoomDwellService when user completes dwell within zoom.
/// Contains the precise on-screen physical screen point where the dwell occurred while the zoom overlay is shown
/// (that is, the point within the magnified overlay, not yet converted to the underlying real/unzoomed screen coordinate).
/// </summary>
public record ZoomFixationCompletedMessage(PhysicalPoint PrecisePoint);

/// <summary>
/// Sent by ZoomDwellService to update the zoom gaze indicator position.
/// Published at ~30Hz during zoom mode.
/// GazePoint is in physical screen pixels.
/// </summary>
public record UpdateZoomIndicatorPositionMessage(PhysicalPoint GazePoint);

/// <summary>
/// Sent by ZoomDwellService to update dwell progress on the indicator.
/// Progress ranges from 0.0 to 1.0.
/// </summary>
public record UpdateZoomIndicatorProgressMessage(double Progress);

/// <summary>
/// Sent by ZoomDwellService to reset the zoom indicator (abort dwell).
/// </summary>
public record ResetZoomIndicatorMessage;

// ViewModel -> Core
public record ActivationCompletedMessage;
public record ActionSelectedMessage(InteractionAction Action);
public record BackSelectedMessage;
public record PauseToggledMessage;
public record ZoomToggledMessage;
public record KeyboardToggledMessage;
public record SettingsRequestedMessage;
public record KeyActivatedMessage(string Key, IReadOnlySet<ModifierKey>? Modifiers = null);
public record PredictionSelectedMessage(string Word, int DeleteCount = 0);
public record SettingsChangedMessage;
