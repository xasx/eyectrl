namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S3-A on the drawn switcher: the "Show desktop" tile is the LAST tile, it wears the outlined
/// look instead of a window tile's filled one, and its label (English and the shipped German) fits
/// the face at the owner's 200 %.
///
/// <para>
/// The production XAML is detached from a never-shown window and laid out at 1920 × 1200 DIP —
/// the same headless arrangement <see cref="TaskSwitcherTileStatesWindowTests"/> uses. The 2.0
/// case hosts the SAME tree through <see cref="FitHost"/>, whose off-screen HwndSource carries a
/// scale transform, so the measured DIP is what the owner's display draws.
/// </para>
///
/// <para>
/// Placement is asserted against the real layout helper with the owner's docked top band: the
/// desktop tile occupies the cell right after the last window, never a clipped row, and its drawn
/// face is the same 184 × 150 DIP (S4-C) the dwell target uses.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class TaskSwitcherDesktopTileWindowTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    /// <summary>A 112 DIP docked top test band (not the owner's real 140 DIP one): it leaves 8 × 5 tiles (F-GAP).</summary>
    private static readonly TaskSwitcherLayout.DipRect TopBand = new(0, 0, 1920, 112);

    private readonly ITestOutputHelper _output;

    public TaskSwitcherDesktopTileWindowTests(ITestOutputHelper output) => _output = output;

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void DesktopTile_IsDrawnOutlined_WithItsOwnGlyphAndLabel_AfterTheWindowTiles(double scale)
    {
        using var board = Board.Open(Windows(1, 2, 3), scale);

        // It is the LAST panel item, and its drawn face is the standard tile face.
        var tiles = board.Faces();
        Check.That(tiles).HasSize(4);
        Check.That(tiles[3].Item.IsDesktop).IsTrue();
        var desktopFace = board.Bounds(tiles[3].Border);
        Check.That(desktopFace.Width).IsEqualTo(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip);
        Check.That(desktopFace.Height).IsEqualTo(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip);

        // Outlined, not filled: the visual difference the ticket asks for.
        Check.WithCustomMessage("A window tile must keep its filled face.")
            .That(tiles[0].Border.Background is SolidColorBrush windowBrush
                && windowBrush.Color == Tokens.SurfaceMedium).IsTrue();
        Check.WithCustomMessage("The desktop tile must be outlined, not filled.")
            .That(tiles[3].Border.Background is not SolidColorBrush desktopBrush
                || desktopBrush.Color.A == 0).IsTrue();
        Check.That(tiles[3].Border.BorderThickness.Left).IsStrictlyGreaterThan(0);
        Check.That(tiles[0].Border.BorderThickness.Left).IsEqualTo(0);

        // Its own content: the label and the drawn monitor glyph, and none of the window content.
        var label = board.Find<TextBlock>(tiles[3].Container, "DesktopLabel")
            ?? throw new InvalidOperationException("The desktop tile has no TextBlock named DesktopLabel.");
        Check.That(label.Text).IsEqualTo("Show desktop");
        Check.That(label.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(BrushColor(label.Foreground)).IsEqualTo(Tokens.TextPrimary);

        var glyph = board.Find<System.Windows.Shapes.Path>(tiles[3].Container, null)
            ?? throw new InvalidOperationException("The desktop tile has no drawn glyph.");
        Check.That(glyph.ActualWidth).IsStrictlyGreaterThan(0);
        Check.That(glyph.ActualHeight).IsStrictlyGreaterThan(0);
        Check.WithCustomMessage("The desktop tile must not draw a window's icon/title/badge block.")
            .That(board.Find<StackPanel>(tiles[3].Container, "WindowContent")!.Visibility)
            .IsEqualTo(Visibility.Collapsed);
        Check.WithCustomMessage("A window tile must not draw the desktop content.")
            .That(board.Find<StackPanel>(tiles[0].Container, "DesktopContent")!.Visibility)
            .IsEqualTo(Visibility.Collapsed);

        // The label is drawn whole inside the face (the German 200 % case is its own test).
        var natural = FitText.NaturalWidth(label, label.Text);
        Check.WithCustomMessage($"The label needs {FitText.Dip(natural)} DIP but is drawn in {FitText.Dip(label.ActualWidth)}.")
            .That(natural).IsLessOrEqualThan(label.ActualWidth + 0.5);
        Check.That(board.Bounds(label).Left).IsGreaterOrEqualThan(desktopFace.Left);
        Check.That(board.Bounds(label).Right).IsLessOrEqualThan(desktopFace.Right);

        _output.WriteLine(
            $"at {scale:0.0}x: desktop face {Box(desktopFace)} in cell 4; "
            + $"label \"{label.Text}\" {FitText.Dip(label.ActualWidth)} DIP drawn, {FitText.Dip(natural)} DIP natural");
    }

    [StaFact]
    public void DesktopTile_OccupiesTheCellRightAfterTheLastWindow_InTheCentredBlock()
    {
        // Five windows on the owner's 1920 × 1200 screen with the 112 DIP test band: six panel
        // cells, one row, block (312, 549.0, 1296, 182) — so the desktop tile's face is at
        // x 1408..1592, y 565..715. A tile drawn anywhere else is not "always the last tile".
        using var board = Board.Open(Windows(1, 2, 3, 4, 5));

        var layout = board.Layout;
        Check.That(layout.Block.X).IsEqualTo(312);
        Check.That(layout.Block.Y).IsCloseTo(549.0, 1e-9);
        Check.That(layout.Block.Width).IsEqualTo(1296);
        Check.That(layout.Block.Height).IsEqualTo(182);

        var faces = board.Faces();
        var desktop = board.Bounds(faces[5].Border);
        var expected = new Rect(1408, 565.0, 184, 150);
        Check.WithCustomMessage($"The desktop tile is at {Box(desktop)}, not {Box(expected)}.")
            .That(Math.Abs(desktop.Left - expected.Left)).IsLessOrEqualThan(0.5);
        Check.That(Math.Abs(desktop.Top - expected.Top)).IsLessOrEqualThan(0.5);
        Check.That(desktop.Width).IsCloseTo(expected.Width, 0.5);
        Check.That(desktop.Height).IsCloseTo(expected.Height, 0.5);
        _output.WriteLine($"desktop face {Box(desktop)}; block ({layout.Block.X}, {layout.Block.Y})");
    }

    [StaFact]
    public void DesktopTile_WithGermanLabel_FitsTheFaceAt200Percent()
    {
        // The shipped German, at the owner's 200 %: "Desktop anzeigen" must be drawn whole on the
        // 184 DIP face (the face minus its 8 DIP padding leaves 168 DIP).
        StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var board = Board.Open(Windows(1, 2), scale: 2.0);
            var faces = board.Faces();
            var label = board.Find<TextBlock>(faces[^1].Container, "DesktopLabel")!;
            Check.That(label.Text).IsEqualTo("Desktop anzeigen");

            var natural = FitText.NaturalWidth(label, label.Text);
            Check.WithCustomMessage($"The German label needs {FitText.Dip(natural)} DIP but is drawn in "
                    + $"{FitText.Dip(label.ActualWidth)} at 200 %.")
                .That(natural).IsLessOrEqualThan(label.ActualWidth + 0.5);
            Check.That(label.ActualWidth).IsStrictlyLessThan(
                TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip);

            var face = board.Bounds(faces[^1].Border);
            _output.WriteLine(
                $"German at 2.0x: \"{label.Text}\" {FitText.Dip(label.ActualWidth)} DIP drawn, "
                + $"{FitText.Dip(natural)} DIP natural, in face {Box(face)}");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void DesktopTile_IsNotDrawnWhenTheSessionHasNothingToMinimise()
    {
        using var board = Board.Open([]);

        Check.That(board.ViewModel.HasDesktopTile).IsFalse();
        Check.That(board.Faces()).IsEmpty();

        // S2-B's message is the whole tile area in that session.
        var message = board.Find<TextBlock>(board.Root, "EmptyMessage")!;
        Check.That(message.Visibility).IsEqualTo(Visibility.Visible);
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Windows as the enumeration hands them over: numbered, ordinary, none minimised.</summary>
    private static IReadOnlyList<AppWindow> Windows(params int[] hwnds) =>
        hwnds.Select(h => Win(h, $"Window {h}")).ToList();

    private static AppWindow Win(int hwnd, string title) =>
        new(new IntPtr(hwnd), title, (uint)(100 + hwnd), "app");

    private static string Box(Rect r) =>
        $"[{FitText.Dip(r.Left)}, {FitText.Dip(r.Top)} to {FitText.Dip(r.Right)}, {FitText.Dip(r.Bottom)}]";

    private static Color BrushColor(Brush brush) =>
        brush is SolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"{brush} is not a solid brush.");

    private sealed record Slot(
        TaskSwitcherTileViewModel Item, DependencyObject Container, Border Border);

    /// <summary>
    /// The production window, laid out at the screen's size without ever being shown. Mirrors
    /// <c>ShowFullScreen</c>'s order — capacity trim, then the layout for the DRAWN panel — without
    /// calling it (the show path needs an HWND for PointToScreen).
    /// </summary>
    private sealed class Board : IDisposable
    {
        private readonly TaskSwitcherWindow _window;
        private readonly FitHost? _host;
        private readonly ItemsControl _panel;
        private readonly double _scale;

        private Board(TaskSwitcherWindow window, FrameworkElement root, ItemsControl panel,
            TaskSwitcherViewModel viewModel, TaskSwitcherLayout.Result layout, FitHost? host, double scale)
        {
            _window = window;
            Root = root;
            _panel = panel;
            ViewModel = viewModel;
            Layout = layout;
            _host = host;
            _scale = scale;
        }

        internal FrameworkElement Root { get; }
        internal TaskSwitcherViewModel ViewModel { get; }
        internal TaskSwitcherLayout.Result Layout { get; }

        internal static Board Open(IReadOnlyList<AppWindow> windows, double scale = 1.0)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var messenger = new WeakReferenceMessenger();
            var viewModel = new TaskSwitcherViewModel(
                new FakeWindowManager(windows), new EyeCtrlSettings(), messenger);
            messenger.Send(new TaskSwitcherToggledMessage());

            var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
            window.DataContext = viewModel;

            // ShowFullScreen's own order: capacity first (a region property), then the final
            // layout for the panel that will actually be drawn.
            viewModel.ApplyTileCapacity(TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", tileCount: 0).Capacity);
            var layout = TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", viewModel.PanelItems.Count);
            window.ApplyLayout(layout, ScreenWidth);

            var root = (FrameworkElement)window.Content;
            var panel = (ItemsControl)window.FindName("TilesPanel")!;
            window.Content = null;   // detach: the window is never shown, the content only measured
            root.DataContext = viewModel;

            FitHost? host = null;
            if (Math.Abs(scale - 1.0) < 1e-9)
            {
                root.Measure(new Size(ScreenWidth, ScreenHeight));
                root.Arrange(new Rect(0, 0, ScreenWidth, ScreenHeight));
                root.UpdateLayout();
            }
            else
            {
                host = FitHost.Open(root, width: ScreenWidth, height: ScreenHeight, inheritFrom: window);
            }

            return new Board(window, root, panel, viewModel, layout, host, scale);
        }

        /// <summary>Every panel item's container and its template face, in display order.</summary>
        internal IReadOnlyList<Slot> Faces()
        {
            var slots = new List<Slot>();
            foreach (var item in ViewModel.PanelItems)
            {
                var container = _panel.ItemContainerGenerator.ContainerFromItem(item)
                    ?? throw new InvalidOperationException(
                        "a panel tile has no realised container — the ItemsSource binding is missing.");
                Border? face = null;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(container); i++)
                {
                    if (VisualTreeHelper.GetChild(container, i) is Border candidate)
                    {
                        face = candidate;
                        break;
                    }
                }

                slots.Add(new Slot(item, container, face
                    ?? throw new InvalidOperationException("a panel tile has no template Border.")));
            }

            return slots;
        }

        internal T? Find<T>(DependencyObject searchRoot, string? name) where T : FrameworkElement
        {
            var stack = new Stack<DependencyObject>();
            stack.Push(searchRoot);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is T match && (name is null || match.Name == name))
                    return match;
                if (node is not Visual) continue;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                    stack.Push(VisualTreeHelper.GetChild(node, i));
            }

            return null;
        }

        /// <summary>The element's drawn rectangle in DIP (translate at 1.0; render chain at 2.0).</summary>
        internal Rect Bounds(FrameworkElement element)
        {
            if (_host is null)
                return new Rect(element.TranslatePoint(new Point(0, 0), Root),
                    new Size(element.ActualWidth, element.ActualHeight));

            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var rootOrigin = Root.PointToScreen(new Point(0, 0));
            return new Rect(
                (topLeft.X - rootOrigin.X) / _scale, (topLeft.Y - rootOrigin.Y) / _scale,
                (bottomRight.X - topLeft.X) / _scale, (bottomRight.Y - topLeft.Y) / _scale);
        }

        public void Dispose()
        {
            _host?.Dispose();
            _window.DataContext = null;
            _window.Close();
            ViewModel.Dispose();
        }
    }

    /// <summary>Minimal IWindowManager fake: a fixed list, no foreground, nothing else.</summary>
    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows) => _windows = windows;

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
