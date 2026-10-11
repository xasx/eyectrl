namespace EyeCtrl.App.Tests.Localization;

using System;
using System.Collections.Generic;
using System.Linq;
using EyeCtrl.App.Tests.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Key coverage (synthesis Decision 6, "construction coverage"): every key referenced in C#
/// <c>T("…")</c>, in XAML through the markup extension, and in key maps exists in
/// <c>strings.json</c>. A typo must fail here rather than reach a screen as a raw key — the failure
/// mode is that the fallback chain shows the key itself, which only a log would otherwise notice.
/// </summary>
/// <remarks>
/// The shipped English table is empty while Step 0 runs, so the repo scan is legitimately quiet;
/// the extractor is therefore pinned against fixture texts below, and the repo scan grows teeth as
/// T06–T08 add the keys.
/// </remarks>
public class StringKeyCoverageTests
{
    private static IReadOnlyCollection<string> EnglishKeys() =>
        LocalizationTestData.EnglishContent().Texts.Keys.ToList();

    private static List<string> MissingIn(IEnumerable<(string Relative, string Text)> files, IReadOnlyCollection<string> known)
    {
        var violations = new List<string>();
        foreach (var (relative, text) in files)
        {
            foreach (var reference in StringKeyReferences.Extract(text))
            {
                if (!known.Contains(reference.Key))
                {
                    violations.Add(
                        $"{relative}: {reference.Kind} references '{reference.Key}', which strings.json does not define");
                }
            }
        }
        return violations;
    }

    [Fact]
    public void EveryReferencedKey_InSource_ExistsInTheEnglishTable()
    {
        var violations = MissingIn(LocalizationTestData.SourceFiles(), EnglishKeys());

        Check.WithCustomMessage(
                "A key is referenced in src/ that strings.json does not define, so it would render as "
                + "the raw key. Add the key to src/EyeCtrl.Core/Localization/strings.json.\n  "
                + string.Join("\n  ", violations))
            .That(violations).IsEmpty();
    }

    [Fact]
    public void TheGate_CatchesATypoInACallSite()
    {
        var violations = MissingIn(
            [("src/Fixture.cs", """var text = T("settings.titel");""")],
            ["settings.title"]);

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("settings.titel");
    }

    [Fact]
    public void TheGate_AcceptsAKnownKey()
    {
        var violations = MissingIn(
            [("src/Fixture.cs", """var text = T("settings.title");""")],
            ["settings.title"]);

        Check.That(violations).IsEmpty();
    }

    [Fact]
    public void TheGate_SeesNamedLanguageCallsAndXamlUsages()
    {
        var violations = MissingIn(
            [
                ("src/Fixture.cs", """var text = StringTable.Current.T(UiLanguage.German, "supervisor.titel");"""),
                ("src/Fixture.xaml", """<TextBlock Text="{loc:T 'overlays.missing'}" />""")
            ],
            ["supervisor.title", "overlays.present"]);

        Check.That(violations).HasSize(2);
        Check.That(violations[0]).Contains("supervisor.titel");
        Check.That(violations[1]).Contains("overlays.missing");
    }

    [Fact]
    public void TheGate_CatchesATypoInAKeyMapLiteral()
    {
        var violations = MissingIn(
            [("src/Fixture.cs", """map["back"] = "overlays.ring.bak";""")],
            ["overlays.ring.back"]);

        Check.That(violations).HasSize(1);
        Check.That(violations[0]).Contains("overlays.ring.bak");
    }

    [Fact]
    public void TheGate_IgnoresFileNamesAndNonSurfaceLiterals()
    {
        var violations = MissingIn(
            [
                ("src/Fixture.cs", """var a = "settings.json"; var b = "user32.dll"; var c = "gaze-recording.csv";"""),
                ("src/Fixture.cs", """var d = "not.a.key";""")
            ],
            []);

        Check.That(violations).IsEmpty();
    }
}
