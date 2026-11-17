namespace EyeCtrl.App.Tests;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
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

            // Docked top at 112 DIP on the owner's screen: area (40,136,1840,940), block 1800x840.
            var top = TaskSwitcherLayout.Compute(
                1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 63);
            window.ApplyLayout(top, screenWidthDip: 1920);

            Check.That(tiles.Margin).IsEqualTo(new Thickness(40, 136, 40, 0));
            Check.That(tiles.Width).IsEqualTo(1800);
            Check.That(tiles.Height).IsEqualTo(840);
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
            Check.That(tiles.Height).IsEqualTo(7 * TaskSwitcherLayout.CellHeight);
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
        Check.That(at200.Columns).IsEqualTo(9);
        Check.That(at200.Rows).IsEqualTo(7);
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
            new TaskSwitcherLayout.DipRect(460, 546, 1000, 120));
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
        Check.That(viewport).IsEqualTo(new Rect(460, 546, 1000, 120));

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
    /// 64 windows against a 9×7 capacity: exactly 63 faces lie fully inside the viewport, none is
    /// partially visible, and the 64th starts below the viewport's bottom edge — the geometry the
    /// window's <c>VisibleTileRect</c> clamp turns into "no dwell target".
    /// </summary>
    [StaFact]
    public void DrawnTilesBeyondCapacity_StayFullyOutsideTheViewport()
    {
        var layout = TaskSwitcherLayout.Compute(
            1920, 1200, new TaskSwitcherLayout.DipRect(0, 0, 1920, 112), "Top", tileCount: 64);
        using var arranged = Arrange(64, layout);

        var viewport = InRoot(arranged.Root, arranged.Scroll);
        Check.That(viewport).IsEqualTo(new Rect(60, 186, 1800, 840));

        var faces = Faces(arranged);
        Check.That(faces.Count).IsEqualTo(64);

        static bool FullyInside(Rect face, Rect vp) =>
            face.Left >= vp.Left && face.Right <= vp.Right
            && face.Top >= vp.Top && face.Bottom <= vp.Bottom;

        var fullyVisible = faces.Count(f => FullyInside(f, viewport));
        Check.That(fullyVisible).IsEqualTo(63);
        var partiallyVisible = faces.Count(f => f.IntersectsWith(viewport) && !FullyInside(f, viewport));
        Check.That(partiallyVisible).IsEqualTo(0);
        Check.That(faces[63].Top).IsGreaterOrEqualThan(viewport.Bottom);
        Check.That(faces.Min(f => f.Top)).IsGreaterOrEqualThan(
            112 + TaskSwitcherLayout.ToolbarGap);
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
