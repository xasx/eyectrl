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

    /// <summary>Height of the NoteWindow title bar in logical pixels.</summary>
    public const double NoteTitleBarHeight = 24.0;

    /// <summary>Gap between the stacked note control buttons in logical pixels.</summary>
    public const double NoteButtonGap = 10.0;

    /// <summary>Padding from the note window edges to the button column in logical pixels.</summary>
    public const double NoteButtonPadding = 10.0;

    /// <summary>
    /// Vertical distance (logical px) from the note window's top edge down to the gaze anchor point.
    /// The fixation point maps to this offset below the window top so the note appears just under the gaze.
    /// Shared by <c>NoteManager</c> (window positioning + dwell-target math) and must stay in sync.
    /// </summary>
    public const double NoteAnchorOffsetY = 70.0;

    /// <summary>
    /// Height of the NoteWindow in logical pixels. Sized to fit the title bar plus the vertical
    /// column of three control buttons (close/edit/move) with top and bottom padding, so no button
    /// is clipped: title + padding + 3·diameter + 2·gap + padding.
    /// </summary>
    public const double NoteWindowHeight =
        NoteTitleBarHeight + NoteButtonPadding
        + 6 * NoteButtonRadius
        + 2 * NoteButtonGap
        + NoteButtonPadding;

    /// <summary>Radius (logical px) of the placement dwell target shown while a note is being moved.</summary>
    public const double NotePlacementRadius = 100.0;
}
