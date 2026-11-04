namespace EyeCtrl.Setup.Display;

using System.Globalization;
using System.Text.Json;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup.Abstractions;

/// <summary>
/// The theme and language Setup's windows use: the owner's theme from
/// <c>%APPDATA%\EyeCtrl\settings.json</c> when it is readable, else Midnight; the App's UI language,
/// with the German table read from the App's install folder (never a second copy beside Setup).
/// Every failure falls back to the embedded English and the default theme — a hand-edited settings
/// file must never leave the owner without a usable uninstall screen.
/// </summary>
public sealed class SetupUi
{
    private SetupUi(StringTable strings, StringTable installedStrings, ITheme theme)
    {
        Strings = strings;
        InstalledStrings = installedStrings;
        Theme = theme;
    }

    /// <summary>The verb's preferred table (the update package's own during an update).</summary>
    public StringTable Strings { get; }

    /// <summary>The table resolved from the install root (or embedded English): the trusted table
    /// for a result that reports a refused package.</summary>
    public StringTable InstalledStrings { get; }

    /// <summary>The active theme (also published through <see cref="Tokens"/>).</summary>
    public ITheme Theme { get; }

    /// <summary>
    /// The table a result must be rendered from. A <see cref="SetupOutcome.VerifyFailed"/> comes
    /// from a refused package, so its text must never come from that package's own table (a
    /// tampered table could make its refusal read as success); every other result uses the verb's
    /// preferred table.
    /// </summary>
    public StringTable TableFor(SetupResult result) =>
        result.Outcome == SetupOutcome.VerifyFailed ? InstalledStrings : Strings;

    /// <summary>The English/default-theme instance, for tests and as the last resort.</summary>
    public static SetupUi English { get; } =
        new(StringTable.EmbeddedEnglish, StringTable.EmbeddedEnglish, ThemeRegistry.Resolve(null));

    /// <summary>
    /// Reads the owner's settings and loads the tables. <paramref name="preferredDirectory"/> is
    /// tried first (the update package's own folder, whose table comes from the same build as the
    /// code reading it); <paramref name="fallbackDirectory"/> is the folder to look in when neither
    /// the preferred directory nor the install root has a German file (normally the App's own
    /// folder). The install-root table is kept separately for <see cref="TableFor"/>.
    /// </summary>
    public static SetupUi Load(
        SetupPaths paths, IFileSystem fileSystem, string? preferredDirectory = null,
        string? fallbackDirectory = null)
    {
        var settings = ReadSettings(paths, fileSystem);
        var theme = ThemeRegistry.Resolve(settings.ThemeName);
        Tokens.Activate(theme);

        var installedDirectory = FindLooseStringDirectory(
            paths, fileSystem, fallbackDirectory: fallbackDirectory);
        var installed = LoadTable(settings.Language, installedDirectory);

        var preferred = installed;
        if (!string.IsNullOrWhiteSpace(preferredDirectory))
        {
            var preferredResolved = FindLooseStringDirectory(
                paths, fileSystem, preferredDirectory, fallbackDirectory);
            if (!string.Equals(preferredResolved, installedDirectory, StringComparison.OrdinalIgnoreCase))
                preferred = LoadTable(settings.Language, preferredResolved);
        }

        return new SetupUi(preferred, installed, theme);
    }

    private static StringTable LoadTable(UiLanguage language, string? directory)
    {
        try
        {
            return StringTable.Load(language, CultureInfo.CurrentUICulture, directory);
        }
        catch (Exception)
        {
            return StringTable.EmbeddedEnglish;
        }
    }

    /// <summary>Reads the two fields Setup needs, tolerantly.</summary>
    internal static SetupUiSettings ReadSettings(SetupPaths paths, IFileSystem fileSystem)
    {
        try
        {
            if (!fileSystem.FileExists(paths.SettingsPath))
                return SetupUiSettings.Default;
            return ParseSettings(fileSystem.ReadAllText(paths.SettingsPath));
        }
        catch (Exception)
        {
            return SetupUiSettings.Default;
        }
    }

    /// <summary>Parses <c>settings.json</c>; missing, damaged or unknown means the defaults.</summary>
    public static SetupUiSettings ParseSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return SetupUiSettings.Default;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return SetupUiSettings.Default;

            string? themeName = null;
            if (root.TryGetProperty("theme", out var theme)
                && theme.ValueKind == JsonValueKind.Object
                && theme.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                themeName = name.GetString();
            }

            var language = UiLanguage.System;
            if (root.TryGetProperty("general", out var general)
                && general.ValueKind == JsonValueKind.Object
                && general.TryGetProperty("uiLanguage", out var uiLanguage)
                && uiLanguage.ValueKind == JsonValueKind.String
                && UiLanguageTokens.TryFromJson(uiLanguage.GetString(), out var parsed))
            {
                language = parsed;
            }

            return new SetupUiSettings(themeName, language);
        }
        catch (Exception)
        {
            return SetupUiSettings.Default;
        }
    }

    /// <summary>
    /// The first folder that holds <c>strings.de.json</c>: the preferred folder (an update's new
    /// package), the App's install folder (the copy the App ships), then the explicit fallback and
    /// Setup's own folder. Null means embedded English.
    /// </summary>
    internal static string? FindLooseStringDirectory(
        SetupPaths paths, IFileSystem fileSystem, string? preferredDirectory = null,
        string? fallbackDirectory = null)
    {
        var candidates = new[]
        {
            preferredDirectory, paths.InstallRoot, fallbackDirectory, AppContext.BaseDirectory,
        };
        foreach (var directory in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            try
            {
                if (fileSystem.FileExists(Path.Combine(directory, StringTable.GermanFileName)))
                    return directory;
            }
            catch (Exception)
            {
                // An unreadable candidate is simply not the table's folder.
            }
        }

        return null;
    }
}

/// <summary>The two settings Setup reads: the theme name and the UI language selection.</summary>
/// <param name="ThemeName">The persisted theme name, or null for the default.</param>
/// <param name="Language">The persisted language selection.</param>
public readonly record struct SetupUiSettings(string? ThemeName, UiLanguage Language)
{
    /// <summary>Midnight and the system language.</summary>
    public static SetupUiSettings Default => new(null, UiLanguage.System);
}
