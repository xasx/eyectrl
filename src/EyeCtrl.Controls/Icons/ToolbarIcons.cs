namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;

/// <summary>
/// The main toolbar's drawn icon set: <see cref="ToolbarIconKey"/> to <see cref="IconGeometry"/>.
///
/// <para>
/// Authored on the same <see cref="ActionIcons.GridSize"/> square at the same
/// <see cref="ActionIcons.GridStrokeThickness"/> as the action menu, and scaled by the caller's
/// RenderTransform — which is what <see cref="EyeCtrl.Controls.DwellArcButton"/> already does for
/// any <see cref="IconGeometry"/> it is handed. One grid and one weight across the app is the
/// point; do not introduce a per-icon thickness, and note DwellArcButton would ignore it.
/// </para>
///
/// <para>
/// Geometries are parsed once, frozen and shared. Frozen is not an optimisation: these are
/// statics handed to every button on screen, and an unfrozen Geometry is mutable and
/// thread-affine.
/// </para>
/// </summary>
public static class ToolbarIcons
{
    /// <summary>A full circle of radius 7 centred on the grid, as two half arcs.</summary>
    private const string Ring = "M5,12 A7,7 0 1 1 19,12 A7,7 0 1 1 5,12 Z";

    private static readonly Dictionary<ToolbarIconKey, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="key"/>. Throws if the registry has no entry.</summary>
    /// <param name="key">Which icon to resolve.</param>
    /// <returns>That icon's two layers.</returns>
    public static IconGeometry For(ToolbarIconKey key) => Map[key];

    private static Dictionary<ToolbarIconKey, IconGeometry> Build() => new()
    {
        [ToolbarIconKey.Pause] = IconGeometry.FromPathData(null,
            "M8.4,5.2 L11.2,5.2 L11.2,18.8 L8.4,18.8 Z "
            + "M12.8,5.2 L15.6,5.2 L15.6,18.8 L12.8,18.8 Z"),
        [ToolbarIconKey.Play] = IconGeometry.FromPathData(null, "M9,5.6 L18.6,12 L9,18.4 Z"),
        [ToolbarIconKey.SelectionNone] = IconGeometry.FromPathData(Ring + " M7.1,7.1 L16.9,16.9"),
        [ToolbarIconKey.SelectionManual] = IconGeometry.FromPathData(
            Ring + " M12,1.8 L12,5.2 M12,18.8 L12,22.2 M1.8,12 L5.2,12 M18.8,12 L22.2,12",
            "M12,10.3 A1.7,1.7 0 1 1 12,13.7 A1.7,1.7 0 1 1 12,10.3 Z"),
        [ToolbarIconKey.SelectionAuto] = IconGeometry.FromPathData(
            Ring + " M8.6,9.4 L15.4,9.4 L15.4,14.6 L8.6,14.6 Z"),
        // The four key dots are zero-length segments ("M x,y L x,y"): they render as visible dots
        // only because DwellArcButton draws icon strokes with PenLineCap.Round
        // (DwellArcButton.cs:297-298). A flat cap would render these invisible with no error;
        // DwellArcButtonIconTests.TheIcon_UsesRoundLineCaps pins the cap so this can't drift silently.
        [ToolbarIconKey.Keyboard] = IconGeometry.FromPathData(
            "M4.8,6 L19.2,6 A2.6,2.6 0 0 1 21.8,8.6 L21.8,15.4 A2.6,2.6 0 0 1 19.2,18 "
            + "L4.8,18 A2.6,2.6 0 0 1 2.2,15.4 L2.2,8.6 A2.6,2.6 0 0 1 4.8,6 Z "
            + "M6,10.2 L6.01,10.2 M9.5,10.2 L9.51,10.2 M13,10.2 L13.01,10.2 "
            + "M16.5,10.2 L16.51,10.2 M8,14.2 L16,14.2"),
        // An 8-tooth gear: toothed outline (outer r=10.0, root r=7.6) plus an inner hole r=3.6,
        // centred on (12,12). The earlier disc-plus-detached-dashes version rendered as a sun/
        // brightness mark rather than a gear at toolbar size; this shape was rendered headlessly
        // and compared against alternatives before being chosen.
        [ToolbarIconKey.Settings] = IconGeometry.FromPathData(
            "M21.74,9.75 L21.74,14.25 L19.41,13.71 L18.45,16.03 L20.48,17.3 L17.3,20.48 "
            + "L16.03,18.45 L13.71,19.41 L14.25,21.74 L9.75,21.74 L10.29,19.41 L7.97,18.45 "
            + "L6.7,20.48 L3.52,17.3 L5.55,16.03 L4.59,13.71 L2.26,14.25 L2.26,9.75 "
            + "L4.59,10.29 L5.55,7.97 L3.52,6.7 L6.7,3.52 L7.97,5.55 L10.29,4.59 "
            + "L9.75,2.26 L14.25,2.26 L13.71,4.59 L16.03,5.55 L17.3,3.52 L20.48,6.7 "
            + "L18.45,7.97 L19.41,10.29 Z M12,8.4 A3.6,3.6 0 1 1 12,15.6 A3.6,3.6 0 1 1 12,8.4 Z"),
        [ToolbarIconKey.TaskSwitcher] = IconGeometry.FromPathData(
            "M4.8,4.6 L13.2,4.6 A2.2,2.2 0 0 1 15.4,6.8 L15.4,12 A2.2,2.2 0 0 1 13.2,14.2 "
            + "L4.8,14.2 A2.2,2.2 0 0 1 2.6,12 L2.6,6.8 A2.2,2.2 0 0 1 4.8,4.6 Z "
            + "M10.8,9.8 L19.2,9.8 A2.2,2.2 0 0 1 21.4,12 L21.4,17.2 A2.2,2.2 0 0 1 19.2,19.4 "
            + "L10.8,19.4 A2.2,2.2 0 0 1 8.6,17.2 L8.6,12 A2.2,2.2 0 0 1 10.8,9.8 Z"),
        // A mode stack: the filled top bar is the active mode, the two open bars below it the
        // alternatives — reads as "choose how input behaves", distinct from the gear (settings).
        [ToolbarIconKey.Modes] = IconGeometry.FromPathData(
            "M4.6,12 L19.4,12 M4.6,19.4 L19.4,19.4",
            "M4.6,3.4 L19.4,3.4 L19.4,6.2 L4.6,6.2 Z"),
        // Locked-action mode states on the Modes button (D30): the same mouse glyphs the
        // action ring uses, so the toolbar button shows WHAT is locked, not just that.
        [ToolbarIconKey.ModeLeftClick] = Icons.ActionIcons.For(Core.Models.ActionIcon.LeftClick),
        [ToolbarIconKey.ModeRightClick] = Icons.ActionIcons.For(Core.Models.ActionIcon.RightClick),
        [ToolbarIconKey.ModeDoubleClick] = Icons.ActionIcons.For(Core.Models.ActionIcon.DoubleClick),
        [ToolbarIconKey.ModeMiddleClick] = Icons.ActionIcons.For(Core.Models.ActionIcon.MiddleClick),
        [ToolbarIconKey.ResetAdaptation] = IconGeometry.FromPathData(
            "M3,12 A9,9 0 1 0 12,3 A9,9 0 0 0 5.26,5.74 L3,8 M3,3 L3,8 L8,8"),
        [ToolbarIconKey.ProviderEye] = IconGeometry.FromPathData(
            "M2,12 C5,7 8.4,6 12,6 C15.6,6 19,7 22,12 C19,17 15.6,18 12,18 C8.4,18 5,17 2,12 Z "
            + "M12,9.2 A2.8,2.8 0 1 1 12,14.8 A2.8,2.8 0 1 1 12,9.2 Z"),
        // Draws the same mouse ActionIcons builds its own icons from, rather than a second one at
        // slightly different proportions: this is the fallback provider, and the app should have
        // exactly one mouse silhouette.
        [ToolbarIconKey.ProviderMouse] = IconGeometry.FromPathData(ActionIcons.Body + ActionIcons.Wheel),
    };
}
