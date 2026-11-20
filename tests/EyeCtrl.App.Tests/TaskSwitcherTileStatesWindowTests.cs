namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
using EyeCtrl.Platform.Icons;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S2-B on the drawn switcher: the "Minimiert" badge inside a tile face, the "Keine offenen
/// Fenster" message on an empty grid, and the "Weitere Fenster: N" overflow chip in the hint row.
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
/// label. The ✕ is the surface's escape: F-CORNER puts it in the window's top-right corner, so
/// the pins assert the chip clears its hit circle by the whole screen diagonal and that the tiles
/// never reach it (the 107 DIP corner band plus the 24 DIP clearance).
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class TaskSwitcherTileStatesWindowTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    private readonly ITestOutputHelper _output;

    public TaskSwitcherTileStatesWindowTests(ITestOutputHelper output) => _output = output;

    /// <summary>A 112 DIP docked top test band (not the owner's real 140 DIP one): it leaves 8 × 5 tiles (F-GAP).</summary>
    private static readonly TaskSwitcherLayout.DipRect TopBand = new(0, 0, 1920, 112);

    private const int Capacity = 40;   // 8 columns × 5 rows at 1920 × 1200 with the 112 DIP band

    /// <summary>
    /// The "Minimiert" badge, in every case that can move it: DPI 1.0 and the owner's 200 %, English
    /// and German. What it pins:
    /// <list type="bullet">
    /// <item>the face is still 184 × 150 DIP (S4-C: the badge must not widen the perceived tile — the
    /// face is the dwell target);</item>
    /// <item>the badge sits INSIDE the preview box, inside the face's 8 DIP padding and centred on
    /// it (a minimised tile is the one state whose box has no live picture);</item>
    /// <item>it HUGS ITS CONTENT: its width is its own bar + gap + word + padding, never the content
    /// column's width. With a long, trimmed title a Border without HorizontalAlignment stretches to
    /// ~167 DIP and reads as a light band across the tile, not as a badge;</item>
    /// <item>it ends above the progress bar;</item>
    /// <item>the fallback column's natural height (icon + badge) still fits the 94 DIP box: a font
    /// or padding change that grew it would clip silently.</item>
    /// </list>
    /// </summary>
    [StaTheory]
    [InlineData(1.0, "English")]
    [InlineData(2.0, "English")]
    [InlineData(1.0, "German")]
    [InlineData(2.0, "German")]
    public void MinimisedWindow_DrawsTheBadge_InsideItsFace_AtEveryScaleAndCulture(double scale, string language)
    {
        var expectedWord = language == "German" ? "Minimiert" : "Minimized";
        if (language == "German") StringTable.Activate(ShippedGerman.Load());
        try
        {
            // The long title is the case that exposes a stretched badge: the title is trimmed inside
            // the content column, and a Border left at the default Stretch widens with it.
            using var laid = Laid.Open(
                [
                    Win(1, "Quarterly report - a long document title that the tile trims", minimized: true),
                    Win(2, "Editor"),
                ], scale: scale);

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
            // face must still be the 184 × 150 DIP the template promises (216 × 182 cell less the
            // 16 DIP margin, F-GAP).
            var face = laid.Bounds(faces[0].Border);
            var badgeBox = laid.Bounds(badge);
            var bar = Descendants<Border>(badge).Single(b => b.Name == "MinimizedGlyph");
            var word = Descendants<TextBlock>(badge).Single();
            var room = face.Width - 16;   // the template's 8 DIP padding on each side

            Check.WithCustomMessage("The minimised tile grew: the perceived tile must not change.")
                .That(face.Width).IsEqualTo(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip);
            Check.That(face.Height).IsEqualTo(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip);
            Check.WithCustomMessage($"The badge {Box(badgeBox)} leaves its face {Box(face)}.")
                .That(Inside(face, badgeBox, inset: 8)).IsTrue();
            Check.WithCustomMessage("The badge is not centred in its tile face.")
                .That(badgeBox.Left + (badgeBox.Width / 2)).IsCloseTo(face.Left + (face.Width / 2), 0.5);

            // The width pin: the pill is exactly its own content — the drawn bar, the gap after it,
            // the word and the Border's padding — and never the content column's (title-driven) width.
            Check.That(word.Text).IsEqualTo(expectedWord);
            var contentWidth = badge.Padding.Left + bar.Width + bar.Margin.Right
                               + FitText.NaturalWidth(word, word.Text) + badge.Padding.Right;
            Check.WithCustomMessage(
                    $"{language} at {scale:0.0}x: the badge is {FitText.Dip(badgeBox.Width)} DIP wide, its own "
                    + $"content needs {FitText.Dip(contentWidth)} DIP, and the content column is "
                    + $"{FitText.Dip(room)} DIP — a badge at the column's width is stretched, not hugging.")
                .That(badgeBox.Width).IsCloseTo(contentWidth, 1.0);

            // It ends above the accent progress bar (it is a caption, not a second progress surface).
            var progress = Descendants<ProgressBar>(faces[0].Border).Single();
            var progressTop = laid.Bounds(progress).Top;
            Check.WithCustomMessage($"The badge {Box(badgeBox)} reaches into the progress bar at y {FitText.Dip(progressTop)}.")
                .That(badgeBox.Bottom).IsLessOrEqualThan(progressTop + 0.5);

            // The headroom: the icon + badge column centres vertically in the preview box. Measure
            // the column without a height bound and compare with the box it is given.
            var content = (FrameworkElement)badge.Parent;
            var slot = LayoutInformation.GetLayoutSlot(content);
            content.Measure(new Size(slot.Width, double.PositiveInfinity));
            var needed = content.DesiredSize.Height;
            laid.Relayout();
            Check.WithCustomMessage(
                    $"{language} at {scale:0.0}x: the tile's content column needs {FitText.Dip(needed)} DIP "
                    + $"but the row gives it {FitText.Dip(slot.Height)} — it would be clipped silently.")
                .That(needed).IsLessOrEqualThan(slot.Height);

            // The badge is a word plus a drawn bar, both in the secondary text colour — and the tile
            // is NOT dimmed: the paused-dwell cue owns dimming (DwellPausedOpacityConverter), and a
            // restored window must not read as "held".
            _output.WriteLine(
                $"{language} at {scale:0.0}x: face {Box(face)}; badge {Box(badgeBox)} "
                + $"(content {FitText.Dip(contentWidth)}, column {FitText.Dip(room)}); "
                + $"progress top {FitText.Dip(progressTop)}; column needs {FitText.Dip(needed)} of "
                + $"{FitText.Dip(slot.Height)} DIP (headroom {FitText.Dip(slot.Height - needed)})");
            Check.That(BrushColor(word.Foreground)).IsEqualTo(Tokens.TextSecondary);
            Check.WithCustomMessage("The minimised cue must not dim the tile — that is the paused cue.")
                .That(faces[0].Border.Opacity).IsEqualTo(1.0);
            Check.WithCustomMessage("The badge's drawn minimise bar has no ink.")
                .That(bar.ActualWidth).IsStrictlyGreaterThan(0);
            Check.That(bar.ActualHeight).IsStrictlyGreaterThan(0);
            Check.That(BrushColor(bar.Background)).IsEqualTo(Tokens.TextSecondary);
        }
        finally
        {
            if (language == "German") StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// S3-B, reshaped by S4-C: every window tile draws its app icon TWICE from the same per-window
    /// seam — at <b>20 DIP</b> in the identity row and at <b>40 DIP</b> as the preview box's
    /// fallback — both with high-quality scaling, and each tile shows ITS OWN window's icon (the
    /// grid asks the per-window seam once per tile, so two Store apps in one frame-host process can
    /// never share an icon). At 1.0 and at the owner's 200 %, English and German, the icons, the
    /// title, the badge and the progress bar must all stay inside the 184 × 150 DIP face, the title
    /// must stay OUT of the preview box (a DWM thumbnail draws above everything inside it), and the
    /// fallback column must still fit the box (the badge theory measures the same column).
    /// </summary>
    [StaTheory]
    [InlineData(1.0, "English")]
    [InlineData(2.0, "English")]
    [InlineData(1.0, "German")]
    [InlineData(2.0, "German")]
    public void WindowTile_DrawsItsOwnAppIcon_At20InTheRowAnd40InThePreviewFallback(
        double scale, string language)
    {
        if (language == "German") StringTable.Activate(ShippedGerman.Load());
        try
        {
            var icons = new FakeIconProvider();
            using var laid = Laid.Open(
                [
                    Win(1, "Quarterly report - a long document title that the tile trims", minimized: true),
                    Win(2, "Editor"),
                ],
                scale: scale, icons: icons);

            var faces = laid.Faces();
            Check.That(faces).HasSize(2);

            for (var index = 0; index < faces.Count; index++)
            {
                var container = faces[index].Container;
                var face = laid.Bounds(faces[index].Border);
                var tile = laid.ViewModel.Tiles[index];

                var previewBox = laid.Find<Border>(container, "PreviewBox")
                    ?? throw new InvalidOperationException("The tile template has no Border named PreviewBox.");
                var rowIcon = laid.Find<Image>(container, "TileIcon")
                    ?? throw new InvalidOperationException("The tile template has no Image named TileIcon.");
                var fallback = laid.Find<Image>(container, "PreviewIcon")
                    ?? throw new InvalidOperationException("The tile template has no Image named PreviewIcon.");

                var rowBox = laid.Bounds(rowIcon);
                var fallbackBox = laid.Bounds(fallback);

                Check.WithCustomMessage(
                        $"{language} at {scale:0.0}x: the row icon is {FitText.Dip(rowBox.Width)} x "
                        + $"{FitText.Dip(rowBox.Height)} DIP, not the design's 20 x 20.")
                    .That(rowBox.Width).IsCloseTo(20, 0.5);
                Check.That(rowBox.Height).IsCloseTo(20, 0.5);
                Check.WithCustomMessage(
                        $"{language} at {scale:0.0}x: the preview fallback is {FitText.Dip(fallbackBox.Width)} x "
                        + $"{FitText.Dip(fallbackBox.Height)} DIP, not the design's 40 x 40.")
                    .That(fallbackBox.Width).IsCloseTo(40, 0.5);
                Check.That(fallbackBox.Height).IsCloseTo(40, 0.5);
                Check.WithCustomMessage("A 20 DIP row icon must be drawn with high-quality scaling.")
                    .That(RenderOptions.GetBitmapScalingMode(rowIcon)).IsEqualTo(BitmapScalingMode.HighQuality);
                Check.WithCustomMessage("A 40 DIP fallback icon must be drawn with high-quality scaling.")
                    .That(RenderOptions.GetBitmapScalingMode(fallback)).IsEqualTo(BitmapScalingMode.HighQuality);

                // Per WINDOW, not per process: both draws carry the image its own handle resolved.
                Check.That(rowIcon.Source).IsSameReferenceAs(icons.IconFor(tile.Hwnd));
                Check.That(fallback.Source).IsSameReferenceAs(icons.IconFor(tile.Hwnd));
                Check.WithCustomMessage($"The row icon {Box(rowBox)} leaves the tile face {Box(face)}.")
                    .That(Inside(face, rowBox, inset: 8)).IsTrue();
                Check.WithCustomMessage($"The fallback icon {Box(fallbackBox)} leaves the preview box.")
                    .That(Inside(laid.Bounds(previewBox), fallbackBox)).IsTrue();

                // The title kept its size, fits the face, and stays clear of the preview box.
                var title = Descendants<TextBlock>(faces[index].Border)
                    .First(t => t.Text == tile.Title);
                var titleBox = laid.Bounds(title);
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the title {Box(titleBox)} "
                        + $"leaves the face {Box(face)}.")
                    .That(Inside(face, titleBox, inset: 8)).IsTrue();
                Check.WithCustomMessage(
                        $"{language} at {scale:0.0}x: the title {Box(titleBox)} reaches into the preview box "
                        + $"{Box(laid.Bounds(previewBox))} — a DWM thumbnail draws over any WPF content there.")
                    .That(titleBox.IntersectsWith(laid.Bounds(previewBox))).IsFalse();

                // The fallback column (icon + the badge on a minimised tile) still fits the box.
                var content = (FrameworkElement)fallback.Parent;
                var slot = LayoutInformation.GetLayoutSlot(content);
                content.Measure(new Size(slot.Width, double.PositiveInfinity));
                var needed = content.DesiredSize.Height;
                laid.Relayout();
                Check.WithCustomMessage(
                        $"{language} at {scale:0.0}x: the fallback column needs {FitText.Dip(needed)} DIP "
                        + $"but the preview box gives it {FitText.Dip(slot.Height)} — it would be clipped silently.")
                    .That(needed).IsLessOrEqualThan(slot.Height);
                _output.WriteLine(
                    $"{language} at {scale:0.0}x: row icon {Box(rowBox)}; fallback {Box(fallbackBox)}; "
                    + $"face {Box(face)}; title {Box(titleBox)}; column needs {FitText.Dip(needed)} of "
                    + $"{FitText.Dip(slot.Height)} DIP (headroom {FitText.Dip(slot.Height - needed)})");
            }

            // The badge is inside the preview box, with the 40 DIP fallback above it.
            var badge = laid.Find<Border>(faces[0].Container, "MinimizedBadge")!;
            Check.That(Inside(laid.Bounds(faces[0].Border), laid.Bounds(badge), inset: 8)).IsTrue();
            Check.That(Inside(laid.Bounds(laid.Find<Border>(faces[0].Container, "PreviewBox")!),
                laid.Bounds(badge))).IsTrue();
        }
        finally
        {
            if (language == "German") StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// S3-B's View wiring: the icon pass asks the seam once per WINDOW tile, with that tile's own
    /// handle and process id (never a process-wide lookup), and never for the "Show desktop" tile —
    /// which is not a window and draws its own glyph.
    /// </summary>
    [StaFact]
    public void ResolveIcons_AsksTheProviderOncePerWindowTile_AndNeverForTheDesktopTile()
    {
        var icons = new FakeIconProvider();
        using var laid = Laid.Open([Win(1, "Editor"), Win(2, "Browser")], icons: icons);

        Check.That(laid.ViewModel.HasDesktopTile).IsTrue();
        Check.WithCustomMessage("the desktop tile must not be offered to the icon seam")
            .That(icons.Requests).HasSize(laid.ViewModel.Tiles.Count);

        var expected = laid.ViewModel.Tiles.Select(t => (t.Hwnd, t.ProcessId)).ToList();
        Check.That(icons.Requests).ContainsExactly(expected);
        Check.That(laid.ViewModel.Tiles.Select(t => t.Icon))
            .ContainsOnlyElementsThatMatch(icon => icon is not null);

        var desktop = laid.ViewModel.PanelItems.Single(t => t.IsDesktop);
        Check.That(desktop.Icon).IsNull();
    }

    [StaFact]
    public void NoWindowAtAll_DrawsTheEmptyMessage_WhereTheTilesWouldBe()
    {
        // The one empty state left after S3-A: the enumeration itself returned nothing. (A session
        // whose only window is the foreground one draws the desktop tile instead — S3-A's rule.)
        using var laid = Laid.Open(
            [],
            foreground: null);

        Check.That(laid.ViewModel.Tiles).IsEmpty();
        Check.That(laid.ViewModel.HasDesktopTile).IsFalse();

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

    /// <summary>
    /// Fixup-01 (review finding 1): the fixture's registered face must be the face the template
    /// DRAWS — the cell inset by ONE margin, not two. The chip tests only count, so a 2 × margin
    /// offset was invisible; the first positional assertion placed on this fixture would have read
    /// rects 16 DIP to the right and below. Tile 8 is the first face of row 1 at the 8-column grid,
    /// so the column and the row derivation are both exercised.
    /// </summary>
    [StaFact]
    public void RegisterFaces_RegistersTheSameRectTheTemplateDraws()
    {
        using var laid = Laid.Open(Windows(9));   // 8 columns: tile 8 starts row 1
        laid.RegisterFaces(laid.ViewModel.Tiles.Count);

        var faces = laid.Faces();
        Check.That(faces).HasSize(9);
        for (var i = 0; i < faces.Count; i++)
        {
            var drawn = laid.Bounds(faces[i].Border);
            var id = laid.ViewModel.Tiles[i].Hwnd.ToString();
            var target = laid.ViewModel.DetectorForTest.Targets.Single(t => t.Id == id);

            if (target.Bounds is not { } registered)
                throw new InvalidOperationException($"tile {i}'s target has no rectangular bounds.");

            Check.WithCustomMessage(
                    $"tile {i}: the fixture registered {registered.X},{registered.Y} "
                    + $"{registered.Width}x{registered.Height} but the template draws {Box(drawn)}.")
                .That(Math.Abs(registered.X - drawn.Left)).IsLessOrEqualThan(0.5);
            Check.That(Math.Abs(registered.Y - drawn.Top)).IsLessOrEqualThan(0.5);
            Check.That(Math.Abs(registered.Width - drawn.Width)).IsLessOrEqualThan(0.5);
            Check.That(Math.Abs(registered.Height - drawn.Height)).IsLessOrEqualThan(0.5);
        }
    }

    [StaFact]
    public void OverflowChip_InEnglish_CountsTheWindowsWithoutATile()
    {
        // 64 windows, a 40-cell grid: the desktop tile takes the last cell, so TWENTY-FIVE windows
        // go without (the twenty-four that were already overflowing plus the one the tile
        // displaced).
        using var laid = Laid.Open(Windows(64));
        laid.RegisterFaces(Capacity);

        var chip = laid.Find<Border>(laid.Root, "OverflowChip")
            ?? throw new InvalidOperationException("The window has no Border named OverflowChip.");
        Check.That(chip.Visibility).IsEqualTo(Visibility.Visible);

        var text = Descendants<TextBlock>(chip).Single();
        Check.That(text.Text).IsEqualTo("More windows: 25");
        Check.That(BrushColor(text.Foreground)).IsEqualTo(Tokens.TextSecondary);
    }

    [StaFact]
    public void AtCapacity_DrawsNoChip()
    {
        // 39 windows + the desktop tile = the 40 cells: nothing is hidden, so there is nothing to
        // explain. This is the two-sided half of the count — an off-by-one either claims a hidden
        // window or hides one.
        using var laid = Laid.Open(Windows(Capacity - 1));
        laid.RegisterFaces(Capacity);

        var chip = laid.Find<Border>(laid.Root, "OverflowChip")!;
        Check.That(chip.Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(laid.ViewModel.OverflowCount).IsEqualTo(0);
    }

    [StaFact]
    public void WindowsWithoutATile_CountAllOfThem_DesktopSlotIncluded()
    {
        // 66 windows at a 40-cell grid with the desktop tile in the last cell: 39 keep a tile, 27
        // go without — the 26 that were already overflowing plus the one the tile displaced.
        using var laid = Laid.Open(Windows(66));
        laid.RegisterFaces(Capacity);

        Check.That(laid.ViewModel.OverflowCount).IsEqualTo(27);
        var chip = laid.Find<Border>(laid.Root, "OverflowChip")!;
        Check.That(Descendants<TextBlock>(chip).Single().Text).IsEqualTo("More windows: 27");
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
            Check.That(text.Text).IsEqualTo("Weitere Fenster: 25");

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
                .IsEqualTo("Weitere Fenster: 1");
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void GermanEmptyMessage_IsTheShippedText_AndFitsAt200Percent()
    {
        StringTable.Activate(ShippedGerman.Load());
        try
        {
            // At the owner's 200 %: the German message must be drawn whole, not clipped. (The badge's
            // German text is measured by the badge theory, which runs in both cultures.) The session
            // must have NO window at all — one foreground window now draws the desktop tile instead
            // (S3-A), and the message would be collapsed.
            using var empty = Laid.Open([], scale: 2.0, foreground: null);
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
    /// F-HINT: with a two-digit count the chip and the 24 DIP hint share ONE row on the owner's
    /// 1920 DIP width — the chip right-aligned 32 DIP left of the hint, no overlap, no wrapping
    /// and neither text clipped — in German (the widest shipped text) and English, at 100 % and
    /// at the owner's 200 %. A wrapping or clipped hint is what the owner's "hint too small"
    /// fix would turn into at the larger size if the row arithmetic were stale.
    /// </summary>
    [StaTheory]
    [InlineData("en", 1.0)]
    [InlineData("en", 2.0)]
    [InlineData("de", 1.0)]
    [InlineData("de", 2.0)]
    public void ChipAndHint_ShareOneRow_AtTwoDigits_InGermanAndEnglish(string language, double scale)
    {
        var german = language == "de";
        if (german) StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var laid = Laid.Open(Windows(64), scale: scale);   // 64 windows → chip 25
            laid.RegisterFaces(Capacity);

            var chipElement = laid.Find<Border>(laid.Root, "OverflowChip")!;
            var chip = laid.Bounds(chipElement);
            var hintElement = laid.Find<TextBlock>(laid.Root, "HintText")!;
            var hint = laid.Bounds(hintElement);
            var row = laid.Bounds(laid.Find<Grid>(laid.Root, "HintRow")!);
            var chipText = Descendants<TextBlock>(chipElement).Single();

            Check.That(chipElement.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(chipText.Text).IsEqualTo(german ? "Weitere Fenster: 25" : "More windows: 25");

            var messages = new List<string>();
            if (chipText.FontSize != hintElement.FontSize)
                messages.Add($"the chip is {chipText.FontSize} DIP but the hint is {hintElement.FontSize} DIP");
            if (hintElement.TextWrapping != TextWrapping.NoWrap || chipText.TextWrapping != TextWrapping.NoWrap)
                messages.Add("a text is allowed to wrap");

            var chipNatural = FitText.NaturalWidth(chipText, chipText.Text);
            if (chipNatural > chipText.ActualWidth + 0.5)
                messages.Add($"the chip needs {FitText.Dip(chipNatural)} DIP but is drawn in {FitText.Dip(chipText.ActualWidth)}");
            var hintNatural = FitText.NaturalWidth(hintElement, hintElement.Text);
            if (hintNatural > hintElement.ActualWidth + 0.5)
                messages.Add($"the hint needs {FitText.Dip(hintNatural)} DIP but is drawn in {FitText.Dip(hintElement.ActualWidth)}");

            // One row, side by side: the chip is 32 DIP left of the hint (the template's own
            // margin), they never overlap, and both sit inside the row and the 1920 DIP window.
            var gap = hint.Left - chip.Right;
            if (gap < 31.25 || gap > 32.75)
                messages.Add($"the chip→hint gap is {FitText.Dip(gap)} DIP, not 32");
            if (chip.IntersectsWith(hint))
                messages.Add($"the chip {Box(chip)} overlaps the hint {Box(hint)}");
            if (!Inside(row, chip) || !Inside(row, hint))
                messages.Add($"chip {Box(chip)} / hint {Box(hint)} leaves the hint row {Box(row)}");
            if (chip.Left < 0 || hint.Right > ScreenWidth)
                messages.Add($"the row leaves the {ScreenWidth} DIP window");
            if (Math.Abs(((hint.Top + hint.Bottom) / 2) - ((row.Top + row.Bottom) / 2)) > 0.5)
                messages.Add($"the hint is not vertically centred in the row: {Box(hint)} vs {Box(row)}");

            Check.WithCustomMessage($"At {scale:0.0}x ({language}): " + string.Join("; ", messages))
                .That(messages).IsEmpty();
            _output.WriteLine($"at {scale:0.0}x ({language}): chip {Box(chip)} \"{chipText.Text}\" "
                + $"({FitText.Dip(chipText.ActualWidth)} of {FitText.Dip(chipNatural)} DIP); "
                + $"hint {Box(hint)} ({FitText.Dip(hintElement.ActualWidth)} of {FitText.Dip(hintNatural)} DIP); "
                + $"gap {FitText.Dip(gap)} DIP; row {Box(row)}");
        }
        finally
        {
            if (german) StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>
    /// The chip's placement rules, measured in DIP at 1.0 and at the owner's 200 %: it must not
    /// overlap the ✕, it must keep ≥ 100 DIP edge-to-edge from the ✕'s hit CIRCLE (so gaze resting
    /// on the chip cannot fill the close dwell), it must stay in the row that is now text-height,
    /// and the ✕ must sit above that row (F-CORNER: the escape is the corner, not a neighbour of
    /// the text).
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ChipGeometry_ClearsTheCloseCircle_AndSitsBelowTheCornerExit(double scale)
    {
        using var laid = Laid.Open(Windows(64), scale: scale);
        laid.RegisterFaces(Capacity);

        var chipElement = laid.Find<Border>(laid.Root, "OverflowChip")!;
        var chip = laid.Bounds(chipElement);
        var hint = laid.Bounds(laid.Find<TextBlock>(laid.Root, "HintText")!);
        var disc = laid.Bounds(laid.Find<Border>(laid.Root, "CloseButton")!);
        var row = laid.Bounds(laid.Find<Grid>(laid.Root, "HintRow")!);

        var messages = new List<string>();

        // "Edge-to-edge from the hit circle": the nearest point of the chip to the disc's centre,
        // less the hit radius. The drawn disc IS the hit circle (B8: 2 × the shared r50), so its
        // own half-width is the radius — never a restated number. Since F-CORNER the two sit in
        // opposite corners, so this is the screen diagonal; the old 100 DIP floor still applies.
        var centre = new Point(disc.Left + (disc.Width / 2), disc.Top + (disc.Height / 2));
        var clearance = DistanceToRect(chip, centre) - (disc.Width / 2);
        if (clearance < 100)
            messages.Add($"the chip is {FitText.Dip(clearance)} DIP edge-to-edge from the ✕'s hit circle, not ≥ 100");

        if (chip.IntersectsWith(disc) && chip.Width > 0 && chip.Height > 0)
            messages.Add($"the chip {Box(chip)} overlaps the ✕ disc {Box(disc)}");

        // The chip must fit the row WPF lays out (the row is no longer 124 DIP tall): its vertical
        // margins were dropped to 0 so its 39.92 DIP pill (F-HINT: a 31.92 DIP title line plus
        // 2 × 4 padding) fits the 56 DIP text row.
        if (!Inside(row, chip))
            messages.Add($"the chip {Box(chip)} leaves the hint row {Box(row)}");

        // And the ✕ must be ABOVE the row: an escape in the corner cannot be wedged beside the text.
        if (disc.Bottom > row.Top + 0.5 && disc.IntersectsWith(row))
            messages.Add($"the ✕ disc {Box(disc)} reaches into the hint row {Box(row)}");

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
        Check.That(disc.Bottom).IsLessOrEqualThan(row.Top + 0.5);

        _output.WriteLine($"at {scale:0.0}x: chip {Box(chip)}; hint {Box(hint)}; disc {Box(disc)}; "
            + $"row {Box(row)}; chip to hit circle {FitText.Dip(clearance)} DIP; chip text {FitText.Dip(natural)} DIP");
    }

    /// <summary>
    /// F-CORNER: the same keep-out the pure layout computes, read back from the DRAWN tree at
    /// 1.0 and the owner's 200 %: every tile face starts below the ✕'s painted box, so no face —
    /// and therefore no registered dwell target, and no thumbnail (which fits inside its face)
    /// can be under the escape. The 16 DIP face margin is dead space, so the face bound is the
    /// conservative one.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void Tiles_NeverReachTheCloseDisc_OnTheDrawnGrid(double scale)
    {
        using var laid = Laid.Open(Windows(64), scale: scale);
        laid.RegisterFaces(Capacity);

        var disc = laid.Bounds(laid.Find<Border>(laid.Root, "CloseButton")!);
        var messages = new List<string>();
        foreach (var slot in laid.Faces())
        {
            var face = laid.Bounds(slot.Border);
            if (face.Top < TaskSwitcherLayout.TilesTopMinimum + TaskSwitcherLayout.TileFaceMarginDip - 0.5)
                messages.Add($"a face at {Box(face)} starts above the ✕ keep-out");
            if (face.IntersectsWith(disc))
                messages.Add($"a face {Box(face)} intersects the ✕ {Box(disc)}");
        }

        Check.WithCustomMessage($"At {scale:0.0}x: " + string.Join("; ", messages)).That(messages).IsEmpty();

        // The escape itself: the disc's centre is the shared corner offset (53 DIP from both edges).
        Check.That(disc.Left + (disc.Width / 2)).IsCloseTo(ScreenWidth - UIConstants.BackdropHideButtonOffset, 0.5);
        Check.That(disc.Top + (disc.Height / 2)).IsCloseTo(UIConstants.BackdropHideButtonOffset, 0.5);
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
        using var englishNoChip = Laid.Open(Windows(Capacity - 1), scale: scale);
        englishNoChip.RegisterFaces(Capacity);
        var englishReference = DiscCentre(englishNoChip);
        _output.WriteLine($"at {scale:0.0}x English: no chip, ✕ centre {Point(englishReference)}");

        using var englishChip = Laid.Open(Windows(Capacity + 1), scale: scale);
        englishChip.RegisterFaces(Capacity);
        Check.WithCustomMessage("The overflowing session drew no chip, so the pin would compare two chip-less rows.")
            .That(englishChip.Find<Border>(englishChip.Root, "OverflowChip")!.Visibility)
            .IsEqualTo(Visibility.Visible);
        var englishChipBox = englishChip.Bounds(englishChip.Find<Border>(englishChip.Root, "OverflowChip")!);
        var english = DiscCentre(englishChip);
        _output.WriteLine($"at {scale:0.0}x English: chip {Box(englishChipBox)}, ✕ centre {Point(english)}");
        AssertSameDiscPosition(englishReference, english, "the English chip", scale);

        StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var germanNoChip = Laid.Open(Windows(Capacity - 1), scale: scale);
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

    private static void AssertSameDiscPosition(Point reference, Point measured, string what, double scale)
    {
        Check.WithCustomMessage(
                $"At {scale:0.0}x the ✕ moved when {what} appeared: {Point(reference)} → {Point(measured)}. "
                + "It is the escape; it must be where it was last time.")
            .That(measured.X).IsCloseTo(reference.X, 0.5);
        Check.WithCustomMessage(
                $"At {scale:0.0}x the ✕ moved vertically when {what} appeared: {Point(reference)} → {Point(measured)}.")
            .That(measured.Y).IsCloseTo(reference.Y, 0.5);
    }

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
        /// Shows <paramref name="windows"/> through the real toggle, applies a docked-top keep-out
        /// layout (a 112 DIP test band) and arranges the content — at <paramref name="scale"/> 1.0
        /// directly, at 2.0 through the fit host's 200 % render chain.
        /// </summary>
        internal static Laid Open(
            IReadOnlyList<AppWindow> windows, double scale = 1.0, IntPtr? foreground = null,
            IAppIconProvider? icons = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var messenger = new WeakReferenceMessenger();
            var viewModel = new TaskSwitcherViewModel(
                new FakeWindowManager(windows, foreground), new EyeCtrlSettings(), messenger);
            messenger.Send(new TaskSwitcherToggledMessage());

            var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
            window.DataContext = viewModel;
            // ShowFullScreen's own order, without showing the window: the grid's capacity decides
            // whether the desktop tile costs the last window its slot, so trim first and lay out
            // the panel that will actually be drawn (window tiles + the desktop tile).
            viewModel.ApplyTileCapacity(TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", tileCount: 0).Capacity);
            if (icons is not null)
            {
                // Production's icon step, in its place: after the capacity trim, before the layout.
                window.Configure(viewModel, icons);
                window.ResolveIcons();
            }
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
                // The 200 % chain: the frame carries a ScaleTransform of 2.0 / the device scale, so
                // the effective scale is exactly the owner's 2.0 whatever this machine reports.
                host = FitHost.Open(root, width: ScreenWidth, height: ScreenHeight, inheritFrom: window);
            }

            return new Laid(window, root, panel, viewModel, host, scale);
        }

        /// <summary>
        /// The View's post-layout registration pass for <paramref name="reachable"/> faces, then the
        /// close disc — exactly what <c>TaskSwitcherWindow.UpdateTilePositions</c> drives (the real
        /// one needs an HWND for PointToScreen, so the rectangles are supplied here). The grid is
        /// <see cref="TaskSwitcherLayout.Result.Columns"/> wide (never a restated 8); the face is the
        /// cell less the template's 16 DIP margin on each side, so it sits ONE margin into its cell
        /// (two would put it 16 DIP right and below the drawn face — review finding 1).
        /// </summary>
        internal void RegisterFaces(int reachable)
        {
            var layout = TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", ViewModel.PanelItems.Count);
            for (var i = 0; i < reachable && i < ViewModel.Tiles.Count; i++)
            {
                var column = i % layout.Columns;
                var row = i / layout.Columns;
                ViewModel.UpdateTilePosition(ViewModel.Tiles[i].Hwnd, new PhysicalRect(
                    layout.Block.X + (column * TaskSwitcherLayout.CellWidth) + TaskSwitcherLayout.TileFaceMarginDip,
                    layout.Block.Y + (row * TaskSwitcherLayout.CellHeight) + TaskSwitcherLayout.TileFaceMarginDip,
                    TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip,
                    TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip));
            }

            // The close disc's REAL drawn centre, pushed the way the View pushes it (physical
            // pixels): the corner host's centre × the render scale. Reading it from the tree — not
            // restating a point — is what lets the chip/tile pins describe the actual escape.
            var closeHost = Find<Border>(Root, "CloseButton")
                ?? throw new InvalidOperationException("The switcher has no Border named CloseButton.");
            var close = Bounds(closeHost);
            ViewModel.UpdateCloseButtonPosition(new PhysicalPoint(
                (close.Left + (close.Width / 2)) * _scale,
                (close.Top + (close.Height / 2)) * _scale));

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

    /// <summary>
    /// A per-WINDOW icon double: a distinct frozen glyph per handle plus the request log. The log is
    /// what proves the View asks once per window tile (and never for the desktop tile); the distinct
    /// instances are what prove each tile got its own window's icon, not a shared process icon.
    /// </summary>
    private sealed class FakeIconProvider : IAppIconProvider
    {
        private readonly Dictionary<IntPtr, ImageSource> _icons = [];

        internal List<(IntPtr Hwnd, uint ProcessId)> Requests { get; } = [];

        public ImageSource GetIcon(IntPtr hwnd, uint processId)
        {
            Requests.Add((hwnd, processId));
            if (!_icons.TryGetValue(hwnd, out var icon))
            {
                var drawing = new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb((byte)(0x40 + (hwnd.ToInt64() & 0x3F)), 0x80, 0xC0)),
                    null,
                    new RectangleGeometry(new Rect(0, 0, 24, 24)));
                drawing.Freeze();
                var image = new DrawingImage(drawing);
                image.Freeze();
                _icons[hwnd] = icon = image;
            }

            return icon;
        }

        internal ImageSource IconFor(IntPtr hwnd) => _icons[hwnd];
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
        public void MinimizeNonBlocking(IntPtr hwnd) { }

        public void MaximizeOrRestore(IntPtr hwnd) { }

        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
