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
    private SetupUi(StringTable strings, StringTable installedStrings, ITheme theme, SetupDwellTiming dwell)
    {
        Strings = strings;
        InstalledStrings = installedStrings;
        Theme = theme;
        Dwell = dwell;
    }

    /// <summary>The verb's preferred table (the update package's own during an update).</summary>
    public StringTable Strings { get; }

    /// <summary>The table resolved from the install root (or embedded English): the trusted table
    /// for a result that reports a refused package.</summary>
    public StringTable InstalledStrings { get; }

    /// <summary>The active theme (also published through <see cref="Tokens"/>).</summary>
    public ITheme Theme { get; }

    /// <summary>C9 S6e fixup-06 M3(b): the owner's external-dwell timing, clamped to the App's
    /// legal ranges. Q8's resting-gaze gate is derived from it.</summary>
    public SetupDwellTiming Dwell { get; }

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
        new(StringTable.EmbeddedEnglish, StringTable.EmbeddedEnglish,
            ThemeRegistry.Resolve(null), SetupDwellTiming.Default);

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

        return new SetupUi(preferred, installed, theme, settings.Dwell);
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

            var dwell = SetupDwellTiming.Default;
            if (root.TryGetProperty("externalDwell", out var external)
                && external.ValueKind == JsonValueKind.Object)
            {
                dwell = new SetupDwellTiming(
                    ReadClampedInt(external, "defaultDwellMs", 200, 3000, dwell.DefaultDwellMs),
                    ReadClampedInt(external, "cooldownMs", 0, 5000, dwell.CooldownMs));
            }

            return new SetupUiSettings(themeName, language, dwell);
        }
        catch (Exception)
        {
            return SetupUiSettings.Default;
        }
    }

    /// <summary>Reads one external-dwell number, clamped to the App's own legal range. Missing or
    /// non-numeric keeps the default; a hand-edited out-of-range value is clamped, never used raw
    /// (a 99 s gate would make Q8 unanswerable by gaze).</summary>
    private static int ReadClampedInt(
        JsonElement section, string name, int minimum, int maximum, int fallback) =>
        section.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? Math.Clamp(number, minimum, maximum)
            : fallback;

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

/// <summary>The settings Setup reads: the theme name, the UI language selection, and the owner's
/// external-dwell timing (used by Q8's resting-gaze gate).</summary>
/// <param name="ThemeName">The persisted theme name, or null for the default.</param>
/// <param name="Language">The persisted language selection.</param>
/// <param name="Dwell">The owner's external-dwell timing, clamped to the App's legal ranges.</param>
public readonly record struct SetupUiSettings(string? ThemeName, UiLanguage Language, SetupDwellTiming Dwell)
{
    /// <summary>Midnight, the system language, and the App's default external-dwell timing.</summary>
    public static SetupUiSettings Default =>
        new(null, UiLanguage.System, SetupDwellTiming.Default);
}

/// <summary>
/// The owner's external-dwell timing (C9 S6e fixup-06 M3b, widened by fixup-07 N1).
/// <see cref="RestingGate"/> is the Q8 gate a pre-arm Invoke restarts. It covers a whole dwell
/// RESTART after a blink or tracking gap, not just one clean dwell: at grace 0 a sample silence of
/// 150 ms restarts the detector, so the next Invoke can arrive as late as
/// <c>cooldown + 2 × dwell + a blink</c> after the previous one. A gaze that leaves for longer than
/// the gate and returns sees a normal, armed window.
/// </summary>
/// <param name="DefaultDwellMs">The external dwell in milliseconds (the App's range is 200–3000).</param>
/// <param name="CooldownMs">The post-activation cooldown in milliseconds (0–5000).</param>
public readonly record struct SetupDwellTiming(int DefaultDwellMs, int CooldownMs)
{
    /// <summary>The margin over the worst restart rhythm, so scheduling jitter cannot squeeze a
    /// real dwell between two pre-arm Invokes.</summary>
    public const int MarginMs = 300;

    /// <summary>The blink/tracking-gap allowance: one silence of up to this length anywhere in the
    /// dwell must not let the gate elapse (the detector restarts at 150 ms; 500 ms covers a blink).</summary>
    public const int BlinkAllowanceMs = 500;

    /// <summary>The App's defaults (<c>ExternalDwellSettings</c>): 800 ms dwell, 400 ms cooldown.</summary>
    public static SetupDwellTiming Default => new(800, 400);

    /// <summary>
    /// <c>2 × dwell + cooldown + 500 ms blink allowance + 300 ms margin</c> = 2.8 s at the
    /// defaults. Two dwells because a blink restarts the dwell from zero, and the restart can begin
    /// at the end of the first one.
    /// </summary>
    public TimeSpan RestingGate => TimeSpan.FromMilliseconds(
        (2 * DefaultDwellMs) + CooldownMs + BlinkAllowanceMs + MarginMs);
}
