namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;

/// <summary>
/// T10: the German draft as a TEST table. The shipped manifest stays empty until T11–T13, so
/// production resolves no German at all; T09's gates run on German by building a table here with
/// a TEST manifest that names every surface (<see cref="StringTableSchema.Surfaces"/>) — the
/// switch-on, rehearsed in the test process only. Nothing is written beside the App.
///
/// <para>
/// The German is read from the repo's source copy (<c>src/EyeCtrl.App/Localization/strings.de.json</c>),
/// the file the App ships, so a test measures exactly the German under review. EyeCtrl.App.Tests and
/// EyeCtrl.Supervisor.Tests link this one file, like <see cref="PseudoLocale"/>.
/// </para>
/// </summary>
internal static class GermanDraft
{
    private static readonly Lazy<string> GermanJson = new(() => File.ReadAllText(GermanPath));

    private static readonly Lazy<(StringTable Table, IReadOnlyList<string> Warnings)> TableValue = new(() =>
    {
        // Only what the table says while it LOADS: once built, the table is shared by every test in
        // the run, and a later caller's runtime warning (a template resolved without its arguments,
        // as SettingsWindow does for the combo's "not available" format) is not a load problem.
        var warnings = new List<string>();
        var loading = true;
        void Warn(string line)
        {
            if (loading) warnings.Add(line);
        }

        var table = StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(EnglishJson(), Warn),
            StringTableContent.ParseGerman(GermanJson.Value, Warn),
            TestManifest,
            Warn);
        loading = false;
        return (table, warnings);
    });

    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> Entries = new(() =>
    {
        using var document = JsonDocument.Parse(GermanJson.Value);
        return document.RootElement.EnumerateObject()
            .Where(p => !p.Name.StartsWith('_'))
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("text").Clone(), StringComparer.Ordinal);
    });

    /// <summary>The test manifest: every surface, so every key with German resolves German.</summary>
    internal static IReadOnlyList<string> TestManifest => StringTableSchema.Surfaces;

    /// <summary>The German table with every surface switched on, de-DE number formats included.</summary>
    internal static StringTable Table => TableValue.Value.Table;

    /// <summary>What the table logged while LOADING: a surface left English for a missing key, a bad entry.</summary>
    internal static IReadOnlyList<string> LoadWarnings => TableValue.Value.Warnings;

    /// <summary>The shipped German file's source copy.</summary>
    internal static string GermanPath =>
        Path.Combine(RepoRoot(), "src", "EyeCtrl.App", "Localization", StringTable.GermanFileName);

    /// <summary>The keys of <paramref name="fitClass"/> with their GERMAN text, plural forms expanded.</summary>
    /// <exception cref="InvalidOperationException">A key of the class has no German, or the wrong shape.</exception>
    internal static IReadOnlyList<FitCatalog.Item> ItemsOf(string fitClass) =>
        FitCatalog.ItemsOf(fitClass).Select(item => item with { Text = TextOf(item.Key, item.Form) }).ToList();

    /// <summary>The German text of one key (and plural form, "" when singular).</summary>
    internal static string TextOf(string key, string form)
    {
        if (!Entries.Value.TryGetValue(key, out var text))
            throw new InvalidOperationException($"{key} has no German in {StringTable.GermanFileName}.");
        if (form.Length == 0)
        {
            return text.ValueKind == JsonValueKind.String
                ? text.GetString()!
                : throw new InvalidOperationException($"{key}: German is plural, English is not.");
        }

        return text.ValueKind == JsonValueKind.Object && text.TryGetProperty(form, out var value)
            ? value.GetString()!
            : throw new InvalidOperationException($"{key}: German has no '{form}' form.");
    }

    /// <summary>The English keys with no German entry.</summary>
    internal static IReadOnlyList<string> MissingKeys() =>
        FitCatalog.FitByKey().Keys.Where(k => !Entries.Value.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

    private static string EnglishJson()
    {
        using var stream = typeof(StringTable).Assembly.GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{StringTable.EmbeddedResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EyeCtrl.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("EyeCtrl.slnx was not found above " + AppContext.BaseDirectory);
    }
}
