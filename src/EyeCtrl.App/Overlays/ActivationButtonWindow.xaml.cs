namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ActivationButtonWindow : OverlayWindow
{
    private const double ButtonCenter = 50.0;
    private const double ArcRadius = 45.0;

    public ActivationButtonWindow()
    {
        InitializeComponent();
        Opacity = 0;
    }

    public void ShowAt(ScreenPoint position, double offset)
    {
        var screenW = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;

        // Convert physical gaze point to DIPs
        var dipX = DpiHelper.PhysicalToLogicalX(position.X);
        var dipY = DpiHelper.PhysicalToLogicalY(position.Y);

        var dx = dipX < screenW / 2 ? 1.0 : -1.0;
        var dy = dipY < screenH / 2 ? 1.0 : -1.0;

        var buttonCenterX = dipX + dx * offset / Math.Sqrt(2);
        var buttonCenterY = dipY + dy * offset / Math.Sqrt(2);

        Left = Math.Clamp(buttonCenterX - Width / 2, 0, screenW - Width);
        Top  = Math.Clamp(buttonCenterY - Height / 2, 0, screenH - Height);

        UpdateDwellProgress(0);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public new void Hide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) => base.Hide();
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void HideImmediate()
    {
        Opacity = 0;
        base.Hide();
    }

    public void UpdateDwellProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);

        var angle = progress * 360;
        var radians = angle * Math.PI / 180;

        var endX = ButtonCenter + ArcRadius * Math.Sin(radians);
        var endY = ButtonCenter - ArcRadius * Math.Cos(radians);

        ArcSegment.Point = new Point(endX, endY);
        ArcSegment.IsLargeArc = angle > 180;

        // Reset start point if progress is ~0
        if (progress < 0.001)
        {
            ArcSegment.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
        }
    }
}
