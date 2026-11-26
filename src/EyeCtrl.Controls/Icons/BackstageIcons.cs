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

    /// <summary>The toolbar's crosshair - the EyeCtrl's own calibration.</summary>
    public static IconGeometry Calibrate => ToolbarIcons.For(ToolbarIconKey.Calibrate);

    /// <summary>A down arrow into a tray - install an update.</summary>
    public static IconGeometry InstallUpdate { get; } = IconGeometry.FromPathData(
        "M12,4 L12,15 M8,11 L12,15 L16,11 M5,19 L19,19");

    /// <summary>A counter-clockwise circulation arrow - restore the previous version.</summary>
    public static IconGeometry Rollback { get; } = IconGeometry.FromPathData(
        "M3,12 A9,9 0 1 0 6.2,5.1 L3,8 M3,3 L3,8 L8,8");

    /// <summary>A lidded bin - uninstall.</summary>
    public static IconGeometry Uninstall { get; } = IconGeometry.FromPathData(
        "M4,7 L20,7 M9,7 L9,4 L15,4 L15,7 M7,7 L8,20 L16,20 L17,7");
}
