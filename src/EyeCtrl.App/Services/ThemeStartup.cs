namespace EyeCtrl.App.Services;

using System.Windows;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Settings;

/// <summary>
/// Resolves the persisted theme and makes it active, for both the C# call sites
/// (via <see cref="Tokens"/>) and the XAML lookups (via the resource dictionary).
///
/// Extracted from App.OnStartup so it can be exercised without launching the app.
/// Must run before any overlay window or dwell control is constructed.
/// </summary>
public static class ThemeStartup
{
    /// <summary>
    /// Applies <c>settings.Theme.Name</c>, falling back to Midnight if the name is
    /// not recognised.
    /// </summary>
    /// <param name="settings">Loaded application settings.</param>
    /// <param name="appResources">
    /// The application's top-level resource dictionary. Entries written here shadow
    /// the merged OverlayStyles.xaml, so the theme reaches every
    /// {StaticResource Brush*} regardless of when WPF parsed that dictionary.
    /// </param>
    /// <returns>The theme that was activated.</returns>
    public static ITheme Apply(EyeCtrlSettings settings, ResourceDictionary appResources)
    {
        var theme = ThemeRegistry.Resolve(settings.Theme.Name);
        Tokens.Activate(theme);
        ThemeResourceApplier.Apply(appResources, theme);
        return theme;
    }
}
