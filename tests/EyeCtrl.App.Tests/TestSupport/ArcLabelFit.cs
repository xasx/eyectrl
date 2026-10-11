namespace EyeCtrl.App.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Measures a real button the way a person sees it: the INK box of the drawn label — not the
/// label <see cref="TextBlock"/>'s layout box, which is the full button width and would fail
/// every word, "Flip" included — and the drawn icon's ink in its actual placement. Both
/// <see cref="Measure"/> (an edge definition through the production edge window) and
/// <see cref="MeasureButton"/> (a button a caller already built — the modes menu's, or the edge
/// window's own) return the same shape of result.
///
/// <para>
/// <b>Three assertions, reported as data.</b> The caller asserts:
/// <list type="number">
/// <item><description>
/// <b>Fit.</b> <see cref="ArcLabelFitResult.ClearanceDip"/> is at least
/// <see cref="RequiredClearanceDip"/> — every ink-box corner lies that far inside the progress
/// arc's inner edge (<c>radius - arc inset - half the arc thickness</c>).
/// </description></item>
/// <item><description>
/// <b>No overlap.</b> <see cref="ArcLabelFitResult.OverlapsIcon"/> and
/// <see cref="ArcLabelFitResult.OverlapsSymbol"/> are false — the ink box does not intersect the
/// icon paths' bounds or the symbol's box. This is the assertion that catches the actual Recapture
/// defect, which the fit rule alone cannot see: the label was drawn over the glyph but sat inside
/// the arc, so a fit-only test stayed green.
/// </description></item>
/// <item><description>
/// <b>Icon fit.</b> <see cref="ArcLabelFitResult.IconClearanceDip"/> is at least
/// <see cref="RequiredClearanceDip"/> — the icon's real drawn ink (flattened outlines plus half
/// the stroke, walked point by point) stays that far inside the arc. Box corners are never used.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>Real visuals, never re-implemented layout.</b> The measurement drives
/// <see cref="EdgeButtonBarViewModel.ShowButtons"/> then
/// <see cref="EdgeButtonBarWindow.SyncButtons"/> on the caller's STA thread, with no
/// <c>Show()</c> — no HWND, no screen, no input. The icon-vs-symbol choice under test lives in the
/// window, so a test that rebuilt the stacking arithmetic itself could not see it (critique E4).
/// </para>
///
/// <para>
/// The ink reference is <see cref="FormattedText.BuildGeometry"/> from the label's own font
/// properties; its origin is the line's layout origin, read via
/// <c>ContentStart</c>/<c>ContentEnd.GetCharacterRect</c>. The vertical extent is the geometry's
/// (so descenders count and empty line space does not); the horizontal extent is the character
/// rects' (so a spaced label keeps its advance width). Ideal text-formatting mode is required and
/// guarded — the measurement is a DIP-space description of the glyphs, and at Display mode the
/// pixel-grid snapping is a device-resolution fact this helper does not model.
/// </para>
/// </summary>
internal static class ArcLabelFit
{
    /// <summary>The clearance, in DIP, every ink-box corner must keep from the arc's inner edge.</summary>
    internal const double RequiredClearanceDip = 2.0;

    // A logical anchor far from any screen edge, so the window's bounding-box arithmetic cannot
    // clamp. Scaled to physical because ShowButtons' anchor is physical while the seed centre is
    // mapped straight back to logical for drawing.
    private const double AnchorLogicalX = 1000.0;
    private const double AnchorLogicalY = 500.0;

    /// <summary>
    /// Builds one edge-bar button for <paramref name="definition"/> through the production window
    /// — <c>ShowButtons</c> + <c>SyncButtons</c>, laid out by hand — and returns the real button
    /// and its canvas. Must run on an STA thread. Shared by <see cref="Measure"/> and the UIA-name
    /// checks, so both inspect the same production-drawn control.
    /// </summary>
    internal static (DwellArcButton Button, Canvas Canvas) BuildButton(EdgeButtonDefinition definition)
    {
        var (button, canvas, _) = BuildButtonInWindow(definition);
        return (button, canvas);
    }

    /// <summary>
    /// <see cref="BuildButton"/>, also returning the window — T09's fit harness detaches the
    /// window's content into its 2.0 host, so it needs the window to inherit the font from.
    /// </summary>
    internal static (DwellArcButton Button, Canvas Canvas, EdgeButtonBarWindow Window) BuildButtonInWindow(
        EdgeButtonDefinition definition)
    {
        var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        var window = new EdgeButtonBarWindow { DataContext = vm };

        vm.ShowButtons(
            new[] { definition },
            new PhysicalPoint(AnchorLogicalX * DpiHelper.ScaleX, AnchorLogicalY * DpiHelper.ScaleY));
        window.SyncButtons();

        // Layout by hand: the window is never shown, so its canvas has no presentation source and
        // its children are never measured or arranged. A bare Canvas Measure/Arrange is all the
        // TextBlocks need for GetCharacterRect to answer.
        var canvas = (Canvas)window.FindName("ButtonCanvas");
        canvas.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        canvas.Arrange(new Rect(new Point(0, 0), canvas.DesiredSize));

        return (window.DrawnButtons[definition.Id], canvas, window);
    }

    /// <summary>
    /// Measures the button <see cref="BuildButton"/> built: the label's ink fit and overlap, and
    /// the icon's real-ink clearance to the arc.
    /// </summary>
    /// <param name="definition">The definition to render; its Label and Icon are what get measured.</param>
    /// <returns>The measurement, including the resolved font face for the caller to log.</returns>
    internal static ArcLabelFitResult Measure(EdgeButtonDefinition definition)
    {
        var (_, canvas) = BuildButton(definition);
        var radius = definition.RadiusLogical;
        var dia = radius * 2;
        var background = canvas.Children.OfType<Ellipse>()
            .Single(e => Math.Abs(e.Width - dia) < 1e-6);
        var centre = new Point(Canvas.GetLeft(background) + radius, Canvas.GetTop(background) + radius);

        var label = LabelOf(definition, canvas);
        var symbol = SymbolOf(definition, canvas, label);
        var iconPaths = IconPathsOf(canvas);

        return BuildResult(
            definition.Id,
            hasLabel: label is not null,
            hasIcon: definition.Icon is not null,
            hasSymbol: symbol is not null,
            ink: label is null ? Rect.Empty : InkBoxOf(label),
            centre,
            InnerEdgeOf(radius),
            IconBoundsOf(iconPaths),
            BoundsOf(symbol),
            iconPaths,
            FaceOf(label, symbol));
    }

    /// <summary>
    /// Measures a button the caller already built and laid out — the modes menu's own drawn
    /// buttons, handed out through its <c>DrawnButtons</c> seam, or an edge window's through
    /// <see cref="BuildButton"/>. No canvas is needed: <see cref="DwellArcButton"/> publishes the
    /// placed visuals, and the label's character rects only require the label to have been
    /// measured and arranged first.
    /// </summary>
    internal static ArcLabelFitResult MeasureButton(DwellArcButton button, string buttonId)
    {
        var radius = button.Background.Width / 2;
        var centre = new Point(
            Canvas.GetLeft(button.Background) + radius,
            Canvas.GetTop(button.Background) + radius);
        var ink = button.Label is null ? Rect.Empty : InkBoxOf(button.Label);
        var iconPaths = IconPathsOf(button).ToList();

        return BuildResult(
            buttonId,
            hasLabel: button.Label is not null,
            hasIcon: button.IconStroke is not null || button.IconFill is not null,
            hasSymbol: button.Symbol is not null,
            ink,
            centre,
            InnerEdgeOf(radius),
            IconBoundsOf(iconPaths),
            BoundsOf(button.Symbol),
            iconPaths,
            FaceOf(button.Label, button.Symbol));
    }

    private static ArcLabelFitResult BuildResult(
        string buttonId, bool hasLabel, bool hasIcon, bool hasSymbol,
        Rect ink, Point centre, double innerEdge, Rect iconBounds, Rect symbolBounds,
        IReadOnlyList<Path> iconPaths, string face)
        => new(
            buttonId,
            hasLabel,
            hasIcon,
            hasSymbol,
            ink,
            ink.IsEmpty ? double.NaN : ClearanceOf(ink, centre, innerEdge),
            innerEdge,
            Overlaps(ink, iconBounds),
            Overlaps(ink, symbolBounds),
            IconInkClearanceOf(iconPaths, centre, innerEdge),
            IconScaleOf(iconPaths),
            face);

    private static double InnerEdgeOf(double radius)
    {
        var thickness = Math.Max(radius * 2 * DwellArcButton.ArcThicknessRatio, DwellArcButton.MinArcThickness);
        return radius - DwellArcButton.DefaultArcInset - thickness / 2;
    }

    private static IEnumerable<Path> IconPathsOf(DwellArcButton button)
    {
        if (button.IconStroke is not null) yield return button.IconStroke;
        if (button.IconFill is not null) yield return button.IconFill;
    }

    /// <summary>
    /// The icon layers off a built button canvas. The ScaleTransform filter is the same rule
    /// <see cref="IconBoundsOf"/> used inline: only Create's icon layers carry one, while the
    /// halo and progress arc paths do not.
    /// </summary>
    private static IReadOnlyList<Path> IconPathsOf(Canvas canvas)
        => canvas.Children.OfType<Path>().Where(p => p.RenderTransform is ScaleTransform).ToList();

    private static Rect BoundsOf(TextBlock? text)
        => text is null
            ? Rect.Empty
            : new Rect(
                Canvas.GetLeft(text),
                Canvas.GetTop(text),
                text.DesiredSize.Width,
                text.DesiredSize.Height);

    /// <summary>
    /// The label's ink box in canvas coordinates. <c>AddTo</c> paints the label last, which is how
    /// it is picked off the canvas — the type is the one element whose role the definition names.
    /// </summary>
    private static TextBlock? LabelOf(EdgeButtonDefinition definition, Canvas canvas)
    {
        if (definition.Label is null) return null;

        var last = canvas.Children[canvas.Children.Count - 1] as TextBlock;
        if (last is null || last.Text != definition.Label)
            throw new InvalidOperationException(
                "DwellArcButton.AddTo no longer paints the label last, so ArcLabelFit cannot pick "
                + "the label off the button canvas. Update its identification rule.");

        return last;
    }

    /// <summary>The symbol TextBlock for a glyph definition (icon null, non-empty symbol), else null.</summary>
    private static TextBlock? SymbolOf(EdgeButtonDefinition definition, Canvas canvas, TextBlock? label)
    {
        if (definition.Icon is not null || string.IsNullOrEmpty(definition.Symbol)) return null;
        return canvas.Children.OfType<TextBlock>().First(t => !ReferenceEquals(t, label));
    }

    private static Rect InkBoxOf(TextBlock label)
    {
        var formattingMode = TextOptions.GetTextFormattingMode(label);
        if (formattingMode != TextFormattingMode.Ideal)
            throw new InvalidOperationException(
                "ArcLabelFit measures ink in Ideal text-formatting mode, but this label is in "
                + formattingMode + " mode. The helper's DIP-space measurement would not describe "
                + "what the pixel grid draws, so the assertion would be about the wrong geometry.");

        var lineStart = label.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        var lineEnd = label.ContentEnd.GetCharacterRect(LogicalDirection.Backward);

        var typeface = new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch);
        var formatted = new FormattedText(
            label.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            label.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(label).PixelsPerDip);

        // The line's own origin anchors the geometry: the TextBlock is Width = dia with centred
        // text, so its box corners sit on the button's bounding square and say nothing about where
        // the word starts.
        var inkBounds = formatted.BuildGeometry(new Point(lineStart.Left, lineStart.Top)).Bounds;
        var left = Canvas.GetLeft(label) + lineStart.Left;
        var right = Canvas.GetLeft(label) + lineEnd.Right;

        return new Rect(
            left,
            Canvas.GetTop(label) + inkBounds.Top,
            right - left,
            inkBounds.Height);
    }

    /// <summary>The union of the icon layers' drawn bounds (path bounds plus half their stroke).</summary>
    private static Rect IconBoundsOf(IEnumerable<Path> paths)
    {
        var bounds = Rect.Empty;
        foreach (var path in paths)
        {
            // Half the stroke sits outside the path's own bounds; Data.Bounds is in grid units and
            // the RenderTransform scales the stroke with the geometry.
            var scale = TransformOf(path);
            var halfStroke = path.Stroke is null ? 0 : path.StrokeThickness / 2;
            var drawn = scale.TransformBounds(Rect.Inflate(path.Data.Bounds, halfStroke, halfStroke));
            var placed = new Rect(
                Canvas.GetLeft(path) + drawn.X,
                Canvas.GetTop(path) + drawn.Y,
                drawn.Width,
                drawn.Height);
            bounds = bounds.IsEmpty ? placed : Rect.Union(bounds, placed);
        }

        return bounds;
    }

    /// <summary>
    /// The clearance, in DIP, between the icon's INK and the arc's inner edge: the farthest point
    /// of the real drawn geometry — flattened outlines, placed where the path actually sits
    /// (<c>Canvas.Left/Top + point × scale</c>), plus half the stroke — from the button centre,
    /// subtracted from the inner edge. Positive means every drawn point is inside the arc. NaN
    /// when the button has no icon.
    ///
    /// <para>
    /// Bounding-box corners are deliberately NOT used: an icon's box corner can cross the arc
    /// while every drawn point stays well inside (rejecting a correctly sized glyph), and a
    /// box-based rule can also accept a spiky shape whose box happens to fit. This walks the ink
    /// in its real placement, so a path translated off-centre moves the measured ink with it and
    /// a stacked (labelled) icon measures where it really is.
    /// </para>
    /// </summary>
    private static double IconInkClearanceOf(
        IReadOnlyList<Path> paths, Point centre, double innerEdge)
    {
        if (paths.Count == 0) return double.NaN;

        var clearance = double.PositiveInfinity;
        foreach (var path in paths)
        {
            var scale = TransformOf(path).ScaleX;
            // Half the stroke sits outside the path's own outline; RenderTransform scales it with
            // the geometry, so it scales too before the placed radius is formed.
            var halfStrokeDip = (path.Stroke is null ? 0 : path.StrokeThickness / 2) * scale;
            var inkRadiusDip = MaxPlacedDistanceFromCentre(
                    path.Data, scale, Canvas.GetLeft(path), Canvas.GetTop(path), centre)
                + halfStrokeDip;
            clearance = Math.Min(clearance, innerEdge - inkRadiusDip);
        }

        return clearance;
    }

    /// <summary>
    /// The ScaleTransform Create gives every icon path. Throws rather than assuming one: without
    /// it the measurement would describe unscaled grid units, silently.
    /// </summary>
    private static ScaleTransform TransformOf(Path path)
        => path.RenderTransform as ScaleTransform
           ?? throw new InvalidOperationException(
               "An icon path lost the ScaleTransform Create gives it; ArcLabelFit would then "
               + "measure its unscaled geometry, not the drawn icon.");

    /// <summary>The drawn icon's scale, or NaN when there is no icon. One scale serves both layers.</summary>
    private static double IconScaleOf(IReadOnlyList<Path> paths)
        => paths.Count == 0 ? double.NaN : TransformOf(paths[0]).ScaleX;

    /// <summary>
    /// The farthest a point of <paramref name="geometry"/> sits from the button centre once the
    /// path has been placed: each grid point is mapped to canvas DIP (<c>left + x × scale</c>)
    /// first. Flattened at a tight tolerance because a curve's control points lie OUTSIDE the
    /// curve (which would overstate the ink) and a bounding box overstates it in every direction
    /// at once.
    /// </summary>
    private static double MaxPlacedDistanceFromCentre(
        Geometry geometry, double scale, double left, double top, Point centre)
    {
        var flattened = geometry.GetFlattenedPathGeometry(0.001, ToleranceType.Absolute);
        var max = 0.0;

        void Consider(Point p)
        {
            var dx = left + p.X * scale - centre.X;
            var dy = top + p.Y * scale - centre.Y;
            max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy));
        }

        foreach (var figure in flattened.Figures)
        {
            Consider(figure.StartPoint);
            foreach (var segment in figure.Segments)
            {
                switch (segment)
                {
                    case LineSegment line:
                        Consider(line.Point);
                        break;
                    case PolyLineSegment poly:
                        foreach (var p in poly.Points) Consider(p);
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Flattened geometry produced a {segment.GetType().Name}, which "
                            + "MaxPlacedDistanceFromCentre does not enumerate; the measurement "
                            + "would silently understate the icon's ink.");
                }
            }
        }

        return max;
    }

    private static double ClearanceOf(Rect ink, Point centre, double innerEdge)
    {
        var corners = new[]
        {
            new Point(ink.Left, ink.Top),
            new Point(ink.Right, ink.Top),
            new Point(ink.Left, ink.Bottom),
            new Point(ink.Right, ink.Bottom),
        };

        var furthest = corners.Max(c => Math.Sqrt(
            (c.X - centre.X) * (c.X - centre.X) + (c.Y - centre.Y) * (c.Y - centre.Y)));

        return innerEdge - furthest;
    }

    /// <summary>True only for a positive-area intersection; touching edges are not an overlap.</summary>
    private static bool Overlaps(Rect a, Rect b)
    {
        if (a.IsEmpty || b.IsEmpty) return false;
        var intersection = Rect.Intersect(a, b);
        return !intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0;
    }

    private static string FaceOf(TextBlock? label, TextBlock? symbol)
    {
        var element = label ?? symbol;
        if (element is null) return "<no text>";

        var family = element.FontFamily.Source;
        var weight = element.FontWeight;
        var mode = TextOptions.GetTextFormattingMode(element);
        return $"{family} {weight} {element.FontSize.ToString("0.#", CultureInfo.InvariantCulture)} DIP ({mode})";
    }
}

/// <summary>
/// One measured button. <see cref="ClearanceDip"/> is positive while the whole label ink box is
/// inside the arc's inner edge, and negative once any corner crosses it;
/// <see cref="IconClearanceDip"/> does the same for the icon's real drawn ink. Both pass
/// thresholds live on <see cref="ArcLabelFit.RequiredClearanceDip"/>.
/// </summary>
internal sealed record ArcLabelFitResult(
    string ButtonId,
    bool HasLabel,
    bool HasIcon,
    bool HasSymbol,
    Rect LabelInkBox,
    double ClearanceDip,
    double ArcInnerEdgeDip,
    bool OverlapsIcon,
    bool OverlapsSymbol,
    double IconClearanceDip,
    double IconScale,
    string ResolvedFace);
