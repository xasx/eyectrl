namespace EyeCtrl.IconBuilder;

using System.Windows;

/// <summary>
/// All constants come from docs/superpowers/specs/2026-09-30-blank-slate-icon-design.md
/// (the blank-slate redesign that superseded the 2026-09-28 pie-iris design).
/// The 96-grid design renders at scale size/96; one geometry serves all six .ico sizes.
/// </summary>
internal static class IconSpec
{
    public const double GridSize = 96;

    // Tile — flat near-black navy, FULL-BLEED: other tray icons draw edge-to-edge, so the
    // old 4-unit transparent border made the whole icon look smaller than its neighbours.
    public const double TileX = 0, TileY = 0, TileExtent = 96, TileRadius = 24;
    public static readonly System.Windows.Media.Color Tile = System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x28);

    // Amber gaze point — the light source. r28 = 58% of the tile width: a touch smaller than
    // the r32 the owner called "a bit big", now that the tile fills the frame.
    public static readonly Point DotCenter = new(48, 48);
    public const double DotRadius = 28;
    public static readonly System.Windows.Media.Color Amber = System.Windows.Media.Color.FromRgb(0xE8, 0xA3, 0x3C);

    // Trail — one fading motion dot toward the upper-left (two faint ones were invisible at tray size)
    public static readonly Point TrailCenter = new(14, 15);
    public const double TrailRadius = 7.5;
    public const double TrailAlpha = 0.5;

    // Amber bloom behind the dot (radial gradient, Gaussian-ish falloff)
    public const double BloomRadius = 42;
    public static readonly (double Offset, double Alpha)[] BloomStops =
        [(0.0, 0.38), (0.55, 0.22), (1.0, 0.0)];

    // Dwell arc — starts 12 o'clock, sweeps 120° clockwise (mirrors DwellArcButton).
    // Screen angles (y-down): 270° = 12 o'clock.
    public const double ArcRadius = 41;
    public const double ArcWidth = 10;
    public const double ArcAlpha = 0.85;
    public const double ArcStartDegrees = 270;
    public const double ArcSweepDegrees = 120;
    public static readonly System.Windows.Media.Color Cyan = System.Windows.Media.Color.FromRgb(0xA5, 0xF3, 0xFC);

    public static Point PointOnArc(double degrees) =>
        new(DotCenter.X + ArcRadius * System.Math.Cos(System.Math.PI * degrees / 180),
            DotCenter.Y + ArcRadius * System.Math.Sin(System.Math.PI * degrees / 180));
}

/// <summary>One .ico frame. The blank-slate design is a single geometry — frames differ only in size.</summary>
internal sealed record FramePlan(int Size)
{
    /// <summary>The six ICO frames, ascending (directory order).</summary>
    public static IReadOnlyList<FramePlan> All { get; } =
        [new FramePlan(16), new FramePlan(32), new FramePlan(48), new FramePlan(64), new FramePlan(128), new FramePlan(256)];
}
