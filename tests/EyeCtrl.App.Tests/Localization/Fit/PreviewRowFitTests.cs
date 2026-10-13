namespace EyeCtrl.App.Tests.Localization.Fit;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// D15's PV1 acceptance: the longest English ROWS, composed by the production window itself
/// (<see cref="AdaptationPreviewWindow.BuildContentForTest"/> over a worst-case snapshot — every
/// band at full fill, every absolute at its widest display-rounded pair in the reachable
/// direction), fit the ≤360 DIP card at scale 2.0 with each row's ink ≥
/// <see cref="PreviewRowFit.RequiredClearanceDip"/> from its fill bar. The brief's example row
/// ("Tolerance" with its offset value) predates the landed composition; the equivalent worst set
/// is the snapshot below — whichever of the four measures widest is the longest English row, and
/// is logged as such.
/// </summary>
[Collection("WpfApplication")]
public class PreviewRowFitTests
{
    private readonly ITestOutputHelper _output;

    public PreviewRowFitTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every row at its worst, in the reachable direction the knob policies compose: dwell and
    /// geometry at a full (tightening) fill; tolerance UP-only from its hand-set baseline at the
    /// 200 px clamp's edge ("199.9 → 187.5 px"); smoothing DOWN-only from its factor ceiling
    /// ("0.87 → 0.99 factor"). These are the widest strings the display-rounded renderer can emit
    /// — the same pairs <see cref="PreviewRowFit"/>'s per-key samples and opposite-slot worsts are
    /// derived from (review F1).
    /// </summary>
    internal static AdaptationPreviewSnapshot WorstEnglishSnapshot() => new(
    [
        new AdaptationPreviewRow(AdaptationKnob.DwellTime, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: true, AbsoluteValue: null, BaselineValue: null, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.FixationRadius, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: 199.9, BaselineValue: 187.5, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.Smoothing, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: 0.87, BaselineValue: 0.99, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.TargetGeometry, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: null, BaselineValue: null, IsHzUnit: false),
    ],
        AnythingActive: true);

    /// <summary>
    /// Review F1's renderer pin: the composition chain never rounds (Step accumulates binary-inexact
    /// fill steps, EffectiveValue clamps only, StringTable formats bare), so the ABSOLUTE arms must
    /// round for display — a raw 0.49999999999999994 must render "0.5", never its 17-digit tail.
    /// The band arm's Math.Round discipline, extended to the absolutes.
    /// </summary>
    [Fact]
    public void ValueTextFor_RoundsAbsolutesForDisplay_NoFloatTails()
    {
        var strings = StringTable.EmbeddedEnglish;

        // The review's example pair: a binary-inexact composed factor against an
        // exactly-representable 0.625 baseline (a legal hand-edited [0, 1] setting; banker's
        // rounding takes 0.625 to 0.62).
        var factor = new AdaptationPreviewRow(AdaptationKnob.Smoothing, 0.5, 0.0, false,
            AbsoluteValue: 0.49999999999999994, BaselineValue: 0.625, IsHzUnit: false);
        Check.That(AdaptationPreviewWindow.ValueTextFor(strings, factor)).IsEqualTo("0.5 → 0.62 factor");

        // Tolerance at the 200 px clamp's edge, both values one float-tail short of a decimal.
        var tolerance = new AdaptationPreviewRow(AdaptationKnob.FixationRadius, 0.5, 0.0, false,
            AbsoluteValue: 199.99999999999997, BaselineValue: 187.49999999999997, IsHzUnit: false);
        Check.That(AdaptationPreviewWindow.ValueTextFor(strings, tolerance)).IsEqualTo("200 → 187.5 px");

        // The Hz arm shares the smoothing precision (2 decimals), not the pixel one.
        var hz = new AdaptationPreviewRow(AdaptationKnob.Smoothing, 0.5, 0.0, false,
            AbsoluteValue: 12.749999999999998, BaselineValue: 19.999999, IsHzUnit: true);
        Check.That(AdaptationPreviewWindow.ValueTextFor(strings, hz)).IsEqualTo("12.75 → 20 Hz");
    }

    [StaFact]
    public void TheWorstEnglishRows_FitThePanelAtTwo()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new AdaptationPreviewWindow();
        window.BuildContentForTest(WorstEnglishSnapshot(), "Bottom");

        // The production card under the window's own MaxWidth clamp: the root grid in a panel of
        // that width, so the rows' star column shrinks exactly as the shown panel's would.
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var clamp = new StackPanel { Width = window.MaxWidth };
        clamp.Children.Add(root);
        using var host = FitHost.Open(clamp, inheritFrom: window);

        Check.That(window.Card.ActualWidth).IsLessOrEqualThan(window.MaxWidth);

        // A row is the grid with both text slots; the bar is a grid with none.
        var rows = FitText.Descendants<Grid>(root)
            .Where(g => g.Children.OfType<TextBlock>().Count() == 2)
            .ToList();
        Check.WithCustomMessage("The built panel does not hold four two-text rows.").That(rows).HasSize(4);

        double room = PreviewRowFit.RoomFor(window);
        var failures = new System.Collections.Generic.List<string>();
        var widest = (Width: 0.0, Detail: "");
        foreach (var row in rows)
        {
            var measured = PreviewRowFit.MeasureRow(row, room);
            _output.WriteLine(measured.Detail);
            if (!measured.Fits) failures.Add(measured.Detail);
            if (measured.Demand > widest.Width) widest = (measured.Demand, $"\"{measured.Label}\" + \"{measured.Value}\"");
        }

        _output.WriteLine($"The longest English row: {widest.Detail} at {FitText.Dip(widest.Width)} DIP.");
        Check.WithCustomMessage(
                $"A worst-case English row does not fit the panel at 2.0 (rule: ink ≥ "
                + $"{PreviewRowFit.RequiredClearanceDip} DIP from the bar, row ≤ room):\n  "
                + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }
}
