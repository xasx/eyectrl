namespace EyeCtrl.App.Tests.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Services;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// C10 fixup-01: the German escape label must fit the real button at 200 % DPI — the coordinator's
/// stop condition. The button is 340 x 100 DIP with a 14 DIP label margin (312 x 100 usable, 24 DIP
/// type). The banner is built headlessly and never shown; the measurement uses de-DE metrics at
/// <c>pixelsPerDip = 2.0</c>, so the test box's DPI cannot weaken it.
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class BlockedInputBannerGermanFitTests
{
    private const double ButtonWidth = 340;
    private const double ButtonHeight = 100;
    private const double LabelMargin = 14;
    private const double PixelsPerDip = 2.0;

    [StaFact]
    public void TheGermanEscapeLabel_FitsThe340x100Button_At200Percent()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0);

        StringTable.Activate(GermanDraft.Table);
        try
        {
            var banner = new BlockedInputBannerWindow(dwellMs: 25, present: _ => { });
            var label = banner.EscapeLabel;

            var german = GermanDraft.TextOf("overlays.blocked.escape", string.Empty);
            Check.WithCustomMessage("the banner did not load the German escape label.")
                .That(label.Text).IsEqualTo(german);

            var usableWidth = ButtonWidth - 2 * LabelMargin;
            var typeface = new Typeface(
                label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);

            var formatted = Format(label.Text, typeface, label.FontSize, usableWidth);
            Check.WithCustomMessage(
                    $"the German escape label needs {formatted.Width:F0} x {formatted.Height:F0} DIP "
                    + $"but the button's label box is {usableWidth:F0} x {ButtonHeight:F0} DIP.")
                .That(formatted.Width).IsLessOrEqualThan(usableWidth + 0.5);
            Check.WithCustomMessage(
                    $"the German escape label needs {formatted.Height:F0} DIP of height; the button has "
                    + $"{ButtonHeight:F0}.")
                .That(formatted.Height).IsLessOrEqualThan(ButtonHeight + 0.5);

            // A word wider than the box would be broken mid-word; every word must fit on its own.
            foreach (var word in label.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var single = Format(word, typeface, label.FontSize, double.PositiveInfinity);
                Check.WithCustomMessage($"'{word}' is {single.Width:F0} DIP wide and cannot fit unbroken.")
                    .That(single.Width).IsLessOrEqualThan(usableWidth + 0.5);
            }

            // The real element, laid out in the same box, must agree with the formatted measurement.
            label.Measure(new Size(usableWidth, ButtonHeight));
            Check.That(label.DesiredSize.Width).IsLessOrEqualThan(usableWidth + 0.5);
            Check.That(label.DesiredSize.Height).IsLessOrEqualThan(ButtonHeight + 0.5);

            // The dismiss label shares the type size; its 200 DIP button leaves 172 DIP and it is one
            // word, so it must also fit without breaking.
            var dismiss = banner.DismissLabel;
            Check.That(dismiss.Text).IsEqualTo(GermanDraft.TextOf("overlays.blocked.dismiss", string.Empty));
            var dismissFormatted = Format(
                dismiss.Text, new Typeface(dismiss.FontFamily, dismiss.FontStyle, dismiss.FontWeight, dismiss.FontStretch),
                dismiss.FontSize, 200 - 2 * LabelMargin);
            Check.That(dismissFormatted.Width).IsLessOrEqualThan(200 - 2 * LabelMargin + 0.5);
            Check.That(dismissFormatted.Height).IsLessOrEqualThan(ButtonHeight + 0.5);
        }
        finally
        {
            // The table is process-global; German must not outlive this test.
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static FormattedText Format(string text, Typeface typeface, double fontSize, double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.GetCultureInfo("de-DE"),
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.Black,
            pixelsPerDip: PixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };

        // FormattedText rejects an infinite paragraph width; omitting the cap gives one line.
        if (!double.IsPositiveInfinity(maxWidth))
            formatted.MaxTextWidth = maxWidth;

        return formatted;
    }
}
