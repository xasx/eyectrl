namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Icons;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;
using IOFile = System.IO.File;
using IOPath = System.IO.Path;

/// <summary>
/// F-CLOSE: the Task Switcher's and Quick Actions' ✕ are now the shared
/// <see cref="DwellArcButton"/> every edge exit uses — the bold drawn <c>ActionIcon.Close</c> at
/// Solo, a 2 DIP rim with its 3 DIP dark halo, and the clockwise rim-arc progress — instead of a
/// hand-made Border with a thin text "✕" and a straight bottom bar.
///
/// <para>
/// The two hosts keep their name, size (100 DIP = 2 × the shared r50 hit radius) and the view
/// models' 600 ms dwell, so this file pins the ANATOMY and the drawn = hit chain. F-CORNER moved
/// the switcher's ✕ into the window's top-right corner, so its drawn = hit test runs the REAL
/// <c>UpdateTilePositions</c> pass (through the one-way hidden-registration seam) instead of
/// restating the target expression, and the corner/halo pins live here too.
/// </para>
///
/// <para>
/// Nothing here shows a window. The switcher is non-layered, so it goes through
/// <see cref="InvisibleTestWindow.PrepareCloaked"/>; the grid goes through
/// <see cref="InvisibleTestWindow.Prepare"/>. Layout and <c>PointToScreen</c> run in an off-screen
/// <c>WS_POPUP</c> HwndSource, the harness <c>QuickActionsGridHost</c> already uses.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (App)")]
public class CloseArcWindowsTests
{
    /// <summary>The owner's screen in DIP: 3840×2400 at 200 %.</summary>
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    // ── anatomy: one Solo Close disc, no text glyph, no bar ─────────────────────────────────

    [StaFact]
    public void TheTaskSwitcherClose_IsOneSoloCloseDisc_WithRimHaloAndRimArc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        try
        {
            LayoutContent(window);
            AssertCloseDiscAnatomy(window, window.CloseDisc, "TaskSwitcherWindow");
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TheQuickActionsClose_IsOneSoloCloseDisc_WithRimHaloAndRimArc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new QuickActionsGridWindow());
        try
        {
            LayoutContent(window);
            AssertCloseDiscAnatomy(window, window.CloseDisc, "QuickActionsGridWindow");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The shared body of the anatomy pins: the host holds exactly ONE disc (two Ellipses — the
    /// halo and the fill — plus the halo path, the progress path and the one filled icon layer),
    /// the icon is <see cref="ActionIcon.Close"/> at Solo and the rim is the 2 DIP BorderSubtle /
    /// 3 DIP StrokeHalo pair <c>DwellArcButton</c> owns. And no part of the old construction is
    /// left inside the host: no U+2715 TextBlock and no ProgressBar.
    /// </summary>
    private static void AssertCloseDiscAnatomy(Window window, DwellArcButton? disc, string windowName)
    {
        Check.WithCustomMessage($"{windowName} drew no close disc (CloseDisc is null).")
            .That(disc).IsNotNull();

        var layer = window.FindName("CloseLayer") as Canvas;
        Check.WithCustomMessage($"{windowName} has no Canvas named CloseLayer.")
            .That(layer).IsNotNull();

        // Exactly one disc: AddTo adds the halo, the fill, the two arc paths and the one icon
        // layer. Counting by element kind (and asserting the instances) is what makes "one disc"
        // a fact rather than a described intention.
        var ellipses = layer!.Children.OfType<Ellipse>().ToList();
        Check.WithCustomMessage($"{windowName}'s CloseLayer must hold exactly the disc's two Ellipses.")
            .That(ellipses).HasSize(2);
        Check.That(ellipses.Contains(disc!.RimHalo)).IsTrue();
        Check.That(ellipses.Contains(disc.Background)).IsTrue();
        Check.WithCustomMessage($"{windowName}'s CloseLayer must hold exactly the three disc Paths.")
            .That(layer.Children.OfType<Path>().ToList()).HasSize(3);

        // The rim pair. The numbers 2 and the geometry of the halo are DwellArcButton's own
        // pinned contract (DwellArcButtonRimTests); what this asserts is that the close disc IS
        // one — not a hand-rolled look-alike.
        Check.WithCustomMessage($"{windowName}'s close disc rim must be the 2 DIP BorderSubtle rim.")
            .That(disc.Background.StrokeThickness).IsEqualTo(2.0);
        Check.That(disc.Background.Stroke).IsSameReferenceAs(Tokens.BrushBorderSubtle);
        Check.That(disc.RimHalo.Stroke).IsSameReferenceAs(Tokens.BrushStrokeHalo);
        Check.WithCustomMessage($"{windowName}'s close disc has no halo ring outside its rim.")
            .That(disc.RimHalo.Width).IsStrictlyGreaterThan(disc.Background.Width);

        // The glyph: the drawn, filled ActionIcon.Close — no text symbol anywhere in the host.
        Check.WithCustomMessage($"{windowName}'s close disc draws no filled icon layer.")
            .That(disc.IconFill).IsNotNull();
        Check.WithCustomMessage(
                $"{windowName}'s close disc must draw the fill-only ActionIcon.Close — no stroked layer.")
            .That(disc.IconStroke).IsNull();
        Check.WithCustomMessage($"{windowName}'s close disc is not the ActionIcon.Close geometry.")
            .That(ReferenceEquals(disc.IconFill!.Data, ActionIcons.For(ActionIcon.Close).Fill)).IsTrue();
        Check.That(disc.Symbol).IsNull();

        var host = (FrameworkElement)window.FindName("CloseButton")!;
        var texts = FitText.Descendants<TextBlock>(host).ToList();
        Check.WithCustomMessage(
                $"{windowName}'s CloseButton still contains a U+2715 text glyph — the owner asked "
                + "for the drawn icon, not the thin symbol.")
            .That(texts.Any(t => t.Text == "\u2715")).IsFalse();
        Check.WithCustomMessage($"{windowName}'s CloseButton still contains a ProgressBar.")
            .That(FitText.Descendants<ProgressBar>(host).ToList()).IsEmpty();

        // The UIA name still rides an automation anchor (the drawn icon has no peer of its own),
        // exactly like every icon-only edge button.
        var anchor = disc.AutomationAnchor;
        Check.WithCustomMessage($"{windowName}'s close disc has no UIA anchor.")
            .That(anchor).IsNotNull();
        Check.That(AutomationProperties.GetName(anchor!)).IsEqualTo("Close");
    }

    // ── drawn = hit: the disc's centre and radius ARE the registered target ─────────────────

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void QuickActionsCloseDisc_IsDrawnWhereItsR50TargetIsRegistered(double scale)
    {
        WithDpi(scale, () =>
        {
            using var grid = QuickActionsGridHost.Open(scale);
            var window = grid.Window;
            var vm = grid.Vm;

            // The production push: SyncTilePositions registers the host's centre, in physical
            // pixels, as the close target.
            window.SyncTilePositions();
            var target = vm.DetectorForTest.Targets
                .Single(t => t.Id == QuickActionsGridViewModel.CloseTargetId);

            var disc = window.CloseDisc!;
            var drawnCentre = grid.ToScreen(DrawnDiscCentre(grid.InContent(window.CloseLayer), disc));
            Check.WithCustomMessage(
                    $"at DPI {scale} the Quick Actions ✕'s drawn disc is centred "
                    + $"({drawnCentre.X:0.##},{drawnCentre.Y:0.##}) but its hit circle is at "
                    + $"({target.Center.X:0.##},{target.Center.Y:0.##}).")
                .That(drawnCentre.X).IsCloseTo(target.Center.X, 1e-6);
            Check.That(drawnCentre.Y).IsCloseTo(target.Center.Y, 1e-6);
            Check.WithCustomMessage(
                    $"at DPI {scale} the drawn disc is {disc.Background.Width / 2} DIP in radius "
                    + $"but the hit circle is {target.Radius / scale} DIP.")
                .That(disc.Background.Width / 2).IsCloseTo(target.Radius / scale, 1e-9);
        });
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TaskSwitcherCloseDisc_IsDrawnWhereItsR50TargetIsRegistered(double scale)
    {
        WithDpi(scale, () =>
        {
            using var host = SwitcherCloseHost.Open(scale);
            var window = host.Window;
            var vm = host.Vm;

            // The REAL production pass — no restated target expression. The window is cloaked and
            // never shown; the one-way seam lets the post-layout registration run on the off-screen
            // host, exactly as ShowFullScreen's Loaded callback would on screen. If the View stops
            // reading CloseButton's centre (or moves the host), this test follows the code and the
            // drawn = hit comparison below fails.
            window.Configure(vm, new NoIcons());
            window.AllowHiddenRegistrationForTests();
            window.UpdateTilePositions();

            var target = vm.DetectorForTest.Targets
                .Single(t => t.Id == TaskSwitcherViewModel.CloseTargetId);

            var disc = window.CloseDisc!;
            var drawnCentre = host.ToScreen(DrawnDiscCentre(host.InContent(window.CloseLayer), disc));
            Check.WithCustomMessage(
                    $"at DPI {scale} the switcher ✕'s drawn disc is centred "
                    + $"({drawnCentre.X:0.##},{drawnCentre.Y:0.##}) but its hit circle is at "
                    + $"({target.Center.X:0.##},{target.Center.Y:0.##}).")
                .That(drawnCentre.X).IsCloseTo(target.Center.X, 1e-6);
            Check.That(drawnCentre.Y).IsCloseTo(target.Center.Y, 1e-6);
            Check.WithCustomMessage(
                    $"at DPI {scale} the drawn disc is {disc.Background.Width / 2} DIP in radius "
                    + $"but the hit circle is {target.Radius / scale} DIP.")
                .That(disc.Background.Width / 2).IsCloseTo(target.Radius / scale, 1e-9);

            // …and the shared place is the backdrop hide ✕'s corner: (screen width − 53, 53) DIP.
            var hostRect = host.InContent((FrameworkElement)window.FindName("CloseButton")!);
            Check.That(hostRect.Left + (hostRect.Width / 2))
                .IsCloseTo(ScreenWidth - UIConstants.BackdropHideButtonOffset, 0.5);
            Check.That(hostRect.Top + (hostRect.Height / 2))
                .IsCloseTo(UIConstants.BackdropHideButtonOffset, 0.5);
        });
    }

    /// <summary>
    /// The disc's DRAWN centre in the layer's own DIP space, read from the Ellipse the factory
    /// positioned — not from the host's centre. That is what makes the assertion able to catch a
    /// disc created off-centre.
    /// </summary>
    private static Point DrawnDiscCentre(Rect layerInContent, DwellArcButton disc) =>
        new(
            layerInContent.Left + Canvas.GetLeft(disc.Background) + (disc.Background.Width / 2),
            layerInContent.Top + Canvas.GetTop(disc.Background) + (disc.Background.Height / 2));

    // ── the halo is not clipped, and it reaches nothing it did not reach before ─────────────

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TheTaskSwitcherCloseDisc_AndItsHalo_StayOnScreen_AndClearTheHintAndChip(double scale)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        WithDpi(scale, () =>
        {
            // Five windows, one visible slot: the overflow chip is showing, so its clearance is a
            // real measurement rather than a collapsed element's empty rect.
            using var host = SwitcherCloseHost.Open(scale, windows: 5, capacity: 1);
            var window = host.Window;

            window.Configure(host.Vm, new NoIcons());
            window.AllowHiddenRegistrationForTests();
            window.UpdateTilePositions();
            host.Relayout();

            var disc = window.CloseDisc!;
            AssertNoAncestorClips(disc.Background, host.Root, "TaskSwitcherWindow");

            var halo = ConservativeHaloRect(host.InContent(window.CloseLayer), disc);

            // F-CORNER: the whole PAINTED halo is on screen. WPF insets a Shape's stroke (the
            // Ellipse is dia + 2 x 2.5 wide and the 3 DIP stroke sits inside that box), so the
            // real edge is +2.5 DIP past the disc — at the 53 DIP offset that leaves 0.5 DIP of
            // margin. The rejected 40 DIP offset would put 12.5 DIP of halo off the top/right
            // edge and fail here.
            var painted = PaintedHaloRect(host.InContent(window.CloseLayer), disc);
            Check.WithCustomMessage($"at {scale:0.0}x the ✕'s painted halo {Box(painted)} leaves the screen.")
                .That(painted.Left >= -1e-6 && painted.Top >= -1e-6
                      && painted.Right <= ScreenWidth + 1e-6 && painted.Bottom <= ScreenHeight + 1e-6)
                .IsTrue();

            var hint = host.InContent((FrameworkElement)window.FindName("HintText")!);
            Check.WithCustomMessage(
                    $"the ✕'s halo {Box(halo)} overlaps the hint text {Box(hint)}.")
                .That(halo.IntersectsWith(hint)).IsFalse();

            var chipElement = (FrameworkElement)window.FindName("OverflowChip")!;
            Check.WithCustomMessage("the overflow chip was not shown, so its clearance proves nothing.")
                .That(chipElement.Visibility).IsEqualTo(Visibility.Visible);
            var chip = host.InContent(chipElement);
            Check.WithCustomMessage($"the ✕'s halo {Box(halo)} overlaps the overflow chip {Box(chip)}.")
                .That(halo.IntersectsWith(chip)).IsFalse();

            // Tiles, and therefore their thumbnails (fitted INSIDE a face), are below the corner.
            foreach (var slot in host.Faces())
            {
                var face = host.InContent(slot.Border);
                Check.WithCustomMessage($"at {scale:0.0}x the ✕'s halo {Box(halo)} overlaps a tile {Box(face)}.")
                    .That(halo.IntersectsWith(face)).IsFalse();
            }
        });
    }

    /// <summary>
    /// Source pin for the registration expression the cloaked real-path test above executes: the
    /// close target's centre is <c>CloseButton</c>'s OWN centre read through <c>PointToScreen</c>,
    /// and it is pushed through <c>UpdateCloseButtonPosition</c>. A future edit that computes the
    /// corner from a second literal, or registers before layout, fails here even if the geometry
    /// happens to coincide today.
    /// </summary>
    [Fact]
    public void UpdateTilePositions_RegistersTheCloseTargetFromTheHostsOwnCentre()
    {
        var source = IOFile.ReadAllText(IOPath.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var start = source.IndexOf("internal void UpdateTilePositions()", StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        var body = SourceScanner.StripComments(
            source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)]);

        Check.WithCustomMessage("UpdateTilePositions no longer registers the close target from CloseButton.")
            .That(Regex.IsMatch(body, @"CloseButton\.PointToScreen\(\s*new Point\(\s*CloseButton\.ActualWidth\s*/\s*2"))
            .IsTrue();
        Check.WithCustomMessage("UpdateTilePositions no longer pushes the close centre through the view model.")
            .That(body.Contains("UpdateCloseButtonPosition(")).IsTrue();
    }

    [StaFact]
    public void TheQuickActionsCloseHalo_IsNotClipped_AndClearsTheTilesAndTheCardEdge()
    {
        WithDpi(1.0, () =>
        {
            using var grid = QuickActionsGridHost.Open(1.0);
            var window = grid.Window;
            var disc = window.CloseDisc!;

            AssertNoAncestorClips(disc.Background, grid.Root, "QuickActionsGridWindow");

            var halo = ConservativeHaloRect(grid.InContent(window.CloseLayer), disc);
            foreach (var face in grid.TileFaces())
            {
                var tile = grid.InContent(face);
                Check.WithCustomMessage(
                        $"the ✕'s halo {Box(halo)} overlaps a tile face {Box(tile)} — the halo must "
                        + "stay in the 8 DIP band above the tile rail.")
                    .That(halo.IntersectsWith(tile)).IsFalse();
            }

            // The card edge: the halo must stay inside the window's own content root (the dark
            // card). Four DIP outside the disc is still 8 DIP inside the 12 DIP margin.
            var card = grid.InContent(grid.Root);
            Check.WithCustomMessage($"the ✕'s halo {Box(halo)} leaves the grid card {Box(card)}.")
                .That(halo.Left >= card.Left - 1e-6 && halo.Top >= card.Top - 1e-6
                      && halo.Right <= card.Right + 1e-6 && halo.Bottom <= card.Bottom + 1e-6)
                .IsTrue();
        });
    }

    /// <summary>
    /// The halo's PAINTED outer edge in the host's DIP space: the Ellipse's own half-width.
    /// WPF insets a <c>Shape</c>'s stroke, so a <c>RimHalo</c> of <c>dia + 2 × 2.5</c> with a 3 DIP
    /// stroke paints its rim at exactly half that width — +2.5 DIP past the disc, NOT +4. The
    /// earlier wording (and the conservative helper below) treated the stroke as if it reached its
    /// full width outside the rim; that is the comment this ticket corrects.
    /// </summary>
    private static Rect PaintedHaloRect(Rect layerInContent, DwellArcButton disc)
    {
        var overshoot = (disc.RimHalo.Width / 2) - (disc.Background.Width / 2);
        return Inflated(disc, layerInContent, overshoot);
    }

    /// <summary>
    /// The conservative halo box: the painted edge plus half the stroke (+4 DIP). The tile keep-out
    /// (<c>TaskSwitcherLayout.CloseHaloDip</c>) uses this bound. The clearance checks use it too, so
    /// they fail if even the conservative box reaches something it must not.
    /// </summary>
    private static Rect ConservativeHaloRect(Rect layerInContent, DwellArcButton disc)
    {
        var overshoot = (disc.RimHalo.Width / 2) + (disc.RimHalo.StrokeThickness / 2)
                        - (disc.Background.Width / 2);
        return Inflated(disc, layerInContent, overshoot);
    }

    private static Rect Inflated(DwellArcButton disc, Rect layerInContent, double overshoot)
    {
        var discRect = new Rect(
            layerInContent.Left + Canvas.GetLeft(disc.Background),
            layerInContent.Top + Canvas.GetTop(disc.Background),
            disc.Background.Width,
            disc.Background.Height);
        discRect.Inflate(overshoot, overshoot);
        return discRect;
    }

    /// <summary>
    /// Walks from the drawn disc up to the content root and fails on any ancestor that would cut
    /// the halo off: <c>ClipToBounds</c> set, or a <c>Clip</c> geometry assigned.
    /// </summary>
    private static void AssertNoAncestorClips(DependencyObject from, DependencyObject root, string windowName)
    {
        var offenders = new List<string>();
        for (var node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { ClipToBounds: true } clipping)
                offenders.Add($"{node.GetType().Name} '{clipping.Name}' has ClipToBounds=true");
            if (node is UIElement { Clip: not null } clipped)
                offenders.Add($"{node.GetType().Name} '{(clipped as FrameworkElement)?.Name}' has a Clip");

            if (ReferenceEquals(node, root)) break;
        }

        Check.WithCustomMessage(
                $"{windowName}: an ancestor of the close disc clips it, so the halo (2.5 DIP painted, "
                + "4 DIP conservative) would be cut off:\n  " + string.Join("\n  ", offenders))
            .That(offenders).IsEmpty();
    }

    // ── progress/pause initial state on a re-show or a re-load ──────────────────────────────

    [StaFact]
    public void QuickActionsClose_StartsAtProgressZero_OnAReload()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.Prepare(new QuickActionsGridWindow());
        try
        {
            var vm = new QuickActionsGridViewModel(
                new NoActions(), new EyeCtrlSettings(), new WeakReferenceMessenger());
            window.DataContext = vm;
            var disc = window.CloseDisc!;

            vm.CloseDwellProgress = 0.6;
            Check.That(disc.ProgressPath.Visibility).IsEqualTo(Visibility.Visible);

            vm.Reload();   // the show path
            Check.WithCustomMessage("a reload must start the close arc at 0, not carry the last dwell")
                .That(disc.ProgressPath.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void TaskSwitcherClose_StartsAtProgressZero_OnAShow()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
        try
        {
            using var vm = new TaskSwitcherViewModel(
                new CloseArcWindowManager(0), new EyeCtrlSettings(), new WeakReferenceMessenger());
            window.DataContext = vm;
            var disc = window.CloseDisc!;

            vm.CloseDwellProgress = 0.6;
            vm.CloseDwellPaused = true;

            vm.Receive(new TaskSwitcherToggledMessage());   // ShowOverlay resets the close state
            Check.WithCustomMessage("a show must start the close arc at 0, not carry the last dwell")
                .That(disc.ProgressPath.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.WithCustomMessage("a show must not carry a held look into the new session")
                .That(disc.ProgressPath.Opacity).IsEqualTo(1.0);
        }
        finally
        {
            window.Close();
        }
    }

    // ── a source pin: the text glyph cannot quietly come back ───────────────────────────────

    /// <summary>
    /// The two XAML files cannot carry the old construction inside the close host: no U+2715
    /// (neither the entity nor the character) and no ProgressBar between
    /// <c>x:Name="CloseButton"</c> and its closing tag, and the layer the code fills is there.
    /// This is deliberately a text scan: it fails on a re-introduced character, which no
    /// constructed test can see if the drawing code is bypassed.
    /// </summary>
    [Fact]
    public void NeitherCloseHostXaml_CarriesTheTextGlyphOrABar()
    {
        foreach (var relative in new[]
                 {
                     "src/EyeCtrl.App/Overlays/TaskSwitcherWindow.xaml",
                     "src/EyeCtrl.App/Overlays/QuickActionsGridWindow.xaml",
                 })
        {
            var path = IOPath.Combine(SourceScanner.RepoRoot().FullName,
                relative.Replace('/', IOPath.DirectorySeparatorChar));
            var text = IOFile.ReadAllText(path);

            var start = text.IndexOf("x:Name=\"CloseButton\"", StringComparison.Ordinal);
            Check.WithCustomMessage($"{relative} has no element named CloseButton.")
                .That(start).IsStrictlyGreaterThan(-1);
            var end = text.IndexOf("</Border>", start, StringComparison.Ordinal);
            Check.WithCustomMessage($"{relative}: CloseButton is not a Border.").That(end)
                .IsStrictlyGreaterThan(start);

            var host = text[start..end];
            Check.WithCustomMessage(
                    $"{relative}: the close host still carries the text glyph (U+2715) — the owner "
                    + "asked for the drawn Close icon.")
                .That(host.Contains("&#x2715;", StringComparison.Ordinal)
                      || host.Contains("\u2715", StringComparison.Ordinal)).IsFalse();
            Check.WithCustomMessage($"{relative}: the close host still carries a ProgressBar.")
                .That(host.Contains("<ProgressBar", StringComparison.Ordinal)).IsFalse();
            Check.WithCustomMessage($"{relative}: the close host has no CloseLayer for the drawn disc.")
                .That(host.Contains("x:Name=\"CloseLayer\"", StringComparison.Ordinal)).IsTrue();
        }
    }

    // ── harness ────────────────────────────────────────────────────────────────────────────

    private static void WithDpi(double scale, Action body)
    {
        var (previousX, previousY) = (DpiHelper.ScaleX, DpiHelper.ScaleY);
        try
        {
            DpiHelper.Update(scale, scale);
            body();
        }
        finally
        {
            DpiHelper.Update(previousX, previousY);
        }
    }

    /// <summary>
    /// Lays a constructed window's content out at the owner's screen size without showing it —
    /// enough for the visual tree (ancestor walks, FindName descendants) to exist.
    /// </summary>
    private static void LayoutContent(Window window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(ScreenWidth, ScreenHeight));
        root.Arrange(new Rect(0, 0, ScreenWidth, ScreenHeight));
        root.UpdateLayout();
    }

    private static string Box(Rect r) =>
        $"[{r.Left:0.##}, {r.Top:0.##} to {r.Right:0.##}, {r.Bottom:0.##}]";

    private sealed class NoActions : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => Array.Empty<QuickAction>();
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => null;
    }

    /// <summary>
    /// No icon seam at all: <c>UpdateTilePositions</c> never resolves icons (that is
    /// <c>ShowFullScreen</c>'s step), so the real-path registration test needs the dependency
    /// only to reach the view model through <c>Configure</c>.
    /// </summary>
    private sealed class NoIcons : IAppIconProvider
    {
        public ImageSource GetIcon(IntPtr hwnd, uint processId) => null!;
    }

    /// <summary>An empty window enumeration — the switcher's hint row and close disc need no tiles.</summary>
    private sealed class CloseArcWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal CloseArcWindowManager(int count) =>
            _windows = Enumerable.Range(1, count).Select(i => Win(5000 + i, $"Window {i}")).ToList();

        /// <summary>
        /// One Core <see cref="AppWindow"/> record — the switcher's window DESCRIPTOR, not a WPF
        /// window. Built target-typed on purpose: the no-visible-window guard's name heuristic
        /// reads <c>new AppWindow(</c> as a real window construction.
        /// </summary>
        private static AppWindow Win(int hwnd, string title) =>
            new(new IntPtr(hwnd), title, (uint)(200 + hwnd), "app");

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }

    /// <summary>The switcher's production content, laid out off-screen and never shown.</summary>
    private sealed class SwitcherCloseHost : IDisposable
    {
        private const int WsPopup = unchecked((int)0x80000000);

        private readonly HwndSource _source;
        private readonly Border _frame;

        private SwitcherCloseHost(
            TaskSwitcherWindow window, TaskSwitcherViewModel vm,
            HwndSource source, Border frame, FrameworkElement root)
        {
            Window = window;
            Vm = vm;
            Root = root;
            _source = source;
            _frame = frame;
        }

        internal TaskSwitcherWindow Window { get; }
        internal TaskSwitcherViewModel Vm { get; }
        internal FrameworkElement Root { get; }

        /// <summary>
        /// Builds the switcher with <paramref name="windows"/> enumerated windows (none shown),
        /// applies <paramref name="capacity"/> when given, and lays the content out at the
        /// owner's screen size through an off-screen host whose effective scale is
        /// <paramref name="scale"/>.
        /// </summary>
        internal static SwitcherCloseHost Open(double scale, int windows = 0, int? capacity = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var vm = new TaskSwitcherViewModel(
                new CloseArcWindowManager(windows), new EyeCtrlSettings(), new WeakReferenceMessenger());
            var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
            window.DataContext = vm;

            if (windows > 0)
            {
                vm.Receive(new TaskSwitcherToggledMessage());
                if (capacity is { } cap)
                    vm.ApplyTileCapacity(cap);
            }

            window.ApplyLayout(
                TaskSwitcherLayout.Compute(ScreenWidth, ScreenHeight, null, null, vm.PanelItems.Count),
                ScreenWidth);

            var root = (FrameworkElement)window.Content;
            window.Content = null;
            root.DataContext = window.DataContext;

            var source = new HwndSource(new HwndSourceParameters("close-arc-switcher")
            {
                WindowStyle = WsPopup,
                PositionX = -30000,
                PositionY = -30000,
                Width = 1,
                Height = 1,
            });

            var device = source.CompositionTarget!.TransformToDevice.M11;
            var frame = new Border
            {
                Child = root,
                Width = ScreenWidth,
                Height = ScreenHeight,
                DataContext = window.DataContext,
                RenderTransform = new ScaleTransform(scale / device, scale / device),
            };
            source.RootVisual = frame;
            frame.Measure(new Size(ScreenWidth, ScreenHeight));
            frame.Arrange(new Rect(0, 0, ScreenWidth, ScreenHeight));
            frame.UpdateLayout();

            var effective = (root.PointToScreen(new Point(100, 0)).X
                             - root.PointToScreen(new Point(0, 0)).X) / 100.0;
            if (Math.Abs(effective - scale) > 1e-6)
            {
                throw new InvalidOperationException(
                    $"the off-screen switcher host's effective scale is {effective:0.###}, not "
                    + $"{scale}. The registered targets would not describe the DPI under test.");
            }

            return new SwitcherCloseHost(window, vm, source, frame, root);
        }

        /// <summary>An element's rectangle in the content's DIP space.</summary>
        internal Rect InContent(FrameworkElement element) =>
            element.TransformToAncestor(_frame).TransformBounds(new Rect(element.RenderSize));

        /// <summary>A content-space DIP point mapped to the physical pixel the View would report.</summary>
        internal Point ToScreen(Point contentPoint) => Root.PointToScreen(contentPoint);

        /// <summary>Every realised panel tile's template face Border, in display order.</summary>
        internal IReadOnlyList<(TaskSwitcherTileViewModel Tile, Border Border)> Faces()
        {
            var panel = (ItemsControl)Window.FindName("TilesPanel")!;
            var faces = new List<(TaskSwitcherTileViewModel, Border)>();
            foreach (var tile in Vm.PanelItems)
            {
                if (panel.ItemContainerGenerator.ContainerFromItem(tile) is not FrameworkElement container)
                    continue;

                Border? face = null;
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(container); i++)
                {
                    if (VisualTreeHelper.GetChild(container, i) is Border candidate)
                    {
                        face = candidate;
                        break;
                    }
                }

                if (face is not null)
                    faces.Add((tile, face));
            }

            return faces;
        }

        /// <summary>Re-lays the hosted content after a binding changed its size.</summary>
        internal void Relayout()
        {
            _frame.Measure(new Size(ScreenWidth, ScreenHeight));
            _frame.Arrange(new Rect(0, 0, ScreenWidth, ScreenHeight));
            _frame.UpdateLayout();
        }

        public void Dispose()
        {
            _source.RootVisual = null;
            _source.Dispose();
            _frame.Child = null;
            Window.DataContext = null;
            Window.Close();
            Vm.Dispose();
        }
    }
}
