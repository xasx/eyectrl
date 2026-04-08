namespace EyeCtrl.App.Overlays;

using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.ViewModels;

public partial class ActionMenuWindow : OverlayWindow
{
    // UI Layout Constants
    private const double ButtonRadius = 50; // Radius of action menu buttons in pixels
    private const int WindowSizeMargin = 20; // Extra margin around buttons for window sizing
    private const int ScreenCaptureDelayMs = 100; // Delay before capturing screen to let WPF render
    private const int ProgressArcInset = 4; // Inset of progress arc from button edge
    private const double IndicatorOffsetMargin = 10; // Margin to keep indicator inside preview circle
    private const double IndicatorSizeRatio = 0.15; // Indicator size as ratio of preview circle size
    private const double MaxIndicatorSize = 30; // Maximum indicator size in pixels
    private const double CrosshairLineThickness = 1.5; // Thickness of crosshair lines
    private const double PinHeadRatio = 0.8; // Pin head size as ratio of indicator size
    private const double PinLineThickness = 2.0; // Thickness of pin line
    private const int PreviewCircleBorderThickness = 2; // Border thickness of preview circle
    private const int ButtonSymbolFontSize = 22; // Font size for action symbols
    private const int ProgressArcThickness = 4; // Thickness of dwell progress arc
    private const byte ButtonBackgroundAlpha = 200; // Alpha value for button background
    private const byte ButtonBorderAlpha = 100; // Alpha value for button border

    private readonly Dictionary<string, (Ellipse Background, Path ProgressArc, ArcSegment Arc)> _buttonVisuals = new();
    private PhysicalPoint _targetCenter = new(0, 0);

    public ActionMenuWindow()
    {
        InitializeComponent();
    }

    public void Show(PhysicalPoint center, List<InteractionAction> actions, bool showBackButton, int dwellTimeMs, double ringRadius, double previewCircleSize, string borderColor, PositionIndicatorStyle positionIndicatorStyle, PhysicalPoint? zoomCenter = null, int? zoomRadius = null, PhysicalPoint? gazePoint = null)
    {
        _targetCenter = center;
        _buttonVisuals.Clear();
        MenuCanvas.Children.Clear();

        var allItems = new List<(string Id, string Label)>();
        foreach (var action in actions)
        {
            var id = action.ToString();
            allItems.Add((id, GetActionSymbol(action)));
        }
        if (showBackButton)
            allItems.Add(("Back", "\u21A9")); // ↩ Back arrow

        // Size window to fit buttons at the given ring radius
        var windowSize = (ringRadius + ButtonRadius) * 2 + WindowSizeMargin;
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
            // scale = logical-px / physical-px  →  offset = physical-px × (logical/physical) = logical-px
            // Use captureRadius (already in physical px) so the result is in WPF logical canvas px.
            double scale = previewCircleSize / (2.0 * captureRadius);
            indicatorOffsetX = (center.X - zoomCenter.Value.X) * scale;
            indicatorOffsetY = (center.Y - zoomCenter.Value.Y) * scale;

            // Clamp so indicator stays inside the preview circle (10 px margin)
            double offsetMag = Math.Sqrt(indicatorOffsetX * indicatorOffsetX + indicatorOffsetY * indicatorOffsetY);
            double maxOffset = previewCircleSize / 2 - IndicatorOffsetMargin;
            if (offsetMag > maxOffset && offsetMag > 0)
            {
                indicatorOffsetX = indicatorOffsetX / offsetMag * maxOffset;
                indicatorOffsetY = indicatorOffsetY / offsetMag * maxOffset;
            }
        }
        else if (gazePoint != null && gazePoint != center)
        {
            // View is at center, gaze is elsewhere - show indicator offset
            double scale = previewCircleSize / (2.0 * captureRadius);
            indicatorOffsetX = (indicatorPoint.X - captureCenter.X) * scale;
            indicatorOffsetY = (indicatorPoint.Y - captureCenter.Y) * scale;

            // Clamp so indicator stays inside the preview circle (10 px margin)
            double offsetMag = Math.Sqrt(indicatorOffsetX * indicatorOffsetX + indicatorOffsetY * indicatorOffsetY);
            double maxOffset = previewCircleSize / 2 - IndicatorOffsetMargin;
            if (offsetMag > maxOffset && offsetMag > 0)
            {
                indicatorOffsetX = indicatorOffsetX / offsetMag * maxOffset;
                indicatorOffsetY = indicatorOffsetY / offsetMag * maxOffset;
            }
        }

        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(ScreenCaptureDelayMs);
            var capturedImage = ScreenCaptureService.CaptureRegion(captureCenter, captureRadius);
            if (capturedImage == null) return;

            var previewEllipse = new Ellipse
            {
                Width = captureCircleSize,
                Height = captureCircleSize,
                Fill = new ImageBrush(capturedImage) { Stretch = Stretch.Fill },
                Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(captureBorderColor)),
                StrokeThickness = PreviewCircleBorderThickness
            };
            Canvas.SetLeft(previewEllipse, captureCanvasCenter.X - captureCircleSize / 2);
            Canvas.SetTop(previewEllipse, captureCanvasCenter.Y - captureCircleSize / 2);
            MenuCanvas.Children.Insert(0, previewEllipse); // Insert at back so buttons stay on top
        });

        // Position indicator at center of preview
        var indicatorSize = Math.Min(previewCircleSize * IndicatorSizeRatio, MaxIndicatorSize); // scale with circle, cap at 30px

        if (positionIndicatorStyle == PositionIndicatorStyle.Crosshair)
        {
            // Thin red crosshair lines
            var hLine = new Line
            {
                X1 = canvasCenter.X - indicatorSize + indicatorOffsetX,
                Y1 = canvasCenter.Y + indicatorOffsetY,
                X2 = canvasCenter.X + indicatorSize + indicatorOffsetX,
                Y2 = canvasCenter.Y + indicatorOffsetY,
                Stroke = Brushes.Red,
                StrokeThickness = CrosshairLineThickness
            };
            var vLine = new Line
            {
                X1 = canvasCenter.X + indicatorOffsetX,
                Y1 = canvasCenter.Y - indicatorSize + indicatorOffsetY,
                X2 = canvasCenter.X + indicatorOffsetX,
                Y2 = canvasCenter.Y + indicatorSize + indicatorOffsetY,
                Stroke = Brushes.Red,
                StrokeThickness = CrosshairLineThickness
            };
            MenuCanvas.Children.Add(hLine);
            MenuCanvas.Children.Add(vLine);
        }
        else // Pin
        {
            // Pin: small filled circle with a line pointing down to center
            var pinHeadSize = indicatorSize * PinHeadRatio;
            var pinHead = new Ellipse
            {
                Width = pinHeadSize,
                Height = pinHeadSize,
                Fill = Brushes.Red,
                Stroke = Brushes.White,
                StrokeThickness = 1
            };
            Canvas.SetLeft(pinHead, canvasCenter.X - pinHeadSize / 2 + indicatorOffsetX);
            Canvas.SetTop(pinHead, canvasCenter.Y - indicatorSize + indicatorOffsetY);

            var pinLine = new Line
            {
                X1 = canvasCenter.X + indicatorOffsetX,
                Y1 = canvasCenter.Y - indicatorSize / 2 + indicatorOffsetY,
                X2 = canvasCenter.X + indicatorOffsetX,
                Y2 = canvasCenter.Y + indicatorOffsetY,
                Stroke = Brushes.Red,
                StrokeThickness = PinLineThickness
            };
            MenuCanvas.Children.Add(pinHead);
            MenuCanvas.Children.Add(pinLine);
        }

        var angleStep = MathConstants.FullCircleDegrees / allItems.Count;

        for (int i = 0; i < allItems.Count; i++)
        {
            var (id, label) = allItems[i];
            var angleRad = (angleStep * i - MathConstants.RightAngleDegrees) * MathConstants.DegreesToRadians;
            var bx = canvasCenter.X + ringRadius * Math.Cos(angleRad);
            var by = canvasCenter.Y + ringRadius * Math.Sin(angleRad);

            // Background circle
            var bg = new Ellipse
            {
                Width = ButtonRadius * 2,
                Height = ButtonRadius * 2,
                Fill = new SolidColorBrush(Color.FromArgb(ButtonBackgroundAlpha, 30, 30, 30)),
                Stroke = new SolidColorBrush(Color.FromArgb(ButtonBorderAlpha, 100, 100, 100)),
                StrokeThickness = 1
            };
            Canvas.SetLeft(bg, bx - ButtonRadius);
            Canvas.SetTop(bg, by - ButtonRadius);
            MenuCanvas.Children.Add(bg);

            // Progress arc (dwell indicator ring)
            var arcRadius = ButtonRadius - ProgressArcInset;
            var arcSegment = new ArcSegment
            {
                Point = new Point(bx, by - arcRadius),
                Size = new Size(arcRadius, arcRadius),
                IsLargeArc = false,
                SweepDirection = SweepDirection.Clockwise
            };
            var figure = new PathFigure
            {
                StartPoint = new Point(bx, by - arcRadius),
                IsClosed = false
            };
            figure.Segments.Add(arcSegment);
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            var progressPath = new Path
            {
                Data = geometry,
                Stroke = Brushes.Lime,
                StrokeThickness = ProgressArcThickness
            };
            MenuCanvas.Children.Add(progressPath);

            _buttonVisuals[id] = (bg, progressPath, arcSegment);

            // Symbol label
            var textBlock = new TextBlock
            {
                Text = label,
                Foreground = Brushes.White,
                FontSize = ButtonSymbolFontSize,
                TextAlignment = TextAlignment.Center,
                Width = ButtonRadius * 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(textBlock, bx - ButtonRadius);
            Canvas.SetTop(textBlock, by - textBlock.DesiredSize.Height / 2);
            MenuCanvas.Children.Add(textBlock);
        }

        // Subscribe to VM item dwell progress changes for visual updates
        if (DataContext is ActionMenuViewModel vm)
        {
            foreach (var itemVm in vm.Items)
            {
                var capturedId = itemVm.Id;
                itemVm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ActionMenuItemViewModel.DwellProgress))
                        UpdateItemProgress(capturedId, itemVm.DwellProgress);
                };
            }
        }

        Left = DpiHelper.PhysicalToLogicalX(center.X) - Width / 2;
        Top = DpiHelper.PhysicalToLogicalY(center.Y) - Height / 2;
        Show();
    }

    private void UpdateItemProgress(string targetId, double progress)
    {
        if (!_buttonVisuals.TryGetValue(targetId, out var visuals)) return;

        var (bg, _, arcSegment) = visuals;
        var centerX = Canvas.GetLeft(bg) + ButtonRadius;
        var centerY = Canvas.GetTop(bg) + ButtonRadius;
        var radius = ButtonRadius - ProgressArcInset;

        var angle = progress * MathConstants.FullCircleDegrees;
        var radians = angle * MathConstants.DegreesToRadians;

        var endX = centerX + radius * Math.Sin(radians);
        var endY = centerY - radius * Math.Cos(radians);

        arcSegment.Point = new Point(endX, endY);
        arcSegment.IsLargeArc = angle > 180;
    }

    /// <summary>
    /// Returns a clear Unicode symbol for each action.
    /// </summary>
    private static string GetActionSymbol(InteractionAction action) => action switch
    {
        InteractionAction.LeftClick => "\u261D",     // ☝ Index pointing up (tap/click)
        InteractionAction.RightClick => "\u2630",    // ☰ Trigram (context menu)
        InteractionAction.DoubleClick => "\u270C",   // ✌ Victory hand (two taps)
        InteractionAction.DragStart => "\u270A",     // ✊ Raised fist (grab)
        InteractionAction.DragEnd => "\u270B",       // ✋ Raised hand (release)
        InteractionAction.ScrollUp => "\u25B2",      // ▲ Up triangle
        InteractionAction.ScrollDown => "\u25BC",    // ▼ Down triangle
        InteractionAction.ScrollLeft => "\u25C0",    // ◀ Left triangle
        InteractionAction.ScrollRight => "\u25B6",   // ▶ Right triangle
        _ => action.ToString()
    };
}
