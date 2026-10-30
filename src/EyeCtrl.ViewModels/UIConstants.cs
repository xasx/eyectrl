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

    // ActionMenuButtonOuterGap was deleted 2026-09-30 (T4): the ring radius formula moved into
    // RingPlan.EffectiveRingRadiusFor, which reads LayoutConstants directly, and this alias had no
    // production caller left. The LayoutConstants member itself stays — the plan uses it.

    /// <summary>
    /// Gap between the ManualSelection overlay edge and side buttons in logical pixels.
    /// Used for positioning close and recapture buttons next to the ManualSelection window.
    /// </summary>
    public const double ManualSelectionSideButtonGap = 20.0;

    /// <summary>
    /// Distance in logical pixels (DIPs) from the cursor circle edge to the center of the activation
    /// button. An <c>int</c> alias of <see cref="LayoutConstants.ActivationButtonOffset"/>, kept for its
    /// existing readers (the constants tests); Core's placement function owns the value now.
    /// </summary>
    public const int ActivationButtonOffset = (int)LayoutConstants.ActivationButtonOffset;

    /// <summary>
    /// Distance from screen edges to the backdrop hide button center in logical pixels. The user
    /// chose 53 on 2026-10-13 off the 40/50/53 corner sheet: at 53 the button's 100-DIP dwell disc
    /// and its 105-DIP halo are fully on screen (at 40 the disc reached 10 DIP past the right and
    /// top edges, and the clipped corner was visible in the T13 live check).
    /// </summary>
    public const double BackdropHideButtonOffset = 53.0;

    /// <summary>
    /// The edge bar's default button radius (<c>EdgeButtonDefinition</c>). Despite the name it is
    /// NOT the drag's drop target any more: that is <see cref="DragDropTargetMinRadius"/> (C2).
    /// </summary>
    public const double DragButtonRadius = 20.0;

    /// <summary>
    /// The floor of the drag's gaze-following drop target radius in DIP: an r50 disc like every
    /// other gaze button (C2). The hit radius is the larger of this, converted to physical px, and
    /// the user's fixation radius (<c>DwellSettings.FixationRadiusPixels</c>), capped at
    /// <see cref="DragDropTargetMaxRadius"/>. The old target was r20 (<see cref="DragButtonRadius"/>,
    /// which the edge bar still shares and which therefore must not change) placed at cursor −
    /// (14, 14) DIP, so the cursor sat 0.2 DIP inside its down-right edge.
    /// </summary>
    public const double DragDropTargetMinRadius = ButtonRadius;

    /// <summary>
    /// The cap of the drop target radius in DIP (C2). It binds only for a large fixation-radius
    /// setting at a low scale (the setting reaches 200 px; at 100 % that would be a 400 DIP disc
    /// over the very spot being dropped on). It cannot keep the disc itself clear of Cancel/Back —
    /// the disc follows the gaze — and does not need to: the exit-zone filter withholds every
    /// sample inside Cancel, Back or Pause from the drop dwell whatever the radius.
    /// </summary>
    public const double DragDropTargetMaxRadius = 100.0;

    /// <summary>
    /// Distance in logical pixels from the shared right-edge anchor to the fixed exit ("out")
    /// slot's centre: <see cref="ButtonRadius"/> plus half the side-button gap, so two r50 buttons
    /// in the two slots are exactly the gap apart. 60 DIP at the shipped sizes. Positive Y is
    /// down, so the UPPER (exit) slot is the negative offset and a secondary button takes the
    /// mirrored positive one.
    /// </summary>
    public const double EdgeButtonSlotOffset = ButtonRadius + ManualSelectionSideButtonGap / 2;
}
