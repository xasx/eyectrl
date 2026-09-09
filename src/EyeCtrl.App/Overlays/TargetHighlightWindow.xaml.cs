namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class TargetHighlightWindow : OverlayWindow, ITargetHighlightOverlay
{
    private double _configuredOpacity = 0.7;

    public TargetHighlightWindow()
    {
        InitializeComponent();
    }

    public void Configure(string color, double thickness, double opacity)
    {
        try
        {
            var converter = new BrushConverter();
            HighlightBorder.BorderBrush = (Brush)converter.ConvertFromString(color)!;
        }
        catch
        {
            HighlightBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 120, 212));
        }

        HighlightBorder.BorderThickness = new Thickness(thickness);
        _configuredOpacity = opacity;
    }

    public void ShowHighlight()
    {
        HighlightBorder.Opacity = 0;
        Show();
        HighlightBorder.BeginAnimation(Border.OpacityProperty,
            new DoubleAnimation(_configuredOpacity, TimeSpan.FromMilliseconds(200)));
    }

    public void HideHighlight()
    {
        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
        fadeOut.Completed += (_, _) => Hide();
        HighlightBorder.BeginAnimation(Border.OpacityProperty, fadeOut);
    }

    public void PositionAt(PhysicalRect bounds)
    {
        var logicalX = DpiHelper.PhysicalToLogicalX(bounds.X);
        var logicalY = DpiHelper.PhysicalToLogicalY(bounds.Y);
        var logicalW = DpiHelper.PhysicalToLogicalX(bounds.Width);
        var logicalH = DpiHelper.PhysicalToLogicalY(bounds.Height);

        Left = logicalX;
        Top = logicalY;
        Width = logicalW;
        Height = logicalH;
    }
}
