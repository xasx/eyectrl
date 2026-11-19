namespace EyeCtrl.App.Tests;

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// The window half of the keep-out layout: applying a computed layout must move the tile
/// viewport and raise the hint row exactly as the pure geometry says, and ShowFullScreen must
/// actually compose and apply it. Nothing here shows the window — ApplyLayout is a pure property
/// write, and the show path is pinned at the source (PointToScreen-based layout cannot run
/// headless).
/// </summary>
[Collection("WpfApplication")]
public class TaskSwitcherWindowLayoutTests
{
    [StaFact]
    public void ApplyLayout_MovesTheTileViewportAndRaisesTheHintRowAboveABottomBand()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        try
        {
            var tiles = (ScrollViewer)window.FindName("TilesScroll")!;
            var hintRow = (Grid)window.FindName("HintRow")!;

            // A 112 DIP docked top band on the owner's 1920 × 1200 screen: area (40,136,1840,940),
            // block 1728x910.
            var top = TaskSwitcherLayout.Compute(
                1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 63);
            window.ApplyLayout(top, screenWidthDip: 1920);

            Check.That(tiles.Margin).IsEqualTo(new Thickness(40, 136, 40, 0));
            Check.That(tiles.Width).IsEqualTo(1728);
            Check.That(tiles.Height).IsEqualTo(910);
            Check.That(hintRow.Height).IsEqualTo(TaskSwitcherLayout.HintRowHeight);
            Check.That(hintRow.Margin.Bottom).IsEqualTo(0);

            // The reserved hint-row height is the close disc plus its 12 DIP top/bottom margins. Pin
            // the coupling: growing the disc without growing the reserved row would clip the bottom
            // full tile row the geometry promised.
            Check.WithCustomMessage("HintRowHeight must stay the close disc plus its 24 DIP of margins")
                .That(TaskSwitcherLayout.HintRowHeight).IsEqualTo(TaskSwitcherWindow.CloseButtonSize + 24);

            // Docked bottom at 112 DIP: the hint row must sit fully above the band (112 + 24 = 136).
            var bottom = TaskSwitcherLayout.Compute(
                1920, 1200, new TaskSwitcherLayout.DipRect(0, 1088, 1920, 112), "Bottom", tileCount: 63);
            window.ApplyLayout(bottom, screenWidthDip: 1920);

            Check.That(hintRow.Margin.Bottom).IsEqualTo(136);
            Check.That(tiles.Margin.Top).IsEqualTo(40);
            Check.That(tiles.Height).IsEqualTo(4 * TaskSwitcherLayout.CellHeight);
        }
        finally
        {
            window.Close();   // PrepareCloaked created the HWND; release it
        }
    }

    /// <summary>
    /// Source-scan pin: ShowFullScreen must hand the coordinator's keep-out to the pure helper
    /// (converted once through the live DPI scale) and apply the result before showing. A
    /// regression that ignores the argument, computes but never applies, or applies the scale by
    /// multiplying (the 2026-08-17 double-scale class) leaves every pure test green while the
    /// tiles land under the toolbar.
    /// </summary>
    [Fact]
    public void ShowFullScreen_ComputesTheLayoutFromTheKeepOut_AndAppliesIt()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var start = source.IndexOf("public void ShowFullScreen(ToolbarKeepOut? toolbarKeepOut)",
            StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = SourceScanner.StripComments(
            source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)]);

        Check.WithCustomMessage(
                "ShowFullScreen ignores the toolbar keep-out — the tiles start under the docked band.")
            .That(body.Contains("ComputeLayout(toolbarKeepOut")).IsTrue();
        Check.WithCustomMessage(
                "ShowFullScreen computes the layout but never applies it to the window.")
            .That(body.Contains("ApplyLayout(")).IsTrue();
        Check.WithCustomMessage(
                "The physical toolbar bounds must be converted with the live DPI scale, not by "
                + "multiplying DIPs into physical pixels again (the double-scale class).")
            .That(body.Contains("DpiHelper.ScaleX") && body.Contains("DpiHelper.ScaleY")).IsTrue();
        Check.WithCustomMessage(
                "ShowFullScreen must not scale the physical bounds a second time — ToDip already "
                + "divides by the scale.")
            .That(body.Contains("DpiHelper.LogicalToPhysical")).IsFalse();
    }

    /// <summary>
    /// Executes the physical→DIP composition the source pin above only names: at 200 % a 3840×224
    /// px band must become the same 1920×112 DIP band the 100 % case uses, so the whole layout is
    /// identical. The multiply-by-scale mutant in <c>ToDip</c> dies here too.
    /// </summary>
    [Fact]
    public void ComputeLayout_ConvertsThePhysicalToolbarBandToDipOnce_At200Percent()
    {
        var physical = new ToolbarKeepOut(new PhysicalRect(0, 0, 3840, 224), "Top");
        var at200 = TaskSwitcherWindow.ComputeLayout(physical, 1920, 1200, 2.0, 2.0, tileCount: 63);
        var at100 = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 63);

        Check.That(at200.Area).IsEqualTo(at100.Area);
        Check.That(at200.Block).IsEqualTo(at100.Block);
        Check.That(at200.Columns).IsEqualTo(8);
        Check.That(at200.Rows).IsEqualTo(5);
    }

    /// <summary>
    /// Each axis uses its own scale. On an anisotropic transform a swapped X/Y (or the X scale used
    /// for both axes) would turn the 168 px band into 84 DIP and move the area from y=136 to 108 —
    /// invisible to the uniform-200 % case above.
    /// </summary>
    [Fact]
    public void ComputeLayout_UsesEachAxesScale_ForANonUniformDpi()
    {
        var physical = new ToolbarKeepOut(new PhysicalRect(0, 0, 3840, 168), "Top");
        var layout = TaskSwitcherWindow.ComputeLayout(physical, 1920, 1200, 2.0, 1.5, tileCount: 5);

        Check.That(layout.Area.Y).IsEqualTo(136);
        Check.That(layout.Block).IsEqualTo(
            new TaskSwitcherLayout.DipRect(420, 515, 1080, 182));
    }

    /// <summary>
    /// The drawn arrangement, headless: the production XAML is detached from the never-shown window,
    /// measured and arranged at 1920×1200 DIP, and every tile face is read with
    /// <c>TranslatePoint</c>. Five tiles must wrap into a 1000-wide block centred on the area
    /// (review finding 1), fully inside the viewport and clear of the toolbar band + gap.
    /// </summary>
    [StaFact]
    public void DrawnFiveTiles_AreCentredOnTheArea_AndClearOfTheToolbarBand()
    {
        var layout = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 5);
        using var arranged = Arrange(5, layout);

        var viewport = InRoot(arranged.Root, arranged.Scroll);
        Check.That(viewport).IsEqualTo(new Rect(420, 515, 1080, 182));

        var faces = Faces(arranged);
        Check.That(faces.Count).IsEqualTo(5);
        foreach (var face in faces)
        {
            Check.That(face.Top).IsGreaterOrEqualThan(
                112 + TaskSwitcherLayout.ToolbarGap);
            Check.That(face.Left).IsGreaterOrEqualThan(viewport.Left);
            Check.That(face.Right).IsLessOrEqualThan(viewport.Right);
            Check.That(face.Top).IsGreaterOrEqualThan(viewport.Top);
            Check.That(face.Bottom).IsLessOrEqualThan(viewport.Bottom);
        }

        var left = faces.Min(f => f.Left);
        var right = faces.Max(f => f.Right);
        Check.That((left + right) / 2).IsCloseTo(960, 1e-6);
    }

    /// <summary>
    /// 64 windows against an 8×5 capacity: exactly 40 faces lie fully inside the viewport, none is
    /// partially visible, and the 41st starts below the viewport's bottom edge — the geometry the
    /// window's <c>VisibleTileRect</c> clamp turns into "no dwell target".
    /// </summary>
    [StaFact]
    public void DrawnTilesBeyondCapacity_StayFullyOutsideTheViewport()
    {
        var layout = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 64);
        using var arranged = Arrange(64, layout);

        var viewport = InRoot(arranged.Root, arranged.Scroll);
        Check.That(viewport).IsEqualTo(new Rect(96, 151, 1728, 910));

        var faces = Faces(arranged);
        Check.That(faces.Count).IsEqualTo(64);

        static bool FullyInside(Rect face, Rect vp) =>
            face.Left >= vp.Left && face.Right <= vp.Right
            && face.Top >= vp.Top && face.Bottom <= vp.Bottom;

        var fullyVisible = faces.Count(f => FullyInside(f, viewport));
        Check.That(fullyVisible).IsEqualTo(40);
        var partiallyVisible = faces.Count(f => f.IntersectsWith(viewport) && !FullyInside(f, viewport));
        Check.That(partiallyVisible).IsEqualTo(0);
        Check.That(faces[40].Top).IsGreaterOrEqualThan(viewport.Bottom);
        Check.That(faces.Min(f => f.Top)).IsGreaterOrEqualThan(
            112 + TaskSwitcherLayout.ToolbarGap);
    }

    /// <summary>
    /// F-GAP, drawn: two horizontally and two vertically adjacent faces sit 32 DIP apart (the
    /// doubled gap — 2 × the template's 16 DIP margin) and every face keeps its 184 × 150 DIP, at
    /// DPI 1.0 (the layout transform) and at the owner's 200 % through the real render chain,
    /// where the raw PointToScreen delta across the gap is 64 physical pixels.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void AdjacentFaces_Are32DipApart_AndKeepThe184x150Face_AtEveryScale(double scale)
    {
        var layout = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 11);
        WpfApplicationGate.EnsureApplicationWithTokens();

        var viewModel = new TaskSwitcherViewModel(
            windowManager: null!, new EyeCtrlSettings(), new WeakReferenceMessenger());
        for (var i = 0; i < 11; i++)
        {
            viewModel.Tiles.Add(new TaskSwitcherTileViewModel(
                new IntPtr(i + 1), $"Window {i + 1}", 0, "test", new PhysicalPoint(0, 0)));
        }

        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        FitHost? host = null;
        try
        {
            window.DataContext = viewModel;
            window.ApplyLayout(layout, screenWidthDip: 1920);
            var root = (FrameworkElement)window.Content;
            var panel = (ItemsControl)window.FindName("TilesPanel")!;
            window.Content = null;   // detach: the window is never shown, the content only measured
            root.DataContext = viewModel;

            if (Math.Abs(scale - 1.0) < 1e-9)
            {
                root.Measure(new Size(1920, 1200));
                root.Arrange(new Rect(0, 0, 1920, 1200));
                root.UpdateLayout();
            }
            else
            {
                // The owner's 200 %: the frame carries a ScaleTransform of 2.0 / device scale, so
                // PointToScreen composes the whole render chain (the Board fixture's route).
                host = FitHost.Open(root, width: 1920, height: 1200, inheritFrom: window);
            }

            // 11 tiles: 8 fill row 0, 3 sit in row 1 — both adjacency directions exist.
            var borders = FaceBordersOf(panel, viewModel);
            Check.That(borders).HasSize(11);
            var face0 = RectIn(root, borders[0], host);
            var face1 = RectIn(root, borders[1], host);
            var face8 = RectIn(root, borders[8], host);
            var face9 = RectIn(root, borders[9], host);

            foreach (var (face, index) in new[] { (face0, 0), (face1, 1), (face8, 8), (face9, 9) })
            {
                Check.WithCustomMessage($"face {index} drew {face.Width} x {face.Height} at {scale:0.0}x.")
                    .That(Math.Abs(face.Width - 184)).IsLessOrEqualThan(0.5);
                Check.That(Math.Abs(face.Height - 150)).IsLessOrEqualThan(0.5);
            }

            Check.WithCustomMessage(
                    $"the horizontal gap face0→face1 is {face1.Left - face0.Right} DIP at {scale:0.0}x, not 32.")
                .That(Math.Abs(face1.Left - face0.Right - 32)).IsLessOrEqualThan(0.5);
            Check.WithCustomMessage(
                    $"the vertical gap face0→face8 is {face8.Top - face0.Bottom} DIP at {scale:0.0}x, not 32.")
                .That(Math.Abs(face8.Top - face0.Bottom - 32)).IsLessOrEqualThan(0.5);

            if (host is { })
            {
                // The physical truth at 200 %: the 32 DIP gap is 64 device pixels.
                var right0 = borders[0].PointToScreen(new Point(borders[0].ActualWidth, 0));
                var left1 = borders[1].PointToScreen(new Point(0, 0));
                Check.WithCustomMessage(
                        $"the 32 DIP gap drew {left1.X - right0.X} physical px at 200 %, not 64.")
                    .That(Math.Abs(left1.X - right0.X - 64)).IsLessOrEqualThan(1.0);
            }
        }
        finally
        {
            host?.Dispose();
            window.Close();   // PrepareCloaked created the HWND; release it
            viewModel.Dispose();
        }
    }

    /// <summary>
    /// F-GAP coupling pin: the cell lives in TaskSwitcherLayout's constants AND in the production
    /// XAML's WrapPanel, the face margin in TileFaceMarginDip AND in the TileFace Border — the
    /// numbers are coupled only through this pin. It holds the XAML attributes against the
    /// constants and asserts the pairing they promise: cell = face + 2 × margin, in both
    /// directions, with the face staying 184 × 150 and the gap 32 DIP.
    /// </summary>
    [Fact]
    public void TheTileFaceMargin_AndTheWrapPanelCell_StayCoupledToTheConstants()
    {
        var xaml = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml"));

        var wrap = Regex.Match(
            xaml, @"<WrapPanel\s+ItemWidth=""(?<w>[\d.]+)""\s+ItemHeight=""(?<h>[\d.]+)""");
        Check.WithCustomMessage("the WrapPanel cell attributes moved — the pin must follow them.")
            .That(wrap.Success).IsTrue();
        Check.WithCustomMessage("the XAML WrapPanel ItemWidth must equal TaskSwitcherLayout.CellWidth.")
            .That(double.Parse(wrap.Groups["w"].Value, CultureInfo.InvariantCulture))
            .IsEqualTo(TaskSwitcherLayout.CellWidth);
        Check.WithCustomMessage("the XAML WrapPanel ItemHeight must equal TaskSwitcherLayout.CellHeight.")
            .That(double.Parse(wrap.Groups["h"].Value, CultureInfo.InvariantCulture))
            .IsEqualTo(TaskSwitcherLayout.CellHeight);

        var face = Regex.Match(
            xaml, @"<Border\s+x:Name=""TileFace""\s+Margin=""(?<m>[\d.]+)""");
        Check.WithCustomMessage("the TileFace Margin attribute moved — the pin must follow it.")
            .That(face.Success).IsTrue();
        Check.WithCustomMessage("the XAML TileFace Margin must equal TaskSwitcherLayout.TileFaceMarginDip.")
            .That(double.Parse(face.Groups["m"].Value, CultureInfo.InvariantCulture))
            .IsEqualTo(TaskSwitcherLayout.TileFaceMarginDip);

        // The pairing's promise, both directions: the face keeps 184 × 150, the gap is 32 DIP.
        Check.That(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip)
            .IsEqualTo(184);
        Check.That(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip)
            .IsEqualTo(150);
        Check.That(2 * TaskSwitcherLayout.TileFaceMarginDip).IsEqualTo(32);
    }

    /// <summary>Every tile's template face Border, in display order (the Faces() equivalent).</summary>
    private static IReadOnlyList<Border> FaceBordersOf(ItemsControl panel, TaskSwitcherViewModel viewModel)
    {
        var borders = new List<Border>();
        foreach (var tile in viewModel.Tiles)
        {
            if (panel.ItemContainerGenerator.ContainerFromItem(tile)
                is not FrameworkElement container)
            {
                throw new InvalidOperationException(
                    $"tile '{tile.Title}' has no realised container — the XAML's "
                    + "ItemsSource=\"{Binding Tiles}\" is missing or broken.");
            }

            borders.Add(FindFaceBorder(container)
                ?? throw new InvalidOperationException(
                    $"tile '{tile.Title}' has no ItemTemplate Border in its visual tree."));
        }
        return borders;
    }

    /// <summary>
    /// A border's rectangle in DIP relative to <paramref name="root"/>: the layout transform at
    /// 1.0, the render chain (physical PointToScreen divided by the host's 2.0) in a FitHost.
    /// </summary>
    private static Rect RectIn(FrameworkElement root, Border border, FitHost? host)
    {
        if (host is null)
        {
            var origin = border.TranslatePoint(new Point(0, 0), root);
            return new Rect(origin, new Size(border.ActualWidth, border.ActualHeight));
        }

        var topLeft = border.PointToScreen(new Point(0, 0));
        var bottomRight = border.PointToScreen(new Point(border.ActualWidth, border.ActualHeight));
        var rootOrigin = root.PointToScreen(new Point(0, 0));
        return new Rect(
            (topLeft.X - rootOrigin.X) / FitHost.Scale, (topLeft.Y - rootOrigin.Y) / FitHost.Scale,
            (bottomRight.X - topLeft.X) / FitHost.Scale, (bottomRight.Y - topLeft.Y) / FitHost.Scale);
    }

    /// <summary>
    /// Lays the real window content out at the screen size without ever showing it: detach from the
    /// Window (no HWND), restore the DataContext the detach drops, Measure/Arrange at 1920×1200 DIP.
    /// </summary>
    private static ArrangedSwitcher Arrange(int tileCount, TaskSwitcherLayout.Result layout)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();

        var viewModel = new TaskSwitcherViewModel(
            windowManager: null!, new EyeCtrlSettings(), new WeakReferenceMessenger());
        for (var i = 0; i < tileCount; i++)
        {
            viewModel.Tiles.Add(new TaskSwitcherTileViewModel(
                new IntPtr(i + 1), $"Window {i + 1}", 0, "test", new PhysicalPoint(0, 0)));
        }

        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        window.DataContext = viewModel;
        window.ApplyLayout(layout, screenWidthDip: 1920);

        var root = (FrameworkElement)window.Content;
        var scroll = (ScrollViewer)window.FindName("TilesScroll")!;
        var panel = (ItemsControl)window.FindName("TilesPanel")!;

        window.Content = null;   // detach: the window is never shown, the content only measured
        window.Close();          // PrepareCloaked created the HWND; release it (the content is detached)
        root.DataContext = viewModel;
        root.Measure(new Size(1920, 1200));
        root.Arrange(new Rect(0, 0, 1920, 1200));
        root.UpdateLayout();

        return new ArrangedSwitcher(root, scroll, panel, viewModel);
    }

    private static Rect InRoot(FrameworkElement root, FrameworkElement element) =>
        new(element.TranslatePoint(new Point(0, 0), root),
            new Size(element.ActualWidth, element.ActualHeight));

    private static IReadOnlyList<Rect> Faces(ArrangedSwitcher arranged)
    {
        var faces = new List<Rect>();
        foreach (var tile in arranged.ViewModel.Tiles)
        {
            if (arranged.Panel.ItemContainerGenerator.ContainerFromItem(tile)
                is not FrameworkElement container)
            {
                throw new InvalidOperationException(
                    $"tile '{tile.Title}' has no realised container — the XAML's "
                    + "ItemsSource=\"{Binding Tiles}\" is missing or broken.");
            }

            var border = FindFaceBorder(container)
                ?? throw new InvalidOperationException(
                    $"tile '{tile.Title}' has no ItemTemplate Border in its visual tree.");
            faces.Add(InRoot(arranged.Root, border));
        }
        return faces;
    }

    private static Border? FindFaceBorder(DependencyObject container)
    {
        var count = VisualTreeHelper.GetChildrenCount(container);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(container, i) is Border border)
                return border;
        }
        return null;
    }

    private sealed record ArrangedSwitcher(
        FrameworkElement Root, ScrollViewer Scroll, ItemsControl Panel,
        TaskSwitcherViewModel ViewModel) : IDisposable
    {
        public void Dispose() => ViewModel.Dispose();
    }
}
