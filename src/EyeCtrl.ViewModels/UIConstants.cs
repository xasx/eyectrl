namespace EyeCtrl.ViewModels;

using EyeCtrl.Core;

/// <summary>
/// UI-related constants for button sizes, gaps, and layout calculations.
/// These constants define the visual appearance and spacing of UI elements.
/// </summary>
public static class UIConstants
{
    /// <summary>
    /// Default radius for circular buttons (activation button, action menu buttons, zoom side buttons) in logical pixels.
    /// </summary>
    public const double ButtonRadius = LayoutConstants.ActionMenuButtonRadius;

    /// <summary>
    /// Gap between the cursor circle edge and the action menu button ring in logical pixels.
    /// This defines how far action menu buttons are positioned from the cursor circle.
    /// </summary>
    public const double ActionMenuButtonOuterGap = LayoutConstants.ActionMenuButtonOuterGap;

    /// <summary>
    /// Gap between the zoom overlay edge and side buttons in logical pixels.
    /// Used for positioning close and recapture buttons next to the zoom window.
    /// </summary>
    public const double ZoomSideButtonGap = 20.0;

    /// <summary>
    /// Dwell time in milliseconds for zoom side buttons (close and recapture).
    /// User must gaze at these buttons for this duration to activate them.
    /// </summary>
    public const int ZoomSideButtonDwellMs = 600;

    /// <summary>
    /// Distance in logical pixels (DIPs) from the cursor circle edge to the center of the activation button.
    /// This determines how far the activation button appears from the cursor.
    /// </summary>
    public const int ActivationButtonOffset = 55;
}
