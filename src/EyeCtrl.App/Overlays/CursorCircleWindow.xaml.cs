namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;

public partial class CursorCircleWindow : OverlayWindow
{
    // UI Constants
    private const int MinimumCircleSize = 10; // Minimum allowed circle size in pixels

    public CursorCircleWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep the cursor circle out of the magnifier/preview capture
        InitializeComponent();
    }

    public void UpdatePosition(PhysicalPoint point)
    {
        Left = DpiHelper.PhysicalToLogicalX(point.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(point.Y) - Height / 2;
    }

    public void UpdateAppearance(CursorSettings settings)
    {
        var size = Math.Max(MinimumCircleSize, settings.CircleSize);

        // Core owns the geometry; this method owns only WPF. The palette colours are passed
        // in as hex because EyeCtrl.Core cannot reference EyeCtrl.Controls.
        var stack = CursorRingStacks.For(
            settings.RingStyle, size, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, ToHex(Tokens.StrokeHalo), ToHex(Tokens.TextPrimary));

        Apply(CircleHalo, stack.Outer, Tokens.BrushStrokeHalo);
        Apply(Circle, stack.Core, Tokens.BrushAccent);

        Width = size;
        Height = size;
        Circle.Width = size;
        Circle.Height = size;
        CircleHalo.Width = size;
        CircleHalo.Height = size;
    }

    /// <summary>
    /// Paints one band onto one ellipse. The brush is built and frozen here, once per
    /// settings change and never per frame — see the note in HaloZOrderTests about why this
    /// file is exempt from ActivationButton_NeverAllocatesABrush.
    /// </summary>
    /// <param name="target">The ellipse to paint.</param>
    /// <param name="band">The band's colour, width, opacity and dash geometry.</param>
    /// <param name="fallback">Used when the band's hex is unparseable, which only the
    /// user-supplied mark colour can be.</param>
    private static void Apply(Ellipse target, RingBand band, SolidColorBrush fallback)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(band.Hex));
            brush.Freeze();
            target.Stroke = brush;
        }
        catch
        {
            target.Stroke = fallback;
        }

        target.StrokeThickness = band.Thickness;
        target.Opacity = band.Opacity;

        if (band.DashPeriod > 0)
        {
            // StrokeDashArray and StrokeDashOffset are both in MULTIPLES OF StrokeThickness,
            // not device pixels (same as DragTrailOverlay). Dash and gap are equal, and the
            // offset is one dash — half a cycle — which interleaves the two bands.
            var units = band.DashPeriod / band.Thickness;
            target.StrokeDashArray = new DoubleCollection { units, units };
            target.StrokeDashOffset = band.DashOffset / band.Thickness;
        }
        else
        {
            target.StrokeDashArray = new DoubleCollection();
            target.StrokeDashOffset = 0;
        }
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
