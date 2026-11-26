namespace EyeCtrl.Controls.Icons;

/// <summary>
/// The fullscreen Backstage menu's own glyphs. Kept out of <see cref="ToolbarIcons"/> (the main
/// toolbar's faces) and <see cref="ActionIcons"/> (the action menu's mouse vocabulary): these are
/// application-lifecycle verbs on one surface, not a toolbar button or an interaction action.
///
/// <para>
/// Authored on the same <see cref="ActionIcons.GridSize"/>-unit square at
/// <see cref="ActionIcons.GridStrokeThickness"/> as every other registry, so the card faces keep
/// one stroke weight and one grid. Calibration is the toolbar's crosshair, deliberately the same
/// glyph the toolbar Calibrate entry uses — one concept, one drawing.
/// </para>
/// </summary>
public static class BackstageIcons
{
    /// <summary>Power: a ring with a gap at the top and a stem through it — Quit.</summary>
    public static IconGeometry Quit { get; } = IconGeometry.FromPathData(
        "M12,3.2 L12,11.4 M7.4,6.8 A7,7 0 1 0 16.6,6.8");

    /// <summary>A clockwise circulation arrow — Restart.</summary>
    public static IconGeometry Restart { get; } = IconGeometry.FromPathData(
        "M21,12 A9,9 0 1 1 17.8,4.9 L21,8 M21,3 L21,8 L16,8");

    /// <summary>The toolbar's crosshair — the EyeCtrl's own calibration.</summary>
    public static IconGeometry Calibrate => ToolbarIcons.For(ToolbarIconKey.Calibrate);
}
