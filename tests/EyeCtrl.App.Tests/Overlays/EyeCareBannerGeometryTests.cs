namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The eye-care banner's geometry: the dwell targets' arithmetic (below) and, since T13 fixup-03,
/// the strip's height against the captions and the rings on the real window.
///
/// <para>
/// The layout gate activates the shipped German and restores English, so the class sits in the
/// exclusive "StringTable Tests (App)" collection; the window is hosted headlessly at the user's
/// 2.0 (<see cref="FitHost"/>) and never shown.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class EyeCareBannerGeometryTests
{
    [Fact]
    public void ComputeDwellGeometry_At100Percent_MatchesXamlButtonCenters()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1920, scaleX: 1.0, scaleY: 1.0);
        Check.That(g.Start.X).IsCloseTo(1920 - 52, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132, 0.001);
        Check.That(g.Start.Y).IsCloseTo(40, 0.001);
        Check.That(g.Radius).IsCloseTo(40, 0.001);
    }

    [Fact]
    public void ComputeDwellGeometry_At150Percent_ScalesEverything()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1280, scaleX: 1.5, scaleY: 1.5);
        Check.That(g.Start.X).IsCloseTo(1920 - 52 * 1.5, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 132 * 1.5, 0.001);
        Check.That(g.Start.Y).IsCloseTo(40 * 1.5, 0.001);
        Check.That(g.Radius).IsCloseTo(40 * 1.5, 0.001);
    }

    /// <summary>
    /// T13 fixup-03 (the user-approved 96-DIP strip): measured on the real window at the user's
    /// 2.0, in English and in the shipped German — every caption under a ring lies inside the
    /// strip with at least 4 DIP of bottom padding, and both rings' DRAWN centres still coincide
    /// with their dwell targets (<see cref="EyeCareBannerWindow.ComputeDwellGeometry"/>: 40 DIP
    /// from the top, 1920 − 52 / 1920 − 132 from the left). The strip grew DOWNWARD (80 → 96)
    /// while the rings' panel stayed top-anchored at its original 8 DIP, so the circles the user
    /// aims at did not move; at 80 the captions hung ~7.5 DIP below the strip.
    /// </summary>
    [StaFact]
    public void TheStrip_ContainsTheRingCaptions_AndTheRingsStayOnTheirDwellTargets()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var (language, table) in new[]
                 {
                     ("English", StringTable.EmbeddedEnglish),
                     ("shipped German", ShippedGerman.Load()),
                 })
        {
            In(table, () => AssertStripContainsTheCaptionsWithoutMovingTheRings(language));
        }
    }

    private static void AssertStripContainsTheCaptionsWithoutMovingTheRings(string language)
    {
        var window = new EyeCareBannerWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: window.Height, inheritFrom: window);

        var strip = ((Grid)root).Children.OfType<Border>().Single();
        Check.WithCustomMessage($"{language}: the strip is {strip.ActualHeight:0.#} DIP, not the approved 96.")
            .That(strip.ActualHeight).IsCloseTo(96, 0.01);

        var targets = EyeCareBannerWindow.ComputeDwellGeometry(screenDipWidth: 1920, scaleX: 1.0, scaleY: 1.0);
        foreach (var (name, ring, target) in new[]
                 {
                     ("snooze", (FrameworkElement)window.SnoozeRing, targets.Snooze),
                     ("start", window.StartRing, targets.Start),
                 })
        {
            var centre = CentreOf(ring, root);
            Check.WithCustomMessage(
                    $"{language}: the {name} ring is drawn at ({centre.X:0.#}, {centre.Y:0.#}), not on its dwell "
                    + $"target ({target.X:0.#}, {target.Y:0.#}) — the circles the user aims at moved.")
                .That(centre.X).IsCloseTo(target.X, 0.01);
            Check.That(centre.Y).IsCloseTo(target.Y, 0.01);
        }

        foreach (var (name, caption) in new (string, FrameworkElement)[]
                 {
                     ("snooze", window.SnoozeLabel),
                     ("start", window.StartLabel),
                 })
        {
            var bottom = BottomOf(caption, root);
            Check.WithCustomMessage(
                    $"{language}: the {name} caption ends at {bottom:0.#} DIP; the strip ends at "
                    + $"{strip.ActualHeight:0.#} DIP, leaving less than the 4 DIP of bottom padding.")
                .That(bottom).IsLessOrEqualThan(strip.ActualHeight - 4.0);
        }
    }

    /// <summary>An element's centre in <paramref name="root"/>'s DIP coordinates, exact (no pixel snapping).</summary>
    private static Point CentreOf(FrameworkElement element, Visual root)
    {
        var box = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        return new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    /// <summary>The bottom edge of <paramref name="element"/> in <paramref name="root"/>'s DIP coordinates.</summary>
    private static double BottomOf(FrameworkElement element, UIElement root) =>
        element.TranslatePoint(new Point(0, element.ActualHeight), root).Y;

    /// <summary>Runs <paramref name="assert"/> under <paramref name="table"/>, restoring English after.</summary>
    private static void In(StringTable table, Action assert)
    {
        StringTable.Activate(table);
        try
        {
            assert();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }
}
