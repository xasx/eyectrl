namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using Xunit;

/// <summary>
/// P4 D5: the owner's reading-test renders. Headless - the window is NEVER shown; a
/// RenderTargetBitmap draws the real NoteWindow visual. The helper writes a Display card per
/// text size (18 / 24 / 28 / 32 / 40 DIP) in the two themes the reading test reads (Midnight =
/// the owner's default, Daylight = the light theme), at the user's 2.0, into a documented
/// folder. The owner opens the PNGs at 1:1 on the 18-inch panel, a caretaker reads them from
/// their seat, and the smallest comfortable size becomes the default - a one-constant change
/// (<see cref="EyeCtrl.Core.Settings.NoteSettings.DefaultTextSizeDip"/>).
///
/// <para>
/// Output: <c>note-reading-renders/</c> at the repo root (git-excluded), overridable with
/// EYECTRL_READING_RENDER_DIR. The folder path is part of the deliverable.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class NoteReadingRenderTests
{
    internal static readonly int[] Sizes = [18, 24, 28, 32, 40];

    internal static readonly string[] ThemeNames = ["Midnight", "Daylight"];

    internal const string SampleText =
        "Meds at 8:00 - drink a full glass of water.\n" +
        "Back at 14:00. Anna calls afterwards.\n" +
        "Physiotherapy on Thursday at 10:00 - take a taxi. \u2713";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var themeName in ThemeNames)
            foreach (var size in Sizes)
                yield return new object[] { themeName, size };
    }

    [StaTheory]
    [MemberData(nameof(Cases))]
    public void RendersAReadableCard(string themeName, int size)
    {
        var directory = RenderDirectory();
        Directory.CreateDirectory(directory);
        RenderCard(themeName, size, directory);
    }

    [Fact]
    public void TheRenderSet_IsComplete_TenFilesBothThemes()
    {
        var directory = RenderDirectory();
        Directory.CreateDirectory(directory);
        foreach (var themeName in ThemeNames)
            foreach (var size in Sizes)
            {
                var file = Path.Combine(directory, $"note-{themeName.ToLowerInvariant()}-{size}dip.png");
                Check.WithCustomMessage($"{file} missing - run the render theory once first")
                    .That(File.Exists(file)).IsTrue();
            }
        Check.That(Directory.GetFiles(directory, "note-*.png").Length).IsEqualTo(10);
    }

    internal static void RenderCard(string themeName, int size, string directory)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve(themeName));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow("render-note");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Display);
            window.SetTextSize(size);
            var card = new Size(
                NoteLayout.CardWidthDip(size),
                NoteLayout.CardHeightDip(size, NoteLayout.TextCapHeightDip(1200, size), 1200));
            var placement = NoteLayout.Place(
                new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!;
            window.ApplyPlacement(placement);
            window.SetText(SampleText);
            window.UpdateContent();

            // The canvas as the window lays it out - no HWND, nothing on the owner's screen.
            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            int pxWidth = (int)Math.Ceiling(window.Width * 2.0);
            int pxHeight = (int)Math.Ceiling(window.Height * 2.0);
            var bitmap = new RenderTargetBitmap(pxWidth, pxHeight, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(canvas);

            var file = Path.Combine(directory, $"note-{themeName.ToLowerInvariant()}-{size}dip.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file))
                encoder.Save(stream);

            Check.WithCustomMessage($"{file} must be a real render")
                .That(new FileInfo(file).Length).IsStrictlyGreaterThan(0);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    internal static string RenderDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("EYECTRL_READING_RENDER_DIR");
        return overrideDir is { Length: > 0 }
            ? overrideDir
            : Path.Combine(SourceScanner.RepoRoot().FullName, "note-reading-renders");
    }
}
