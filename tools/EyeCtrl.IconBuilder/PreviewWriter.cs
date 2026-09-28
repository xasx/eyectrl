namespace EyeCtrl.IconBuilder;

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class PreviewWriter
{
    // A5 surfaces: 16 (tray floor), 32 (tray @200% + Explorer small), 48/64 (pinned shortcut),
    // 96 (Explorer medium), 128 (splash @64 DIP). Each rendered with the frame plan that
    // surface actually consumes, on dark and light taskbar-like chips.
    private static readonly int[] Sizes = [16, 32, 48, 64, 96, 128];

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        WriteStrip(Path.Combine(dir, "preview-dark.png"), Color.FromRgb(0x1F, 0x1F, 0x1F));
        WriteStrip(Path.Combine(dir, "preview-light.png"), Color.FromRgb(0xF4, 0xF4, 0xF4));
    }

    private static void WriteStrip(string path, Color chip)
    {
        const int pad = 10, gap = 14;
        int tallest = Sizes.Max();
        int width = pad * 2 + Sizes.Sum() + gap * (Sizes.Length - 1);
        int height = pad * 2 + tallest;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(chip), null, new Rect(0, 0, width, height));
            int x = pad;
            foreach (var size in Sizes)
            {
                var bgra = FrameRenderer.RenderBgra(FramePlan.PlanFor(size));
                var image = FrameRenderer.ToBitmapSource(size, bgra);
                dc.DrawImage(image, new Rect(x, pad + (tallest - size), size, size));
                x += size + gap;
            }
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        using var fs = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        encoder.Save(fs);
        Console.WriteLine($"wrote {path}");
    }
}
