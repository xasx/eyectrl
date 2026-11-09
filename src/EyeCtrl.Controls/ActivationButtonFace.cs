namespace EyeCtrl.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;

/// <summary>
/// The activation button's face (B2/F1): a static, solid disc 100 DIP across with the toolbar's own
/// look — <see cref="Tokens.BrushSurfaceMedium"/> face and a <see cref="Tokens.BrushBorderSubtle"/>
/// rim — carrying one sign: the little ring menu (<see cref="ActivationIcons.RingMenu"/>). There
/// is no word and no per-mode face; the sign is the same in ManualSelection, AutoSelection and
/// None.
///
/// <para>
/// While the button's dwell runs, an <see cref="Tokens.BrushAccent"/> fill rises from the bottom
/// inside the disc, and the sign switches colour across the fill line so both halves stay
/// readable.
/// </para>
///
/// <para>
/// <b>Two content copies, each clipped to its side of the fill line.</b> The upper copy is drawn in
/// <see cref="Tokens.BrushTextPrimary"/> over the face; the lower one in the face colour over the
/// accent. Draw them once and clip, never recolour mid-flight: the fill line can sit inside the
/// sign, and a single recoloured copy would show the wrong colour on one side of the line.
/// </para>
///
/// <para>
/// <b>The disc never grows or moves.</b> Progress only moves the fill's top edge and the two clips;
/// the hit area stays the offer engine's 150 px radius, which is larger than the drawn disc. The
/// paused look dims the fill alone (<see cref="DwellPausedCue"/>), leaving the sign crisp and
/// readable while the value is held.
/// </para>
///
/// <para>
/// Framework-only (no window, no ViewModel): the render and contrast gates construct this directly
/// and draw it through a <c>RenderTargetBitmap</c>, and the activation window only hosts it.
/// </para>
/// </summary>
public sealed class ActivationButtonFace : Grid
{
    /// <summary>The drawn diameter in DIP: the toolbar's shared button size, 100 DIP across.</summary>
    public const double Diameter = 2 * LayoutConstants.SharedButtonRadius;

    /// <summary>Rim width in DIP, the toolbar button's own 2 DIP edge.</summary>
    public const double RimThickness = 2;

    /// <summary>
    /// The fill stops this far inside the rim's inner edge, in DIP. The face-coloured band it leaves
    /// keeps a visible boundary between the accent and the rim once the disc fills (F3): in the
    /// Contrast theme the rim is white, so without the band a white page would meet the cyan accent
    /// through a white rim as the fill rose.
    /// </summary>
    public const double FillInset = 2;

    /// <summary>
    /// The sign's ink keeps this much clear of the content clip's edge, in DIP (F1). The face
    /// derives no text room any more; this is the clearance the rendered-extent gate enforces
    /// against the same r46 clip that cuts both content copies.
    /// </summary>
    public const double ContentInkClearance = 2;

    private readonly Canvas _upperIcon;
    private readonly Canvas _lowerIcon;
    private readonly Grid _upperContent;
    private readonly Grid _lowerContent;

    public ActivationButtonFace()
    {
        Width = Diameter;
        Height = Diameter;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        Face = new Ellipse
        {
            Width = Diameter,
            Height = Diameter,
            Fill = Tokens.BrushSurfaceMedium,
            Stroke = Tokens.BrushBorderSubtle,
            StrokeThickness = RimThickness,
        };
        Children.Add(Face);

        Fill = new Rectangle
        {
            Width = Diameter,
            Height = 0,
            Fill = Tokens.BrushAccent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        _upperIcon = NewIconCanvas();
        _lowerIcon = NewIconCanvas();
        _upperContent = NewContentCopy(_upperIcon);
        _lowerContent = NewContentCopy(_lowerIcon);

        var clipped = new Grid
        {
            Width = Diameter,
            Height = Diameter,
            // The fill lives INSIDE the rim, with a face-coloured band between them (FillInset), so
            // the disc keeps a visible edge as the accent rises — including the Contrast theme,
            // whose white rim would otherwise meet the cyan fill on a white page.
            Clip = new EllipseGeometry(
                new Point(Diameter / 2, Diameter / 2),
                (Diameter / 2) - RimThickness - FillInset,
                (Diameter / 2) - RimThickness - FillInset),
        };
        clipped.Children.Add(Fill);
        clipped.Children.Add(_upperContent);
        clipped.Children.Add(_lowerContent);
        Children.Add(clipped);

        (UpperIconStroke, UpperIconFill) =
            PopulateIcon(_upperIcon, ActivationIcons.RingMenu, Tokens.BrushTextPrimary);
        (LowerIconStroke, LowerIconFill) =
            PopulateIcon(_lowerIcon, ActivationIcons.RingMenu, Tokens.BrushSurfaceMedium);

        UpdateProgress(0);
    }

    /// <summary>The disc's face and rim.</summary>
    public Ellipse Face { get; }

    /// <summary>The accent fill, bottom-aligned and clipped to the disc.</summary>
    public Rectangle Fill { get; }

    /// <summary>The copy of the sign drawn above the fill line (text colour).</summary>
    public Grid UpperContent => _upperContent;

    /// <summary>The copy of the sign drawn over the fill (face colour).</summary>
    public Grid LowerContent => _lowerContent;

    /// <summary>The sign's host in the upper copy, for the rendered-pixel gates.</summary>
    public Canvas UpperIconCanvas => _upperIcon;

    /// <summary>The sign's outlined layer (the six circles) in the upper copy.</summary>
    public Path? UpperIconStroke { get; }

    /// <summary>The sign's solid layer (the centre dot) in the upper copy.</summary>
    public Path? UpperIconFill { get; }

    /// <summary>The sign's outlined layer in the lower copy.</summary>
    public Path? LowerIconStroke { get; }

    /// <summary>The sign's solid layer in the lower copy.</summary>
    public Path? LowerIconFill { get; }

    /// <summary>The fill's height in DIP (0 at progress 0, <see cref="Diameter"/> at 1).</summary>
    public double FillHeightDip => Fill.Height;

    /// <summary>
    /// Places the fill's top edge at <paramref name="progress"/> × the diameter from the bottom and
    /// moves each content copy's clip to its side of that line. A non-finite value draws nothing.
    /// </summary>
    /// <param name="progress">The dwell progress, clamped to 0–1.</param>
    public void UpdateProgress(double progress)
    {
        if (!double.IsFinite(progress))
            progress = 0.0;
        progress = Math.Clamp(progress, 0.0, 1.0);

        double fillHeight = progress * Diameter;
        Fill.Height = fillHeight;

        // The fill is bottom-aligned, so the line sits fillHeight above the disc's bottom edge.
        double lineFromTop = Diameter - fillHeight;
        _upperContent.Clip = new RectangleGeometry(new Rect(0, 0, Diameter, lineFromTop));
        _lowerContent.Clip = new RectangleGeometry(new Rect(0, lineFromTop, Diameter, fillHeight));
    }

    /// <summary>Dims the fill only while its dwell is held (<see cref="DwellPausedCue"/>); the
    /// sign keeps full opacity so it stays readable over the dimmed accent.</summary>
    /// <param name="paused">True while the engine's pause grace holds the value.</param>
    public void SetPaused(bool paused) => Fill.Opacity = DwellPausedCue.OpacityFor(paused);

    private static Canvas NewIconCanvas() => new()
    {
        // F1: with no word, the sign gets the room a label-less disc gives it — ContainerRatio.Solo
        // (0.58 → 58 DIP). The board's sign is ~56 DIP tall on the 100 DIP disc (ring radius 20,
        // small-circle radius 5.5, 3 stroke; 54 DIP across its vertical extremes); at Solo the
        // glyph draws ~56 DIP tall and stays 18 DIP inside the r46 content clip (minus its 2 DIP
        // ink clearance).
        Width = Diameter * ContainerRatio.Solo,
        Height = Diameter * ContainerRatio.Solo,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Grid NewContentCopy(Canvas icon)
    {
        var copy = new Grid
        {
            Width = Diameter,
            Height = Diameter,
        };
        copy.Children.Add(icon);
        return copy;
    }

    /// <summary>
    /// Replaces <paramref name="canvas"/>'s glyph with <paramref name="icon"/> drawn in
    /// <paramref name="brush"/>, on the same render transform <c>DwellArcButton</c> uses (the
    /// transform scales the stroke with the geometry). Returns both drawn layers.
    /// </summary>
    private static (Path? Stroke, Path? Fill) PopulateIcon(Canvas canvas, IconGeometry icon, Brush brush)
    {
        canvas.Children.Clear();
        double iconSide = canvas.Width;
        double scale = iconSide / ActionIcons.GridSize;

        Path BuildLayer(Geometry geometry, bool filled)
        {
            var path = new Path
            {
                Data = geometry,
                Stroke = filled ? null : brush,
                StrokeThickness = filled ? 0 : ActionIcons.GridStrokeThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = filled ? brush : null,
                RenderTransform = new ScaleTransform(scale, scale),
            };
            return path;
        }

        Path? stroke = null;
        Path? fill = null;
        if (icon.Stroke is not null)
        {
            stroke = BuildLayer(icon.Stroke, filled: false);
            canvas.Children.Add(stroke);
        }

        if (icon.Fill is not null)
        {
            fill = BuildLayer(icon.Fill, filled: true);
            // Solid parts above the outline, matching DwellArcButton's own layer order.
            canvas.Children.Add(fill);
        }

        return (stroke, fill);
    }
}
