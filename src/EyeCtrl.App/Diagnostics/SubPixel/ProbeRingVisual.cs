namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Serilog;

/// <summary>
/// The sub-pixel probe's own copy of the production cursor ring (build-plan C4): the visual both
/// A/B conditions are drawn with, sized and painted from the same <see cref="CursorRingStacks"/>
/// stack the real ring uses.
///
/// <para>
/// It is a copy rather than a reuse of <see cref="CursorCircleWindow"/> because the probe must not
/// touch the production ring (the plan's boundary rule), and it is a <i>faithful</i> copy: the band
/// application below mirrors <c>CursorCircleWindow.Apply</c> step for step, including the size
/// floor and the dash units being multiples of the stroke thickness. The parity test
/// (<c>ProbeRingParityTests</c>) pins the two against each other for every style and look, so a
/// drift on either side fails a test rather than shipping.
/// </para>
///
/// <para>
/// <b>The one A/B difference.</b> A caller passes the same centre for both conditions and only
/// <paramref name="snap"/> differs, so the only difference in the whole subtree is the one rounding
/// call in <see cref="SubPixelPlacement.TopLeft"/>. Three WPF behaviours could silently re-quantize
/// the position anyway and are deliberately switched off: <see cref="UIElement.SnapsToDevicePixels"/>
/// and <see cref="FrameworkElement.UseLayoutRounding"/> (both explicitly false) and
/// <see cref="UIElement.CacheMode"/> (never set — a <c>BitmapCache</c> quantizes the rendered ring
/// to whole pixels). Positioning goes through a <see cref="TranslateTransform"/>, never
/// <c>Canvas.Left</c>/<c>Top</c>, because layout offsets are exactly what layout rounding would
/// quantize.
/// </para>
///
/// <para>
/// <b>Scale is injected, never ambient.</b> The constructor takes the DIP→device-pixel scale and
/// the class never reads <c>DpiHelper</c>: that is a process static which defaults to 1.0, so a
/// probe reading it could not be rendered at 192 DPI by a headless test at all. The raster test
/// (R1) renders the same visual at 96 and 192 DPI through this argument.
/// </para>
///
/// <para>
/// <b>Look opacity is the container's.</b> Production applies the quiet (0.6) and pinned (1.0)
/// looks as <c>Window.Opacity</c>, above both bands; the probe applies them as this container's
/// <see cref="UIElement.Opacity"/>. Applying the look per band instead changes what the overlap
/// region blends to, which is why the parity test compares the container opacity separately from
/// the band opacities.
/// </para>
/// </summary>
internal sealed class ProbeRingVisual : Canvas
{
    /// <summary>The quiet look's container opacity — production's FreeLook window opacity.</summary>
    internal const double QuietLookOpacity = CursorCircleWindow.FreeLookQuietOpacity;

    /// <summary>The pinned look's container opacity — production's pinned window opacity.</summary>
    internal const double PinnedLookOpacity = 1.0;

    /// <summary>Production's floor on the ring diameter (<c>CursorCircleWindow.MinimumCircleSize</c>).</summary>
    private const double MinimumSizeDip = 10.0;

    private readonly double _scale;
    private readonly TranslateTransform _position = new();

    /// <summary>
    /// Builds the ring for <paramref name="scale"/> device pixels per DIP, in the look's container
    /// opacity.
    /// </summary>
    /// <param name="scale">Device pixels per DIP; the only scale this visual ever uses.</param>
    /// <param name="settings">The ring settings whose style, size, colours and bands are painted.</param>
    /// <param name="lookOpacity">The look's container opacity; see <see cref="QuietLookOpacity"/> and <see cref="PinnedLookOpacity"/>.</param>
    internal ProbeRingVisual(double scale, CursorSettings settings, double lookOpacity)
    {
        if (scale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive.");
        if (lookOpacity is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(lookOpacity), lookOpacity, "Look opacity must be in [0, 1].");

        _scale = scale;
        SizeDip = Math.Max(MinimumSizeDip, settings.CircleSize);

        // Core owns the geometry; this class owns only WPF. The palette colours are passed in as
        // hex because EyeCtrl.Core cannot reference EyeCtrl.Controls. Identical to
        // CursorCircleWindow.UpdateAppearance.
        var stack = CursorRingStacks.For(
            settings.RingStyle, SizeDip, settings.CircleThickness, settings.CircleOpacity,
            settings.CircleColor, ToHex(Tokens.StrokeHalo), ToHex(Tokens.TextPrimary));

        CircleHalo = new Ellipse();
        Circle = new Ellipse();
        Apply(CircleHalo, stack.Outer, Tokens.BrushStrokeHalo);
        Apply(Circle, stack.Core, Tokens.BrushAccent);

        Width = SizeDip;
        Height = SizeDip;
        CircleHalo.Width = SizeDip;
        CircleHalo.Height = SizeDip;
        Circle.Width = SizeDip;
        Circle.Height = SizeDip;

        Children.Add(CircleHalo);
        Children.Add(Circle);

        RenderTransform = _position;

        UseLayoutRounding = false;
        SnapsToDevicePixels = false;

        Opacity = lookOpacity;
    }

    /// <summary>The drawn ring diameter in DIPs, after production's size floor.</summary>
    internal double SizeDip { get; }

    /// <summary>The wide outer band's ellipse, painted first.</summary>
    internal Ellipse CircleHalo { get; }

    /// <summary>The core band's ellipse, painted on top.</summary>
    internal Ellipse Circle { get; }

    /// <summary>
    /// Draws the ring centred on <paramref name="centrePhys"/> (physical device pixels) as
    /// condition A (<paramref name="snap"/> <c>true</c>) or condition B (<c>false</c>).
    /// <para>
    /// The physical top-left comes from <see cref="SubPixelPlacement.TopLeft"/> — the one placement
    /// rule the two conditions differ by — and is converted to DIPs with the injected scale.
    /// </para>
    /// </summary>
    /// <param name="centrePhys">The ring's exact centre, in physical device pixels.</param>
    /// <param name="snap">Whether the top-left snaps to whole device pixels (condition A).</param>
    internal void Place(PhysicalPoint centrePhys, bool snap)
    {
        var topLeftPhys = SubPixelPlacement.TopLeft(centrePhys, SizeDip, _scale, snap);
        _position.X = topLeftPhys.X / _scale;
        _position.Y = topLeftPhys.Y / _scale;
    }

    /// <summary>
    /// Draws a still park — the static block's motion — honouring SP-1's documented contract for
    /// the non-owner case.
    /// <para>
    /// <see cref="SubPixelAbStaticMotion.Position"/> is the ring <b>centre</b> with a fractional
    /// part in [0.3, 0.7], but condition A rounds the <b>top-left</b>. When the physical half-size
    /// is a whole number of pixels (the owner's 250 DIP at scale 2.0) the two coincide and this is
    /// identical to <see cref="Place"/>. When it is not, the scheduled fraction is re-anchored onto
    /// the exact top-left — <c>topLeft = ⌊scheduledCentre − half⌋ + fraction</c> — so A and B stay
    /// 0.3–0.5 px apart per axis instead of collapsing together. Without the re-anchor a centre
    /// fraction of 0.6 at a half-pixel offset (250 DIP at scale 1.5) would put the top-left on a
    /// 0.1 fraction, and the trial would test nothing.
    /// </para>
    /// </summary>
    /// <param name="scheduledCentrePhys">The scheduled park centre, in physical pixels; its fractional part is in [0.3, 0.7] per the schedule.</param>
    /// <param name="snap">Whether the top-left snaps to whole device pixels (condition A).</param>
    internal void PlaceStaticPark(PhysicalPoint scheduledCentrePhys, bool snap)
    {
        double halfPhys = SizeDip * _scale / 2.0;
        double fractionX = scheduledCentrePhys.X - Math.Floor(scheduledCentrePhys.X);
        double fractionY = scheduledCentrePhys.Y - Math.Floor(scheduledCentrePhys.Y);
        double topLeftX = Math.Floor(scheduledCentrePhys.X - halfPhys) + fractionX;
        double topLeftY = Math.Floor(scheduledCentrePhys.Y - halfPhys) + fractionY;
        Place(new PhysicalPoint(topLeftX + halfPhys, topLeftY + halfPhys), snap);
    }

    /// <summary>
    /// Paints one band onto one ellipse. Identical to <c>CursorCircleWindow.Apply</c>: the brush is
    /// built and frozen once per construction, WPF dash units are multiples of the stroke
    /// thickness, and an unparseable user colour falls back to the theme brush.
    /// </summary>
    private static void Apply(Ellipse target, RingBand band, SolidColorBrush fallback)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(band.Hex));
            brush.Freeze();
            target.Stroke = brush;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not parse probe ring colour {Hex}; using the theme brush", band.Hex);
            target.Stroke = fallback;
        }

        target.StrokeThickness = band.Thickness;
        target.Opacity = band.Opacity;

        if (band.DashPeriod > 0)
        {
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

    private static string ToHex(Color c) =>
        "#" + c.R.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.G.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
            + c.B.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
}
