namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Holistic review-01 F4: the delayed show happens in <see cref="InteractionManager.NoteActivationGazeSample"/>,
/// after the sample's request drain. A Pause that lands on the dispatcher between the two must not
/// publish the waiting button for one sample. Driven on a bare <see cref="InteractionManager"/> so the
/// race can be staged exactly: drain, then the Pause, then the feed — the order the gaze thread and
/// the dispatcher can interleave in <c>GazeEngine.ProcessSample</c>.
/// </summary>
public class ActivationOfferShowDelayStateTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);
    private const long FrameUs = 30_000;
    private const long SettleUs = 300_000;
    private const long WaitUs = 500_000;   // 1000 ms rest − the 500 ms settle age

    private sealed class Harness
    {
        public Harness()
        {
            Im.AttachActivationOffers(new ActivationOfferEngine(Screen, 2.0, 250, 700, 75) { OfferShowAfterMs = 1000 });
            Im.ActivationOfferShown += _ => Shown++;
        }

        public InteractionManager Im { get; } = new();
        public int Shown { get; private set; }

        /// <summary>The gaze thread's per-sample order: drain, then the sample, then the feed. The
        /// optional action runs between the drain and the feed (the dispatcher's window).</summary>
        public void Sample(long timestampUs, Action? betweenDrainAndFeed = null)
        {
            Im.ApplyPendingActivationOfferRequests();
            Im.NoteGazeSample(true, timestampUs);
            betweenDrainAndFeed?.Invoke();
            Im.NoteActivationGazeSample(P, timestampUs, true, continuesRun: true);
        }

        /// <summary>A run at P that settles at <see cref="SettleUs"/> into a waiting button, fed up to
        /// the last sample before the show.</summary>
        public long RestUntilJustBeforeTheShow()
        {
            Sample(0);
            Im.OnFixationStarted(P);
            long ts = 0;
            while (ts < SettleUs)
            {
                ts += FrameUs;
                Sample(ts);
            }
            Im.OnFixationSettled(P);
            Check.That(Im.ActivationOffers!.OfferCandidatePoint).IsNotNull();

            while (ts + FrameUs < SettleUs + WaitUs)
            {
                ts += FrameUs;
                Sample(ts);
            }
            Check.That(Shown).IsEqualTo(0);
            return ts + FrameUs;   // the crossing sample
        }
    }

    [Fact]
    public void TheCrossingSample_ShowsTheButton_WhenNothingIntervenes()
    {
        // The control: the staged run does show the button on the crossing sample, so the test below
        // proves the Pause stopped it, not the setup.
        var h = new Harness();
        long crossingUs = h.RestUntilJustBeforeTheShow();

        h.Sample(crossingUs);

        Check.That(h.Shown).IsEqualTo(1);
        Check.That(h.Im.ActivationOffers!.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void APauseBetweenTheCrossingSamplesDrainAndItsFeed_ShowsNothing()
    {
        // Mutation: drop the Fixation check in NoteActivationGazeSample → the button is published on
        // the crossing sample (and torn down by the next drain: a one-sample flash), red.
        var h = new Harness();
        long crossingUs = h.RestUntilJustBeforeTheShow();

        h.Sample(crossingUs, betweenDrainAndFeed: h.Im.TogglePause);
        Check.That(h.Im.CurrentState).IsEqualTo(InteractionState.Paused);
        Check.WithCustomMessage("no button may be published after the Pause").That(h.Shown).IsEqualTo(0);

        h.Sample(crossingUs + FrameUs);   // the next drain applies the Pause's clear and run end
        Check.That(h.Shown).IsEqualTo(0);
        Check.That(h.Im.ActivationOffers!.CurrentOffer).IsNull();
        Check.That(h.Im.ActivationOffers.OfferCandidatePoint).IsNull();
    }
}
