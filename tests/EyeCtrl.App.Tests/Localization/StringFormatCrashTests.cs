namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
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
