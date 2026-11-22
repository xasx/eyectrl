namespace EyeCtrl.App.Tests.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using NFluent;
using Xunit;

/// <summary>
/// Notes readability (sitting 9): the note's readable text — the body, the chip and the tag
/// numeral — draws at SemiBold, and every measurement of that text measures the SAME weight.
/// The pins:
/// <list type="bullet">
/// <item>the drawn body text block is SemiBold in EVERY state (Display and Edit share one
/// block, P4 D2);</item>
/// <item>the drawn chip text block is SemiBold (its measured column comes from
/// <c>NoteLayout.MeasureChip</c> at the same weight);</item>
/// <item>the drawn tag numeral is SemiBold at 1.0× the body size;</item>
/// <item><c>MeasureTextHeightDip</c>'s height equals the drawn block's height at SemiBold —
/// measured on a realistic multi-line German text AND on a boundary string where the weight
/// alone decides the line count, so a measure that slips back to Normal goes red.</item>
/// </list>
/// Mutant teeth: body back to Normal (red: body pin), chip back to Normal (red: chip pin),
/// tag scale back to a fraction (red: the 1.0× pin in <c>NoteBadgeTests</c>), measure at
/// Normal while drawing SemiBold (red: the boundary height equality).
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class NoteSemiBoldReadabilityTests
{
    /// <summary>A realistic multi-line German note: three physical lines, each wrapping inside
    /// the text column at the stock sizes, so the height is more than one line everywhere.</summary>
    private const string GermanSample =
        "Medikamente um 8:00 - dazu ein volles Glas Wasser trinken.\n" +
        "Zurück um 14:00, danach ruft Anna an.\n" +
        "Physiotherapie am Donnerstag um 10:00 - bitte ein Taxi rufen.";

    /// <summary>The text sizes the height-equality theory reads: the current default, and the
    /// two sizes the owner said they would raise the setting to after seeing the renders.</summary>
    public static TheoryData<double> TextSizes => new() { 28.0, 36.0, 48.0 };

    // ── the drawn weights ──

    [StaFact]
    public void TheNoteText_IsDrawnSemiBold_InDisplayAndEdit()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow("semibold-body");
        window.SetTextSize(28);
        window.SetText(GermanSample);

        window.SetState(NoteState.Display);
        var display = window.BodyTextForTest;
        Check.WithCustomMessage("Display must draw the body text").That(display).IsNotNull();
        Check.WithCustomMessage("the body text must draw SemiBold in Display")
            .That(display!.FontWeight).IsEqualTo(FontWeights.SemiBold);

        window.SetState(NoteState.Edit);
        var edit = window.BodyTextForTest;
        Check.WithCustomMessage("Edit must draw the body text").That(edit).IsNotNull();
        Check.WithCustomMessage("the body text must draw SemiBold in Edit — Display and Edit share one block")
            .That(edit!.FontWeight).IsEqualTo(FontWeights.SemiBold);
    }

    [StaFact]
    public void TheChipText_IsDrawnSemiBold_AtTheMeasuredColumn()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow("semibold-chip");
        var texts = ChipTexts();
        var column = NoteLayout.ChipTextWidthDip(28, texts);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(texts[0], 28, column);

        var block = window.ChipTextForTest;
        Check.WithCustomMessage("the chip must draw its text").That(block).IsNotNull();
        Check.WithCustomMessage("the chip text must draw SemiBold")
            .That(block!.FontWeight).IsEqualTo(FontWeights.SemiBold);
        Check.WithCustomMessage("the chip must draw in the MEASURED column")
            .That(block.Width).IsEqualTo(column);
    }

    [StaFact]
    public void TheTagNumeral_IsDrawnSemiBold_AtTheBodySize()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow("semibold-tag");
        window.SetTextSize(28);
        var card = new Size(
            NoteLayout.CardWidthDip(28),
            NoteLayout.CardHeightDip(28, window.MeasureTextHeightDip(GermanSample), NoteTestBench.WorkArea.Height));
        var placement = NoteLayout.WithTagClearance(
            NoteLayout.Place(
                new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!,
            28);
        window.ApplyPlacement(placement);
        window.SetBadge(1);

        var label = window.BadgeLabelForTest;
        Check.WithCustomMessage("the badged note must draw the numeral").That(label).IsNotNull();
        Check.WithCustomMessage("the tag numeral must draw SemiBold")
            .That(label!.FontWeight).IsEqualTo(FontWeights.SemiBold);
        Check.WithCustomMessage("the numeral must read at 1.0× the body size")
            .That(label.FontSize).IsEqualTo(28.0);
    }

    // ── measured height == drawn height, at SemiBold ──

    [StaTheory]
    [MemberData(nameof(TextSizes))]
    public void MeasuredHeight_EqualsTheDrawnHeight_AtSemiBold(double size)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = NewWindow($"semibold-height-{(int)size}");
        window.SetTextSize(size);
        // A placement sized by the PRODUCTION Display derivation (D2: the reserved card), so
        // UpdateContent does not clip over the cap: the manager derives the card height from the
        // very measure under test, and avail == measured keeps both texts fully drawn.
        var card = NoteLayout.DisplayReservedSize(
            window.MeasureTextWidthDip(GermanSample, size),
            window.MeasureTextHeightDip(GermanSample, size));
        window.ApplyPlacement(NoteLayout.Place(
            new PhysicalPoint(1200, 1000), NoteSide.Right, card,
            NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!);

        // (1) A realistic multi-line German text: the manager derives the card height from
        // the very measure under test, so it must equal what the drawn block takes.
        double drawnSample = DrawnHeight(window, GermanSample, size);
        double measuredSample = window.MeasureTextHeightDip(GermanSample, size);
        Check.WithCustomMessage($"the drawn sample block must wrap past one line at {size} (test validity)")
            .That(drawnSample).IsStrictlyGreaterThan(NoteLayout.TextLineHeightDip(size));
        Check.WithCustomMessage(
                $"[{size}] measured {measuredSample} must equal the drawn {drawnSample} (German sample)")
            .That(measuredSample).IsCloseTo(drawnSample, 1e-6);

        // (2) A boundary string the weight ALONE decides: at SemiBold it overflows the
        // column and wraps to two lines, at Normal it still fits one. The equality above
        // would hold through a measure that slipped back to Normal on a text whose line
        // count the weight never moves; this one goes red if the measure draws Normal.
        string boundary = BoundaryText(size);
        double drawnBoundary = DrawnHeight(window, boundary, size);
        double measuredBoundary = window.MeasureTextHeightDip(boundary, size);
        double normalRef = NormalReferenceHeight(boundary, size);
        Check.WithCustomMessage(
                $"[{size}] the boundary text must wrap at SemiBold ({drawnBoundary}) and not at Normal ({normalRef}) (test validity)")
            .That(drawnBoundary).IsStrictlyGreaterThan(normalRef);
        Check.WithCustomMessage(
                $"[{size}] measured {measuredBoundary} must equal the drawn {drawnBoundary} on the weight-decided boundary text")
            .That(measuredBoundary).IsCloseTo(drawnBoundary, 1e-6);
    }

    // ── helpers ──

    /// <summary>A headless NoteWindow: built, Loaded-raised (Draw runs), never shown.</summary>
    private static NoteWindow NewWindow(string id)
    {
        var window = new NoteWindow(id);
        InvisibleTestWindow.Prepare(window);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        return window;
    }

    /// <summary>Sets the text, re-renders, and measures the DRAWN block at the text column
    /// width — the height the card actually shows. DesiredSize carries the block's (10 DIP
    /// top) margin; the measured block has none, so the margin comes back off before the
    /// comparison.</summary>
    private static double DrawnHeight(NoteWindow window, string text, double size)
    {
        window.SetText(text);
        window.UpdateContent();
        var block = window.BodyTextForTest;
        Check.WithCustomMessage("the window must draw the body text").That(block).IsNotNull();
        block!.Measure(new Size(NoteLayout.TextColumnWidthDip(size), double.PositiveInfinity));
        return block.DesiredSize.Height - block.Margin.Top - block.Margin.Bottom;
    }

    /// <summary>The height the SAME text takes at Normal weight, measured exactly the way
    /// <c>NoteWindow.MeasureTextHeightDip</c> measures — the reference a Normal-slipped measure
    /// would return for the boundary text.</summary>
    private static double NormalReferenceHeight(string text, double size)
    {
        double width = NoteLayout.TextColumnWidthDip(size);
        var block = new TextBlock
        {
            FontSize = size,
            FontFamily = Tokens.FontStandard,
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = NoteLayout.TextLineHeightDip(size),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Text = text,
            Width = width
        };
        block.Measure(new Size(width, double.PositiveInfinity));
        return block.DesiredSize.Height;
    }

    /// <summary>A run of one repeated letter that straddles the column: wide enough at
    /// SemiBold to wrap onto a second line, still one line at Normal. Uniform letters make the
    /// search exact; several letter choices give finer width steps, so some run always lands in
    /// the band — a miss is a test-validity failure, not a silent pass.</summary>
    private static string BoundaryText(double size)
    {
        double column = NoteLayout.TextColumnWidthDip(size);
        foreach (var letter in new[] { "n", "o", "m", "M" })
        {
            for (int k = 1; k <= 600; k++)
            {
                string candidate = string.Concat(Enumerable.Repeat(letter, k));
                double semiBold = TextWidth(candidate, size, null);
                double normal = TextWidth(candidate, size, FontWeights.Normal);
                if (semiBold > column && normal <= column)
                    return candidate;
            }
        }

        throw new InvalidOperationException(
            $"no boundary letter run found at {size} DIP (column {column}): the test's weight band assumptions broke");
    }

    /// <summary>The natural width of <paramref name="text"/> at <paramref name="fontSize"/>,
    /// at the drawn face (null weight) or at an explicit Normal weight — the same FormattedText
    /// walk the production measurements use.</summary>
    private static double TextWidth(string text, double fontSize, FontWeight? weight)
    {
        var face = weight is { } w
            ? new Typeface(Tokens.FontStandard, FontStyles.Normal, w, FontStretches.Normal)
            : NoteLayout.SemiBoldFace;
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, fontSize,
            Tokens.BrushSurfaceMedium, null, TextFormattingMode.Ideal, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>The chip's two texts in the current table's language, as the manager measures them.</summary>
    private static IReadOnlyList<string> ChipTexts() =>
    [
        NoteLayout.NoBreakBeforeDot(StringTable.Current.T("overlays.note.chip.removed")),
        NoteLayout.NoBreakBeforeDot(StringTable.Current.T("overlays.note.chip.discarded")),
    ];
}
