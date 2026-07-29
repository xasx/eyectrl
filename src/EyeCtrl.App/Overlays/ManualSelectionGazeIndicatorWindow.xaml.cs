namespace EyeCtrl.App.Overlays;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Services;

public partial class ManualSelectionGazeIndicatorWindow : OverlayWindow
{
    private double _maxDiameter = 30;

    public ManualSelectionGazeIndicatorWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // don't bake the indicator into the magnified image
        InitializeComponent();
    }

    /// <summary>
    /// Configure sizes and appearance. Call once after construction or when settings change.
    /// </summary>
    public void Configure(double outerDiameter, double centerDotDiameter, double strokeThickness, double opacity)
    {
        _maxDiameter = outerDiameter;

        Width = outerDiameter;
        Height = outerDiameter;

        OuterCircle.Width = outerDiameter;
        OuterCircle.Height = outerDiameter;
        OuterCircle.StrokeThickness = strokeThickness;
        OuterCircle.Opacity = opacity;

        // Halo mirrors the outer circle's geometry but stays at Opacity=1: the §4.1.3
        // backdrop-independent edge holds only at full opacity, so the accent dims and
        // the halo does not. StrokeThickness is accent + 2 (1px wider each side).
        OuterCircleHalo.Width = outerDiameter;
        OuterCircleHalo.Height = outerDiameter;
        OuterCircleHalo.StrokeThickness = strokeThickness + 2;

        CenterDot.Width = centerDotDiameter;
        CenterDot.Height = centerDotDiameter;
        CenterDot.Opacity = opacity;

        CenterOuterCircle();
        CenterDotElement();
    }

    /// <summary>
    /// Update indicator position (screen coordinates of indicator center).
    /// </summary>
    public void UpdatePosition(double screenX, double screenY)
    {
        Left = DpiHelper.PhysicalToLogicalX(screenX) - _maxDiameter / 2;
        Top = DpiHelper.PhysicalToLogicalY(screenY) - _maxDiameter / 2;
    }

    /// <summary>
    /// Update the outer circle diameter (called as fixation progresses).
    /// </summary>
    public void UpdateOuterDiameter(double diameter)
    {
        OuterCircle.Width = diameter;
        OuterCircle.Height = diameter;
        OuterCircleHalo.Width = diameter;
        OuterCircleHalo.Height = diameter;
        CenterOuterCircle();
    }

    /// <summary>
    /// Animate the outer circle back to full size over the given duration.
    /// </summary>
    public void AnimateReset(double targetDiameter, int durationMs)
    {
        var anim = new DoubleAnimation
        {
            To = targetDiameter,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            FillBehavior = FillBehavior.Stop
        };
        anim.Completed += (_, _) =>
        {
            OuterCircle.Width = targetDiameter;
            OuterCircle.Height = targetDiameter;
            OuterCircleHalo.Width = targetDiameter;
            OuterCircleHalo.Height = targetDiameter;
            CenterOuterCircle();
        };
        OuterCircle.BeginAnimation(FrameworkElement.WidthProperty, anim);
        OuterCircle.BeginAnimation(FrameworkElement.HeightProperty, anim);
        OuterCircleHalo.BeginAnimation(FrameworkElement.WidthProperty, anim);
        OuterCircleHalo.BeginAnimation(FrameworkElement.HeightProperty, anim);
    }

    private void CenterOuterCircle()
    {
        System.Windows.Controls.Canvas.SetLeft(OuterCircle, (_maxDiameter - OuterCircle.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(OuterCircle, (_maxDiameter - OuterCircle.Height) / 2);
        System.Windows.Controls.Canvas.SetLeft(OuterCircleHalo, (_maxDiameter - OuterCircleHalo.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(OuterCircleHalo, (_maxDiameter - OuterCircleHalo.Height) / 2);
    }

    private void CenterDotElement()
    {
        System.Windows.Controls.Canvas.SetLeft(CenterDot, (_maxDiameter - CenterDot.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(CenterDot, (_maxDiameter - CenterDot.Height) / 2);
    }
}
