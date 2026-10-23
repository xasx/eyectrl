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
    /// offer — the Euclidean gap, so the diagonal placement divides it into 1/√2 per axis. Moved here
    /// from <c>UIConstants</c> (ticket 7, re-check N9) so the Core placement function owns it; the only
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
    /// Radius of a note control disc (close, edit, move) in logical pixels (P4 D3).
    /// 28px radius = 56px diameter, centred inside its 80x80 DIP hit cell.
    /// </summary>
    public const double NoteButtonRadius = 28.0;

    /// <summary>
    /// Edge of the square note hit cell in logical pixels (P4 D3): every control disc is
    /// centred inside an 80x80 DIP cell, which is the dwell target rect bounds - forgiving
    /// hit areas around a smaller visible disc. A full empty cell between Move and Remove
    /// carries the one-slot-apart rule (P2 D8) as geometry.
    /// </summary>
    public const double NoteCellDip = 80.0;

    /// <summary>Padding from the note window edges to the control column in logical pixels.</summary>
    public const double NoteButtonPadding = 10.0;

    /// <summary>Width of the note control column in logical pixels (P4 D3): padding + cell + padding.</summary>
    public const double NoteColumnWidthDip = NoteButtonPadding + NoteCellDip + NoteButtonPadding;
    // ── the Place-here window (P3 D4) ──
    // One small window per NoteManager, shown beside an accepted settle's centroid while a note
    // is being moved. It is not click-through, so the P2 own-window conjunct can resolve it.

    /// <summary>Width of the Place-here window in logical pixels (DIP).</summary>
    public const double NotePlaceHereWidthDip = 150.0;

    /// <summary>Height of the Place-here window in logical pixels (DIP).</summary>
    public const double NotePlaceHereHeightDip = 44.0;

    /// <summary>Horizontal offset from an accepted settle's centroid to the Place-here window's
    /// left edge, so the window never covers the centroid it was born from.</summary>
    public const double NotePlaceHereOffsetDip = 40.0;

    /// <summary>The Place-here exclusion margin (P3 D4): the window inflated by its hit half-size
    /// plus this. While the gaze is inside that margin the window never moves — an accepted
    /// settle inside it is ignored.</summary>
    public const double NotePlaceHereDetourDip = 60.0;

    /// <summary>The Place-here dwell target's hit half-size in logical pixels: the same disc the
    /// note buttons use, over the window's centre.</summary>
    public const double NotePlaceHereHitRadiusDip = NoteButtonRadius;
}
