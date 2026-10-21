namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// T6 fixup-06's renders for the owner: the two chip texts and the "Place here" label at the
/// note's own size, and the owner's own screen with the note stepped clear of the keyboard.
/// <para>
/// The PNGs land OUTSIDE the repo, in <c>C:\t\note-hints-test\</c> - the folder is the
/// deliverable, and nothing is written beside the source. Headless: no window is ever shown.
/// </para>
/// </summary>
[Collection("ActiveTheme")]
public class NoteHintRenderTests
{
    /// <summary>The owner's render folder - outside the repo, as the brief asks.</summary>
    internal const string RenderDirectory = @"C:\t\note-hints-test";

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
        // own size (the table is restored to the embedded English after the test).
        StringTable.Activate(GermanDraft.Table);

        foreach (var theme in new[] { "Midnight", "Daylight" })
        {
            using var scope = ThemeScope.Activate(ThemeRegistry.Resolve(theme));
            Write(RenderChip(theme, "removed"), Path.Combine(RenderDirectory, $"note-chip-removed-{theme.ToLowerInvariant()}-28dip.png"));
            Write(RenderChip(theme, "discarded"), Path.Combine(RenderDirectory, $"note-chip-discarded-{theme.ToLowerInvariant()}-28dip.png"));
        }

        Write(RenderPlaceHere(), Path.Combine(RenderDirectory, "note-place-here-de-28dip.png"));
        Write(RenderOwnerScreen(), Path.Combine(RenderDirectory, "note-owner-screen-keyboard-clear.png"));

        Check.WithCustomMessage("the render set must hold all six PNGs")
            .That(Directory.GetFiles(RenderDirectory, "note-*.png").Length).IsGreaterOrEqualThan(6);
    }

    /// <summary>The chip as the owner sees it: the German line at 28 DIP, in the card the
    /// manager would place it in.</summary>
    private static RenderTargetBitmap RenderChip(string theme, string kind)
    {
        var texts = ChipTexts();
        var column = NoteLayout.ChipTextWidthDip(28, texts);
        var card = NoteLayout.ChipCardSize(28, texts);
        var text = kind == "removed"
            ? StringTable.Current.T("overlays.note.chip.removed")
            : StringTable.Current.T("overlays.note.chip.discarded");

        var window = new NoteWindow($"hint-render-{kind}");
        window.SetTextSize(28);
        window.SetState(NoteState.PendingUndo);
        window.SetChipText(text, 28, column);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

        // The chip's own placement: the card the manager would place it in, so the render shows
        // the card and the line in the frame the owner sees.
        var placement = NoteLayout.Place(
            new PhysicalPoint(1200, 1200), NoteSide.Right, card, WorkArea,
            Array.Empty<Rect>(), 2.0)!;
        window.ApplyPlacement(placement);
        window.UpdateContent();

        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(window.Width * 2.0), (int)Math.Ceiling(window.Height * 2.0),
            192, 192, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        return bitmap;
    }

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
    private static RenderTargetBitmap RenderOwnerScreen()
    {
        var card = new Size(
            NoteLayout.CardWidthDip(NoteSettings.DefaultTextSizeDip),
            NoteLayout.CardHeightDip(NoteSettings.DefaultTextSizeDip, 0, WorkArea.Height));
        var placement = NoteLayout.Place(
            new PhysicalPoint(960 * 2, 300 * 2), NoteSide.Right, card, WorkArea,
            new[] { Keyboard, EdgeBar }, 2.0, exclude: null, allowDisplaced: true)!;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)), null,
                new Rect(0, 0, WorkArea.Width, WorkArea.Height));
            // The keyboard band and its edge bar, where the owner's keyboard sits.
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x30, 0x30, 0x38)), null, Keyboard);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0x50, 0x50, 0x5A)), null, EdgeBar);
            // The note, where it lands: clear of the keyboard.
            context.DrawRectangle(Brushes.Black, null, placement.Card);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x40)), null, placement.Card);
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
                var text = table.T(language, key);
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
