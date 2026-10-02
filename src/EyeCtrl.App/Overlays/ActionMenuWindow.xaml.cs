namespace EyeCtrl.App.Overlays;

using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Controls.Icons;
using EyeCtrl.Controls.Theme;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class ActionMenuWindow : OverlayWindow, IActionMenuOverlay
{
    // UI Layout Constants
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render
    private const double IndicatorOffsetMargin = 10; // Margin to keep indicator inside preview circle
    private const double IndicatorSizeRatio = 0.15; // Indicator size as ratio of preview circle size
    private const double MaxIndicatorSize = 30; // Maximum indicator size in pixels
    private const double CrosshairLineThickness = 1.5; // Thickness of crosshair lines
    private const double PinHeadRatio = 0.8; // Pin head size as ratio of indicator size
    private const double PinLineThickness = 2.0; // Thickness of pin line
    private const int PreviewCircleBorderThickness = 2; // Border thickness of preview circle

    /// <summary>
    /// Group opacity for disabled (<see cref="ActionMenuItemViewModel.IsDisabled"/>) ring items.
    /// The value the dim contrast test validates (<c>ActionIconDimContrastTests</c>) and the dim
    /// render test reads (<c>ActionMenuWindowDimRenderTests</c>): 0.45 measured 2.30:1
    /// dimmed-icon vs dimmed-fill on Midnight/#FFFFFF — below any usable floor. Adjust ONLY with
    /// a measured note.
    /// </summary>
    internal const double DisabledOpacity = 0.6;

    private readonly Dictionary<string, DwellArcButton> _buttonVisuals = new();

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

    public ActionMenuWindow()
    {
        InitializeComponent();
        ExcludeFromScreenCapture = true; // the action-menu preview must not capture the menu itself
    }

    public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null, int? zoomRadius = null, PhysicalPoint? gazePoint = null, bool showLabels = false)
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

        // Size window to fit buttons at the given ring radius
        var windowSize = ActionMenuGeometry.WindowSize(ringRadius);
        Width = windowSize;
        Height = windowSize;

        var canvasCenter = new Point(Width / 2, Height / 2);

        // Delay capture to let WPF render overlay visibility changes
        var previewRadius = (int)(previewCircleSize / 2);
        // In zoom path: capture the same region the zoom view showed (same center, same radius)
        // In AtCenter path: capture the menu display center so indicator can show gaze offset
        var captureCenter = zoomCenter ?? center;
        var captureRadius = zoomRadius != null
            ? (int)(zoomRadius.Value * DpiHelper.ScaleX)          // zoomRadius is logical (DIP) px → convert to physical px for capture
            : (int)(previewRadius * DpiHelper.ScaleX);            // previewRadius is logical px → convert to physical
        // The capture rectangle is clamped to the screen, so the image's centre is the effective
        // one, not the requested centre. The marker must be placed against the image actually shown.
        var effectiveCaptureCenter = ScreenCaptureService.GetEffectiveCenter(captureCenter, captureRadius);
        var captureCanvasCenter = canvasCenter;
        var captureCircleSize = previewCircleSize;
        var captureBorderColor = borderColor;

        // Compute where within the preview circle the precise fixation point (center) lies.
        // When not in zoom mode both offsets are 0 and the indicator stays at canvasCenter.
        double indicatorOffsetX = 0;
        double indicatorOffsetY = 0;

        // Calculate indicator offset based on the actual gaze/fixation point
        var indicatorPoint = gazePoint ?? center;
        if (zoomCenter != null && zoomRadius != null)
        {
            double scale = previewCircleSize / (2.0 * captureRadius);
            var markerPoint = CaptureViewMapping.ToViewPoint(effectiveCaptureCenter, canvasCenter, scale, center);
            indicatorOffsetX = markerPoint.X - canvasCenter.X;
            indicatorOffsetY = markerPoint.Y - canvasCenter.Y;
            (indicatorOffsetX, indicatorOffsetY) = ClampToScreen(indicatorOffsetX, indicatorOffsetY, previewCircleSize);
        }
        else if (gazePoint != null && gazePoint != center)
        {
            double scale = previewCircleSize / (2.0 * captureRadius);
            var markerPoint = CaptureViewMapping.ToViewPoint(effectiveCaptureCenter, canvasCenter, scale, indicatorPoint);
            indicatorOffsetX = markerPoint.X - canvasCenter.X;
            indicatorOffsetY = markerPoint.Y - canvasCenter.Y;
            (indicatorOffsetX, indicatorOffsetY) = ClampToScreen(indicatorOffsetX, indicatorOffsetY, previewCircleSize);
        }

        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var captureToken = _captureCts.Token;
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(ScreenCaptureDelayMs, captureToken);
                var capture = ScreenCaptureService.CaptureRegion(captureCenter, captureRadius);
                if (capture == null) return;

                var previewEllipse = new Ellipse
                {
                    Width = captureCircleSize,
                    Height = captureCircleSize,
                    Fill = new ImageBrush(capture.Value.Image) { Stretch = Stretch.Fill },
                    Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(captureBorderColor)),
                    StrokeThickness = PreviewCircleBorderThickness
                };
                Canvas.SetLeft(previewEllipse, captureCanvasCenter.X - captureCircleSize / 2);
                Canvas.SetTop(previewEllipse, captureCanvasCenter.Y - captureCircleSize / 2);
                MenuCanvas.Children.Insert(0, previewEllipse); // Insert at back so buttons stay on top
            }
            catch
            {
                // Cancellation (window re-shown, token superseded) and GDI+ capture/preview
                // failures (secure desktop, session lock, RDP transition) alike: no preview
                // this time, the menu itself is unaffected. Catching here also fully observes
                // the async lambda's task — any other escape would fault it unobserved.
            }
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

        // Render action menu buttons from VM items using DwellArcButton
        if (vm is not null)
        {
            foreach (var itemVm in vm.Items)
            {
                // itemVm.Position is an ABSOLUTE physical-screen point (menu center + ring offset);
                // MenuCanvas is window-relative, so convert via ActionMenuGeometry (see its remarks
                // for the 894ea34 off-canvas regression this guards against).
                var canvasPoint = ActionMenuGeometry.ButtonCanvasCenter(itemVm.Position, center, canvasCenter);
                var btn = DwellArcButton.Create(
                    canvasPoint,
                    ActionMenuGeometry.ButtonRadius,
                    symbol: null,
                    icon: ActionIcons.For(itemVm.Icon),
                    label: showLabels ? itemVm.Label : null);
                if (itemVm.IsDisabled)
                {
                    // Group opacity via a per-button child canvas: AddTo places its parts relative to ANY
                    // canvas, so a host canvas at origin carries them unchanged and Opacity dims the whole
                    // group at once. Per-part opacity would be neither group opacity nor hazard-free.
                    var host = new Canvas { Opacity = DisabledOpacity };
                    btn.AddTo(host);
                    MenuCanvas.Children.Add(host);
                }
                else
                {
                    btn.AddTo(MenuCanvas);
                }
                _buttonVisuals[itemVm.Id] = btn;

                var capturedId = itemVm.Id;
                var capturedItem = itemVm;
                PropertyChangedEventHandler handler = (_, e) =>
                {
                    if (e.PropertyName == nameof(ActionMenuItemViewModel.DwellProgress))
                        UpdateItemProgress(capturedId, capturedItem.DwellProgress);
                };
                itemVm.PropertyChanged += handler;
                _itemProgressSubs.Add((capturedItem, handler));
            }
        }

        Left = DpiHelper.PhysicalToLogicalX(center.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(center.Y) - Height / 2;
        Show();
    }

    private void UpdateItemProgress(string targetId, double progress)
    {
        if (_buttonVisuals.TryGetValue(targetId, out var btn))
            btn.UpdateProgress(progress);
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
