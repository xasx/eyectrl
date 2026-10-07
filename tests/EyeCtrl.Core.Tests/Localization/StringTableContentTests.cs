namespace EyeCtrl.Core.Tests.Localization;

using System;
using System.Collections.Generic;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// Parsing is the never-fail layer: whatever the loose files contain, the app must start. A
/// malformed file or entry is warned about and skipped; nothing throws.
/// </summary>
public class StringTableContentTests
{
    [Fact]
    public void EmptyObject_ParsesToAnEmptyTable()
    {
        var content = StringTableContent.ParseEnglish("{}");
        Check.That(content.Count).IsEqualTo(0);
        Check.That(content.ContainsKey("settings.alpha")).IsFalse();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("")]
    public void MalformedRoot_IsEmptyWithAWarning(string json)
    {
        var warnings = new List<string>();
        var content = StringTableContent.ParseEnglish(json, warnings.Add);

        Check.That(content.Count).IsEqualTo(0);
        Check.That(warnings).Not.IsEmpty();
    }

    [Fact]
    public void MalformedEntries_AreSkipped_ValidOnesSurvive()
    {
        var warnings = new List<string>();
        var content = StringTableContent.ParseEnglish("""
        {
          "settings.good": { "text": "Good", "who": "user", "where": "settings", "fit": "row", "args": [] },
          "settings.noText": { "who": "user", "where": "settings", "fit": "row", "args": [] },
          "settings.noWhere": { "text": "Nope", "who": "user", "fit": "row", "args": [] },
          "settings.notAnObject": 5,
          "settings.halfPlural": { "text": { "one": "Only one" }, "who": "user", "where": "settings", "fit": "row", "args": [] }
        }
        """, warnings.Add);

        Check.That(content.Count).IsEqualTo(1);
        Check.That(content.ContainsKey("settings.good")).IsTrue();
        Check.That(warnings).HasSize(4);
    }

    [Fact]
    public void GermanEntries_NeedOnlyText()
    {
        var warnings = new List<string>();
        var content = StringTableContent.ParseGerman("""
        {
          "settings.alpha": { "text": "Alfa", "src": "00000000" },
          "settings.plural": { "text": { "one": "Eins", "other": "Mehrere" } }
        }
        """, warnings.Add);

        Check.That(content.Count).IsEqualTo(2);
        Check.That(warnings).IsEmpty();
    }

    [Fact]
    public void BlankText_IsNotAValue()
    {
        var content = StringTableContent.ParseGerman("""{ "settings.blank": { "text": "   " } }""");
        Check.That(content.Count).IsEqualTo(0);
    }
}
