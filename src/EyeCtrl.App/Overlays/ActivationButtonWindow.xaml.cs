namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;

/// <summary>
/// The activation feedback: a symmetric halo centred on the offered button's centre B, drawn where
/// the dwell is counted.
///
/// <para>
/// Since ticket 8 the halo is on the button — the one action that fires — and its outer radius never
/// exceeds the button's hit radius, so no drawn feedback lies outside the counted area. Ticket 4's
/// version was centred on the fixation point and sized from the fixation radius; the ring now owns
/// the hold's marking, and this window only says how far the button's own dwell has come.
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
    /// The hit radius (physical px) the current halo was sized for. Set by <see cref="ShowAt"/>;
    /// 0 before the first show, when there is no geometry to draw yet.
    /// </summary>
    private double _hitRadiusPixels;

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
    /// Centres the halo on <paramref name="buttonCenter"/> — the offer's B, in physical pixels — and
    /// sizes the window around the largest radius the progress scalar can reach (the hit radius).
    ///
    /// <para>
    /// There is deliberately no screen clamping: B is already clamped on screen by the placement
    /// function, and clamping again here would move the drawn centre off the counted point and break
    /// "drawn = counted" at the edges. The cursor ring is centred the same way.
    /// </para>
    /// </summary>
    public void ShowAt(PhysicalPoint buttonCenter, double hitRadiusPixels)
    {
        _hitRadiusPixels = hitRadiusPixels;

        var maxRadiusDip = ActivationButtonHaloGeometry.OuterRadiusDip(hitRadiusPixels, DpiHelper.ScaleX);
        var halfExtentDip = maxRadiusDip + (ProgressHalo.StrokeThickness / 2) + EdgeMarginDip;
        Width = 2 * halfExtentDip;
        Height = 2 * halfExtentDip;

        Left = DpiHelper.PhysicalToLogicalX(buttonCenter.X) - (Width / 2);
        Top = DpiHelper.PhysicalToLogicalY(buttonCenter.Y) - (Height / 2);

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
    /// Draws the halo at the radius for <paramref name="progress"/> — half the hit radius at 0 through
    /// the full hit radius at 1, in physical px divided by the live scale, both bands sharing it,
    /// centred in the window. No-op before <see cref="ShowAt"/> has supplied a radius: a progress
    /// notification can arrive from the ViewModel's show path before the window has been placed.
    /// </summary>
    public void UpdateDwellProgress(double progress)
    {
        if (_hitRadiusPixels <= 0)
            return;

        var radiusDip = ActivationButtonHaloGeometry.RadiusDip(_hitRadiusPixels, DpiHelper.ScaleX, progress);
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
