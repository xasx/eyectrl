namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Services;

public partial class ManualSelectionGazeIndicatorWindow : OverlayWindow, IManualSelectionIndicatorOverlay
{
    /// <summary>The crosshair's bounding square in DIP; its intersection marks the anchor point.</summary>
    private const double AnchorMarkSize = 24;

    // The window must be big enough to hold the gaze dot AND the anchor mark, which can sit a full
    // hold radius away from the gaze. The dot is centred, so the window half-size is the hold
    // radius plus the dot's own half-size.
    private double _windowDiameter = 30;

    private double _gazeScreenX;
    private double _gazeScreenY;
    private double _anchorScreenX;
    private double _anchorScreenY;
    private bool _anchorVisible;

    public ManualSelectionGazeIndicatorWindow()
    {
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // don't bake the indicator into the magnified image
        InitializeComponent();
    }

    /// <summary>
    /// Configure sizes and appearance. Call once after construction or when settings change.
    /// <paramref name="holdRadiusLogical"/> sizes the window so the anchor mark stays visible when
    /// it sits a full hold radius away from the gaze.
    /// </summary>
    public void Configure(double outerDiameter, double centerDotDiameter, double strokeThickness,
        double opacity, double holdRadiusLogical)
    {
        _windowDiameter = outerDiameter + 2 * holdRadiusLogical;

        Width = _windowDiameter;
        Height = _windowDiameter;

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
        // Halo mirrors the dot's geometry but stays at Opacity=1: the §4.1.3 edge holds only at
        // full opacity, so the fill dims (per the passed opacity) and the ring does not.
        CenterDotHalo.Width = centerDotDiameter;
        CenterDotHalo.Height = centerDotDiameter;

        CenterOuterCircle();
        CenterDotElement();
        PositionAnchorMark();
    }

    /// <summary>
    /// Update indicator position (screen coordinates of indicator center).
    /// </summary>
    public void UpdatePosition(double screenX, double screenY)
    {
        _gazeScreenX = screenX;
        _gazeScreenY = screenY;

        Left = DpiHelper.PhysicalToLogicalX(screenX) - _windowDiameter / 2;
        Top = DpiHelper.PhysicalToLogicalY(screenY) - _windowDiameter / 2;

        // The anchor is a fixed screen point, so moving the window over the gaze must move the mark
        // the other way to keep it on that point.
        PositionAnchorMark();
    }

    /// <summary>
    /// Sets the held dwell anchor's mark. The anchor is a fixed screen point; the mark is drawn at
    /// the anchor-gaze offset inside this window. Pass <paramref name="visible"/> false to release.
    /// </summary>
    public void UpdateAnchor(double screenX, double screenY, bool visible)
    {
        _anchorScreenX = screenX;
        _anchorScreenY = screenY;
        _anchorVisible = visible;
        PositionAnchorMark();
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
        System.Windows.Controls.Canvas.SetLeft(OuterCircle, (_windowDiameter - OuterCircle.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(OuterCircle, (_windowDiameter - OuterCircle.Height) / 2);
        System.Windows.Controls.Canvas.SetLeft(OuterCircleHalo, (_windowDiameter - OuterCircleHalo.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(OuterCircleHalo, (_windowDiameter - OuterCircleHalo.Height) / 2);
    }

    private void CenterDotElement()
    {
        System.Windows.Controls.Canvas.SetLeft(CenterDot, (_windowDiameter - CenterDot.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(CenterDot, (_windowDiameter - CenterDot.Height) / 2);
        System.Windows.Controls.Canvas.SetLeft(CenterDotHalo, (_windowDiameter - CenterDotHalo.Width) / 2);
        System.Windows.Controls.Canvas.SetTop(CenterDotHalo, (_windowDiameter - CenterDotHalo.Height) / 2);
    }

    private void PositionAnchorMark()
    {
        var visibility = _anchorVisible ? Visibility.Visible : Visibility.Collapsed;
        AnchorMark.Visibility = visibility;
        AnchorMarkHalo.Visibility = visibility;
        if (!_anchorVisible) return;

        // Window-local DIP position of the anchor = window centre + the anchor-gaze offset.
        var centerX = _windowDiameter / 2
            + DpiHelper.PhysicalToLogicalX(_anchorScreenX) - DpiHelper.PhysicalToLogicalX(_gazeScreenX);
        var centerY = _windowDiameter / 2
            + DpiHelper.PhysicalToLogicalY(_anchorScreenY) - DpiHelper.PhysicalToLogicalY(_gazeScreenY);

        System.Windows.Controls.Canvas.SetLeft(AnchorMark, centerX - AnchorMarkSize / 2);
        System.Windows.Controls.Canvas.SetTop(AnchorMark, centerY - AnchorMarkSize / 2);
        System.Windows.Controls.Canvas.SetLeft(AnchorMarkHalo, centerX - AnchorMarkSize / 2);
        System.Windows.Controls.Canvas.SetTop(AnchorMarkHalo, centerY - AnchorMarkSize / 2);
    }
}
