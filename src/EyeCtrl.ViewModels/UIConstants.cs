namespace EyeCtrl.ViewModels;

using EyeCtrl.Core;

/// <summary>
/// UI-related constants for button sizes, gaps, and layout calculations.
/// These constants define the visual appearance and spacing of UI elements.
/// </summary>
public static class UIConstants
{
    /// <summary>
    /// Default radius for circular buttons in logical pixels: activation button, backdrop hide
    /// button, ManualSelection side buttons, scroll-stop button, quick-action grid, task switcher.
    ///
    /// NOT the action menu — that reads <see cref="ActionMenuButtonRadius"/>. This property named
    /// the action menu among its callers until 2026-08-26, when the two were split.
    /// </summary>
    public const double ButtonRadius = LayoutConstants.SharedButtonRadius;

    /// <summary>
    /// Radius of an action-menu ring button in logical pixels. Feeds both the rendered
    /// <c>DwellArcButton</c> and the dwell hit-target, and appears twice in the ring formula:
    /// once in the ring radius and once again in the on-screen clamp margin.
    /// </summary>
    public const double ActionMenuButtonRadius = LayoutConstants.ActionMenuButtonRadius;

    /// <summary>
    /// Gap between the cursor circle edge and the action menu button ring in logical pixels.
    /// This defines how far action menu buttons are positioned from the cursor circle.
    /// </summary>
    public const double ActionMenuButtonOuterGap = LayoutConstants.ActionMenuButtonOuterGap;

    /// <summary>
    /// Gap between the ManualSelection overlay edge and side buttons in logical pixels.
    /// Used for positioning close and recapture buttons next to the ManualSelection window.
    /// </summary>
    public const double ManualSelectionSideButtonGap = 20.0;

    /// <summary>
    /// Distance in logical pixels (DIPs) from the cursor circle edge to the center of the activation button.
    /// This determines how far the activation button appears from the cursor.
    /// </summary>
    public const int ActivationButtonOffset = 55;

    /// <summary>Distance from screen edges to the backdrop hide button center in logical pixels.</summary>
    public const double BackdropHideButtonOffset = 40.0;

    public const double DragButtonRadius = 20.0;
    public const double DragReleaseButtonOffset = 14.0;
}
