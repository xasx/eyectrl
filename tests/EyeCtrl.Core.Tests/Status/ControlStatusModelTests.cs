namespace EyeCtrl.Core.Tests.Status;

using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Status;
using Microsoft.Extensions.Time.Testing;

/// <summary>
/// The headless control-status model (Wave B ticket B1). Every time bound here is deliberately
/// written as a literal (3000, 10 000, 300) rather than the model's own constants: a mutant that
/// changes a constant must turn these red, which it cannot do if the test reads the same constant.
/// </summary>
public class ControlStatusModelTests
{
    private static (ControlStatusModel Model, FakeTimeProvider Time, List<ControlStatus> Published)
        NewModel(GazeProviderType provider = GazeProviderType.StreamEngine, bool initiallyPaused = false)
    {
        var time = new FakeTimeProvider();
        var model = new ControlStatusModel(provider, time, initiallyPaused);
        var published = new List<ControlStatus>();
        model.StatusChanged += published.Add;
        model.Start();
        return (model, time, published);
    }

    private static void AdvanceMs(FakeTimeProvider time, int ms) =>
        time.Advance(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void AnEyeTracker_StartsNormal_AndPublishesNothingUntilAChange()
    {
        var (model, _, published) = NewModel();

        Check.That(model.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(model.Current.Disconnected).IsFalse();
        Check.That(published).IsEmpty();
    }

    [Fact]
    public void AMouseProvider_IsNoTracker_AndStaysThereUnderEveryOtherCondition()
    {
        var (model, time, published) = NewModel(GazeProviderType.Mouse);

        Check.That(model.Current.State).IsEqualTo(ControlState.NoTracker);

        model.OnGazeSample(true);
        model.SetPaused(true);
        AdvanceMs(time, 60_000);
        model.Poll(false);
        model.OnGazeSample(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.NoTracker);
        Check.That(published).IsEmpty();
    }

    [Fact]
    public void NoValidSampleForJustUnderThreeSeconds_StaysNormal()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        AdvanceMs(time, 2_999);
        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(published).IsEmpty();
    }

    [Fact]
    public void NoValidSampleForThreeSeconds_EntersTrackingLost()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        AdvanceMs(time, 3_000);
        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.TrackingLost);
        Check.That(published).HasSize(1);
        Check.That(published[0]).IsEqualTo(new ControlStatus(ControlState.TrackingLost, Disconnected: false));
    }

    [Fact]
    public void AValidSampleAfterAThreeAndAHalfSecondGap_ReturnsToNormalImmediately()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        AdvanceMs(time, 3_500);
        model.Poll(true);
        Check.That(model.Current.State).IsEqualTo(ControlState.TrackingLost);

        model.OnGazeSample(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.Normal);
        Check.That(published).HasSize(2);
        Check.That(published[0].State).IsEqualTo(ControlState.TrackingLost);
        Check.That(published[1].State).IsEqualTo(ControlState.Normal);
    }

    [Fact]
    public void AnInvalidSample_DoesNotResetTheLostClock()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        AdvanceMs(time, 3_000);
        model.Poll(true);
        Check.That(model.Current.State).IsEqualTo(ControlState.TrackingLost);

        model.OnGazeSample(isValid: false);
        AdvanceMs(time, 500);
        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.TrackingLost);
        Check.That(published).HasSize(1);
    }

    [Fact]
    public void APausedUser_WithALiveStream_IsPaused()
    {
        var (model, time, published) = NewModel();

        model.SetPaused(true);
        Check.That(model.Current.State).IsEqualTo(ControlState.Paused);

        AdvanceMs(time, 500);
        model.OnGazeSample(true);
        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.Paused);
        Check.That(published).HasSize(1);
    }

    [Fact]
    public void TrackingLost_OutranksPaused()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);
        model.SetPaused(true);
        Check.That(model.Current.State).IsEqualTo(ControlState.Paused);

        AdvanceMs(time, 3_000);
        model.Poll(true);

        Check.That(model.Current.State).IsEqualTo(ControlState.TrackingLost);
        Check.That(published[^1].State).IsEqualTo(ControlState.TrackingLost);
    }

    [Fact]
    public void NoTracker_OutranksTrackingLostAndPaused()
    {
        var (model, time, _) = NewModel(GazeProviderType.Mouse, initiallyPaused: true);

        AdvanceMs(time, 60_000);
        model.Poll(false);

        Check.That(model.Current.State).IsEqualTo(ControlState.NoTracker);
    }

    [Fact]
    public void Disconnected_NeedsMoreThanTenSecondsOfContinuousFalse()
    {
        var (model, time, published) = NewModel();

        // Polls at the 1 s observation limit: a larger gap is unobserved time and restarts the
        // clock (review 01 F3), so the boundary below must be reached by observed polls.
        model.Poll(false);
        for (int second = 0; second < 10; second++)
        {
            AdvanceMs(time, 1_000);
            model.OnGazeSample(true);       // keep the STATE Normal: the flag is independent of it
            model.Poll(false);
        }
        Check.That(model.Current.Disconnected).IsFalse();   // exactly 10.0 s is not "more than"

        AdvanceMs(time, 1);
        model.OnGazeSample(true);
        model.Poll(false);
        Check.That(model.Current.Disconnected).IsTrue();
        Check.That(published[^1].Disconnected).IsTrue();
        Check.That(published[^1].State).IsEqualTo(ControlState.Normal);
    }

    [Fact]
    public void AConnectedBlip_ResetsTheContinuousDisconnectClock()
    {
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        model.Poll(false);
        for (int second = 0; second < 9; second++)
        {
            AdvanceMs(time, 1_000);
            model.Poll(false);
        }
        model.Poll(true);          // one connected observation resets "continuously"
        model.OnGazeSample(true);
        model.Poll(false);
        for (int second = 0; second < 9; second++)
        {
            AdvanceMs(time, 1_000);
            model.Poll(false);
        }

        Check.That(model.Current.Disconnected).IsFalse();
        Check.That(published).ContainsOnlyElementsThatMatch(s => !s.Disconnected);
    }

    [Fact]
    public void Disconnected_ClearsOnTheFirstConnectedPoll()
    {
        var (model, time, _) = NewModel();

        model.Poll(false);
        for (int second = 0; second < 10; second++)
        {
            AdvanceMs(time, 1_000);
            model.Poll(false);
        }
        AdvanceMs(time, 100);
        model.Poll(false);
        Check.That(model.Current.Disconnected).IsTrue();

        model.Poll(true);

        Check.That(model.Current.Disconnected).IsFalse();
    }

    [Fact]
    public void The0924WalkAwayStorm_NeverSetsDisconnected_AndDoesNotFlickerTheState()
    {
        // The 09-24 log: 4,827 TIMED_OUT flaps, each ~0.53 s disconnected then ~0.1 s connected.
        // The replay below runs 20 cycles (> 10 s of accumulated false time, 12.6 s of wall time),
        // so only the CONTINUOUS requirement keeps the flag false. A mutant that forgets to reset
        // the false-since clock on a connected observation turns this red.
        var (model, time, published) = NewModel();
        model.OnGazeSample(true);

        for (int cycle = 0; cycle < 20; cycle++)
        {
            for (int poll = 0; poll < 5; poll++)
            {
                model.Poll(false);
                AdvanceMs(time, 106);
            }

            model.Poll(true);
            model.OnGazeSample(true);   // a short burst of samples in the connected window
            AdvanceMs(time, 100);

            Check.That(model.Current.Disconnected).IsFalse();
            Check.That(model.Current.State).IsEqualTo(ControlState.Normal);
        }

        Check.That(published).IsEmpty();
    }

    [Fact]
    public void Resuming_IsImmediate_NoStateExitDebounce()
    {
        // B1 fixup-01 (review 01 F2): the 300 ms Paused-exit debounce is deleted. The strip hides
        // on CurrentState; the status must follow the resume on the same edge.
        var (model, _, published) = NewModel();

        model.SetPaused(true);
        Check.That(model.Current.State).IsEqualTo(ControlState.Paused);

        model.SetPaused(false);
        Check.That(model.Current.State).IsEqualTo(ControlState.Normal);

        Check.That(published).HasSize(2);
        Check.That(published[0].State).IsEqualTo(ControlState.Paused);
        Check.That(published[1].State).IsEqualTo(ControlState.Normal);
    }

    [Fact]
    public void Blocked_IsReservedAndNeverRaised()
    {
        Check.That(Enum.IsDefined(ControlState.Blocked)).IsTrue();

        var (model, time, published) = NewModel();
        model.OnGazeSample(true);
        model.SetPaused(true);
        AdvanceMs(time, 3_000);
        model.Poll(true);               // Lost outranks Paused
        model.Poll(false);
        AdvanceMs(time, 30_000);
        model.Poll(false);              // long disconnect
        model.OnGazeSample(true);
        model.SetPaused(false);

        Check.That(published).ContainsOnlyElementsThatMatch(s => s.State != ControlState.Blocked);
        Check.That(published).Not.IsEmpty();
    }
}
