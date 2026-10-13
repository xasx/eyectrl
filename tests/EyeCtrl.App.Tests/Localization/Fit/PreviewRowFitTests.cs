namespace EyeCtrl.App.Tests.Localization.Fit;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// D15's PV1 acceptance: the longest English ROWS, composed by the production window itself
/// (<see cref="AdaptationPreviewWindow.BuildContentForTest"/> over a worst-case snapshot — every
/// band at full fill, every absolute at its unit's settings-range ceiling), fit the ≤360 DIP card
/// at scale 2.0 with each row's ink ≥ <see cref="PreviewRowFit.RequiredClearanceDip"/> from its
/// fill bar. The brief's example row ("Tolerance" with its offset value) predates the landed
/// composition; the equivalent worst set is the snapshot below — whichever of the four measures
/// widest is the longest English row, and is logged as such.
/// </summary>
[Collection("WpfApplication")]
public class PreviewRowFitTests
{
    private readonly ITestOutputHelper _output;

    public PreviewRowFitTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Every row at its worst: dwell and geometry at a full (tightening) fill, tolerance drifting
    /// at its 200 px ceiling, smoothing at its factor ceiling — the numbers
    /// <see cref="PreviewRowFit"/>'s per-key samples and opposite-slot worsts are built from too.
    /// </summary>
    internal static AdaptationPreviewSnapshot WorstEnglishSnapshot() => new(
    [
        new AdaptationPreviewRow(AdaptationKnob.DwellTime, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: true, AbsoluteValue: null, BaselineValue: null, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.FixationRadius, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: 187.5, BaselineValue: 200.0, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.Smoothing, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: 0.875, BaselineValue: 1.0, IsHzUnit: false),
        new AdaptationPreviewRow(AdaptationKnob.TargetGeometry, TotalFill: 1.0, LearnedFill: 0.5,
            TighteningDirection: false, AbsoluteValue: null, BaselineValue: null, IsHzUnit: false),
    ],
        AnythingActive: true);

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
