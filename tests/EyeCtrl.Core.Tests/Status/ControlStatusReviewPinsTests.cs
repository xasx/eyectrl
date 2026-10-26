namespace EyeCtrl.Core.Tests.Status;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Status;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// Pins adopted from review 01's probes (the probes themselves were reviewer-only in
/// <c>C:\t\gxb1rvm</c>; this file is a renamed copy, never an edit of that pin). The debounce probe
/// is deliberately absent — the Paused-exit debounce it guarded was deleted by fixup-01 (F2).
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
}
