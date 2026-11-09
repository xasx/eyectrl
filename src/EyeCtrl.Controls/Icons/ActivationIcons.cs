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
    /// A magnifier: a lens circle of radius 8 centred at (10.5, 10.5) — the board's chosen lens,
/// 2 x 13 + 3.6 ≈ 29.6 DIP on the 100 DIP disc — with a handle running down-right from the lens
/// edge to 22.2, whose round cap keeps the ink inside the 24-unit grid (22.2 + 0.9). Scaled by
/// ContainerRatio.Large (40 DIP), the lens measures ~29.7 DIP across and the whole glyph ~36 DIP.
    /// </summary>
    public static IconGeometry Magnifier { get; } = IconGeometry.FromPathData(
        "M2.5,10.5 A8,8 0 1 1 18.5,10.5 A8,8 0 1 1 2.5,10.5 Z M16.15,16.15 L22.2,22.2");
}
