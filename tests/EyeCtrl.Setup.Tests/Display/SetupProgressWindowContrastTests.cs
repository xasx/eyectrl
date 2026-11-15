namespace EyeCtrl.Setup.Tests.Display;

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// N1: the H1 layered window keeps the r01 background colour, opaque and frozen, and the result
/// text keeps the contrast it had on the non-layered window. The owner reads the failure title and
/// the detail in the worst case (rolled back, degraded, "get the caregiver"), so both are pinned
/// against the real background in every theme.
/// </summary>
[Collection(SetupUiWindowsCollection.Name)]
public sealed class SetupProgressWindowContrastTests
{
    /// <summary>The r01 normal title ratios run 12.69–21.00; nothing may fall below 10.</summary>
    private const double MinimumTitleContrast = 10.0;

    /// <summary>The r01 failure-title floor is Daylight's 4.44; keep it there.</summary>
    private const double MinimumFailureTitleContrast = 4.4;

    /// <summary>The detail must stay at least WCAG AA for normal text.</summary>
    private const double MinimumDetailContrast = 4.5;

    [Fact]
    public void TheResultText_KeepsItsContrast_OnTheOpaqueDarkSurface()
    {
        RunSta(() =>
        {
            try
            {
                foreach (var theme in ThemeRegistry.All)
                {
                    Tokens.Activate(theme);
                    var window = new SetupProgressWindow(
                        StringTable.EmbeddedEnglish, SetupVerb.Update, close: () => { });
                    try
                    {
                        var background = (SolidColorBrush)window.Background;
                        Check.WithCustomMessage($"{theme.Name}: the window background is not frozen.")
                            .That(background.IsFrozen).IsTrue();
                        Check.That(background.Color.A).IsEqualTo((byte)255);

                        var band = BandOf(window);
                        var bandBackground = (SolidColorBrush)band.Background;
                        Check.WithCustomMessage($"{theme.Name}: the band background is not frozen.")
                            .That(bandBackground.IsFrozen).IsTrue();
                        Check.That(bandBackground.Color.A).IsEqualTo((byte)255);
                        Check.WithCustomMessage($"{theme.Name}: the band brush is not the theme's opaque SurfaceMedium.")
                            .That(bandBackground.Color).IsEqualTo(SetupBrushes.OpaqueColor(theme.SurfaceMedium));

                        // r6 I2/M7: the surfaces under the text are read FROM THE TREE, not from
                        // window.Background — the body Border (row 2) is what the detail and the
                        // confirming text actually sit on, and its colour is pinned per theme.
                        var body = BodyOf(window);
                        var bodyBackground = (SolidColorBrush)body.Background;
                        Check.WithCustomMessage($"{theme.Name}: the body background is not frozen.")
                            .That(bodyBackground.IsFrozen).IsTrue();
                        Check.That(bodyBackground.Color.A).IsEqualTo((byte)255);
                        Check.WithCustomMessage($"{theme.Name}: the body brush is not the theme's opaque SurfaceDark.")
                            .That(bodyBackground.Color).IsEqualTo(SetupBrushes.OpaqueColor(theme.SurfaceDark));

                        var bandTitle = BandTitleOf(window);
                        Check.WithCustomMessage($"{theme.Name}: the band title brush is not TextPrimary.")
                            .That(((SolidColorBrush)bandTitle.Foreground).Color).IsEqualTo(theme.TextPrimary);

                        var detail = DetailOf(window);
                        Check.WithCustomMessage($"{theme.Name}: the detail brush is not TextSecondary.")
                            .That(((SolidColorBrush)detail.Foreground).Color).IsEqualTo(theme.TextSecondary);

                        var surface = bodyBackground.Color;
                        Check.WithCustomMessage(
                                $"{theme.Name}: title contrast {Contrast(theme.TextPrimary, surface):0.00}:1 < 10.")
                            .That(Contrast(theme.TextPrimary, surface)).IsGreaterOrEqualThan(MinimumTitleContrast);
                        Check.WithCustomMessage(
                                $"{theme.Name}: detail contrast {Contrast(theme.TextSecondary, surface):0.00}:1 < 4.5.")
                            .That(Contrast(theme.TextSecondary, surface)).IsGreaterOrEqualThan(MinimumDetailContrast);

                        var bandSurface = bandBackground.Color;
                        var bandTitleContrast = Contrast(theme.TextPrimary, bandSurface);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the band title contrast on the band is "
                                + $"{bandTitleContrast:0.00}:1 < 10.")
                            .That(bandTitleContrast).IsGreaterOrEqualThan(MinimumTitleContrast);

                        // D8/r3 F2: on results the band title is demoted (TextSecondary — Midnight
                        // measures 5.63 on the band) and the state edge + icon carry the colour; the
                        // confirming text stays TextPrimary (D7).
                        window.SetBandTitle(SetupDisplayText.ProgressTitle(
                            StringTable.EmbeddedEnglish, SetupVerb.Update));
                        window.ShowResult(new SetupDisplayResult(
                            SetupDisplayText.ProgressTitle(StringTable.EmbeddedEnglish, SetupVerb.Update),
                            SetupDisplayState.Failure));

                        Check.WithCustomMessage($"{theme.Name}: the band title was not demoted.")
                            .That(((SolidColorBrush)BandTitleOf(window).Foreground).Color)
                            .IsEqualTo(theme.TextSecondary);
                        var demotedContrast = Contrast(theme.TextSecondary, bandSurface);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the demoted band title contrast is {demotedContrast:0.00}:1 < 4.5.")
                            .That(demotedContrast).IsGreaterOrEqualThan(MinimumDetailContrast);
                        Check.WithCustomMessage($"{theme.Name}: the state edge was not failure-red.")
                            .That(((SolidColorBrush)EdgeOf(window).Background).Color)
                            .IsEqualTo(theme.AccentDanger);
                        Check.WithCustomMessage($"{theme.Name}: the confirming text brush is not TextPrimary (D7).")
                            .That(((SolidColorBrush)ResultTitleOf(window).Foreground).Color)
                            .IsEqualTo(theme.TextPrimary);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the failure-icon contrast is "
                                + $"{Contrast(theme.AccentDanger, surface):0.00}:1 < {MinimumFailureTitleContrast}.")
                            .That(Contrast(theme.AccentDanger, surface))
                            .IsGreaterOrEqualThan(MinimumFailureTitleContrast);
                        Check.WithCustomMessage($"{theme.Name}: the failure icon is not danger-red.")
                            .That(((SolidColorBrush)window.ResultStateGlyph.Stroke).Color)
                            .IsEqualTo(theme.AccentDanger);

                        // r4 finding 5: the glyph's geometry is 24 units under a LayoutTransform to
                        // the 64-DIP box — pin the transform (a missing or halved transform drew the
                        // check at ~17 DIP).
                        Check.That(window.ResultStateGlyph.LayoutTransform)
                            .IsInstanceOf<ScaleTransform>();
                        var transform = (ScaleTransform)window.ResultStateGlyph.LayoutTransform;
                        Check.That(transform.ScaleX).IsEqualTo(SetupWindowMetrics.ResultIconSize / 24.0);
                        Check.That(window.ResultStateGlyph.Width).IsEqualTo(24);
                        Check.That(window.ResultStateGlyph.Data.Bounds.Width).IsStrictlyGreaterThan(14);
                        Check.That(window.ResultStateGlyph.Data.Bounds.Height).IsStrictlyGreaterThan(14);

                        // r6 M6: the Success and Neutral state colours are pinned per theme too —
                        // a mutant painting the success screen danger-red survived until this pin.
                        window.ShowResult(new SetupDisplayResult("success", SetupDisplayState.Success));
                        Check.WithCustomMessage($"{theme.Name}: the success edge is not success-green.")
                            .That(((SolidColorBrush)EdgeOf(window).Background).Color)
                            .IsEqualTo(theme.AccentSuccess);
                        Check.WithCustomMessage($"{theme.Name}: the success icon is not success-green.")
                            .That(((SolidColorBrush)window.ResultStateGlyph.Stroke).Color)
                            .IsEqualTo(theme.AccentSuccess);
                        window.ShowResult(new SetupDisplayResult("neutral", SetupDisplayState.Neutral));
                        Check.WithCustomMessage($"{theme.Name}: the neutral edge is not dim.")
                            .That(((SolidColorBrush)EdgeOf(window).Background).Color)
                            .IsEqualTo(theme.TextDim);
                        Check.WithCustomMessage($"{theme.Name}: the neutral screen did not collapse the state icon.")
                            .That(window.ResultStateGlyph.Visibility).IsEqualTo(Visibility.Collapsed);
                        Check.WithCustomMessage($"{theme.Name}: the neutral screen did not show the dash.")
                            .That(window.NeutralDashVisible).IsTrue();
                    }
                    finally
                    {
                        window.Close();
                    }
                }
            }
            finally
            {
                Tokens.Activate(MidnightTheme.Instance);
            }
        });
    }

    private static Border BandOf(SetupProgressWindow window) => (Border)((Grid)window.Content).Children[1];
    private static Border EdgeOf(SetupProgressWindow window) => (Border)((Grid)window.Content).Children[0];
    private static Border BodyOf(SetupProgressWindow window) => (Border)((Grid)window.Content).Children[2];

    private static TextBlock BandTitleOf(SetupProgressWindow window) =>
        ((Grid)BandOf(window).Child).Children.OfType<TextBlock>().First();

    private static TextBlock DetailOf(SetupProgressWindow window) =>
        FitText.Descendants<TextBlock>((FrameworkElement)window.Content)
            .First(t => t.FontSize == SetupWindowMetrics.ProseFontSize);

    private static TextBlock ResultTitleOf(SetupProgressWindow window) =>
        FitText.Descendants<TextBlock>((FrameworkElement)window.Content)
            .First(t => t.FontSize == SetupWindowMetrics.TitleFontSize && t != BandTitleOf(window));

    /// <summary>The WCAG 2.x contrast ratio of two opaque colours.</summary>
    private static double Contrast(Color a, Color b)
    {
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
