namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using NFluent;
using Xunit;

/// <summary>
/// T06 fixup-01 F1: the caregiver alert and drift notice carry moved English with no literal pin —
/// key coverage cannot see a key used in the wrong slot. The windows are constructed headless
/// (never shown), which needs the token dictionary the way App.xaml supplies it at runtime, hence
/// the shared "WpfApplication" collection.
/// </summary>
[Collection("WpfApplication")]
public class CrisisOverlayTextTests
{
    [StaFact]
    public void TheCaregiverAlert_ShowsItsEnglishTexts()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new CaregiverAlertWindow();
        try
        {
            Check.That(window.TitleText.Text).IsEqualTo("Caregiver attention needed");

            Check.That(CaregiverAlertWindow.MessageFor(
                    StringTable.EmbeddedEnglish, CaregiverAlertCondition.SustainedPoorTracking))
                .IsEqualTo("Eye tracking has been unreliable for the last minute — consider repositioning, or recalibrating in Tobii.");
            Check.That(CaregiverAlertWindow.MessageFor(
                    StringTable.EmbeddedEnglish, CaregiverAlertCondition.SustainedStruggle))
                .IsEqualTo("The user has been struggling continuously — consider checking in.");
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheDriftNotice_ShowsItsEnglishMessage()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var window = new DriftNoticeWindow();
        try
        {
            Check.That(window.Message.Text)
                .IsEqualTo("Tracking may have drifted — consider recalibrating in Tobii");
        }
        finally
        {
            window.Close();
        }
    }
}
