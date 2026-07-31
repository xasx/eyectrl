namespace EyeCtrl.Controls.Theme;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Writes a theme's brushes and thickness into a <see cref="ResourceDictionary"/>,
/// so the 106 {StaticResource Brush*} lookups across the overlay XAML resolve to
/// that theme.
///
/// OverlayStyles.xaml pulls the same values through {x:Static}, but those resolve
/// when WPF parses the dictionary — which happens during Application construction,
/// before OnStartup can choose a theme. Writing explicitly makes the result
/// independent of that ordering. Applied to Application.Resources at the top level,
/// these entries take precedence over the merged dictionary's.
///
/// Keys are the ITheme brush property names verbatim (BrushAccent, BrushSurfaceDark,
/// …), which is exactly what OverlayStyles.xaml uses.
/// </summary>
public static class ThemeResourceApplier
{
    /// <summary>The XAML resource key holding the raw pixel value of the border thickness.</summary>
    public const string ThicknessKey = "BorderThicknessStandard";

    /// <summary>The XAML resource key holding the uniform <see cref="Thickness"/> form.</summary>
    public const string UniformThicknessKey = "StandardBorderThickness";

    /// <summary>
    /// Every brush the theme exposes, paired with the resource key it is published
    /// under. Reflected over <see cref="ITheme"/> rather than listed, so a token
    /// added to the interface cannot be forgotten here.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/brush pairs, in interface declaration order.</returns>
    public static IEnumerable<(string Key, SolidColorBrush Brush)> EnumerateBrushes(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(SolidColorBrush))
            .Select(p => (p.Name, (SolidColorBrush)p.GetValue(theme)!));

    /// <summary>
    /// Writes <paramref name="theme"/> into <paramref name="target"/>, replacing any
    /// existing entries under the same keys.
    /// </summary>
    /// <param name="target">The dictionary to write into — normally <c>Application.Resources</c>.</param>
    /// <param name="theme">The theme to publish.</param>
    public static void Apply(ResourceDictionary target, ITheme theme)
    {
        foreach (var (key, brush) in EnumerateBrushes(theme))
            target[key] = brush;

        var t = theme.BorderThicknessStandard;
        target[ThicknessKey] = t;
        target[UniformThicknessKey] = new Thickness(t, t, t, t);
    }
}
