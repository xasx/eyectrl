namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using EyeCtrl.Core.Models;

/// <summary>
/// Fixup 10 item 2, headless: the background mask and the off-background check. A clean synthetic
/// capture (the ring rendered on the capture key) is never flagged; a patch painted over the
/// background outside the ring's dilated footprint is flagged with its bounds, and a patch inside
/// the ring band is protected. The thresholds are measured on the archives: run 2's 103 clean
/// frames read 0 px above every tolerance tried (10–50), run 3's overlays read 1,759–9,519 px
/// above 20 with a max channel difference of 75 (<c>ProbeRingOfflineAnalysisTests</c> opt-in
/// checks). Kills "the intrusion check off".
/// </summary>
[Collection("WpfApplication")]
public class ProbeRingIntrusionTests
{
    private static readonly PhysicalPoint Centre = new(300, 300);

    [StaFact]
    public void CleanSyntheticCaptures_AreNotFlagged()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var clean = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            clean, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        var verdict = ProbeRingIntrusion.Check(
            clean, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx, mask, background);

        Check.WithCustomMessage($"A clean frame must not be flagged; got {verdict.Pixels} px, bbox {verdict.Bounds}.")
            .That(verdict.Intruded).IsFalse();
        Check.That(verdict.Pixels).IsEqualTo(0);
    }

    [StaFact]
    public void APatchOverTheBackground_IsFlagged_WithItsBounds()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var patched = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        // A 30x20 patch at (30, 30): the ring's stroke sits at radius ~250 around (300, 300), so
        // the corner side of the frame is far outside the dilated band.
        PaintPatch(patched, fromX: 30, fromY: 30, toX: 60, toY: 50);

        var verdict = ProbeRingIntrusion.Check(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx, mask, background);

        Check.WithCustomMessage($"The patch must be flagged; got {verdict.Pixels} px.")
            .That(verdict.Intruded).IsTrue();
        Check.That(verdict.Pixels).IsEqualTo(30 * 20);
        Check.WithCustomMessage($"The bbox must cover the patch; got {verdict.Bounds}.")
            .That(verdict.Bounds).IsEqualTo("30,30,30,20");
    }

    [StaFact]
    public void APatchInsideTheRingBand_IsProtected()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var patched = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        // Straddling the ring's left edge (x = 300 - 250): inside the dilated footprint.
        PaintPatch(patched, fromX: 40, fromY: 295, toX: 60, toY: 305);

        var verdict = ProbeRingIntrusion.Check(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx, mask, background);

        Check.WithCustomMessage(
                $"Pixels inside the ring's dilated footprint are the ring's own; got {verdict.Pixels} px.")
            .That(verdict.Intruded).IsFalse();
    }

    [StaFact]
    public void ATooSmallMask_IsRejected()
    {
        var mask = new bool[4];
        var failure = (Exception?)null;
        try
        {
            ProbeRingIntrusion.Check(new byte[4 * 4], 4, 4, mask, (0, 0, 0));
        }
        catch (ArgumentException ex)
        {
            failure = ex;
        }

        Check.WithCustomMessage("A mask for the wrong frame size is a harness fault, not a capture result.")
            .That(failure).IsNotNull();
    }

    /// <summary>
    /// The session line's format, with run 3's measured numbers for its sweep f = 0.40 frame
    /// (2,768 px, bbox 390,365,210,235).
    /// </summary>
    [Fact]
    public void Line_CarriesTheStepKindCountAndBounds()
    {
        var verdict = new ProbeRingIntrusion.Verdict(true, 2768, 390, 365, 599, 599);

        Check.That(ProbeRingIntrusion.Line(12, "sweep", 0.40, verdict))
            .IsEqualTo("R2 intrusion: step 12 (sweep f=0.40) 2768 px off-background (bbox 390,365,210,235)");
    }

    private static void PaintPatch(byte[] bgra, int fromX, int fromY, int toX, int toY)
    {
        for (var y = fromY; y < toY; y++)
        {
            for (var x = fromX; x < toX; x++)
            {
                var p = (y * ProbeRingFrameSchedule.CaptureSizePx + x) * 4;
                bgra[p] = (byte)(bgra[p] / 2);
                bgra[p + 1] = 128;
                bgra[p + 2] = 255;
            }
        }
    }
}
