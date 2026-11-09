namespace EyeCtrl.Controls.Icons;

/// <summary>
/// Glyphs that belong to the activation button itself rather than to the action menu or the
/// toolbar. Authored on the shared <see cref="ActionIcons.GridSize"/> square at
/// <see cref="ActionIcons.GridStrokeThickness"/>, like every other drawn icon, so the weight is
/// the app's one icon weight at any render size.
///
/// <para>
/// The menu face deliberately reuses <c>ActionIcons.For(ActionIcon.More)</c> — three dots is the
/// set's own "more" affordance and already reads as a menu. Only the magnifier is new: no existing
/// glyph reads as "open the magnifier", and the option the owner chose is a lens plus its word.
/// </para>
/// </summary>
public static class ActivationIcons
{
    /// <summary>
    /// A magnifier: a lens circle centred at (10, 10) with a handle running down-right from the
    /// lens edge. The handle's round cap keeps the ink inside the 24-unit grid (it ends at 20.4,
    /// the cap at 21.3), so the glyph never clips at any scale.
    /// </summary>
    public static IconGeometry Magnifier { get; } = IconGeometry.FromPathData(
        "M3,10 A7,7 0 1 1 17,10 A7,7 0 1 1 3,10 Z M14.9,14.9 L20.4,20.4");
}
