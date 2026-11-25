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
/// Ticket 02's status strip is <c>fit=row</c> until Ticket 03 adds the real badge/strip class and
/// measurer. That deferral left the German fit ungated, so this is the REVIEWER's headless
/// backstop the l10n coordinator asked for: the three WIDEST German badges, rendered side by side
/// at the user's 2.0× in the production overlay font, must show whole — no clipping, no ellipsis,
/// and the whole strip inside the reference work area.
///
/// <para>
/// It is deliberately NOT a fit class and adds no string-table metadata. The badge padding and
/// gap below are this probe's generous assumptions; Ticket 03's real strip geometry replaces them
/// when the window exists. The doubled-text probe at the end proves the width check can fail.
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
    public void TheThreeWidestGermanBadges_ShowWholeSideBySideAt2x()
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
            foreach (var (badge, label) in badges.Zip(labels))
            {
                var text = (TextBlock)badge.Child;
                Check.WithCustomMessage($"\"{label}\" is trimmed or clipped in its badge.")
                    .That(text.TextTrimming).IsEqualTo(TextTrimming.None);
                Check.WithCustomMessage($"\"{label}\" wraps instead of reading on one line.")
                    .That(text.TextWrapping).IsEqualTo(TextWrapping.NoWrap);
                Check.WithCustomMessage($"\"{label}\" needs {FitText.Dip(FitText.NaturalWidth(text, label))} "
                        + $"but is given {FitText.Dip(text.ActualWidth)} DIP.")
                    .That(FitText.NaturalWidth(text, label)).IsLessOrEqualThan(text.ActualWidth + 0.5);
            }

            Check.WithCustomMessage(
                    $"The three widest German badges need {FitText.Dip(width)} DIP side by side, "
                    + $"wider than the {FitText.Dip(AppFitMeasurers.ReferenceWorkArea.Width)} DIP work area.")
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
