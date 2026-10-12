namespace EyeCtrl.App.Tests.Localization;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T11: wrong-language recovery with the REAL German — the shipped manifest and
/// <c>strings.de.json</c>, loaded through the production loader, not T10's marker fixture. The
/// ticket's Step-1 promise is that the way back out of German works from the first German string
/// on: the tray item and the Settings row are readable, and the restart prompt and every notice
/// speak the TARGET language.
///
/// <para>
/// Crises assert German (or English) <b>literals</b>, never <c>T(key)</c> on both sides. The table
/// is activated globally for the Settings page, so this class lives in the exclusive
/// "StringTable Tests (App)" collection and restores English after each test.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class ShippedLanguageRecoveryTests
{
    /// <summary>
    /// Activates the shipped table as this de-DE machine's App loads it (System + de-DE). Internal
    /// so T12's Settings gates activate the same table through the same check.
    /// </summary>
    internal static StringTable ActivateShippedGerman()
    {
        var warnings = new List<string>();
        var table = ShippedGerman.Load(warnings.Add);
        Check.WithCustomMessage("The shipped German warned while loading:\n  " + string.Join("\n  ", warnings))
            .That(warnings).IsEmpty();
        StringTable.Activate(table);
        return table;
    }

    /// <summary>The language row of a freshly built General page.</summary>
    private static ComboBoxSpec LanguageRow(LanguageTestSupport.Fixture page) => page.LanguageRow();

    [Fact]
    public void TheLanguageRow_InGerman_IsReadable_AndTheEndonymsGiveTheWayBack()
    {
        try
        {
            ActivateShippedGerman();
            var page = LanguageTestSupport.Build();
            var row = LanguageRow(page);

            // The label is bilingual by design (D9); the description is German.
            Check.That(row.Label).IsEqualTo("Sprache / Language");
            Check.That(row.Description).IsEqualTo("Legt fest, in welcher Sprache EyeCtrl seine eigenen Fenster anzeigt.");

            // The endonyms are language-independent, so "English" is reachable without reading German.
            Check.That(row.Display).ContainsExactly("System", "Deutsch", "English");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void PickingEnglish_FromTheGermanUi_ShowsThePromptInTheTargetLanguage()
    {
        try
        {
            ActivateShippedGerman();
            var page = LanguageTestSupport.Build();

            LanguageRow(page).Set("english");

            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.English);
            Check.That(page.ViewModel.IsModalVisible).IsTrue();
            Check.That(page.ViewModel.ModalMessage).IsEqualTo("The new language takes effect after a restart.");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("Restart now");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("Cancel");

            // The pending note sits beside the row, bilingual, until the restart happens.
            Check.That(page.Rows().OfType<LabelSpec>()
                    .Any(l => l.Text() == "Wird nach Neustart wirksam / Takes effect after restart"))
                .IsTrue();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void PickingGerman_WhileEnglishRuns_ShowsTheWholeRestartFlowInGerman()
    {
        // The inverse direction with the real German: an English-running table still resolves the
        // German target through the shipped manifest, and every notice after the prompt is German.
        var warnings = new List<string>();
        StringTable.Activate(StringTable.Load(
            UiLanguage.English, ShippedGerman.GermanCulture, System.AppContext.BaseDirectory, warnings.Add));
        try
        {
            Check.That(warnings).IsEmpty();
            var page = LanguageTestSupport.Build();

            LanguageRow(page).Set("german");

            Check.That(page.Settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
            Check.That(page.ViewModel.ModalMessage).IsEqualTo("Die neue Sprache gilt nach einem Neustart.");
            Check.That(page.ViewModel.ModalChoices[0].Label).IsEqualTo("Neu starten");
            Check.That(page.ViewModel.ModalChoices[1].Label).IsEqualTo("Abbrechen");

            Check.That(RestartNotices.InProgressMessage(UiLanguage.German)).IsEqualTo(
                "EyeCtrl wird neu gestartet.\n"
                + "Das neue EyeCtrl startet gerade; klappt das nicht, läuft dieses weiter.\n"
                + "Ausblenden hält den Neustart nicht an.");
            Check.That(RestartNotices.InProgressChoices(UiLanguage.German)[0].Label).IsEqualTo("✕ Ausblenden");
            Check.That(RestartNotices.RefusedChoices(UiLanguage.German)[0].Label).IsEqualTo("✓ OK");

            Check.That(SelfRestartStrategy.Describe(RestartOutcome.LaunchFailed, UiLanguage.German))
                .IsEqualTo("Neustart fehlgeschlagen: Das neue EyeCtrl konnte nicht gestartet werden. "
                           + "EyeCtrl läuft weiter.");
            Check.That(CoordinatorFailureFrame(page.Settings))
                .IsEqualTo("Neustart fehlgeschlagen. EyeCtrl läuft weiter. Technische Details (Englisch): boom");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void TheGermanRestartFlow_ReadsTheRealGermanNotices()
    {
        // Running German, target German: the notices resolve the shipped German rather than the
        // fixture marker the T04 tests use.
        try
        {
            ActivateShippedGerman();
            Check.That(RestartNotices.InProgressMessage(UiLanguage.German)).IsEqualTo(
                "EyeCtrl wird neu gestartet.\n"
                + "Das neue EyeCtrl startet gerade; klappt das nicht, läuft dieses weiter.\n"
                + "Ausblenden hält den Neustart nicht an.");
            Check.That(RestartNotices.InProgressChoices(UiLanguage.German)[0].Label).IsEqualTo("✕ Ausblenden");
            Check.That(RestartNotices.RefusedChoices(UiLanguage.German)[0].Label).IsEqualTo("✓ OK");
            Check.That(SelfRestartStrategy.Describe(RestartOutcome.NoExePath, UiLanguage.German))
                .IsEqualTo("Neustart fehlgeschlagen: EyeCtrl konnte seinen eigenen Programmpfad nicht ermitteln. "
                           + "EyeCtrl läuft weiter.");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [Fact]
    public void TheStartupErrorBox_InGerman_ReadsTheGermanLiterals()
    {
        try
        {
            ActivateShippedGerman();
            var text = StartupErrorText.Resolve("Zugriff verweigert. (0x80070005)");

            Check.That(text.Title).IsEqualTo("EyeCtrl konnte nicht starten");
            Check.That(text.Message).IsEqualTo(
                "Beim Start ist ein Fehler aufgetreten.\n"
                + "Technische Details (Englisch): Zugriff verweigert. (0x80070005)");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void TheTrayItem_InGerman_CarriesTheBilingualLabel_AndDeepLinksToTheRow()
    {
        try
        {
            ActivateShippedGerman();
            var messenger = new WeakReferenceMessenger();
            SettingsRequestedMessage? received = null;
            messenger.Register<SettingsRequestedMessage>(this, (_, m) => received = m);

            var dispatcher = Dispatcher.CurrentDispatcher;
            var item = TrayIconManager.CreateLanguageMenuItem(dispatcher, messenger);

            Check.That(item.Text).IsEqualTo("Sprache / Language");

            item.PerformClick();
            // The production handler marshals through the dispatcher, as every tray item does; a
            // synchronous Invoke at Normal priority runs the queued send first.
            dispatcher.Invoke(() => { }, DispatcherPriority.Normal);

            Check.That(received).IsNotNull();
            Check.That(received!.Target).IsEqualTo(SettingsTarget.GeneralPage1);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// Drives the real coordinator's failure path — the end-to-end frame, resolved from the
    /// settings object it holds — with a strategy that throws (the T04 fixture's shape, on the
    /// shipped table).
    /// </summary>
    private static string CoordinatorFailureFrame(EyeCtrlSettings settings)
    {
        var reported = new List<string>();
        var messenger = new WeakReferenceMessenger();
        _ = new AppRestartCoordinator(
            messenger, settings,
            tryReachSupervisor: () => false,
            selfRestart: () => throw new System.InvalidOperationException("boom"),
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
}
