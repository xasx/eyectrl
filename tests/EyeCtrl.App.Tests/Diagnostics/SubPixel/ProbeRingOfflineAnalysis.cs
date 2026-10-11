namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using EyeCtrl.Core.Models;

/// <summary>
/// R2's offline analyser (fixup 03 item 6): re-runs the exact measurement from a saved capture
/// folder — same analysis, same expectations, same schedule checks as the live harness — so every
/// later diagnosis is headless and needs no second on-screen run.
/// </summary>
internal static class ProbeRingOfflineAnalysis
{
    /// <summary>
    /// Loads a folder written by <see cref="ProbeRingCaptureArchive.Save"/> and measures it.
    /// </summary>
    /// <param name="folder">The run folder.</param>
    /// <returns>The measured report; render it with <see cref="ProbeRingOnScreenJudge.WriteReport"/>.</returns>
    internal static ProbeRingOnScreenJudge.JudgeReport Analyse(string folder)
    {
        var archive = ProbeRingCaptureArchive.Load(folder);

        // Select by Kind, not by save order (fixup 04 F4): the assertion fractions are also sweep
        // fractions, and the diagnostic blocks carry f = 0 references of their own.
        var referenceB = archive.Samples
            .Where(sample => sample.Kind == ProbeRingOnScreenJudge.KindReference
                             && sample.Condition == ProbeRingOnScreenJudge.ConditionB
                             && sample.Fraction == 0.0)
            .OrderBy(sample => sample.Repeat)
            .First();
        var centreInCapture = new PhysicalPoint(
            referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
            referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY);

        // The expected pattern comes from the look the capture was drawn with, recorded in the
        // manifest (fixup 04 F5) — not from today's code, so a later look change cannot silently
        // change what a saved run is measured against.
        var expectedBgra = ProbeRingCaptureScene.RenderCapture(
            archive.Scale, referenceB.Look.ToSettings(), centreInCapture,
            snap: false, ProbeRingFrameSchedule.CaptureSizePx);
        var expected = ProbeRingCaptureAnalysis.ToWeights(
            expectedBgra, ProbeRingFrameSchedule.CaptureSizePx, ProbeRingFrameSchedule.CaptureSizePx,
            referenceB.Background);

        return ProbeRingOnScreenJudge.Measure(
            ProbeRingOnScreenJudge.BuildInputs(archive.Scale, expected, archive.Samples));
    }
}
