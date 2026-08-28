namespace EyeCtrl.Controls.Icons;

using System.Collections.Generic;
using System.Windows.Media;
using EyeCtrl.Core.Models;

/// <summary>
/// The action menu's drawn icon set: <see cref="ActionIcon"/> to WPF <see cref="Geometry"/>.
///
/// <para>
/// <b>Everything is authored on a <see cref="GridSize"/>-unit square at
/// <see cref="GridStrokeThickness"/> stroke,</b> and scaled to the target by the caller's
/// <c>RenderTransform</c>. RenderTransform scales the stroke along with the geometry;
/// <c>Geometry.Transform</c> would not, and would leave a 1.8px hairline on a 63px icon.
/// </para>
///
/// <para>
/// <b>Every action is a mouse action, so every icon is a mouse.</b> Modifiers sit in the margins
/// and never on the body: scroll variants take chevrons at the right, drag takes motion bars at
/// the left. <see cref="ActionIcon.DoubleClick"/> is the deliberate exception — it must differ
/// from <see cref="ActionIcon.LeftClick"/> in outline rather than in detail, because with labels
/// hidden a mark inside the same silhouette is invisible in peripheral vision, which is where a
/// gaze user finds a target before looking straight at it.
/// </para>
///
/// <para>
/// Geometries are parsed once, frozen, and shared. Frozen is not an optimisation here: these are
/// statics handed to every button on screen, and an unfrozen Geometry is both mutable and
/// thread-affine.
/// </para>
/// </summary>
public static class ActionIcons
{
    /// <summary>The square every path in this file is authored on, in grid units.</summary>
    public const double GridSize = 24.0;

    /// <summary>Stroke width in grid units, before the caller's scale transform.</summary>
    public const double GridStrokeThickness = 1.8;

    /// <summary>The mouse outline every action icon is built from: a 10 x 19 stadium at x=7, y=2.5.</summary>
    private const string Body = "M7,7.5 A5,5 0 0 1 17,7.5 L17,16.5 A5,5 0 0 1 7,16.5 Z";

    /// <summary>The button divider and separator: what makes the outline read as a mouse, not a pill.</summary>
    private const string Buttons = " M12,2.5 L12,10.5 M7,10.5 L17,10.5";

    /// <summary>A short wheel stroke, used instead of <see cref="Buttons"/> on the scroll icons.</summary>
    private const string Wheel = " M12,6 L12,9.5";

    private const string LeftButtonFill = "M12,2.5 A5,5 0 0 0 7,7.5 L7,10.5 L12,10.5 Z";
    private const string RightButtonFill = "M12,2.5 A5,5 0 0 1 17,7.5 L17,10.5 L12,10.5 Z";

    private static readonly Dictionary<ActionIcon, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="icon"/>. Throws if the registry has no entry.</summary>
    /// <param name="icon">Which icon to resolve.</param>
    /// <returns>That icon's two layers.</returns>
    public static IconGeometry For(ActionIcon icon) => Map[icon];

    private static Geometry? Parse(string? data)
    {
        if (data is null) return null;
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static IconGeometry Make(string? stroke, string? fill) => new(Parse(stroke), Parse(fill));

    private static Dictionary<ActionIcon, IconGeometry> Build() => new()
    {
        [ActionIcon.LeftClick] = Make(Body + Buttons, LeftButtonFill),

        [ActionIcon.RightClick] = Make(Body + Buttons, RightButtonFill),

        // Two click ripples concentric on (10, 7.5) reaching left of the body, and a "2" drawn
        // in the right margin. Together they change the outline; a badge inside it would be
        // invisible at a glance.
        [ActionIcon.DoubleClick] = Make(
            Body + Buttons
            + " M4.88,8.4 A5.2,5.2 0 0 1 8.05,2.68"
            + " M2.67,8.53 A7.4,7.4 0 0 1 6.3,1.09"
            + " M18.5,16.3 C18.5,14.2 22.9,14.2 22.9,16.6 C22.9,18.2 18.5,19.4 18.5,21.2 L23,21.2",
            LeftButtonFill),

        // Held button plus motion bars trailing at the left.
        [ActionIcon.Drag] = Make(
            Body + Buttons + " M4.4,8.6 L4.4,15.4 M1.6,10.6 L1.6,13.4",
            LeftButtonFill),

        [ActionIcon.ScrollUp] = Make(Body + Wheel + " M18.6,12.4 L20.8,10 L23,12.4", null),

        [ActionIcon.ScrollDown] = Make(Body + Wheel + " M18.6,11.6 L20.8,14 L23,11.6", null),

        [ActionIcon.ScrollLeft] = Make(Body + Wheel + " M22,9.4 L19.6,11.8 L22,14.2", null),

        [ActionIcon.ScrollRight] = Make(Body + Wheel + " M19.6,9.4 L22,11.8 L19.6,14.2", null),

        // Both chevrons: the gaze-joystick mode that scrolls either way.
        [ActionIcon.ScrollMode] = Make(
            Body + Wheel + " M18.6,9.4 L20.8,7 L23,9.4 M18.6,14.6 L20.8,17 L23,14.6", null),

        // A note with two text lines, and a pin whose head is the only filled part.
        [ActionIcon.PinNote] = Make(
            "M5,10 L5,19 A2.5,2.5 0 0 0 7.5,21.5 L16.5,21.5 A2.5,2.5 0 0 0 19,19 L19,10"
            + " A2.5,2.5 0 0 0 16.5,7.5 L7.5,7.5 A2.5,2.5 0 0 0 5,10 Z"
            + " M8.6,12.5 L15.4,12.5 M8.6,16.5 L13,16.5 M12,5.8 L12,7.5",
            "M9.8,3.6 A2.2,2.2 0 1 1 14.2,3.6 A2.2,2.2 0 1 1 9.8,3.6 Z"),

        [ActionIcon.Back] = Make(
            "M10,5.5 L4.5,11 L10,16.5 M4.5,11 L14.5,11 A5,5 0 0 1 19.5,16 L19.5,19", null),

        // Fill-only: three dots, the one icon in the set that is not a mouse. More is navigation,
        // and drawing it as a mouse would say it performs a click.
        [ActionIcon.More] = Make(null,
            "M3.7,12 A1.9,1.9 0 1 1 7.5,12 A1.9,1.9 0 1 1 3.7,12 Z"
            + " M10.1,12 A1.9,1.9 0 1 1 13.9,12 A1.9,1.9 0 1 1 10.1,12 Z"
            + " M16.5,12 A1.9,1.9 0 1 1 20.3,12 A1.9,1.9 0 1 1 16.5,12 Z"),

        // The hook and stem are stroked; the dot is the only filled part.
        [ActionIcon.Help] = Make(
            "M8.6,8.6 A3.5,3.5 0 1 1 13.3,11.9 C12.2,12.3 11.6,13.1 11.6,14.2 L11.6,15.2",
            "M10.3,18.6 A1.3,1.3 0 1 1 12.9,18.6 A1.3,1.3 0 1 1 10.3,18.6 Z"),
    };
}
