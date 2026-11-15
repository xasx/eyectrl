namespace EyeCtrl.Setup.Display;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;

/// <summary>
/// The opaque, frozen brushes Setup's screens draw with (r3 F11): every theme fill carries 0xFA
/// alpha, and N1's opaque-surface rule (SetupProgressWindow.cs:186-192) applies to bands, edges
/// and tints alike. One definition per compilation unit — Tasks 5, 6, 7 and 8 consume these.
/// </summary>
internal static class SetupBrushes
{
    /// <summary>A frozen solid brush in the given colour, alpha included.</summary>
    public static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The N1 opaque variant of a theme colour, frozen.</summary>
    public static SolidColorBrush Opaque(Color color) =>
        Frozen(Color.FromRgb(color.R, color.G, color.B));

    /// <summary>The N1 opaque variant as a colour (for contrast arithmetic and pins).</summary>
    public static Color OpaqueColor(Color color) => Color.FromRgb(color.R, color.G, color.B);

    /// <summary>A linear colour blend: <paramref name="ratio"/> of <paramref name="ink"/> over
    /// <paramref name="over"/> (the confirm band's 0.18 danger tint).</summary>
    public static Color Blend(Color over, Color ink, double ratio) => Color.FromRgb(
        (byte)(ink.R * ratio + over.R * (1 - ratio)),
        (byte)(ink.G * ratio + over.G * (1 - ratio)),
        (byte)(ink.B * ratio + over.B * (1 - ratio)));

    /// <summary>SD7/r3 F11: the confirm band's danger tint — AccentDanger at 0.18 over the
    /// OPAQUE SurfaceMedium, frozen.</summary>
    public static SolidColorBrush DangerTint() =>
        Frozen(Blend(OpaqueColor(Tokens.SurfaceMedium), Tokens.AccentDanger, 0.18));
}
