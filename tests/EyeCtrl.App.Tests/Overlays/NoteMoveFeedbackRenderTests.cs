namespace EyeCtrl.App.Tests.Overlays;

using System;
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
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using NFluent;
using Xunit;

/// <summary>
/// The sitting-9 renders for the owner: the dimmed card with its Move hint, the settle preview
/// mid-way (ring half-filled, dashed landing outline) and the place-here disc with its arc
/// half-filled — all German, at the note's 28 DIP body size, on the Midnight theme, at 200 %.
/// <para>
/// The PNGs land in a TEMP folder (<c>%TEMP%\eyectrl-note-move-feedback</c>), never in the
/// repository: the ticket's copy under the epic's <c>renders/</c> folder is made from there, so
/// every App run (CI included) stops rewriting what the owner looks at. The windows are built and
/// measured, never shown (the note seams and <see cref="InvisibleTestWindow"/> keep them off the
/// screen).
/// </para>
/// </summary>
[Collection("StringTable Tests (App)")]
public class NoteMoveFeedbackRenderTests
{
    /// <summary>The render folder: a TEMP one, like the note-hint renders.</summary>
    internal static string RenderDirectory => Path.Combine(Path.GetTempPath(), "eyectrl-note-move-feedback");

    /// <summary>The owner's screen: 3840x2400 physical at 200 % = 1920x1200 DIP.</summary>
    private static readonly Rect WorkArea = new(0, 0, 1920, 1200);

    private const double TextSize = NoteSettings.DefaultTextSizeDip;

    [StaFact]
    public void RendersTheMoveHint_TheMidSettlePreview_AndThePlaceDisc()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            Directory.CreateDirectory(RenderDirectory);
        }
        catch (Exception ex)
        {
            // The renders are for the OWNER's machine; where the folder cannot be made the test
            // skips rather than failing a build that has nothing to show for it.
            Assert.Skip($"the render folder is not available here ({RenderDirectory}): {ex.Message}");
            return;
        }

        StringTable.Activate(GermanDraft.Table);
        try
        {
            using var scope = ThemeScope.Activate(ThemeRegistry.Resolve("Midnight"));
            Write(RenderMoveCard(), Path.Combine(RenderDirectory, "note-move-card-hint-de-28dip.png"));
            Write(RenderMidSettlePreview(), Path.Combine(RenderDirectory, "note-move-preview-mid-settle-de-28dip.png"));
            Write(RenderPlaceDisc(), Path.Combine(RenderDirectory, "note-place-disc-half-de-28dip.png"));
        }
        finally
        {
            StringTable.ResetToEmbeddedEnglish();
        }
    }

    /// <summary>The card in Move with its hint, cropped to the card and drawn at the 0.6 the Move
    /// state dims it to — the picture the owner gets when ↕ picks a note up.</summary>
    private static RenderTargetBitmap RenderMoveCard()
    {
        const string text = "Einkauf: Milch, Brot, Äpfel für morgen";
        var window = new NoteWindow("render-move-card");
        InvisibleTestWindow.Prepare(window);
        window.SetTextSize(TextSize);
        window.SetText(text);
        var card = new Size(
            NoteLayout.CardWidthDip(TextSize),
            NoteLayout.CardHeightDip(TextSize, window.MeasureTextHeightDip(text), WorkArea.Height));
        var placement = NoteLayout.WithTagClearance(
            NoteLayout.Place(new PhysicalPoint(1600, 1000), NoteSide.Right, card, WorkArea,
                Array.Empty<Rect>(), 2.0)!,
            TextSize);
        window.ApplyPlacement(placement);
        window.SetState(NoteState.Move);
        // The manager pushes Other-side availability on pick-up; draw it here so the render shows
        // the card's real Move set (⇄ Andere Seite and ⊘ Abbrechen).
        window.SetOtherSideAvailable(true);

        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(0, 0, window.Width, window.Height));

        var local = NoteLayout.RectLocal(placement);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            DrawPage(context, new Size(local.Width, local.Height));
            // The Move dim lives on the card's own elements now (F1), not on the window: the
            // canvas renders the dimmed card and the full-opacity hint and discs as they are.
            context.DrawRectangle(CanvasBrush(canvas, local), null, new Rect(0, 0, local.Width, local.Height));
        }
        return Bitmap(visual, local.Width, local.Height);
    }

    /// <summary>The preview mid-settle: the ring around the anchor half-filled, and the dashed
    /// landing outline for the card the same derivation would place.</summary>
    private static RenderTargetBitmap RenderMidSettlePreview()
    {
        var window = new NoteMovePreviewWindow();
        InvisibleTestWindow.Prepare(window);
        var anchorDip = new Point(400, 900);
        var card = new Size(
            NoteLayout.CardWidthDip(TextSize),
            NoteLayout.CardHeightDip(TextSize, NoteLayout.TextLineHeightDip(TextSize) * 2, WorkArea.Height));
        var placement = NoteLayout.WithTagClearance(
            NoteLayout.Place(new PhysicalPoint(800, 1800), NoteSide.Right, card, WorkArea,
                Array.Empty<Rect>(), 2.0)!,
            TextSize);
        window.Place(anchorDip, placement.Card, progress: 0.5, ring: true);

        var size = window.WindowRectDip.Size;
        var canvas = (Canvas)window.Content;
        canvas.Measure(size);
        canvas.Arrange(new Rect(0, 0, size.Width, size.Height));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            DrawPage(context, size);
            context.DrawRectangle(CanvasBrush(canvas, new Rect(0, 0, size.Width, size.Height)), null,
                new Rect(0, 0, size.Width, size.Height));
        }
        return Bitmap(visual, size.Width, size.Height);
    }

    /// <summary>The place-here disc with its dwell arc half-filled and the German label under it.</summary>
    private static RenderTargetBitmap RenderPlaceDisc()
    {
        var window = new NotePlaceHereWindow(TextSize);
        InvisibleTestWindow.Prepare(window);
        window.UpdateDwellProgress(0.5);

        var size = window.SizeDip;
        var canvas = (Canvas)window.Content;
        canvas.Measure(size);
        canvas.Arrange(new Rect(0, 0, size.Width, size.Height));

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            DrawPage(context, size);
            context.DrawRectangle(CanvasBrush(canvas, new Rect(0, 0, size.Width, size.Height)), null,
                new Rect(0, 0, size.Width, size.Height));
        }
        return Bitmap(visual, size.Width, size.Height);
    }

    /// <summary>The owner's dark desktop, so the transparent overlays' strokes read on the PNG.</summary>
    private static void DrawPage(DrawingContext context, Size size) =>
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28)), null,
            new Rect(0, 0, size.Width, size.Height));

    /// <summary>A 1:1 brush over one region of a window's canvas (the note renders' recipe).</summary>
    private static VisualBrush CanvasBrush(Canvas canvas, Rect region) => new(canvas)
    {
        Stretch = Stretch.None,
        ViewboxUnits = BrushMappingMode.Absolute,
        Viewbox = region
    };

    /// <summary>Renders a visual at 200 % (192 DPI) and writes it as a PNG, checking it is real.</summary>
    private static RenderTargetBitmap Bitmap(Visual visual, double widthDip, double heightDip)
    {
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(widthDip * 2.0), (int)Math.Ceiling(heightDip * 2.0),
            192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
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
