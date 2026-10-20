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
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// T6 P5: the badge renders for the owner's and reviewer's eyes. Headless — the windows are
/// NEVER shown; a RenderTargetBitmap draws the real NoteWindow visuals (the same construction
/// as <see cref="NoteReadingRenderTests"/>). Four PNGs: three badged notes (numerals 1-3) and
/// one lone note without a badge, in Midnight and Daylight at 28 DIP, into
/// <c>note-badge-renders/</c> next to <c>note-reading-renders/</c> (git-excluded; the folder
/// path is part of the deliverable).
/// </summary>
[Collection("WpfApplication")]
public class NoteBadgeRenderTests
{
    internal const string BadgeRenderDirName = "note-badge-renders";

    public static TheoryData<string> ThemeNames()
    {
        var data = new TheoryData<string> { "Midnight", "Daylight" };
        return data;
    }

    [StaTheory]
    [MemberData(nameof(ThemeNames))]
    public void RendersTheBadgedSet(string themeName)
    {
        var directory = Path.Combine(SourceScanner.RepoRoot().FullName, BadgeRenderDirName);
        Directory.CreateDirectory(directory);
        RenderSet(themeName, directory);
    }

    [StaFact]
    public void TheRenderSet_IsComplete_SixFilesBothThemesAndAllSizes()
    {
        // Self-sufficient: renders the whole set itself, so test ORDER cannot decide whether
        // the files exist (the NoteReadingRenderTests discipline).
        var directory = Path.Combine(SourceScanner.RepoRoot().FullName, BadgeRenderDirName);
        Directory.CreateDirectory(directory);
        foreach (var themeName in new[] { "Midnight", "Daylight" })
            RenderSet(themeName, directory);
        RenderBadgedSet("Midnight", 18, directory);
        RenderBadgedSet("Midnight", 48, directory);

        Check.WithCustomMessage("the badge render set must hold exactly 6 PNGs — the 4 at 28 "
                + "plus the Midnight column at 18 and at 48")
            .That(Directory.GetFiles(directory, "note-badges-*.png").Length).IsEqualTo(6);
    }

    private static void RenderSet(string themeName, string directory)
    {
        RenderBadgedSet(themeName, 28, directory);
        var lone = RenderOneLoneCard(themeName);
        Write(lone, Path.Combine(directory, $"note-badges-{themeName.ToLowerInvariant()}-lone-28dip.png"));
    }

    private static void RenderBadgedSet(string themeName, double textSize, string directory)
    {
        var badged = RenderThreeBadgedCards(themeName, textSize);
        Write(badged, Path.Combine(directory, $"note-badges-{themeName.ToLowerInvariant()}-{(int)textSize}dip.png"));
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

    /// <summary>Renders three Display cards with badges 1, 2 and 3 at the given text size,
    /// composed vertically into one bitmap.</summary>
    private static RenderTargetBitmap RenderThreeBadgedCards(string themeName, double textSize)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve(themeName));
        var frames = new (RenderTargetBitmap Bitmap, double Width, double Height)[3];
        for (int i = 0; i < 3; i++)
            frames[i] = RenderCard(themeName, badgeNumber: i + 1, lone: false, textSize: textSize);

        double width = frames.Max(f => f.Width);
        double height = frames.Sum(f => f.Height) + 40;
        return Compose(frames, width, height);
    }

    private static RenderTargetBitmap RenderOneLoneCard(string themeName)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve(themeName));
        var frame = RenderCard(themeName, badgeNumber: null, lone: true, textSize: 28);
        return Compose(new[] { frame }, frame.Width, frame.Height);
    }

    private static (RenderTargetBitmap Bitmap, double Width, double Height) RenderCard(
        string themeName, int? badgeNumber, bool lone, double textSize)
    {
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            var window = new NoteWindow($"badge-render-{badgeNumber?.ToString() ?? "lone"}");
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            window.SetState(NoteState.Display);
            window.SetTextSize(textSize);
            string text = lone
                ? "Only one note - no badge."
                : $"Note {badgeNumber} of three - the badge names it.";
            var card = new Size(
                NoteLayout.CardWidthDip(textSize),
                NoteLayout.CardHeightDip(textSize, window.MeasureTextHeightDip(text), 1200));
            double y = badgeNumber switch { 1 => 900, 2 => 100, 3 => 300, _ => 1200 };
            var placement = NoteLayout.WithTagClearance(
                NoteLayout.Place(
                    new PhysicalPoint(1200, y), NoteSide.Right, card,
                    NoteTestBench.WorkArea, Array.Empty<Rect>(), 2.0)!,
                textSize);
            window.ApplyPlacement(placement);
            window.SetText(text);
            window.UpdateContent();
            if (badgeNumber is { } number)
                window.SetBadge(number);

            var canvas = (Canvas)window.Content;
            canvas.Measure(new Size(window.Width, window.Height));
            canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));

            int pxWidth = (int)Math.Ceiling(window.Width * 2.0);
            int pxHeight = (int)Math.Ceiling(window.Height * 2.0);
            var bitmap = new RenderTargetBitmap(pxWidth, pxHeight, 192, 192, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            return (bitmap, window.Width, window.Height);
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static RenderTargetBitmap Compose(
        (RenderTargetBitmap Bitmap, double Width, double Height)[] frames, double width, double height)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.DimGray, null, new Rect(0, 0, width, height));
            double y = 0;
            foreach (var frame in frames)
            {
                context.DrawImage(frame.Bitmap, new Rect(0, y, frame.Width, frame.Height));
                y += frame.Height + 20;
            }
        }

        var composed = new RenderTargetBitmap(
            (int)Math.Ceiling(width * 2.0), (int)Math.Ceiling(height * 2.0), 192, 192, PixelFormats.Pbgra32);
        composed.Render(visual);
        return composed;
    }
}
