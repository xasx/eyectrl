namespace EyeCtrl.Core.Models;

/// <summary>Visual style of the gaze position indicator (cursor visualization).</summary>
public enum PositionIndicatorStyle
{
    /// <summary>Crosshair indicator — vertical and horizontal lines intersecting at the gaze point.</summary>
    Crosshair,
    /// <summary>Pin indicator — a circular dot or teardrop shape at the gaze point.</summary>
    Pin
}
