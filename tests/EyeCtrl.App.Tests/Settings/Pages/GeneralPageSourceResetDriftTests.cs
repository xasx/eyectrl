namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

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
}
