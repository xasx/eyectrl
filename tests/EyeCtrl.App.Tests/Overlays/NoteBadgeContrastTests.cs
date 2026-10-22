namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// T6 P5/fixup-01: the number tag's colour floors, on the DRAWN brushes (the same discipline
/// <see cref="NoteTailContrastTests"/> pins — a test on token constants would stay green over a
/// window that draws something else). The tag is text-bearing chrome: the NUMERAL owes the
/// 4.5:1 text floor against the tag fill; and the tag boundary owes the 3:1 non-text floor
/// (WCAG 1.4.11) in max(fill, edge) form on BOTH of its surroundings — the CARD it straddles
/// (base half) and the PAGE its protruding half sits over, against a white AND a black page in
/// every theme (the T5b tail's discipline, with its alpha-composited fill handling).
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
        var (tag, label, card) = DrawnBadgedNote();

        var numeral = ((SolidColorBrush)label.Foreground).Color;
        var fill = ((SolidColorBrush)tag.Background).Color;
        var edge = ((SolidColorBrush)tag.BorderBrush).Color;
        var cardFill = ((SolidColorBrush)card.Background).Color;

        // The numeral's TEXT floor: 4.5:1 against the tag fill, on both page lightnesses
        // (composited when the fill carries alpha).
        foreach (var page in new[] { Colors.White, Colors.Black })
        {
            var tagFill = CompositedOver(fill, page);
            var numeralRatio = Contrast(numeral, tagFill);
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "In the {0} theme the drawn tag numeral reads {1:F2}:1 against the tag "
                        + "fill on the {2} page — the floor for text is {3:F1}. The numeral names "
                        + "the note; it must clear 4.5:1 on its own tag.",
                    theme.Name, numeralRatio, PageName(page), TextFloor))
                .That(numeralRatio).IsGreaterOrEqualThan(TextFloor);
        }

        // Non-text boundary floors in max(fill, edge) form — the same rationale NoteTailContrastTests
        // pins: the gate asks whether the boundary is PERCEPTIBLE by either property it is
        // drawn with. (a) Against the CARD the tag straddles (its base half).
        {
            var fillRatio = Contrast(CompositedOver(fill, Colors.White), CompositedOver(cardFill, Colors.White));
            var edgeRatio = Contrast(edge, CompositedOver(cardFill, Colors.White));
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "In the {0} theme the drawn tag reads fill {1:F2}:1 and edge {2:F2}:1 against "
                        + "the card fill — the floor for a non-text boundary is {3:F1}. The tag's "
                        + "rim must be perceptible on the card it straddles.",
                    theme.Name, fillRatio, edgeRatio, ComponentBoundaryFloor))
                .That(Math.Max(fillRatio, edgeRatio)).IsGreaterOrEqualThan(ComponentBoundaryFloor);
        }

        // (b) Against the PAGE the protruding half sits over — white AND black, in every
        // theme (the T5b discipline NoteTailContrastTests runs the tail through).
        foreach (var page in new[] { Colors.White, Colors.Black })
        {
            var fillRatio = Contrast(CompositedOver(fill, page), page);
            var edgeRatio = Contrast(edge, page);
            Check.WithCustomMessage(string.Format(
                    CultureInfo.InvariantCulture,
                    "In the {0} theme the drawn tag reads fill {1:F2}:1 and edge {2:F2}:1 against "
                        + "the {3} page — neither reaches the {4:F1} WCAG 1.4.11 floor for a "
                        + "non-text component boundary. The tag's straddled half sits over the "
                        + "page, which may be either lightness; the pair must carry it.",
                    theme.Name, fillRatio, edgeRatio, PageName(page), ComponentBoundaryFloor))
                .That(Math.Max(fillRatio, edgeRatio)).IsGreaterOrEqualThan(ComponentBoundaryFloor);
        }

        // The tag Border and the drawn numeral exist and carry the real tag size (a 0-size tag
        // would pass every ratio while drawing nothing), the edge is drawn at a real width (a
        // 0-width stroke paints nothing — review-01 m1's discipline), and the numeral reads off.
        Check.That(tag.Width).IsGreaterOrEqualThan(NoteLayout.TagWidthDip(NoteSettings.DefaultTextSizeDip) - 0.5);
        Check.That(tag.Height).IsGreaterOrEqualThan(NoteLayout.TagHeightDip(NoteSettings.DefaultTextSizeDip) - 0.5);
        Check.WithCustomMessage("the drawn tag edge must be at least 1 DIP wide — a zero-width "
                + "stroke paints no rim however correct its brush")
            .That(tag.BorderThickness.Left).IsGreaterOrEqualThan(1.0);
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

    /// <summary>Draws a real headless Display note with its tag pushed — the same window
    /// construction the reading renders use — and returns the drawn tag Border, tag label and
    /// card Border from the visual tree.</summary>
    private static (Border Tag, TextBlock Label, Border Card) DrawnBadgedNote()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("badge-contrast");
            InvisibleTestWindow.Prepare(window);
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

            var tag = window.BadgeTagForTest;
            var label = window.BadgeLabelForTest;
            Check.WithCustomMessage("the drawn canvas must contain the tag Border — SetBadge "
                    + "drew nothing, so there is no tag to gate")
                .That(tag).IsNotNull();
            Check.WithCustomMessage("the drawn canvas must contain the tag numeral")
                .That(label).IsNotNull();
            var bg = canvas.Children.OfType<Border>().First(b => !ReferenceEquals(b, tag));
            return (tag!, label!, bg);
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
