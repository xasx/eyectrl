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
/// T5b: the note's pointer tail and the Display card edge owe the non-text 3:1 of WCAG 1.4.11
/// (the floor <see cref="EyeCtrl.App.Tests.Settings.SettingsContentCardBoundaryTests"/> pins),
/// because the tail shows the caretaker WHAT the note points at and the edge shows where the
/// note ends. Both ship with a SurfaceDark fill and used to sit on strokes that vanished
/// against a page of their own lightness (SurfaceMedium, 1.1-1.5:1) — T5b strokes both with
/// BorderSubtle, which clears 3:1 against a page of either extreme in every theme.
///
/// <para>
/// The brushes are read from the drawn visual tree of a real headless NoteWindow (never shown;
/// the same construction as <see cref="NoteReadingRenderTests"/>), not from the token constants:
/// a test on constants would stay green over a window that draws something else — the exact
/// staleness the mutants in this ticket's brief exploit.
/// </para>
///
/// <para>
/// WHY max(fill, stroke): the gate asks whether the boundary is PERCEPTIBLE, by either of the
/// two properties the part is drawn with. In the Contrast theme BorderSubtle is #FFFFFF (1.00
/// against a white page) while the black fill gives 21:1 — the boundary is visible by fill
/// there, and that satisfies 1.4.11.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteTailContrastTests
{
    /// <summary>
    /// A non-text user-interface component boundary owes 3:1 under WCAG 1.4.11, not the 4.5
    /// body text owes — the same floor <c>SettingsContentCardBoundaryTests</c> uses.
    /// </summary>
    private const double ComponentBoundaryFloor = 3.0;

    /// <summary>Every registered theme; the palette could add one tomorrow.</summary>
    public static TheoryData<ITheme> EveryTheme()
    {
        var data = new TheoryData<ITheme>();
        foreach (var theme in ThemeRegistry.All) data.Add(theme);
        return data;
    }

    /// <summary>
    /// The tail outline and the Display card edge are actually perceptible: in every theme,
    /// each clears 3:1 against a white page AND a black page — the note may sit over anything.
    /// </summary>
    [StaTheory]
    [MemberData(nameof(EveryTheme))]
    public void TheTailAndTheDisplayCardEdge_ArePerceptibleAgainstAnyPage_InEveryTheme(ITheme theme)
    {
        using var themeScope = ThemeScope.Activate(theme);
        var (tailFill, tailStroke, cardFill, cardStroke) = DrawnBrushes();

        // T5b's constancy half, on the DRAWN brushes: at rest in Display the tail's stroke and
        // the card's edge must be the same brush. The probe is drawn in Edit (a note is born
        // there) and committed into Display, whose SetState restyles ONLY the card border —
        // UpdateBorder never touches the tail polygon — so a state brush captured for the tail
        // at Draw time survives the flip as a stale stroke. The contrast gate below cannot see
        // that defect (a light Accent still clears 3:1 against a dark page, so max(fill, stroke)
        // holds on both extremes); this equality is what catches it.
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme the note's drawn tail stroke ({1}) and card edge ({2}) differ "
                    + "at rest in Display. The tail's stroke must be the state-constant "
                    + "BorderSubtle — not a state brush captured at Draw time, which SetState "
                    + "would leave stale on the tail while restyling only the card.",
                theme.Name, tailStroke, cardStroke))
            .That(tailStroke).IsEqualTo(cardStroke);

        foreach (var page in new[] { Colors.White, Colors.Black })
        {
            CheckPart("tail", theme.Name, page, tailFill, tailStroke);
            CheckPart("Display card", theme.Name, page, cardFill, cardStroke);
        }
    }

    private static void CheckPart(string part, string themeName, Color page, Color fill, Color stroke)
    {
        var fillRatio = Contrast(fill, page);
        var strokeRatio = Contrast(stroke, page);
        Check.WithCustomMessage(string.Format(
                CultureInfo.InvariantCulture,
                "In the {0} theme the note {1} reads fill {2:F2}:1 and stroke {3:F2}:1 against "
                    + "the {4} page — neither reaches the {5:F1} WCAG 1.4.11 floor for a "
                    + "non-text component boundary. The tail shows the caretaker what the note "
                    + "points at and the card edge shows where the note ends; stroke them with "
                    + "BorderSubtle (or another brush that clears 3:1 against pages of both "
                    + "lightnesses in this theme), and keep the check on the DRAWN brushes.",
                themeName, part, fillRatio, strokeRatio, PageName(page), ComponentBoundaryFloor))
            .That(Math.Max(fillRatio, strokeRatio)).IsGreaterOrEqualThan(ComponentBoundaryFloor);
    }

    private static string PageName(Color page) =>
        page == Colors.White ? "white (#FFFFFF)" : "black (#000000)";

    /// <summary>
    /// Draws a real headless note — DPI 2.0, a placement with a visible tail, the same window
    /// construction the reading renders use — through the PRODUCTION state sequence (a note is
    /// born in Edit: the manager draws it there, and the user commits into Display, which
    /// restyles only the card border via UpdateBorder and never redraws the tail), and returns
    /// the colours ACTUALLY assigned in the visual tree at rest: the tail Polygon's fill and
    /// stroke, and the card Border's background and border brush. Drawing the probe directly in
    /// Display would miss exactly the stale-tail defect this sequence exposes.
    /// </summary>
    private static (Color TailFill, Color TailStroke, Color CardFill, Color CardStroke) DrawnBrushes()
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("tail-contrast");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Edit);
            var card = new Size(
                NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip),
                NoteLayout.CardHeightDip(NoteSettings.DefaultTextSizeDip, 0, NoteTestBench.WorkArea.Height));
            var placement = NoteLayout.Place(
                new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!;
            Check.WithCustomMessage("the probe placement must show the tail — without it the "
                    + "gate below would pass over a window that draws no tail at all")
                .That(placement.TailVisible).IsTrue();
            window.ApplyPlacement(placement);
            window.SetText("Meds at 8:00.");
            window.SetState(NoteState.Display);

            // The canvas as the window lays it out - no HWND, nothing on the owner's screen.
            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            // The card border is found by type exactly the way UpdateBorder finds it: the tail
            // polygon may occupy slot 0, so index is not an identity.
            var tail = canvas.Children.OfType<Polygon>().FirstOrDefault();
            Check.WithCustomMessage("the drawn canvas must contain the tail polygon — Draw() "
                    + "stopped drawing it, so there is no tail boundary to gate")
                .That(tail).IsNotNull();
            var bg = canvas.Children.OfType<Border>().First();

            return (
                ((SolidColorBrush)tail!.Fill).Color,
                ((SolidColorBrush)tail.Stroke).Color,
                ((SolidColorBrush)bg.Background).Color,
                ((SolidColorBrush)bg.BorderBrush).Color);
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
