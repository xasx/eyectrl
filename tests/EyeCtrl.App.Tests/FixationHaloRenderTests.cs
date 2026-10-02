namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 8's button-halo geometry, headless and at the user's ScaleX 2.0.
///
/// <para>
/// The halo is centred on the offer's B and its outer radius never exceeds the button's hit radius —
/// ticket 4's pin-centred, fixation-radius-sized halo went red on exactly these checks. "Drawn =
/// counted" is read back from the window itself: the window positions in DIP from a physical B, and
/// the read-back converts Left/Top + half the window back to physical pixels, so a DIP/px mix-up on
/// any edge or corner goes red.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class FixationHaloRenderTests
{
    private const double EffectiveScale = 2.0;
    private static readonly double HitPx = LayoutConstants.ActivationButtonHitRadiusPx;

    [StaFact]
    public void HaloRadius_AtScaleX2_GrowsFromHalfToFullHitRadius()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), HitPx);

            // Progress 0: hit/(2·scale) = 37.5 DIP. Progress 1: hit/scale = 75 DIP.
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);
            Check.That(window.ProgressRing.Width / 2).IsCloseTo(37.5, 1e-10);

            window.UpdateDwellProgress(0.5);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(56.25, 1e-10);

            // The radius at progress 1 is the full hit radius in DIP. WPF draws the stroke inside the
            // element's bounds (review A's probe E1), so this element edge IS the stroke's outer edge
            // and lands exactly on the hit radius. Mutation: cap it (the reverted fixup-01 form) →
            // 71.5 here and the outer-edge test below red.
            window.UpdateDwellProgress(1.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(75.0, 1e-10);
            Check.That(window.ProgressRing.Width / 2).IsCloseTo(75.0, 1e-10);

            // Both bands share the window's centre: the halo is symmetric around B, and the window is
            // wide enough for the largest radius plus the halo's own stroke.
            Check.That(Canvas.GetLeft(window.ProgressHalo) + (window.ProgressHalo.Width / 2))
                .IsCloseTo(window.Width / 2, 1e-10);
            Check.That(Canvas.GetTop(window.ProgressRing) + (window.ProgressRing.Height / 2))
                .IsCloseTo(window.Height / 2, 1e-10);
            Check.That((window.ProgressHalo.Width / 2) + (window.ProgressHalo.StrokeThickness / 2))
                .IsLessOrEqualThan(window.Width / 2);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void HaloOuterEdge_NeverExceedsAndAtFullProgressEqualsTheHitRadius()
    {
        // The acceptance row at ScaleX 2.0. WPF draws the stroke inside the element's bounds, so the
        // element's edge (Width/2) IS the stroke's outer edge; at full progress it must land exactly on
        // the hit radius, and no progress may exceed it. Mutation: re-add the fixup-01 stroke cap
        // (subtract StrokeThickness/2) → 71.5 DIP at progress 1, red.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), HitPx);
            for (double p = 0.0; p <= 1.0; p += 0.1)
            {
                window.UpdateDwellProgress(p);
                double outerEdgePx = window.ProgressHalo.Width / 2 * EffectiveScale;
                Check.WithCustomMessage($"progress {p}: the halo escaped the hit radius")
                    .That(outerEdgePx).IsLessOrEqualThan(HitPx);
            }

            window.UpdateDwellProgress(1.0);
            Check.WithCustomMessage("the halo's outer edge at full progress must BE the hit radius")
                .That(window.ProgressHalo.Width / 2 * EffectiveScale).IsEqualTo(HitPx);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Halo_HasNoArcHead_OnlySymmetricEllipses()
    {
        // "No moving arc head" is a structural property of the drawing, not a number: an arc, a path
        // geometry or a figure would let the progress scalar move a mark beside the held point.
        var xaml = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Overlays/ActivationButtonWindow.xaml"));

        Check.That(xaml).Contains("x:Name=\"ProgressHalo\"");
        Check.That(xaml).Contains("Ellipse");
        Check.That(xaml).Not.Contains("ArcSegment");
        Check.That(xaml).Not.Contains("PathGeometry");
        Check.That(xaml).Not.Contains("PathFigure");
    }

    [StaTheory]
    [InlineData(0.0, 0.0)]           // top-left corner
    [InlineData(3839.0, 0.0)]        // top-right
    [InlineData(0.0, 2399.0)]        // bottom-left
    [InlineData(3839.0, 2399.0)]     // bottom-right of the user's 3840x2400 at 200 %
    [InlineData(1920.0, 1200.0)]     // mid-screen
    [InlineData(0.0, 1200.0)]        // left edge
    [InlineData(3839.0, 1200.0)]     // right edge
    [InlineData(1920.0, 0.0)]        // top edge
    [InlineData(1920.0, 2399.0)]     // bottom edge
    public void DrawnCentre_ReadBack_EqualsB_InPhysicalPixels(double x, double y)
    {
        // "Drawn = counted": the window's own Left/Top read back through the live scale is B. A DIP
        // window placed with physical px (or the reverse) moves this by whole DIP on every edge and
        // corner. No clamping — B is already clamped by the placement function.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var b = new PhysicalPoint(x, y);

        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(b, HitPx);

            var drawnX = DpiHelper.LogicalToPhysicalX(window.Left + (window.Width / 2));
            var drawnY = DpiHelper.LogicalToPhysicalY(window.Top + (window.Height / 2));
            Check.WithCustomMessage($"B=({x},{y}): the drawn centre is ({drawnX},{drawnY}), not B")
                .That(drawnX).IsCloseTo(x, 1e-6);
            Check.That(drawnY).IsCloseTo(y, 1e-6);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void Halo_IsClickThrough_AndExcludedFromCapture()
    {
        // The halo sits over the offered button, so it must never become a mouse target over whatever
        // is underneath it — the same contract the cursor ring has.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ActivationButtonWindow();
        try
        {
            Check.That(window.IsClickThrough).IsTrue();
            Check.That(window.ExcludeFromScreenCapture).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ReShow_StartsAtProgressZero_OnTheNewButton_WithoutAHeldOpacity()
    {
        // HideImmediate's Opacity write must land, and the next show must be positioned and at
        // progress 0 before it can present anything — otherwise the previous halo flashes at the new
        // point at its last radius. Pumpless reads return base values, so this asserts base state and
        // the presence/absence of a held animation rather than an animated frame.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), HitPx);
            window.UpdateDwellProgress(1.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(75.0, 1e-10);   // the full hit radius
            Check.That(window.HasAnimatedProperties).IsTrue(); // the fade-in holds Opacity

            window.HideImmediate();

            // The held fade-in is cleared (so Opacity = 0 actually takes effect) and the radius drops
            // back to the inner radius.
            Check.That(window.HasAnimatedProperties).IsFalse();
            Check.That(window.Opacity).IsEqualTo(0.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);

            var secondButton = new PhysicalPoint(300, 200);
            window.ShowAt(secondButton, HitPx);

            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);
            Check.That(window.Left + (window.Width / 2))
                .IsCloseTo(DpiHelper.PhysicalToLogicalX(secondButton.X), 1e-10);
            Check.That(window.Top + (window.Height / 2))
                .IsCloseTo(DpiHelper.PhysicalToLogicalY(secondButton.Y), 1e-10);
            // The new fade-in starts from the base value, which is still 0: nothing full-opacity can
            // present before the animation's first pumped frame.
            Check.That(window.Opacity).IsEqualTo(0.0);
        }
        finally
        {
            window.Close();
        }
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(FixationHaloRenderTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;
        Check.That(dir).IsNotNull();
        return dir!;
    }
}
