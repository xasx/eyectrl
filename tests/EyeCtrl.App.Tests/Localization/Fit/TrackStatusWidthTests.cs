namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Localization;
using EyeCtrl.ViewModels;
using EyeCtrl.Tests.TestSupport;

/// <summary>
/// T09: the TrackStatus window's rows fit its WIDTH on one line. <see cref="TrackStatusFitTests"/>
/// guards the heights only, and nothing guarded the 160 DIP width: a longer "Quality:" word or
/// "Distance:" label would be clipped by the fixed, NoResize window, with no wrap and no ellipsis.
///
/// <para>
/// The real window's content is hosted at 2.0 in <see cref="FitHost"/> at the window's own
/// 160×116, with every quality word (through the production <c>QualityWord</c>) and the widest
/// distance reading (through the production template). Each row's one-line width must fit the
/// stack's arranged width — the window less the border's padding.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class TrackStatusWidthTests
{
    private readonly ITestOutputHelper _output;

    public TrackStatusWidthTests(ITestOutputHelper output) => _output = output;

    /// <summary>The widest distance the strip shows: four digits.</summary>
    private const double WidestDistanceMm = 1000;

    [StaFact]
    public void EveryRow_FitsTheWindowWidth_InEnglish()
    {
        var english = StringTable.EmbeddedEnglish;
        var failures = new List<string>();
        foreach (var quality in Enum.GetValues<DistanceQuality>())
        {
            var verdict = Measure(
                english.T("track-status.title"),
                english.T("track-status.distance"),
                english.T("track-status.distance.mm", WidestDistanceMm),
                english.T("track-status.quality"),
                TrackStatusWindow.QualityWord(english, quality));
            _output.WriteLine($"{quality}: {verdict}");
            if (!verdict.Fits) failures.Add($"{quality}: {verdict}");
        }

        Check.WithCustomMessage("TrackStatus rows overrun the window width:\n  " + string.Join("\n  ", failures))
            .That(failures).IsEmpty();
    }

    [StaFact]
    public void AnOverLongQualityWord_FailsTheGate()
    {
        var english = StringTable.EmbeddedEnglish;
        var verdict = Measure(english.T("track-status.title"), english.T("track-status.distance"),
            english.T("track-status.distance.mm", WidestDistanceMm), english.T("track-status.quality"),
            "Ausgezeichnetsehrgut");
        _output.WriteLine(verdict.ToString());
        Check.WithCustomMessage("An over-long quality word passed: the width gate is vacuous.")
            .That(verdict.Fits).IsFalse();
    }

    [StaFact]
    public void AnOverLongTitle_FailsTheGate()
    {
        var english = StringTable.EmbeddedEnglish;
        var verdict = Measure("Verfolgungsstatusanzeige", english.T("track-status.distance"),
            english.T("track-status.distance.mm", WidestDistanceMm), english.T("track-status.quality"),
            TrackStatusWindow.QualityWord(english, DistanceQuality.Unknown));
        _output.WriteLine(verdict.ToString());
        Check.WithCustomMessage("An over-long title passed: the width gate is vacuous.")
            .That(verdict.Fits).IsFalse();
    }

    /// <summary>Lays the real window's content out at 2.0 with the given texts and measures every row.</summary>
    internal static FitVerdict Measure(string title, string distanceLabel, string distance, string qualityLabel, string quality)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TrackStatusWindow();
        window.TitleLabel.Text = title;
        window.DistanceLabel.Text = distanceLabel;
        window.DistanceText.Text = distance;
        window.QualityLabel.Text = qualityLabel;
        window.QualityText.Text = quality;

        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: window.Width, height: window.Height, inheritFrom: window);

        var stack = (StackPanel)window.TitleLabel.Parent;
        var room = stack.ActualWidth;
        var rows = new (string Name, double Width)[]
        {
            ("title", FitText.NaturalWidth(window.TitleLabel, title)),
            ("distance", FitText.NaturalWidth(window.DistanceLabel, distanceLabel) + FitText.NaturalWidth(window.DistanceText, distance)),
            ("quality", FitText.NaturalWidth(window.QualityLabel, qualityLabel) + FitText.NaturalWidth(window.QualityText, quality)),
        };
        var widest = rows.MaxBy(r => r.Width);
        return new FitVerdict(widest.Width <= room,
            string.Join("; ", rows.Select(r => $"{r.Name} {FitText.Dip(r.Width)}")) + $" vs room {FitText.Dip(room)}",
            FitText.ResolvedFace(window.TitleLabel));
    }
}
