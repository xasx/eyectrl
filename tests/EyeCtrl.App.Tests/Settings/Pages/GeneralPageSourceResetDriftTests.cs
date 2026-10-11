namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// Drift guard for the General tab's "Reset General Settings" button (issue #164): every
/// settings row the page renders must come back to its default when g_reset is confirmed.
/// Unlike every other tab, the General page's rows bind THREE different sub-settings objects
/// (Theme.Name, General.StartOnBoot/StartPaused, TrackStatus.Enabled) — the original handler
/// reset only the GeneralSettings pair, which is exactly the #157-class drift this guard
/// names. The rows are enumerated from <see cref="GeneralPageSource.Build"/> through the
/// shared <see cref="SettingsPageResetDriftGuard"/>, not hand-listed, so a new row joins the
/// guard the moment it joins the page.
/// </summary>
public class GeneralPageSourceResetDriftTests
{
    [Fact]
    public void ResetGeneral_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new GeneralPageSource(ctx).Build(),
            resetButtonId: "g_reset",
            pageName: "General");
    }

    [Fact]
    public void ResetGeneral_WithSupervisionAvailable_LeavesSupervisionUntouched()
    {
        // F8 pin: g_supervision is exempt from the reset contract by decision (the guard's
        // NotResetByPageReset): the section is the Supervisor's, and turning crash recovery off
        // goes through the row's RequestDisable (sanctioned persist + Run key), never a bulk
        // reset. This builds the page WITH a live service so the row is really present.
        var path = Path.Combine(Path.GetTempPath(), $"eyectrl_t04_reset_{Guid.NewGuid()}.json");
        try
        {
            var settings = new EyeCtrlSettings();
            settings.Supervision.Enabled = true;
            settings.Supervision.MaxRetries = 7;
            new SupervisionEnabledWriter(path).Persist(true);

            var (_, toggle, _) = SupervisionComposition.Create(
                settings, path, new FakeStartupRegistrar(),
                enablePublishing: () => { },
                launchSupervisor: () => false,
                tryReachSupervisor: () => false,
                isSupervisorInstalled: () => true,
                isLaunchRegistered: () => true,
                isSupervisorConnected: () => true,
                runInBackground: work => work(),
                postToUi: work => work());

            var ctx = TestSettingsPageContext.Build(
                settings: settings, supervision: toggle, showModal: (_, act) => act());
            var rows = new GeneralPageSource(ctx).Build().Single().Rows;
            Check.That(rows.OfType<ToggleSpec>().Any(t => t.Id == "g_supervision")).IsTrue();

            rows.OfType<ButtonSpec>().Single(b => b.Id == "g_reset").OnActivate();

            Check.That(settings.Supervision.Enabled).IsTrue();
            Check.That(settings.Supervision.MaxRetries).IsEqualTo(7);
            Check.That(new SettingsService(path, SettingsWriter.App).Load().Supervision.Enabled).IsTrue();
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Fact]
    public void ResetGeneral_KeepsTheLanguage()
    {
        // D10 (the user's call 2026-10-07): the language row sits on this page, but reset must
        // not touch it — a user who chose a readable language must not be reset into one they
        // cannot read. g_language is exempt in SettingsPageResetDriftGuard by name.
        var settings = new EyeCtrlSettings();
        settings.General.UiLanguage = EyeCtrl.Core.Enums.UiLanguage.English;
        settings.General.StartPaused = true;

        var ctx = TestSettingsPageContext.Build(settings: settings, showModal: (_, act) => act());
        var rows = new GeneralPageSource(ctx).Build().Single().Rows;

        // The row exists and carries the choice, so this is genuinely about the reset.
        var language = rows.OfType<ComboBoxSpec>().Single(c => c.Id == "g_language");
        Check.That(language.Get()).IsEqualTo("english");

        rows.OfType<ButtonSpec>().Single(b => b.Id == "g_reset").OnActivate();

        Check.That(settings.General.UiLanguage).IsEqualTo(EyeCtrl.Core.Enums.UiLanguage.English);
        Check.That(settings.General.StartPaused).IsFalse();   // other General rows still reset
    }

    private sealed class FakeStartupRegistrar : IStartupRegistrar
    {
        public bool IsRegistered => true;
        public void SetStartOnBoot(bool enabled) { }
    }
}
