namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;

public partial class ActivationButtonWindow : OverlayWindow
{
    private const double ButtonCenter = 50.0;
    private const double ArcRadius = 45.0;

    public ActivationButtonWindow()
    {
        InitializeComponent();
        Opacity = 0;
    }

    public void ShowAt(ScreenPoint position, double verticalOffset = 180)
    {
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        var screenH = System.Windows.SystemParameters.PrimaryScreenHeight;
        Left = Math.Clamp(position.X - Width / 2, 0, screenW - Width);
        Top  = Math.Clamp(position.Y - Height / 2 - verticalOffset, 0, screenH - Height);

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
