namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

public partial class ActivationButtonWindow : OverlayWindow, IActivationButtonOverlay
{
    // UI Layout Constants
    private const double ButtonCenter = 50.0; // Center coordinate within the button canvas
    private const double ArcRadius = 45.0; // Radius of the dwell progress arc
    private const int FadeInDurationMs = 150; // Duration of fade-in animation
    private const int FadeOutDurationMs = 100; // Duration of fade-out animation
    private const double ProgressResetThreshold = 0.001; // Threshold below which progress is considered zero

    public ActivationButtonWindow()
    {
        InitializeComponent();
        ExcludeFromScreenCapture = true; // keep out of the magnifier/preview capture
        Opacity = 0;
    }

    public void ShowAt(PhysicalPoint position, double offset)
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

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInDurationMs));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public new void Hide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutDurationMs));
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

        if (progress < ProgressResetThreshold)
        {
            ArcSegment.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
            ArcSegment.IsLargeArc = false;
            ArcSegmentHalo.Point = new Point(ButtonCenter, ButtonCenter - ArcRadius);
            ArcSegmentHalo.IsLargeArc = false;
            return;
        }

        DwellArcButton.UpdateArc(ArcSegment, new Point(ButtonCenter, ButtonCenter), ArcRadius, progress);
        DwellArcButton.UpdateArc(ArcSegmentHalo, new Point(ButtonCenter, ButtonCenter), ArcRadius, progress);
    }
}
