namespace EyeCtrl.Core.Localization;

using System.Globalization;
using System.Text.Json.Nodes;
using EyeCtrl.Core.Enums;

/// <summary>
/// The pure string resolver behind every localized UI string. The language selection and the UI
/// culture are constructor inputs — it never reads <see cref="CultureInfo.CurrentUICulture"/>
/// itself — and all lookups come from one cached dictionary built once, with no IO per call.
/// </summary>
/// <remarks>
/// <para>
/// The fallback chain is German → embedded English → the key itself. A raw key appears only
/// through a build defect (a key referenced but absent from <c>strings.json</c>), which the key
/// coverage gate catches; the resolver logs it loudly and never throws.
/// </para>
/// <para>
/// A key resolves to German when its surface is listed in the German manifest <b>and</b> the German
/// file carries a value for it. A manifest surface that is missing any key degrades to
/// whole-surface English with a warning — never a half-German mix. There is deliberately no runtime
/// <c>src</c> freshness check; exactly one canonicalizer (the Python validator) exists, and it fails
/// CI on a stale <c>src</c>.
/// </para>
/// </remarks>
public sealed class StringTable
{
    /// <summary>The manifest resource name the embedded English table is compiled under.</summary>
    public const string EmbeddedResourceName = "EyeCtrl.Core.Localization.strings.json";

    /// <summary>File name of the loose German table, expected beside the App exe.</summary>
    public const string GermanFileName = "strings.de.json";

    /// <summary>File name of the loose German surface manifest, expected beside the App exe.</summary>
    public const string ManifestFileName = "strings.de.manifest.json";

    private static readonly CultureInfo GermanFormatCulture = CultureInfo.GetCultureInfo("de-DE");

    private static readonly StringTable EmbeddedEnglishTable = Create(
        UiLanguage.English,
        CultureInfo.InvariantCulture,
        ParseEmbeddedEnglish(warn: null),
        StringTableContent.Empty,
        germanSurfaces: [],
        warn: null);

    private static StringTable _current = EmbeddedEnglishTable;

    private readonly IReadOnlyDictionary<string, StringTableText> _english;
    private readonly IReadOnlyDictionary<string, StringTableText> _germanEffective;
    private readonly Action<string>? _warn;

    private StringTable(
        UiLanguage selection,
        CultureInfo culture,
        StringTableContent english,
        StringTableContent german,
        IReadOnlyCollection<string> germanSurfaces,
        Action<string>? warn)
    {
        Selection = selection;
        Culture = culture;
        Language = ResolveLanguage(selection, culture);
        FormatProvider = Language == UiLanguage.German ? GermanFormatCulture : CultureInfo.InvariantCulture;
        _english = english.Texts;
        _warn = warn;
        _germanEffective = BuildEffectiveGerman(english, german, germanSurfaces, warn);
    }

    /// <summary>The selection this table was built for (<c>System</c>, <c>German</c> or <c>English</c>).</summary>
    public UiLanguage Selection { get; }

    /// <summary>The effective language after resolving <see cref="Selection"/> against <see cref="Culture"/>.</summary>
    public UiLanguage Language { get; }

    /// <summary>The injected UI culture <c>System</c> was resolved against.</summary>
    public CultureInfo Culture { get; }

    /// <summary>de-DE for German, invariant for English — the explicit provider every format uses.</summary>
    public IFormatProvider FormatProvider { get; }

    /// <summary>The process-global table. Defaults to embedded English before anything loads.</summary>
    /// <remarks>
    /// Global state like <c>Tokens.Current</c>, and deliberately so: the XAML markup extension and
    /// code-built rows read it without a dependency-injection path, and a failure inside
    /// <c>InitializeSettingsAndLogging</c> reaches the startup-error box before any table has been
    /// loaded — the default must therefore be the compiled English.
    /// </remarks>
    public static StringTable Current => _current;

    /// <summary>The compiled English table every process starts on.</summary>
    public static StringTable EmbeddedEnglish => EmbeddedEnglishTable;

    /// <summary>Makes <paramref name="table"/> the process-global table.</summary>
    /// <param name="table">The table to activate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is null.</exception>
    public static void Activate(StringTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _current = table;
    }

    /// <summary>Puts the process-global table back to the compiled English default.</summary>
    public static void ResetToEmbeddedEnglish() => _current = EmbeddedEnglishTable;

    /// <summary>
    /// Builds a table from injected content — the seam that lets tests and later tickets create
    /// tables without touching <see cref="Current"/>.
    /// </summary>
    /// <param name="selection">The configured language selection.</param>
    /// <param name="culture">The UI culture <c>System</c> resolves against.</param>
    /// <param name="english">The English content; its <c>where</c> metadata drives surface gating.</param>
    /// <param name="german">The German content, if any.</param>
    /// <param name="germanSurfaces">The manifest's surface ids; only these may resolve German.</param>
    /// <param name="warn">Receives warning lines; null discards them.</param>
    /// <returns>The table.</returns>
    public static StringTable Create(
        UiLanguage selection,
        CultureInfo culture,
        StringTableContent english,
        StringTableContent? german = null,
        IReadOnlyCollection<string>? germanSurfaces = null,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(english);
        ArgumentNullException.ThrowIfNull(culture);
        return new StringTable(
            selection,
            culture,
            english,
            german ?? StringTableContent.Empty,
            germanSurfaces ?? [],
            warn);
    }

    /// <summary>
    /// Loads the shipped tables: embedded English always, and the loose German file and manifest
    /// from <paramref name="looseFileDirectory"/> when given. A missing or corrupt German file or
    /// manifest means English plus a log — never a failed start.
    /// </summary>
    /// <param name="selection">The configured language selection.</param>
    /// <param name="culture">The UI culture <c>System</c> resolves against.</param>
    /// <param name="looseFileDirectory">The directory beside the App exe, or null for embedded English only.</param>
    /// <param name="warn">Receives warning lines; null discards them.</param>
    /// <returns>The table.</returns>
    public static StringTable Load(
        UiLanguage selection,
        CultureInfo culture,
        string? looseFileDirectory,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var english = ParseEmbeddedEnglish(warn);
        if (string.IsNullOrWhiteSpace(looseFileDirectory))
        {
            return Create(selection, culture, english, warn: warn);
        }

        return Create(
            selection,
            culture,
            english,
            ReadGerman(looseFileDirectory, warn),
            ReadManifest(looseFileDirectory, warn),
            warn);
    }

    /// <summary>Resolves <paramref name="key"/> in the effective language.</summary>
    /// <param name="key">The literal key, e.g. <c>supervisor.fallback.title</c>.</param>
    /// <returns>The resolved text; the raw key when the table has no such key.</returns>
    public string T(string key) => T(key, []);

    /// <summary>Resolves and formats <paramref name="key"/> in the effective language.</summary>
    /// <param name="key">The literal key, e.g. <c>settings.count</c>.</param>
    /// <param name="args">Format arguments; for a plural entry the first numeric argument picks the form.</param>
    /// <returns>The resolved text; the raw key when the table has no such key.</returns>
    public string T(string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Language == UiLanguage.German
            && _germanEffective.TryGetValue(key, out var germanText))
        {
            return FormatText(germanText, args, FormatProvider);
        }

        if (_english.TryGetValue(key, out var englishText))
        {
            return FormatText(englishText, args, FormatProvider);
        }

        _warn?.Invoke(
            $"String key '{key}' is missing from the English table; the raw key is shown. "
            + "This is a build defect — add the key to strings.json.");
        return key;
    }

    /// <summary>
    /// Resolves <paramref name="key"/> in a named language, gated by the manifest.
    /// </summary>
    /// <remarks>
    /// The German branch resolves through the same effective map the instance uses, so a named
    /// lookup obeys the whole-surface rule: an unsigned-off surface — T04's target-language
    /// restart prompt before T11 included — shows English, never a partial or unapproved German.
    /// <see cref="UiLanguage.System"/> names this instance's effective language.
    /// </remarks>
    /// <param name="language">The language to resolve in.</param>
    /// <param name="key">The literal key.</param>
    /// <param name="args">Format arguments; for a plural entry the first numeric argument picks the form.</param>
    /// <returns>The resolved text; the raw key when the named language and English both lack it.</returns>
    public string T(UiLanguage language, string key, params object?[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(
                nameof(language), language, "Not a defined UiLanguage.");
        }

        var resolved = language == UiLanguage.System ? Language : language;
        var provider = resolved == UiLanguage.German ? GermanFormatCulture : CultureInfo.InvariantCulture;

        if (resolved == UiLanguage.German
            && _germanEffective.TryGetValue(key, out var germanText))
        {
            return FormatText(germanText, args, provider);
        }

        if (_english.TryGetValue(key, out var englishText))
        {
            return FormatText(englishText, args, provider);
        }

        _warn?.Invoke(
            $"String key '{key}' is missing from the English table; the raw key is shown. "
            + "This is a build defect — add the key to strings.json.");
        return key;
    }

    private static UiLanguage ResolveLanguage(UiLanguage selection, CultureInfo culture) => selection switch
    {
        UiLanguage.German => UiLanguage.German,
        UiLanguage.English => UiLanguage.English,
        _ => string.Equals(culture.TwoLetterISOLanguageName, "de", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.German
            : UiLanguage.English
    };

    /// <summary>
    /// The effective German map: for every manifest surface, all of its English keys must have a
    /// complete German value, or the whole surface stays English with a warning.
    /// </summary>
    private static IReadOnlyDictionary<string, StringTableText> BuildEffectiveGerman(
        StringTableContent english,
        StringTableContent german,
        IReadOnlyCollection<string> germanSurfaces,
        Action<string>? warn)
    {
        var effective = new Dictionary<string, StringTableText>(StringComparer.Ordinal);
        foreach (var surface in germanSurfaces.Distinct(StringComparer.Ordinal))
        {
            var keys = english.SurfacesByKey
                .Where(pair => string.Equals(pair.Value, surface, StringComparison.Ordinal))
                .Select(pair => pair.Key)
                .ToList();
            if (keys.Count == 0) continue;

            var missing = keys
                .Where(key => !german.Texts.TryGetValue(key, out _))
                .ToList();
            if (missing.Count > 0)
            {
                warn?.Invoke(
                    $"Surface '{surface}' is declared in the manifest but incomplete in "
                    + $"{GermanFileName} (missing: {string.Join(", ", missing)}); showing English "
                    + "for the whole surface.");
                continue;
            }

            foreach (var key in keys) effective[key] = german.Texts[key];
        }

        return effective;
    }

    private string FormatText(StringTableText text, object?[] args, IFormatProvider provider)
    {
        var template = text.IsPlural ? SelectPluralForm(text, args) : text.Value!;
        try
        {
            return string.Format(provider, template, args);
        }
        catch (FormatException ex)
        {
            _warn?.Invoke($"String template '{template}' could not be formatted: {ex.Message}");
            return template;
        }
    }

    /// <summary>
    /// Chooses the plural form. Both rules are settled and documented here:
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count is the first numeric argument.</b> Arguments are inspected in order and the
    /// first integer-typed or floating-typed value is the count; an entry formatted without a
    /// numeric argument falls back to <c>other</c>.
    /// </para>
    /// <para>
    /// <b><c>one</c> applies only to an integral count of exactly 1.</b> A floating-point or
    /// decimal count is treated as a visible decimal even when its value is whole — 1.0 renders
    /// as "1.0" under <c>{0:0.0}</c> — so it takes <c>other</c>, following CLDR's rule for
    /// languages without a special decimal category (German and English alike).
    /// </para>
    /// </remarks>
    private static string SelectPluralForm(StringTableText text, object?[] args)
    {
        foreach (var arg in args)
        {
            if (!TryGetCount(arg, out var count, out var integral)) continue;
            return integral && count == 1m ? text.One! : text.Other!;
        }

        return text.Other!;
    }

    private static bool TryGetCount(object? value, out decimal count, out bool integral)
    {
        switch (value)
        {
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                count = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                integral = true;
                return true;
            case float or double or decimal:
                try
                {
                    count = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    integral = false;
                    return true;
                }
                catch (OverflowException)
                {
                    break;
                }
            default:
                break;
        }

        count = 0m;
        integral = false;
        return false;
    }

    private static StringTableContent ParseEmbeddedEnglish(Action<string>? warn)
    {
        try
        {
            using var stream = typeof(StringTable).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
            {
                warn?.Invoke(
                    $"Embedded string table resource '{EmbeddedResourceName}' is missing; "
                    + "every key will show raw.");
                return StringTableContent.Empty;
            }

            using var reader = new StreamReader(stream);
            return StringTableContent.ParseEnglish(reader.ReadToEnd(), warn);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"Embedded string table could not be read ({ex.Message}); every key will show raw.");
            return StringTableContent.Empty;
        }
    }

    private static StringTableContent ReadGerman(string directory, Action<string>? warn)
    {
        var path = Path.Combine(directory, GermanFileName);
        try
        {
            if (!File.Exists(path))
            {
                warn?.Invoke($"'{GermanFileName}' was not found beside the App; German surfaces will show English.");
                return StringTableContent.Empty;
            }

            return StringTableContent.ParseGerman(File.ReadAllText(path), warn);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"'{GermanFileName}' could not be read ({ex.Message}); German surfaces will show English.");
            return StringTableContent.Empty;
        }
    }

    private static IReadOnlyCollection<string> ReadManifest(string directory, Action<string>? warn)
    {
        var path = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(path))
            {
                warn?.Invoke($"'{ManifestFileName}' was not found beside the App; German surfaces will show English.");
                return [];
            }

            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
            {
                warn?.Invoke($"'{ManifestFileName}' is not a JSON array of surface ids; German surfaces will show English.");
                return [];
            }

            var surfaces = new List<string>();
            foreach (var node in array)
            {
                if (node is JsonValue value
                    && value.TryGetValue(out string? surface)
                    && !string.IsNullOrWhiteSpace(surface))
                {
                    surfaces.Add(surface);
                }
                else
                {
                    warn?.Invoke($"'{ManifestFileName}' holds a non-string surface entry; it is ignored.");
                }
            }

            return surfaces;
        }
        catch (Exception ex)
        {
            // Catch-all, like ReadGerman: a truncated manifest mid-write, an IO error or anything
            // else must leave the App on English rather than escape into the startup try.
            warn?.Invoke($"'{ManifestFileName}' could not be read ({ex.Message}); German surfaces will show English.");
            return [];
        }
    }
}
