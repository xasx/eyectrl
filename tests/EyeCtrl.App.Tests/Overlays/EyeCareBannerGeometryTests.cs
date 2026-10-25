namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The eye-care banner's geometry (B7): the rings' arithmetic — r50, a ≥ 20 DIP dead gap, fully
/// inset and clear of the toolbar band, at DPI 1 and 2 — and, on the real window, that the drawn
/// circles sit on the registered dwell targets and the captions fit the window.
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
    private static readonly double Toolbar = new ToolbarSettings().Size;

    [Fact]
    public void ComputeDwellGeometry_At100Percent_MatchesTheShippedLayout()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth: 1920, toolbarSizeDip: Toolbar, scaleX: 1.0, scaleY: 1.0);

        // Start is the right-hand disc: 20 DIP inset + its r50. Snooze is one 124 DIP pitch away.
        Check.That(g.Start.X).IsCloseTo(1920 - 70, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 194, 0.001);
        // Below the toolbar band (size + 40) plus an 8 DIP gap, then its own radius.
        Check.That(g.Start.Y).IsCloseTo(120 + 40 + 8 + 50, 0.001);
        Check.That(g.Snooze.Y).IsCloseTo(g.Start.Y, 0.001);
        Check.That(g.Radius).IsCloseTo(50, 0.001);
    }

    [Fact]
    public void ComputeDwellGeometry_At200Percent_ScalesEverything()
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth: 1920, toolbarSizeDip: 108, scaleX: 2.0, scaleY: 2.0);

        Check.That(g.Start.X).IsCloseTo((1920 - 70) * 2, 0.001);
        Check.That(g.Snooze.X).IsCloseTo((1920 - 194) * 2, 0.001);
        Check.That(g.Start.Y).IsCloseTo((108 + 40 + 8 + 50) * 2, 0.001);
        Check.That(g.Radius).IsCloseTo(100, 0.001);
    }

    /// <summary>
    /// B7's three geometric requirements, at both DPIs and at a small screen with the largest
    /// toolbar: both hit circles fully on-screen, their TOP clear of the toolbar band, and a dead
    /// gap of at least 20 DIP between the two different actions.
    /// </summary>
    [Theory]
    [InlineData(1920.0, 1200.0, 120.0, 1.0)]
    [InlineData(1920.0, 1200.0, 108.0, 2.0)]
    [InlineData(1366.0, 768.0, 200.0, 1.0)]
    [InlineData(1366.0, 768.0, 120.0, 2.0)]
    public void TheRings_AreFullyInset_ClearOfTheBand_AndAtLeast20DipApart(
        double screenDipWidth, double screenDipHeight, double toolbarSizeDip, double scale)
    {
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth, toolbarSizeDip, scale, scale);
        var screenWidthPx = screenDipWidth * scale;
        var screenHeightPx = screenDipHeight * scale;

        // The dead gap between the two HIT circles (what the user aims at).
        var gap = Math.Abs(g.Start.X - g.Snooze.X) - 2 * g.Radius;
        Check.WithCustomMessage($"the two rings are only {gap:0.#} physical px apart")
            .That(gap).IsGreaterOrEqualThan(20 * scale);

        // Fully on-screen: neither circle crosses an edge.
        Check.That(g.Start.X + g.Radius).IsLessOrEqualThan(screenWidthPx);
        Check.That(g.Snooze.X - g.Radius).IsGreaterOrEqualThan(0);
        Check.That(g.Start.Y - g.Radius).IsGreaterOrEqualThan(0);
        Check.That(g.Start.Y + g.Radius).IsLessOrEqualThan(screenHeightPx);

        // Fully below the toolbar band (toolbar size + 40 DIP), not merely off the top edge.
        Check.WithCustomMessage("a ring's top edge is inside the toolbar band")
            .That(g.Start.Y - g.Radius).IsGreaterOrEqualThan((toolbarSizeDip + 40) * scale);
    }

    /// <summary>
    /// The band above the rings is the app's real toolbar band, not a number of ours that drifted:
    /// <see cref="RepeatModeController.BuildToolbarBandRect"/> is the same rect repeat mode uses to
    /// suppress the toolbar row. (The theory above pins the arithmetic at DPI 1 and 2; this pins
    /// the shared value at the machine's real DPI, read-only.)
    /// </summary>
    [Fact]
    public void TheRingsTop_ClearsTheRealToolbarBandRect()
    {
        var band = RepeatModeController.BuildToolbarBandRect(Toolbar);
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            SystemParameters.PrimaryScreenWidth, Toolbar, DpiHelper.ScaleX, DpiHelper.ScaleY);

        Check.WithCustomMessage(
                $"the ring top is {g.Start.Y - g.Radius:0.#} px, inside the {band.Height:0.#} px toolbar band")
            .That(g.Start.Y - g.Radius).IsGreaterOrEqualThan(band.Height);
    }

    [StaFact]
    public void TheWindow_DrawsEachRingOnItsDwellTarget_AndTheCaptionsFit()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var (language, table) in new[]
                 {
                     ("English", StringTable.EmbeddedEnglish),
                     ("shipped German", ShippedGerman.Load()),
                 })
        {
            In(table, () => AssertRingsOnTargetsAndCaptionsFit(language));
        }
    }

    private static void AssertRingsOnTargetsAndCaptionsFit(string language)
    {
        var window = new EyeCareBannerWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: window.Height, inheritFrom: window);

        // The message strip keeps its decided 96 DIP height.
        var strip = ((Grid)root).Children.OfType<Border>().Single();
        Check.That(strip.ActualHeight).IsCloseTo(96, 0.01);

        var targets = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth: 1920, toolbarSizeDip: Toolbar, scaleX: 1.0, scaleY: 1.0);
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

        // The discs are drawn at the r50 hit radius (not a smaller glyph-like circle).
        Check.That(((FrameworkElement)window.SnoozeRing.Parent).ActualWidth)
            .IsCloseTo(EyeCareBannerWindow.RingSize, 0.01);
        Check.That(((FrameworkElement)window.StartRing.Parent).ActualHeight)
            .IsCloseTo(EyeCareBannerWindow.RingSize, 0.01);

        // The captions hang below the discs and still fit the window with padding.
        foreach (var (name, caption) in new (string, FrameworkElement)[]
                 {
                     ("snooze", window.SnoozeLabel),
                     ("start", window.StartLabel),
                 })
        {
            var bottom = BottomOf(caption, root);
            Check.WithCustomMessage(
                    $"{language}: the {name} caption ends at {bottom:0.#} DIP; the window ends at "
                    + $"{window.Height:0.#} DIP, leaving less than the 4 DIP of bottom padding.")
                .That(bottom).IsLessOrEqualThan(window.Height - 4.0);
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
