namespace EyeCtrl.App.Tests.Localization;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Wrong-language recovery (T04): with an unreadable language active, the recovery path must be
/// reachable and readable — and the whole restart flow must speak the TARGET language (F1): the
/// saved selection resolved against the machine culture, i.e. what the replacement will run.
///
/// <para>
/// The German table is INJECTED — German ships in T11 — so these tests activate a fixture table
/// and restore the global afterwards (the collection serializes them). The fixture English is the
/// REAL embedded table plus one <c>settings</c> key, and the fixture German carries a marker per
/// <c>language-recovery</c> key. Markers make the routing provable: an assertion can distinguish
/// "resolved the German value" from "fell back to English".
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class LanguageRecoveryTests
{
    private static readonly CultureInfo GermanCulture = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo EnUsCulture = CultureInfo.GetCultureInfo("en-US");

    private const string SettingsSampleEnglish = "English sample";
    private const string SettingsSampleGermanMarker = "de:settings.sample";

    /// <summary>The fixture German for the failure frame keeps the placeholder so the raw detail's
    /// interpolation is still provable while the FRAME itself is routed.</summary>
    private const string FrameGermanMarker = "de-frame {0} de-tail";

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
            germanRoot[key] = new JsonObject
            {
                ["text"] = key == "language-recovery.restart.failed.frame"
                    ? FrameGermanMarker
                    : "de:" + key
            };
        }
        germanRoot["settings.sample"] = new JsonObject { ["text"] = SettingsSampleGermanMarker };
        var german = StringTableContent.ParseGerman(germanRoot.ToJsonString());

        return (english, german);
    }

    private static void ActivateTable(UiLanguage selection, CultureInfo culture, bool manifestTheSurface)
    {
        var (english, german) = FixtureContent();
        StringTable.Activate(StringTable.Create(
            selection,
            culture,
            english,
            german,
            manifestTheSurface ? ["language-recovery"] : []));
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

    private static ComboBoxSpec LanguageRow(PageFixture page) =>
        new GeneralPageSource(page.Context).Build().Single().Rows
            .OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");

    /// <summary>
    /// Drives the real coordinator's failure path — the end-to-end frame, resolved from the
    /// settings object it holds — with a strategy that throws.
    /// </summary>
    private static string CoordinatorFailureFrame(EyeCtrlSettings settings)
    {
        var reported = new List<string>();
        var messenger = new WeakReferenceMessenger();
        _ = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => throw new InvalidOperationException("boom"),
            shutdown: () => { },
            reportInProgress: () => { },
            reportUnavailable: reported.Add,
            runInBackground: work => work(),
            postToUi: work => work(),
            supervisorInstalled: () => false,
            bringUp: new SupervisorBringUp(
                settings,
                enablePublishing: () => { },
                launchSupervisor: () => false,
                persistEnabled: _ => { },
                reconcileRunKey: () => { },
                tryReachSupervisor: () => false));

        messenger.Send(new RestartAppRequestedMessage());
        return reported.Single();
    }

    [Fact]
    public void AGermanTarget_OpensThePromptInGerman_WhileTheSurfaceIsManifested()
    {
        try
        {
            ActivateTable(UiLanguage.English, GermanCulture, manifestTheSurface: true);
            var page = BuildPage();

            LanguageRow(page).Set("german");

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
    public void EnglishTarget_WhileGermanRuns_RendersTheWholeRestartFlowInEnglish()
    {
        // The user cannot read German; they pick English and dwell Restart now. The prompt AND
        // every notice, reason and frame must be English (F1).
        try
        {
            ActivateTable(UiLanguage.German, GermanCulture, manifestTheSurface: true);
            var page = BuildPage();

            LanguageRow(page).Set("english");

            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.English);
            Check.That(page.ViewModel.ModalMessage).IsEqualTo("The new language takes effect after a restart.");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("Restart now");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("Cancel");

            Check.That(EyeCtrl.App.Views.RestartNotices.InProgressMessage(UiLanguage.English)).IsEqualTo(
                "Restarting EyeCtrl…\n"
                + "Waiting for the new EyeCtrl to start. If it cannot, this one keeps running.\n"
                + "Hiding this notice does not stop the restart.");
            Check.That(EyeCtrl.App.Views.RestartNotices.InProgressChoices(UiLanguage.English)[0].Label)
                .IsEqualTo("✕ Hide");
            Check.That(EyeCtrl.App.Views.RestartNotices.RefusedChoices(UiLanguage.English)[0].Label)
                .IsEqualTo("✓ OK");

            Check.That(SelfRestartStrategy.Describe(RestartOutcome.NoExePath, UiLanguage.English))
                .IsEqualTo("Restart failed: EyeCtrl could not determine its own program path. EyeCtrl is still running.");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.LaunchFailed, UiLanguage.English))
                .IsEqualTo("Restart failed: the new EyeCtrl could not be started. EyeCtrl is still running.");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.ChildExitedEarly, UiLanguage.English))
                .IsEqualTo("Restart failed: the new EyeCtrl closed immediately. EyeCtrl is still running.");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.ChildNotReady, UiLanguage.English))
                .IsEqualTo("Restart failed: the new EyeCtrl did not start in time. EyeCtrl is still running.");

            Check.That(CoordinatorFailureFrame(page.Settings))
                .IsEqualTo("Restart failed. EyeCtrl is still running. Technical details: boom");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void GermanTarget_WhileEnglishRuns_RendersTheWholeRestartFlowInGerman()
    {
        // The mirror: English runs, the user picks German. The prompt and everything after it
        // routes German through the manifest (F1).
        try
        {
            ActivateTable(UiLanguage.English, GermanCulture, manifestTheSurface: true);
            var page = BuildPage();
            page.Settings.General.UiLanguage = UiLanguage.English;   // running = saved, not pending yet

            LanguageRow(page).Set("german");

            Check.That(page.ViewModel.ModalMessage).IsEqualTo("de:language-recovery.prompt.message");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("de:language-recovery.prompt.restart");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("de:language-recovery.prompt.cancel");

            Check.That(EyeCtrl.App.Views.RestartNotices.InProgressMessage(UiLanguage.German))
                .IsEqualTo("de:language-recovery.restart.in_progress.message");
            Check.That(EyeCtrl.App.Views.RestartNotices.InProgressChoices(UiLanguage.German)[0].Label)
                .IsEqualTo("de:language-recovery.restart.in_progress.hide");
            Check.That(EyeCtrl.App.Views.RestartNotices.RefusedChoices(UiLanguage.German)[0].Label)
                .IsEqualTo("de:language-recovery.restart.refused.ok");

            Check.That(SelfRestartStrategy.Describe(RestartOutcome.NoExePath, UiLanguage.German))
                .IsEqualTo("de:language-recovery.restart.failed.no_exe_path");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.LaunchFailed, UiLanguage.German))
                .IsEqualTo("de:language-recovery.restart.failed.launch_failed");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.ChildExitedEarly, UiLanguage.German))
                .IsEqualTo("de:language-recovery.restart.failed.child_exited_early");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.ChildNotReady, UiLanguage.German))
                .IsEqualTo("de:language-recovery.restart.failed.child_not_ready");

            Check.That(CoordinatorFailureFrame(page.Settings)).IsEqualTo("de-frame boom de-tail");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void SystemTarget_OnEnUs_OpensItsPromptInEnglish()
    {
        // F2: "System" targets whatever it resolves to on the machine. German was set explicitly
        // on an en-US machine; picking System returns to English, and the PROMPT must be English
        // even though the running table's language is German.
        try
        {
            ActivateTable(UiLanguage.German, EnUsCulture, manifestTheSurface: true);
            var page = BuildPage();
            page.Settings.General.UiLanguage = UiLanguage.German;

            LanguageRow(page).Set("system");

            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.System);
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
    public void SelectionAndResolvedLanguageDiffer_ChoosingAnEquivalentLanguage_ShowsNothing()
    {
        // F3: the user's own machine — System + de-DE, so the SELECTION is System while the
        // LANGUAGE is German. Picking Deutsch (or re-picking System) resolves to the language
        // already running: no prompt, no note. Comparing the selection instead of the resolved
        // language would prompt for every pick.
        try
        {
            ActivateTable(UiLanguage.System, GermanCulture, manifestTheSurface: true);
            var page = BuildPage();
            Check.That(StringTable.Current.Selection).IsEqualTo(UiLanguage.System);
            Check.That(StringTable.Current.Language).IsEqualTo(UiLanguage.German);

            LanguageRow(page).Set("german");
            Check.That(page.ViewModel.IsModalVisible).IsFalse();
            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.German);

            LanguageRow(page).Set("system");
            Check.That(page.ViewModel.IsModalVisible).IsFalse();

            Check.That(new GeneralPageSource(page.Context).Build().Single().Rows
                    .OfType<LabelSpec>().Any(l => l.Text().Contains("Takes effect after restart")))
                .IsFalse();
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
            ActivateTable(UiLanguage.English, GermanCulture, manifestTheSurface: false);

            Check.That(StringTable.Current.T(UiLanguage.German, "language-recovery.prompt.restart"))
                .IsEqualTo("Restart now");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void WithGermanRunning_TheRow_TheEndonymsAndTheTrayItem_AreReadable()
    {
        // The reading-free entry points: endonyms are language-independent by construction, and
        // the row/tray labels are keyed. (The notices are covered by the target-language tests.)
        try
        {
            ActivateTable(UiLanguage.German, GermanCulture, manifestTheSurface: true);
            var page = BuildPage();
            var row = LanguageRow(page);

            Check.That(row.Display).ContainsExactly("System", "Deutsch", "English");
            Check.That(row.Label).IsEqualTo("de:language-recovery.row.label");
            Check.That(StringTable.Current.T("language-recovery.tray.item"))
                .IsEqualTo("de:language-recovery.tray.item");

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
            ActivateTable(UiLanguage.German, GermanCulture, manifestTheSurface: true);

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
