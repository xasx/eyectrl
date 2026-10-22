namespace EyeCtrl.App.Tests.Overlays;

using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.App.Tests.TestSupport;

using System;
using System.Linq;
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
/// T5 review-03 G1 (the reviewer's rendered-ink probe, adopted as assertions): every visible
/// disc glyph's INK - measured by rendering the canvas with and without the label and diffing
/// the pixels - must have its centre within 1.5 DIP of the disc centre and a radius of at most
/// 22.5 DIP (the dwell arc's inner edge 24.5 minus 2 DIP of clearance, the #190 limit), for all
/// four states x both themes at DPI 2.0. Box centring sat every glyph 6-10 DIP low and cannot
/// pass this; only ink centring can.
/// </summary>
[Collection("WpfApplication")]
public class NoteGlyphInkTests
{
    private const double MaxInkRadiusDip = 22.5;
    private const double MaxCentreOffsetDip = 1.5;

    [StaTheory]
    [InlineData("Midnight")]
    [InlineData("Daylight")]
    public void EveryDiscGlyph_CentresItsInk_AndStaysInsideTheArc(string themeName)
    {
        using var themeScope = ThemeScope.Activate(ThemeRegistry.Resolve(themeName));
        DpiHelper.Initialize(2.0, 2.0);
        try
        {
            foreach (var state in new[] { NoteState.Display, NoteState.Edit, NoteState.Move, NoteState.PendingUndo })
            {
                var window = new NoteWindow("ink");
                InvisibleTestWindow.Prepare(window);
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                window.SetState(state);
                if (state == NoteState.Move) window.SetOtherSideAvailable(true);
                window.SetTextSize(28);
                var card = new Size(NoteLayout.CardWidthDip(28), NoteLayout.ControlsColumnHeightDip);
                var placement = NoteLayout.Place(new PhysicalPoint(1200, 1000), NoteSide.Right, card,
                    new Rect(0, 0, 1920, 1200), Array.Empty<Rect>(), 2.0)!;
                window.ApplyPlacement(placement);
                window.UpdateContent();
                var canvas = (Canvas)window.Content;

                foreach (var (id, disc) in window.ButtonsForTest())
                {
                    if (disc.Visibility != Visibility.Visible) continue;
                    var label = window.LabelsForTest()[id];
                    double cx = Canvas.GetLeft(disc) + window.ButtonRadiusForTest;
                    double cy = Canvas.GetTop(disc) + window.ButtonRadiusForTest;

                    var with = Render(canvas, window);
                    var keep = label.Visibility;
                    label.Visibility = Visibility.Hidden;
                    var without = Render(canvas, window);
                    label.Visibility = keep;

                    int stride = with.w * 4;
                    double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue, maxR = 0;
                    var count = 0;
                    int px0 = (int)((cx - 30) * 2), px1 = (int)((cx + 30) * 2), py0 = (int)((cy - 30) * 2), py1 = (int)((cy + 30) * 2);
                    for (var y = Math.Max(0, py0); y < Math.Min(with.h, py1); y++)
                    {
                        for (var x = Math.Max(0, px0); x < Math.Min(with.w, px1); x++)
                        {
                            var i = (y * stride) + (x * 4);
                            var d = Math.Abs(with.px[i] - without.px[i]) + Math.Abs(with.px[i + 1] - without.px[i + 1]) + Math.Abs(with.px[i + 2] - without.px[i + 2]);
                            if (d < 40) continue;
                            double xd = (x / 2.0) + 0.25, yd = (y / 2.0) + 0.25;
                            minX = Math.Min(minX, xd); maxX = Math.Max(maxX, xd);
                            minY = Math.Min(minY, yd); maxY = Math.Max(maxY, yd);
                            maxR = Math.Max(maxR, Math.Sqrt(((xd - cx) * (xd - cx)) + ((yd - cy) * (yd - cy))));
                            count++;
                        }
                    }

                    var suffix = id.Split(':').Last();
                    Check.WithCustomMessage($"{themeName}/{state}: the {suffix} glyph rendered NO ink")
                        .That(count).IsStrictlyGreaterThan(0);
                    double dx = ((minX + maxX) / 2) - cx;
                    double dy = ((minY + maxY) / 2) - cy;
                    Check.WithCustomMessage($"{themeName}/{state}: the {suffix} glyph ink centre sits (dx {dx:+0.0;-0.0}, dy {dy:+0.0;-0.0}) off the disc centre - must be within {MaxCentreOffsetDip} DIP")
                        .That(Math.Sqrt((dx * dx) + (dy * dy)) <= MaxCentreOffsetDip).IsTrue();
                    Check.WithCustomMessage($"{themeName}/{state}: the {suffix} glyph ink radius {maxR:0.0} exceeds the {MaxInkRadiusDip} DIP arc-clearance limit")
                        .That(maxR <= MaxInkRadiusDip).IsTrue();
                }
            }
        }
        finally { DpiHelper.Initialize(1.0, 1.0); }
    }

    private static (byte[] px, int w, int h) Render(Canvas canvas, Window window)
    {
        canvas.Measure(new Size(window.Width, window.Height));
        canvas.Arrange(new Rect(new Point(0, 0), new Size(window.Width, window.Height)));
        var w = (int)Math.Ceiling(window.Width * 2.0);
        var h = (int)Math.Ceiling(window.Height * 2.0);
        var bmp = new RenderTargetBitmap(w, h, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(canvas);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }
}
