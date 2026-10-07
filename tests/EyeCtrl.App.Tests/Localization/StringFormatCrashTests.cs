namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// The <c>string.Format</c> crash check: every key and every plural form, with dummy arguments,
/// under de-DE and invariant. A malformed template or a placeholder/args mismatch throws
/// <see cref="FormatException"/> here; the resolver itself catches and logs it, so only a gate
/// that formats the raw templates can make the defect loud before a user sees an unresolved one.
/// </summary>
public class StringFormatCrashTests
{
    private const string FixtureEnglish = """
    {
      "settings.count": { "text": { "one": "{0} file", "other": "{0} files" }, "who": "user", "where": "settings", "fit": "row", "args": ["count"] },
      "settings.percent": { "text": "{0} %", "who": "user", "where": "settings", "fit": "row", "args": ["value"] },
      "settings.marker": { "text": "✓ OK · {0}", "who": "user", "where": "settings", "fit": "row", "args": ["name"] },
      "overlays.braces": { "text": "Set {{literal}} {0}", "who": "user", "where": "overlays", "fit": "A44", "args": ["name"] }
    }
    """;

    private const string FixtureGerman = """
    {
      "settings.count": { "text": { "one": "{0} Datei", "other": "{0} Dateien" }, "src": "00000000" },
      "settings.percent": { "text": "{0} %", "src": "00000000" },
      "settings.marker": { "text": "✓ OK · {0}", "src": "00000000" },
      "overlays.braces": { "text": "Setze {{literal}} {0}", "src": "00000000" }
    }
    """;

    private static object[] Dummies(int count, int first) =>
        Enumerable.Range(0, count).Select(i => i == 0 ? (object)first : "x").ToArray();

    private static IEnumerable<string> Forms(JsonElement text)
    {
        if (text.ValueKind == JsonValueKind.String) yield return text.GetString()!;
        else
        {
            yield return text.GetProperty("one").GetString()!;
            yield return text.GetProperty("other").GetString()!;
        }
    }

    [Fact]
    public void Fixture_PluralBracesAndNonAscii_FormatUnderBothLanguages()
    {
        var german = StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(FixtureEnglish),
            StringTableContent.ParseGerman(FixtureGerman),
            ["settings", "overlays"]);

        Check.That(german.T("settings.count", 1)).IsEqualTo("1 Datei");
        Check.That(german.T("settings.count", 3)).IsEqualTo("3 Dateien");
        Check.That(german.T("settings.percent", 12.5)).IsEqualTo("12,5 %");
        Check.That(german.T("settings.marker", "Anna")).IsEqualTo("✓ OK · Anna");
        Check.That(german.T("overlays.braces", "x")).IsEqualTo("Setze {literal} x");

        var english = StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish(FixtureEnglish));
        Check.That(english.T("settings.count", 1)).IsEqualTo("1 file");
        Check.That(english.T("settings.percent", 12.5)).IsEqualTo("12.5 %");
    }

    [Fact]
    public void Fixture_EveryKeyAndForm_FormatsDirectlyUnderBothCultures()
    {
        using var document = JsonDocument.Parse(FixtureEnglish);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var count = entry.Value.GetProperty("args").GetArrayLength();
            foreach (var form in Forms(entry.Value.GetProperty("text")))
            {
                foreach (var provider in new IFormatProvider[]
                         {
                             CultureInfo.GetCultureInfo("de-DE"),
                             CultureInfo.InvariantCulture
                         })
                {
                    var dummies = Dummies(count, 3);
                    Check.ThatCode(() => { _ = string.Format(provider, form, dummies); }).DoesNotThrow();
                }
            }
        }
    }

    [Fact]
    public void ShippedTable_EveryKeyAndForm_FormatsDirectlyUnderDeDeAndInvariant()
    {
        using var document = LocalizationTestData.EnglishDocument();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var count = entry.Value.TryGetProperty("args", out var args) ? args.GetArrayLength() : 0;
            foreach (var form in Forms(entry.Value.GetProperty("text")))
            {
                foreach (var provider in new IFormatProvider[]
                         {
                             CultureInfo.GetCultureInfo("de-DE"),
                             CultureInfo.InvariantCulture
                         })
                {
                    var dummies = Dummies(count, 1);
                    Check.ThatCode(() => { _ = string.Format(provider, form, dummies); }).DoesNotThrow();
                }
            }
        }
    }

    [Fact]
    public void ShippedGermanTable_EveryKeyAndForm_FormatsDirectlyUnderDeDe()
    {
        // The German file is loose and never parsed by the embedded-English check: a German
        // template with a dropped or shifted placeholder renders the raw template (or silently
        // loses the number), so it gets the same direct-format crash check as English.
        var path = Path.Combine(
            SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Localization", StringTable.GermanFileName);
        Check.WithCustomMessage($"The shipped German table is missing at {path}")
            .That(File.Exists(path)).IsTrue();

        Dictionary<string, int> argsByKey;
        using (var english = LocalizationTestData.EnglishDocument())
        {
            argsByKey = ArgsByKey(english);
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var count = argsByKey.TryGetValue(entry.Name, out var declared) ? declared : 0;
            foreach (var form in Forms(entry.Value.GetProperty("text")))
            {
                var dummies = Dummies(count, 3);
                Check.ThatCode(() => { _ = string.Format(CultureInfo.GetCultureInfo("de-DE"), form, dummies); })
                    .DoesNotThrow();
            }
        }
    }

    [Fact]
    public void Fixture_GermanEveryKeyAndForm_FormatsDirectlyUnderDeDe()
    {
        using var english = JsonDocument.Parse(FixtureEnglish);
        var argsByKey = ArgsByKey(english);
        using var document = JsonDocument.Parse(FixtureGerman);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var count = argsByKey.TryGetValue(entry.Name, out var declared) ? declared : 0;
            foreach (var form in Forms(entry.Value.GetProperty("text")))
            {
                var dummies = Dummies(count, 3);
                Check.ThatCode(() => { _ = string.Format(CultureInfo.GetCultureInfo("de-DE"), form, dummies); })
                    .DoesNotThrow();
            }
        }
    }

    /// <summary>English <c>args</c> count per key — the German file carries no args metadata.</summary>
    private static Dictionary<string, int> ArgsByKey(JsonDocument english)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in english.RootElement.EnumerateObject())
        {
            counts[entry.Name] = entry.Value.TryGetProperty("args", out var args)
                ? args.GetArrayLength()
                : 0;
        }
        return counts;
    }

    [Fact]
    public void ShippedTable_EveryKeyAndForm_ResolvesThroughTheTable()
    {
        var english = LocalizationTestData.EnglishContent();
        var table = StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            english);

        using var document = LocalizationTestData.EnglishDocument();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var count = entry.Value.TryGetProperty("args", out var args) ? args.GetArrayLength() : 0;
            foreach (var plural in new[] { 1, 3 })
            {
                var dummies = Dummies(count, plural);
                Check.That(table.T(entry.Name, dummies)).Not.IsEqualTo(entry.Name);
                Check.That(table.T(UiLanguage.German, entry.Name, dummies)).Not.IsEqualTo(entry.Name);
            }
        }
    }
}
