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

        var referenceB = archive.Samples
            .Where(sample => sample.Condition == ProbeRingOnScreenJudge.ConditionB && sample.Fraction == 0.0)
            .OrderBy(sample => sample.Repeat)
            .First();
        var centreInCapture = new PhysicalPoint(
            referenceB.Frame.CaptureCentrePx.X - referenceB.Frame.CaptureOriginX,
            referenceB.Frame.CaptureCentrePx.Y - referenceB.Frame.CaptureOriginY);

        // The expected pattern is deterministic: the same look, rendered at the same reference
        // geometry the live run rendered it at, converted through the captured background.
        var expected = ProbeRingCaptureScene.ExpectedWeightsAt(centreInCapture, referenceB.Background, archive.Scale);

        return ProbeRingOnScreenJudge.Measure(
            ProbeRingOnScreenJudge.BuildInputs(archive.Scale, expected, archive.Samples));
    }
}
