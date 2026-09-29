namespace EyeCtrl.IconBuilder;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// All constants come from docs/superpowers/specs/2026-09-28-app-icon-redesign-design.md
/// (@ 9af69129). The 96-grid design renders at scale size/96.
/// </summary>
internal static class IconSpec
{
    public const double GridSize = 96;

    // Tile
    public const double TileX = 4, TileY = 4, TileExtent = 88, TileRadius = 22;
    public static readonly Color TileLight = Color.FromRgb(0x25, 0x2B, 0x4A);
    public static readonly Color TileDark = Color.FromRgb(0x14, 0x18, 0x28);
    public static readonly Color TileFlat16 = Color.FromRgb(0x1A, 0x20, 0x38);
    public static readonly Color Rim = Color.FromArgb(140, 0x4A, 0x55, 0x78); // 55 %

    // Glow (own visual — an Effect applies to the visual's whole subtree)
    public static readonly Point GlowCenter = new(48, 47);
    public const double GlowRx = 30, GlowRy = 21;
    public static readonly Color Glow = Color.FromArgb(26, 0xA5, 0xF3, 0xFC); // 10 %
    public const double BlurRadius = 12; // r2 measured sigma ≈ Radius/3, local (96-grid) units

    // Body (almond)
    public const string BodyPath = "M12,48 C26,26 70,26 84,48 C70,70 26,70 12,48 Z";
    public static readonly Color BodyTop = Color.FromRgb(0xAF, 0xF0, 0xFF);
    public static readonly Color BodyBottom = Color.FromRgb(0x4A, 0x7E, 0xCF);
    public const double BodyGradientTop = 25, BodyGradientBottom = 71;

    // Iris track + hairline
    public static readonly Color Track = Color.FromRgb(0x14, 0x18, 0x28);
    public static readonly Color Hairline = Color.FromRgb(0x0A, 0x0A, 0x14);

    // Whisper arc
    public static readonly Color Arc = Color.FromRgb(0xA5, 0xF3, 0xFC);

    // Pie (¾ CCW from 12 o'clock to 3 o'clock — deliberate mirror of DwellArcButton's CW sweep)
    public static readonly Color PieHead = Color.FromRgb(0xF6, 0xC7, 0x7E);   // at (60,48)
    public static readonly Color PieTail = Color.FromRgb(0xE8, 0xA3, 0x3C);   // at (36,44)
    public static readonly Color PieSolid16 = Color.FromRgb(0xE8, 0xA3, 0x3C);

    // Gaze dot
    public static readonly Color Dot = Color.FromRgb(0xF7, 0xFC, 0xFF);

    // Invariant formatting: this box runs de-DE, where double.ToString() emits "34,5" and
    // the comma kills the path grammar.
    public static string PiePath(double radius) =>
        FormattableString.Invariant(
            $"M48,48 L48,{48 - radius} A{radius},{radius} 0 1 0 {48 + radius},48 Z");

    public static string ArcPath(double radius) =>
        FormattableString.Invariant(
            $"M{48 + radius},48 A{radius},{radius} 0 0 0 48,{48 - radius}");
}

/// <summary>Per-frame layer toggles — the spec's layers × frames table, one record per frame.</summary>
internal sealed record FramePlan(
    int Size,
    bool FlatTile,
    bool Rim,
    bool Glow,
    double GlowPeakAlpha,   // bloom peak opacity; small frames need more than the master's 10% to read at all
    bool Hairline,
    double TrackRadius,
    double? ArcRadius,
    double? ArcWidth,
    double ArcAlpha,        // 0..1 brush opacity
    double PieRadius,
    bool PieGradient,       // false = solid PieSolid16
    Point DotCenter,
    double DotRadius)
{
    // Declared BEFORE All: static initializers run in declaration order — an initializer
    // that read these after declaration would store nulls (the CS8601s this replaces were real).
    private static readonly FramePlan Tuned32 = new(
        32,
        FlatTile: false, Rim: false, Glow: false, GlowPeakAlpha: 0, Hairline: false,
        TrackRadius: 14,
        ArcRadius: 11.5, ArcWidth: 3, ArcAlpha: 0.60,
        PieRadius: 13, PieGradient: true,
        DotCenter: new Point(61, 48), DotRadius: 5);

    private static readonly FramePlan Tuned16 = new(
        16,
        FlatTile: true, Rim: false, Glow: false, GlowPeakAlpha: 0, Hairline: false,
        TrackRadius: 15.5,
        ArcRadius: null, ArcWidth: null, ArcAlpha: 0,
        PieRadius: 14.5, PieGradient: false,
        DotCenter: new Point(62.5, 48), DotRadius: 6);

    /// <summary>The six ICO frames, ascending (directory order).</summary>
    public static IReadOnlyList<FramePlan> All { get; } =
    [
        Tuned16,
        Tuned32,
        Master(48),
        Master(64),
        Master(128),
        Master(256),
    ];

    public static FramePlan PlanFor(int size) =>
        All.FirstOrDefault(p => p.Size == size) ?? Master(size);

    public static FramePlan Master(int size) => new(
        size,
        FlatTile: false, Rim: true, Glow: true, GlowPeakAlpha: 0.10, Hairline: true,
        TrackRadius: 13.5,
        ArcRadius: 11.5, ArcWidth: 2, ArcAlpha: 0.55,
        PieRadius: 12.5, PieGradient: true,
        DotCenter: new Point(60.5, 48), DotRadius: 4.5);
}
