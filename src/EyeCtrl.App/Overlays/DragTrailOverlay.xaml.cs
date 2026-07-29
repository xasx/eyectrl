namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls.Theme;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class DragTrailOverlay : OverlayWindow
{
    private readonly Line _trailLine;
    private readonly Ellipse _settleRing;
    private readonly TextBlock _grabIcon;
    private readonly TextBlock _releaseIcon;
    private readonly Ellipse _releaseBg;

    public DragTrailOverlay()
    {
        IsClickThrough = true;
        InitializeComponent();

        // Trail-line colour is settings-driven (DragSettings.TrailColor); set in Configure.
        // Default to AccentWarning so the line is visible if Configure is never called.
        _trailLine = new Line
        {
            Stroke = Tokens.BrushAccentWarning,
            StrokeThickness = 2,
            Opacity = 0.7
        };
        TrailCanvas.Children.Add(_trailLine);

        _settleRing = new Ellipse
        {
            Width = 40, Height = 40,
            Stroke = Tokens.BrushAccentSuccess,
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_settleRing);

        _grabIcon = CreateIcon("\u270A", Tokens.BrushAccentWarning);
        _releaseIcon = CreateIcon("\u270B", Tokens.BrushAccentSuccess);

        _releaseBg = CreateButtonBg(Tokens.BrushSurfacePrediction, Tokens.BrushAccentSuccess);

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
    }

    public void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
        PhysicalPoint releasePos)
    {
        var originLogical = new Point(DpiHelper.PhysicalToLogicalX(grabOrigin.X), DpiHelper.PhysicalToLogicalY(grabOrigin.Y));
        var cursorLogical = new Point(DpiHelper.PhysicalToLogicalX(cursorPos.X), DpiHelper.PhysicalToLogicalY(cursorPos.Y));

        _trailLine.X1 = originLogical.X;
        _trailLine.Y1 = originLogical.Y;
        _trailLine.X2 = cursorLogical.X;
        _trailLine.Y2 = cursorLogical.Y;

        Canvas.SetLeft(_grabIcon, cursorLogical.X - 10);
        Canvas.SetTop(_grabIcon, cursorLogical.Y - 10);

        var releaseLogical = new Point(DpiHelper.PhysicalToLogicalX(releasePos.X), DpiHelper.PhysicalToLogicalY(releasePos.Y));
        PositionButton(_releaseBg, _releaseIcon, releaseLogical);

        if (settleProgress > 0)
        {
            _settleRing.Visibility = Visibility.Visible;
            Canvas.SetLeft(_settleRing, cursorLogical.X - 20);
            Canvas.SetTop(_settleRing, cursorLogical.Y - 20);

            var circumference = Math.PI * 40;
            var dashArray = new DoubleCollection { settleProgress * circumference / 3, circumference };
            _settleRing.StrokeDashArray = dashArray;
        }
        else
        {
            _settleRing.Visibility = Visibility.Collapsed;
        }
    }

    public void Clear()
    {
        _trailLine.X1 = 0; _trailLine.Y1 = 0;
        _trailLine.X2 = 0; _trailLine.Y2 = 0;
        _settleRing.Visibility = Visibility.Collapsed;
    }

    private static TextBlock CreateIcon(string glyph, Brush foreground)
    {
        return new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            Foreground = foreground,
            IsHitTestVisible = false
        };
    }

    private static Ellipse CreateButtonBg(Brush fill, Brush stroke)
    {
        return new Ellipse
        {
            Width = 26, Height = 26,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
    }

    private static void PositionButton(Ellipse bg, TextBlock icon, Point logicalPos)
    {
        Canvas.SetLeft(bg, logicalPos.X - 13);
        Canvas.SetTop(bg, logicalPos.Y - 13);
        Canvas.SetLeft(icon, logicalPos.X - 8);
        Canvas.SetTop(icon, logicalPos.Y - 8);
    }
}
