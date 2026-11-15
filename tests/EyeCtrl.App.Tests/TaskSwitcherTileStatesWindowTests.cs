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
/// S2-B on the drawn switcher: the "Minimiert" badge inside a tile face, the "Keine offenen
/// Fenster" message on an empty grid, and the "+N weitere Fenster" overflow chip in the hint row.
///
/// <para>
/// The production XAML is detached from a never-shown window, laid out at 1920 × 1200 DIP and
/// read back with transforms — the same headless arrangement <see cref="TaskSwitcherWindowLayoutTests"/>
/// uses. The 2.0 case hosts the SAME tree through <see cref="FitHost"/>, whose off-screen
/// HwndSource carries a scale transform so the effective scale is the owner's 200 %: reading
/// <c>PointToScreen</c> there and dividing by that scale gives the DIP the user's display draws,
/// rounding included.
/// </para>
///
/// <para>
/// The three states are display-only. Nothing here is a dwell target: the badge lives inside the
/// tile face (the face is the target), the empty message registers nothing, and the chip is a
/// label. The ✕ stays the surface's escape and its 48 DIP distance from the hint is asserted,
/// not assumed.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class TaskSwitcherTileStatesWindowTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    private readonly ITestOutputHelper _output;

    public TaskSwitcherTileStatesWindowTests(ITestOutputHelper output) => _output = output;

    /// <summary>The owner's docked top toolbar band: a 112 DIP band that leaves 9 × 7 tiles.</summary>
    private static readonly TaskSwitcherLayout.DipRect TopBand = new(0, 0, 1920, 112);

    private const int Capacity = 63;   // 9 columns × 7 rows at 1920 × 1200 with the 112 DIP band

    [StaFact]
    public void MinimisedWindow_DrawsTheBadge_InsideItsFace_AndDoesNotDimTheTile()
    {
        using var laid = Laid.Open(
            [
                Win(1, "Chat", minimized: true),
                Win(2, "Editor"),
            ]);

        var faces = laid.Faces();
        Check.That(faces).HasSize(2);

        var badge = laid.Find<Border>(faces[0].Container, "MinimizedBadge")
            ?? throw new InvalidOperationException("The tile template has no Border named MinimizedBadge.");
        var absent = laid.Find<Border>(faces[1].Container, "MinimizedBadge")
            ?? throw new InvalidOperationException("The tile template has no Border named MinimizedBadge.");

        Check.WithCustomMessage("A minimised window's tile must draw the badge.")
            .That(badge.Visibility).IsEqualTo(Visibility.Visible);
        Check.WithCustomMessage("An open window's tile must not draw the badge.")
            .That(absent.Visibility).IsEqualTo(Visibility.Collapsed);

        // Drawn = hit: the badge must sit INSIDE the face the dwell detector hit-tests, and the
        // face must still be the 184 × 104 DIP the template promises (200 × 120 cell less the
        // 8 DIP margin). A badge that grew the tile would put a hit area where nothing is drawn.
        var face = laid.Bounds(faces[0].Border);
        var badgeBox = laid.Bounds(badge);
        Check.WithCustomMessage("The minimised tile grew: the perceived tile must not change.")
            .That(face.Width).IsEqualTo(TaskSwitcherLayout.CellWidth - 16);
        Check.That(face.Height).IsEqualTo(TaskSwitcherLayout.CellHeight - 16);
        Check.WithCustomMessage($"The badge {Box(badgeBox)} leaves its face {Box(face)}.")
            .That(Inside(face, badgeBox, inset: 8)).IsTrue();
        Check.WithCustomMessage("The badge is not centred in its tile face.")
            .That(badgeBox.Left + (badgeBox.Width / 2)).IsCloseTo(face.Left + (face.Width / 2), 0.5);
        _output.WriteLine($"minimised face {Box(face)}; badge {Box(badgeBox)}");

        // The badge is the word plus a drawn bar, both in the secondary text colour — and the tile
        // is NOT dimmed: the paused-dwell cue owns dimming (DwellPausedOpacityConverter), and a
        // restored window must not read as "held".
        var word = Descendants<TextBlock>(badge).Single(t => t.Text == "Minimised");
        Check.That(BrushColor(word.Foreground)).IsEqualTo(Tokens.TextSecondary);
        Check.WithCustomMessage("The minimised cue must not dim the tile — that is the paused cue.")
            .That(faces[0].Border.Opacity).IsEqualTo(1.0);

        var bar = Descendants<Border>(badge).Single(b => b.Name == "MinimizedGlyph");
        Check.WithCustomMessage("The badge's drawn minimise bar has no ink.")
            .That(bar.ActualWidth).IsStrictlyGreaterThan(0);
        Check.That(bar.ActualHeight).IsStrictlyGreaterThan(0);
        Check.That(BrushColor(bar.Background)).IsEqualTo(Tokens.TextSecondary);
    }

    [StaFact]
    public void NoWindowAtAll_DrawsTheEmptyMessage_WhereTheTilesWouldBe()
    {
        // One window, and it is the foreground one (S2-A leaves it out): the grid is empty.
        using var laid = Laid.Open(
            [Win(7, "Only")],
            foreground: new IntPtr(7));

        Check.That(laid.ViewModel.Tiles).IsEmpty();

        var message = laid.Find<TextBlock>(laid.Root, "EmptyMessage")
            ?? throw new InvalidOperationException("The window has no TextBlock named EmptyMessage.");
        Check.WithCustomMessage("An empty switcher must say so instead of showing a bare screen.")
            .That(message.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(message.Text).IsEqualTo("No open windows");

        // Centred in the TILE AREA, not the window: the same 40/136 keep-out the tile block gets,
        // so a message that ignored the docked toolbar would sit 68 DIP too high.
        var area = TaskSwitcherLayout.Compute(ScreenWidth, ScreenHeight, TopBand, "Top", tileCount: 0);
        var box = laid.Bounds(message);
        Check.WithCustomMessage($"The empty message {Box(box)} is not at the tile area's centre.")
            .That(box.Left + (box.Width / 2)).IsCloseTo(area.Area.X + (area.Area.Width / 2), 0.5);
        Check.That(box.Top + (box.Height / 2)).IsCloseTo(area.Area.Y + (area.Area.Height / 2), 0.5);
        Check.That(BrushColor(message.Foreground)).IsEqualTo(Tokens.TextSecondary);
        _output.WriteLine($"empty message {Box(box)} in area {area.Area}");

        // Still escapable: the ✕ is the only target the View registers, and the empty message adds
        // none (it is a label in the tile row, where UpdateTilePositions registers nothing).
        laid.RegisterFaces(0);
        Check.That(laid.ViewModel.DetectorForTest.Targets.Select(t => t.Id))
            .ContainsExactly(TaskSwitcherViewModel.CloseTargetId);
    }

    [StaFact]
    public void TilesPresent_DrawNoEmptyMessage()
    {
        using var laid = Laid.Open([Win(1, "Editor")]);

        var message = laid.Find<TextBlock>(laid.Root, "EmptyMessage")!;
        Check.That(message.Visibility).IsEqualTo(Visibility.Collapsed);
    }

    [StaFact]
    public void OverflowChip_InEnglish_CountsTheWindowsWithoutATile()
    {
        using var laid = Laid.Open(Windows(64));
        laid.RegisterFaces(Capacity);

        var chip = laid.Find<Border>(laid.Root, "OverflowChip")
            ?? throw new InvalidOperationException("The window has no Border named OverflowChip.");
        Check.That(chip.Visibility).IsEqualTo(Visibility.Visible);

        var text = Descendants<TextBlock>(chip).Single();
        Check.That(text.Text).IsEqualTo("+1 more windows");
        Check.That(BrushColor(text.Foreground)).IsEqualTo(Tokens.TextSecondary);
    }

    [StaFact]
    public void AtCapacity_DrawsNoChip()
    {
        // 63 windows, 63 faces: nothing is hidden, so there is nothing to explain. This is the
        // two-sided half of the count — an off-by-one either claims a hidden window or hides one.
        using var laid = Laid.Open(Windows(Capacity));
        laid.RegisterFaces(Capacity);

        var chip = laid.Find<Border>(laid.Root, "OverflowChip")!;
        Check.That(chip.Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(laid.ViewModel.OverflowCount).IsEqualTo(0);
    }

    [StaFact]
    public void ThreeWindowsWithoutATile_CountThree()
    {
        using var laid = Laid.Open(Windows(66));
        laid.RegisterFaces(Capacity);

        Check.That(laid.ViewModel.OverflowCount).IsEqualTo(3);
        var chip = laid.Find<Border>(laid.Root, "OverflowChip")!;
        Check.That(Descendants<TextBlock>(chip).Single().Text).IsEqualTo("+3 more windows");
    }

    /// <summary>
    /// The chip's German text, in the SHIPPED German (the real switch-on, not the draft): the XAML
    /// resolves <c>{loc:T}</c> when the window is parsed, so the table has to be active first.
    /// </summary>
    [StaFact]
    public void OverflowChip_InShippedGerman_ReadsTheSameCount()
    {
        StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var laid = Laid.Open(Windows(64), scale: 2.0);
            laid.RegisterFaces(Capacity);

            var chip = laid.Find<Border>(laid.Root, "OverflowChip")!;
            Check.That(chip.Visibility).IsEqualTo(Visibility.Visible);
            var text = Descendants<TextBlock>(chip).Single();
            Check.That(text.Text).IsEqualTo("+1 weitere Fenster");

            // The German chip is the longest of the three texts: at 200 % it must be drawn whole.
            var natural = FitText.NaturalWidth(text, text.Text);
            Check.WithCustomMessage($"The German chip needs {FitText.Dip(natural)} DIP but is drawn in "
                    + $"{FitText.Dip(text.ActualWidth)}.")
                .That(natural).IsLessOrEqualThan(text.ActualWidth + 0.5);
            _output.WriteLine($"German chip \"{text.Text}\": {FitText.Dip(text.ActualWidth)} DIP drawn, "
                + $"text {FitText.Dip(natural)} DIP, chip {Box(laid.Bounds(chip))}");

            // The count is an ARGUMENT: the template's own spelling is what the German table
            // holds, and the number is formatted in, not concatenated.
            Check.That(StringTable.Current.T("overlays.taskSwitcher.overflow", 1))
                .IsEqualTo("+1 weitere Fenster");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void GermanBadgeEmptyMessageAndChip_AreTheShippedTexts_AndFitAt200Percent()
    {
        StringTable.Activate(ShippedGerman.Load());
        try
        {
            // At the owner's 200 %: the three German texts must be drawn whole, not clipped — the
            // German is longer than the English in every one of them.
            using var minimal = Laid.Open(
                [
                    Win(1, "Chat", minimized: true),
                    Win(2, "Editor"),
                ], scale: 2.0);
            var badge = minimal.Find<Border>(minimal.Faces()[0].Container, "MinimizedBadge")!;
            var word = Descendants<TextBlock>(badge).Single();
            Check.That(word.Text).IsEqualTo("Minimiert");
            Check.WithCustomMessage($"The German badge needs {FitText.Dip(FitText.NaturalWidth(word, word.Text))} DIP "
                    + $"but is drawn in {FitText.Dip(word.ActualWidth)}.")
                .That(FitText.NaturalWidth(word, word.Text)).IsLessOrEqualThan(word.ActualWidth + 0.5);
            _output.WriteLine($"German badge \"{word.Text}\": {FitText.Dip(word.ActualWidth)} DIP drawn in "
                + $"{Box(minimal.Bounds(badge))}");

            using var empty = Laid.Open([Win(7, "Only")], scale: 2.0, foreground: new IntPtr(7));
            var message = empty.Find<TextBlock>(empty.Root, "EmptyMessage")!;
            Check.That(message.Text).IsEqualTo("Keine offenen Fenster");
            Check.That(FitText.NaturalWidth(message, message.Text)).IsLessOrEqualThan(message.ActualWidth + 0.5);
            _output.WriteLine($"German empty message: {FitText.Dip(message.ActualWidth)} DIP drawn in "
                + $"{Box(empty.Bounds(message))}");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The chip's placement rules, measured in DIP at 1.0 and at the owner's 200 %: it must not
    /// overlap the ✕, it must keep ≥ 100 DIP edge-to-edge from the ✕'s hit CIRCLE (so gaze resting
    /// on the chip cannot fill the close dwell), and it must not eat the hint→✕ gap.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ChipGeometry_ClearsTheCloseCircle_AndKeepsTheHintGap(double scale)
    {
        using var laid = Laid.Open(Windows(64), scale: scale);
        laid.RegisterFaces(Capacity);

        var chipElement = laid.Find<Border>(laid.Root, "OverflowChip")!;
        var chip = laid.Bounds(chipElement);
        var hint = laid.Bounds(laid.Find<TextBlock>(laid.Root, "HintText")!);
        var disc = laid.Bounds(laid.Find<Border>(laid.Root, "CloseButton")!);

        var messages = new List<string>();

        // The hint→✕ gap is the row's 48 DIP attribute, and it must hold whatever appears to the left.
        const double hintGap = 48;
        if (Math.Abs(disc.Left - hint.Right - hintGap) > 0.5)
            messages.Add($"the hint→✕ gap is {FitText.Dip(disc.Left - hint.Right)} DIP, not {hintGap}");

        // "Edge-to-edge from the hit circle": the nearest point of the chip to the disc's centre,
        // less the hit radius. The drawn disc IS the hit circle (B8: 2 × the shared r50), so its
        // own half-width is the radius — never a restated number.
        var centre = new Point(disc.Left + (disc.Width / 2), disc.Top + (disc.Height / 2));
        var clearance = DistanceToRect(chip, centre) - (disc.Width / 2);
        if (clearance < 100)
            messages.Add($"the chip is {FitText.Dip(clearance)} DIP edge-to-edge from the ✕'s hit circle, not ≥ 100");

        if (chip.IntersectsWith(disc) && chip.Width > 0 && chip.Height > 0)
            messages.Add($"the chip {Box(chip)} overlaps the ✕ disc {Box(disc)}");

        // Still on screen: the fixup's own rule for the widest chip (the German one is measured in
        // its own test; this one keeps the English one inside the window's edges).
        if (chip.Left < 0 || chip.Right > ScreenWidth)
            messages.Add($"the chip {Box(chip)} leaves the {ScreenWidth} DIP window");

        // The chip's whole text must be shown, not clipped: the row gives it its natural width.
        var word = Descendants<TextBlock>(chipElement).Single();
        var natural = FitText.NaturalWidth(word, word.Text);
        if (natural > word.ActualWidth + 0.5)
            messages.Add($"the chip's text needs {FitText.Dip(natural)} DIP but is shown in {FitText.Dip(word.ActualWidth)}");

        Check.WithCustomMessage($"At {scale:0.0}x: " + string.Join("; ", messages)).That(messages).IsEmpty();
        Check.That(clearance).IsStrictlyGreaterThan(100);

        _output.WriteLine($"at {scale:0.0}x: chip {Box(chip)}; hint {Box(hint)}; disc {Box(disc)}; "
            + $"hint to close {FitText.Dip(disc.Left - hint.Right)} DIP; "
            + $"chip to hit circle {FitText.Dip(clearance)} DIP; chip text {FitText.Dip(natural)} DIP");
    }

    /// <summary>
    /// S2-B fixup-01: the ✕ is the escape, and a gaze-only user finds it by where it was last time
    /// — so the overflow chip must not move it. Measured per language (the hint's own width is what
    /// centres the pair, so English and German legitimately sit at different x): the same session
    /// with and without the 64th window's chip must put the disc in the same place, at 1.0 and at
    /// the owner's 200 %. The German chip is the widest case.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void CloseDisc_DoesNotMove_WhenTheOverflowChipAppears(double scale)
    {
        using var englishNoChip = Laid.Open(Windows(Capacity), scale: scale);
        englishNoChip.RegisterFaces(Capacity);
        var englishReference = DiscCentre(englishNoChip);
        _output.WriteLine($"at {scale:0.0}x English: no chip, ✕ centre {Point(englishReference)}");

        using var englishChip = Laid.Open(Windows(Capacity + 1), scale: scale);
        englishChip.RegisterFaces(Capacity);
        Check.WithCustomMessage("The 64-window case drew no chip, so the pin would compare two chip-less rows.")
            .That(englishChip.Find<Border>(englishChip.Root, "OverflowChip")!.Visibility)
            .IsEqualTo(Visibility.Visible);
        var englishChipBox = englishChip.Bounds(englishChip.Find<Border>(englishChip.Root, "OverflowChip")!);
        var english = DiscCentre(englishChip);
        _output.WriteLine($"at {scale:0.0}x English: chip {Box(englishChipBox)}, ✕ centre {Point(english)}");
        AssertSameDiscPosition(englishReference, english, "the English chip", scale);

        StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var germanNoChip = Laid.Open(Windows(Capacity), scale: scale);
            germanNoChip.RegisterFaces(Capacity);
            var germanReference = DiscCentre(germanNoChip);
            _output.WriteLine($"at {scale:0.0}x German: no chip, ✕ centre {Point(germanReference)}");

            using var germanChip = Laid.Open(Windows(Capacity + 1), scale: scale);
            germanChip.RegisterFaces(Capacity);
            var germanChipBox = germanChip.Bounds(germanChip.Find<Border>(germanChip.Root, "OverflowChip")!);
            var german = DiscCentre(germanChip);
            _output.WriteLine($"at {scale:0.0}x German: chip {Box(germanChipBox)}, ✕ centre {Point(german)}");
            AssertSameDiscPosition(germanReference, german, "the German chip", scale);

            // Both chips are on screen, and the German one (the wider) leaves the window's edges alone.
            Check.That(germanChipBox.Left).IsGreaterOrEqualThan(0);
            Check.That(germanChipBox.Right).IsLessOrEqualThan(ScreenWidth);
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    private static void AssertSameDiscPosition(Point reference, Point measured, string what, double scale) =>
        Check.WithCustomMessage(
                $"At {scale:0.0}x the ✕ moved when {what} appeared: {Point(reference)} → {Point(measured)}. "
                + "It is the escape; it must be where it was last time.")
            .That(measured.X).IsCloseTo(reference.X, 0.5);

    private static Point DiscCentre(Laid laid)
    {
        var disc = laid.Bounds(laid.Find<Border>(laid.Root, "CloseButton")!);
        return new Point(disc.Left + (disc.Width / 2), disc.Top + (disc.Height / 2));
    }

    private static string Point(Point p) => $"({FitText.Dip(p.X)}, {FitText.Dip(p.Y)})";

    /// <summary>Windows as the enumeration hands them over: numbered, ordinary, none minimised.</summary>
    private static IReadOnlyList<AppWindow> Windows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => Win(i, $"Window {i}"))
            .ToList();

    /// <summary>
    /// One Core <see cref="AppWindow"/> record — the switcher's window DESCRIPTOR, not a WPF
    /// window. Built target-typed on purpose: the no-visible-window guard's name heuristic reads
    /// <c>new AppWindow(</c> as a real window construction, and this file does build a real
    /// <see cref="TaskSwitcherWindow"/> (prepared, never shown), so the descriptor must not blur
    /// that scan.
    /// </summary>
    private static AppWindow Win(int hwnd, string title, bool minimized = false) =>
        new(new IntPtr(hwnd), title, (uint)(100 + hwnd), "app", minimized);

    private static string Box(Rect r) =>
        $"[{FitText.Dip(r.Left)}, {FitText.Dip(r.Top)} to {FitText.Dip(r.Right)}, {FitText.Dip(r.Bottom)}]";

    /// <summary>
    /// The colour a brush carries, for a claim about "the secondary text colour". The XAML
    /// resolves the brush from the theme dictionary and the token surface hands out another
    /// instance of the same colour, so the colour is the thing to compare.
    /// </summary>
    private static Color BrushColor(Brush? brush) =>
        brush is SolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"{brush} is not a solid brush.");

    private static bool Inside(Rect outer, Rect inner, double inset = 0) =>
        inner.Left >= outer.Left + inset - 0.5 && inner.Top >= outer.Top + inset - 0.5
        && inner.Right <= outer.Right - inset + 0.5 && inner.Bottom <= outer.Bottom - inset + 0.5;

    /// <summary>Euclidean distance from a point to the nearest point of a rect (0 when inside).</summary>
    private static double DistanceToRect(Rect rect, Point point)
    {
        var dx = Math.Max(Math.Max(rect.Left - point.X, 0), point.X - rect.Right);
        var dy = Math.Max(Math.Max(rect.Top - point.Y, 0), point.Y - rect.Bottom);
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>One realised tile: its generator container and the template's face Border.</summary>
    private sealed record TileSlot(DependencyObject Container, Border Border);

    /// <summary>
    /// The production window, laid out at the screen's size without ever being shown. Disposing it
    /// detaches the host and disposes the VM.
    /// </summary>
    private sealed class Laid : IDisposable
    {
        private readonly TaskSwitcherWindow _window;
        private readonly FitHost? _host;
        private readonly ItemsControl _panel;
        private readonly double _scale;

        private Laid(TaskSwitcherWindow window, FrameworkElement root, ItemsControl panel,
            TaskSwitcherViewModel viewModel, FitHost? host, double scale)
        {
            _window = window;
            Root = root;
            _panel = panel;
            ViewModel = viewModel;
            _host = host;
            _scale = scale;
        }

        internal FrameworkElement Root { get; }

        internal TaskSwitcherViewModel ViewModel { get; }

        /// <summary>
        /// Shows <paramref name="windows"/> through the real toggle, applies the owner's keep-out
        /// layout (a 112 DIP top band) and arranges the content — at <paramref name="scale"/> 1.0
        /// directly, at 2.0 through the fit host's 200 % render chain.
        /// </summary>
        internal static Laid Open(
            IReadOnlyList<AppWindow> windows, double scale = 1.0, IntPtr? foreground = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var messenger = new WeakReferenceMessenger();
            var viewModel = new TaskSwitcherViewModel(
                new FakeWindowManager(windows, foreground), new EyeCtrlSettings(), messenger);
            messenger.Send(new TaskSwitcherToggledMessage());

            var window = InvisibleTestWindow.Prepare(new TaskSwitcherWindow());
            window.DataContext = viewModel;
            var layout = TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", viewModel.Tiles.Count);
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
                // The 200 % chain: the frame carries a ScaleTransform of 2.0 / the device scale, so
                // the effective scale is exactly the owner's 2.0 whatever this machine reports.
                host = FitHost.Open(root, width: ScreenWidth, height: ScreenHeight, inheritFrom: window);
            }

            return new Laid(window, root, panel, viewModel, host, scale);
        }

        /// <summary>
        /// The View's post-layout registration pass for <paramref name="reachable"/> faces, then the
        /// close disc — exactly what <c>TaskSwitcherWindow.UpdateTilePositions</c> drives (the real
        /// one needs an HWND for PointToScreen, so the rectangles are supplied here).
        /// </summary>
        internal void RegisterFaces(int reachable)
        {
            var layout = TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", ViewModel.Tiles.Count);
            for (var i = 0; i < reachable && i < ViewModel.Tiles.Count; i++)
            {
                var column = i % 9;
                var row = i / 9;
                ViewModel.UpdateTilePosition(ViewModel.Tiles[i].Hwnd, new PhysicalRect(
                    layout.Block.X + (column * TaskSwitcherLayout.CellWidth) + 8,
                    layout.Block.Y + (row * TaskSwitcherLayout.CellHeight) + 8,
                    TaskSwitcherLayout.CellWidth - 16,
                    TaskSwitcherLayout.CellHeight - 16));
            }

            ViewModel.UpdateCloseButtonPosition(new PhysicalPoint(960, 1120));

            // The chip's Visibility follows the count, which is only known here: the row must be
            // laid out again before its geometry means anything (production does this implicitly —
            // the registration pass runs inside a dispatcher callback, and WPF lays out before the
            // next frame).
            Relayout();
        }

        /// <summary>Runs a layout pass over the anchored tree (the host's, at 200 %).</summary>
        internal void Relayout()
        {
            if (_host is { } host)
            {
                host.Relayout();
                return;
            }

            Root.Measure(new Size(ScreenWidth, ScreenHeight));
            Root.Arrange(new Rect(0, 0, ScreenWidth, ScreenHeight));
            Root.UpdateLayout();
        }

        /// <summary>Every realised tile's container and its template face, in display order.</summary>
        internal IReadOnlyList<TileSlot> Faces()
        {
            var slots = new List<TileSlot>();
            foreach (var tile in ViewModel.Tiles)
            {
                var container = _panel.ItemContainerGenerator.ContainerFromItem(tile)
                    ?? throw new InvalidOperationException(
                        $"tile '{tile.Title}' has no realised container — the XAML's ItemsSource binding is missing.");

                // The template's root Border: the first Border among the container's direct visual
                // children (the generator wraps the template root in a ContentPresenter) — the same
                // rule the production code's FindTemplateBorder applies.
                Border? face = null;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(container); i++)
                {
                    if (VisualTreeHelper.GetChild(container, i) is Border candidate)
                    {
                        face = candidate;
                        break;
                    }
                }

                slots.Add(new TileSlot(container, face
                    ?? throw new InvalidOperationException($"tile '{tile.Title}' has no template Border.")));
            }

            return slots;
        }

        /// <summary>The element <paramref name="name"/> inside <paramref name="searchRoot"/>'s visual tree.</summary>
        internal T? Find<T>(DependencyObject searchRoot, string name) where T : FrameworkElement =>
            searchRoot is T itself && itself.Name == name
                ? itself
                : Descendants<T>(searchRoot).FirstOrDefault(e => e.Name == name);

        /// <summary>
        /// The element's drawn rectangle in DIP. At 1.0 that is the layout transform; at 2.0 it is
        /// <c>PointToScreen</c> — the real render chain, physical pixels — divided by the host's
        /// effective 2.0, so the figure is the DIP the user's display draws.
        /// </summary>
        internal Rect Bounds(FrameworkElement element)
        {
            if (_host is null)
            {
                var origin = element.TranslatePoint(new Point(0, 0), Root);
                return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
            }

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

    /// <summary>Minimal IWindowManager fake: a fixed list plus the foreground the show path reads.</summary>
    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;
        private readonly IntPtr? _foreground;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows, IntPtr? foreground)
        {
            _windows = windows;
            _foreground = foreground;
        }

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;

        public void BringToFront(IntPtr hwnd) { }

        public IntPtr? TryGetEligibleForegroundWindow() => _foreground;

        public void Minimize(IntPtr hwnd) { }

        public void MaximizeOrRestore(IntPtr hwnd) { }

        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
