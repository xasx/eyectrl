namespace EyeCtrl.App.Tests.Settings.Pages;

using EyeCtrl.App.Views;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Controls.Theme;
using System.Windows.Media;
using NFluent;
using Xunit;

public class SettingsRenderMappingTests
{
    // ResetBase and DeleteBase are BOTH Tokens.AccentWarning, so swapping the two fill arms is
    // invisible on screen — only the label brush differs (surface-dark for both, primary for
    // Default). This pins the brush mapping, which is the only observable difference.
    [StaFact]
    public void FillFor_ResetAndDelete_UseDarkLabel_DefaultUsesPrimary()
    {
        var (_, resetText) = SettingsWindow.FillFor(ButtonKind.Reset);
        var (_, deleteText) = SettingsWindow.FillFor(ButtonKind.Delete);
        var (_, defaultText) = SettingsWindow.FillFor(ButtonKind.Default);

        Check.That(resetText).IsSameReferenceAs(Tokens.BrushSurfaceDark);
        Check.That(deleteText).IsSameReferenceAs(Tokens.BrushSurfaceDark);
        Check.That(defaultText).IsNull();
    }

    [StaFact]
    public void FillFor_ResetAndDeleteFills_AreTheSharedWarning_DefaultIsTheSurface()
    {
        // The fills themselves: both warning-tinted, default is the neutral surface. Asserted so a
        // future token rename that silently merges them is caught here, not on a live screen.
        var (resetFill, _) = SettingsWindow.FillFor(ButtonKind.Reset);
        var (deleteFill, _) = SettingsWindow.FillFor(ButtonKind.Delete);
        var (defaultFill, _) = SettingsWindow.FillFor(ButtonKind.Default);

        Check.That(resetFill).IsEqualTo(Tokens.AccentWarning);
        Check.That(deleteFill).IsEqualTo(Tokens.AccentWarning);
        Check.That(defaultFill).IsEqualTo(Tokens.SurfaceMedium);
    }
}
