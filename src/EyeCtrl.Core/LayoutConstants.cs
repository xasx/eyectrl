namespace EyeCtrl.Core;

/// <summary>
/// UI layout constants that affect coordinate calculations.
/// These constants define physical dimensions that impact positioning logic in the Core layer.
/// </summary>
public static class LayoutConstants
{
    /// <summary>
    /// Radius of the circular dwell buttons shared by every overlay that is NOT the action menu,
    /// in logical pixels: the activation button, the backdrop hide button, the ManualSelection
    /// side buttons, the scroll-stop button, the quick-action grid and the task switcher.
    ///
    /// Split from <see cref="ActionMenuButtonRadius"/> on 2026-08-26. They held the same value
    /// through one constant until the action-menu targets grew to 140 DIP; sharing meant that
    /// change would have resized six unrelated surfaces and their dwell hit-targets. Keep them
    /// separate even while the numbers agree — see
    /// <c>UIConstantsTests.ActionMenuButtonRadius_IsSeparateFrom_TheSharedButtonRadius</c>.
    /// </summary>
    public const double SharedButtonRadius = 50.0;

    /// <summary>
    /// Distance in logical pixels (DIP) from the cursor circle edge to the centre of the activation
    /// offer, along each axis of the diagonal (the placement divides it by √2). Moved here from
    /// <c>UIConstants</c> (ticket 7, re-check N9) so the Core placement function owns it; the only
    /// production reader was a test, and <c>UIConstants</c> keeps an <c>int</c> alias.
    ///
    /// 55 DIP, as shipped: at the user's ring size 250 it puts the offer 180 DIP — 360 px at ScaleX 2.0 —
    /// from the settle point.
    /// </summary>
    public const double ActivationButtonOffset = 55.0;

    /// <summary>
    /// Radius of the activation offer's dwell target in PHYSICAL pixels, never DPI-converted
    /// (re-check N4). 150 px is the user's choice after the measured corner bias (75–130 px): the old
    /// 100 px target (a 50 DIP <see cref="SharedButtonRadius"/>) let a biased near miss fall outside it,
    /// and the offer would then move away from the attempt.
    /// <para>
    /// This is deliberately its own constant: <see cref="SharedButtonRadius"/> sizes about ten other
    /// buttons and must not move with this one. Do not derive it through a logical-to-physical
    /// conversion — bias and R live in physical pixels.
    /// </para>
    /// </summary>
    public const double ActivationButtonHitRadiusPx = 150.0;

    /// <summary>
    /// Radius of action menu buttons in logical pixels.
    /// Used for coordinate clamping calculations to ensure buttons stay on screen.
    /// Deliberately NOT <see cref="SharedButtonRadius"/> — see that constant for why.
    ///
    /// 70 since 2026-08-26 (was 50). A 140 DIP target carries a drawn icon at
    /// <c>ContainerRatio.Dominant</c> without the Unicode glyph's font-fallback lottery, and gives
    /// the ring room the old 100 DIP target did not have.
    /// </summary>
    public const double ActionMenuButtonRadius = 70.0;

    /// <summary>
    /// Gap between the cursor circle edge and the action menu button ring in logical pixels.
    /// Used in conjunction with circle size to calculate the effective ring radius for positioning.
    ///
    /// 60 since 2026-08-26 (was 20). At 20 the ring sat roughly a third of a degree of visual
    /// angle from the preview circle, so gaze resting on the thing about to be acted upon was one
    /// jitter away from arming a target. 60 DIP is roughly a degree at desk distance. This is
    /// reasoning, not measurement — it is the number to revisit first if mis-selection persists.
    /// </summary>
    public const double ActionMenuButtonOuterGap = 60.0;

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
