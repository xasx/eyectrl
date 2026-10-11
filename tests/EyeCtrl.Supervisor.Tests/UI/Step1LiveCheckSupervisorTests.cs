namespace EyeCtrl.Supervisor.Tests.UI;

using System;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;
using EyeCtrl.Supervisor.UI;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T11's supervised live check, Supervisor side: the FallenBack dialog, the setup window and a real
/// tray icon are shown ON THE REAL SCREEN with the shipped German, at the screen's real DPI, for a
/// fixed time, then closed. Nothing is simulated — no input, no cursor moves.
///
/// <para>
/// The two windows come from a background test process, so the showcase raises them to the
/// foreground itself: <c>Topmost = true</c>, <c>WindowStartupLocation = CenterScreen</c> and
/// <c>Activate()</c> (the FallenBack XAML already sets both properties; setting them again here is
/// the showcase's own guard, and no production XAML changes). Before every hold,
/// <see cref="LiveCheck.AssertVisible"/> proves the window is visible, topmost, on-screen and not
/// transparent.
/// </para>
///
/// <para>
/// <b>Not a gate.</b> The class is <c>Category=UI</c> so CI's <c>Category!=UI</c> filter excludes
/// it, and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. It is only ever run with the
/// owner's explicit go-ahead, under supervision, one case at a time:
/// <c>dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~&lt;case&gt;"</c>.
/// The order a viewer should see them in is the declaration order below: FallenBack, the setup
/// window, then the tray icon.
/// </para>
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
[Trait("Category", "UI")]
public class Step1LiveCheckSupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eyectrl-t11-live-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;
    private readonly string _writerMutexName =
        @"Local\EyeCtrl.Settings.Writer.Test." + Guid.NewGuid().ToString("N");

    public Step1LiveCheckSupervisorTests()
    {
        _install = Path.Combine(_root, "install");
        Directory.CreateDirectory(_install);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>The real files the App ships, loaded through the production loader.</summary>
    private StringTable German()
    {
        File.Copy(GermanDraft.GermanPath, Path.Combine(_install, StringTable.GermanFileName), overwrite: true);
        File.Copy(
            Path.Combine(GermanDraft.RepoRoot(), "src", "EyeCtrl.App", "Localization", StringTable.ManifestFileName),
            Path.Combine(_install, StringTable.ManifestFileName),
            overwrite: true);
        return StringTable.Load(UiLanguage.German, CultureInfo.GetCultureInfo("de-DE"), _install);
    }

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

    [Fact]
    public void TheFallenBackDialog_ShowsTheGermanTexts()
    {
        LiveCheck.Require();
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(German());
            dialog.Topmost = true;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            LiveCheck.ShowAndHold(dialog, nameof(FallenBackDialog), TimeSpan.FromSeconds(25));
        });
    }

    [Fact]
    public void TheSupervisorSetupWindow_ShowsTheGermanForm()
    {
        LiveCheck.Require();
        RunSta(() =>
        {
            var german = German();
            var language = new SettingDelivery<UiLanguage>(
                "UiLanguage",
                (value, restart) => SettingRequest.SetUiLanguage(value, restart),
                _ => new System.Threading.Tasks.TaskCompletionSource<AppRequestResult>().Task,
                work => work(),
                readDisk: () => UiLanguage.German,
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
                new LanguageSetupRow(language, () => UiLanguage.German, CultureInfo.GetCultureInfo("de-DE")),
                german,
                SupervisorStateText.Describe(german, SupervisorState.IdleWatch));
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            LiveCheck.ShowAndHold(window, nameof(SupervisorSetupWindow), TimeSpan.FromSeconds(20));
        });
    }

    /// <summary>
    /// A real tray icon for half a minute: the Supervisor's own icon with its German tooltip, so
    /// the owner can hover it by gaze. The menu is the production one (right-click); nothing here
    /// opens it for them. NOTE: the shell may place the icon in the notification OVERFLOW area
    /// (the "^" chevron), which the test cannot control — look there if it is not on the strip.
    /// </summary>
    [Fact]
    public void TheSupervisorTray_ShowsItsGermanTooltip()
    {
        LiveCheck.Require();
        RunSta(() =>
        {
            var german = German();
            var status = new TrayStatusText(() => german);
            using var tray = new SupervisorTrayIcon(
                () => { }, () => { }, () => { },
                texts: () => SupervisorTrayTexts.For(german),
                visible: true,
                refreshStatus: () => status.Update(SupervisorState.Supervising));
            tray.ApplyStatusRefresh();
            // What the icon will show on hover and when the menu opens: the German tooltip line and
            // the three German item texts. (The icon's placement is the shell's; see the note above.)
            Check.That(tray.StatusText).IsEqualTo("EyeCtrl Supervisor: überwacht EyeCtrl");
            Check.That(tray.ItemTexts).IsEqualTo(
                ("EyeCtrl Supervisor – Einstellungen", "EyeCtrl jetzt starten", "Supervisor beenden"));
            LiveCheck.PumpFor(TimeSpan.FromSeconds(30));
        });
    }
}
