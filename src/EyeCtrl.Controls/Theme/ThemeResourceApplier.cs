namespace EyeCtrl.Controls.Theme;

using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

/// <summary>
/// Writes a theme's brushes, thickness, geometry and typography into a
/// <see cref="ResourceDictionary"/>, so the 106 {StaticResource Brush*} lookups across the
/// overlay XAML resolve to that theme.
///
/// OverlayStyles.xaml pulls the same values through {x:Static}, but those resolve
/// when WPF parses the dictionary — which happens during Application construction,
/// before OnStartup can choose a theme. Writing explicitly makes the result
/// independent of that ordering. Applied to Application.Resources at the top level,
/// these entries take precedence over the merged dictionary's.
///
/// Keys are the ITheme property names verbatim, plus four derived CornerRadius keys and
/// StandardBorderThickness.
/// </summary>
public static class ThemeResourceApplier
{
    /// <summary>
    /// The XAML resource key holding the raw pixel value of the border thickness. Written by
    /// the reflected loop under its property name, not by this const — the const exists for
    /// callers that want to reference the key symbolically.
    /// </summary>
    public const string ThicknessKey = nameof(ITheme.BorderThicknessStandard);

    /// <summary>The XAML resource key holding the uniform <see cref="Thickness"/> form.</summary>
    public const string UniformThicknessKey = "StandardBorderThickness";

    /// <summary>
    /// Every value the theme publishes, paired with its resource key. Reflected over
    /// <see cref="ITheme"/> rather than listed, so a token added to the interface cannot be
    /// forgotten here. <c>Name</c> is excluded by name, not by type — <c>FontFamilyStandard</c>
    /// is also a string. Raw <see cref="Color"/>s are excluded because no XAML consumes them.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/value pairs; order unspecified — irrelevant here, since keys are distinct
    /// and writes are independent.</returns>
    public static IEnumerable<(string Key, object Value)> EnumeratePublishedValues(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType != typeof(Color) && p.Name != nameof(ITheme.Name))
            .Select(p => (p.Name, p.GetValue(theme)!));

    /// <summary>
    /// Every brush the theme exposes. Kept as its own accessor for callers that want only the
    /// brushes; currently unused — <see cref="Apply"/> uses <see cref="EnumeratePublishedValues"/>
    /// instead — but retained as the brush-only projection for external consumers.
    /// </summary>
    /// <param name="theme">The theme to read.</param>
    /// <returns>Key/brush pairs.</returns>
    public static IEnumerable<(string Key, SolidColorBrush Brush)> EnumerateBrushes(ITheme theme) =>
        typeof(ITheme).GetProperties()
            .Where(p => p.PropertyType == typeof(SolidColorBrush))
            .Select(p => (p.Name, (SolidColorBrush)p.GetValue(theme)!));

    /// <summary>
    /// Writes <paramref name="theme"/> into <paramref name="target"/>, replacing any
    /// existing entries under the same keys.
    ///
    /// The reflected half covers everything on <see cref="ITheme"/>. The explicit tail covers
    /// the *derived* values, which are not interface members and would otherwise reach XAML
    /// only through <c>{x:Static}</c> — resolved at parse time, before a theme is chosen, and
    /// therefore frozen to Midnight. See the design spec §6.6;
    /// <c>Apply_CoversEveryKeyTheDictionaryDeclares</c> fails if this tail falls behind
    /// <c>OverlayStyles.xaml</c>.
    /// </summary>
    /// <param name="target">The dictionary to write into — normally <c>Application.Resources</c>.</param>
    /// <param name="theme">The theme to publish.</param>
    public static void Apply(ResourceDictionary target, ITheme theme)
    {
        foreach (var (key, value) in EnumeratePublishedValues(theme))
            target[key] = value;

        var t = theme.BorderThicknessStandard;
        target[UniformThicknessKey] = new Thickness(t, t, t, t);

        target["SmallCornerRadius"] = new CornerRadius(theme.CornerRadiusSmall);
        target["StandardCornerRadius"] = new CornerRadius(theme.CornerRadiusStandard);
        target["LargeCornerRadius"] = new CornerRadius(theme.CornerRadiusLarge);
        target["CircleCornerRadius"] = new CornerRadius(theme.CornerRadiusCircle);
    }
}
