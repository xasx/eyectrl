namespace EyeCtrl.App.Tests.TestSupport;

using System;
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
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Measures a real edge-bar button, built by the production window, the way a person sees it: the
/// INK box of the drawn label — not the label <see cref="TextBlock"/>'s layout box, which is the
/// full button width and would fail every word, "Flip" included.
///
/// <para>
/// <b>Two assertions, reported as data.</b> The caller asserts:
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
    /// Builds one edge-bar button for <paramref name="definition"/> through the production
    /// window and measures its label. Must run on an STA thread.
    /// </summary>
    /// <param name="definition">The definition to render; its Label is what gets measured.</param>
    /// <returns>The measurement, including the resolved font face for the caller to log.</returns>
    internal static ArcLabelFitResult Measure(EdgeButtonDefinition definition)
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

        var radius = definition.RadiusLogical;
        var dia = radius * 2;
        var background = canvas.Children.OfType<Ellipse>()
            .Single(e => Math.Abs(e.Width - dia) < 1e-6);
        var centre = new Point(Canvas.GetLeft(background) + radius, Canvas.GetTop(background) + radius);

        var thickness = Math.Max(dia * DwellArcButton.ArcThicknessRatio, DwellArcButton.MinArcThickness);
        var innerEdge = radius - DwellArcButton.DefaultArcInset - thickness / 2;

        var label = LabelOf(definition, canvas);
        var symbol = SymbolOf(definition, canvas, label);
        var ink = label is null ? Rect.Empty : InkBoxOf(label);

        var iconBounds = IconBoundsOf(canvas);
        var symbolBounds = symbol is null
            ? Rect.Empty
            : new Rect(
                Canvas.GetLeft(symbol),
                Canvas.GetTop(symbol),
                symbol.DesiredSize.Width,
                symbol.DesiredSize.Height);

        return new ArcLabelFitResult(
            definition.Id,
            HasLabel: label is not null,
            HasIcon: definition.Icon is not null,
            HasSymbol: symbol is not null,
            LabelInkBox: ink,
            ClearanceDip: ink.IsEmpty ? double.NaN : ClearanceOf(ink, centre, innerEdge),
            ArcInnerEdgeDip: innerEdge,
            OverlapsIcon: Overlaps(ink, iconBounds),
            OverlapsSymbol: Overlaps(ink, symbolBounds),
            ResolvedFace: FaceOf(label, symbol));
    }

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
    private static Rect IconBoundsOf(Canvas canvas)
    {
        var bounds = Rect.Empty;
        foreach (var path in canvas.Children.OfType<Path>().Where(p => p.RenderTransform is ScaleTransform))
        {
            var scale = (ScaleTransform)path.RenderTransform;
            // Half the stroke sits outside the path's own bounds; Data.Bounds is in grid units and
            // the RenderTransform scales the stroke with the geometry.
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
/// One measured button. <see cref="ClearanceDip"/> is positive while the whole ink box is inside
/// the arc's inner edge, and negative once any corner crosses it; the pass threshold lives on
/// <see cref="ArcLabelFit.RequiredClearanceDip"/>.
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
    string ResolvedFace);
