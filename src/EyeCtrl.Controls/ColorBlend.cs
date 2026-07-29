namespace EyeCtrl.Controls;

using System.Windows.Media;
using EyeCtrl.Controls.Theme;

/// <summary>
/// Provides linear colour interpolation and dwell-highlight utilities.
/// </summary>
public static class ColorBlend
{
    /// <summary>
    /// Returns a colour that is a linear interpolation between <paramref name="from"/>
    /// and <paramref name="to"/> at parameter <paramref name="t"/>.
    /// </summary>
    public static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A * (1 - t) + to.A * t),
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }

    /// <summary>
    /// Returns a <see cref="SolidColorBrush"/> that blends <paramref name="baseColor"/>
    /// toward <see cref="Tokens.Accent"/> as dwell progress increases — the standard
    /// dwell-highlight overlay pattern used by buttons, toggles, tabs, and pagination
    /// controls. Blending toward the accent rather than white is what makes
    /// "accent means dwell in progress" (spec §4.1.2) true across the whole app.
    /// </summary>
    /// <param name="baseColor">Resting background colour of the dwell target.</param>
    /// <param name="progress">Dwell progress in [0, 1].</param>
    /// <param name="factor">Blend factor applied to progress (default 0.5).</param>
    public static SolidColorBrush ToHighlightBrush(Color baseColor, double progress, double factor = 0.5)
    {
        var t = Math.Clamp(progress * factor, 0, 1);
        return new SolidColorBrush(Blend(baseColor, Tokens.Accent, t));
    }
}
