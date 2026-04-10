namespace EyeCtrl.App.Controls;

using System.Windows.Media;

/// <summary>
/// Provides linear colour interpolation between two <see cref="Color"/> values.
/// </summary>
public static class ColorBlend
{
    /// <summary>
    /// Returns a colour that is a linear interpolation between <paramref name="from"/>
    /// and <paramref name="to"/> at parameter <paramref name="t"/>.
    /// </summary>
    /// <param name="from">The starting colour, returned when <paramref name="t"/> is 0.</param>
    /// <param name="to">The ending colour, returned when <paramref name="t"/> is 1.</param>
    /// <param name="t">
    /// Interpolation factor. Values outside [0, 1] are clamped.
    /// </param>
    /// <returns>The interpolated <see cref="Color"/>.</returns>
    public static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A * (1 - t) + to.A * t),
            (byte)(from.R * (1 - t) + to.R * t),
            (byte)(from.G * (1 - t) + to.G * t),
            (byte)(from.B * (1 - t) + to.B * t));
    }
}
