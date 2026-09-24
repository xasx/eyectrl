namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Dwell tab's "Reset Dwell Settings" button — same machinery as the
/// Keyboard guard (issue #157 class), extracted into SettingsPageResetDriftGuard so any page
/// source with a *_reset button can use it (spec §Settings; General pending #164, other eight
/// deferred — see the one-time census note in this task).
/// </summary>
public class DwellPageSourceResetDriftTests
{
    [Fact]
    public void ResetDwell_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new DwellPageSource(ctx).Build(),
            resetButtonId: "d_reset",
            pageName: "Dwell");
    }
}
