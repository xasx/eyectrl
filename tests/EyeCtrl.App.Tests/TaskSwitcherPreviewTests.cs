namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.Localization;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Platform.Icons;
using EyeCtrl.Platform.Thumbnails;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S4-C on the drawn switcher: the live preview lifecycle (register / fail / unregister) with a
/// FAKE thumbnail service, and the Option C tile template at 1.0 and the owner's 200 %, English and
/// German.
///
/// <para>
/// Nothing here registers a real DWM thumbnail and nothing shows the switcher. The window is built
/// through <see cref="InvisibleTestWindow.PrepareCloaked"/> like every other switcher fixture, and
/// its content is detached and arranged headlessly (the same arrangement
/// <see cref="TaskSwitcherTileStatesWindowTests"/> uses). The production registration pass itself is
/// driven — <c>RegisterTileThumbnails</c> needs only a laid-out tree, not an HWND — so the calls,
/// skips and releases asserted below are the ones the show path makes.
/// </para>
///
/// <para>
/// The template's load-bearing rule: DWM draws a thumbnail ABOVE every WPF pixel of the switcher,
/// so the 168 × 94 preview box is a hole — the title, the 20 DIP icon row, the badge and the
/// progress bar must all lie outside it. A minimised or failed tile is the only state with content
/// inside the box (the 40 DIP icon and, when minimised, the badge).
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class TaskSwitcherPreviewTests
{
    private const double ScreenWidth = 1920;
    private const double ScreenHeight = 1200;

    /// <summary>The owner's docked top toolbar band: 8 columns × 5 rows = 40 cells (F-GAP).</summary>
    private static readonly TaskSwitcherLayout.DipRect TopBand = new(0, 0, 1920, 112);

    // ── lifecycle ──────────────────────────────────────────────────────────────────────────────

    [StaFact]
    public void OneRegistrationPerVisibleWindowTile_NoneForMinimisedOrDesktop()
    {
        using var board = Board.Open(
            [Win(1, "Editor"), Win(2, "Browser"), Win(3, "Notes"), Win(4, "Chat", minimized: true)]);

        var (registered, failed, _) = board.Register();

        Check.That((registered, failed)).IsEqualTo((3, 0));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(3);
        Check.That(board.Thumbnails.LiveSources).ContainsExactly(
            new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(board.ViewModel.Tiles.Select(t => t.HasPreview))
            .ContainsExactly(true, true, true, false);
        Check.That(board.ViewModel.PanelItems.Last().IsDesktop).IsTrue();
        Check.That(board.ViewModel.PanelItems.Last().HasPreview).IsFalse();
    }

    [StaFact]
    public void OverflowTrimmedWindows_GetNoPreview_OnlyTheTilesThatAreDrawn()
    {
        // 50 windows against 40 cells: the VM trims the tail, so 39 window tiles are drawn (the
        // desktop tile takes the last cell) and the preview pass must never see — or register — a
        // window without a tile.
        using var board = Board.Open(NumberedWindows(50));

        var (registered, failed, _) = board.Register();

        Check.That(board.ViewModel.Tiles).HasSize(39);
        Check.That((registered, failed)).IsEqualTo((39, 0));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(39);
        Check.That(board.Thumbnails.LiveSources)
            .ContainsOnlyElementsThatMatch(source => board.ViewModel.Tiles.Any(t => t.Hwnd == source));
        Check.That(board.Thumbnails.Registrations.Select(r => r.Source))
            .Not.Contains(new IntPtr(50));
    }

    [StaFact]
    public void Hide_ReleasesEveryHandle_AndThreeOpenCloseCyclesLeaveZeroLive()
    {
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser"), Win(3, "Notes")]);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var (registered, failed, _) = board.Register();
            Check.That((registered, failed)).IsEqualTo((3, 0));
            Check.That(board.Thumbnails.LiveCount).IsEqualTo(3);
            Check.That(board.ViewModel.Tiles).ContainsOnlyElementsThatMatch(t => t.HasPreview);

            board.Hide();

            Check.WithCustomMessage($"cycle {cycle}: a DWM handle survived HideOverlay.")
                .That(board.Thumbnails.LiveCount).IsEqualTo(0);
            Check.That(board.ViewModel.Tiles).ContainsOnlyElementsThatMatch(t => !t.HasPreview);
        }

        Check.That(board.Thumbnails.UnregisterCalls).IsEqualTo(9);
    }

    [StaFact]
    public void AFailedRegistration_LeavesThatTileInIconMode_AndTheOthersInPreviewMode()
    {
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser"), Win(3, "Notes")]);
        board.Thumbnails.FailRegistrationFor.Add(new IntPtr(2));

        var (registered, failed, _) = board.Register();

        Check.That((registered, failed)).IsEqualTo((2, 1));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(2);
        Check.That(board.ViewModel.Tiles[0].HasPreview).IsTrue();
        Check.That(board.ViewModel.Tiles[1].HasPreview).IsFalse();
        Check.That(board.ViewModel.Tiles[2].HasPreview).IsTrue();
    }

    [StaFact]
    public void AZeroSourceSize_IsAFailure_AndTheAllocatedHandleIsReleased()
    {
        // "A window gone between enumeration and registration": DWM still hands out a thumbnail id
        // but reports no source size. The tile must fall back, and the id must not leak.
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser")]);
        board.Thumbnails.ZeroSourceSizeFor.Add(new IntPtr(2));

        var (registered, failed, _) = board.Register();

        Check.That((registered, failed)).IsEqualTo((1, 1));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(1);
        Check.That(board.Thumbnails.UnregisterCalls).IsEqualTo(1);
        Check.That(board.ViewModel.Tiles[1].HasPreview).IsFalse();
    }

    [StaFact]
    public void AFailingUpdate_IsAFailure_AndTheAllocatedHandleIsReleased()
    {
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser")]);
        board.Thumbnails.FailUpdateFor.Add(new IntPtr(2));

        var (registered, failed, _) = board.Register();

        Check.That((registered, failed)).IsEqualTo((1, 1));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(1);
        Check.That(board.Thumbnails.UnregisterCalls).IsEqualTo(1);
        Check.That(board.ViewModel.Tiles[1].HasPreview).IsFalse();
    }

    [StaFact]
    public void ARegisteredPreview_IsPlacedRelativeToTheWindow_InPhysicalPixels()
    {
        using var board = Board.Open([Win(1, "Editor")]);

        var (registered, _, _) = board.Register();
        Check.That(registered).IsEqualTo(1);

        var registration = board.Thumbnails.Registrations.Single();
        Check.WithCustomMessage("the destination must be the switcher's own HWND")
            .That(registration.Destination).IsNotEqualTo(IntPtr.Zero);
        Check.That(registration.Source).IsEqualTo(new IntPtr(1));

        var update = board.Thumbnails.Updates.Single();
        var box = board.Bounds(board.Find<Border>(board.Faces()[0].Container, "PreviewBox")!);

        // At 1.0 the physical rect is the DIP box: 168 x 94, aspect-fitted and centred. The origin
        // is the box's position in the WINDOW (never the screen): it must land on the laid-out box
        // (within the one rounding of a fractional layout position), and the fit is the helper's.
        Check.WithCustomMessage($"box {Box(box)}; destination {update.Destination}")
            .That(Math.Abs(update.Destination.X - box.Left)).IsLessOrEqualThan(1.0);
        Check.That(Math.Abs(update.Destination.Y - box.Top)).IsLessOrEqualThan(1.0);
        Check.That(update.Destination.Width).IsEqualTo(167);
        Check.That(update.Destination.Height).IsEqualTo(94);
        Check.That(update.Destination).IsEqualTo(ThumbnailGeometry.FitDestination(
            box.Left, box.Top, box.Width, box.Height, 1920, 1080));
    }

    [StaFact]
    public void ARegisteredPreview_At200Percent_IsPlacedInPhysicalPixels()
    {
        // The real registration pass through an off-screen render chain at 2.0: the box is
        // PointToScreen'd, so the destination is twice its DIP geometry — 334 x 188, not 167 x 94.
        // (The review's mutant: handing the fit a 1.0 scale / DIP rect survives every 1.0 test.)
        using var board = Board.Open([Win(1, "Editor")], scale: 2.0);

        var (registered, _, _) = board.Register();
        Check.That(registered).IsEqualTo(1);

        var update = board.Thumbnails.Updates.Single();
        var box = board.Bounds(board.Find<Border>(board.Faces()[0].Container, "PreviewBox")!);

        Check.WithCustomMessage($"box {Box(box)} at 2.0; destination {update.Destination}")
            .That(update.Destination.Width).IsEqualTo(334);
        Check.That(update.Destination.Height).IsEqualTo(188);
        Check.That(Math.Abs(update.Destination.X - (box.Left * 2))).IsLessOrEqualThan(2.0);
        Check.That(Math.Abs(update.Destination.Y - (box.Top * 2))).IsLessOrEqualThan(2.0);
    }

    [StaFact]
    public void APassQueuedBeforeAHide_RegistersNothingOnTheHiddenWindow()
    {
        // The Loaded callback can run after HideOverlay. Production would register on a hidden
        // window and keep the handles until the next show; the pass must refuse instead.
        using var board = Board.Open([Win(1, "Editor")]);

        board.Hide();
        board.UpdatePositions();

        Check.That(board.Thumbnails.Registrations).IsEmpty();
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(0);
    }

    [StaFact]
    public void AnExceptionAfterRegister_ReleasesTheHandle()
    {
        // L2: a throw between Register and the handle list must not leak a DWM thumbnail that
        // HideOverlay can never see.
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser")]);
        board.Thumbnails.ThrowAfterRegisterFor.Add(new IntPtr(2));

        var (registered, failed, _) = board.Register();

        Check.That((registered, failed)).IsEqualTo((1, 1));
        Check.That(board.Thumbnails.LiveCount).IsEqualTo(1);
        Check.That(board.Thumbnails.LiveSources).ContainsExactly(new IntPtr(1));
        Check.That(board.Thumbnails.UnregisterCalls).IsEqualTo(1);
    }

    [StaFact]
    public void TheFirstFailingHresult_IsCarriedWithTheCounts()
    {
        // previewHr= exists for the installed build: E_ACCESSDENIED / E_INVALIDARG / E_FAIL must
        // reach the log without a title or a process.
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser"), Win(3, "Notes")]);
        board.Thumbnails.FailRegistrationFor.Add(new IntPtr(2));   // E_ACCESSDENIED, first
        board.Thumbnails.FailUpdateFor.Add(new IntPtr(3));         // E_FAIL, later

        var (registered, failed, hresult) = board.Register();

        Check.That((registered, failed)).IsEqualTo((1, 2));
        Check.That(hresult).IsEqualTo(unchecked((int)0x80070005));
    }

    /// <summary>
    /// M2: the production show pass, driven for real on a cloaked switcher with the FAKE service.
    /// The review's surviving mutant made the fully-visible filter never true: production would
    /// register zero previews with every lifecycle test green. This gate pins the filter, the
    /// destination HWND and the ordering (every registration before the uncloak request).
    /// </summary>
    [StaFact]
    public void ShowFullScreen_RegistersEveryVisibleWindowTile_BeforeTheUncloakRequest()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var windows = new[]
        {
            Win(1, "Editor"), Win(2, "Browser"), Win(3, "Chat", minimized: true),
        };
        var messenger = new WeakReferenceMessenger();
        using var viewModel = new TaskSwitcherViewModel(
            new FakeWindowManager(windows), new EyeCtrlSettings(), messenger);
        var window = new TaskSwitcherWindow();
        try
        {
            InvisibleTestWindow.PrepareCloaked(window);   // anchors the one-way seam

            // SAFETY GATE, before any show: only a window DWM reports as cloaked may be shown.
            var prepared = InvisibleTestWindow.IsCloaked(window);
            Assert.SkipWhen(prepared is null, "DWM did not report the cloak state (no composition): refusing to show the switcher.");
            Assert.SkipUnless(prepared == true, "DWM did not cloak the prepared switcher: refusing to paint a full-screen non-layered window.");

            var fake = new FakeThumbnailService();
            var sequence = new List<string>();
            fake.OnRegister = source => sequence.Add($"register:{source.ToInt64()}");
            window.Configure(viewModel, new FakeIconProvider(), fake);
            window.UncloakSuppressedForTests += () => sequence.Add("uncloak-request");

            messenger.Send(new TaskSwitcherToggledMessage());
            window.ShowFullScreen(toolbarKeepOut: null);
            LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));

            Check.WithCustomMessage($"registrations: {string.Join(" | ", sequence)}")
                .That(fake.Registrations).HasSize(2);
            Check.That(fake.Registrations.Select(r => r.Source))
                .ContainsExactly(new IntPtr(1), new IntPtr(2));
            var destination = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            Check.That(destination).IsNotEqualTo(IntPtr.Zero);
            Check.That(fake.Registrations)
                .ContainsOnlyElementsThatMatch(r => r.Destination == destination);

            var uncloak = sequence.IndexOf("uncloak-request");
            Check.WithCustomMessage($"unexpected sequence: {string.Join(" | ", sequence)}")
                .That(uncloak).IsStrictlyGreaterThan(-1);
            var registeredAt = sequence
                .Select((step, index) => (step, index))
                .Where(x => x.step.StartsWith("register:", StringComparison.Ordinal))
                .Select(x => x.index)
                .ToList();
            Check.That(registeredAt).HasSize(2);
            Check.WithCustomMessage(
                    $"a registration came after the uncloak request: {string.Join(" | ", sequence)}")
                .That(registeredAt.Max()).IsStrictlyLessThan(uncloak);

            // The live pictures are released by the product hide path.
            window.HideOverlay();
            Check.That(fake.LiveCount).IsEqualTo(0);
        }
        finally
        {
            try { window.HideOverlay(); } catch { /* an assertion may have failed mid-show */ }
            window.Close();
        }
    }

    // ── template geometry ─────────────────────────────────────────────────────────────────────

    [StaTheory]
    [InlineData(1.0, "English")]
    [InlineData(2.0, "English")]
    [InlineData(1.0, "German")]
    [InlineData(2.0, "German")]
    public void PreviewBox_SitsAboveTheIconRowAndTheBar_AndNothingElseDrawsInsideIt(
        double scale, string language)
    {
        if (language == "German") StringTable.Activate(ShippedGerman.Load());
        try
        {
            using var board = Board.Open(
                [Win(1, "Editor"), Win(2, "Chat", minimized: true)], scale: scale);

            var faces = board.Faces();
            Check.That(faces).HasSize(3);   // two windows + the desktop tile

            foreach (var slot in faces.Take(2))
            {
                var face = board.Bounds(slot.Border);
                var box = board.Bounds(board.Find<Border>(slot.Container, "PreviewBox")!);
                var row = board.Bounds(board.Find<StackPanel>(slot.Container, "TileRow")!);
                var rowIcon = board.Bounds(board.Find<Image>(slot.Container, "TileIcon")!);
                var bar = board.Bounds(board.Find<ProgressBar>(slot.Container, "WindowProgress")!);
                var title = board.Bounds(Descendants<TextBlock>(slot.Border)
                    .First(t => t.Text == slot.Item.Title));

                Check.WithCustomMessage($"{language} at {scale:0.0}x: face {Box(face)} is not 184 x 150.")
                    .That(face.Width).IsCloseTo(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip, 0.5);
                Check.That(face.Height).IsCloseTo(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip, 0.5);

                Check.WithCustomMessage($"{language} at {scale:0.0}x: preview box {Box(box)} is not 168 x 94.")
                    .That(box.Width).IsCloseTo(168, 0.5);
                Check.That(box.Height).IsCloseTo(94, 0.5);
                Check.That(Inside(face, box, inset: 8)).IsTrue();

                // The box is ABOVE the identity row and the bar, and nothing that must be read is
                // inside it (a DWM thumbnail covers WPF content there).
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the preview box is not above the icon row.")
                    .That(box.Bottom).IsLessOrEqualThan(row.Top + 0.5);
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the preview box reaches into the progress bar.")
                    .That(box.Bottom).IsLessOrEqualThan(bar.Top + 0.5);
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the title {Box(title)} intersects the preview box.")
                    .That(title.IntersectsWith(box)).IsFalse();
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the row icon {Box(rowIcon)} intersects the preview box.")
                    .That(rowIcon.IntersectsWith(box)).IsFalse();
                Check.That(Inside(face, bar, inset: 8)).IsTrue();
                Check.That(Inside(face, row, inset: 8)).IsTrue();

                // L1: the icon + title row is LEFT-aligned (the Option C board), so a short title
                // starts at the tile's content edge instead of drifting to the middle.
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the row {Box(row)} is not left-aligned in {Box(face)}.")
                    .That(row.Left).IsCloseTo(face.Left + 8, 1.0);
                Check.WithCustomMessage($"{language} at {scale:0.0}x: the row icon {Box(rowIcon)} does not start at the row's left edge.")
                    .That(rowIcon.Left - row.Left).IsLessOrEqualThan(1.0);

                // The fallback: 40 DIP, inside the box on every tile (the normal tile has no
                // registration here, so its box shows the icon too — never empty, never black).
                var fallback = board.Bounds(board.Find<Image>(slot.Container, "PreviewIcon")!);
                Check.That(fallback.Width).IsCloseTo(40, 0.5);
                Check.That(fallback.Height).IsCloseTo(40, 0.5);
                Check.That(Inside(box, fallback)).IsTrue();
            }

            // The minimised tile: icon AND badge inside the box, the badge below the icon.
            var minimised = faces[1];
            var minimisedBox = board.Bounds(board.Find<Border>(minimised.Container, "PreviewBox")!);
            var minimisedIcon = board.Bounds(board.Find<Image>(minimised.Container, "PreviewIcon")!);
            var badge = board.Bounds(board.Find<Border>(minimised.Container, "MinimizedBadge")!);
            Check.That(board.Find<Border>(minimised.Container, "MinimizedBadge")!.Visibility)
                .IsEqualTo(Visibility.Visible);
            Check.That(Inside(minimisedBox, minimisedIcon)).IsTrue();
            Check.That(Inside(minimisedBox, badge)).IsTrue();
            Check.That(badge.Top).IsGreaterOrEqualThan(minimisedIcon.Bottom - 0.5);

            // An open tile draws no badge inside its box.
            Check.That(board.Find<Border>(faces[0].Container, "MinimizedBadge")!.Visibility)
                .IsEqualTo(Visibility.Collapsed);

            // The desktop tile: same face, its own glyph and label, and no window content at all.
            var desktop = faces[2];
            var desktopFace = board.Bounds(desktop.Border);
            Check.That(desktopFace.Width).IsCloseTo(TaskSwitcherLayout.CellWidth - 2 * TaskSwitcherLayout.TileFaceMarginDip, 0.5);
            Check.That(desktopFace.Height).IsCloseTo(TaskSwitcherLayout.CellHeight - 2 * TaskSwitcherLayout.TileFaceMarginDip, 0.5);
            Check.That(board.Find<StackPanel>(desktop.Container, "WindowContent")!.Visibility)
                .IsEqualTo(Visibility.Collapsed);
            Check.That(board.Find<StackPanel>(desktop.Container, "DesktopContent")!.Visibility)
                .IsEqualTo(Visibility.Visible);
        }
        finally
        {
            if (language == "German") StringTable.ResetToEmbeddedEnglish();
        }
    }

    [StaFact]
    public void ARegisteredPreview_CollapsesTheFallback_AndAFailedTileKeepsIt()
    {
        using var board = Board.Open([Win(1, "Editor"), Win(2, "Browser")]);
        board.Thumbnails.FailRegistrationFor.Add(new IntPtr(2));

        board.Register();
        board.Relayout();

        var registered = board.Find<Grid>(board.Faces()[0].Container, "PreviewFallback")!;
        var failed = board.Find<Grid>(board.Faces()[1].Container, "PreviewFallback")!;

        // A DWM thumbnail draws above WPF: an icon left underneath would sit in the picture (and
        // flash on a failed re-register). A failed tile must keep its icon box, never a blank hole.
        Check.That(registered.Visibility).IsEqualTo(Visibility.Collapsed);
        Check.That(failed.Visibility).IsEqualTo(Visibility.Visible);
        Check.That(board.Find<Image>(board.Faces()[1].Container, "PreviewIcon")!.Visibility)
            .IsEqualTo(Visibility.Visible);
    }

    /// <summary>
    /// F-GAP grew the cell (216 × 182) together with the face margin (16 DIP): the unchanged
    /// 168 × 94 preview box must still sit inside its face on a tile that is neither in the first
    /// row nor the first column — the margin is what places the face inside the cell, so a drift
    /// between the WrapPanel cell and the margin shows up away from the grid origin, not at tile 0.
    /// </summary>
    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ThePreviewBox_StillSitsInsideItsFace_OnATileBeyondTheFirstRowAndColumn(double scale)
    {
        using var board = Board.Open(NumberedWindows(10), scale: scale);

        var faces = board.Faces();
        Check.That(faces).HasSize(11);   // ten windows + the desktop tile

        var slot = faces[9];             // 8 columns: index 9 = row 1, column 1
        var face = board.Bounds(slot.Border);
        var box = board.Bounds(board.Find<Border>(slot.Container, "PreviewBox")
            ?? throw new InvalidOperationException("The tile template has no Border named PreviewBox."));

        Check.WithCustomMessage($"at {scale:0.0}x the preview box {Box(box)} left its face {Box(face)}.")
            .That(Inside(face, box, inset: 8)).IsTrue();
        Check.That(box.Width).IsCloseTo(168, 0.5);
        Check.That(box.Height).IsCloseTo(94, 0.5);
    }

    // ── wiring ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Source pin: the show path must report the registration result BEFORE the close-disc call
    /// that flushes the open line, or <c>previews=</c> would always be the previous session's 0.
    /// </summary>
    [Fact]
    public void UpdateTilePositions_RegistersPreviews_ThenReportsThem_BeforeTheFlush()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var body = SourceScanner.StripComments(ExtractMethod(source, "internal void UpdateTilePositions()"));

        var register = body.IndexOf("RegisterTileThumbnails(", StringComparison.Ordinal);
        var report = body.IndexOf("ReportPreviews(", StringComparison.Ordinal);
        var flush = body.IndexOf("UpdateCloseButtonPosition(", StringComparison.Ordinal);

        Check.WithCustomMessage("UpdateTilePositions no longer registers the tiles' thumbnails.")
            .That(register).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("UpdateTilePositions never reports the preview counts to the view model.")
            .That(report).IsStrictlyGreaterThan(register);
        Check.WithCustomMessage("the counts must be reported before the close-disc call flushes the open line.")
            .That(flush).IsStrictlyGreaterThan(report);
    }

    /// <summary>
    /// Source pin: the hide path must release the handles BEFORE it hides. The lifecycle test above
    /// drives the same method behaviourally; this pin keeps the call in <c>HideOverlay</c> itself,
    /// so a future refactor cannot move it to a path a test never reaches.
    /// </summary>
    [Fact]
    public void HideOverlay_ReleasesTheThumbnails()
    {
        var source = File.ReadAllText(Path.Combine(SourceScanner.RepoRoot().FullName,
            "src", "EyeCtrl.App", "Overlays", "TaskSwitcherWindow.xaml.cs"));
        var body = SourceScanner.StripComments(ExtractMethod(source, "public void HideOverlay()"));

        var unregister = body.IndexOf("UnregisterThumbnails(", StringComparison.Ordinal);
        var hide = body.IndexOf("Hide()", StringComparison.Ordinal);
        Check.That(unregister).IsStrictlyGreaterThan(-1);
        Check.That(hide).IsStrictlyGreaterThan(unregister);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.That(start).IsStrictlyGreaterThan(-1);
        return source[start..source.IndexOf("\n    }", start, StringComparison.Ordinal)];
    }

    private static IReadOnlyList<AppWindow> NumberedWindows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => Win(i, $"Window {i}"))
            .ToList();

    /// <summary>
    /// One Core <see cref="AppWindow"/> record (the switcher's window DESCRIPTOR, not a WPF window);
    /// built target-typed so the no-visible-window guard does not read it as a window construction.
    /// </summary>
    private static AppWindow Win(int hwnd, string title, bool minimized = false) =>
        new(new IntPtr(hwnd), title, (uint)(100 + hwnd), "app", minimized);

    private static string Box(Rect r) =>
        $"[{FitText.Dip(r.Left)}, {FitText.Dip(r.Top)} to {FitText.Dip(r.Right)}, {FitText.Dip(r.Bottom)}]";

    private static bool Inside(Rect outer, Rect inner, double inset = 0) =>
        inner.Left >= outer.Left + inset - 0.5 && inner.Top >= outer.Top + inset - 0.5
        && inner.Right <= outer.Right - inset + 0.5 && inner.Bottom <= outer.Bottom - inset + 0.5;

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

    private sealed record Slot(TaskSwitcherTileViewModel Item, DependencyObject Container, Border Border);

    /// <summary>
    /// The production window, laid out at 1920 × 1200 without ever being shown, with its content
    /// detached so the tile template can be measured headlessly. Disposing it hides (releasing the
    /// previews through the product path) and closes the window.
    /// </summary>
    private sealed class Board : IDisposable
    {
        private readonly TaskSwitcherWindow _window;
        private readonly FitHost? _host;
        private readonly ItemsControl _panel;

        private Board(
            TaskSwitcherWindow window, FrameworkElement root, ItemsControl panel,
            TaskSwitcherViewModel viewModel, FakeThumbnailService thumbnails, FitHost? host)
        {
            _window = window;
            _host = host;
            _panel = panel;
            Root = root;
            ViewModel = viewModel;
            Thumbnails = thumbnails;
        }

        internal FrameworkElement Root { get; }
        internal TaskSwitcherViewModel ViewModel { get; }
        internal FakeThumbnailService Thumbnails { get; }

        internal static Board Open(
            IReadOnlyList<AppWindow> windows, double scale = 1.0, FakeThumbnailService? thumbnails = null)
        {
            WpfApplicationGate.EnsureApplicationWithTokens();

            var messenger = new WeakReferenceMessenger();
            var viewModel = new TaskSwitcherViewModel(
                new FakeWindowManager(windows), new EyeCtrlSettings(), messenger);
            messenger.Send(new TaskSwitcherToggledMessage());

            thumbnails ??= new FakeThumbnailService();
            var window = InvisibleTestWindow.PrepareCloaked(new TaskSwitcherWindow());
            window.DataContext = viewModel;

            // ShowFullScreen's order: capacity trim for the DRAWN panel, then the layout.
            viewModel.ApplyTileCapacity(TaskSwitcherLayout.Compute(
                ScreenWidth, ScreenHeight, TopBand, "Top", tileCount: 0).Capacity);
            window.Configure(viewModel, new FakeIconProvider(), thumbnails);
            window.ResolveIcons();
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

            return new Board(window, root, panel, viewModel, thumbnails, host);
        }

        /// <summary>The production post-layout preview pass, over every realised panel tile.</summary>
        internal (int Registered, int Failed, int FirstFailureHresult) Register() =>
            _window.RegisterTileThumbnails(Root);

        /// <summary>
        /// The production pass itself (dwell targets + previews). Drivable after the tree was laid
        /// out detached — the stale-pass gate only needs it to take the hidden-window early return.
        /// </summary>
        internal void UpdatePositions() => _window.UpdateTilePositions();

        /// <summary>The product hide path: releases the handles, then hides (and requests the uncloak).</summary>
        internal void Hide() => _window.HideOverlay();

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

        internal T? Find<T>(DependencyObject searchRoot, string name) where T : FrameworkElement =>
            searchRoot is T itself && itself.Name == name
                ? itself
                : Descendants<T>(searchRoot).FirstOrDefault(e => e.Name == name);

        /// <summary>
        /// The element's drawn rectangle in DIP. At 1.0 that is the layout transform; at 2.0 it is
        /// <c>PointToScreen</c> — the real render chain, physical pixels — divided by the host's
        /// effective scale, so the figure is the DIP the owner's display draws.
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
                (topLeft.X - rootOrigin.X) / FitHost.Scale,
                (topLeft.Y - rootOrigin.Y) / FitHost.Scale,
                (bottomRight.X - topLeft.X) / FitHost.Scale,
                (bottomRight.Y - topLeft.Y) / FitHost.Scale);
        }

        public void Dispose()
        {
            _host?.Dispose();
            _window.HideOverlay();
            _window.DataContext = null;
            _window.Close();
            ViewModel.Dispose();
        }
    }

    /// <summary>
    /// The thumbnail seam as a counting double. It never talks to DWM: it hands out ids and records
    /// every register/update/unregister, so the lifecycle assertions are about the switcher's calls,
    /// not about Windows. A test can make one window fail at registration, report a zero source
    /// size, fail its update, or throw after the handle exists (the L2 leak case).
    /// </summary>
    private sealed class FakeThumbnailService : IWindowThumbnailService
    {
        private readonly Dictionary<IntPtr, IntPtr> _sourceByHandle = [];
        private readonly HashSet<IntPtr> _live = [];
        private int _next = 0x1000;

        internal List<(IntPtr Destination, IntPtr Source)> Registrations { get; } = [];
        internal List<(IntPtr Thumbnail, PhysicalRect Destination)> Updates { get; } = [];
        internal HashSet<IntPtr> FailRegistrationFor { get; } = [];
        internal HashSet<IntPtr> ZeroSourceSizeFor { get; } = [];
        internal HashSet<IntPtr> FailUpdateFor { get; } = [];
        internal HashSet<IntPtr> ThrowAfterRegisterFor { get; } = [];
        internal int UnregisterCalls { get; private set; }

        /// <summary>A failure HRESULT the seam can report; overridable per test for the log token.</summary>
        internal int RegisterFailureHresult { get; set; } = unchecked((int)0x80070005);   // E_ACCESSDENIED
        internal int QueryFailureHresult { get; set; } = unchecked((int)0x80070057);      // E_INVALIDARG
        internal int UpdateFailureHresult { get; set; } = unchecked((int)0x80004005);     // E_FAIL

        /// <summary>Called inside Register, so a gate can place the registration in a sequence.</summary>
        internal Action<IntPtr>? OnRegister { get; set; }

        internal int LiveCount => _live.Count;

        /// <summary>The SOURCE hwnd of every handle still registered, in registration order.</summary>
        internal IReadOnlyList<IntPtr> LiveSources =>
            _sourceByHandle.Where(p => _live.Contains(p.Key)).Select(p => p.Value).ToList();

        public IntPtr Register(IntPtr destinationHwnd, IntPtr sourceHwnd, out int hresult)
        {
            Registrations.Add((destinationHwnd, sourceHwnd));
            OnRegister?.Invoke(sourceHwnd);
            if (FailRegistrationFor.Contains(sourceHwnd))
            {
                hresult = RegisterFailureHresult;
                return IntPtr.Zero;
            }

            hresult = 0;
            var handle = new IntPtr(_next++);
            _sourceByHandle[handle] = sourceHwnd;
            _live.Add(handle);
            return handle;
        }

        public bool TryGetSourceSize(IntPtr thumbnail, out int width, out int height, out int hresult)
        {
            width = 0;
            height = 0;
            if (thumbnail == IntPtr.Zero || !_sourceByHandle.TryGetValue(thumbnail, out var source))
            {
                hresult = QueryFailureHresult;
                return false;
            }

            if (ThrowAfterRegisterFor.Contains(source))
                throw new InvalidOperationException("fake: a throw between Register and the handle list");

            if (ZeroSourceSizeFor.Contains(source))
            {
                hresult = 0;   // the query SUCCEEDED and reported nothing; no HRESULT to log
                return false;
            }

            hresult = 0;
            width = 1920;
            height = 1080;
            return true;
        }

        public bool Update(IntPtr thumbnail, PhysicalRect destination, out int hresult)
        {
            Updates.Add((thumbnail, destination));
            if (thumbnail == IntPtr.Zero
                || !_sourceByHandle.TryGetValue(thumbnail, out var source))
            {
                hresult = QueryFailureHresult;
                return false;
            }

            if (FailUpdateFor.Contains(source))
            {
                hresult = UpdateFailureHresult;
                return false;
            }

            hresult = 0;
            return true;
        }

        public void Unregister(IntPtr thumbnail)
        {
            UnregisterCalls++;
            _live.Remove(thumbnail);
            _sourceByHandle.Remove(thumbnail);
        }
    }

    /// <summary>A distinct frozen glyph per window handle; the template only needs a Source.</summary>
    private sealed class FakeIconProvider : IAppIconProvider
    {
        private readonly Dictionary<IntPtr, ImageSource> _icons = [];

        public ImageSource GetIcon(IntPtr hwnd, uint processId)
        {
            if (!_icons.TryGetValue(hwnd, out var icon))
            {
                var drawing = new GeometryDrawing(
                    new SolidColorBrush(Color.FromRgb(0x40, 0x80, 0xC0)),
                    null,
                    new RectangleGeometry(new Rect(0, 0, 24, 24)));
                drawing.Freeze();
                var image = new DrawingImage(drawing);
                image.Freeze();
                _icons[hwnd] = icon = image;
            }

            return icon;
        }
    }

    /// <summary>Minimal IWindowManager fake: a fixed list, nothing in front.</summary>
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
