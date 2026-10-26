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
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The eye-care banner's geometry (B7 + fixup-01 F2): the rings' arithmetic — r50, a ≥ 20 DIP dead
/// gap, fully inset and clear of the toolbar's REAL band for all four docks — and, on the real
/// window, that the drawn circles sit on the registered dwell targets and the captions fit.
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
    private static ToolbarSettings TopToolbar(double size = 120) =>
        new() { IsDocked = true, DockedEdge = "Top", Size = size };

    private static ToolbarSettings DockedAt(string edge, double size = 120) =>
        new() { IsDocked = true, DockedEdge = edge, Size = size };

    [Fact]
    public void ComputeDwellGeometry_At100Percent_MatchesTheShippedLayout()
    {
        var toolbar = TopToolbar();
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth: 1920, toolbar, scaleX: 1.0, scaleY: 1.0);

        // Start is the right-hand disc: 20 DIP inset + its r50. Snooze is one 124 DIP pitch away.
        Check.That(g.Start.X).IsCloseTo(1920 - 70, 0.001);
        Check.That(g.Snooze.X).IsCloseTo(1920 - 194, 0.001);
        // Below the real toolbar window (size + band margins + padding) plus an 8 DIP gap, then
        // its own radius.
        Check.That(g.Start.Y).IsCloseTo(
            EyeCarePlacement.ToolbarWindowThicknessDip(120) + 8 + 50, 0.001);
        Check.That(g.Snooze.Y).IsCloseTo(g.Start.Y, 0.001);
        Check.That(g.Radius).IsCloseTo(50, 0.001);
    }

    [Fact]
    public void ComputeDwellGeometry_At200Percent_ScalesEverything()
    {
        var toolbar = TopToolbar(108);
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth: 1920, toolbar, scaleX: 2.0, scaleY: 2.0);

        Check.That(g.Start.X).IsCloseTo((1920 - 70) * 2, 0.001);
        Check.That(g.Snooze.X).IsCloseTo((1920 - 194) * 2, 0.001);
        Check.That(g.Start.Y).IsCloseTo(
            (EyeCarePlacement.ToolbarWindowThicknessDip(108) + 8 + 50) * 2, 0.001);
        Check.That(g.Radius).IsCloseTo(100, 0.001);
    }

    /// <summary>
    /// The placement's band arithmetic against the REAL toolbar window: the band extent is the
    /// rendered button row, the window thickness adds the strip's 4 DIP padding on each side.
    /// The window is only measured, never shown (this file is headless-listed).
    /// </summary>
    [StaFact]
    public void TheCodedBand_MatchesTheRealToolbarWindow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        foreach (var size in new[] { 108.0, 120.0, 200.0 })
        {
            var toolbar = new ToolbarWindow();
            toolbar.Initialize(size, 700, includeReset: true);

            var band = toolbar.ButtonCanvasForTest.Height;
            var padding = toolbar.StripRoot.Padding.Top + toolbar.StripRoot.Padding.Bottom;

            Check.WithCustomMessage($"the coded band extent drifted from the real Canvas at size {size}.")
                .That(EyeCarePlacement.ToolbarBandExtentDip(size)).IsCloseTo(band, 0.001);
            Check.WithCustomMessage($"the coded window thickness drifted from Canvas + padding at size {size}.")
                .That(EyeCarePlacement.ToolbarWindowThicknessDip(size)).IsCloseTo(band + padding, 0.001);
            Check.That(ToolbarWindow.BandPaddingDip)
                .IsCloseTo(toolbar.StripRoot.Padding.Top, 0.001);
            Check.That(toolbar.StripRoot.Padding.Left).IsCloseTo(toolbar.StripRoot.Padding.Top, 0.001);
        }
    }

    /// <summary>
    /// B7 fixup-01 F2: for EVERY dock, both r50 hit circles are fully on-screen, ≥ 20 DIP apart,
    /// and outside the toolbar's occupied band (top/bottom: a horizontal strip; left/right: a
    /// vertical one). At both DPIs and on a small screen.
    /// </summary>
    [Theory]
    [InlineData("Top", 1920.0, 1200.0, 120.0, 1.0)]
    [InlineData("Top", 1920.0, 1200.0, 108.0, 2.0)]
    [InlineData("Bottom", 1920.0, 1200.0, 108.0, 2.0)]
    [InlineData("Left", 1920.0, 1200.0, 120.0, 1.0)]
    [InlineData("Right", 1920.0, 1200.0, 108.0, 2.0)]
    [InlineData("Right", 1366.0, 768.0, 200.0, 1.0)]
    [InlineData("Bottom", 1366.0, 768.0, 120.0, 2.0)]
    public void TheRings_ClearTheToolbarsRealBand_ForEveryDock(
        string edge, double screenDipWidth, double screenDipHeight, double toolbarSize, double scale)
    {
        var toolbar = DockedAt(edge, toolbarSize);
        var g = EyeCareBannerWindow.ComputeDwellGeometry(
            screenDipWidth, toolbar, scale, scale);
        var w = screenDipWidth;
        var h = screenDipHeight;
        var thickness = EyeCarePlacement.ToolbarWindowThicknessDip(toolbarSize);

        // The dead gap between the two HIT circles (what the user aims at).
        var gap = Math.Abs(g.Start.X - g.Snooze.X) / scale - 2 * EyeCareBannerWindow.HitRadiusDip;
        Check.WithCustomMessage($"the two rings are only {gap:0.#} DIP apart")
            .That(gap).IsGreaterOrEqualThan(20);

        // Fully on-screen: neither circle crosses an edge.
        Check.That(g.Start.X / scale + EyeCareBannerWindow.HitRadiusDip).IsLessOrEqualThan(w);
        Check.That(g.Snooze.X / scale - EyeCareBannerWindow.HitRadiusDip).IsGreaterOrEqualThan(0);
        Check.That(g.Start.Y / scale - EyeCareBannerWindow.HitRadiusDip).IsGreaterOrEqualThan(0);
        Check.That(g.Start.Y / scale + EyeCareBannerWindow.HitRadiusDip).IsLessOrEqualThan(h);

        // Outside the toolbar's occupied band for this dock.
        var start = new Point(g.Start.X / scale, g.Start.Y / scale);
        var snooze = new Point(g.Snooze.X / scale, g.Snooze.Y / scale);
        var bands = new (string Name, Rect Rect)[]
        {
            ("top", new Rect(0, 0, w, thickness)),
            ("bottom", new Rect(0, h - thickness, w, thickness)),
            ("left", new Rect(0, 0, thickness, h)),
            ("right", new Rect(w - thickness, 0, thickness, h)),
        };
        var occupied = bands.Single(b => b.Name == edge.ToLowerInvariant()).Rect;
        foreach (var (name, centre) in new[] { ("start", start), ("snooze", snooze) })
        {
            var dx = Math.Max(0, Math.Max(occupied.Left - centre.X, centre.X - occupied.Right));
            var dy = Math.Max(0, Math.Max(occupied.Top - centre.Y, centre.Y - occupied.Bottom));
            var distance = Math.Sqrt(dx * dx + dy * dy);
            Check.WithCustomMessage(
                    $"the {name} ring (centre {centre.X:0.#},{centre.Y:0.#}) overlaps the {edge}-docked "
                    + $"toolbar's {thickness:0.#} DIP band [{occupied}]")
                .That(distance).IsGreaterOrEqualThan(EyeCareBannerWindow.HitRadiusDip);
        }

        // A right-docked toolbar pushes the panel to the left, where no band sits.
        if (edge == "Right")
        {
            Check.WithCustomMessage("a right-docked toolbar must move the rings off the right edge")
                .That(g.Start.X / scale).IsStrictlyLessThan(w / 2);
            Check.That(g.Snooze.X).IsStrictlyLessThan(g.Start.X);
        }
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
        var toolbar = TopToolbar();
        var window = new EyeCareBannerWindow(dwellSettings: new DwellSettings(), toolbarSettings: toolbar);
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        using var host = FitHost.Open(root, width: 1920, height: window.Height, inheritFrom: window);

        // The message strip keeps its decided 96 DIP height.
        var strip = ((Grid)root).Children.OfType<Border>().Single();
        Check.That(strip.ActualHeight).IsCloseTo(96, 0.01);

        var targets = EyeCareBannerWindow.ComputeDwellGeometry(1920, toolbar, 1.0, 1.0);
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
