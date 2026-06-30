namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class DragTrailOverlay : OverlayWindow
{
    private readonly Line _trailLine;
    private readonly Ellipse _settleRing;
    private readonly TextBlock _grabIcon;
    private readonly TextBlock _releaseIcon;
    private readonly TextBlock _cancelIcon;
    private readonly TextBlock _backIcon;
    private readonly Ellipse _cancelBg;
    private readonly Ellipse _backBg;
    private readonly Ellipse _releaseBg;

    public DragTrailOverlay()
    {
        IsClickThrough = true;
        InitializeComponent();

        _trailLine = new Line
        {
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800")),
            StrokeThickness = 2,
            Opacity = 0.7
        };
        TrailCanvas.Children.Add(_trailLine);

        _settleRing = new Ellipse
        {
            Width = 40, Height = 40,
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4caf50")),
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            Visibility = Visibility.Collapsed
        };
        TrailCanvas.Children.Add(_settleRing);

        _grabIcon = CreateIcon("\u270A", "#ff9800");
        _releaseIcon = CreateIcon("\u270B", "#4caf50");
        _cancelIcon = CreateIcon("\u2715", "#f44336");
        _backIcon = CreateIcon("\u21A9", "#4fc3f7");

        _releaseBg = CreateButtonBg("#1a3a1a", "#4caf50");
        _cancelBg = CreateButtonBg("#3a1a1a", "#f44336");
        _backBg = CreateButtonBg("#1a2a3a", "#4fc3f7");

        TrailCanvas.Children.Add(_releaseBg);
        TrailCanvas.Children.Add(_releaseIcon);
        TrailCanvas.Children.Add(_cancelBg);
        TrailCanvas.Children.Add(_cancelIcon);
        TrailCanvas.Children.Add(_backBg);
        TrailCanvas.Children.Add(_backIcon);
        TrailCanvas.Children.Add(_grabIcon);
    }

    public void Update(PhysicalPoint grabOrigin, PhysicalPoint cursorPos, double settleProgress,
        bool showBack, PhysicalPoint releasePos, PhysicalPoint cancelPos, PhysicalPoint backPos)
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

        var cancelLogical = new Point(DpiHelper.PhysicalToLogicalX(cancelPos.X), DpiHelper.PhysicalToLogicalY(cancelPos.Y));
        PositionButton(_cancelBg, _cancelIcon, cancelLogical);

        _backBg.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        _backIcon.Visibility = showBack ? Visibility.Visible : Visibility.Collapsed;
        if (showBack)
        {
            var backLogical = new Point(DpiHelper.PhysicalToLogicalX(backPos.X), DpiHelper.PhysicalToLogicalY(backPos.Y));
            PositionButton(_backBg, _backIcon, backLogical);
        }

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
        _backBg.Visibility = Visibility.Collapsed;
        _backIcon.Visibility = Visibility.Collapsed;
    }

    private static TextBlock CreateIcon(string glyph, string color)
    {
        return new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
            IsHitTestVisible = false
        };
    }

    private static Ellipse CreateButtonBg(string fill, string stroke)
    {
        return new Ellipse
        {
            Width = 26, Height = 26,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
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
