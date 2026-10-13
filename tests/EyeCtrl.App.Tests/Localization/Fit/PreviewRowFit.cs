namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// PV1 (D15): one adaptation-preview row, measured at 2.0 in the production window's own card
/// geometry — <see cref="PreviewRowFitTests"/> measures the whole worst-case panel; the class
/// measurer below measures each key's text in its slot of one <see cref="AdaptationPreviewWindow.RowForTest"/>
/// row (the A68 <c>with { Label = text }</c> pattern). The room is never restated: it is read off
/// the built window (<c>MaxWidth</c> less the card's padding and border, which is why the class is
/// theme-dependent — <see cref="FitClassThemeTests"/>), and the row is the production grid.
///
/// <para>
/// A row composes TWO slots — knob label left, direction word + right-aligned value right — so a
/// probe text is measured with the OPPOSITE slot at its widest shipped text (English and the
/// German draft both, since the same row geometry carries either language). The widest value is
/// composed at each unit's settings-range ceiling (tolerance px 10–200, EMA factor 0–1, One-Euro
/// cutoff 0.1–20 Hz) and at a full band fill (the row's fill clamps at 100 %).
/// </para>
///
/// <para>
/// The rule (D15): the row fits its ≤360 DIP panel, and each text keeps its ink ≥
/// <see cref="RequiredClearanceDip"/> from the fill bar on its side. The layout box is measured,
/// never narrower than the ink, so the rule is never more lenient than what is drawn. An over-long
/// probe shrinks the bar's star column to nothing, drives the bar over the value, and fails on the
/// clearance — the class has teeth.
/// </para>
/// </summary>
internal static class PreviewRowFit
{
    /// <summary>D15: each side's text keeps ≥ this many DIP between its box and the fill bar.</summary>
    internal const double RequiredClearanceDip = 2.0;

    private const string KnobPrefix = "overlays.adaptationPreview.knob.";
    private const string BandKey = "overlays.adaptationPreview.value.band";
    private const string AbsoluteKey = "overlays.adaptationPreview.value.absolute";

    private static readonly string[] KnobKeys =
    [
        "overlays.adaptationPreview.knob.dwell",
        "overlays.adaptationPreview.knob.tolerance",
        "overlays.adaptationPreview.knob.smoothing",
        "overlays.adaptationPreview.knob.geometry",
    ];

    private static readonly string[] BandDirectionKeys =
    [
        "overlays.adaptationPreview.direction.shorter",
        "overlays.adaptationPreview.direction.longer",
        "overlays.adaptationPreview.direction.bigger",
    ];

    // Each unit's worst absolute pair, at its knob's settings-range ceiling: tolerance px tops out
    // at 200 (one decimal composes under a fill), the EMA factor at 1, One-Euro's cutoff at 20 Hz.
    private static readonly (double Current, double Baseline, string UnitKey)[] UnitWorsts =
    [
        (187.5, 200.0, "overlays.adaptationPreview.unit.px"),
        (0.875, 1.0, "overlays.adaptationPreview.unit.factor"),
        (18.75, 20.0, "overlays.adaptationPreview.unit.hz"),
    ];

    /// <summary>One measured row: its room, its demand, and the two clearances the class rules on.</summary>
    internal sealed record RowMeasure(
        double Room, double Demand, double LabelToBar, double BarToValue, string Label, string Value, string Face)
    {
        public bool Fits => Demand <= Room + 1e-6
                            && LabelToBar >= RequiredClearanceDip
                            && BarToValue >= RequiredClearanceDip;

        public double Margin => Math.Min(LabelToBar, BarToValue) - RequiredClearanceDip;

        public string Detail => $"row {FitText.Dip(Demand)} DIP vs room {FitText.Dip(Room)}; label→bar "
            + $"{FitText.Dip(LabelToBar)} DIP, bar→value {FitText.Dip(BarToValue)} DIP (ink must keep "
            + $"≥ {FitText.Dip(RequiredClearanceDip)}); label \"{Label}\", value \"{Value}\"";
    }

    /// <summary>
    /// The card's inner width — the room a row laid out in the ≤360 DIP panel actually gets, read
    /// off the production window (MaxWidth less the card's padding and border; the border is a
    /// themed token, so the room moves with the theme).
    /// </summary>
    internal static double RoomFor(AdaptationPreviewWindow window) =>
        window.MaxWidth
        - window.Card.Padding.Left - window.Card.Padding.Right
        - window.Card.BorderThickness.Left - window.Card.BorderThickness.Right;

    /// <summary>
    /// The class measurer: the key's text in its slot of one production row, the opposite slot at
    /// its widest shipped text (both drafts), hosted at 2.0 in a panel of the card's room.
    /// </summary>
    internal static FitVerdict Measure(string key, string text)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new AdaptationPreviewWindow();
        double room = RoomFor(window);

        // A probe in the production row's own font (the label's size, the family the window
        // lends an arranged row) for picking the opposite slot's widest text.
        var font = LabelOf(AdaptationPreviewWindow.RowForTest("x", "x"));
        font.FontFamily = window.FontFamily;
        bool inLabel = key.StartsWith(KnobPrefix, StringComparison.Ordinal);
        string label = inLabel ? text : WidestLabel(font);
        string value = inLabel ? WidestValue(font) : Compose(key, text);

        var row = AdaptationPreviewWindow.RowForTest(label, value);
        var panel = new StackPanel { Width = room };
        panel.Children.Add(row);
        using var host = FitHost.Open(panel, inheritFrom: window);

        var measured = MeasureRow(row, room);
        return new FitVerdict(measured.Fits, measured.Detail, measured.Face, measured.Margin);
    }

    /// <summary>
    /// The clearances of an ARRANGED production row: label-box right edge to the bar's left edge,
    /// bar's right edge to the value-box left edge, in the row's own coordinates.
    /// </summary>
    internal static RowMeasure MeasureRow(Grid row, double room)
    {
        var label = LabelOf(row);
        var value = ValueOf(row);
        var bar = BarOf(row);

        double labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), row).X;
        double barLeft = bar.TranslatePoint(new Point(0, 0), row).X;
        double valueLeft = value.TranslatePoint(new Point(0, 0), row).X;
        return new RowMeasure(room, row.DesiredSize.Width, barLeft - labelRight,
            valueLeft - (barLeft + bar.ActualWidth), label.Text, value.Text, FitText.ResolvedFace(value));
    }

    /// <summary>
    /// The composed samples for the two PV1 keys that take arguments (the value templates): the
    /// template formatted at each unit's worst pair / a full band, with every shipped spelling of
    /// the unit or direction word — the harness cannot tell which table an item's template came
    /// from, so the wider spelling is measured for both. Numbers are invariant (a real German row
    /// formats "187,5", the same glyph count as "187.5").
    /// </summary>
    internal static IReadOnlyList<string> SamplesFor(string key, string template)
    {
        var samples = new List<string>();
        if (key == AbsoluteKey)
        {
            foreach (var (current, baseline, unitKey) in UnitWorsts)
                foreach (var unit in DraftTexts([unitKey]))
                    samples.Add(string.Format(CultureInfo.InvariantCulture, template, current, baseline, unit));
            return samples;
        }

        if (key == BandKey)
        {
            foreach (var directionKey in BandDirectionKeys)
                foreach (var direction in DraftTexts([directionKey]))
                    samples.Add(string.Format(CultureInfo.InvariantCulture, template, 100, direction));
            return samples;
        }

        throw new InvalidOperationException($"{key} has no PV1 sample rule; only the value templates take arguments.");
    }

    /// <summary>
    /// The value a probe composes into when it is not the whole value already: direction words ride
    /// a full band (the fill clamps at 100 %), unit words ride their unit's worst absolute pair,
    /// at-baseline and both value templates (and every over-long probe) are the value themselves.
    /// </summary>
    private static string Compose(string key, string text)
    {
        var english = StringTable.EmbeddedEnglish;
        if (BandDirectionKeys.Contains(key))
            return english.T(BandKey, 100, text);

        var unit = UnitWorsts.FirstOrDefault(u => u.UnitKey == key);
        return unit.UnitKey is null
            ? text
            : english.T(AbsoluteKey, unit.Current, unit.Baseline, text);
    }

    /// <summary>The widest shipped knob label — what a value-slot probe is measured against.</summary>
    private static string WidestLabel(TextBlock font) =>
        Widest(DraftTexts(KnobKeys), font);

    /// <summary>
    /// The widest shipped composed value — what a label-slot probe is measured against: both
    /// drafts' bands at full fill, absolutes at each unit's worst, and at-baseline lines.
    /// </summary>
    private static string WidestValue(TextBlock font) =>
        Widest(ValueCandidates(), font);

    private static IEnumerable<string> ValueCandidates()
    {
        var english = StringTable.EmbeddedEnglish;
        var german = GermanDraft.Table;
        foreach (var directionKey in BandDirectionKeys)
        {
            yield return english.T(BandKey, 100, english.T(directionKey));
            yield return german.T(BandKey, 100, german.T(directionKey));
        }

        foreach (var (current, baseline, unitKey) in UnitWorsts)
        {
            yield return english.T(AbsoluteKey, current, baseline, english.T(unitKey));
            yield return german.T(AbsoluteKey, current, baseline, german.T(unitKey));
        }

        yield return english.T("overlays.adaptationPreview.direction.atBaseline");
        yield return german.T("overlays.adaptationPreview.direction.atBaseline");
    }

    /// <summary>Both drafts' text for each key: the row geometry carries either language.</summary>
    private static IEnumerable<string> DraftTexts(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            yield return StringTable.EmbeddedEnglish.T(key);
            yield return GermanDraft.TextOf(key, "");
        }
    }

    private static string Widest(IEnumerable<string> texts, TextBlock font) =>
        texts.MaxBy(t => FitText.NaturalWidth(font, t))!;

    private static TextBlock LabelOf(Grid row) => row.Children.OfType<TextBlock>().First();

    private static TextBlock ValueOf(Grid row) => row.Children.OfType<TextBlock>().Last();

    private static FrameworkElement BarOf(Grid row) =>
        row.Children.OfType<FrameworkElement>().Single(child => child is not TextBlock);
}
