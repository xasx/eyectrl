namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core;
using EyeCtrl.Core.Processing;

/// <summary>
/// The Move state's visible feedback (sitting 9, row c): ONE click-through overlay that draws
/// <list type="bullet">
/// <item>a RING at the settle anchor whose arc fills over the 500 ms settle, driven by
/// <see cref="NoteSettleTracker.Progress"/> — the owner can see the move "taking hold" instead of
/// only seeing the accepted end of it, and</item>
/// <item>a DASHED OUTLINE of the card where the note will land — the exact card rect the real
/// re-anchor derives through <see cref="NoteLayout.Place"/> with the same arguments, so the
/// preview and the result cannot disagree.</item>
/// </list>
/// It is click-through (it must never take a gaze or a click from the note it previews), owns no
/// dwell target, and never activates: the base class pins WS_EX_NOACTIVATE and the UIAccess band
/// once, and the manager never raises it per frame — the layout is placed when the settle window
/// starts and at the accepted spot, and only the arc's geometry follows the samples.
/// <para>
/// The manager owns its lifetime (one per manager, created lazily, never in DI; Dispose closes
/// it), exactly like the Place-here window.
/// </para>
/// </summary>
public sealed class NoteMovePreviewWindow : OverlayWindow
{
    /// <summary>The ring's radius in DIP: the settle tracker's own radius, so the ring the owner
    /// sees IS the area the tracker judges the gaze against.</summary>
    public static double RingRadiusDip => NoteSettleTracker.RadiusDip;

    /// <summary>The settle arc's stroke in DIP (the note buttons' own dwell-arc weight).</summary>
    private const double RingFillStrokeDip = 3.0;

    /// <summary>The dashed card outline's stroke in DIP.</summary>
    private const double CardStrokeDip = 2.0;

    /// <summary>The dashed card outline's dash/gap in stroke widths (6/4 DIP at 2 DIP).</summary>
    private const double DashLength = 3.0;
    private const double DashGap = 2.0;

    private readonly Canvas _canvas;
    private Ellipse? _ringTrack;
    private Path? _ringFill;
    private Rectangle? _cardOutline;
    private bool _ringShown;

    // T6 fixup-08 R4's seam, the place-here window's twin: set when the manager's show seam
    // skipped the HWND show, so the window still counts as on screen in a headless test.
    private bool _displayedWithoutHwnd;

    /// <summary>The preview window's screen rect in DIP, or an empty rect before the first place.</summary>
    public Rect WindowRectDip { get; private set; }

    /// <summary>The ring's centre in SCREEN DIP (the settle anchor the manager placed it on).</summary>
    public Point RingCentreDip { get; private set; }

    /// <summary>The dashed card rect in SCREEN DIP: the landing outline the manager derived.</summary>
    public Rect CardRectDip { get; private set; }

    /// <summary>The ring's fill, 0..1.</summary>
    public double Progress { get; private set; }

    /// <summary>Whether the ring is part of the current preview (hidden once a settle is accepted).</summary>
    public bool RingShown => _ringShown;

    public NoteMovePreviewWindow()
    {
        // Click-through on purpose (note-gate census: exempt): the preview sits under the eyes
        // while the owner looks for a spot, and it must never become the window a dwell resolves
        // to or swallow a caretaker click meant for the page.
        IsClickThrough = true;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _canvas = new Canvas { ClipToBounds = false };
        Content = _canvas;
    }

    /// <summary>
    /// Shows the window the way production does, or - with <paramref name="realHwnd"/> false -
    /// only records that it counts as on screen, without an HWND (the note windows' seam). A
    /// second show while already visible is a no-op: the manager reuses the window for every
    /// sample of a settle, and a real <c>Show()</c> per sample would be the re-assert storm the
    /// UIAccess band's contract forbids.
    /// </summary>
    public void ShowForDisplay(bool realHwnd)
    {
        if (realHwnd)
        {
            if (!IsVisible)
                Show();
            return;
        }

        _displayedWithoutHwnd = true;
    }

    /// <summary>Whether the window counts as on screen: the real HWND's IsVisible in production,
    /// or the seam's own flag when it was never handed to the OS.</summary>
    public bool IsDisplayed => _displayedWithoutHwnd || IsVisible;

    /// <summary>Hides the window and clears the seam's displayed state, so a hidden preview never
    /// reads as displayed until the next show.</summary>
    public new void Hide()
    {
        _displayedWithoutHwnd = false;
        base.Hide();
    }

    /// <summary>
    /// Places the preview for one settle state: the ring centred on the settle anchor (screen DIP)
    /// and the dashed outline around the card rect the note would land on. Geometry is rebuilt
    /// only when it actually changed — the arc alone moves sample to sample, through
    /// <see cref="SetProgress"/> — and the window is positioned from the union of the two, so
    /// neither is ever clipped.
    /// </summary>
    /// <param name="ringCentreDip">The ring's centre in screen DIP (the settle anchor).</param>
    /// <param name="cardDip">The landing card's rect in screen DIP.</param>
    /// <param name="progress">The ring's fill, 0..1.</param>
    /// <param name="ring">Whether to draw the ring (false once a settle is accepted: the outline
    /// alone holds the accepted spot while the place disc is up).</param>
    public void Place(Point ringCentreDip, Rect cardDip, double progress, bool ring)
    {
        bool geometryChanged = _cardOutline is null || ring != _ringShown
            || ringCentreDip != RingCentreDip || cardDip != CardRectDip;
        _ringShown = ring;
        RingCentreDip = ringCentreDip;
        CardRectDip = cardDip;

        if (geometryChanged)
        {
            WindowRectDip = BoundsOf(ringCentreDip, cardDip, ring);
            Left = WindowRectDip.Left;
            Top = WindowRectDip.Top;
            Width = WindowRectDip.Width;
            Height = WindowRectDip.Height;
            Layout();
        }

        SetProgress(progress);
    }

    /// <summary>The ring's fill, 0..1. A zero (or a hidden ring) hides the arc; the track and the
    /// dashed outline stay. Cheap on purpose: one PathGeometry, no child rebuild — this runs on
    /// every gaze sample of a live settle.</summary>
    public void SetProgress(double progress)
    {
        Progress = Math.Clamp(progress, 0.0, 1.0);
        if (_ringFill is null)
            return;
        if (!_ringShown || Progress <= 0)
        {
            _ringFill.Visibility = Visibility.Hidden;
            return;
        }

        var origin = new Point(WindowRectDip.Left, WindowRectDip.Top);
        double cx = RingCentreDip.X - origin.X;
        double cy = RingCentreDip.Y - origin.Y;
        double r = RingRadiusDip;

        var arc = new ArcSegment
        {
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise
        };
        DwellArcButton.UpdateArc(arc, new Point(cx, cy), r, Progress);

        var figure = new PathFigure { StartPoint = new Point(cx, cy - r) };
        figure.Segments.Add(arc);
        _ringFill.Data = new PathGeometry(new[] { figure });
        _ringFill.Visibility = Visibility.Visible;
    }

    /// <summary>The window rect: the union of the ring's circle and the landing card, plus the
    /// margin that keeps both strokes inside the window.</summary>
    private static Rect BoundsOf(Point ringCentreDip, Rect cardDip, bool ring)
    {
        double left = cardDip.Left, top = cardDip.Top;
        double right = cardDip.Right, bottom = cardDip.Bottom;
        if (ring)
        {
            double outer = RingRadiusDip + (LayoutConstants.NoteMovePreviewRingStrokeDip / 2);
            left = Math.Min(left, ringCentreDip.X - outer);
            top = Math.Min(top, ringCentreDip.Y - outer);
            right = Math.Max(right, ringCentreDip.X + outer);
            bottom = Math.Max(bottom, ringCentreDip.Y + outer);
        }

        var bounds = new Rect(left, top, right - left, bottom - top);
        bounds.Inflate(LayoutConstants.NoteMovePreviewMarginDip, LayoutConstants.NoteMovePreviewMarginDip);
        return bounds;
    }

    /// <summary>Rebuilds the drawn tree for the current geometry (once per place, not per
    /// sample). Everything is drawn in WINDOW-relative DIP from the placed screen rects.</summary>
    private void Layout()
    {
        _canvas.Children.Clear();
        _ringFill = null;
        _cardOutline = null;

        var origin = new Point(WindowRectDip.Left, WindowRectDip.Top);

        if (_ringShown)
        {
            double r = RingRadiusDip;
            double cx = RingCentreDip.X - origin.X;
            double cy = RingCentreDip.Y - origin.Y;
            _ringTrack = new Ellipse
            {
                Width = 2 * r,
                Height = 2 * r,
                // The ring's track: the same WCAG 1.4.11 edge brush the note cards use, so it
                // reads against a page of either lightness without competing with the accent fill
                // that is the progress signal.
                Stroke = Tokens.BrushBorderSubtle,
                StrokeThickness = LayoutConstants.NoteMovePreviewRingStrokeDip,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_ringTrack, cx - r);
            Canvas.SetTop(_ringTrack, cy - r);
            _canvas.Children.Add(_ringTrack);

            _ringFill = new Path
            {
                Stroke = Tokens.BrushAccent,
                StrokeThickness = RingFillStrokeDip,
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false
            };
            _canvas.Children.Add(_ringFill);
        }

        var localCard = new Rect(
            CardRectDip.X - origin.X, CardRectDip.Y - origin.Y,
            CardRectDip.Width, CardRectDip.Height);
        _cardOutline = new Rectangle
        {
            Width = localCard.Width,
            Height = localCard.Height,
            // MarkerMove is the Move state's own mark on the card's border, so the dashed landing
            // outline reads as "this moving note", not as another control.
            Stroke = Tokens.BrushMarkerMove,
            StrokeThickness = CardStrokeDip,
            StrokeDashArray = new DoubleCollection { DashLength, DashGap },
            RadiusX = Tokens.StandardCornerRadius.TopLeft,
            RadiusY = Tokens.StandardCornerRadius.TopLeft,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_cardOutline, localCard.Left);
        Canvas.SetTop(_cardOutline, localCard.Top);
        _canvas.Children.Add(_cardOutline);
    }
}
