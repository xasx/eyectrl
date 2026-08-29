namespace EyeCtrl.Controls;

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;

public class DwellArcButton
{
    public const double DefaultArcInset = 4;

    /// <summary>
    /// Progress-arc width as a fraction of the button diameter.
    ///
    /// <para>
    /// 0.04 is not a new choice: it is the weight the arc already had, recovered. The old
    /// <c>DefaultArcThickness</c> was a flat 4 DIP, picked when every caller was 100 DIP across.
    /// Plan 1 grew the action-menu button to 140 DIP on 2026-08-26 and left the constant alone,
    /// which thinned that ring's arc to 2.9% of its button while the other callers kept 4%.
    /// Nothing failed, because nothing pinned the number.
    /// </para>
    ///
    /// <para>
    /// Expressed as a fraction so the next resize cannot repeat that silently, and pinned by
    /// <c>DwellArcButtonArcWeightTests</c>.
    /// </para>
    /// </summary>
    public const double ArcThicknessRatio = 0.04;

    /// <summary>
    /// The narrowest the progress arc may be drawn, whatever the fraction works out to.
    ///
    /// <para>
    /// Same reasoning as <see cref="BadgeDiameterThreshold"/>, applied to stroke rather than
    /// glyph: a small button needs a LARGER fraction of itself, not a proportional one. Without
    /// this floor the edge bar's 40 DIP drag buttons would take 40 x 0.04 = 1.6 DIP of arc, on
    /// the affordance that CANCELS A DRAG for a user whose only input channel is an eye tracker.
    /// At 4 it is the exact width those buttons and the 100 DIP hide button already drew, so
    /// neither moves.
    /// </para>
    /// </summary>
    public const double MinArcThickness = 4;

    /// <summary>
    /// Diameter below which a glyph needs <see cref="ContainerRatio.Badge"/>'s larger fraction
    /// to stay legible. The 40px edge-bar drag buttons (<c>Drag_cancel</c>, <c>Drag_back</c>,
    /// which take <c>EdgeButtonDefinition</c>'s default <c>UIConstants.DragButtonRadius</c> of
    /// 20) would render at 40 × 0.23 = 9.2px under <see cref="ContainerRatio.Small"/> — below
    /// the 12px accessibility floor, on the affordance that cancels a drag for a user whose
    /// only input channel is an eye tracker. Above the threshold the larger callers keep Small:
    /// the 100px backdrop hide button and ManualSelection side buttons
    /// (<c>UIConstants.ButtonRadius</c> = 50). The 140px action-menu ring
    /// (<c>ActionMenuGeometry.ButtonRadius</c> / <c>LayoutConstants.ActionMenuButtonRadius</c> =
    /// 70, split from the shared radius on 2026-08-26) no longer reaches this branch at all: it
    /// passes an <see cref="EyeCtrl.Controls.Icons.IconGeometry"/> instead of a glyph, and that
    /// path sizes itself by <see cref="ContainerRatio.Dominant"/>.
    ///
    /// Named rather than written inline so RawGeometryLiteralTests's bare-number rule does not
    /// flag the <c>FontSize</c> expression that reads it.
    ///
    /// Both arms are asserted by <c>DwellArcButtonGlyphSizeTests</c>. They have to be: the
    /// expression is token-backed whichever ratio each arm picks, so that guard passes if the arms
    /// are swapped or the branch collapses onto one ratio.
    /// </summary>
    private const double BadgeDiameterThreshold = 64;

    /// <summary>
    /// Gap between a button's icon and its label, as a fraction of the diameter. 0.05 of 140 is
    /// 7 DIP. Named rather than written inline so RawGeometryLiteralTests's bare-number rule does
    /// not flag the expression that reads it.
    /// </summary>
    private const double LabelGapRatio = 0.05;

    /// <summary>
    /// The label's line box as a multiple of its font size. 1.1 is the ordinary single-line
    /// leading for this face; it exists so the icon/label stack can be centred BEFORE the
    /// TextBlock has been measured, which is what lets the icon and its label be positioned in
    /// one pass rather than two.
    /// </summary>
    private const double LabelLineHeightRatio = 1.1;

    /// <summary>Rim width in DIP, straddling the button radius (so it spans radius +/- 1).</summary>
    private const double RimThickness = 2;

    /// <summary>Halo ring width in DIP.</summary>
    private const double RimHaloThickness = 3;

    /// <summary>
    /// Gap in DIP from the button radius to the halo ring's centre line. 2.5 places the halo's
    /// painted band at radius+1 to radius+4 — starting exactly where the 2 DIP rim ends, so the
    /// two never overlap and the light/dark edge lands on the silhouette.
    /// </summary>
    private const double RimHaloOffset = 2.5;

    public Ellipse Background { get; }

    /// <summary>
    /// A dark ring stroked just outside the button edge. Pairs with the light
    /// <see cref="Background"/> rim so the visible feature is the edge between them, which holds
    /// on any desktop content — the rim alone is 2.72:1 on the fill and the fill alone can reach
    /// 1.31:1 on a near-black window.
    /// </summary>
    public Ellipse RimHalo { get; }
    public Path HaloPath { get; }
    public Path ProgressPath { get; }
    public ArcSegment HaloArc { get; }
    public ArcSegment Arc { get; }
    public TextBlock? Symbol { get; }

    /// <summary>The icon's outlined layer, or null when this button renders a glyph or nothing.</summary>
    public Path? IconStroke { get; }

    /// <summary>The icon's solid layer, or null when the icon has no filled paths.</summary>
    public Path? IconFill { get; }

    /// <summary>
    /// The button's name, drawn under the icon, or null when this button is icon-only. Present
    /// only where a target has to be READ rather than recognised — the action-menu ring while
    /// Help has names switched on, and the Help and Close targets themselves.
    /// </summary>
    public TextBlock? Label { get; }

    private readonly Point _center;
    private readonly double _arcRadius;

    private DwellArcButton(Ellipse background, Ellipse rimHalo, Path haloPath, Path progressPath, ArcSegment haloArc, ArcSegment arc, TextBlock? symbol, Path? iconStroke, Path? iconFill, TextBlock? label, Point center, double arcRadius)
    {
        Background = background;
        RimHalo = rimHalo;
        HaloPath = haloPath;
        ProgressPath = progressPath;
        HaloArc = haloArc;
        Arc = arc;
        Symbol = symbol;
        IconStroke = iconStroke;
        IconFill = iconFill;
        Label = label;
        _center = center;
        _arcRadius = arcRadius;
    }

    // `fontSize` is nullable rather than defaulted because a defaulted parameter cannot
    // reference `radius`; the size is resolved from the diameter at the assignment site below.
    // The old `DefaultSymbolFontSize = 22` is gone — 22 was 0.22 of the 100px diameter the
    // action-menu and hide-button callers use, i.e. ContainerRatio.Small.
    //
    // It is also an unguarded hole in RawGeometryLiteralTests, recorded here rather than left to
    // be rediscovered: that guard matches `FontSize = <value>` assignments, so a caller writing
    // `fontSize: 22` passes a raw size in through an ARGUMENT and is never seen. Accepted, not
    // overlooked — all three callers (ActionMenuWindow, EdgeButtonBarWindow, GlassBackdropWindow)
    // omit the parameter entirely and take the ratio, which is the point of it defaulting to null.
    // If a caller ever does pass it, pass a ContainerRatio expression, and widen the guard to
    // named arguments rather than treating this comment as permission.
    public static DwellArcButton Create(Point center, double radius, string? symbol,
        Brush? fill = null, Brush? stroke = null, Brush? arcBrush = null, Brush? symbolBrush = null,
        double? arcThickness = null, double arcInset = DefaultArcInset,
        double? fontSize = null,
        IconGeometry? icon = null,
        string? label = null)
    {
        var fillBrush = fill ?? Tokens.BrushSurfaceMedium;
        var strokeBrush = stroke ?? Tokens.BrushBorderSubtle;
        var arcBrushActual = arcBrush ?? Tokens.BrushAccent;
        var symbolBrushActual = symbolBrush ?? Tokens.BrushTextPrimary;
        var dia = radius * 2;
        var arcRad = radius - arcInset;
        // Resolved here rather than in the parameter list because a default argument cannot
        // reference `radius` - the same reason `fontSize` is nullable. See ArcThicknessRatio.
        var arcThicknessActual = arcThickness ?? Math.Max(dia * ArcThicknessRatio, MinArcThickness);

        var rimHalo = new Ellipse
        {
            Width = dia + RimHaloOffset * 2,
            Height = dia + RimHaloOffset * 2,
            Fill = null,
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = RimHaloThickness,
        };
        Canvas.SetLeft(rimHalo, center.X - radius - RimHaloOffset);
        Canvas.SetTop(rimHalo, center.Y - radius - RimHaloOffset);

        var bg = new Ellipse
        {
            Width = dia,
            Height = dia,
            Fill = fillBrush,
            Stroke = strokeBrush,
            StrokeThickness = RimThickness
        };
        Canvas.SetLeft(bg, center.X - radius);
        Canvas.SetTop(bg, center.Y - radius);

        // Halo under-stroke (spec §4.1.3). ActionMenu and EdgeButtonBar are both
        // transparent overlays, so this is where their arcs get a contrast
        // guarantee - neither window file needs to change.
        var haloSegment = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRad),
            Size = new Size(arcRad, arcRad),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
        };
        var haloFigure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRad),
            IsClosed = false,
        };
        haloFigure.Segments.Add(haloSegment);
        var haloGeometry = new PathGeometry();
        haloGeometry.Figures.Add(haloFigure);
        var haloPath = new Path
        {
            Stroke = Tokens.BrushStrokeHalo,
            StrokeThickness = arcThicknessActual + 2,
            Data = haloGeometry,
        };

        var arcSegment = new ArcSegment
        {
            Point = new Point(center.X, center.Y - arcRad),
            Size = new Size(arcRad, arcRad),
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise
        };
        var figure = new PathFigure
        {
            StartPoint = new Point(center.X, center.Y - arcRad),
            IsClosed = false
        };
        figure.Segments.Add(arcSegment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var progressPath = new Path
        {
            Data = geometry,
            Stroke = arcBrushActual,
            StrokeThickness = arcThicknessActual
        };

        TextBlock? symbolText = null;
        if (symbol is not null)
        {
            symbolText = new TextBlock
            {
                Text = symbol,
                Foreground = symbolBrushActual,
                // Sized from the diameter that bounds it, not from a constant. Assigned inline
                // rather than hoisted into a local: the guard examines the assignment's own
                // value, and a local name references no token.
                FontSize = fontSize ?? dia * (dia < BadgeDiameterThreshold ? ContainerRatio.Badge : ContainerRatio.Small),
                TextAlignment = TextAlignment.Center,
                Width = dia,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            symbolText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(symbolText, center.X - radius);
            Canvas.SetTop(symbolText, center.Y - symbolText.DesiredSize.Height / 2);
        }

        // Drawn icon. Sized by ContainerRatio.Dominant when the icon is the button's whole
        // content, and by ContainerRatio.Medium when a label shares the face — Dominant names the
        // former case only. The scale goes on RenderTransform rather than Geometry.Transform on
        // purpose: RenderTransform scales the stroke with the path, so GridStrokeThickness stays
        // 1.8 at every size. Geometry.Transform would leave a 1.8px hairline on a 63px icon.
        //
        // iconSize and iconTop are hoisted out of the `icon is not null` block because the label
        // is positioned from them. Both are 0 when there is no icon, which is also the case where
        // the icon/label stack collapses to the label alone.
        var iconSize = icon is null
            ? 0
            : dia * (label is null ? ContainerRatio.Dominant : ContainerRatio.Medium);
        var labelHeight = label is null ? 0 : Tokens.FontSizeSubtitle * LabelLineHeightRatio;
        var stackGap = label is null || icon is null ? 0 : dia * LabelGapRatio;
        // Centres the icon and its label together rather than centring the icon alone. With no
        // label this reduces to center.Y - iconSize / 2, which is what
        // DwellArcButtonIconTests.TheIcon_IsCentredOnTheButton pins.
        var iconTop = center.Y - (iconSize + stackGap + labelHeight) / 2;

        Path? iconStrokePath = null;
        Path? iconFillPath = null;
        if (icon is not null)
        {
            var scale = iconSize / ActionIcons.GridSize;

            Path BuildLayer(Geometry geometry, bool filled)
            {
                var path = new Path
                {
                    Data = geometry,
                    Stroke = filled ? null : symbolBrushActual,
                    StrokeThickness = filled ? 0 : ActionIcons.GridStrokeThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Fill = filled ? symbolBrushActual : null,
                    RenderTransform = new ScaleTransform(scale, scale),
                };
                Canvas.SetLeft(path, center.X - iconSize / 2);
                Canvas.SetTop(path, iconTop);
                return path;
            }

            if (icon.Stroke is not null) iconStrokePath = BuildLayer(icon.Stroke, filled: false);
            if (icon.Fill is not null) iconFillPath = BuildLayer(icon.Fill, filled: true);
        }

        TextBlock? labelText = null;
        if (label is not null)
        {
            labelText = new TextBlock
            {
                Text = label,
                Foreground = Tokens.BrushTextPrimary,
                FontSize = Tokens.FontSizeSubtitle,
                FontWeight = Tokens.FontWeightMedium,
                FontFamily = Tokens.FontStandard,
                TextAlignment = TextAlignment.Center,
                Width = dia,
            };
            labelText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(labelText, center.X - radius);
            Canvas.SetTop(labelText, iconTop + iconSize + stackGap);
        }

        return new DwellArcButton(bg, rimHalo, haloPath, progressPath, haloSegment, arcSegment, symbolText, iconStrokePath, iconFillPath, labelText, center, arcRad);
    }

    public void UpdateProgress(double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        if (clamped <= 0)
        {
            ProgressPath.Visibility = Visibility.Collapsed;
            HaloPath.Visibility = Visibility.Collapsed;
            return;
        }
        ProgressPath.Visibility = Visibility.Visible;
        HaloPath.Visibility = Visibility.Visible;

        UpdateArc(Arc, _center, _arcRadius, progress);
        UpdateArc(HaloArc, _center, _arcRadius, progress);
    }

    public static void UpdateArc(ArcSegment arc, Point center, double arcRadius, double progress)
    {
        var clamped = Math.Clamp(progress, 0, 1);
        var angle = clamped * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;

        var endX = center.X + arcRadius * Math.Sin(radians);
        var endY = center.Y - arcRadius * Math.Cos(radians);

        arc.Point = new Point(endX, endY);
        arc.IsLargeArc = angle > 180;
    }

    public void AddTo(Canvas canvas)
    {
        canvas.Children.Add(RimHalo);       // must precede Background
        canvas.Children.Add(Background);
        canvas.Children.Add(HaloPath);      // must precede ProgressPath
        canvas.Children.Add(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Add(Symbol);
        if (IconFill is not null)
            canvas.Children.Add(IconFill); // must precede IconStroke
        if (IconStroke is not null)
            canvas.Children.Add(IconStroke);
        if (Label is not null)
            canvas.Children.Add(Label);
    }

    /// <summary>
    /// Removes exactly what <see cref="AddTo"/> added. Keep the two in step: a caller that
    /// removes ONE button from a shared canvas cannot use <c>Children.Clear</c>, and the
    /// alternative is each caller hand-copying the element list.
    ///
    /// <para>
    /// <c>GlassBackdropWindow.ClearButton</c> is the cautionary case. Its hand-copied list named
    /// three of the eight elements and had been wrong for some time, yet orphaned nothing —
    /// every path to it happens to run <c>Children.Clear()</c> first, so it only ever met an
    /// empty canvas. The divergence was masked, not harmless: a caller reordering would have
    /// turned it into a silent leak, and silent is the operative word, since a stale layer
    /// accumulating over a rebuilt button fails no test and looks like nothing at all.
    /// <c>DwellArcButtonTests.RemoveFrom_IsTheInverseOfAddTo</c> pins the pairing.
    /// </para>
    /// </summary>
    /// <param name="canvas">The canvas to remove this button's visuals from.</param>
    public void RemoveFrom(Canvas canvas)
    {
        canvas.Children.Remove(RimHalo);
        canvas.Children.Remove(Background);
        canvas.Children.Remove(HaloPath);
        canvas.Children.Remove(ProgressPath);
        if (Symbol is not null)
            canvas.Children.Remove(Symbol);
        if (IconFill is not null)
            canvas.Children.Remove(IconFill);
        if (IconStroke is not null)
            canvas.Children.Remove(IconStroke);
        if (Label is not null)
            canvas.Children.Remove(Label);
    }
}
