namespace EyeCtrl.Core;

/// <summary>
/// UI layout constants that affect coordinate calculations.
/// These constants define physical dimensions that impact positioning logic in the Core layer.
/// </summary>
public static class LayoutConstants
{
    /// <summary>
    /// Radius of action menu buttons in logical pixels.
    /// Used for coordinate clamping calculations to ensure buttons stay on screen.
    /// </summary>
    public const double ActionMenuButtonRadius = 50.0;

    /// <summary>
    /// Gap between the cursor circle edge and the action menu button ring in logical pixels.
    /// Used in conjunction with circle size to calculate the effective ring radius for positioning.
    /// </summary>
    public const double ActionMenuButtonOuterGap = 20.0;
}
