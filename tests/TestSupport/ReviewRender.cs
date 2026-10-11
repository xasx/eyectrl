namespace EyeCtrl.Tests.TestSupport;

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// T10's review sheets: production elements rendered headless at the user's 2.0, for the user's
/// one review sitting (German next to English, in context). Nothing here is a gate — the writers
/// run only when <c>EYECTRL_WRITE_T10_REVIEW=1</c>, so a normal test run writes no file and
/// shows nothing on screen (the elements are hosted in <see cref="FitHost"/>'s off-screen source,
/// never shown). EyeCtrl.App.Tests and EyeCtrl.Supervisor.Tests link this one file: the
/// Supervisor's windows are rendered there as fragments, which App.Tests composes into the sheets.
/// </summary>
internal static class ReviewRender
{
    /// <summary>True when the review sheets were asked for.</summary>
    internal static bool Requested => Environment.GetEnvironmentVariable("EYECTRL_WRITE_T10_REVIEW") == "1";

    /// <summary>
    /// The sheet directory: <c>EYECTRL_T10_REVIEW_DIR</c>, or <c>eyectrl-t10-review</c> under the
    /// user's temp folder. Both test projects resolve the same folder, so the Supervisor's fragments
    /// reach the App's sheet composer either way.
    /// </summary>
    internal static string Directory =>
        Environment.GetEnvironmentVariable("EYECTRL_T10_REVIEW_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Path.GetTempPath(), "eyectrl-t10-review");

    /// <summary>Where one project's rendered pieces wait for the sheet composer.</summary>
    internal static string FragmentPath(string name) => Path.Combine(Directory, "fragments", name + ".png");

    /// <summary>
    /// Hosts <paramref name="content"/> at 2.0 (see <see cref="FitHost.Open"/>) and renders it at
    /// its laid-out DIP size into a 192-DPI bitmap — one DIP is two pixels, as on the user's screen.
    /// </summary>
    internal static BitmapSource Snap(
        FrameworkElement content, double? width = null, double? height = null, Control? inheritFrom = null,
        FontFamily? family = null, Brush? background = null)
    {
        using var host = FitHost.Open(content, width, height, inheritFrom, family);
        return Render(content, new Size(content.ActualWidth, content.ActualHeight), background);
    }

    /// <summary>Renders an already laid-out element at <paramref name="size"/> DIP into a 192-DPI bitmap.</summary>
    internal static BitmapSource Render(Visual visual, Size size, Brush? background = null)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(size.Width * FitHost.Scale));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(size.Height * FitHost.Scale));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * FitHost.Scale, 96 * FitHost.Scale, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            if (background is not null) dc.DrawRectangle(background, null, new Rect(size));
            dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(size));
        }

        bitmap.Render(drawing);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Writes <paramref name="bitmap"/> as a PNG, creating the directory.</summary>
    internal static string Save(BitmapSource bitmap, string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    /// <summary>Reads a PNG written by <see cref="Save"/>, fully loaded and frozen.</summary>
    internal static BitmapSource Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{path} is missing: run the Supervisor fragments first (EyeCtrl.Supervisor.Tests, filter ReviewFragments).", path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>A bitmap's size in DIP at the user's 2.0 (pixels / 2).</summary>
    internal static Size DipSize(BitmapSource bitmap) =>
        new(bitmap.PixelWidth / FitHost.Scale, bitmap.PixelHeight / FitHost.Scale);
}
