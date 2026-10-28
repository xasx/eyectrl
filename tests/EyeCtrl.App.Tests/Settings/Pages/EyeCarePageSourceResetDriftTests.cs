namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views.Settings;

/// <summary>
/// Drift guard for the Eye-care tab's reset — same machinery as the Dwell/General/Keyboard
/// guards (issue #157 class). B6 adds the banner's dwell knob to this page; the shared guard
/// perturbs every settings row the page renders and requires the confirmed reset to restore it,
/// so a reset copy line left out for the knob reddens this file.
/// </summary>
public class EyeCarePageSourceResetDriftTests
{
    [Fact]
    public void ResetEyeCare_OnConfirmedModal_RestoresEverySettingsRowOnThePage()
    {
        SettingsPageResetDriftGuard.ResetRestoresEverySettingsRow(
            ctx => new EyeCarePageSource(ctx).Build(),
            resetButtonId: "ec_reset",
            pageName: "Eye-Care");
    }
}
