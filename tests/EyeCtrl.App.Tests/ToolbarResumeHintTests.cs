namespace EyeCtrl.App.Tests;

using System.Windows;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// The paused toolbar's resume affordance must be labelled: the icon swap alone (Pause → Play)
/// reads as a broken button to a user who cannot tell the states apart. Nothing calls Show();
/// constructing the window parses its XAML, which is all this needs.
/// </summary>
[Collection("WpfApplication")]
public class ToolbarResumeHintTests
{
    [StaFact]
    public void SetPausedState_ShowsTheResumeLabel_AndHidesItOnResume()
    {
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();
        var hint = window.ResumeHintForTest;

        Check.That(hint.Visibility).IsEqualTo(Visibility.Collapsed);

        window.SetPausedState(true);
        Check.That(hint.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(hint.Text).Contains("look here to resume");

        window.SetPausedState(false);
        Check.That(hint.Visibility).IsEqualTo(Visibility.Collapsed);
    }
}
