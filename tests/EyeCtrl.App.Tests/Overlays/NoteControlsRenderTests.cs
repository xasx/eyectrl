namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// K2a + A2 (sitting 10): the headless before/after render set at 36 DIP — the Edit card with one
/// line and with three lines, and the Display card. Written into the TEST OUTPUT folder
/// (<c>bin/.../note-controls-renders</c>) and mirrored into <c>%TEMP%</c>, so every run leaves
/// the repository clean and the images sit beside the test results. Nothing is shown: the window
/// goes through <see cref="InvisibleTestWindow.Prepare"/>, and no HWND is ever created.
/// </summary>
[Collection("WpfApplication")]
public class NoteControlsRenderTests
{
    internal static string RenderDirectory =>
        Path.Combine(AppContext.BaseDirectory, "note-controls-renders");

    internal static string MirrorDirectory =>
        Path.Combine(Path.GetTempPath(), "eyectrl-note-controls-renders");

    private const double Size = 36;

    private const string OneLine = "Milch und Brot mitbringen";
    private const string ThreeLines = "Zeile eins\nZeile zwei\nZeile drei";

    [StaFact]
    public void RendersTheBeforeAfterSet_EditOneLine_EditThreeLines_Display_At36()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        try
        {
            Directory.CreateDirectory(RenderDirectory);
            Directory.CreateDirectory(MirrorDirectory);
        }
        catch (Exception ex)
        {
            Assert.Skip($"the render folder is not available here ({RenderDirectory}): {ex.Message}");
            return;
        }

        var editOneHeight = RenderEdit(OneLine, "edit-1line-after.png", expectedLines: 1);
        var editThreeHeight = RenderEdit(ThreeLines, "edit-3lines-after.png", expectedLines: 3);
        var displayHeight = RenderDisplay("display-after.png");

        Check.WithCustomMessage($"the one-line Edit card must be the compact 194 DIP card (got {editOneHeight})")
            .That(editOneHeight).IsEqualTo(194.0);
        Check.WithCustomMessage($"the three-line Edit card must be 284 DIP (got {editThreeHeight})")
            .That(editThreeHeight).IsEqualTo(284.0);
        Check.WithCustomMessage($"the Display card keeps A2's 468 DIP column floor (got {displayHeight})")
            .That(displayHeight).IsEqualTo(NoteLayout.ControlsColumnHeightDip);
        Check.That(new FileInfo(Path.Combine(RenderDirectory, "edit-1line-after.png")).Length)
            .IsStrictlyGreaterThan(0);
    }

    /// <summary>Renders the compact Edit card at <paramref name="text"/> and returns its DIP
    /// height. The drawn text must produce the expected line count, or the "1 line"/"3 lines"
    /// name would lie.</summary>
    private static double RenderEdit(string text, string file, int expectedLines)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve("Midnight"));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow($"render-edit-{expectedLines}");
            InvisibleTestWindow.Prepare(window);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetTextSize(Size);
            window.SetState(NoteState.Edit);
            // The three controls show only while the keyboard is hidden — production drives this
            // from the manager's probe; the render wants the full Edit row.
            window.SetKeyboardControlVisible(true);

            double measured = window.MeasureTextHeightDip(text);
            Check.WithCustomMessage(
                    $"the render text must measure {expectedLines} line(s) at {Size} DIP (got {measured:0.#})")
                .That(measured).IsCloseTo(expectedLines * NoteLayout.TextLineHeightDip(Size), 1e-6);

            var card = NoteLayout.EditCardSize(Size, measured, NoteTestBench.WorkArea.Height);
            var placement = NoteLayout.WithTagClearance(
                NoteLayout.Place(new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                    NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!,
                Size);
            window.ApplyPlacement(placement);
            window.SetText(text);
            window.UpdateContent();
            window.SetBadge(1);

            Write(window, placement, file);
            return card.Height;
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>Renders the Display card at 36 (A2's four-row column) and returns its height.</summary>
    private static double RenderDisplay(string file)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve("Midnight"));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("render-display");
            InvisibleTestWindow.Prepare(window);
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetTextSize(Size);
            window.SetState(NoteState.Display);

            double measured = window.MeasureTextHeightDip(ThreeLines);
            var card = new Size(
                NoteLayout.CardWidthDip(Size),
                NoteLayout.CardHeightDip(Size, measured, NoteTestBench.WorkArea.Height));
            var placement = NoteLayout.WithTagClearance(
                NoteLayout.Place(new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                    NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!,
                Size);
            window.ApplyPlacement(placement);
            window.SetText(ThreeLines);
            window.UpdateContent();
            window.SetBadge(1);

            Write(window, placement, file);
            return card.Height;
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    /// <summary>The card's region of the canvas, drawn off-screen into a PNG and written to both
    /// folders. Never a screenshot of the screen and never a shown window.</summary>
    private static void Write(NoteWindow window, NotePlacement placement, string file)
    {
        var canvas = (Canvas)window.Content;
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

        // The card plus the number tag's strip above it (the window was grown upward for the tag),
        // so the render shows the whole note, not a clipped tag.
        var local = NoteLayout.RectLocal(placement);
        var view = new Rect(local.X, 0, local.Width, local.Bottom);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(view.Width * 2.0), (int)Math.Ceiling(view.Height * 2.0),
            192, 192, PixelFormats.Pbgra32);
        var brush = new VisualBrush(canvas)
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = view,
        };
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(brush, null, new Rect(0, 0, view.Width, view.Height));
        bitmap.Render(visual);

        foreach (var directory in new[] { RenderDirectory, MirrorDirectory })
        {
            var path = Path.Combine(directory, file);
            // A BitmapEncoder can Save once: one encoder per file.
            var fileEncoder = new PngBitmapEncoder();
            fileEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            fileEncoder.Save(stream);
            Check.WithCustomMessage($"{path} must be a real render")
                .That(new FileInfo(path).Length).IsStrictlyGreaterThan(0);
        }
    }
}
