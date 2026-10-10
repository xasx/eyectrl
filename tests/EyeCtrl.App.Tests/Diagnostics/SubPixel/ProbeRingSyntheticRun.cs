namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Shared fixtures for fixup 03's archive and offline tests: a healthy synthetic run and a
/// throwaway temp folder. The samples are the same renders the analysis tests use
/// (<see cref="ProbeRingCaptureScene.SyntheticSample"/>), shaped exactly like the on-screen
/// harness's capture plan: 5 reference repeats, then A/B assertion rows and the B sweep, 3 repeats
/// each.
/// </summary>
internal static class ProbeRingSyntheticRun
{
    /// <summary>A run that should measure cleanly: every row at its scheduled fraction.</summary>
    internal static List<ProbeRingCapturedSample> Healthy(
        int referenceRepeats = ProbeRingOnScreenHarness.ReferenceRepeats,
        int rowRepeats = ProbeRingOnScreenHarness.RowRepeats)
    {
        var samples = new List<ProbeRingCapturedSample>();

        for (var repeat = 0; repeat < referenceRepeats; repeat++)
            samples.Add(ProbeRingCaptureScene.SyntheticSample(ProbeRingOnScreenJudge.KindReference, "B", 0.0, snap: false, repeat));

        samples.Add(ProbeRingCaptureScene.SyntheticSample(ProbeRingOnScreenJudge.KindReference, "A", 0.0, snap: true));

        foreach (var fraction in ProbeRingOnScreenJudge.AssertionFractions)
        {
            for (var repeat = 0; repeat < rowRepeats; repeat++)
            {
                samples.Add(ProbeRingCaptureScene.SyntheticSample(ProbeRingOnScreenJudge.KindAssertion, "A", fraction, snap: true, repeat));
                samples.Add(ProbeRingCaptureScene.SyntheticSample(ProbeRingOnScreenJudge.KindAssertion, "B", fraction, snap: false, repeat));
            }
        }

        foreach (var fraction in ProbeRingOnScreenJudge.SweepFractions)
        {
            for (var repeat = 0; repeat < rowRepeats; repeat++)
                samples.Add(ProbeRingCaptureScene.SyntheticSample(ProbeRingOnScreenJudge.KindSweep, "B", fraction, snap: false, repeat));
        }

        return samples;
    }

    /// <summary>A unique throwaway folder under %TEMP%; the caller deletes it.</summary>
    internal static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), "eyectrl-r2-test-" + Guid.NewGuid().ToString("N"));
}
