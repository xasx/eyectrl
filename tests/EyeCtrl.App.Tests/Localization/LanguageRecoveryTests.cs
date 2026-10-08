namespace EyeCtrl.App.Tests.Localization;

using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Xunit;

/// <summary>
/// Wrong-language recovery (T04): with an unreadable language active, the recovery path must be
/// reachable and readable. The German table is INJECTED — German ships in T11 — so these tests
/// activate a fixture table and restore the global afterwards (the collection serializes them).
///
/// <para>
/// The fixture English is the REAL embedded table plus one <c>settings</c> key, and the fixture
/// German carries a marker per <c>language-recovery</c> key. Markers make the routing provable:
/// an assertion can distinguish "resolved the German value" from "fell back to English".
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class LanguageRecoveryTests
{
    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");

    private const string SettingsSampleEnglish = "English sample";
    private const string SettingsSampleGermanMarker = "de:settings.sample";

    private static (StringTableContent English, StringTableContent German) FixtureContent()
    {
        using var document = LocalizationTestData.EnglishDocument();
        var englishRoot = JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        englishRoot["settings.sample"] = new JsonObject
        {
            ["text"] = SettingsSampleEnglish,
            ["who"] = "user",
            ["where"] = "settings",
            ["fit"] = "row",
            ["args"] = new JsonArray()
        };
        var english = StringTableContent.ParseEnglish(englishRoot.ToJsonString());

        var germanRoot = new JsonObject();
        foreach (var (key, node) in englishRoot)
        {
            if (key.StartsWith('_')) continue;
            if (node!["where"]!.GetValue<string>() != "language-recovery") continue;
            germanRoot[key] = new JsonObject { ["text"] = "de:" + key };
        }
        germanRoot["settings.sample"] = new JsonObject { ["text"] = SettingsSampleGermanMarker };
        var german = StringTableContent.ParseGerman(germanRoot.ToJsonString());

        return (english, german);
    }

    private static StringTable ActivateTable(UiLanguage selection, bool manifestTheSurface)
    {
        var (english, german) = FixtureContent();
        var table = StringTable.Create(
            selection,
            GermanCulture,
            english,
            german,
            manifestTheSurface ? ["language-recovery"] : []);
        StringTable.Activate(table);
        return table;
    }

    private sealed record PageFixture(
        EyeCtrlSettings Settings,
        SettingsWindowViewModel ViewModel,
        SettingsPageContext Context);

    private static PageFixture BuildPage()
    {
        var fixture = LanguageTestSupport.Build();
        return new PageFixture(fixture.Settings, fixture.ViewModel, fixture.Context);
    }

    [Fact]
    public void AGermanTarget_OpensThePromptInGerman_WhileTheSurfaceIsManifested()
    {
        try
        {
            ActivateTable(UiLanguage.English, manifestTheSurface: true);
            var page = BuildPage();

            var row = new GeneralPageSource(page.Context).Build().Single().Rows
                .OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");
            row.Set("german");

            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(page.ViewModel.ModalMessage).IsEqualTo("de:language-recovery.prompt.message");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("de:language-recovery.prompt.restart");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("de:language-recovery.prompt.cancel");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void AnEnglishTarget_OpensThePromptInEnglish_EvenWhileGermanRuns()
    {
        // The prompt's language follows the TARGET, not the running one. German running (System
        // on de-DE), choosing English: the prompt is English.
        try
        {
            ActivateTable(UiLanguage.German, manifestTheSurface: true);
            var page = BuildPage();
            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.System);

            var row = new GeneralPageSource(page.Context).Build().Single().Rows
                .OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");
            row.Set("english");

            Check.That(page.ViewModel.ModalMessage).IsEqualTo("The new language takes effect after a restart.");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("Restart now");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("Cancel");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void BeforeTheSurfaceIsManifested_AGermanTarget_StillShowsEnglish()
    {
        // The named-language lookup is gated by the manifest: Step 0 shows no German.
        try
        {
            ActivateTable(UiLanguage.English, manifestTheSurface: false);

            Check.That(StringTable.Current.T(UiLanguage.German, "language-recovery.prompt.restart"))
                .IsEqualTo("Restart now");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void WithGermanRunning_TheTrayItem_TheEndonyms_TheNoticesAndTheReasons_AreReadable()
    {
        try
        {
            ActivateTable(UiLanguage.German, manifestTheSurface: true);
            var page = BuildPage();
            var rows = new GeneralPageSource(page.Context).Build().Single().Rows;
            var row = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");

            // Endonyms are language-independent by construction — reachable whatever runs.
            Check.That(row.Display).ContainsExactly("System", "Deutsch", "English");
            Check.That(row.Label).IsEqualTo("de:language-recovery.row.label");
            Check.That(StringTable.Current.T("language-recovery.tray.item"))
                .IsEqualTo("de:language-recovery.tray.item");

            // The whole recovery flow switches language at once (D5): notices and failure reasons.
            Check.That(EyeCtrl.App.Views.RestartNotices.InProgressMessage())
                .IsEqualTo("de:language-recovery.restart.in_progress.message");
            Check.That(EyeCtrl.App.Views.RestartNotices.RefusedChoices()[0].Label)
                .IsEqualTo("de:language-recovery.restart.refused.ok");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.ChildNotReady))
                .IsEqualTo("de:language-recovery.restart.failed.child_not_ready");

            // A saved English choice while German runs = pending, and the note is readable.
            page.Settings.General.UiLanguage = UiLanguage.English;
            Check.That(new GeneralPageSource(page.Context).Build().Single().Rows
                    .OfType<LabelSpec>().Any(l => l.Text() == "de:language-recovery.note.pending"))
                .IsTrue();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void ASurfaceOutsideTheManifest_StaysEnglish_NoHalfGermanScreen()
    {
        try
        {
            ActivateTable(UiLanguage.German, manifestTheSurface: true);

            // The settings surface is NOT manifested: its German value exists in the fixture and
            // must not render — whole-surface gating.
            Check.That(StringTable.Current.T("settings.sample")).IsEqualTo(SettingsSampleEnglish);
            Check.That(StringTable.Current.T("language-recovery.row.label"))
                .IsEqualTo("de:language-recovery.row.label");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }
}
