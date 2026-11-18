namespace EyeCtrl.App.Overlays;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;

/// <summary>
/// The Move state's "Place here" target (P3 D4; sitting 9: the familiar round control): a ROUND
/// DISC the same diameter, rim and fill as the note buttons, a ✓ glyph inside it, and the label
/// below at the note's own text size. The disc carries the same filling dwell arc the note
/// buttons use, so dwelling it looks exactly like dwelling ✓ Done on a card; the manager routes
/// the <c>{id}:place</c> progress here (it used to be dropped into the note's window).
/// <para>
/// The :place dwell target sits on the DISC's centre (<see cref="DiscCentreDip"/>), not on the
/// window's centre: the window is disc + label, and the control the gaze must reach is the disc.
/// </para>
/// <para>
/// It is deliberately NOT click-through — the P2 window gate's own-window conjunct needs the
/// topmost hit-testable window under the gaze to resolve to it, exactly like a note card. Created
/// lazily by the manager, never in DI; <c>NoteManager.Dispose</c> closes it. The window GROWS to
/// fit the measured label (T6 fixup-06 G1), and the manager reads that REAL size back for the
/// on-screen placement, the settle margin and the dwell target.
/// </para>
/// </summary>
public class NotePlaceHereWindow : OverlayWindow
{
    /// <summary>The disc's radius in DIP: the note buttons' own (28 DIP, 56 DIP diameter).</summary>
    public static double DiscRadiusDip => LayoutConstants.NoteButtonRadius;

    /// <summary>The dwell arc's radius: the note buttons' own inset (2 DIP inside the rim).</summary>
    private static double ArcRadiusDip => DiscRadiusDip - 2;

    /// <summary>The arc's stroke in DIP: the note buttons' own dwell-arc weight.</summary>
    private const double ArcStrokeDip = 3.0;

    /// <summary>The disc's glyph: the same ✓ the card's Done control uses — the familiar control
    /// for "this is the spot", not a new affordance the owner has to learn.</summary>
    private const string Glyph = "✓";

    private readonly Canvas _canvas;
    private Ellipse? _disc;
    private TextBlock? _glyph;
    private Path? _arc;
    private TextBlock? _label;

    /// <summary>The window's size in DIP: disc + label, measured at the note's text size.</summary>
    public Size SizeDip { get; private set; }

    /// <summary>The disc's centre in WINDOW-relative DIP — the point the :place dwell target must
    /// sit on. The label below it is not part of the control.</summary>
    public Point DiscCentreDip { get; private set; }

    // T6 fixup-08 R4: set when the manager's show seam skipped the HWND show.
    private bool _displayedWithoutHwnd;

    private double _textSizeDip;
    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false -
    /// only records that it counts as on screen, without an HWND (T6 fixup-08 R4). The Place-here
    /// window builds its content in the constructor and draws nothing on Loaded, so there is no
    /// drawn half to replay; the manager's rect, the settle margin and the dwell target are all
    /// its own and behave as before.
    /// </summary>
    public void ShowForDisplay(bool realHwnd)
    {
        if (realHwnd)
        {
            Show();
            return;
        }

        _displayedWithoutHwnd = true;
    }

    /// <summary>Whether the window counts as on screen: the real HWND's IsVisible in production,
    /// or the seam's own flag when it was never handed to the OS (R4).</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;

    /// <summary>Hides the window and clears the seam's displayed state (T6 fixup-09): the manager
    /// hides Place-here between moves, and a hidden window must never read as displayed again
    /// until the next show. <c>new</c> on purpose - the manager holds the concrete type.</summary>
    public new void Hide()
    {
        _displayedWithoutHwnd = false;
        base.Hide();
    }

    /// <summary>Builds the window for a note text size (T6 fixup-06 G1: the label reads at the
    /// note's own size).</summary>
    /// <param name="textSizeDip">The note's body text size — the label's font size.</param>
    public NotePlaceHereWindow(double textSizeDip)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        IsClickThrough = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _textSizeDip = textSizeDip;
        // The window is the MOVE state's dwell surface: its whole rect must be hit-testable so
        // the gate's own-window conjunct resolves the topmost window under the gaze to it (the
        // note windows' alpha-1/255 fill). A layered window passes mouse and WindowFromPoint
        // through fully transparent pixels, and the drawn disc covers only its own circle.
        _canvas = new Canvas { ClipToBounds = false, Background = CreateHitSurfaceBrush() };
        Content = _canvas;

        SizeDip = MeasureSize(textSizeDip, StringTable.Current.T("overlays.note.placeHere"));
        Width = SizeDip.Width;
        Height = SizeDip.Height;
        Compose();
    }

    /// <summary>
    /// Re-composes at a new text size (T6 fixup-06 G1): the label's font, the measured size, the
    /// disc's position and the window bounds move together, so the label can never overflow its
    /// box and the disc stays centred in the window it grew.
    /// </summary>
    /// <param name="textSizeDip">The note's body text size — the label's font size.</param>
    public void SetTextSize(double textSizeDip)
    {
        _textSizeDip = textSizeDip;
        SizeDip = MeasureSize(textSizeDip, StringTable.Current.T("overlays.note.placeHere"));
        Width = SizeDip.Width;
        Height = SizeDip.Height;
        Compose();
    }

    /// <summary>The window's hit surface (P2 D2 layout rule 2): the palette near-black at 1/255
    /// opacity — alpha above zero under the whole window, invisible against the page. Frozen: the
    /// window is built on whichever STA thread runs, and a frozen brush is shareable.</summary>
    private static Brush CreateHitSurfaceBrush()
    {
        var brush = new SolidColorBrush(Tokens.StrokeHalo) { Opacity = 1.0 / 255.0 };
        brush.Freeze();
        return brush;
    }

    /// <summary>Test accessors: the drawn disc, glyph, label and arc, read rather than assumed.</summary>
    internal Ellipse? DiscForTest => _disc;
    internal TextBlock? GlyphForTest => _glyph;
    internal TextBlock? LabelForTest => _label;
    internal Path? ArcForTest => _arc;

    /// <summary>The last dwell progress the manager pushed, 0..1 — what the arc was drawn from.</summary>
    internal double ProgressForTest { get; private set; }

    /// <summary>
    /// Draws the filling dwell arc for <c>{id}:place</c> (sitting 9: the progress used to be
    /// dropped into the note's window, which had no :place arc). Same geometry convention as the
    /// note buttons' arcs: a 3 DIP accent stroke at the disc's rim, clockwise from twelve o'clock.
    /// </summary>
    public void UpdateDwellProgress(double progress)
    {
        ProgressForTest = Math.Clamp(progress, 0.0, 1.0);
        if (_arc is null || _disc is null)
            return;

        if (progress <= 0)
        {
            _arc.Visibility = Visibility.Hidden;
            return;
        }

        double cx = Canvas.GetLeft(_disc) + DiscRadiusDip;
        double cy = Canvas.GetTop(_disc) + DiscRadiusDip;
        double r = ArcRadiusDip;

        var arc = new ArcSegment
        {
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise
        };
        DwellArcButton.UpdateArc(arc, new Point(cx, cy), r, progress);

        var figure = new PathFigure { StartPoint = new Point(cx, cy - r) };
        figure.Segments.Add(arc);
        _arc.Data = new PathGeometry(new[] { figure });
        _arc.Visibility = Visibility.Visible;
    }

    /// <summary>Builds the drawn tree for the current size: disc, ink-centred glyph, dwell arc,
    /// and the label below. Every position derives from <see cref="DiscCentreDip"/> and the
    /// measured size, so the drawn disc and the registered target cannot drift apart.</summary>
    private void Compose()
    {
        _canvas.Children.Clear();

        double diameter = 2 * DiscRadiusDip;
        DiscCentreDip = new Point(
            SizeDip.Width / 2,
            LayoutConstants.NotePlaceHereDiscMarginDip + DiscRadiusDip);

        // The disc-and-glyph pair mirrors NoteWindow.DrawButtons exactly: the same diameter, the
        // same SurfaceMedium fill, the same 1 DIP rim, and the same label-less Solo glyph size.
        double glyphFont = diameter * ContainerRatio.Solo;

        _disc = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            // The note buttons' own pair: a SurfaceMedium fill with a 1 DIP accent rim, so the
            // place control reads as one of them. AccentSuccess is the ✓ colour on a card.
            Fill = Tokens.BrushSurfaceMedium,
            Stroke = Tokens.BrushAccentSuccess,
            StrokeThickness = 1
        };
        Canvas.SetLeft(_disc, DiscCentreDip.X - DiscRadiusDip);
        Canvas.SetTop(_disc, DiscCentreDip.Y - DiscRadiusDip);
        _canvas.Children.Add(_disc);

        var formatted = new FormattedText(
            Glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Tokens.FontStandard.Source), glyphFont,
            Tokens.BrushAccentSuccess, null, TextFormattingMode.Ideal, 1.0);
        Rect ink = formatted.BuildGeometry(new Point(0, 0)).Bounds;
        _glyph = new TextBlock
        {
            Text = Glyph,
            FontFamily = Tokens.FontStandard,
            FontSize = glyphFont,
            Foreground = Tokens.BrushAccentSuccess,
            // Pure glyph: never a hit-test surface over the disc (the note buttons' rule).
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_glyph, DiscCentreDip.X - (ink.Left + (ink.Width / 2)));
        Canvas.SetTop(_glyph, DiscCentreDip.Y - (ink.Top + (ink.Height / 2)));
        _canvas.Children.Add(_glyph);

        _arc = new Path
        {
            Stroke = Tokens.BrushAccent,
            StrokeThickness = ArcStrokeDip,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_arc, 0);
        Canvas.SetTop(_arc, 0);
        _canvas.Children.Add(_arc);

        double labelWidth = Math.Max(1, SizeDip.Width - (2 * LayoutConstants.NotePlaceHereLabelMarginDip));
        _label = new TextBlock
        {
            Text = StringTable.Current.T("overlays.note.placeHere"),
            Width = labelWidth,
            FontSize = _textSizeDip,
            FontFamily = Tokens.FontStandard,
            // SemiBold: the label is an instruction, not body text — the owner asked for a
            // familiar control and a legible one at arm's length.
            FontWeight = FontWeights.SemiBold,
            Foreground = Tokens.BrushTextPrimary,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_label, LayoutConstants.NotePlaceHereLabelMarginDip);
        Canvas.SetTop(_label,
            LayoutConstants.NotePlaceHereDiscMarginDip + diameter + LayoutConstants.NotePlaceHereLabelGapDip);
        _canvas.Children.Add(_label);
    }

    /// <summary>
    /// The window's size in DIP for a label at <paramref name="fontSize"/>: wide enough for the
    /// disc (plus its arc's air) and for the measured label, never below the old 150 DIP box;
    /// tall enough for the disc, the gap and the label.
    /// </summary>
    private static Size MeasureSize(double fontSize, string text)
    {
        double labelWidth = 0;
        double labelHeight = 0;
        if (!string.IsNullOrEmpty(text))
        {
            var formatted = new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Tokens.FontStandard, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                fontSize, Tokens.BrushTextPrimary, null, TextFormattingMode.Ideal, 1.0);
            labelWidth = formatted.WidthIncludingTrailingWhitespace;
            labelHeight = formatted.Height;
        }

        double width = Math.Ceiling(Math.Max(
            LayoutConstants.NotePlaceHereWidthDip,
            Math.Max(
                (2 * DiscRadiusDip) + (2 * LayoutConstants.NotePlaceHereDiscMarginDip),
                labelWidth + (2 * LayoutConstants.NotePlaceHereLabelMarginDip))));
        double labelTop = LayoutConstants.NotePlaceHereDiscMarginDip
            + (2 * DiscRadiusDip) + LayoutConstants.NotePlaceHereLabelGapDip;
        double height = Math.Ceiling(labelTop + labelHeight + LayoutConstants.NotePlaceHereLabelMarginDip);
        return new Size(width, height);
    }
}
