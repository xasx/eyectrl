namespace EyeCtrl.Core.Tests.Status;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Status;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Pins adopted from review 01's probes (reviewer-only in <c>C:\t\gxb1rvm</c>) and review 02's
/// (reviewer-only in <c>C:\t\gxb1rv2m</c>); every one is a renamed copy, never an edit of the pin.
/// The debounce probe is deliberately absent — the Paused-exit debounce it guarded was deleted by
/// fixup-01 (F2). Review 02's 900 ms / 1.1 s starvation-cadence probes are left out by the
/// coordinator's instruction (they document the trade-off rather than guard a behaviour).
/// </summary>
public class ControlStatusReviewPinsTests
{
    private static void AdvanceMs(FakeTimeProvider time, int ms) =>
        time.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void AnObservationGap_IsNotContinuousDisconnectEvidence()
    {
        // Review 01 F3: two false observations 10.5 s apart with NO poll in between (timer
        // starvation, suspend/resume) must not count as a continuous disconnect. A mutant that drops
        // the MaxPollGapMs restart turns this red.
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(GazeProviderType.StreamEngine, time);
        model.Start();

        model.Poll(false);
        AdvanceMs(time, 10_500);
        model.Poll(false);

        Check.That(model.Current.Disconnected).IsFalse();
    }

    [Fact]
    public void Start_ReseedsTheLostClock()
    {
        // A slow boot between construction and the stream is not lost-gaze time.
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(GazeProviderType.StreamEngine, time);
        AdvanceMs(time, 5_000);
        model.Start();

        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.Normal);
    }

    [Fact]
    public void AnEyeTrackerConstructedPaused_StartsPaused()
    {
        // A --paused / StartPaused launch must publish Paused before any sample arrives.
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(GazeProviderType.StreamEngine, time, initiallyPaused: true);
        model.Start();

        Check.That(model.Current.State).IsEqualTo(ControlState.Paused);
    }

    [Fact]
    public void OneOnePointFiveSecondGap_RestartsTheDisconnectClock()
    {
        // Review 02: the 1 s MaxPollGapMs upper bound. One 1.5 s gap inside an 11 s unplug restarts
        // the false-since clock, so the unobserved interval is not counted as continuous. A mutant
        // that widens MaxPollGapMs to 5 s turns this red.
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(GazeProviderType.StreamEngine, time);
        model.Start();

        for (int i = 0; i < 50; i++)
        {
            model.Poll(false);
            AdvanceMs(time, 100);
        }
        AdvanceMs(time, 1_400);            // the poll-to-poll gap is 1.5 s
        for (int i = 0; i < 50; i++)
        {
            model.Poll(false);
            AdvanceMs(time, 100);
        }

        Check.That(model.Current.Disconnected).IsFalse();
    }
}
