namespace EyeCtrl.App.Overlays;

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Theme;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.ActionMenu;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;
using Serilog;

public partial class ActionMenuWindow : OverlayWindow, IActionMenuOverlay
{
    // UI Layout Constants
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render
    private const int PreviewFailureLogWindowSeconds = 30;
    private const double IndicatorOffsetMargin = 10; // Margin to keep indicator inside preview circle
    private const double IndicatorSizeRatio = 0.15; // Indicator size as ratio of preview circle size
    private const double MaxIndicatorSize = 30; // Maximum indicator size in pixels
    private const double CrosshairLineThickness = 1.5; // Thickness of crosshair lines
    private const double PinHeadRatio = 0.8; // Pin head size as ratio of indicator size
    private const double PinLineThickness = 2.0; // Thickness of pin line
    private const int PreviewCircleBorderThickness = 2; // Border thickness of preview circle

    private readonly Dictionary<string, DwellArcButton> _buttonVisuals = new();

    private readonly HandlerFaultReporter _previewFaults = new(
        (context, exception) => Log.Warning(
            "Menu preview capture FAILED: {ExceptionType:l}: {Message:l} ({Context:l})",
            exception.GetType().Name, exception.Message, context),
        () => Environment.TickCount64 / 1000,
        PreviewFailureLogWindowSeconds);

    /// <summary>
    /// The per-item dwell-progress subscriptions this window owns, so they can be detached before
    /// the next rebuild. Mirrors <c>EdgeButtonBarWindow._progressSubs</c>, and exists for the same
    /// reason: a lambda you did not keep cannot be unsubscribed.
    ///
    /// <para>
    /// It became load-bearing on 2026-08-26. Before then <see cref="Show"/> ran once per menu
    /// open, against item ViewModels the ViewModel had just rebuilt, so each handler died with its
    /// item. <see cref="Show"/> is now also called on a layer change and on the Help toggle — and
    /// the Help toggle only flips <c>ShowLabels</c>, leaving the SAME item ViewModels in place. One
    /// more handler was attached to each of them per toggle.
    /// </para>
    /// </summary>
    private readonly List<(ActionMenuItemViewModel Item, PropertyChangedEventHandler Handler)> _itemProgressSubs = new();

    private CancellationTokenSource? _captureCts;

    /// <summary>
    /// The preview's capture seam: production grabs the screen, tests inject an image (or a fault)
    /// so the failure/info lines are pinnable without reading any screen pixel.
    /// </summary>
    internal Func<PhysicalPoint, int, CaptureResult?> PreviewCapture { get; set; } = ScreenCaptureService.CaptureRegion;

    public ActionMenuWindow()
    {
        InitializeComponent();
        ExcludeFromScreenCapture = true; // the action-menu preview must not capture the menu itself
    }

    public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null, int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false, double previewMagnification = 1.0)
    {
        // Detach the previous rebuild's progress handlers before clearing. A layer change hands us
        // fresh item ViewModels and would drop the old ones anyway, but a Help toggle does not —
        // it reuses the live items, so without this each toggle leaves another handler on each.
        foreach (var (item, handler) in _itemProgressSubs)
            item.PropertyChanged -= handler;
        _itemProgressSubs.Clear();

        _buttonVisuals.Clear();
        MenuCanvas.Children.Clear();

        var vm = DataContext as ActionMenuViewModel;

        // Size window to fit buttons at the given ring radius. RingPlan.WindowSizeFor is the one
        // definition of this square (the settings preview and its fit scalar read the same one).
        var windowSize = RingPlan.WindowSizeFor(ringRadius);
        Width = windowSize;
        Height = windowSize;

        var canvasCenter = new Point(Width / 2, Height / 2);

        // The preview ellipse's centre (the window centre) and size are what the geometry is drawn
        // against. Delay the capture to let WPF render overlay visibility changes.
        var (screenWidth, screenHeight) = ScreenCaptureService.PhysicalScreenSize();
        var geometry = PreviewGeometry(
            center, gazePoint, zoomCenter, zoomRadius, previewCircleSize, previewMagnification,
            DpiHelper.ScaleX, screenWidth, screenHeight);
        var captureCenter = geometry.CaptureCenter;
        var captureRadius = geometry.CaptureRadiusPx;
        var captureCanvasCenter = canvasCenter;
        var captureCircleSize = previewCircleSize;
        var captureBorderColor = borderColor;

        // Where the commit point lands inside the preview circle. In the zoom path the capture is
        // centred on the commit point, so the marker sits at the circle's centre: the preview shows
        // the click point at 1:1 around itself, not the menu display point.
        var indicatorOffsetX = geometry.MarkerOffsetDip.X;
        var indicatorOffsetY = geometry.MarkerOffsetDip.Y;

        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var captureToken = _captureCts.Token;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await CapturePreviewAsync(
                captureCanvasCenter, captureCircleSize, captureCenter, captureRadius,
                geometry.ViewScaleDipPerPx, captureBorderColor, captureToken);
        });

        // Position indicator at center of preview
        var indicatorSize = Math.Min(previewCircleSize * IndicatorSizeRatio, MaxIndicatorSize); // scale with circle, cap at 30px

        // The indicator is drawn ON TOP of the captured preview image — arbitrary screen
        // content — so it is the §4.1.3 case: no single colour holds contrast there. It is a
        // TextPrimary mark over a StrokeHalo under-stroke, which is the conventional
        // precision-marker form (light mark, dark outline) and makes the visible feature the
        // backdrop-independent internal edge. Halo elements are added FIRST: later siblings
        // paint on top. (Previously Brushes.Red / Brushes.White — off-palette, and red on a
        // captured photo can vanish entirely.)
        if (positionIndicatorStyle == PositionIndicatorStyle.Crosshair)
        {
            Line CrosshairLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness) => new()
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = stroke,
                StrokeThickness = thickness
            };

            double hx1 = canvasCenter.X - indicatorSize + indicatorOffsetX;
            double hy = canvasCenter.Y + indicatorOffsetY;
            double hx2 = canvasCenter.X + indicatorSize + indicatorOffsetX;
            double vx = canvasCenter.X + indicatorOffsetX;
            double vy1 = canvasCenter.Y - indicatorSize + indicatorOffsetY;
            double vy2 = canvasCenter.Y + indicatorSize + indicatorOffsetY;

            MenuCanvas.Children.Add(CrosshairLine(hx1, hy, hx2, hy,
                Tokens.BrushStrokeHalo, CrosshairLineThickness + 2));
            MenuCanvas.Children.Add(CrosshairLine(vx, vy1, vx, vy2,
                Tokens.BrushStrokeHalo, CrosshairLineThickness + 2));
            MenuCanvas.Children.Add(CrosshairLine(hx1, hy, hx2, hy,
                Tokens.BrushTextPrimary, CrosshairLineThickness));
            MenuCanvas.Children.Add(CrosshairLine(vx, vy1, vx, vy2,
                Tokens.BrushTextPrimary, CrosshairLineThickness));
        }
        else // Pin
        {
            // Pin: small filled circle with a line pointing down to center. The head is a
            // filled shape, so it takes a StrokeHalo OUTLINE rather than an under-stroke.
            var pinHeadSize = indicatorSize * PinHeadRatio;
            var pinHead = new Ellipse
            {
                Width = pinHeadSize,
                Height = pinHeadSize,
                Fill = Tokens.BrushTextPrimary,
                Stroke = Tokens.BrushStrokeHalo,
                StrokeThickness = 2
            };
            Canvas.SetLeft(pinHead, canvasCenter.X - pinHeadSize / 2 + indicatorOffsetX);
            Canvas.SetTop(pinHead, canvasCenter.Y - indicatorSize + indicatorOffsetY);

            Line PinLine(Brush stroke, double thickness) => new()
            {
                X1 = canvasCenter.X + indicatorOffsetX,
                Y1 = canvasCenter.Y - indicatorSize / 2 + indicatorOffsetY,
                X2 = canvasCenter.X + indicatorOffsetX,
                Y2 = canvasCenter.Y + indicatorOffsetY,
                Stroke = stroke,
                StrokeThickness = thickness
            };

            MenuCanvas.Children.Add(PinLine(Tokens.BrushStrokeHalo, PinLineThickness + 2));
            MenuCanvas.Children.Add(PinLine(Tokens.BrushTextPrimary, PinLineThickness));
            MenuCanvas.Children.Add(pinHead);
        }

        // Paint the ring through the shared painter — the one file that passes the ring radius
        // into DwellArcButton.Create. The coordinate conversion below is this window's seam, not
        // the painter's: itemVm.Position is an ABSOLUTE physical-screen point (menu center + ring
        // offset) and MenuCanvas is window-relative (see ActionMenuGeometry's remarks for the
        // 894ea34 off-canvas regression this guards against). Dwell registration is the
        // ViewModel's, never the painter's.
        if (vm is not null)
        {
            var slots = new List<RingPaintSlot>(vm.Items.Count);
            foreach (var itemVm in vm.Items)
            {
                slots.Add(new RingPaintSlot(
                    itemVm.Id,
                    ActionMenuGeometry.ButtonCanvasCenter(itemVm.Position, center, canvasCenter),
                    itemVm.Icon,
                    itemVm.Label,
                    itemVm.Kind,
                    itemVm.IsDisabled));
            }

            foreach (var (id, button) in ActionMenuRingPainter.Paint(
                         MenuCanvas, slots, RingPaintOptions.Runtime(showLabels)))
                _buttonVisuals[id] = button;

            foreach (var itemVm in vm.Items)
            {
                var capturedId = itemVm.Id;
                var capturedItem = itemVm;
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == nameof(ActionMenuItemViewModel.DwellProgress))
                        UpdateItemProgress(capturedId, capturedItem.DwellProgress);
                    else if (e.PropertyName == nameof(ActionMenuItemViewModel.IsDwellPaused)
                        && _buttonVisuals.TryGetValue(capturedId, out var heldButton))
                        heldButton.SetPaused(capturedItem.IsDwellPaused);
                };
                itemVm.PropertyChanged += handler;
                _itemProgressSubs.Add((capturedItem, handler));
            }
        }

        Left = DpiHelper.PhysicalToLogicalX(center.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(center.Y) - Height / 2;
        Show();
    }

    /// <summary>Test seam: the ring buttons the last <c>Show</c> painted, keyed by item id.</summary>
    internal IReadOnlyDictionary<string, DwellArcButton> DrawnButtonsForTest => _buttonVisuals;

    private void UpdateItemProgress(string targetId, double progress)
    {
        if (_buttonVisuals.TryGetValue(targetId, out var btn))
            btn.UpdateProgress(progress);
    }

    /// <summary>
    /// The preview's capture + ellipse insertion. Extracted from <c>Show</c> so the failure and
    /// success lines are testable headlessly: a capture failure is LOUD (rate-limited warning) and
    /// a cancellation — a re-show or layer change superseding this capture — stays silent.
    /// </summary>
    internal async Task CapturePreviewAsync(
        Point canvasCenter, double circleSize, PhysicalPoint captureCenter, int captureRadius,
        double viewScaleDipPerPx, string borderColor, CancellationToken token)
    {
        try
        {
            await Task.Delay(ScreenCaptureDelayMs, token);
            var capture = PreviewCapture(captureCenter, captureRadius);
            if (capture is null)
            {
                _previewFaults.Report("Menu preview capture failed",
                    new InvalidOperationException("the capture returned no image"));
                return;
            }

            var previewEllipse = new Ellipse
            {
                Width = circleSize,
                Height = circleSize,
                Fill = new ImageBrush(capture.Value.Image) { Stretch = Stretch.Fill },
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor)),
                StrokeThickness = PreviewCircleBorderThickness
            };
            Canvas.SetLeft(previewEllipse, canvasCenter.X - circleSize / 2);
            Canvas.SetTop(previewEllipse, canvasCenter.Y - circleSize / 2);

            // An opaque under-fill below the picture, the same surface the magnifier's disc uses.
            // The capture is centred on the commit point now, so at an edge its off-screen part is
            // transparent; without this the menu's backdrop would show through the preview. The
            // image ellipse goes over it, and both stay behind the buttons.
            var previewBackdrop = new Ellipse
            {
                Width = circleSize,
                Height = circleSize,
                Fill = Tokens.BrushSurfaceDark,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(previewBackdrop, canvasCenter.X - circleSize / 2);
            Canvas.SetTop(previewBackdrop, canvasCenter.Y - circleSize / 2);

            MenuCanvas.Children.Insert(0, previewEllipse); // Insert at back so buttons stay on top
            MenuCanvas.Children.Insert(0, previewBackdrop); // ... and the under-fill behind the image

            var effectiveCenter = capture.Value.EffectiveCenter;
            Log.Information("Menu preview: center=({X:F0},{Y:F0}) captureRadius={R}px scale={S:F2}",
                effectiveCenter.X, effectiveCenter.Y, captureRadius, viewScaleDipPerPx);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a re-show or a layer change: no preview this time, and nothing to say.
        }
        catch (Exception exception)
        {
            // GDI+ capture/preview failures (secure desktop, session lock, RDP transition): no
            // preview this time, the menu itself is unaffected. Catching here also fully observes
            // the task — any other escape would fault it unobserved.
            _previewFaults.Report("Menu preview capture failed", exception);
        }
    }

    /// <summary>
    /// The action-menu preview's geometry. In the zoom path (back from the magnifier) it shows the
    /// COMMIT point: a circle of <paramref name="previewCircleSize"/> DIP renders
    /// <paramref name="previewMagnification"/> screen pixels per captured pixel. Production passes
    /// 1.0 on both paths — the owner's decision — so the menu shows the picked spot at 1× (no zoom)
    /// and the magnifier's factor is not carried into the preview; the parameter stays so the pure
    /// seam keeps its factor coverage. The old form captured the whole zoom region and shrank it
    /// into the circle — 0.59× at the owner's settings.
    ///
    /// <para>
    /// The marker is placed from <paramref name="gazePoint"/> (the precise commit point) rather than
    /// the menu's display <paramref name="center"/>: under <c>atCenter</c> that display point is the
    /// screen centre, so the marker used to show where the screen centre fell, not where the click
    /// would land. In the zoom path the capture is centred on the commit point, so the marker sits
    /// at (0,0) — at edges and corners too. The plain path is unchanged: 1.0× around the menu's
    /// display point.
    /// </para>
    /// <para>
    /// The screen size is a parameter, not a read of the live display, so the effective centre is
    /// testable without a real screen and follows the same centred placement the capture uses.
    /// </para>
    /// </summary>
    internal static (PhysicalPoint CaptureCenter, int CaptureRadiusPx, double ViewScaleDipPerPx, Vector MarkerOffsetDip) PreviewGeometry(
        PhysicalPoint center,
        PhysicalPoint? gazePoint,
        PhysicalPoint? zoomCenter,
        int? zoomRadius,
        double previewCircleSize,
        double previewMagnification,
        double scaleX,
        int screenWidth,
        int screenHeight)
    {
        var commitPoint = gazePoint ?? center;
        var zoomPath = zoomCenter is not null && zoomRadius is not null;

        var captureCenter = zoomPath ? commitPoint : center;
        // `Math.Round`, not truncation: at 1.0/1.5 the truncated radius costs a whole capture pixel
        // and pushes the measured magnification just outside the gate's ±1 %.
        var captureRadius = (int)Math.Round(
            previewCircleSize / 2 / (zoomPath ? previewMagnification : 1.0) * scaleX);
        captureRadius = Math.Max(1, captureRadius);

        // The capture is centred on the requested point, so the image's centre is that point. The
        // marker must be placed against the image actually shown; the mirror below is the same
        // centred placement the capture service uses.
        var effectiveCaptureCenter = CaptureViewMapping.EffectiveCaptureCenter(
            captureCenter, captureRadius, screenWidth, screenHeight);
        var viewScaleDipPerPx = previewCircleSize / (2.0 * captureRadius);
        var (markerX, markerY) = ClampToScreen(
            (commitPoint.X - effectiveCaptureCenter.X) * viewScaleDipPerPx,
            (commitPoint.Y - effectiveCaptureCenter.Y) * viewScaleDipPerPx,
            previewCircleSize);

        return (captureCenter, captureRadius, viewScaleDipPerPx, new Vector(markerX, markerY));
    }

    private static (double x, double y) ClampToScreen(double offsetX, double offsetY, double circleSize)
    {
        var offsetMag = Math.Sqrt(offsetX * offsetX + offsetY * offsetY);
        var maxOffset = circleSize / 2 - IndicatorOffsetMargin;
        if (offsetMag > maxOffset && offsetMag > 0)
        {
            offsetX = offsetX / offsetMag * maxOffset;
            offsetY = offsetY / offsetMag * maxOffset;
        }
        return (offsetX, offsetY);
    }
}
