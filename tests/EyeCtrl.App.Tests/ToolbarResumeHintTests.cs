namespace EyeCtrl.App.Tests;

using System.Windows;
using EyeCtrl.App.Overlays;
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

        // HIDDEN, never Collapsed: the slot is reserved so the band's geometry — and therefore the
        // published dwell targets — is identical in both states (B1 regression; the drawn-face
        // distance itself is pinned by ToolbarPausedTargetAlignmentTests).
        Check.That(hint.Visibility).IsEqualTo(Visibility.Hidden);
        Check.That(hint.Visibility).IsNotEqualTo(Visibility.Collapsed);

        window.SetPausedState(true);
        Check.That(hint.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(hint.Text).Contains("look here to resume");

        window.SetPausedState(false);
        Check.That(hint.Visibility).IsEqualTo(Visibility.Hidden);
        Check.That(hint.Visibility).IsNotEqualTo(Visibility.Collapsed);
    }

    [StaFact]
    public void ResumeHint_Margin_FollowsTheBandOrientation()
    {
        // The XAML margin is horizontal (leading gap before the first button). On a vertical band
        // that gap belongs above the label — a sideways margin there leaves it visibly off-centre.
        var (window, _) = ToolbarTestHelpers.NewBoundToolbar();

        window.SetMode(isDocked: true, edge: "Top");
        Check.That(window.ResumeHintForTest.Margin)
            .IsEqualTo(new Thickness(ToolbarBand.ButtonMargin, 0, 0, 0));

        window.SetMode(isDocked: true, edge: "Left");
        Check.That(window.ResumeHintForTest.Margin)
            .IsEqualTo(new Thickness(0, ToolbarBand.ButtonMargin, 0, 0));
    }
}
