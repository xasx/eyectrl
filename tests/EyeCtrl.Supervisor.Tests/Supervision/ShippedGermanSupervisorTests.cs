namespace EyeCtrl.Supervisor.Tests.Supervision;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T11: the Supervisor follows the App's own language switch — with the REAL German, the shipped
/// manifest and <c>strings.de.json</c> laid out beside the resolved App exe — and without a
/// Supervisor restart. <see cref="SupervisorLanguageResolver"/> resolves per shown surface, so
/// these tests write the settings file the App's language row writes and re-resolve the SAME
/// resolver; the FallenBack dialog and the setup window are then built from that table and must
/// read their German literals. The German is asserted as literals, per the crisis-surface
/// convention.
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
public class ShippedGermanSupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eyectrl-t11-sup-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _settingsPath;
    private readonly string _appExePath;
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public ShippedGermanSupervisorTests()
    {
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
        _settingsPath = Path.Combine(_root, "settings.json");
        _appExePath = Path.Combine(_install, "EyeCtrl.App.exe");
        File.WriteAllText(_appExePath, string.Empty);

        // The real files the App ships, beside the resolved App exe (the installer's layout).
        File.Copy(GermanDraft.GermanPath, Path.Combine(_install, StringTable.GermanFileName));
        File.Copy(
            Path.Combine(GermanDraft.RepoRoot(), "src", "EyeCtrl.App", "Localization", StringTable.ManifestFileName),
            Path.Combine(_install, StringTable.ManifestFileName));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private SupervisorLanguageResolver Resolver() =>
        new(_settingsPath, appExePath: _appExePath, writerMutexName: _writerMutexName);

    private void WriteLanguage(string token) =>
        File.WriteAllText(_settingsPath, "{\"general\":{\"uiLanguage\":\"" + token + "\"}}");

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>
    /// The Supervisor's own surfaces flip with the App, on one resolver instance: English first,
    /// the App saves German, the same resolver now resolves German — and back. Nothing here
    /// restarts anything.
    /// </summary>
    [Fact]
    public void TheSupervisorSurfaces_FollowTheAppsOwnSwitch_WithoutARestart()
    {
        WriteLanguage("english");
        var resolver = Resolver();

        var english = resolver.ResolveTable();
        Check.That(english.Language).IsEqualTo(UiLanguage.English);
        Check.That(SupervisorStateText.TrayPrefix(english)).IsEqualTo("EyeCtrl Supervisor: ");
        Check.That(SupervisorStateText.Describe(english, SupervisorState.Supervising)).IsEqualTo("watching EyeCtrl");
        Check.That(SupervisorTrayTexts.For(english).Settings).IsEqualTo("EyeCtrl Supervisor settings");
        Check.That(SupervisorTrayTexts.For(english).Exit).IsEqualTo("Exit supervisor");

        // The App's own language row saved German; the Supervisor was not restarted.
        WriteLanguage("german");
        var german = resolver.ResolveTable();
        Check.That(german.Language).IsEqualTo(UiLanguage.German);
        Check.That(SupervisorStateText.TrayPrefix(german)).IsEqualTo("EyeCtrl Supervisor: ");
        Check.That(SupervisorStateText.Describe(german, SupervisorState.Supervising)).IsEqualTo("überwacht EyeCtrl");
        Check.That(SupervisorStateText.Describe(german, SupervisorState.FallenBack))
            .IsEqualTo("EyeCtrl gestoppt – Ersatzprogramm läuft");
        Check.That(SupervisorTrayTexts.For(german).Settings).IsEqualTo("EyeCtrl Supervisor – Einstellungen");
        Check.That(SupervisorTrayTexts.For(german).LaunchNow).IsEqualTo("EyeCtrl jetzt starten");
        Check.That(SupervisorTrayTexts.For(german).Exit).IsEqualTo("Supervisor beenden");

        // And back, still without a restart.
        WriteLanguage("english");
        Check.That(resolver.ResolveTable().Language).IsEqualTo(UiLanguage.English);
    }

    /// <summary>
    /// The shipped DEFAULT: <c>uiLanguage</c> is <c>system</c> (or absent), and German appears only
    /// because the machine's UI culture is German. Every other test writes "german" or "english",
    /// so the culture the table resolves <c>system</c> against was unpinned (holistic review F2,
    /// mutant M3: <c>CurrentUICulture</c> to <c>InvariantCulture</c> kept Supervisor.Tests green).
    /// </summary>
    [Theory]
    [InlineData("de-DE", UiLanguage.German, "überwacht EyeCtrl")]
    [InlineData("de-AT", UiLanguage.German, "überwacht EyeCtrl")]
    [InlineData("en-US", UiLanguage.English, "watching EyeCtrl")]
    [InlineData("fr-FR", UiLanguage.English, "watching EyeCtrl")]
    public void TheShippedDefault_System_FollowsTheInjectedUiCulture(
        string culture, UiLanguage expected, string supervising)
    {
        WriteLanguage("system");
        var resolver = new SupervisorLanguageResolver(
            _settingsPath, appExePath: _appExePath, writerMutexName: _writerMutexName,
            culture: CultureInfo.GetCultureInfo(culture));

        var table = resolver.ResolveTable();

        Check.That(table.Language).IsEqualTo(expected);
        Check.That(SupervisorStateText.Describe(table, SupervisorState.Supervising)).IsEqualTo(supervising);
    }

    /// <summary>
    /// The same default through the seam the Supervisor actually uses: no injected culture, so the
    /// resolver reads the thread's UI culture each time it resolves. A settings file with no
    /// <c>uiLanguage</c> at all is the fresh-install shape and resolves the same way.
    /// </summary>
    [Fact]
    public void TheShippedDefault_WithNoInjectedCulture_ReadsTheCurrentUiCulture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var resolver = Resolver();

            File.WriteAllText(_settingsPath, "{}");
            var fresh = resolver.ResolveTable();
            Check.That(fresh.Language).IsEqualTo(UiLanguage.German);
            Check.That(SupervisorStateText.Describe(fresh, SupervisorState.Supervising)).IsEqualTo("überwacht EyeCtrl");

            WriteLanguage("system");
            Check.That(resolver.ResolveTable().Language).IsEqualTo(UiLanguage.German);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var english = resolver.ResolveTable();
            Check.That(english.Language).IsEqualTo(UiLanguage.English);
            Check.That(SupervisorStateText.Describe(english, SupervisorState.Supervising)).IsEqualTo("watching EyeCtrl");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    /// <summary>The FallenBack dialog, built from the resolver's shipped-German table, reads its German literals.</summary>
    [Fact]
    public void TheFallenBackDialog_ShowsTheGermanLiterals()
    {
        WriteLanguage("german");
        var german = Resolver().ResolveTable();

        RunSta(() =>
        {
            var dialog = new FallenBackDialog(german);
            try
            {
                Check.That(dialog.HeadlineText.Text).IsEqualTo("EyeCtrl läuft nicht mehr");
                Check.That(dialog.BodyText.Text).IsEqualTo(
                    "Ein Ersatzprogramm zur Blicksteuerung läuft jetzt. Damit (oder mit der Maus) eine der "
                    + "Möglichkeiten unten wählen.");

                var buttons = dialog.ButtonHost.Children.OfType<Border>().ToList();
                Check.That(buttons).HasSize(2);
                Check.That(TextsUnder(buttons[0])).Contains("EyeCtrl erneut starten (einmal)");
                Check.That(TextsUnder(buttons[1])).Contains("Schließen (Ersatzprogramm behalten)");

                // A3: the auto-retry countdown line — the German carries no ellipsis (T10 copy rules),
                // and echoes the Retry button ("EyeCtrl erneut starten (einmal)"). NBSP before the
                // unit, so the seconds and the "s" never wrap apart.
                dialog.ShowAutoRetryCountdown();
                Check.That(dialog.CountdownText.Text).IsEqualTo("EyeCtrl startet in 60\u00A0s erneut");
                // The seconds are a format argument, not baked in (fixup-02 addendum).
                Check.That(german.T("supervisor.fallback.countdown", 5)).IsEqualTo("EyeCtrl startet in 5\u00A0s erneut");
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>The setup window, built from the resolver's shipped-German table, reads its German literals.</summary>
    [Fact]
    public void TheSetupWindow_ShowsTheGermanLiterals()
    {
        WriteLanguage("german");
        var german = Resolver().ResolveTable();

        RunSta(() =>
        {
            var language = new SettingDelivery<UiLanguage>(
                "UiLanguage",
                (value, restart) => SettingRequest.SetUiLanguage(value, restart),
                _ => new System.Threading.Tasks.TaskCompletionSource<AppRequestResult>().Task,
                work => work(),
                readDisk: () => UiLanguage.System,
                runInBackground: work => work(),
                utcNow: () => DateTime.UtcNow,
                confirmSaveByReRead: true);
            var startOnBoot = new SettingDelivery<bool>(
                "StartOnBoot", (value, _) => SettingRequest.SetStartOnBoot(value),
                _ => new System.Threading.Tasks.TaskCompletionSource<AppRequestResult>().Task,
                work => work());
            var window = new SupervisorSetupWindow(
                new EyeCtrlSettings(),
                new SettingsService(Path.Combine(_root, "settings.json"), SettingsWriter.Supervisor, _writerMutexName),
                _ => { },
                () => { },
                new StartOnBootSetupRow(startOnBoot, () => false),
                new LanguageSetupRow(language, () => UiLanguage.System, CultureInfo.GetCultureInfo("de-DE")),
                german,
                SupervisorStateText.Describe(german, SupervisorState.IdleWatch));
            try
            {
                Check.That(window.Title).IsEqualTo("EyeCtrl Supervisor – Einrichtung");
                Check.That(window.SubtitleText.Text).IsEqualTo(
                    "Hält EyeCtrl am Laufen und weicht bei einem Absturz auf ein anderes Programm zur "
                    + "Blicksteuerung aus.");
                Check.That(window.AdvancedExpander.Header).IsEqualTo("Erweitert");
                Check.That(window.DiagnosticsHeadingText.Text).IsEqualTo("Diagnose");
                Check.That(TextsUnder(window.ToggleHost)).Contains("Überwachung einschalten");
                Check.That(TextsUnder(window.ToggleHost)).Contains("EyeCtrl bei der Anmeldung starten");
                Check.That(TextsUnder(window.DiagnosticsHost)).Contains("EyeCtrl jetzt starten");
                Check.That(TextsUnder(window.DiagnosticsHost)).Contains("Speichern");
                // The row label is bilingual by design (D9); the note under it is not shown at rest.
                Check.That(LanguageSetupRow.Label(german)).IsEqualTo("Sprache / Language");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Every non-empty TextBlock text under <paramref name="node"/> (the dialog harness's helper).</summary>
    private static List<string> TextsUnder(DependencyObject node)
    {
        var found = new List<string>();

        void Walk(DependencyObject current)
        {
            if (current is TextBlock text && !string.IsNullOrEmpty(text.Text))
                found.Add(text.Text);
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                Walk(child);
        }

        Walk(node);
        return found;
    }
}
