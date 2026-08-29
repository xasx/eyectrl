namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;
using System.Windows.Media;

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

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static IconGeometry Icon(string? stroke, string? fill = null)
        => new(Parse(stroke), Parse(fill));

    private static Dictionary<ToolbarIconKey, IconGeometry> Build() => new()
    {
        [ToolbarIconKey.Pause] = Icon(null, "M8.4,5.2 L11.2,5.2 L11.2,18.8 L8.4,18.8 Z "
                                          + "M12.8,5.2 L15.6,5.2 L15.6,18.8 L12.8,18.8 Z"),
        [ToolbarIconKey.Play] = Icon(null, "M9,5.6 L18.6,12 L9,18.4 Z"),
        [ToolbarIconKey.SelectionNone] = Icon(Ring + " M7.1,7.1 L16.9,16.9"),
        [ToolbarIconKey.SelectionManual] = Icon(
            Ring + " M12,1.8 L12,5.2 M12,18.8 L12,22.2 M1.8,12 L5.2,12 M18.8,12 L22.2,12",
            "M12,10.3 A1.7,1.7 0 1 1 12,13.7 A1.7,1.7 0 1 1 12,10.3 Z"),
        [ToolbarIconKey.SelectionAuto] = Icon(
            Ring + " M8.6,9.4 L15.4,9.4 L15.4,14.6 L8.6,14.6 Z"),
        [ToolbarIconKey.Keyboard] = Icon(
            "M4.8,6 L19.2,6 A2.6,2.6 0 0 1 21.8,8.6 L21.8,15.4 A2.6,2.6 0 0 1 19.2,18 "
            + "L4.8,18 A2.6,2.6 0 0 1 2.2,15.4 L2.2,8.6 A2.6,2.6 0 0 1 4.8,6 Z "
            + "M6,10.2 L6.01,10.2 M9.5,10.2 L9.51,10.2 M13,10.2 L13.01,10.2 "
            + "M16.5,10.2 L16.51,10.2 M8,14.2 L16,14.2"),
        [ToolbarIconKey.Settings] = Icon(
            "M12,8.6 A3.4,3.4 0 1 1 12,15.4 A3.4,3.4 0 1 1 12,8.6 Z "
            + "M12,1.6 L12,5 M12,19 L12,22.4 M22.4,12 L19,12 M5,12 L1.6,12 "
            + "M19.35,4.65 L16.95,7.05 M7.05,16.95 L4.65,19.35 "
            + "M19.35,19.35 L16.95,16.95 M7.05,7.05 L4.65,4.65"),
        [ToolbarIconKey.TaskSwitcher] = Icon(
            "M4.8,4.6 L13.2,4.6 A2.2,2.2 0 0 1 15.4,6.8 L15.4,12 A2.2,2.2 0 0 1 13.2,14.2 "
            + "L4.8,14.2 A2.2,2.2 0 0 1 2.6,12 L2.6,6.8 A2.2,2.2 0 0 1 4.8,4.6 Z "
            + "M10.8,9.8 L19.2,9.8 A2.2,2.2 0 0 1 21.4,12 L21.4,17.2 A2.2,2.2 0 0 1 19.2,19.4 "
            + "L10.8,19.4 A2.2,2.2 0 0 1 8.6,17.2 L8.6,12 A2.2,2.2 0 0 1 10.8,9.8 Z"),
        [ToolbarIconKey.ResetAdaptation] = Icon(
            "M3,12 A9,9 0 1 0 12,3 A9,9 0 0 0 5.26,5.74 L3,8 M3,3 L3,8 L8,8"),
        [ToolbarIconKey.ProviderEye] = Icon(
            "M2,12 C5,7 8.4,6 12,6 C15.6,6 19,7 22,12 C19,17 15.6,18 12,18 C8.4,18 5,17 2,12 Z "
            + "M12,9.2 A2.8,2.8 0 1 1 12,14.8 A2.8,2.8 0 1 1 12,9.2 Z"),
        [ToolbarIconKey.ProviderMouse] = Icon(
            "M12,2.4 A6.4,6.4 0 0 1 18.4,8.8 L18.4,15.2 A6.4,6.4 0 0 1 5.6,15.2 "
            + "L5.6,8.8 A6.4,6.4 0 0 1 12,2.4 Z M12,6 L12,10"),
    };
}
