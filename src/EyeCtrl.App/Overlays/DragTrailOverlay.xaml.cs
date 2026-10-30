namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using EyeCtrl.App.Services;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class DragTrailOverlay : OverlayWindow, IDragTrailOverlay
{
    private const double TrailThickness = 2;
    private const double SettleRingThickness = 3;

    /// <summary>The grab/release button disc diameter. <c>internal</c> for the reason recorded
    /// on <see cref="IconSize"/>.</summary>
    internal const double ButtonDiameter = 26;

    /// <summary>
    /// The grab/release glyph size, a ratio of the 26px badge that bounds it.
    ///
    /// This const exists for the CENTRING OFFSETS below, which divide it by two. The FontSize
    /// assignment in <see cref="CreateIcon"/> deliberately repeats the expression rather than
    /// reading this const: RawGeometryLiteralTests examines an assignment's own value, and
    /// <c>FontSize = IconSize</c> references no token, so it would be flagged. The two must stay
    /// in step or the glyph stops being centred — and because a comment is the only thing that
    /// can say so at the assignment site, both are <c>internal</c> so
    /// <c>GlyphSizeCouplingTests</c> can assert the equality instead of trusting the comment.
    /// </summary>
    internal const double IconSize = ButtonDiameter * ContainerRatio.Badge;

    /// <summary>
    /// Opacity of the drop disc's fill (C2). The disc is now the hit circle — at least r50 and
    /// centred on the cursor — so an opaque fill would hide the very spot being dropped on. A
    /// faint wash marks the area; the rim and the arc carry the edge.
    /// </summary>
    internal const double DropFillOpacity = 0.18;

    /// <summary>The drop disc's rim width in DIP, straddling the hit radius (DwellArcButton's).</summary>
    private const double DropRimThickness = 2;

    /// <summary>The dark halo ring's width and its gap from the hit radius, as DwellArcButton's.</summary>
    private const double DropRimHaloThickness = 3;
    private const double DropRimHaloOffset = 2.5;

    /// <summary>The progress arc's inset from the rim, in DIP (DwellArcButton.DefaultArcInset).</summary>
    private const double DropArcInset = DwellArcButton.DefaultArcInset;

    private readonly Line _trailLineHalo;
    private readonly Line _trailLine;
    private readonly Ellipse _settleRingHalo;
    private readonly Ellipse _settleRing;
    private readonly TextBlock _grabIcon;
    private readonly TextBlock _releaseIcon;
    private readonly Ellipse _releaseBg;

    // C2: the drop target drawn as its hit circle — wash, halo, rim, and the dwell arc over its halo.
    private readonly Ellipse _dropFill;
    private readonly Ellipse _dropRimHalo;
    private readonly Ellipse _dropRim;
    private readonly Path _dropArcHalo;
    private readonly Path _dropArc;

    public DragTrailOverlay()
    {
        IsClickThrough = true;
        InitializeComponent();

        // This is a Background="Transparent" click-through overlay, so every mark on it draws
        // straight onto desktop content \u2014 the \u00A74.1.3 case. Each accent mark gets a StrokeHalo
        // under-stroke added FIRST (later siblings paint on top). The halos stay at Opacity 1
        // even where the mark dims: dimming both puts the backdrop back into both terms and
        // voids the backdrop-independent edge.
        _trailLineHalo = new Line
        {
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = TrailThickness + 2
        };
        TrailCanvas.Children.Add(_trailLineHalo);

        // Trail-line colour is settings-driven (DragSettings.TrailColor); set in Configure.
        // Default to AccentWarning so the line is visible if Configure is never called.
        _trailLine = new Line
        {
            Stroke = Tokens.BrushAccentWarning,
            StrokeThickness = TrailThickness,
            Opacity = 0.7
        };
        TrailCanvas.Children.Add(_trailLine);

        _settleRingHalo = new Ellipse
        {
            Width = 40, Height = 40,
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = SettleRingThickness + 2,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_settleRingHalo);

        _settleRing = new Ellipse
        {
            Width = 40, Height = 40,
            Stroke = Tokens.BrushAccentSuccess,
            StrokeThickness = SettleRingThickness,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_settleRing);

        _grabIcon = CreateIcon("\u270A", Tokens.BrushAccentWarning);
        _releaseIcon = CreateIcon("\u270B", Tokens.BrushAccentSuccess);

        // The grab icon is a bare glyph on the transparent overlay \u2014 a TextBlock has no stroke
        // to halo, so it takes an outline via a zero-depth StrokeHalo shadow. The release icon
        // needs none: it sits on _releaseBg, an opaque filled disc.
        _grabIcon.Effect = new DropShadowEffect
        {
            Color = Tokens.StrokeHalo,
            BlurRadius = 3,
            ShadowDepth = 0,
            Opacity = 1
        };

        _releaseBg = CreateButtonBg(Tokens.BrushSurfacePrediction, Tokens.BrushAccentSuccess);

        // C2: the drop disc, drawn = hit. Wash first, then the dark halo under the light rim (the
        // DwellArcButton pairing: the edge between them reads on any desktop), then the arc over
        // its own halo (§4.1.3) — later siblings paint on top.
        _dropFill = new Ellipse
        {
            Fill = Tokens.BrushSurfacePrediction,
            Opacity = DropFillOpacity,
            IsHitTestVisible = false
        };
        _dropRimHalo = new Ellipse
        {
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = DropRimHaloThickness,
            IsHitTestVisible = false
        };
        _dropRim = new Ellipse
        {
            Stroke = Tokens.BrushAccentSuccess,
            StrokeThickness = DropRimThickness,
            IsHitTestVisible = false
        };
        _dropArcHalo = new Path
        {
            Stroke = Tokens.BrushStrokeHalo,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _dropArc = new Path
        {
            Stroke = Tokens.BrushAccentSuccess,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_dropFill);
        TrailCanvas.Children.Add(_dropRimHalo);
        TrailCanvas.Children.Add(_dropRim);
        TrailCanvas.Children.Add(_dropArcHalo);
        TrailCanvas.Children.Add(_dropArc);

        TrailCanvas.Children.Add(_releaseBg);
        TrailCanvas.Children.Add(_releaseIcon);
        TrailCanvas.Children.Add(_grabIcon);
    }

    /// <summary>
    /// Applies the user-configurable trail colour (a hex string from DragSettings.TrailColor)
    /// and opacity. Mirrors TargetHighlightWindow.Configure. Falls back to AccentWarning on a
    /// bad hex string. Call after construction (e.g. in OnDragStarted before Show).
    /// </summary>
    public void Configure(string trailColor, double trailOpacity)
    {
        Brush brush;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(trailColor);
            var b = new SolidColorBrush(c);
            b.Freeze();
            brush = b;
        }
        catch
        {
            brush = Tokens.BrushAccentWarning;
        }

        _trailLine.Stroke = brush;
        _trailLine.Opacity = trailOpacity;
        _grabIcon.Foreground = brush;
        // _trailLineHalo deliberately keeps Opacity 1 — see the constructor.
    }

    public void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
        DragReleaseDisc release)
    {
        var originLogical = new Point(DpiHelper.PhysicalToLogicalX(grabOrigin.X), DpiHelper.PhysicalToLogicalY(grabOrigin.Y));
        var cursorLogical = new Point(DpiHelper.PhysicalToLogicalX(cursorPos.X), DpiHelper.PhysicalToLogicalY(cursorPos.Y));

        _trailLine.X1 = _trailLineHalo.X1 = originLogical.X;
        _trailLine.Y1 = _trailLineHalo.Y1 = originLogical.Y;
        _trailLine.X2 = _trailLineHalo.X2 = cursorLogical.X;
        _trailLine.Y2 = _trailLineHalo.Y2 = cursorLogical.Y;

        Canvas.SetLeft(_grabIcon, cursorLogical.X - (IconSize / 2));
        Canvas.SetTop(_grabIcon, cursorLogical.Y - (IconSize / 2));

        DrawDropDisc(release);

        if (settleProgress > 0)
        {
            _settleRing.Visibility = Visibility.Visible;
            _settleRingHalo.Visibility = Visibility.Visible;
            Canvas.SetLeft(_settleRing, cursorLogical.X - 20);
            Canvas.SetTop(_settleRing, cursorLogical.Y - 20);
            Canvas.SetLeft(_settleRingHalo, cursorLogical.X - 20);
            Canvas.SetTop(_settleRingHalo, cursorLogical.Y - 20);

            var circumference = Math.PI * 40;
            var on = settleProgress * circumference / 3;
            _settleRing.StrokeDashArray = new DoubleCollection { on, circumference };

            // StrokeDashArray is in MULTIPLES OF StrokeThickness, not device pixels, so the
            // thicker halo needs the pattern scaled or its dashes come out longer than the
            // accent's and the halo peeks past the ends of each dash.
            var scale = SettleRingThickness / (SettleRingThickness + 2);
            _settleRingHalo.StrokeDashArray = new DoubleCollection { on * scale, circumference * scale };
        }
        else
        {
            _settleRing.Visibility = Visibility.Collapsed;
            _settleRingHalo.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// C2: draws the drop target AS its hit circle — the radius is the detector's live one, so the
    /// disc can never promise an area the dwell does not count — with the release dwell as an arc
    /// from 12 o'clock inside the rim, read exactly like every other gaze button's. While the pause
    /// grace holds the dwell the arc dims (<see cref="DwellPausedCue"/>), like the edge bar. The ✋
    /// badge rides the top of the disc, inside the arc, so it never sits on the ✊ at the cursor.
    /// </summary>
    private void DrawDropDisc(DragReleaseDisc release)
    {
        var centre = new Point(
            DpiHelper.PhysicalToLogicalX(release.Center.X), DpiHelper.PhysicalToLogicalY(release.Center.Y));
        var radius = DpiHelper.PhysicalToLogicalX(release.RadiusPhysical);
        var diameter = radius * 2;

        PlaceCircle(_dropFill, centre, radius);
        PlaceCircle(_dropRim, centre, radius);
        PlaceCircle(_dropRimHalo, centre, radius + DropRimHaloOffset);

        var arcRadius = radius - DropArcInset;
        var arcThickness = Math.Max(diameter * DwellArcButton.ArcThicknessRatio, DwellArcButton.MinArcThickness);
        var progress = Math.Clamp(release.Progress, 0, 1);
        if (progress > 0)
        {
            _dropArc.Data = ArcGeometry(centre, arcRadius, progress);
            _dropArcHalo.Data = _dropArc.Data;
            _dropArc.StrokeThickness = arcThickness;
            _dropArcHalo.StrokeThickness = arcThickness + 2;
            _dropArc.Visibility = Visibility.Visible;
            _dropArcHalo.Visibility = Visibility.Visible;
        }
        else
        {
            _dropArc.Visibility = Visibility.Collapsed;
            _dropArcHalo.Visibility = Visibility.Collapsed;
        }
        // Only the accent dims; the halo stays at 1 — it is the contrast the dimmed arc reads against.
        _dropArc.Opacity = DwellPausedCue.OpacityFor(release.Paused);

        var badgeInset = arcRadius - (arcThickness / 2) - (ButtonDiameter / 2);
        PositionButton(_releaseBg, _releaseIcon, new Point(centre.X, centre.Y - badgeInset));
    }

    private static void PlaceCircle(Ellipse circle, Point centre, double radius)
    {
        circle.Width = radius * 2;
        circle.Height = radius * 2;
        Canvas.SetLeft(circle, centre.X - radius);
        Canvas.SetTop(circle, centre.Y - radius);
    }

    /// <summary>A clockwise arc from 12 o'clock covering <paramref name="progress"/> of the circle.</summary>
    private static Geometry ArcGeometry(Point centre, double arcRadius, double progress)
    {
        if (progress >= 1)
            return new EllipseGeometry(centre, arcRadius, arcRadius);

        var top = new Point(centre.X, centre.Y - arcRadius);
        var arc = new ArcSegment
        {
            Point = top,
            Size = new Size(arcRadius, arcRadius),
            SweepDirection = SweepDirection.Clockwise,
        };
        DwellArcButton.UpdateArc(arc, centre, arcRadius, progress);
        var figure = new PathFigure { StartPoint = top, IsClosed = false };
        figure.Segments.Add(arc);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Test seams (C2): the drop disc's rim (drawn = hit), its arc, and the ✋ badge.</summary>
    internal Ellipse DropRimForTest => _dropRim;
    internal Path DropArcForTest => _dropArc;
    internal Ellipse ReleaseBadgeForTest => _releaseBg;

    public void Clear()
    {
        _trailLine.X1 = 0; _trailLine.Y1 = 0;
        _trailLine.X2 = 0; _trailLine.Y2 = 0;
        _trailLineHalo.X1 = 0; _trailLineHalo.Y1 = 0;
        _trailLineHalo.X2 = 0; _trailLineHalo.Y2 = 0;
        _settleRing.Visibility = Visibility.Collapsed;
        _settleRingHalo.Visibility = Visibility.Collapsed;
    }

    private static TextBlock CreateIcon(string glyph, Brush foreground)
    {
        return new TextBlock
        {
            Text = glyph,
            // Must equal IconSize — see its doc comment for why the expression is repeated.
            FontSize = ButtonDiameter * ContainerRatio.Badge,
            Foreground = foreground,
            IsHitTestVisible = false
        };
    }

    private static Ellipse CreateButtonBg(Brush fill, Brush stroke)
    {
        return new Ellipse
        {
            Width = ButtonDiameter, Height = ButtonDiameter,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
    }

    private static void PositionButton(Ellipse bg, TextBlock icon, Point logicalPos)
    {
        Canvas.SetLeft(bg, logicalPos.X - (ButtonDiameter / 2));
        Canvas.SetTop(bg, logicalPos.Y - (ButtonDiameter / 2));
        // Half the FONT SIZE, not half DesiredSize: DesiredSize.Height is 1.33x the font
        // size, so box-centring would shift the glyph ~2.6px from where it sits today.
        // DesiredSize is also (0,0) here — this is static and the TextBlock is unmeasured.
        Canvas.SetLeft(icon, logicalPos.X - (IconSize / 2));
        Canvas.SetTop(icon, logicalPos.Y - (IconSize / 2));
    }
}
