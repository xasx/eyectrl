namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;

/// <summary>
/// The loop this ticket exists to disarm: a wrong key fires → the user presses Backspace to undo it
/// → the correction is scored as accidental activation → and accidental-activation is the one drive
/// that TIGHTENS the dwell knob, so the keyboard gets slower for a user who is already struggling.
///
/// <para>
/// These tests drive the real <see cref="StruggleMonitor"/> → <see cref="AdaptationController"/> →
/// <see cref="EffectiveSettingsProvider"/> pipeline, with the SAME correction evidence on both sides,
/// differing only in the monitor's <c>backspaceTargetId</c>. The armed half is the first construction
/// of a <c>StruggleMonitor</c> with a backspace target in the suite (26 constructions never did) and
/// proves the loop is real, so the disarmed half is not vacuously green; the disarmed half is the
/// shipped wiring.
/// </para>
///
/// <para>
/// <b>What the disarmed half proves is bounded on purpose.</b> The claim is "a Backspace activation
/// does not lengthen dwell" for this evidence — 400 px-separated keys, well outside the 40 px
/// re-approach cone. It is NOT "keyboard dwell can never rise": the geometric re-approach half of
/// <c>ScoreAccidental</c> is still live, and at a geometry where adjacent key centres fall inside
/// that cone (e.g. <c>KeySizeMultiplier</c> 0.5 at 1.0 display scale gives a 39 px centre pitch —
/// 35 DIP key + 2 DIP margins on each side) an ordinary wrong-key-then-adjacent-key correction
/// could still score. At shipped geometry — 70 DIP keys at this user's 2.0 scale, a 148 px
/// centre pitch — it cannot.
/// </para>
/// </summary>
public class BackspaceCorroborationLoopTests
{
    private const long Sec = 1_000_000; // microseconds per second
    private const int KeyboardBaselineMs = 450; // the live KeyboardKeyDwellMs
    private const int CorrectionPairs = 6;

    private static (BufferingTelemetrySink Sink, StruggleMonitor Monitor,
        EffectiveSettingsProvider Provider, AdaptationController Controller) Make(string? backspaceTargetId)
    {
        var settings = new EyeCtrlSettings { Adaptation = { Enabled = true } };
        var sink = new BufferingTelemetrySink(capacity: 512);
        var monitor = new StruggleMonitor(sink, settings.EyeCare, backspaceTargetId: backspaceTargetId);
        var provider = new EffectiveSettingsProvider(settings.Adaptation);
        var controller = new AdaptationController(provider, settings.Adaptation);
        return (sink, monitor, provider, controller);
    }

    private static DwellOutcome Activate(long ts, string id, double cx, double cy) =>
        new(ts, DwellContext.Keyboard, id, OutcomeKind.Activated, AbandonReason.None, 1.0,
            new PhysicalPoint(cx, cy), new PhysicalPoint(cx, cy), 0.0, null);

    private static DwellOutcome Abandon(long ts) =>
        new(ts, DwellContext.Keyboard, "t", OutcomeKind.Abandoned, AbandonReason.GazeLeft, 1.0,
            new PhysicalPoint(15, 0), new PhysicalPoint(0, 0), 30.0, null);

    /// <summary>
    /// The user's correction loop: type a letter, notice the wrong key fired, press Backspace. The
    /// two keys are 400 px apart, well outside the 40 px re-approach floor, so ONLY the
    /// key→Backspace corroboration can score these as reversals — which is exactly what the armed
    /// half tests and the disarmed half must not.
    /// </summary>
    private static void FeedWrongKeyCorrections(BufferingTelemetrySink sink)
    {
        long ts = 0;
        for (int i = 0; i < CorrectionPairs; i++)
        {
            sink.Report(Activate(ts, "q", 100, 100));
            ts += 500_000;
            sink.Report(Activate(ts, "backspace", 500, 100));
            ts += 500_000;
        }
    }

    private static double KeyboardDwell(EffectiveSettingsProvider provider) =>
        provider.Effective(AdaptationKnob.DwellTime, DwellContext.Other, KeyboardBaselineMs);

    [Fact]
    public void ArmedBackspaceTarget_TheCorrectionLoopLengthensKeyboardDwell()
    {
        var (sink, monitor, provider, controller) = Make(backspaceTargetId: "backspace");
        FeedWrongKeyCorrections(sink);

        // 6 q activations each reversed by the following Backspace activation: score 6/12 = 0.5,
        // exactly the Conservative posture's relax threshold, with the 3-activation volume gate met.
        double maxDwell = 0.0;
        long now = 30 * Sec;
        for (int i = 0; i < 10; i++)
        {
            controller.Tick(monitor.Assess(now), now);
            maxDwell = Math.Max(maxDwell, KeyboardDwell(provider));
            now += 3 * Sec; // one relax step per 3-second sustain
        }

        Check.WithCustomMessage(
                "Supplying backspaceTargetId must arm the key→Backspace corroboration and drive the " +
                "dwell knob LONGER for a user who is only correcting wrong keys. If this is green, " +
                "the loop test has no teeth and the disarmed test below proves nothing.")
            .That(maxDwell).IsStrictlyGreaterThan(KeyboardBaselineMs);

        // The rise must be the TIGHTENING sub-band (the quarantined longer-dwell path), not the
        // forgiving one — that is what the disarmed run must hold at zero.
        Check.That(provider.TryGetMomentaryFill(
                AdaptationKnob.DwellTime, DwellContext.Other, out _, out double tightening)).IsTrue();
        Check.That(tightening).IsStrictlyGreaterThan(0.0);
    }

    [Fact]
    public void DisarmedBackspaceTarget_ABackspaceCorrectionDoesNotLengthenDwell_WhileTheForgivingKnobStillMoves()
    {
        // The shipped wiring (AppBootstrapper passes null). Phase 1: the identical correction stream
        // must produce NO dwell movement at all — no tightening and no forgiving drive in it.
        var (sink, monitor, provider, controller) = Make(backspaceTargetId: null);
        FeedWrongKeyCorrections(sink);

        long now = 20 * Sec;
        for (int i = 0; i < 5; i++)
        {
            controller.Tick(monitor.Assess(now), now);
            Check.WithCustomMessage(
                    "A Backspace correction lengthened the keyboard dwell. The category error is back: " +
                    "the monitor is scoring the user's undo gesture as accidental activation.")
                .That(KeyboardDwell(provider)).IsEqualTo((double)KeyboardBaselineMs);
            now += 3 * Sec;
        }

        // Phase 2: a genuine abandonment stream (the drive the forgiving direction is FOR). The knob
        // must move DOWN — proving phase 1's flat line was measured, not dead — and still never
        // above baseline. 20 near-complete abandons after the 12 corrections: 20/32 high-progress
        // plus the retry term clears the 0.5 relax threshold.
        long ts = 40 * Sec;
        for (int i = 0; i < 20; i++)
        {
            sink.Report(Abandon(ts));
            ts += 2 * Sec;
        }

        double maxDwell = 0.0, minDwell = double.MaxValue;
        now = 90 * Sec; // all abandons are in the 60 s evidence window
        for (int i = 0; i < 6; i++)
        {
            controller.Tick(monitor.Assess(now), now);
            double dwell = KeyboardDwell(provider);
            maxDwell = Math.Max(maxDwell, dwell);
            minDwell = Math.Min(minDwell, dwell);
            now += 3 * Sec;
        }

        Check.WithCustomMessage(
                "The abandonment stream did not shorten the dwell, so this test never reached the " +
                "knob's moving state and the 'never increases' assertion above is vacuous.")
            .That(minDwell).IsStrictlyLessThan(KeyboardBaselineMs);
        Check.WithCustomMessage(
                "Keyboard dwell rose above baseline with the backspace corroboration disarmed — " +
                "something other than the (null) backspace target is driving the tightening fill.")
            .That(maxDwell).IsLessOrEqualThan((double)KeyboardBaselineMs);
    }
}
