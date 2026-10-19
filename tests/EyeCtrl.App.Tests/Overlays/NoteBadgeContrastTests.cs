namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// T6 P5: the number badge's colour floors, on the DRAWN brushes (the same discipline
/// <see cref="NoteTailContrastTests"/> pins — a test on token constants would stay green over a
/// window that draws something else). The badge is text-bearing chrome: the NUMERAL owes the
/// 4.5:1 text floor against the badge fill, and the badge EDGE owes the 3:1 non-text boundary
/// floor (WCAG 1.4.11) against the card fill it sits on. Every page extreme is checked because
/// the note may sit over anything: both the badge fill and the card fill carry real alpha
/// (0xFA), so each is composited over the page first.
/// </summary>
[Collection("WpfApplication")]
public class NoteBadgeContrastTests
{
    private const double TextFloor = 4.5;
    private const double ComponentBoundaryFloor = 3.0;

    public static TheoryData<ITheme> EveryTheme()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All) data.Add(theme);
        return data;
    }

    [StaTheory]
    [MemberData(nameof(EveryTheme))]
    public void TheBadgeNumeralAndEdge_ReachTheirFloors_InEveryTheme(ITheme theme)
    {
        using var themeScope = ThemeScope.Activate(theme);
        var (disc, label, card) = DrawnBadgedNote();

        var numeral = ((SolidColorBrush)label.Foreground).Color;
        var badgeFill = CompositedOver(((SolidColorBrush)disc.Fill).Color, Colors.White);
        var badgeEdge = ((SolidColorBrush)disc.Stroke).Color;
        var cardFill = ((SolidColorBrush)card.Background).Color;

        foreach (var page in new[] { Colors.White, Colors.Black })
        {
            var fill = CompositedOver(((SolidColorBrush)disc.Fill).Color, page);
            var numeralRatio = Contrast(numeral, fill);
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "In the {0} theme the drawn badge numeral reads {1:F2}:1 against the badge "
                        + "fill on the {2} page — the floor for text is {3:F1}. The numeral names "
                        + "the note; it must clear 4.5:1 on its own disc.",
                    theme.Name, numeralRatio, PageName(page), TextFloor))
                .That(numeralRatio).IsGreaterOrEqualThan(TextFloor);

            var edgeRatio = Contrast(badgeEdge, CompositedOver(cardFill, page));
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "In the {0} theme the drawn badge edge reads {1:F2}:1 against the card fill "
                        + "on the {2} page — the floor for a non-text boundary is {3:F1}. The "
                        + "badge's rim must be perceptible on the card it sits on.",
                    theme.Name, edgeRatio, PageName(page), ComponentBoundaryFloor))
                .That(edgeRatio).IsGreaterOrEqualThan(ComponentBoundaryFloor);
        }

        // The badge disc and the drawn numeral exist and carry a real diameter (a 0-size disc
        // would pass every ratio while drawing nothing).
        Check.That(disc.Width).IsGreaterOrEqualThan(NoteLayout.BadgeDiameterFor(NoteSettings.DefaultTextSizeDip) - 0.5);
        Check.That(label.Text).IsEqualTo("1");
    }

    private static string PageName(Color page) =>
        page == Colors.White ? "white (#FFFFFF)" : "black (#000000)";

    /// <summary>Alpha fills composite over the page first (review-01's nit) — the ratio is
    /// computed on the colour the page actually receives.</summary>
    private static Color CompositedOver(Color fill, Color page)
    {
        if (fill.A == 255) return fill;
        double a = fill.A / 255.0;
        byte Blend(byte f, byte p) => (byte)Math.Round((a * f) + ((1 - a) * p));
        return Color.FromRgb(Blend(fill.R, page.R), Blend(fill.G, page.G), Blend(fill.B, page.B));
    }

    /// <summary>Draws a real headless Display note with its badge pushed — the same window
    /// construction the reading renders use — and returns the drawn badge disc, badge label and
    /// card Border from the visual tree.</summary>
    private static (Ellipse Disc, TextBlock Label, Border Card) DrawnBadgedNote()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("badge-contrast");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Display);
            var card = new Size(
                NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip),
                NoteLayout.CardHeightDip(NoteSettings.DefaultTextSizeDip, 0, NoteTestBench.WorkArea.Height));
            var placement = NoteLayout.Place(
                new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!;
            window.ApplyPlacement(placement);
            window.SetText("Meds at 8:00.");
            window.SetBadge(1);

            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            var disc = window.BadgeDiscForTest;
            var label = window.BadgeLabelForTest;
            Check.WithCustomMessage("the drawn canvas must contain the badge disc — SetBadge "
                    + "drew nothing, so there is no badge to gate")
                .That(disc).IsNotNull();
            Check.WithCustomMessage("the drawn canvas must contain the badge numeral")
                .That(label).IsNotNull();
            var bg = canvas.Children.OfType<Border>().First();
            return (disc!, label!, bg);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
