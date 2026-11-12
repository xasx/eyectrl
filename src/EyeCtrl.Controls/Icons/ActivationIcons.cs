namespace EyeCtrl.Controls.Icons;

/// <summary>
/// Glyphs that belong to the activation button itself rather than to the action menu or the
/// toolbar. Authored on the shared <see cref="ActionIcons.GridSize"/> square at
/// <see cref="ActionIcons.GridStrokeThickness"/>, like every other drawn icon, so the weight is
/// the app's one icon weight at any render size.
///
/// <para>
/// <b>The sign is the action menu itself: a little ring menu.</b> Six small open circles sit
/// evenly on a ring around a filled centre dot, the first at 12 o'clock — the same shape the
/// action menu opens as, so the button says what completion does rather than naming it in a word
/// (F1, owner pick B). One sign serves every selection mode.
/// </para>
/// </summary>
public static class ActivationIcons
{
    /// <summary>
    /// The little ring menu (owner pick B): six open circles of radius 2.31 grid units evenly
    /// spaced on a ring of radius 8.4 around a filled dot of radius 2.1, all centred on the
    /// 24-unit grid's (12, 12), with the first circle at 12 o'clock.
    ///
    /// <para>
    /// The proportions are the owner's board scaled onto the icon grid: on its 100-unit disc the
    /// ring's centres had radius 20, the small circles radius 5.5 and the dot radius 5 (stroke 3;
    /// the app's stroke is 1.8 grid units). On the 58 DIP label-less canvas
    /// (<c>ContainerRatio.Solo</c>) the whole sign draws ~56 DIP tall and ~51 DIP wide, its outer
    /// ink 28.1 DIP from the centre — well inside the face's r46 content clip and its 2 DIP ink
    /// clearance. The open circles keep clear gaps: neighbouring centres are 8.4 grid units
    /// apart against 2 × (2.31 + 0.9) = 6.42 units of ink.
    /// </para>
    /// </summary>
    public static IconGeometry RingMenu { get; } = IconGeometry.FromPathData(
        "M9.69,3.6 A2.31,2.31 0 1 1 14.31,3.6 A2.31,2.31 0 1 1 9.69,3.6 Z "
        + "M16.9646,7.8 A2.31,2.31 0 1 1 21.5846,7.8 A2.31,2.31 0 1 1 16.9646,7.8 Z "
        + "M16.9646,16.2 A2.31,2.31 0 1 1 21.5846,16.2 A2.31,2.31 0 1 1 16.9646,16.2 Z "
        + "M9.69,20.4 A2.31,2.31 0 1 1 14.31,20.4 A2.31,2.31 0 1 1 9.69,20.4 Z "
        + "M2.4154,16.2 A2.31,2.31 0 1 1 7.0354,16.2 A2.31,2.31 0 1 1 2.4154,16.2 Z "
        + "M2.4154,7.8 A2.31,2.31 0 1 1 7.0354,7.8 A2.31,2.31 0 1 1 2.4154,7.8 Z",
        "M9.9,12 A2.1,2.1 0 1 1 14.1,12 A2.1,2.1 0 1 1 9.9,12 Z");
}
