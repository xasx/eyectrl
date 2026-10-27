namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Advanced tab's reset — same machinery as the Dwell/General/Keyboard
/// guards (issue #157 class). B6 adds five exit-dwell rows here; the shared guard perturbs every
/// settings row the page renders and requires the confirmed reset to restore it, so a reset copy
/// line left out for one of the new rows reddens this file.
/// </summary>
public class AdvancedPageSourceResetDriftTests
{
    [Fact]
    public void ResetAdvanced_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new AdvancedPageSource(ctx).Build(),
            resetButtonId: "av_reset",
            pageName: "Advanced");
    }
}
