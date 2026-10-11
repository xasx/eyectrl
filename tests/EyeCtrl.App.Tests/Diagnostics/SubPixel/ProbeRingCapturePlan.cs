namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System.Collections.Generic;
using System.Linq;
using EyeCtrl.Core.Settings;

/// <summary>
/// One planned capture: what the harness will place and capture, in order.
/// </summary>
/// <param name="Kind">The row kind: reference / assertion / sweep / D2 solid / D3 X-only.</param>
/// <param name="Condition">"A" or "B".</param>
/// <param name="FractionX">The x fraction to place at.</param>
/// <param name="FractionY">The y fraction to place at.</param>
/// <param name="Snap">Whether the placement snaps (condition A).</param>
/// <param name="Look">The look to draw with; a change re-attaches the ring visual.</param>
/// <param name="Repeat">The capture's repeat index within its row.</param>
internal sealed record ProbeRingCaptureStep(
    string Kind,
    string Condition,
    double FractionX,
    double FractionY,
    bool Snap,
    CursorSettings Look,
    int Repeat);

/// <summary>
/// R2's capture order as a pure function (fixup 04 F2). The harness captures exactly this
/// sequence, so the order can be tested headlessly — most importantly that no two consecutive
/// captures share a placement: an identical <see cref="System.Windows.Media.TranslateTransform"/>
/// value invalidates nothing, WPF presents the same surface, and a "repeat" would be the same
/// pixels, which is why the pre-fixup sweep spreads read 0 by construction.
///
/// <para>
/// The shape: the five f = 0 references are interleaved through the run (start, after the
/// assertions, and around the sweep passes) as a drift trace; the sweep is pass-major (three
/// passes over all 20 fractions, not three repeats per fraction); and the two decider blocks are
/// themselves pass-major so their repeats are real re-presents too.
/// </para>
/// </summary>
internal static class ProbeRingCapturePlan
{
    /// <summary>How many passes the sweep runs over its fractions.</summary>
    internal const int SweepPasses = 3;

    /// <summary>The X-only decider's repeats per fraction (about 9 captures with its reference).</summary>
    internal const int XOnlyRepeats = 2;

    /// <summary>Assumed per-capture cost for the printed estimate; the measured time is printed beside it.</summary>
    internal const int EstimatedMillisecondsPerCapture = 80;

    /// <summary>D2's fractions: the same assertion set, drawn solid.</summary>
    internal static IReadOnlyList<double> SolidControlFractions { get; } = [0.25, 0.5, 0.75];

    /// <summary>D3's fractions: the assertion set plus one more, drawn at y = 0.</summary>
    internal static IReadOnlyList<double> XOnlyFractions { get; } = [0.25, 0.5, 0.75, 0.4];

    /// <summary>
    /// Where sweep pass <paramref name="pass"/> starts in the fraction list. Rotated so no pass
    /// begins or ends on f = 0: every f = 0 capture in the run (the references) must have non-zero
    /// captures on both sides, or a neighbour would repeat its placement and present the same
    /// pixels (F2).
    /// </summary>
    internal static int SweepRotation(int pass) => 5 * (pass + 1);

    /// <summary>
    /// The full capture order. Consecutive steps never share a placement — see
    /// <c>ProbeRingCapturePlanTests.NoTwoConsecutiveCaptures_ShareAPlacement</c>.
    /// </summary>
    internal static IReadOnlyList<ProbeRingCaptureStep> Build()
    {
        var steps = new List<ProbeRingCaptureStep>();

        void Reference(int repeat) => steps.Add(new ProbeRingCaptureStep(
            ProbeRingOnScreenJudge.KindReference, ProbeRingOnScreenJudge.ConditionB, 0.0, 0.0, Snap: false,
            ProbeRingCaptureScene.OwnerLook(), repeat));

        void Assertion(string condition, double fraction, bool snap, int repeat) => steps.Add(
            new ProbeRingCaptureStep(
                ProbeRingOnScreenJudge.KindAssertion, condition, fraction, fraction, snap,
                ProbeRingCaptureScene.OwnerLook(), repeat));

        Reference(0);

        // The assertion block, ordered so no two consecutive placements coincide — condition A's
        // whole-pixel placements are what constrain the order (A at f = 0.25 snaps to the same
        // pixel as f = 0, A at 0.5 and 0.75 both snap one pixel along). Condition A's own f = 0
        // reference is threaded between the 0.5 and 0.75 blocks: it exists for the reference A-B
        // check and must be a fresh present, not a stale copy of B's f = 0.
        Assertion(ProbeRingOnScreenJudge.ConditionB, 0.25, snap: false, 0);
        Assertion(ProbeRingOnScreenJudge.ConditionA, 0.25, snap: true, 0);

        foreach (var repeat in Enumerable.Range(0, ProbeRingOnScreenHarness.RowRepeats))
        {
            Assertion(ProbeRingOnScreenJudge.ConditionA, 0.5, snap: true, repeat);
            Assertion(ProbeRingOnScreenJudge.ConditionB, 0.5, snap: false, repeat);
        }

        steps.Add(new ProbeRingCaptureStep(
            ProbeRingOnScreenJudge.KindReference, ProbeRingOnScreenJudge.ConditionA, 0.0, 0.0, Snap: true,
            ProbeRingCaptureScene.OwnerLook(), Repeat: 0));

        foreach (var repeat in Enumerable.Range(0, ProbeRingOnScreenHarness.RowRepeats))
        {
            Assertion(ProbeRingOnScreenJudge.ConditionA, 0.75, snap: true, repeat);
            Assertion(ProbeRingOnScreenJudge.ConditionB, 0.75, snap: false, repeat);
        }

        // The 0.25 row's remaining repeats close the block, ending on B (fractional) so the
        // following f = 0 reference is a fresh present.
        for (var repeat = 1; repeat < ProbeRingOnScreenHarness.RowRepeats; repeat++)
        {
            Assertion(ProbeRingOnScreenJudge.ConditionA, 0.25, snap: true, repeat);
            Assertion(ProbeRingOnScreenJudge.ConditionB, 0.25, snap: false, repeat);
        }

        Reference(1);

        // Pass-major and rotated: each pass covers every fraction once, and neither end of a pass
        // is f = 0, so the references between passes are fresh presents.
        for (var pass = 0; pass < SweepPasses; pass++)
        {
            var rotation = SweepRotation(pass);
            for (var i = 0; i < ProbeRingOnScreenJudge.SweepFractions.Count; i++)
            {
                var fraction = ProbeRingOnScreenJudge.SweepFractions[
                    (rotation + i) % ProbeRingOnScreenJudge.SweepFractions.Count];
                steps.Add(new ProbeRingCaptureStep(
                    ProbeRingOnScreenJudge.KindSweep, ProbeRingOnScreenJudge.ConditionB,
                    fraction, fraction, Snap: false, ProbeRingCaptureScene.OwnerLook(), pass));
            }

            Reference(2 + pass);
        }

        // D2 (diagnostic, never judged): the solid look's rows first, then its own f = 0 reference —
        // the reference must follow a non-zero placement to be a fresh present.
        for (var pass = 0; pass < ProbeRingOnScreenHarness.RowRepeats; pass++)
        {
            foreach (var fraction in SolidControlFractions)
            {
                steps.Add(new ProbeRingCaptureStep(
                    ProbeRingOnScreenJudge.KindSolidControl, ProbeRingOnScreenJudge.ConditionB,
                    fraction, fraction, Snap: false, ProbeRingCaptureScene.SolidLook(), pass));
            }
        }

        steps.Add(new ProbeRingCaptureStep(
            ProbeRingOnScreenJudge.KindSolidControl, ProbeRingOnScreenJudge.ConditionB, 0.0, 0.0, Snap: false,
            ProbeRingCaptureScene.SolidLook(), Repeat: 0));

        // D3 (diagnostic, never judged): X-only rows at (f, 0), then its own f = 0 reference.
        for (var pass = 0; pass < XOnlyRepeats; pass++)
        {
            foreach (var fraction in XOnlyFractions)
            {
                steps.Add(new ProbeRingCaptureStep(
                    ProbeRingOnScreenJudge.KindXOnly, ProbeRingOnScreenJudge.ConditionB,
                    fraction, 0.0, Snap: false, ProbeRingCaptureScene.OwnerLook(), pass));
            }
        }

        steps.Add(new ProbeRingCaptureStep(
            ProbeRingOnScreenJudge.KindXOnly, ProbeRingOnScreenJudge.ConditionB, 0.0, 0.0, Snap: false,
            ProbeRingCaptureScene.OwnerLook(), Repeat: 0));

        return steps;
    }
}
