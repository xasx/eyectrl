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
/// The activation button's face (B2): a static, solid disc 100 DIP across with the toolbar's own
/// look — <see cref="Tokens.BrushSurfaceMedium"/> face and a <see cref="Tokens.BrushBorderSubtle"/>
/// rim — carrying an icon above its label. While the button's dwell runs, an
/// <see cref="Tokens.BrushAccent"/> fill rises from the bottom inside the disc, and the content
/// switches colour across the fill line so both halves stay readable.
///
/// <para>
/// <b>Two content copies, each clipped to its side of the fill line.</b> The upper copy is drawn in
/// <see cref="Tokens.BrushTextPrimary"/> over the face; the lower one in the face colour over the
/// accent. Draw them once and clip, never recolour mid-flight: the fill line can sit inside a glyph,
/// and a single recoloured copy would show the wrong colour on one side of the line.
/// </para>
///
/// <para>
/// <b>The disc never grows or moves.</b> Progress only moves the fill's top edge and the two clips;
/// the hit area stays the offer engine's 150 px radius, which is larger than the drawn disc. The
/// paused look dims the fill alone (<see cref="DwellPausedCue"/>), leaving the content crisp and
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

    /// <summary>The face's ink keeps this much clear of the disc's inner edge, in DIP.</summary>
    public const double LabelInkClearance = 2;

    /// <summary>The icon/label gap as a fraction of the diameter (the toolbar's own 0.05).</summary>
    private const double StackGapRatio = 0.05;

    /// <summary>The label's single-line box as a multiple of its font size (the toolbar's 1.1).</summary>
    private const double LabelLineHeightRatio = 1.1;

    private readonly Canvas _upperIcon;
    private readonly Canvas _lowerIcon;
    private readonly TextBlock _upperLabel;
    private readonly TextBlock _lowerLabel;
    private readonly Grid _upperContent;
    private readonly Grid _lowerContent;

    private ActivationButtonContent _content;

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
        _upperLabel = NewLabel(Tokens.BrushTextPrimary);
        _lowerLabel = NewLabel(Tokens.BrushSurfaceMedium);
        _upperContent = NewContentCopy(_upperIcon, _upperLabel);
        _lowerContent = NewContentCopy(_lowerIcon, _lowerLabel);

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

        SetContent(ActivationButtonContent.Menu, string.Empty);
        UpdateProgress(0);
    }

    /// <summary>The disc's face and rim.</summary>
    public Ellipse Face { get; }

    /// <summary>The accent fill, bottom-aligned and clipped to the disc.</summary>
    public Rectangle Fill { get; }

    /// <summary>The copy of the icon and label drawn above the fill line (text colour).</summary>
    public Grid UpperContent => _upperContent;

    /// <summary>The copy of the icon and label drawn over the fill (face colour).</summary>
    public Grid LowerContent => _lowerContent;

    /// <summary>The label drawn above the fill line; its width is the natural text width
    /// (<c>NoWrap</c>), which is what the fit gate measures.</summary>
    public TextBlock UpperLabel => _upperLabel;

    /// <summary>The label drawn over the fill, for the colour-switch gates.</summary>
    public TextBlock LowerLabel => _lowerLabel;

    /// <summary>The icon's host in the upper copy, for the rendered-pixel gates.</summary>
    public Canvas UpperIconCanvas => _upperIcon;

    /// <summary>The icon's outlined layer in the upper copy, or null when the glyph has none.</summary>
    public Path? UpperIconStroke { get; private set; }

    /// <summary>The icon's solid layer in the upper copy, or null when the glyph has none.</summary>
    public Path? UpperIconFill { get; private set; }

    /// <summary>The icon's outlined layer in the lower copy, or null when the glyph has none.</summary>
    public Path? LowerIconStroke { get; private set; }

    /// <summary>The icon's solid layer in the lower copy, or null when the glyph has none.</summary>
    public Path? LowerIconFill { get; private set; }

    /// <summary>What the face currently names.</summary>
    public ActivationButtonContent Content => _content;

    /// <summary>The fill's height in DIP (0 at progress 0, <see cref="Diameter"/> at 1).</summary>
    public double FillHeightDip => Fill.Height;

    /// <summary>
    /// The one-line horizontal room the label has, in DIP, measured after layout: the chord of the
    /// content clip (the rim's inner edge less <see cref="FillInset"/>, where both content copies are
    /// cut) at the label's bottom edge, less <see cref="LabelInkClearance"/> on both sides. Consumed by
    /// the <c>A76</c> fit gate, which measures the label's natural width.
    /// </summary>
    public double LabelRoomDip
    {
        get
        {
            var labelBottom = _upperLabel.TranslatePoint(new Point(0, _upperLabel.ActualHeight), this).Y;
            double belowCentre = Math.Max(0, labelBottom - (Diameter / 2));
            // The content copies sit in the same inset clip as the fill (B2 review-02 N1), so the
            // room ends there, not at the rim.
            double innerRadius = (Diameter / 2) - RimThickness - FillInset;
            if (belowCentre >= innerRadius)
                return 0;
            double chord = 2 * Math.Sqrt((innerRadius * innerRadius) - (belowCentre * belowCentre));
            return Math.Max(0, chord - (2 * LabelInkClearance));
        }
    }

    /// <summary>
    /// Draws the named action: the magnifier glyph and word, or the menu glyph and word. Safe before
    /// the first layout; both content copies take the same glyph and text.
    /// </summary>
    /// <param name="content">Which action completion opens.</param>
    /// <param name="label">The localized word under the glyph („Lupe"/„Menü" or "Magnifier"/"Menu").</param>
    public void SetContent(ActivationButtonContent content, string label)
    {
        _content = content;
        var icon = content == ActivationButtonContent.Magnifier
            ? ActivationIcons.Magnifier
            : ActionIcons.For(ActionIcon.More);

        var upper = PopulateIcon(_upperIcon, icon, Tokens.BrushTextPrimary);
        UpperIconStroke = upper.Stroke;
        UpperIconFill = upper.Fill;
        var lower = PopulateIcon(_lowerIcon, icon, Tokens.BrushSurfaceMedium);
        LowerIconStroke = lower.Stroke;
        LowerIconFill = lower.Fill;

        _upperLabel.Text = label;
        _lowerLabel.Text = label;
    }

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
    /// content keeps full opacity so it stays readable over the dimmed accent.</summary>
    /// <param name="paused">True while the engine's pause grace holds the value.</param>
    public void SetPaused(bool paused) => Fill.Opacity = DwellPausedCue.OpacityFor(paused);

    private static Canvas NewIconCanvas() => new()
    {
        // F2: the owner's board draws a glyph about 38 DIP across on the 100 DIP disc. At
        // ContainerRatio.Large (0.40 → 40 DIP) the lens alone is ~30 DIP (the board's lens is
        // 2 x 13 + 3.6 ≈ 29.6) and the whole lens + handle ~36 DIP; Medium (32 DIP) drew only
        // ~24 DIP of lens, two thirds of the pick.
        Width = Diameter * ContainerRatio.Large,
        Height = Diameter * ContainerRatio.Large,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static TextBlock NewLabel(Brush foreground) => new()
    {
        Foreground = foreground,
        FontSize = Tokens.FontSizeBodyLarge,
        FontWeight = Tokens.FontWeightMedium,
        FontFamily = Tokens.FontStandard,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static Grid NewContentCopy(Canvas icon, TextBlock label)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(icon);
        label.Margin = new Thickness(0, Diameter * StackGapRatio, 0, 0);
        stack.Children.Add(label);

        var copy = new Grid
        {
            Width = Diameter,
            Height = Diameter,
        };
        copy.Children.Add(stack);
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
