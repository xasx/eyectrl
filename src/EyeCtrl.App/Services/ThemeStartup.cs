namespace EyeCtrl.App.Services;

using System;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls;
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
        MigrateHaloedMarks(theme, settings);
        ThemeResourceApplier.Apply(appResources, theme);
        return theme;
    }

    /// <summary>
    /// Re-derives the persisted haloed-mark colours — the cursor ring and the drag
    /// trail — for the theme being applied. Both are saved as absolute hexes (§7.1
    /// ruling: user-chosen, theme-independent), and every theme's palette presets
    /// are absolute hexes too, so a theme switch carries the OLD theme's mark onto
    /// the NEW theme's StrokeHalo. There the mark/halo edge that §4.1.3 leans on
    /// collapses — Midnight's accent on Daylight's white halo measures 1.25:1, and
    /// the ring is the gaze user's only pointer.
    ///
    /// <para>
    /// The rule is stateless and symmetric, so no "previous theme" needs persisting:
    /// a value that is already one of the new theme's presets stays; a value that is
    /// a preset of ANY other theme maps to the same-index preset of the new one
    /// (green stays green — the lists share one index structure); a custom value
    /// stays only while it clears 7:1 against the new halo, else it snaps to the
    /// setting's own default preset (index 0 for the ring, 2 for the trail, whose
    /// default is deliberately the warning, not the accent).
    /// </para>
    ///
    /// <para>
    /// <see cref="CalibrationSettings.TargetColor"/> and
    /// <see cref="AutoSelectionSettings.HighlightColor"/> are deliberately NOT
    /// migrated. The calibration dot is user-coloured and now draws on the scrim
    /// with a StrokeHalo rim (and its persisted value is finally APPLIED — round 3,
    /// R1, a latent main-branch bug), so it survives every theme without migration;
    /// the selection highlight remains an unhaloed mark over raw desktop content,
    /// backdrop-dependent on every theme alike — a theme switch does not change
    /// its situation.
    /// </para>
    /// </summary>
    /// <param name="theme">The theme being activated.</param>
    /// <param name="settings">Loaded application settings; mutated in place.</param>
    internal static void MigrateHaloedMarks(ITheme theme, EyeCtrlSettings settings)
    {
        settings.Cursor.CircleColor = MigrateMark(settings.Cursor.CircleColor, 0, theme);
        settings.Drag.TrailColor = MigrateMark(settings.Drag.TrailColor, 2, theme);
    }

    private static string MigrateMark(string? persisted, int defaultPresetIndex, ITheme theme)
    {
        var value = (persisted ?? "").Trim();
        var presets = DwellColorPicker.PresetsFor(theme);

        foreach (var preset in presets)
        {
            if (HexEquals(preset.Hex, value))
                return preset.Hex;
        }

        foreach (var other in ThemeRegistry.All)
        {
            if (ReferenceEquals(other, theme))
                continue;

            var otherPresets = DwellColorPicker.PresetsFor(other);
            for (var i = 0; i < otherPresets.Length; i++)
            {
                if (HexEquals(otherPresets[i].Hex, value))
                    return presets[Math.Min(i, presets.Length - 1)].Hex;
            }
        }

        return ClearsHaloBar(value, theme.StrokeHalo) ? value : presets[defaultPresetIndex].Hex;
    }

    private static bool HexEquals(string a, string b) =>
        string.Equals(a.Trim().TrimStart('#'), b.Trim().TrimStart('#'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The §4.1.3 bar the colour-picker presets are held to: 7:1 against the halo.</summary>
    private static bool ClearsHaloBar(string hex, Color halo)
    {
        try
        {
            var colour = (Color)ColorConverter.ConvertFromString(hex)!;
            return Contrast(colour, halo) >= 7.0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
