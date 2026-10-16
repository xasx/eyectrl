namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using EyeCtrl.Core.Models;

/// <summary>
/// Review 01's two blockers, pinned headlessly: B1 — the ring must be placed in <b>window-local</b>
/// pixels while the capture is taken in <b>screen</b> pixels — and B2 — the capture origin must be
/// production's truncating math (<c>CaptureRegionMath.Compute</c>), constant across every fraction.
/// Both lived in harness code no headless test executed, and each one alone made the on-screen run a
/// guaranteed red on a display that works.
///
/// <para>
/// The owner's geometry throughout: a 320-DIP window at (800, 440) DIP — where the harness centres it
/// on the 1920×1200-DIP primary — at scale 2.0 on a 3840×2400-pixel screen. Everything here is a
/// pure calculation; no window is created.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingFrameScheduleTests
{
    private const double OwnerScale = 2.0;
    private const double WindowLeftDip = 800.0;
    private const double WindowTopDip = 440.0;
    private const int ScreenWidthPx = 3840;
    private const int ScreenHeightPx = 2400;

    /// <summary>The window's physical side: 320 DIP at scale 2.0.</summary>
    private const double WindowPx = ProbeRingFrameSchedule.WindowDip * OwnerScale;

    /// <summary>The ring's physical radius: 250 DIP at scale 2.0, halved.</summary>
    private const double HalfPx = ProbeRingCaptureScene.RingSizeDip * OwnerScale / 2.0;

    private static ProbeRingFrameSchedule.Frame OwnerFrame(double fraction) =>
        ProbeRingFrameSchedule.For(WindowLeftDip, WindowTopDip, OwnerScale, fraction, ScreenWidthPx, ScreenHeightPx);

    [Fact]
    public void AtTheOwnerGeometry_TheRingLiesInsideTheWindow_AtItsLocalPosition()
    {
        foreach (var fraction in new[] { 0.0, 0.25, 0.5, 0.55, 0.75, 0.95 })
        {
            var frame = OwnerFrame(fraction);

            // B1: the ring's placement centre is window-local — the window centre plus the fraction.
            Check.WithCustomMessage(
                    $"The window origin at {WindowLeftDip}/{WindowTopDip} DIP and scale {OwnerScale} must be "
                    + "(1600, 880) px so every placement is on whole pixels.")
                .That(frame.WindowOriginPx).IsEqualTo(new PhysicalPoint(1600, 880));
            Check.WithCustomMessage($"The ring's local centre x for f = {fraction} is wrong.")
                .That(frame.RingLocalCentrePx.X).IsCloseTo(WindowPx / 2.0 + fraction, 1e-9);
            Check.WithCustomMessage($"The ring's local centre y for f = {fraction} is wrong.")
                .That(frame.RingLocalCentrePx.Y).IsCloseTo(WindowPx / 2.0 + fraction, 1e-9);

            // ... and the capture centre is that plus the window origin: the other space entirely.
            Check.WithCustomMessage(
                    $"The capture centre for f = {fraction} must be the window origin plus the local centre; "
                    + "passing the local centre (or the screen centre) to the wrong consumer is blocker B1.")
                .That(frame.CaptureCentrePx.X)
                .IsCloseTo(frame.WindowOriginPx.X + frame.RingLocalCentrePx.X, 1e-9);
            Check.That(frame.CaptureCentrePx.Y)
                .IsCloseTo(frame.WindowOriginPx.Y + frame.RingLocalCentrePx.Y, 1e-9);

            // The ring, and then the capture, must lie fully inside the window.
            Check.WithCustomMessage($"The ring's left edge escaped the window at f = {fraction}.")
                .That(frame.RingLocalCentrePx.X - HalfPx).IsGreaterOrEqualThan(0.0);
            Check.WithCustomMessage($"The ring's right edge escaped the window at f = {fraction}.")
                .That(frame.RingLocalCentrePx.X + HalfPx).IsLessOrEqualThan(WindowPx);
            Check.WithCustomMessage($"The ring's top edge escaped the window at f = {fraction}.")
                .That(frame.RingLocalCentrePx.Y - HalfPx).IsGreaterOrEqualThan(0.0);
            Check.WithCustomMessage($"The ring's bottom edge escaped the window at f = {fraction}.")
                .That(frame.RingLocalCentrePx.Y + HalfPx).IsLessOrEqualThan(WindowPx);

            Check.WithCustomMessage($"The capture's left edge escaped the window at f = {fraction}.")
                .That(frame.CaptureOriginX).IsGreaterOrEqualThan((int)frame.WindowOriginPx.X);
            Check.WithCustomMessage($"The capture's right edge escaped the window at f = {fraction}.")
                .That(frame.CaptureOriginX + ProbeRingFrameSchedule.CaptureSizePx)
                .IsLessOrEqualThan((int)(frame.WindowOriginPx.X + WindowPx));
            Check.WithCustomMessage($"The capture's top edge escaped the window at f = {fraction}.")
                .That(frame.CaptureOriginY).IsGreaterOrEqualThan((int)frame.WindowOriginPx.Y);
            Check.WithCustomMessage($"The capture's bottom edge escaped the window at f = {fraction}.")
                .That(frame.CaptureOriginY + ProbeRingFrameSchedule.CaptureSizePx)
                .IsLessOrEqualThan((int)(frame.WindowOriginPx.Y + WindowPx));
        }
    }

    /// <summary>
    /// B2: the origin is production's truncation, and it is one constant across the whole run.
    /// The f = 0.55…0.95 cases are the ones the round-based prediction this replaced mis-predicted
    /// (1621 instead of 1620), so each is asserted individually.
    /// </summary>
    [Fact]
    public void TheCaptureOrigin_UsesProductionTruncation_AndIsConstantAcrossTheRun()
    {
        var reference = OwnerFrame(0.0);
        Check.WithCustomMessage("The owner geometry's reference capture origin must be (1620, 900).")
            .That(reference.CaptureOriginX).IsEqualTo(1620);
        Check.That(reference.CaptureOriginY).IsEqualTo(900);

        foreach (var fraction in new[] { 0.0, 0.25, 0.5, 0.55, 0.6, 0.75, 0.95 })
        {
            var frame = OwnerFrame(fraction);

            // Independent of the implementation: production truncates (int)center.X, it does not round.
            var truncatedX = (int)frame.CaptureCentrePx.X - ProbeRingFrameSchedule.CaptureRadiusPx;
            var truncatedY = (int)frame.CaptureCentrePx.Y - ProbeRingFrameSchedule.CaptureRadiusPx;
            Check.WithCustomMessage(
                    $"At f = {fraction} the capture origin x is {frame.CaptureOriginX}; production truncation "
                    + $"((int){frame.CaptureCentrePx.X} - {ProbeRingFrameSchedule.CaptureRadiusPx}) says {truncatedX}. "
                    + "A round-based prediction is one pixel too far right from f = 0.55 (review 01 B2).")
                .That(frame.CaptureOriginX).IsEqualTo(truncatedX);
            Check.WithCustomMessage($"At f = {fraction} the capture origin y is wrong.")
                .That(frame.CaptureOriginY).IsEqualTo(truncatedY);

            Check.WithCustomMessage(
                    $"The capture origin moved between f = 0 and f = {fraction}; the shift measurement "
                    + "assumes one constant origin for every frame.")
                .That(frame.CaptureOriginX).IsEqualTo(reference.CaptureOriginX);
            Check.WithCustomMessage(
                    $"The capture origin y moved between f = 0 and f = {fraction}.")
                .That(frame.CaptureOriginY).IsEqualTo(reference.CaptureOriginY);
        }
    }

    [Fact]
    public void AFractionalPhysicalWindowOrigin_IsRejected()
    {
        // 800.3 DIP at scale 1.5 is 1200.45 px: not a whole pixel, so every placement would sit half
        // a pixel off the arithmetic the control checks against.
        var threw = false;
        try
        {
            ProbeRingFrameSchedule.For(800.3, 440.0, 1.5, 0.0, ScreenWidthPx, ScreenHeightPx);
        }
        catch (ArgumentException)
        {
            threw = true;
        }

        Check.WithCustomMessage(
                "A window whose physical origin is not whole pixels must be rejected, not silently placed "
                + "off the control's arithmetic.")
            .That(threw).IsTrue();
    }

    [Fact]
    public void AWholePixelPhysicalOrigin_IsAccepted_AtAnyScale()
    {
        // (800, 440) at 2.0 is whole, and so is (801, 440) at 1.0.
        var atTwo = ProbeRingFrameSchedule.For(800, 440, 2.0, 0.0, ScreenWidthPx, ScreenHeightPx);
        Check.That(atTwo.WindowOriginPx).IsEqualTo(new PhysicalPoint(1600, 880));

        var atOne = ProbeRingFrameSchedule.For(801, 440, 1.0, 0.0, ScreenWidthPx, ScreenHeightPx);
        Check.That(atOne.WindowOriginPx).IsEqualTo(new PhysicalPoint(801, 440));
    }
}
