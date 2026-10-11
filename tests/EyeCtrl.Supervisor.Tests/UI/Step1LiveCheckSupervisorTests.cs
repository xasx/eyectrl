namespace EyeCtrl.Supervisor.Tests.UI;

using System;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
/// <b>Not a gate.</b> The class is <c>Category=UI</c> so CI's <c>Category!=UI</c> filter excludes
/// it, and every test SKIPS unless <c>EYECTRL_LIVE_CHECK=1</c>. It is only ever run with the
/// owner's explicit go-ahead, under supervision:
/// <c>dotnet test tests/EyeCtrl.Supervisor.Tests --filter "FullyQualifiedName~Step1LiveCheck"</c>
/// (three cases, about 75 seconds in total).
/// </para>
/// </summary>
[Collection(SupervisorXamlCollection.Name)]
[Trait("Category", "UI")]
public class Step1LiveCheckSupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eyectrl-t11-live-" + Guid.NewGuid().ToString("N"));
    private readonly string _install;

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

    private static void RequireLiveCheck() =>
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("EYECTRL_LIVE_CHECK") is "1" or "true" or "TRUE",
            "The T11 live check paints real windows on the screen; set EYECTRL_LIVE_CHECK=1 with the owner's go-ahead.");

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

    /// <summary>Shows the window and pumps for <paramref name="duration"/>, then closes it.</summary>
    private static void ShowFor(Window window, TimeSpan duration)
    {
        window.Show();
        PumpFor(duration);
        window.Close();
    }

    /// <summary>
    /// Pumps this thread's dispatcher (which also dispatches the WinForms tray's messages) until
    /// the timer ends the frame.
    /// </summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timer.Stop();
        }
    }

    [Fact]
    public void TheFallenBackDialog_ShowsTheGermanTexts()
    {
        RequireLiveCheck();
        RunSta(() =>
        {
            var dialog = new FallenBackDialog(German());
            ShowFor(dialog, TimeSpan.FromSeconds(25));
        });
    }

    [Fact]
    public void TheSupervisorSetupWindow_ShowsTheGermanForm()
    {
        RequireLiveCheck();
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
                new SettingsService(Path.Combine(_root, "settings.json"), SettingsWriter.Supervisor),
                _ => { },
                () => { },
                new StartOnBootSetupRow(startOnBoot, () => false),
                new LanguageSetupRow(language, () => UiLanguage.German, CultureInfo.GetCultureInfo("de-DE")),
                german,
                SupervisorStateText.Describe(german, SupervisorState.IdleWatch));
            ShowFor(window, TimeSpan.FromSeconds(20));
        });
    }

    /// <summary>
    /// A real tray icon for half a minute: the Supervisor's own icon with its German tooltip, so
    /// the owner can hover it by gaze. The menu is the production one (right-click); nothing here
    /// opens it for them.
    /// </summary>
    [Fact]
    public void TheSupervisorTray_ShowsItsGermanTooltip()
    {
        RequireLiveCheck();
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
            PumpFor(TimeSpan.FromSeconds(30));
        });
    }
}
