namespace EyeCtrl.App.Tests.Settings;

using System.Windows.Media;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Views;
using EyeCtrl.Controls.Theme;

/// <summary>
/// Wave D D4: a tab that is merely FILLING must not read as selected. The old code blended the
/// tab's fill from the inactive toward the active colour with dwell progress, so an 80 % dwell
/// looked like a selected tab. Progress now lives on a bottom bar (a separate channel and a
/// separate colour), and the fill is a steady state value the dwell tick never touches.
/// </summary>
[Collection("ActiveTheme")]
public class SettingsTabProgressChannelTests
{
    [StaFact]
    public void TabProgress_IsABar_TheFillIsNeverRepainted()
    {
        foreach (var theme in ThemeRegistry.All)
        {
            using (ThemeScope.Activate(theme))
            {
                var inactive = new SolidColorBrush(Tokens.SurfaceKey);
                var selected = new SolidColorBrush(Tokens.SurfaceKeySwitch);
                var face = SettingsWindow.BuildTabFace("probe", "\u2699", "Dwell", inactive);
                var bar = SettingsWindow.TabProgressScale(face);
                var barColor = SettingsWindow.TabProgressColor(face);

                // The two channels are different visual objects AND different colours.
                Check.WithCustomMessage($"{theme.Name}: the progress bar uses the selected fill colour")
                    .That(barColor).IsNotEqualTo(SettingsWindow.TabBackgroundColor(
                        SettingsWindow.BuildTabFace("selected", "\u2699", "Dwell", selected)));
                Check.WithCustomMessage($"{theme.Name}: the progress bar uses the inactive fill colour")
                    .That(barColor).IsNotEqualTo(SettingsWindow.TabBackgroundColor(face));

                var fillBefore = ((SolidColorBrush)face.Background).Color;

                // Exactly what the window's UpdateTabVisuals path runs for an 80 % dwell.
                SettingsWindow.ApplyTabProgress("Dwell", 0.8, "Cursor", inactive, bar);

                Check.That(bar.ScaleX).IsCloseTo(0.8, 1e-9);
                Check.WithCustomMessage(
                        $"{theme.Name}: an 80 % dwell repainted the tab's fill, so it can read as selected")
                    .That(((SolidColorBrush)face.Background).Color).IsEqualTo(fillBefore);
            }
        }
    }

    [StaFact]
    public void TheSelectedTab_CarriesNoProgress()
    {
        var face = SettingsWindow.BuildTabFace(
            "Dwell", "\u2699", "Dwell", new SolidColorBrush(Tokens.SurfaceKey));
        var bar = SettingsWindow.TabProgressScale(face);
        var fill = SettingsWindow.TabBackgroundColor(face);

        SettingsWindow.ApplyTabProgress("Dwell", 1.0, "Dwell", new SolidColorBrush(Tokens.SurfaceKey), bar);

        Check.That(bar.ScaleX).IsEqualTo(0);
        Check.That(SettingsWindow.TabBackgroundColor(face)).IsEqualTo(fill);
    }

    [StaFact]
    public void TheProgressBar_IsPinnedToTheTabsBottomEdge_AndDoesNotEatClicks()
    {
        var face = SettingsWindow.BuildTabFace(
            "Dwell", "\u2699", "Dwell", new SolidColorBrush(Tokens.SurfaceKey));
        var bar = BarOf(face);

        Check.That(bar.Height).IsEqualTo(SettingsWindow.TabProgressBarHeight);
        Check.That(bar.VerticalAlignment).IsEqualTo(System.Windows.VerticalAlignment.Bottom);
        Check.WithCustomMessage("the bar would swallow the tab's mouse fallback")
            .That(bar.IsHitTestVisible).IsFalse();
    }

    private static System.Windows.Controls.Border BarOf(System.Windows.Controls.Border tab)
    {
        var grid = (System.Windows.Controls.Grid)tab.Child;
        return grid.Children.OfType<System.Windows.Controls.Border>()
            .Single(b => ReferenceEquals(b.Tag, SettingsWindow.TabProgressBarTag));
    }
}
