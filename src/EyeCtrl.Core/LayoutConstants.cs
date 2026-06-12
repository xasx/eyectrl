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

    /// <summary>
    /// Radius of note control buttons (close, edit, move) in logical pixels.
    /// 30px radius = 60px diameter — large enough for gaze targeting, compact enough for the note sidebar.
    /// </summary>
    public const double NoteButtonRadius = 30.0;

    /// <summary>
    /// Width of the NoteWindow in logical pixels (text area + button column: 200 + 60 + 10 + 10).
    /// </summary>
    public const double NoteWindowWidth = 280.0;
}
