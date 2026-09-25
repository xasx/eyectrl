namespace EyeCtrl.App.Tests;

using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using NFluent;
using Xunit;

/// <summary>
/// Ticket 4's display geometry, headless and at the user's ScaleX 2.0.
///
/// <para>
/// The halo must be drawn where the dwell is counted (S3). On the pre-fix code the feedback was drawn
/// <c>CircleSize/2 + 55</c> DIP from the fixation point — about 180 DIP, or 360 physical px at 2.0 —
/// so the checks below go red on the offset placement. The window is sized/positioned in DIP from the
/// rule's PHYSICAL radius, and the drawn centre is read back through an off-screen
/// <see cref="HwndSource"/> whose <c>LayoutTransform</c> makes one DIP two device pixels (the
/// <see cref="ToolbarPausedTargetAlignmentTests"/> technique), so "drawn" means the same thing the
/// window manager would see. Nothing is ever shown on screen.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class FixationHaloRenderTests
{
    private const double EffectiveScale = 2.0;
    private const double RingSizeDip = 250;
    private const double FixationRadiusPx = 75;

    /// <summary>DIP shift keeping every window coordinate positive inside the off-screen source.</summary>
    private const double HostShiftDip = 4000;

    [StaFact]
    public void HaloRadius_AtScaleX2_GrowsFrom37Point5To75Dip()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), FixationRadiusPx);

            // Progress 0: the hold radius R/ScaleX = 37.5 DIP. Progress 1: 2R/ScaleX = 75 DIP.
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);
            Check.That(window.ProgressRing.Width / 2).IsCloseTo(37.5, 1e-10);

            window.UpdateDwellProgress(0.5);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(56.25, 1e-10);

            window.UpdateDwellProgress(1.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(75.0, 1e-10);
            Check.That(window.ProgressRing.Width / 2).IsCloseTo(75.0, 1e-10);

            // Both bands share the window's centre: the halo is symmetric around the pin, and the
            // window is wide enough for the largest radius plus the halo's own stroke.
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
    [InlineData(1200.0, 800.0)]     // mid-screen
    [InlineData(10.0, 10.0)]        // top-left corner: the old placement clamped here
    [InlineData(3830.0, 2390.0)]    // bottom-right corner of the user's 3840x2400 at 200 %
    public void RingAndHalo_DrawnCentresCoincide_AndSitOnThePin(double x, double y)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var pin = new PhysicalPoint(x, y);

        var ring = new CursorCircleWindow();
        ring.UpdateAppearance(new CursorSettings { CircleSize = RingSizeDip });
        ring.UpdatePosition(pin);
        var halo = new ActivationButtonWindow();
        halo.ShowAt(pin, FixationRadiusPx);
        halo.UpdateDwellProgress(0.5);

        using var source = new HwndSource(new HwndSourceParameters("ticket4-halo-concentricity")
        {
            // WS_POPUP, parked off-screen, 1x1: the contents are hosted for layout and
            // PointToScreen only. Never shown to anyone.
            WindowStyle = unchecked((int)0x80000000),
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var factor = EffectiveScale / source.CompositionTarget!.TransformToDevice.M11;
        var root = new Canvas { Width = 8000, Height = 5000 };
        root.LayoutTransform = new ScaleTransform(factor, factor);

        var ringContent = Detach(ring, root, HostShiftDip + ring.Left, HostShiftDip + ring.Top);
        var haloContent = Detach(halo, root, HostShiftDip + halo.Left, HostShiftDip + halo.Top);
        source.RootVisual = root;
        Layout(root);

        var ringCentre = ringContent.PointToScreen(new Point(ring.Width / 2, ring.Height / 2));
        var haloCentre = haloContent.PointToScreen(new Point(halo.Width / 2, halo.Height / 2));

        var separation = Math.Sqrt(
            Math.Pow(ringCentre.X - haloCentre.X, 2) + Math.Pow(ringCentre.Y - haloCentre.Y, 2));
        Check.WithCustomMessage(
                $"pin={pin}: the pinned ring and the halo must be concentric; their drawn centres "
                + $"differ by {separation:F2} device px.")
            .That(separation).IsStrictlyLessThan(0.5);

        // S3: the drawn centre is the activation dwell point, in physical pixels. Clamping the halo
        // (as the old button's ShowAt did) or re-adding the offset moves this by whole DIP.
        var rootOrigin = root.PointToScreen(new Point(0, 0));
        var expectedX = (HostShiftDip + (pin.X / EffectiveScale)) * EffectiveScale;
        var expectedY = (HostShiftDip + (pin.Y / EffectiveScale)) * EffectiveScale;
        Check.WithCustomMessage($"pin={pin}: the halo's drawn centre left the dwell point")
            .That(haloCentre.X - rootOrigin.X).IsCloseTo(expectedX, 0.5);
        Check.That(haloCentre.Y - rootOrigin.Y).IsCloseTo(expectedY, 0.5);
        Check.WithCustomMessage($"pin={pin}: the ring's drawn centre left the dwell point")
            .That(ringCentre.X - rootOrigin.X).IsCloseTo(expectedX, 0.5);
        Check.That(ringCentre.Y - rootOrigin.Y).IsCloseTo(expectedY, 0.5);

        ring.Close();
        halo.Close();
    }

    [StaFact]
    public void Halo_IsClickThrough_AndExcludedFromCapture()
    {
        // A-F2: the halo sits exactly where the user is looking, so it must never become a mouse target
        // over whatever is underneath it — the same contract the cursor ring has.
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
    public void ReShow_StartsAtProgressZero_OnTheNewPin_WithoutAHeldOpacity()
    {
        // A-F4. HideImmediate's Opacity write must land, and the next show must be positioned and at
        // progress 0 before it can present anything — otherwise the previous halo flashes at the new
        // gaze point at its last radius. Pumpless reads return base values, so this asserts base state
        // and the presence/absence of a held animation rather than an animated frame.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(EffectiveScale, EffectiveScale);
        var window = new ActivationButtonWindow();
        try
        {
            window.ShowAt(new PhysicalPoint(1000, 800), FixationRadiusPx);
            window.UpdateDwellProgress(1.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(75.0, 1e-10);
            Check.That(window.HasAnimatedProperties).IsTrue(); // the fade-in holds Opacity

            window.HideImmediate();

            // The held fade-in is cleared (so Opacity = 0 actually takes effect) and the radius drops
            // back to the inner radius.
            Check.That(window.HasAnimatedProperties).IsFalse();
            Check.That(window.Opacity).IsEqualTo(0.0);
            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);

            var secondPin = new PhysicalPoint(300, 200);
            window.ShowAt(secondPin, FixationRadiusPx);

            Check.That(window.ProgressHalo.Width / 2).IsCloseTo(37.5, 1e-10);
            Check.That(window.Left + (window.Width / 2))
                .IsCloseTo(DpiHelper.PhysicalToLogicalX(secondPin.X), 1e-10);
            Check.That(window.Top + (window.Height / 2))
                .IsCloseTo(DpiHelper.PhysicalToLogicalY(secondPin.Y), 1e-10);
            // The new fade-in starts from the base value, which is still 0: nothing full-opacity can
            // present before the animation's first pumped frame.
            Check.That(window.Opacity).IsEqualTo(0.0);
        }
        finally
        {
            window.Close();
        }
    }

    private static FrameworkElement Detach(Window window, Canvas root, double left, double top)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        content.Width = window.Width;
        content.Height = window.Height;
        Canvas.SetLeft(content, left);
        Canvas.SetTop(content, top);
        root.Children.Add(content);
        return content;
    }

    private static void Layout(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();
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
