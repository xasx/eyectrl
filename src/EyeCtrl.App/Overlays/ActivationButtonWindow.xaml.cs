namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The fixation feedback: a symmetric halo centred on the pin, drawn where the dwell is counted.
///
/// <para>
/// Replaces the old offset activation button. That button was placed <c>CircleSize/2 + 55</c> from the
/// fixation point — about 180 DIP, or 360 physical px at ScaleX 2.0 — while its dwell target stayed at
/// the fixation point, so looking at the feedback cancelled the hold. The halo is centred on the pin,
/// which is the same point the rule measures, so looking at it IS holding (S3).
/// </para>
///
/// <para>
/// The progress scalar moves the radius of two concentric full circles; there is no arc head, so no
/// part of the feedback ever draws a moving mark beside the point the user is holding.
/// </para>
/// </summary>
public partial class ActivationButtonWindow : OverlayWindow, IActivationButtonOverlay
{
    // UI Layout Constants
    private const int FadeInDurationMs = 150; // Duration of fade-in animation
    private const int FadeOutDurationMs = 100; // Duration of fade-out animation
    private const double EdgeMarginDip = 2.0; // Slack between the halo's outer stroke edge and the window edge

    /// <summary>
    /// The fixation radius (physical px) the current halo was sized for. Set by <see cref="ShowAt"/>;
    /// 0 before the first show, when there is no geometry to draw yet.
    /// </summary>
    private double _fixationRadiusPixels;

    public ActivationButtonWindow()
    {
        // Click-through: the feedback sits exactly where the user is looking and must not become a
        // mouse target over whatever is underneath it — the same contract the cursor ring has.
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // keep out of the magnifier/preview capture
        InitializeComponent();
        Opacity = 0;
    }

    /// <summary>
    /// Centres the halo on <paramref name="position"/> (the pin: ticket 3's onset centroid) and sizes
    /// the window around the largest radius the progress scalar can reach (2R).
    ///
    /// <para>
    /// There is deliberately no screen clamping. Clamping would move the drawn centre off the pin near
    /// a screen edge, and S3 requires the centre within R of the activation dwell point; it would also
    /// throw when the window is wider than the screen (<c>Math.Clamp</c>'s minimum above its maximum).
    /// The cursor ring is centred the same way, so the two stay concentric at the edges as well.
    /// </para>
    /// </summary>
    public void ShowAt(PhysicalPoint position, double fixationRadiusPixels)
    {
        _fixationRadiusPixels = fixationRadiusPixels;

        var maxRadiusDip = FixationHaloGeometry.OuterRadiusDip(fixationRadiusPixels, DpiHelper.ScaleX);
        var halfExtentDip = maxRadiusDip + (ProgressHalo.StrokeThickness / 2) + EdgeMarginDip;
        Width = 2 * halfExtentDip;
        Height = 2 * halfExtentDip;

        Left = DpiHelper.PhysicalToLogicalX(position.X) - (Width / 2);
        Top = DpiHelper.PhysicalToLogicalY(position.Y) - (Height / 2);

        UpdateDwellProgress(0);

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInDurationMs));
        BeginAnimation(OpacityProperty, fadeIn);
    }

    public new void Hide()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutDurationMs));
        fadeOut.Completed += (_, _) =>
        {
            base.Hide();
            // Torn down for the whole show half of the cycle: drop the radius too, so nothing stale
            // can be presented if the next show is late or its first frame is not pumped.
            UpdateDwellProgress(0);
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public void HideImmediate()
    {
        // The fade-in is a HoldEnd animation, and a held animation outranks the base Opacity written
        // here — without clearing it first, the window would stay fully opaque while "hidden" and a
        // re-show could present the previous halo (its last radius included) for a frame at the new
        // gaze point. Clearing it makes Opacity = 0 take effect, and the base value is what a
        // pump-less read observes.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        UpdateDwellProgress(0);
        base.Hide();
    }

    /// <summary>
    /// Draws the halo at the radius for <paramref name="progress"/> — R/ScaleX at 0 through 2R/ScaleX
    /// at 1, both bands sharing it, centred in the window. No-op before <see cref="ShowAt"/> has
    /// supplied a radius: a progress notification can arrive from the ViewModel's show path before the
    /// window has been placed.
    /// </summary>
    public void UpdateDwellProgress(double progress)
    {
        if (_fixationRadiusPixels <= 0)
            return;

        var radiusDip = FixationHaloGeometry.RadiusDip(_fixationRadiusPixels, DpiHelper.ScaleX, progress);
        ApplyRadius(ProgressHalo, radiusDip);
        ApplyRadius(ProgressRing, radiusDip);
    }

    private void ApplyRadius(FrameworkElement band, double radiusDip)
    {
        band.Width = 2 * radiusDip;
        band.Height = 2 * radiusDip;
        Canvas.SetLeft(band, (Width / 2) - radiusDip);
        Canvas.SetTop(band, (Height / 2) - radiusDip);
    }
}
