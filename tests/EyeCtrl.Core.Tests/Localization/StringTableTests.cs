namespace EyeCtrl.Core.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Globalization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// The resolver's behaviour, always through an injected table instance — the process-global
/// <see cref="StringTable.Current"/> is never touched here (the assembly-wide restore hook and
/// observer exist for the tests that must touch it; see StringTableIsolationTests).
/// </summary>
public class StringTableTests
{
    private const string DefaultEnglish = """
    {
      "settings.alpha": { "text": "Alpha", "who": "user", "where": "settings", "fit": "row", "args": [] },
      "settings.beta": { "text": "Beta", "who": "user", "where": "settings", "fit": "row", "args": [] },
      "settings.price": { "text": "Price {0}", "who": "user", "where": "settings", "fit": "row", "args": ["value"] },
      "overlays.count": { "text": { "one": "{0} item", "other": "{0} items" }, "who": "user", "where": "overlays", "fit": "A44", "args": ["count"] }
    }
    """;

    private const string DefaultGerman = """
    {
      "settings.alpha": { "text": "Alfa", "src": "00000000" },
      "settings.beta": { "text": "Beta-de", "src": "00000000" },
      "settings.price": { "text": "Preis {0}", "src": "00000000" },
      "overlays.count": { "text": { "one": "{0} Element", "other": "{0} Elemente" }, "src": "00000000" }
    }
    """;

    private static StringTable Build(
        UiLanguage selection = UiLanguage.German,
        string culture = "de-DE",
        string? english = null,
        string? german = null,
        string[]? manifest = null,
        List<string>? warnings = null) =>
        StringTable.Create(
            selection,
            CultureInfo.GetCultureInfo(culture),
            StringTableContent.ParseEnglish(english ?? DefaultEnglish),
            StringTableContent.ParseGerman(german ?? DefaultGerman),
            manifest,
            warnings is null ? null : warnings.Add);

    [Fact]
    public void GermanKey_OnAManifestSurface_ResolvesGerman()
    {
        var table = Build(manifest: ["settings"]);
        Check.That(table.T("settings.alpha")).IsEqualTo("Alfa");
    }

    [Fact]
    public void GermanKey_OnANonManifestSurface_ResolvesEnglish()
    {
        var table = Build(manifest: ["settings"]);
        Check.That(table.T("overlays.count", 2)).IsEqualTo("2 items");
    }

    [Fact]
    public void IncompleteManifestSurface_DegradesTheWholeSurfaceToEnglish_WithAWarning()
    {
        // settings.beta has German text, but settings.alpha is missing: the surface is incomplete,
        // so beta must fall back too — never a half-German screen.
        var warnings = new List<string>();
        var table = Build(
            german: """{ "settings.beta": { "text": "Beta-de", "src": "00000000" } }""",
            manifest: ["settings"],
            warnings: warnings);

        Check.That(table.T("settings.alpha")).IsEqualTo("Alpha");
        Check.That(table.T("settings.beta")).IsEqualTo("Beta");
        Check.That(table.FormatProviderFor("settings")).IsSameReferenceAs(CultureInfo.InvariantCulture);
        Check.That(warnings).HasElementThatMatches(w => w.Contains("settings", StringComparison.Ordinal)
            && w.Contains("incomplete", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteSurface_DoesNotAffectACompleteOne()
    {
        var table = Build(
            german: """
            {
              "settings.beta": { "text": "Beta-de", "src": "00000000" },
              "overlays.count": { "text": { "one": "{0} Element", "other": "{0} Elemente" }, "src": "00000000" }
            }
            """,
            manifest: ["overlays", "settings"]);

        Check.That(table.T("overlays.count", 1)).IsEqualTo("1 Element");
        Check.That(table.T("settings.alpha")).IsEqualTo("Alpha");
    }

    [Fact]
    public void Fallback_IsEnglishThenTheRawKey()
    {
        var warnings = new List<string>();
        var table = Build(manifest: ["settings"], warnings: warnings);

        // German exists for overlays.count, but the surface is not switched on: English.
        Check.That(table.T("overlays.count", 3)).IsEqualTo("3 items");
        // A key absent everywhere shows itself, loudly but without throwing.
        Check.That(table.T("settings.nonexistent")).IsEqualTo("settings.nonexistent");
        Check.That(warnings).HasElementThatMatches(w => w.Contains("settings.nonexistent", StringComparison.Ordinal));
    }

    [Fact]
    public void ShippedEmptyTables_ResolveEverythingInEnglish()
    {
        var table = StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(DefaultEnglish),
            StringTableContent.ParseGerman("{}"),
            []);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(table.T("settings.alpha")).IsEqualTo("Alpha");
    }

    [Theory]
    [InlineData("de-DE", UiLanguage.German)]
    [InlineData("de-AT", UiLanguage.German)]
    [InlineData("en-US", UiLanguage.English)]
    [InlineData("fr-FR", UiLanguage.English)]
    public void System_ResolvesAgainstTheInjectedCulture(string culture, UiLanguage expected)
    {
        var table = Build(selection: UiLanguage.System, culture: culture, manifest: ["settings"]);
        Check.That(table.Language).IsEqualTo(expected);
    }

    [Fact]
    public void System_OnInvariantCulture_IsEnglish()
    {
        var table = StringTable.Create(
            UiLanguage.System,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish(DefaultEnglish));
        Check.That(table.Language).IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void ExplicitSelections_IgnoreTheCulture()
    {
        Check.That(Build(selection: UiLanguage.German, culture: "en-US", manifest: ["settings"]).Language)
            .IsEqualTo(UiLanguage.German);
        Check.That(Build(selection: UiLanguage.English, culture: "de-DE", manifest: ["settings"]).Language)
            .IsEqualTo(UiLanguage.English);
    }

    [Fact]
    public void Plurals_PickTheFormFromTheFirstNumericArgument()
    {
        var table = Build(selection: UiLanguage.English);
        Check.That(table.T("overlays.count", 1)).IsEqualTo("1 item");
        Check.That(table.T("overlays.count", 2)).IsEqualTo("2 items");
        Check.That(table.T("overlays.count", 0)).IsEqualTo("0 items");
        Check.That(table.T("overlays.count", 3L)).IsEqualTo("3 items");
    }

    [Fact]
    public void Plurals_ChooseTheFormFromTheCountAsTheTemplateFormatsIt()
    {
        var table = Build(selection: UiLanguage.English);
        Check.That(table.T("overlays.count", 1)).IsEqualTo("1 item");
        Check.That(table.T("overlays.count", 2)).IsEqualTo("2 items");
        Check.That(table.T("overlays.count", 0)).IsEqualTo("0 items");
        Check.That(table.T("overlays.count", 1L)).IsEqualTo("1 item");

        // The count is formatted with the template's own specifier: under {0} a whole double
        // reads "1" and is singular; decimal preserves its scale, so 1.0m reads "1.0" (other).
        Check.That(table.T("overlays.count", 1.0)).IsEqualTo("1 item");
        Check.That(table.T("overlays.count", 1.0f)).IsEqualTo("1 item");
        Check.That(table.T("overlays.count", 1m)).IsEqualTo("1 item");
        Check.That(table.T("overlays.count", 1.0m)).IsEqualTo("1.0 items");
    }

    [Fact]
    public void Plurals_UnderADecimalFormat_TheFormattedCountDecides()
    {
        var table = StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish("""
            {
              "overlays.decimal": { "text": { "one": "{0:0.0} unit", "other": "{0:0.0} units" }, "who": "user", "where": "overlays", "fit": "A44", "args": ["count"] }
            }
            """));

        // "{0:0.0}" renders every count with a visible decimal, so even 1 reads "1.0" (other).
        Check.That(table.T("overlays.decimal", 1)).IsEqualTo("1.0 units");
        Check.That(table.T("overlays.decimal", 1.0)).IsEqualTo("1.0 units");
        Check.That(table.T("overlays.decimal", 2)).IsEqualTo("2.0 units");
    }

    [Fact]
    public void Plurals_WithTheCountAtArgumentIndexOne_UseTheFormattedRule()
    {
        const string english = """
        {
          "settings.files": { "text": { "one": "{0}: {1} file", "other": "{0}: {1} files" }, "who": "user", "where": "settings", "fit": "row", "args": ["name", "count"] },
          "settings.units": { "text": { "one": "{0}: {1:0.0} unit", "other": "{0}: {1:0.0} units" }, "who": "user", "where": "settings", "fit": "row", "args": ["name", "count"] }
        }
        """;
        var table = StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish(english));

        Check.That(table.T("settings.files", "Docs", 1)).IsEqualTo("Docs: 1 file");
        Check.That(table.T("settings.files", "Docs", 2)).IsEqualTo("Docs: 2 files");
        Check.That(table.T("settings.files", "Docs", 1.0)).IsEqualTo("Docs: 1 file");

        // {1:0.0} renders every count with a visible decimal, so even 1 reads "1.0" (other).
        Check.That(table.T("settings.units", "Docs", 1)).IsEqualTo("Docs: 1.0 units");
        Check.That(table.T("settings.units", "Docs", 1.0)).IsEqualTo("Docs: 1.0 units");
    }

    [Fact]
    public void Plurals_FormatTheCountWithTheRenderedTextsProvider()
    {
        const string english = """
        {
          "overlays.decimal": { "text": { "one": "{0:0.0} unit", "other": "{0:0.0} units" }, "who": "user", "where": "overlays", "fit": "A44", "args": ["count"] }
        }
        """;
        const string german = """
        {
          "overlays.decimal": { "text": { "one": "{0:0.0} Einheit", "other": "{0:0.0} Einheiten" }, "src": "00000000" }
        }
        """;

        var germanTable = StringTable.Create(
            UiLanguage.German,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(english),
            StringTableContent.ParseGerman(german),
            ["overlays"]);
        Check.That(germanTable.T("overlays.decimal", 1.0)).IsEqualTo("1,0 Einheiten");
        Check.That(germanTable.T("overlays.decimal", 1)).IsEqualTo("1,0 Einheiten");

        var englishTable = StringTable.Create(
            UiLanguage.English,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(english));
        Check.That(englishTable.T("overlays.decimal", 1.0)).IsEqualTo("1.0 units");
    }

    [Fact]
    public void Plurals_WithoutANumericArgument_FallBackToOther()
    {
        var table = Build(selection: UiLanguage.English);
        Check.That(table.T("overlays.count", "many")).IsEqualTo("many items");
    }

    [Fact]
    public void TheG1Probe_EnglishTextOnDeDeKeepsEnglishNumbers()
    {
        // The review's probe: System on de-DE with no manifest surface renders English text, and
        // the number must stay English too ("Price 12.5", never "Price 12,5").
        var table = Build(selection: UiLanguage.System, culture: "de-DE", manifest: []);

        Check.That(table.Language).IsEqualTo(UiLanguage.German);
        Check.That(table.T("settings.price", 12.5)).IsEqualTo("Price 12.5");
        Check.That(table.FormatProviderFor("settings")).IsSameReferenceAs(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void GermanEffectiveTextOnDeDe_UsesGermanNumbers()
    {
        var table = Build(selection: UiLanguage.System, culture: "de-DE", manifest: ["settings"]);

        Check.That(table.T("settings.price", 12.5)).IsEqualTo("Preis 12,5");
        Check.That(((CultureInfo)table.FormatProviderFor("settings")).Name).IsEqualTo("de-DE");
    }

    [Fact]
    public void EnglishFallbackInsideAGermanSelection_UsesInvariantNumbers()
    {
        // overlays is not in the manifest, so it renders English on a German-effective instance.
        var table = Build(selection: UiLanguage.System, culture: "de-DE", manifest: ["settings"]);

        Check.That(table.T("overlays.count", 12.5)).IsEqualTo("12.5 items");
        Check.That(table.FormatProviderFor("overlays")).IsSameReferenceAs(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void FormatProviderFor_IsInvariantWhenTheSelectionIsEnglish()
    {
        var english = Build(selection: UiLanguage.English, culture: "de-DE", manifest: ["settings"]);
        Check.That(english.FormatProviderFor("settings")).IsSameReferenceAs(CultureInfo.InvariantCulture);
    }

    [Fact]
    public void FormatProviderFor_UnknownSurfaceId_WarnsAndIsInvariant()
    {
        var warnings = new List<string>();
        var table = Build(manifest: ["settings"], warnings: warnings);

        Check.That(table.FormatProviderFor("setings")).IsSameReferenceAs(CultureInfo.InvariantCulture);
        Check.That(warnings).HasElementThatMatches(w => w.Contains("setings", StringComparison.Ordinal));
    }

    [Fact]
    public void NamedLanguageLookup_IsGatedByTheManifest()
    {
        var warnings = new List<string>();
        // The German table is complete, but no surface is switched on yet: German shows English.
        var gated = Build(manifest: [], warnings: warnings);
        Check.That(gated.T(UiLanguage.German, "settings.alpha")).IsEqualTo("Alpha");
        Check.That(gated.T(UiLanguage.English, "settings.alpha")).IsEqualTo("Alpha");
        Check.That(warnings).IsEmpty();

        // A manifest surface may resolve German; a non-manifest one stays English.
        var german = Build(manifest: ["settings"]);
        Check.That(german.T(UiLanguage.German, "settings.alpha")).IsEqualTo("Alfa");
        Check.That(german.T(UiLanguage.German, "overlays.count", 3)).IsEqualTo("3 items");
    }

    [Fact]
    public void NamedLanguageLookup_OnAnIncompleteSurface_IsEnglish()
    {
        var table = Build(
            german: """{ "settings.beta": { "text": "Beta-de", "src": "00000000" } }""",
            manifest: ["settings"]);

        Check.That(table.T(UiLanguage.German, "settings.beta")).IsEqualTo("Beta");
        Check.That(table.T(UiLanguage.German, "settings.alpha")).IsEqualTo("Alpha");
    }

    [Fact]
    public void NamedLanguageLookup_FormatsWithTheResolvedTextsProvider()
    {
        var table = Build(selection: UiLanguage.English, culture: "de-DE", manifest: ["settings"]);

        // German text uses de-DE...
        Check.That(table.T(UiLanguage.German, "settings.price", 12.5)).IsEqualTo("Preis 12,5");
        // ...English text uses the invariant culture, whether asked for or fallen back to.
        Check.That(table.T(UiLanguage.English, "settings.price", 12.5)).IsEqualTo("Price 12.5");
        Check.That(table.T(UiLanguage.German, "overlays.count", 12.5)).IsEqualTo("12.5 items");
    }

    [Fact]
    public void NamedLanguageLookup_System_NamesTheInstanceLanguage()
    {
        var table = Build(manifest: ["settings"]);
        Check.That(table.T(UiLanguage.System, "settings.alpha")).IsEqualTo("Alfa");
    }

    [Fact]
    public void NamedLanguageLookup_RejectsAnUndefinedLanguage()
    {
        var table = Build(manifest: ["settings"]);
        Check.ThatCode(() => table.T((UiLanguage)99, "settings.alpha"))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void BrokenFormat_DoesNotThrow_AndWarns()
    {
        var warnings = new List<string>();
        var table = StringTable.Create(
            UiLanguage.English,
            CultureInfo.InvariantCulture,
            StringTableContent.ParseEnglish("""
            {
              "settings.broken": { "text": "Only {5}", "who": "user", "where": "settings", "fit": "row", "args": ["a"] }
            }
            """),
            warn: warnings.Add);

        Check.That(table.T("settings.broken", "one")).IsEqualTo("Only {5}");
        Check.That(warnings).HasElementThatMatches(w => w.Contains("Only {5}", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_DoesNotTouchTheProcessGlobal()
    {
        _ = Build(manifest: ["settings"]);
        Check.That(StringTable.Current).IsSameReferenceAs(StringTable.EmbeddedEnglish);
    }
}
