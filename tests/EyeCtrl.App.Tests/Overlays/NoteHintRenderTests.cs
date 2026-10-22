namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Notes;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-06's renders for the owner: the two chip texts and the "Place here" label at the
/// note's own size, and the owner's own screen with the note stepped clear of the keyboard.
/// <para>
/// T6 fixup-07: the chip renders come from the REAL chip path - a windowless manager, the
/// removal dwell, the stored placement - so a tail that production never draws (the chip
/// carries none) can never appear in the owner's picture. The PNGs land in a TEMP folder
/// (<c>%TEMP%\eyectrl-note-hints</c>), never in the owner's review folder, so every App run
/// (CI included) stops rewriting the picture the owner looks at. The folder is printed in the
/// test output.
/// </para>
/// <para>
/// The class lives in the exclusive "StringTable Tests (App)" collection: it activates the
/// German table, which is process-global, and the German is reset in a finally.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class NoteHintRenderTests
{
    /// <summary>The render folder: a TEMP one (T6 fixup-07) - the owner's review folder is
    /// theirs, and every App run must not rewrite it.</summary>
    internal static string RenderDirectory => Path.Combine(Path.GetTempPath(), "eyectrl-note-hints");

    /// <summary>The owner's screen: 3840x2400 physical at 200 % = 1920x1200 DIP.</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1200);

    /// <summary>The owner's keyboard on the TOP edge, in DIP: physical (502, 0, 2836, 1150).</summary>
    private static readonly Rect Keyboard = new(251, 0, 1418, 575);

    /// <summary>The keyboard's edge bar, on the same top edge.</summary>
    private static readonly Rect EdgeBar = new(251, 0, 1418, 40);

    [StaFact]
    public void RendersTheChips_ThePlaceHereLabel_AndTheOwnersScreen()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            Directory.CreateDirectory(RenderDirectory);
        }
        catch (Exception ex)
        {
            // The renders are for the OWNER's machine; where the folder cannot be made the
            // test skips rather than failing a build that has nothing to show for it.
            Assert.Skip($"the render folder is not available here ({RenderDirectory}): {ex.Message}");
            return;
        }

        // The owner's screen is German: the renders must show the German lines, at the note's
        // own size. The string table is process-global, so the German is reset below.
        StringTable.Activate(GermanDraft.Table);
        try
        {
            RenderTheSet();
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The renders themselves: the two chip lines and the "Place here" label in German
    /// at 28 DIP, and the owner's own screen with the note stepped clear of the keyboard.</summary>
    private static void RenderTheSet()
    {
        foreach (var theme in new[] { "Midnight", "Daylight" })
        {
            using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
            Write(RenderChip("removed"), Path.Combine(RenderDirectory, $"note-chip-removed-{theme.ToLowerInvariant()}-28dip.png"));
            Write(RenderChip("discarded"), Path.Combine(RenderDirectory, $"note-chip-discarded-{theme.ToLowerInvariant()}-28dip.png"));
        }

        Write(RenderPlaceHere(), Path.Combine(RenderDirectory, "note-place-here-de-28dip.png"));

        // The reviewer's real Edit note under the owner's top keyboard, at 28 and 48 DIP.
        foreach (var textSize in new[] { 28.0, 48.0 })
        {
            Write(RenderOwnerScreen(textSize), Path.Combine(RenderDirectory, $"note-edit-under-top-keyboard-{(int)textSize}dip.png"));
        }
    }

    /// <summary>
    /// The chip as the owner sees it, from the REAL path: a windowless manager, the removal
    /// dwell, the stored placement. The production chip carries NO tail (T5b), and this harness
    /// must not draw one either - the earlier version placed the chip with
    /// <see cref="NoteLayout.Place"/>, which always has a tail, and showed the owner a picture
    /// production never draws.
    /// </summary>
    private static RenderTargetBitmap RenderChip(string kind)
    {
        var placement = RealChipPlacement(kind);
        Check.WithCustomMessage("the production chip carries no tail")
            .That(placement.TailVisible).IsFalse();

        var card = placement.Card;
        var window = new NoteWindow($"hint-render-{kind}");
        window.SetTextSize(28);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(ChipText(kind), 28, NoteLayout.ChipTextWidthDip(28, ChipTexts()));
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        // The REAL placement, so the drawn column and the text land where production puts them.
        window.ApplyPlacement(placement);

        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(0, 0, window.Width, window.Height));

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(card.Width * 2.0), (int)Math.Ceiling(card.Height * 2.0),
            192, 192, PixelFormats.Pbgra32);
        // The card's region of the canvas: the chip's card, not the tail strip beside it. The
        // canvas is in WINDOW-relative coordinates, so the crop uses the local card rect.
        var local = NoteLayout.RectLocal(placement);
        var brush = new VisualBrush(canvas)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(local.X, local.Y, local.Width, local.Height),
        };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(brush, null, new Rect(0, 0, card.Width, card.Height));
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>
    /// The chip's real placement: a windowless manager, the note saved, the removal dwell
    /// fired - the same seam the reviewer's probe used.
    /// </summary>
    private static NotePlacement RealChipPlacement(string kind)
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: () => WorkArea);
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var id = manager.AddNoteTargetsForTest(new PhysicalPoint(1600, 1000));
            // The removal dwell on the ✕ cell: the note becomes a chip and the manager stores
            // the chip's placement - the REAL one, tail and all.
            Hold(manager, NoteTestBench.CellCentre(manager, id, 3), 0);
            Check.WithCustomMessage("the note must have become a chip")
                .That(manager.ActiveNotes[0].State).IsEqualTo(NoteState.PendingUndo);
            var placement = manager.PlacementForTest(id)
                ?? throw new InvalidOperationException("the chip has no placement");
            return placement;
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Holds the gaze: a short look-away, then the hold that fires the dwell.</summary>
    private static void Hold(NoteManager manager, PhysicalPoint centre, long startUs)
    {
        for (long t = startUs; t <= startUs + 200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(new PhysicalPoint(centre.X + 10_000, centre.Y), t, IsInGap: false)));
        for (long t = startUs + 300_000; t <= startUs + 1_200_000; t += 100_000)
            manager.Receive(new GazeCursorMovedMessage(new GazeFrame(centre, t, IsInGap: false)));
    }

    /// <summary>The chip's text, as the manager draws it (the no-break space included).</summary>
    private static string ChipText(string kind) => NoteLayout.NoBreakBeforeDot(
        StringTable.Current.T(kind == "removed"
            ? "overlays.note.chip.removed"
            : "overlays.note.chip.discarded"));

    /// <summary>The "Place here" label at 28 DIP, in its measured window.</summary>
    private static RenderTargetBitmap RenderPlaceHere()
    {
        var window = new NotePlaceHereWindow(28);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.SizeDip.Width, window.SizeDip.Height));
        content.Arrange(new Rect(0, 0, window.SizeDip.Width, window.SizeDip.Height));

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.SizeDip.Width * 2.0),
            (int)Math.Ceiling(window.SizeDip.Height * 2.0), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }

    /// <summary>
    /// The owner's own screen: 1920x1200 DIP (3840x2400 physical at 200 %), the top keyboard
    /// band drawn where the owner's keyboard sits, and the note in Edit stepped clear of it with
    /// its anchor UNDER the keyboard - the case the fixup is about.
    /// </summary>
    private static RenderTargetBitmap RenderOwnerScreen(double textSize)
    {
        var card = new Size(
            NoteLayout.CardWidthDip(textSize),
            NoteLayout.CardHeightDip(textSize, 0, WorkArea.Height));
        var placement = NoteLayout.Place(
            new PhysicalPoint(960 * 2, 300 * 2), NoteSide.Right, card, WorkArea,
            new[] { Keyboard, EdgeBar }, 2.0, exclude: null, allowDisplaced: true)!;
        var grown = NoteLayout.WithTagClearance(placement, textSize);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)), null,
                new Rect(0, 0, WorkArea.Width, WorkArea.Height));
            // The keyboard band and its edge bar, where the owner's keyboard sits.
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x38)), null, Keyboard);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x50, 0x50, 0x5A)), null, EdgeBar);
            // The note, where it lands: clear of the keyboard.
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x40)), null, grown.Card);
            // The anchor, under the keyboard: the gaze that put the note there.
            context.DrawEllipse(new SolidColorBrush(Colors.OrangeRed), null,
                new Point(960, 300), 6, 6);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(WorkArea.Width * 2.0), (int)Math.Ceiling(WorkArea.Height * 2.0),
            192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>The chip's texts in every shipped language - the set the column is measured
    /// over, so the rendered card is the one the manager would place.</summary>
    private static IReadOnlyList<string> ChipTexts()
    {
        var table = StringTable.Current;
        var texts = new List<string>();
        foreach (var language in new[] { UiLanguage.English, UiLanguage.German })
        {
            foreach (var key in new[] { "overlays.note.chip.removed", "overlays.note.chip.discarded" })
            {
                var text = NoteLayout.NoBreakBeforeDot(table.T(language, key));
                if (!texts.Contains(text)) texts.Add(text);
            }
        }
        return texts;
    }

    private static void Write(RenderTargetBitmap bitmap, string file)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file))
            encoder.Save(stream);
        Check.WithCustomMessage($"{file} must be a real render")
            .That(new FileInfo(file).Length).IsStrictlyGreaterThan(0);
    }
}
