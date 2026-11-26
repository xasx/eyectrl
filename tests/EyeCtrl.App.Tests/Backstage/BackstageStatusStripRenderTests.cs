namespace EyeCtrl.App.Tests.Backstage;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.App.Tests.Localization.Fit;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// A SMOKE RENDER, not a fit gate, for the Backstage status strip's German badges. It lays the
/// three widest approved German badges side by side at the user's 2.0× in the production overlay
/// font and checks one thing a headless probe honestly can before the window exists: their total
/// width still fits the 1920 DIP reference work area.
///
/// <para>
/// <b>The German FIT gate is now <c>backstageBadge</c></b> (Ticket 03): <c>GermanFitTests</c>
/// measures every status pill in <c>BackstageWindow.BuildStatusBadge</c> against one third of the
/// 1280 DIP header strip. This test must not be read as that gate: a horizontal
/// <c>StackPanel</c> measures children with infinite width, so per-badge "no clip / no trim /
/// natural ≤ actual" checks are tautological and are deliberately not written here. The tenfold
/// probe at the end only proves the total-width check can fail.
/// </para>
///
/// <para>
/// The badge padding and gap are this probe's generous assumptions; the real strip geometry lives
/// in <c>BackstageWindow</c> and its fit class.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class BackstageStatusStripRenderTests
{
    /// <summary>The probe's per-side horizontal badge padding, DIP.</summary>
    private const double BadgePaddingDip = 16;

    /// <summary>The probe's gap between badges, DIP.</summary>
    private const double BadgeGapDip = 16;

    [StaFact]
    public void TheThreeWidestGermanBadges_SmokeRender_SideBySideAt2x()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var previous = StringTable.Current;
        var german = ShippedGerman.Load();
        StringTable.Activate(german);
        try
        {
            var labels = new[]
            {
                german.T("overlays.backstage.status.installed.uiAccess"),
                german.T("overlays.backstage.status.supervisor.notAnswering"),
                german.T("overlays.backstage.status.supervisor.unavailable"),
            };

            var strip = Strip(labels, out var badges);
            using var host = FitHost.Open(strip, width: AppFitMeasurers.ReferenceWorkArea.Width,
                family: Tokens.FontStandard);

            var width = badges.Sum(b => b.ActualWidth + b.Margin.Left + b.Margin.Right);
            Check.WithCustomMessage(
                    $"The three widest German badges need {FitText.Dip(width)} DIP side by side, "
                    + $"wider than the {FitText.Dip(AppFitMeasurers.ReferenceWorkArea.Width)} DIP work area. "
                    + "The badges' real fit class is Ticket 03's.")
                .That(width).IsStrictlyLessThan(AppFitMeasurers.ReferenceWorkArea.Width);

            // Teeth: the same probe must reject a strip that cannot fit.
            var overLong = labels.Select(label => string.Join(" ", Enumerable.Repeat(label, 10))).ToArray();
            var overLongStrip = Strip(overLong, out var overLongBadges);
            using var overLongHost = FitHost.Open(overLongStrip,
                width: AppFitMeasurers.ReferenceWorkArea.Width, family: Tokens.FontStandard);
            var overLongWidth = overLongBadges.Sum(b => b.ActualWidth + b.Margin.Left + b.Margin.Right);
            Check.WithCustomMessage("The strip probe passed a tenfold German label: it cannot fail.")
                .That(overLongWidth).IsStrictlyGreaterThan(AppFitMeasurers.ReferenceWorkArea.Width);
        }
        finally
        {
            StringTable.Activate(previous);
        }
    }

    /// <summary>Builds the probe's horizontal strip: one padded, untrimmed one-line badge per label.</summary>
    private static StackPanel Strip(string[] labels, out List<Border> badges)
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        badges = new List<Border>(labels.Length);
        foreach (var label in labels)
        {
            var text = new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                Padding = new Thickness(BadgePaddingDip, 8, BadgePaddingDip, 8),
                Margin = new Thickness(0, 0, BadgeGapDip, 0),
                Child = text,
            };
            badges.Add(badge);
            strip.Children.Add(badge);
        }

        return strip;
    }
}
