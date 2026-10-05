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
/// <b>Most actions are mouse actions, so most icons are mice.</b> The deliberate exceptions —
/// <see cref="ActionIcon.More"/> (navigation, not a click), <see cref="ActionIcon.Keyboard"/>
/// and <see cref="ActionIcon.KeyboardEdge"/> (keyboard commands, not mouse actions), and
/// <see cref="ActionIcon.Recapture"/> (re-shoots the screen region, not a click) — break
/// the mouse silhouette on purpose: drawing them as mice would say they perform a click.
/// Modifiers sit in the margins and never on the body: scroll variants take chevrons at the
/// right, drag takes motion bars at the left. <see cref="ActionIcon.DoubleClick"/> is the
/// other exception — it must differ from <see cref="ActionIcon.LeftClick"/> in outline
/// rather than in detail, because with labels hidden a mark inside the same silhouette is
/// invisible in peripheral vision, which is where a gaze user finds a target before looking
/// straight at it.
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
    /// <summary>
    /// The app's icon grid: every icon registry (<see cref="ActionIcons"/> and
    /// <see cref="ToolbarIcons"/>) authors its paths on this size square, in grid units, and
    /// <see cref="EyeCtrl.Controls.DwellArcButton"/> scales from it.
    /// </summary>
    public const double GridSize = 24.0;

    /// <summary>Stroke width in grid units, before the caller's scale transform.</summary>
    public const double GridStrokeThickness = 1.8;

    /// <summary>
    /// The mouse outline every action icon is built from: a 10 x 19 stadium at x=7, y=2.5. Also
    /// shared by <see cref="ToolbarIcons"/>' <c>ProviderMouse</c>, which draws one plain mouse
    /// rather than a second, slightly-different one.
    /// </summary>
    internal const string Body = "M7,7.5 A5,5 0 0 1 17,7.5 L17,16.5 A5,5 0 0 1 7,16.5 Z";

    /// <summary>The button divider and separator: what makes the outline read as a mouse, not a pill.</summary>
    private const string Buttons = " M12,2.5 L12,10.5 M7,10.5 L17,10.5";

    /// <summary>
    /// A short wheel stroke, used instead of <see cref="Buttons"/> on the scroll icons. Also
    /// shared by <see cref="ToolbarIcons"/>' <c>ProviderMouse</c>.
    /// </summary>
    internal const string Wheel = " M12,6 L12,9.5";

    private const string LeftButtonFill = "M12,2.5 A5,5 0 0 0 7,7.5 L7,10.5 L12,10.5 Z";
    private const string RightButtonFill = "M12,2.5 A5,5 0 0 1 17,7.5 L17,10.5 L12,10.5 Z";

    // Modifier keycaps for the left-edge mod bar. Keycap = rounded rect x8..16 over y4..7 with
    // the modifier's mark below it. The latched variants add a rim circling the glyph (primary
    // cue) and a filled dot in the right margin (secondary cue).
    private const string Keycap = "M8.2,7.2 L8.2,5.2 A1.2,1.2 0 0 1 9.4,4 L14.6,4 A1.2,1.2 0 0 1 15.8,5.2 L15.8,7.2";
    private const string ShiftMark = " M6.5,9.5 L12,4.6 L17.5,9.5 M12,4.6 L12,19";
    private const string CtrlMark = " M9,10.5 L12,7.8 L15,10.5 M12,13 L12,19 M8,16.5 L16,16.5";
    private const string AltMark = " M8.5,10.5 L10.5,12.4 M15.5,10.5 L13.5,12.4 M10.5,12.4 A3,3 0 1 0 13.5,12.4 M12,15.4 L12,19";

    /// <summary>
    /// The rim the latched keycap variants draw around the whole glyph. Internal because the
    /// registry test pins that the latched STROKE geometry contains it — the rim is the primary
    /// latched cue (outline-level, the peripheral-vision rule), the filled dot is secondary.
    /// Written in the path mini-language's canonical serialization (comma separators, one figure
    /// per arc) because the test asserts it against <see cref="Geometry.ToString"/>, which
    /// re-serializes rather than echoing the authored text. Integer-only values keep it
    /// parseable under every culture: the parser treats "." decimals and "," separators the same
    /// way regardless of the thread's culture, but a decimal mark would serialize as "," on a
    /// comma-decimal culture and break the substring match there.
    /// </summary>
    internal const string LatchedRim = " M4,8A2,2,0,0,1,6,6 M20,6A2,2,0,0,1,22,8 M22,16A2,2,0,0,1,20,18 M6,18A2,2,0,0,1,4,16";
    private const string LatchedDot = "M19.9,2.1 A1.9,1.9 0 1 1 20,5.9 A1.9,1.9 0 1 1 19.9,2.1 Z";

    private static readonly Dictionary<ActionIcon, IconGeometry> Map = Build();

    /// <summary>The geometry for <paramref name="icon"/>. Throws if the registry has no entry.</summary>
    /// <param name="icon">Which icon to resolve.</param>
    /// <returns>That icon's two layers.</returns>
    public static IconGeometry For(ActionIcon icon) => Map[icon];

    private static Dictionary<ActionIcon, IconGeometry> Build() => new()
    {
        [ActionIcon.LeftClick] = IconGeometry.FromPathData(Body + Buttons, LeftButtonFill),

        [ActionIcon.RightClick] = IconGeometry.FromPathData(Body + Buttons, RightButtonFill),

        // The pressed middle button: the strip between the top cap and the divider, filled. The
        // divider's vertical line rides the fill the way it bounds the Left/Right button fills.
        [ActionIcon.MiddleClick] = IconGeometry.FromPathData(
            Body + Buttons,
            "M10.9,10.5 L10.9,3.6 A1.1,1.1 0 0 1 13.1,3.6 L13.1,10.5 Z"),

        // Two click ripples concentric on (10, 7.5) reaching left of the body, and a "2" drawn
        // in the right margin. Together they change the outline; a badge inside it would be
        // invisible at a glance.
        [ActionIcon.DoubleClick] = IconGeometry.FromPathData(
            Body + Buttons
            + " M4.88,8.4 A5.2,5.2 0 0 1 8.05,2.68"
            + " M2.67,8.53 A7.4,7.4 0 0 1 6.3,1.09"
            + " M18.5,16.3 C18.5,14.2 22.9,14.2 22.9,16.6 C22.9,18.2 18.5,19.4 18.5,21.2 L23,21.2",
            LeftButtonFill),

        // Held button plus motion bars trailing at the left.
        [ActionIcon.Drag] = IconGeometry.FromPathData(
            Body + Buttons + " M4.4,8.6 L4.4,15.4 M1.6,10.6 L1.6,13.4",
            LeftButtonFill),

        [ActionIcon.ScrollUp] = IconGeometry.FromPathData(Body + Wheel + " M18.6,12.4 L20.8,10 L23,12.4"),

        [ActionIcon.ScrollDown] = IconGeometry.FromPathData(Body + Wheel + " M18.6,11.6 L20.8,14 L23,11.6"),

        [ActionIcon.ScrollLeft] = IconGeometry.FromPathData(Body + Wheel + " M22,9.4 L19.6,11.8 L22,14.2"),

        [ActionIcon.ScrollRight] = IconGeometry.FromPathData(Body + Wheel + " M19.6,9.4 L22,11.8 L19.6,14.2"),

        // Both chevrons: the gaze-joystick mode that scrolls either way.
        [ActionIcon.ScrollMode] = IconGeometry.FromPathData(
            Body + Wheel + " M18.6,9.4 L20.8,7 L23,9.4 M18.6,14.6 L20.8,17 L23,14.6"),

        // A note with two text lines, and a pin whose head is the only filled part.
        [ActionIcon.PinNote] = IconGeometry.FromPathData(
            "M5,10 L5,19 A2.5,2.5 0 0 0 7.5,21.5 L16.5,21.5 A2.5,2.5 0 0 0 19,19 L19,10"
            + " A2.5,2.5 0 0 0 16.5,7.5 L7.5,7.5 A2.5,2.5 0 0 0 5,10 Z"
            + " M8.6,12.5 L15.4,12.5 M8.6,16.5 L13,16.5 M12,5.8 L12,7.5",
            "M9.8,3.6 A2.2,2.2 0 1 1 14.2,3.6 A2.2,2.2 0 1 1 9.8,3.6 Z"),

        // A keyboard: rounded body, one dash row, a second short row, and a centred space bar.
        // The first non-mouse ACTION icon — see the revised class doc above.
        [ActionIcon.Keyboard] = IconGeometry.FromPathData(
            "M6,7.5 L18,7.5 A2.5,2.5 0 0 1 20.5,10 L20.5,15 A2.5,2.5 0 0 1 18,17.5 L6,17.5 A2.5,2.5 0 0 1 3.5,15 L3.5,10 A2.5,2.5 0 0 1 6,7.5 Z"
            + " M6.8,10.7 L9.2,10.7 M10.7,10.7 L13.3,10.7 M14.8,10.7 L17.2,10.7"
            + " M6.8,13.2 L9.2,13.2 M14.8,13.2 L17.2,13.2"
            + " M9.7,15.7 L14.3,15.7"),

        // A compact keyboard with chevrons above and below: the edge bar's flip-to-other-edge
        // affordance. Chevrons change the outline from the plain Keyboard glyph, per the
        // peripheral-vision rule in the class doc.
        [ActionIcon.KeyboardEdge] = IconGeometry.FromPathData(
            "M7,10.2 L16.2,10.2 A2,2 0 0 1 18.2,12.2 L18.2,14.2 A2,2 0 0 1 16.2,16.2 L7,16.2 A2,2 0 0 1 5,14.2 L5,12.2 A2,2 0 0 1 7,10.2 Z"
            + " M7.8,12 L9.8,12 M10.8,12 L12.8,12 M13.8,12 L15.4,12"
            + " M7.8,14.4 L15.4,14.4"
            + " M9.6,7.4 L11.6,5.4 L13.6,7.4 M9.6,19 L11.6,21 L13.6,19"),

        // A clockwise rotation arrow, the drawn form of the ↻ glyph Recapture carried before it
        // had an icon: a near-full circle with the arrowhead at the upper-right, pointing along
        // the spin. Mirrored from ToolbarIcons' ResetAdaptation (the counter-clockwise arrow
        // that resets adaptation) so the two rotation marks share one shape language; the clock
        // direction is the whole difference, and it is the meaning — Recapture re-shoots the
        // capture forward from now, ResetAdaptation unwinds to baseline.
        [ActionIcon.Recapture] = IconGeometry.FromPathData(
            "M21,12 A9,9 0 1 1 12,3 A9,9 0 0 1 18.74,5.74 L21,8 M21,3 L21,8 L16,8"),

        [ActionIcon.Back] = IconGeometry.FromPathData(
            "M10,5.5 L4.5,11 L10,16.5 M4.5,11 L14.5,11 A5,5 0 0 1 19.5,16 L19.5,19"),

        // Fill-only: three dots. More is one of the set's three non-mouse icons (see the class
        // doc): navigation, and drawing it as a mouse would say it performs a click.
        [ActionIcon.More] = IconGeometry.FromPathData(null,
            "M3.7,12 A1.9,1.9 0 1 1 7.5,12 A1.9,1.9 0 1 1 3.7,12 Z"
            + " M10.1,12 A1.9,1.9 0 1 1 13.9,12 A1.9,1.9 0 1 1 10.1,12 Z"
            + " M16.5,12 A1.9,1.9 0 1 1 20.3,12 A1.9,1.9 0 1 1 16.5,12 Z"),

        // A plain X. The one target that must read as "none of the above", so it borrows no part
        // of the mouse silhouette the actions share.
        [ActionIcon.Close] = IconGeometry.FromPathData("M6,6 L18,18 M18,6 L6,18"),

        // The hook and stem are stroked; the dot is the only filled part.
        [ActionIcon.Help] = IconGeometry.FromPathData(
            "M8.6,8.6 A3.5,3.5 0 1 1 13.3,11.9 C12.2,12.3 11.6,13.1 11.6,14.2 L11.6,15.2",
            "M10.3,18.6 A1.3,1.3 0 1 1 12.9,18.6 A1.3,1.3 0 1 1 10.3,18.6 Z"),

        // Modifier keycaps for the left-edge mod bar (24-grid; the latched variants' PRIMARY cue
        // is the rim drawn into the stroke geometry, the filled dot at (20, 4) in the right margin
        // is secondary — the peripheral-vision rule is why the dot alone is not enough).
        [ActionIcon.Shift] = IconGeometry.FromPathData(Keycap + ShiftMark),
        [ActionIcon.Ctrl] = IconGeometry.FromPathData(Keycap + CtrlMark),
        [ActionIcon.Alt] = IconGeometry.FromPathData(Keycap + AltMark),
        [ActionIcon.ShiftLatched] = IconGeometry.FromPathData(Keycap + ShiftMark + LatchedRim, LatchedDot),
        [ActionIcon.CtrlLatched] = IconGeometry.FromPathData(Keycap + CtrlMark + LatchedRim, LatchedDot),
        [ActionIcon.AltLatched] = IconGeometry.FromPathData(Keycap + AltMark + LatchedRim, LatchedDot),
    };
}
