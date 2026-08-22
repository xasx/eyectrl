namespace EyeCtrl.Controls.Tests;

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// <see cref="DwellOutlineOverlay"/> replaces the fill wash on the keyboard, the prediction bar
/// and the emoji category tabs. That wash ran at full strength, so a normal key's label fell
/// from 10.42:1 at rest to 1.08:1 at full dwell under Midnight (12.91 to 1.18 Ember, 19.17 to
/// 1.25 Contrast) — the keyboard is the most-dwelled surface in the app, and the collapse was
/// worst in the theme built for low vision.
/// </summary>
public class DwellOutlineOverlayTests
{
    private static double Channel(byte c)
    {
        var v = c / 255.0;
        return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static Border NewKey(Color fill, double size = 70) => new()
    {
        Width = size,
        Height = size,
        Background = new SolidColorBrush(fill),
        CornerRadius = Tokens.StandardCornerRadius
    };

    [StaFact]
    public void Attach_PutsContentAndBothOutlinesUnderTheTarget()
    {
        var key = NewKey(Tokens.SurfaceKey);
        var label = new TextBlock { Text = "A" };

        var overlay = DwellOutlineOverlay.Attach(key, label);

        Check.That(key.Child).IsSameReferenceAs(overlay.Layout);
        Check.That(overlay.Layout.Children.Cast<UIElement>())
            .Contains(label, overlay.Halo, overlay.Outline);
        // Accent last so it paints over the halo, which is the outline's outer edge.
        Check.That(overlay.Layout.Children[overlay.Layout.Children.Count - 1])
            .IsSameReferenceAs(overlay.Outline);
    }

    [StaFact]
    public void SetProgress_ThickensBothOutlines_AndClamps()
    {
        var overlay = DwellOutlineOverlay.Attach(NewKey(Tokens.SurfaceKey), null);

        Check.That(overlay.Outline.StrokeThickness).IsEqualTo(0.0);
        Check.That(overlay.Halo.StrokeThickness).IsEqualTo(0.0);

        overlay.SetProgress(0.5);
        Check.That(overlay.Outline.StrokeThickness).IsEqualTo(DwellOutlineOverlay.MaxThickness / 2);
        Check.That(overlay.Halo.StrokeThickness)
            .IsStrictlyGreaterThan(overlay.Outline.StrokeThickness);

        overlay.SetProgress(2);
        Check.That(overlay.Outline.StrokeThickness).IsEqualTo(DwellOutlineOverlay.MaxThickness);

        overlay.SetProgress(-1);
        Check.That(overlay.Outline.StrokeThickness).IsEqualTo(0.0);
    }

    /// <summary>
    /// At rest there must be no outline at all. A halo that lingered at zero progress would
    /// read as a permanent border on every key rather than as feedback.
    /// </summary>
    [StaFact]
    public void AtRest_NeitherOutlineIsDrawn()
    {
        var overlay = DwellOutlineOverlay.Attach(NewKey(Tokens.SurfaceKey), null);

        overlay.SetProgress(0.8);
        overlay.SetProgress(0);

        Check.That(overlay.Outline.StrokeThickness).IsEqualTo(0.0);
        Check.That(overlay.Halo.StrokeThickness).IsEqualTo(0.0);
    }

    /// <summary>
    /// The whole reason the feedback moved off the fill: neither the key's fill nor its label
    /// may change with progress, so contrast at the end of a dwell equals contrast at rest.
    /// </summary>
    [StaFact]
    public void SetProgress_TouchesNeitherTheFillNorTheLabel()
    {
        var key = NewKey(Tokens.SurfaceKey);
        var label = new TextBlock { Text = "A", Foreground = Tokens.BrushTextPrimary };
        var overlay = DwellOutlineOverlay.Attach(key, label);

        foreach (var p in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            overlay.SetProgress(p);
            Check.WithCustomMessage($"key fill moved at progress {p}")
                .That(((SolidColorBrush)key.Background).Color).IsEqualTo(Tokens.SurfaceKey);
            Check.WithCustomMessage($"label brush changed at progress {p}")
                .That(label.Foreground).IsSameReferenceAs(Tokens.BrushTextPrimary);
        }
    }

    /// <summary>
    /// The stroke is centred on the geometry edge, so its outward half would paint past the
    /// target's rounded silhouette — a WPF Border does not clip its Child to CornerRadius.
    /// Pinned structurally rather than by pixel, for a reason worth recording: the render test
    /// below samples the EXTREME corners, which the arc leaves transparent either way, so
    /// deleting the clip left the whole suite green (measured). What the clip actually changes
    /// is 47 pixels along the corner ARCS, by about 10/255 of alpha — real, but subtle
    /// anti-aliasing rather than a visible nub, and too small to assert on robustly.
    /// </summary>
    [StaFact]
    public void TheLayout_IsClippedToTheTargetsRoundedSilhouette()
    {
        const int size = 70;
        var key = NewKey(Tokens.SurfaceKey, size);
        var overlay = DwellOutlineOverlay.Attach(key, null);

        key.Measure(new Size(size, size));
        key.Arrange(new Rect(0, 0, size, size));
        key.UpdateLayout();

        Check.WithCustomMessage(
                "the outline's layout is unclipped, so the outward half of its stroke paints "
                + "past the key's rounded corners.")
            .That(overlay.Layout.Clip).IsInstanceOf<RectangleGeometry>();

        var clip = (RectangleGeometry)overlay.Layout.Clip;
        Check.That(clip.RadiusX).IsEqualTo(Tokens.CornerRadiusStandard);
        Check.That(clip.RadiusY).IsEqualTo(Tokens.CornerRadiusStandard);
        Check.That(clip.Rect).IsEqualTo(new Rect(0, 0, size, size));
    }

    [StaFact]
    public void TheOutline_IsAccentOverAHalo_AndIsNotHitTestable()
    {
        var overlay = DwellOutlineOverlay.Attach(NewKey(Tokens.SurfaceKey), null);

        Check.That(overlay.Outline.Stroke).IsEqualTo(Tokens.BrushAccent);
        Check.That(overlay.Halo.Stroke).IsEqualTo(Tokens.BrushStrokeHalo);
        Check.That(overlay.Outline.IsHitTestVisible).IsFalse();
        Check.That(overlay.Halo.IsHitTestVisible).IsFalse();
    }

    /// <summary>
    /// The accent alone does not carry every fill the keyboard uses: on an ACTIVE modifier under
    /// Contrast it is 1.91:1. The halo is what rescues that case at 15.30:1. Gates
    /// max(accent, halo) against every key fill in every theme, so removing either term fails.
    /// </summary>
    [Theory]
    [InlineData("SurfaceKey")]
    [InlineData("SurfaceKeyModifier")]
    [InlineData("SurfaceKeyActive")]
    public void TheOutline_IsPerceptibleAgainstEveryKeyFill_InEveryTheme(string fillName)
    {
        foreach (var theme in ThemeRegistry.All)
        {
            var fill = fillName switch
            {
                "SurfaceKey" => theme.SurfaceKey,
                "SurfaceKeyModifier" => theme.SurfaceKeyModifier,
                _ => theme.SurfaceKeyActive
            };

            var best = Math.Max(Contrast(theme.Accent, fill), Contrast(theme.StrokeHalo, fill));
            Check.WithCustomMessage(
                    $"{theme.Name}: outline vs {fillName} reaches only {best:F2}:1 "
                    + $"(accent {Contrast(theme.Accent, fill):F2}, halo "
                    + $"{Contrast(theme.StrokeHalo, fill):F2}) — the outline must clear 3:1 by "
                    + "one term or the other, or the user cannot see the dwell.")
                .That(best).IsStrictlyGreaterThan(3.0);
        }
    }

    /// <summary>
    /// Measured, not assumed: a growing stroke changes a <see cref="Shape"/>'s desired size, so
    /// the risk was that dwelling would shift the label or grow an auto-width button. It does
    /// neither, because the outline is <c>Stretch</c>-aligned and the stroke's outward half is
    /// clipped. Both the fixed-size case (a key) and the auto-width case (a prediction button)
    /// are covered, since only the latter can grow.
    /// </summary>
    [StaFact]
    public void Dwelling_MovesNoLabel_AndGrowsNoButton()
    {
        foreach (var autoWidth in new[] { false, true })
        {
            var target = autoWidth
                ? new Border
                {
                    MinWidth = 120,
                    Height = 70,
                    Padding = new Thickness(16, 0, 16, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Tokens.SurfaceKey),
                    CornerRadius = Tokens.StandardCornerRadius
                }
                : NewKey(Tokens.SurfaceKey);

            var label = new TextBlock
            {
                Text = autoWidth ? "prediction" : "A",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var overlay = DwellOutlineOverlay.Attach(target, label);

            var panel = new StackPanel();
            panel.Children.Add(target);
            var host = new Border { Child = panel, Width = 400, Height = 300 };

            void Layout()
            {
                host.Measure(new Size(400, 300));
                host.Arrange(new Rect(0, 0, 400, 300));
                host.UpdateLayout();
            }

            Layout();
            var restPos = label.TransformToAncestor(target).Transform(new Point(0, 0));
            var restSize = new Size(target.ActualWidth, target.ActualHeight);

            overlay.SetProgress(1.0);
            Layout();
            var dwellPos = label.TransformToAncestor(target).Transform(new Point(0, 0));
            var dwellSize = new Size(target.ActualWidth, target.ActualHeight);

            var which = autoWidth ? "auto-width button" : "fixed key";
            Check.WithCustomMessage(
                    $"{which}: label moved from {restPos} to {dwellPos} as the outline grew — "
                    + "the layout the user's muscle memory is built on must not shift on dwell.")
                .That(dwellPos).IsEqualTo(restPos);
            Check.WithCustomMessage($"{which}: grew from {restSize} to {dwellSize} on dwell.")
                .That(dwellSize).IsEqualTo(restSize);
        }
    }

    /// <summary>
    /// Rendering proof, which no property read can give: at full dwell the accent must actually
    /// PAINT (an Opacity-0 or zero-thickness outline satisfies every assertion above while
    /// showing the user nothing), and it must not paint outside the target's rounded silhouette
    /// — a WPF Border does not clip its Child to CornerRadius, and half of a stroke falls
    /// outside the geometry edge by default.
    /// </summary>
    [StaFact]
    public void AtFullDwell_TheOutlinePaints_AndStaysInsideTheRoundedSilhouette()
    {
        const int size = 70;
        var key = NewKey(Tokens.SurfaceKey, size);
        var overlay = DwellOutlineOverlay.Attach(key, new TextBlock { Text = "A" });
        overlay.SetProgress(1.0);

        key.Measure(new Size(size, size));
        key.Arrange(new Rect(0, 0, size, size));
        key.UpdateLayout();

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(key);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        rtb.CopyPixels(pixels, stride, 0);

        Color At(int x, int y) => Color.FromRgb(
            pixels[y * stride + x * 4 + 2], pixels[y * stride + x * 4 + 1], pixels[y * stride + x * 4]);
        byte Alpha(int x, int y) => pixels[y * stride + x * 4 + 3];

        // Nothing outside the 8 DIP corner band.
        foreach (var (x, y) in new[] { (0, 0), (size - 1, 0), (0, size - 1), (size - 1, size - 1) })
        {
            Check.WithCustomMessage(
                    $"pixel at ({x},{y}) has alpha {Alpha(x, y)} — the outline is painting past "
                    + "the key's rounded silhouette.")
                .That(Alpha(x, y)).IsEqualTo((byte)0);
        }

        // Mid-edge, a couple of DIPs in: the accent must be there. Colour, not alpha — the key
        // fill is opaque, so alpha reads 255 whether or not the outline drew.
        var mid = At(size / 2, 2);
        Check.WithCustomMessage(
                $"pixel at ({size / 2},2) is R{mid.R},G{mid.G},B{mid.B}, not the Accent "
                + $"R{Tokens.Accent.R},G{Tokens.Accent.G},B{Tokens.Accent.B} — the outline is in "
                + "the visual tree but paints nothing at full dwell.")
            .That((mid.R, mid.G, mid.B))
            .IsEqualTo((Tokens.Accent.R, Tokens.Accent.G, Tokens.Accent.B));

        // The centre must still be the key's own fill: an outline, not a wash.
        var centre = At(size / 2, size / 2);
        Check.WithCustomMessage(
                $"the key's centre is R{centre.R},G{centre.G},B{centre.B} at full dwell, not its "
                + "own fill — the feedback is washing the fill rather than outlining it.")
            .That((centre.R, centre.G, centre.B))
            .IsEqualTo((Tokens.SurfaceKey.R, Tokens.SurfaceKey.G, Tokens.SurfaceKey.B));
    }
}
