namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Models;

/// <summary>
/// R2's on-screen geometry as a pure function: where the ring goes inside the harness window and
/// where the capture of it is taken, for one scheduled sub-pixel fraction.
///
/// <para>
/// <b>Two coordinate spaces, and they are not the same.</b>
/// <see cref="ProbeRingVisual.Place"/> takes <b>window-local</b> physical pixels (it sets a
/// translate transform relative to its parent canvas); <see cref="ScreenCaptureService.CaptureRegion"/>
/// takes <b>screen</b> physical pixels. Production only gets away with a single space because its
/// probe window sits at (0, 0); the harness window is centred instead, so a screen-pixel centre
/// passed to <c>Place</c> draws the ring outside the window (review 01 B1). This function returns
/// both: <see cref="Frame.RingLocalCentrePx"/> for placement and
/// <see cref="Frame.CaptureCentrePx"/> for the capture.
/// </para>
///
/// <para>
/// <b>The capture origin comes from production's own math.</b> <see cref="CaptureRegionMath.Compute"/>
/// truncates (<c>(int)center.X</c>) where a hand-rolled <c>Math.Round</c> predicts one pixel too far
/// right for f ≥ 0.55 (review 01 B2). Under truncation the origin is constant for every
/// f ∈ [0, 1) — which is why the shift measurement can ignore the frame's origin — and that
/// constancy is asserted here and re-checked against every real capture by the harness.
/// </para>
///
/// <para>
/// <b>The window must sit on a whole physical pixel.</b> <c>Window.Left/Top</c> are DIPs; their
/// product with the scale is the physical origin, and a fractional one would put every placement
/// half a pixel off the arithmetic the control checks against. <see cref="For"/> rejects it.
/// </para>
/// </summary>
internal static class ProbeRingFrameSchedule
{
    /// <summary>The harness window's side, in DIPs: the 250-DIP ring plus margin on every side.</summary>
    internal const double WindowDip = 320.0;

    /// <summary>The capture's half-size in pixels: the 250 px stroke radius plus the scan windows.</summary>
    internal const int CaptureRadiusPx = 300;

    /// <summary>The capture's side in pixels.</summary>
    internal const int CaptureSizePx = 2 * CaptureRadiusPx;

    /// <summary>One scheduled frame: the ring's local centre, the capture's screen centre, and the capture's origin.</summary>
    /// <param name="Fraction">The scheduled sub-pixel fraction, in pixels, on both axes.</param>
    /// <param name="WindowOriginPx">The window's client origin in screen physical pixels (whole pixels).</param>
    /// <param name="RingLocalCentrePx">The ring's centre in window-local physical pixels — what <c>Place</c> takes.</param>
    /// <param name="CaptureCentrePx">The ring's centre in screen physical pixels — what the capture takes.</param>
    /// <param name="CaptureOriginX">The capture region's left edge, from <see cref="CaptureRegionMath.Compute"/>.</param>
    /// <param name="CaptureOriginY">The capture region's top edge, from <see cref="CaptureRegionMath.Compute"/>.</param>
    internal sealed record Frame(
        double Fraction,
        PhysicalPoint WindowOriginPx,
        PhysicalPoint RingLocalCentrePx,
        PhysicalPoint CaptureCentrePx,
        int CaptureOriginX,
        int CaptureOriginY);

    /// <summary>
    /// Schedules one frame for a window whose client origin is <paramref name="windowLeftDip"/> /
    /// <paramref name="windowTopDip"/> DIPs at <paramref name="scale"/> device pixels per DIP, with
    /// the same fraction on both axes.
    /// </summary>
    /// <param name="windowLeftDip">The window's left edge, in DIPs (as <c>Window.Left</c> reports it).</param>
    /// <param name="windowTopDip">The window's top edge, in DIPs (as <c>Window.Top</c> reports it).</param>
    /// <param name="scale">The window's measured device pixels per DIP.</param>
    /// <param name="fraction">The scheduled sub-pixel fraction in [0, 1), applied to both axes.</param>
    /// <param name="screenWidthPx">The primary screen's width in physical pixels.</param>
    /// <param name="screenHeightPx">The primary screen's height in physical pixels.</param>
    internal static Frame For(
        double windowLeftDip,
        double windowTopDip,
        double scale,
        double fraction,
        int screenWidthPx,
        int screenHeightPx) =>
        For(windowLeftDip, windowTopDip, scale, fraction, fraction, screenWidthPx, screenHeightPx);

    /// <summary>
    /// Schedules one frame with independent per-axis fractions (fixup 04's X-only/Y-only
    /// diagnostics need the axes decoupled).
    /// </summary>
    /// <param name="windowLeftDip">The window's left edge, in DIPs (as <c>Window.Left</c> reports it).</param>
    /// <param name="windowTopDip">The window's top edge, in DIPs (as <c>Window.Top</c> reports it).</param>
    /// <param name="scale">The window's measured device pixels per DIP.</param>
    /// <param name="fractionX">The scheduled sub-pixel fraction in [0, 1) on the x axis.</param>
    /// <param name="fractionY">The scheduled sub-pixel fraction in [0, 1) on the y axis.</param>
    /// <param name="screenWidthPx">The primary screen's width in physical pixels.</param>
    /// <param name="screenHeightPx">The primary screen's height in physical pixels.</param>
    internal static Frame For(
        double windowLeftDip,
        double windowTopDip,
        double scale,
        double fractionX,
        double fractionY,
        int screenWidthPx,
        int screenHeightPx)
    {
        if (scale <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be positive.");
        if (fractionX is < 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(fractionX), fractionX, "Fraction must be in [0, 1).");
        if (fractionY is < 0.0 or >= 1.0)
            throw new ArgumentOutOfRangeException(nameof(fractionY), fractionY, "Fraction must be in [0, 1).");

        var originX = windowLeftDip * scale;
        var originY = windowTopDip * scale;
        if (originX != Math.Floor(originX) || originY != Math.Floor(originY))
        {
            throw new ArgumentException(
                $"The window's physical origin must be whole pixels; ({originX}, {originY}) is not. "
                + "A fractional origin puts every placement and every capture half a pixel off the "
                + "arithmetic the positive control checks against.",
                nameof(windowLeftDip));
        }

        var halfDip = WindowDip / 2.0;
        var local = new PhysicalPoint(halfDip * scale + fractionX, halfDip * scale + fractionY);
        var captureCentre = new PhysicalPoint(originX + local.X, originY + local.Y);

        var region = CaptureRegionMath.Compute(
                (int)captureCentre.X, (int)captureCentre.Y, CaptureRadiusPx, screenWidthPx, screenHeightPx)
            ?? throw new InvalidOperationException(
                $"No valid capture region for centre ({captureCentre.X}, {captureCentre.Y}) and radius {CaptureRadiusPx}.");

        return new Frame(
            fractionX,
            new PhysicalPoint(originX, originY),
            local,
            captureCentre,
            region.x,
            region.y);
    }
}
