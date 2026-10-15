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

    private static ProbeRingCaptureAnalysis.RingScanGeometry Geometry =>
        ProbeRingCaptureAnalysis.RingScanGeometry.For(
            Centre, ProbeRingCaptureScene.RingSizeDip * ProbeRingCaptureAnalysis.OwnerScale / 2.0);

    /// <summary>
    /// Fixup 11 item 2: the faint in-window overlay review 08 measured — a stripe at Δ12 in the
    /// right scan window — biased the judged X by ~1.6 px yet stayed below the 20/200 rule. Inside
    /// the judged scan windows the strict rule (Δ &gt; 4, ≥ 5 px) must flag it. The stripe also
    /// extends outside the windows, where Δ12 is below the loose rule, so only the strict rule can
    /// catch it.
    /// </summary>
    [StaFact]
    public void AFaintInWindowStripe_IsFlagged_ByTheStrictRule()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var patched = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        // The right scan window: x in [530, 570], y in [290, 310] (21 rows around the scan row).
        // The stripe (8 x 60 at Δ12) crosses it; outside the window its Δ12 is under the loose rule.
        PaintColour(patched, fromX: 540, fromY: 285, toX: 548, toY: 345, b: 255, g: 12, r: 255);

        var verdict = ProbeRingIntrusion.Check(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx,
            mask, background, Geometry);

        Check.WithCustomMessage(
                $"The strict rule must flag the faint in-window stripe; got strict={verdict.StrictPixels}, "
                + $"loose={verdict.LoosePixels}.")
            .That(verdict.Intruded).IsTrue();
        Check.That(verdict.StrictPixels).IsGreaterOrEqualThan(ProbeRingIntrusion.StrictMinimumPixels);
        Check.WithCustomMessage("The stripe's out-of-window part is Δ12: below the loose rule, so it must not count.")
            .That(verdict.LoosePixels).IsEqualTo(0);
    }

    /// <summary>
    /// The converse control: the same faint stripe away from the scan windows is harmless and stays
    /// unflagged (Δ12 &lt; the loose tolerance), so the strict rule is what makes the difference.
    /// </summary>
    [StaFact]
    public void AFaintStripeOutsideTheWindows_IsNotFlagged()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var patched = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        PaintColour(patched, fromX: 100, fromY: 100, toX: 108, toY: 160, b: 255, g: 12, r: 255);

        var verdict = ProbeRingIntrusion.Check(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx,
            mask, background, Geometry);

        Check.WithCustomMessage(
                $"Far from the judged windows, Δ12 is below the loose rule; got strict={verdict.StrictPixels}, "
                + $"loose={verdict.LoosePixels}.")
            .That(verdict.Intruded).IsFalse();
        Check.That(verdict.Pixels).IsEqualTo(0);
    }

    /// <summary>
    /// Fixup 11 P1 (review 08 M1): run 3's real overlay blends — (253,75,253) light and
    /// (183,5,183) dark over the (255,0,255) key — must be flagged at the shipped tolerance. A
    /// regression to 80 would not see them (max deltas 75 and 72). The stripes sit outside the scan
    /// windows, so the loose rule is what must catch them.
    /// </summary>
    [StaFact]
    public void Run3sRealOverlayBlends_AreFlagged_AtTheShippedTolerance()
    {
        var settings = ProbeRingCaptureScene.OwnerLook();
        var mask = ProbeRingIntrusion.BackgroundMask(settings, Centre, ProbeRingCaptureAnalysis.OwnerScale);
        var patched = ProbeRingCaptureScene.RenderCapture(
            2.0, settings, Centre, snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var background = ProbeRingCaptureAnalysis.MeasureBackground(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx);

        // (253, 75, 253): magenta blended ~30 % with white; (183, 5, 183): ~28 % with black.
        PaintColour(patched, fromX: 40, fromY: 40, toX: 50, toY: 100, b: 253, g: 75, r: 253);
        PaintColour(patched, fromX: 70, fromY: 40, toX: 80, toY: 100, b: 183, g: 5, r: 183);

        var verdict = ProbeRingIntrusion.Check(
            patched, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx,
            mask, background, Geometry);

        Check.WithCustomMessage(
                $"Run 3's overlay blends (max deltas 75 and 72) must be flagged at tolerance "
                + $"{ProbeRingIntrusion.ChannelTolerance}; got loose={verdict.LoosePixels}.")
            .That(verdict.Intruded).IsTrue();
        Check.That(verdict.LoosePixels).IsGreaterOrEqualThan(600);
    }

    private static void PaintColour(byte[] bgra, int fromX, int fromY, int toX, int toY, byte b, byte g, byte r)
    {
        for (var y = fromY; y < toY; y++)
        {
            for (var x = fromX; x < toX; x++)
            {
                var p = (y * ProbeRingFrameSchedule.CaptureSizePx + x) * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
            }
        }
    }

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
