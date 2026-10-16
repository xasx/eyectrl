namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Shared fixtures for the archive, offline and plan tests: synthetic runs and a throwaway temp
/// folder. <see cref="Healthy"/> mirrors the on-screen capture plan step for step — the same kinds,
/// orders and looks — so the offline and live paths are exercised over the same shape the owner's
/// run will produce.
/// </summary>
internal static class ProbeRingSyntheticRun
{
    /// <summary>The full run, rendered from <see cref="ProbeRingCapturePlan.Build"/> itself.</summary>
    internal static List<ProbeRingCapturedSample> Healthy()
    {
        var samples = new List<ProbeRingCapturedSample>();
        var elapsed = 0L;

        foreach (var step in ProbeRingCapturePlan.Build())
        {
            elapsed += 40;
            samples.Add(ProbeRingCaptureScene.SyntheticSample(
                step.Kind, step.Condition, step.FractionX, step.FractionY, step.Snap, step.Repeat,
                look: step.Look, elapsedMs: elapsed));
        }

        return samples;
    }

    /// <summary>A minimal run — one repeat per row, no diagnostics — for tests that only need a valid archive.</summary>
    internal static List<ProbeRingCapturedSample> Small()
    {
        var samples = new List<ProbeRingCapturedSample>();
        var elapsed = 0L;

        void Add(string kind, string condition, double fraction, bool snap)
        {
            elapsed += 40;
            samples.Add(ProbeRingCaptureScene.SyntheticSample(
                kind, condition, fraction, snap: snap, elapsedMs: elapsed));
        }

        Add(ProbeRingOnScreenJudge.KindReference, "B", 0.0, snap: false);

        // The two f = 0 references are identical placements, so they must not be adjacent — the plan
        // interleaves them too — or MarkStale would (correctly) call the second one a stale copy
        // and the reference-clean check (fixup 11 R1) would fail every later measurement as void.
        Add(ProbeRingOnScreenJudge.KindSweep, "B", 0.05, snap: false);
        Add(ProbeRingOnScreenJudge.KindReference, "A", 0.0, snap: true);

        foreach (var fraction in ProbeRingOnScreenJudge.AssertionFractions)
        {
            Add(ProbeRingOnScreenJudge.KindAssertion, "A", fraction, snap: true);
            Add(ProbeRingOnScreenJudge.KindAssertion, "B", fraction, snap: false);
        }

        foreach (var fraction in ProbeRingOnScreenJudge.SweepFractions)
        {
            Add(ProbeRingOnScreenJudge.KindSweep, "B", fraction, snap: false);
        }

        return samples;
    }

    /// <summary>A unique throwaway folder under %TEMP%; the caller deletes it.</summary>
    internal static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), "eyectrl-r2-test-" + Guid.NewGuid().ToString("N"));
}
