namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// <see cref="InteractionManager.LockedModeGazeLost"/>: sustained invalid raw gaze in Dragging or
/// ScrollMode hands the exit to the mode's App-side owner (which alone can release the pressed
/// button / stop the scroll worker), once per lost-gaze episode. Core leaves the state alone for
/// those modes; RepeatMode keeps exiting itself (architecture-check-2026-10, ticket 02). Each test
/// names the production mutation that turns it red.
/// </summary>
public class LockedModeGazeLostTests
{
    private const long StepUs = 30_000;

    private static InteractionManager EnterMode(InteractionState mode)
    {
        var im = new InteractionManager();
        if (mode == InteractionState.RepeatMode)
        {
            im.EnterRepeatMode(InteractionAction.LeftClick);
        }
        else
        {
            im.OnFixationStarted(new PhysicalPoint(400, 400));
            im.OnActivationButtonDwelled();
            im.OnActionSelected(mode == InteractionState.Dragging
                ? InteractionAction.Drag : InteractionAction.ScrollMode);
        }
        Check.That(im.CurrentState).IsEqualTo(mode);
        return im;
    }

    // Feeds samples from fromUs (inclusive) for durationUs. Returns the next timestamp.
    private static long Feed(InteractionManager im, long fromUs, long durationUs, bool valid)
    {
        long ts = fromUs;
        for (; ts < fromUs + durationUs; ts += StepUs)
            im.NoteGazeSample(valid, ts);
        return ts;
    }

    [Theory]
    [InlineData(InteractionState.Dragging)]
    [InlineData(InteractionState.ScrollMode)]
    public void SustainedInvalidGaze_RaisesOnce_AndLeavesTheStateToTheOwner(InteractionState mode)
    {
        // Mutations: drop the once-per-episode latch → raised on every sample past 2 s; end the mode
        // in Core (a state-only exit) → the state reads FreeLook.
        var im = EnterMode(mode);
        var raised = new List<InteractionState>();
        im.LockedModeGazeLost += raised.Add;

        long ts = Feed(im, 0, 1_000_000, valid: true);
        ts = Feed(im, ts, 1_900_000, valid: false);          // 1.9 s lost: below the threshold
        Check.That(raised).IsEmpty();

        Feed(im, ts, 1_500_000, valid: false);               // well past 2 s
        Check.That(raised).ContainsExactly(mode);
        Check.WithCustomMessage("Core ended the mode itself — the drag's button / the scroll worker would be left behind.")
            .That(im.CurrentState).IsEqualTo(mode);
    }

    [Theory]
    [InlineData(InteractionState.Dragging)]
    [InlineData(InteractionState.ScrollMode)]
    public void Blinks_NeverRaiseIt(InteractionState mode)
    {
        // ~300 ms blinks between valid stretches, for 10 s. Mutation: measure from the last
        // sample instead of the last VALID sample → every blink accumulates and it fires.
        var im = EnterMode(mode);
        var raised = 0;
        im.LockedModeGazeLost += _ => raised++;

        long ts = 0;
        for (int i = 0; i < 10; i++)
        {
            ts = Feed(im, ts, 700_000, valid: true);
            ts = Feed(im, ts, 300_000, valid: false);
        }
        Check.That(raised).IsEqualTo(0);
    }

    [Fact]
    public void AValidSample_ReArmsIt_ForTheNextEpisode()
    {
        // Mutation: never clear the latch on recovery → the second episode goes unreported and the
        // drag stays pressed for good.
        var im = EnterMode(InteractionState.Dragging);
        var raised = 0;
        im.LockedModeGazeLost += _ => raised++;

        long ts = Feed(im, 0, 500_000, valid: true);
        ts = Feed(im, ts, 2_500_000, valid: false);           // episode 1
        ts = Feed(im, ts, 500_000, valid: true);              // eyes back
        Feed(im, ts, 2_500_000, valid: false);                // episode 2
        Check.That(raised).IsEqualTo(2);
    }

    [Fact]
    public void FreeLook_LostGaze_RaisesNothing()
    {
        var im = new InteractionManager();
        var raised = 0;
        im.LockedModeGazeLost += _ => raised++;

        long ts = Feed(im, 0, 500_000, valid: true);
        Feed(im, ts, 5_000_000, valid: false);
        Check.That(raised).IsEqualTo(0);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
    }

    [Fact]
    public void RepeatMode_StillExitsItself_AndRaisesNothing()
    {
        // Repeat holds no OS state, so Core's own exit stays. Mutation: route repeat through the
        // event instead → nobody ends it in Core and the state stays RepeatMode.
        var im = EnterMode(InteractionState.RepeatMode);
        var raised = 0;
        im.LockedModeGazeLost += _ => raised++;

        long ts = Feed(im, 0, 500_000, valid: true);
        Feed(im, ts, 2_500_000, valid: false);
        Check.That(im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        Check.That(raised).IsEqualTo(0);
    }
}
