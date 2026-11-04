namespace EyeCtrl.Setup.Tests.Display;

using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Setup;
using EyeCtrl.Setup.Display;

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
                        Check.WithCustomMessage($"{theme.Name}: the layered background is not opaque.")
                            .That(background.Color.A).IsEqualTo((byte)255);
                        Check.That(background.Color.R).IsEqualTo(theme.SurfaceDark.R);
                        Check.That(background.Color.G).IsEqualTo(theme.SurfaceDark.G);
                        Check.That(background.Color.B).IsEqualTo(theme.SurfaceDark.B);

                        var border = (Border)window.Content;
                        var borderBackground = (SolidColorBrush)border.Background;
                        Check.WithCustomMessage($"{theme.Name}: the content border uses another background.")
                            .That(borderBackground.Color).IsEqualTo(background.Color);

                        var panel = (StackPanel)border.Child;
                        var title = (TextBlock)panel.Children[0];
                        var detail = (TextBlock)panel.Children[1];
                        Check.WithCustomMessage($"{theme.Name}: the title brush is not TextPrimary.")
                            .That(((SolidColorBrush)title.Foreground).Color).IsEqualTo(theme.TextPrimary);
                        Check.WithCustomMessage($"{theme.Name}: the detail brush is not TextSecondary.")
                            .That(((SolidColorBrush)detail.Foreground).Color).IsEqualTo(theme.TextSecondary);

                        var titleContrast = Contrast(theme.TextPrimary, background.Color);
                        var detailContrast = Contrast(theme.TextSecondary, background.Color);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the title contrast is {titleContrast:0.00}:1, below {MinimumTitleContrast}.")
                            .That(titleContrast).IsGreaterOrEqualThan(MinimumTitleContrast);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the detail contrast is {detailContrast:0.00}:1, below {MinimumDetailContrast}.")
                            .That(detailContrast).IsGreaterOrEqualThan(MinimumDetailContrast);

                        window.ShowResult(new SetupDisplayResult("failure", IsFailure: true));
                        var failureTitle = (SolidColorBrush)title.Foreground;
                        Check.That(failureTitle.Color).IsEqualTo(theme.AccentDanger);
                        var failureContrast = Contrast(theme.AccentDanger, background.Color);
                        Check.WithCustomMessage(
                                $"{theme.Name}: the failure-title contrast is {failureContrast:0.00}:1, below {MinimumFailureTitleContrast}.")
                            .That(failureContrast).IsGreaterOrEqualThan(MinimumFailureTitleContrast);
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
