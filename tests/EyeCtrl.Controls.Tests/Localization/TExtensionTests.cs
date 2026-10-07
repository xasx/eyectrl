namespace EyeCtrl.Controls.Tests.Localization;

using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Markup;
using EyeCtrl.Controls.Localization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// The XAML markup extension: <c>{loc:T 'key'}</c> resolves through
/// <see cref="StringTable.Current"/> (and only through it — there is no injection path in XAML).
/// The class is in the "StringTable Tests (Controls)" exclusive collection because it activates
/// the process-global table; the assembly-wide restore hook puts embedded English back afterwards.
/// </summary>
[Collection("StringTable Tests (Controls)")]
public class TExtensionTests
{
    private const string FixtureEnglish = """
    {
      "overlays.sample": { "text": "Sample", "who": "user", "where": "overlays", "fit": "A44", "args": [] }
    }
    """;

    private const string FixtureGerman = """
    {
      "overlays.sample": { "text": "Beispiel", "src": "00000000" }
    }
    """;

    private static StringTable Table(UiLanguage selection) =>
        StringTable.Create(
            selection,
            CultureInfo.GetCultureInfo("de-DE"),
            StringTableContent.ParseEnglish(FixtureEnglish),
            StringTableContent.ParseGerman(FixtureGerman),
            ["overlays"]);

    private static void WithActive(StringTable table, Action action)
    {
        StringTable.Activate(table);
        try { action(); }
        finally { StringTable.ResetToEmbeddedEnglish(); }
    }

    [Fact]
    public void ProvideValue_ResolvesEnglishThroughTheActiveGlobal() =>
        WithActive(Table(UiLanguage.English), () =>
        {
            Check.That(new TExtension("overlays.sample").ProvideValue(null!)).IsEqualTo("Sample");
        });

    [Fact]
    public void ProvideValue_ResolvesGermanWhenGermanIsActive() =>
        WithActive(Table(UiLanguage.German), () =>
        {
            Check.That(new TExtension("overlays.sample").ProvideValue(null!)).IsEqualTo("Beispiel");
        });

    [Fact]
    public void Key_IsAssignableAsAProperty() =>
        WithActive(Table(UiLanguage.English), () =>
        {
            var extension = new TExtension { Key = "overlays.sample" };
            Check.That(extension.ProvideValue(null!)).IsEqualTo("Sample");
        });

    [StaFact]
    public void ARealXamlDocument_ResolvesTheExtension() =>
        WithActive(Table(UiLanguage.English), () =>
        {
            var block = (TextBlock)XamlReader.Parse("""
                <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                           xmlns:loc="clr-namespace:EyeCtrl.Controls.Localization;assembly=EyeCtrl.Controls"
                           Text="{loc:T 'overlays.sample'}" />
                """);

            Check.That(block.Text).IsEqualTo("Sample");
        });
}
