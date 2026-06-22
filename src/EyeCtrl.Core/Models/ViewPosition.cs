namespace EyeCtrl.Core.Models;

/// <summary>Positioning strategy for overlays relative to the gaze point or screen center.</summary>
public enum ViewPosition
{
    /// <summary>Place the overlay at the user's current gaze point.</summary>
    AtGaze,
    /// <summary>Place the overlay at the screen center.</summary>
    AtCenter
}
