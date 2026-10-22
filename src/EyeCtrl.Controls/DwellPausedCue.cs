namespace EyeCtrl.Controls;

/// <summary>
/// The one "paused, not reset" look every dwell surface shares: a dwell whose gaze has stepped off
/// the target keeps its progress for the pause grace, and the progress indicator shows that by
/// dimming — same value, same thickness, same colour, no motion and no flash.
///
/// <para>
/// A single opacity, applied to the PROGRESS element only (the accent arc or bar). The dark halo
/// under an arc is left alone: it is the contrast guarantee that the dimmed accent is read
/// against. Dimming lowers the accent's luminance toward that halo; it never changes its hue.
/// </para>
///
/// <para>
/// 0.7 is a measured choice, not a taste one. At the pixel the accent arc covers, the dimmed
/// colour is <c>0.7·accent + 0.3·halo</c> (the arc is stroked over its halo), and
/// <c>DwellPausedCueContrastTests</c> pins that colour at 3:1 or better against every button fill
/// the arcs and bars are drawn on, in all four themes. Raising the dimming past that floor makes
/// the held value unreadable, which defeats the point of holding it.
/// </para>
/// </summary>
public static class DwellPausedCue
{
    /// <summary>Opacity of a progress arc or bar while its dwell is held. 1 is the normal look.</summary>
    public const double ProgressOpacity = 0.7;

    /// <summary>The opacity a progress element takes for the given paused state.</summary>
    public static double OpacityFor(bool paused) => paused ? ProgressOpacity : 1.0;
}
