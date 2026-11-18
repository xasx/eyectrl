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
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Notes readability (sitting 9): the ticket's before/after renders. German, DPI 2.0, Midnight
/// — the same discipline as <see cref="NoteReadingRenderTests"/>, <see cref="NoteHintRenderTests"/>
/// and <see cref="NoteBadgeRenderTests"/>; headless, the window is NEVER shown.
/// <para>
/// Six PNGs per run: a Display card with a realistic two-line German text and its number tag,
/// and the removal chip ("Notiz entfernt · Rückgängig"), each at 28 / 36 / 40 DIP — the three
/// sizes the ticket renders before and after. They land in a TEMP folder
/// (<c>%TEMP%\eyectrl-note-readability-ticket</c>) so every App run leaves the repository clean;
/// the runner copies them beside the ticket as <c>card|chip-{before|after}-{size}.png</c>.
/// </para>
/// <para>
/// The class lives in the exclusive "StringTable Tests (App)" collection: it activates the
/// German table, which is process-global, and the German is reset in a finally.
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class NoteReadabilityTicketRenders
{
    /// <summary>The render folder: a TEMP one — the same rule as the other note renders.</summary>
    internal static string RenderDirectory => Path.Combine(Path.GetTempPath(), "eyectrl-note-readability-ticket");

    /// <summary>The ticket's three sizes: the current default and the two the owner considers.</summary>
    internal static readonly double[] Sizes = [28, 36, 40];

    /// <summary>A realistic two-line German note, the kind the owner actually keeps.</summary>
    private const string GermanCardText =
        "Medikamente um 8:00 - dazu ein volles Glas Wasser trinken.\n" +
        "Zurück um 14:00, danach ruft Anna an.";

    [StaFact]
    public void RendersTheTicketSet_CardAndChip_AtTheThreeSizes()
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

        // The owner's screen is German: the chip must show the German line. The string table
        // is process-global, so the German is reset below.
        StringTable.Activate(GermanDraft.Table);
        try
        {
            foreach (var size in Sizes)
            {
                Write(RenderCard(size), Path.Combine(RenderDirectory, $"card-{(int)size}dip.png"));
                Write(RenderChip(size), Path.Combine(RenderDirectory, $"chip-{(int)size}dip.png"));
            }
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }

        Check.WithCustomMessage("the ticket render set must hold exactly 6 PNGs")
            .That(Directory.GetFiles(RenderDirectory, "*.png").Length).IsEqualTo(6);
    }

    /// <summary>A Display card with the German sample and the number tag, as
    /// <see cref="NoteBadgeRenderTests"/> renders its badged cards.</summary>
    private static RenderTargetBitmap RenderCard(double size)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve("Midnight"));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("readability-ticket-card");
            InvisibleTestWindow.Prepare(window);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Display);
            window.SetTextSize(size);
            var card = new Size(
                NoteLayout.CardWidthDip(size),
                // The PRODUCTION height: the same measure the manager derives from.
                NoteLayout.CardHeightDip(size, window.MeasureTextHeightDip(GermanCardText), 1200));
            var placement = NoteLayout.WithTagClearance(
                NoteLayout.Place(
                    new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                    NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!,
                size);
            window.ApplyPlacement(placement);
            window.SetText(GermanCardText);
            window.UpdateContent();
            window.SetBadge(1);

            return RenderWindow(window);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>The removal chip as the owner sees it, from the REAL chip path — the same seam
    /// <see cref="NoteHintRenderTests"/> uses, so a tail production never draws cannot appear.</summary>
    private static RenderTargetBitmap RenderChip(double size)
    {
        var placement = RealChipPlacement(size);
        Check.WithCustomMessage("the production chip carries no tail")
            .That(placement.TailVisible).IsFalse();

        var card = placement.Card;
        var window = new NoteWindow($"readability-ticket-chip-{(int)size}");
        InvisibleTestWindow.Prepare(window);
        window.SetTextSize(size);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(ChipText(), size, NoteLayout.ChipTextWidthDip(size, ChipTexts()));
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

    /// <summary>The whole arranged window, drawn off-screen into a bitmap — no HWND anywhere.</summary>
    private static RenderTargetBitmap RenderWindow(Window window)
    {
        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

        int pxWidth = (int)Math.Ceiling(window.Width * 2.0);
        int pxHeight = (int)Math.Ceiling(window.Height * 2.0);
        var bitmap = new RenderTargetBitmap(pxWidth, pxHeight, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        return bitmap;
    }

    /// <summary>The chip's real placement: a windowless manager, the note saved, the removal
    /// dwell fired - the same seam the chip renders use.</summary>
    private static NotePlacement RealChipPlacement(double size)
    {
        var messenger = new WeakReferenceMessenger();
        using var manager = new NoteManager(messenger, hitTester: InactiveNoteHitTester.Instance,
            workAreaDip: NoteTestBench.WorkAreaProvider);
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
    private static string ChipText() => NoteLayout.NoBreakBeforeDot(
        StringTable.Current.T("overlays.note.chip.removed"));

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
