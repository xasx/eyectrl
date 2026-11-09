namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Round button B3: the offer shows only once the rest has lasted
/// <see cref="ActivationOfferEngine.OfferShowAfterMs"/>. The engine is driven directly at the user's
/// geometry (3840×2400, ScaleX 2.0, ring 250, R 75); the settle age is the detector default (500 ms),
/// so a 1000 ms show delay waits 500 ms after the settle. Each test names the production one-liner
/// mutation that turns it red.
/// </summary>
public class ActivationOfferShowDelayTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);
    private static readonly PhysicalPoint Q = new(2600, 1400);   // far outside P's no-move zone
    private const long FrameUs = 30_000;
    private const long WaitUs = 500_000;   // 1000 ms rest − 500 ms settle age

    private static ActivationOfferEngine NewEngine(int showAfterMs = 1000)
        => new(Screen, 2.0, 250, 700, 75) { OfferShowAfterMs = showAfterMs };

    // Feeds valid samples at point from fromUs (inclusive) to toUs (inclusive), one per frame.
    private static void Feed(ActivationOfferEngine engine, PhysicalPoint point, long fromUs, long toUs, bool valid = true)
    {
        for (long ts = fromUs; ts <= toUs; ts += FrameUs)
            engine.Update(point, ts, valid);
    }

    // An offer at P made with no delay, its run already ended at runEndUs.
    private static ActivationOffer OldOfferAtP(ActivationOfferEngine engine, long runEndUs)
    {
        int delay = engine.OfferShowAfterMs;
        engine.OfferShowAfterMs = 500;
        engine.Update(P, 0, true);
        Check.That(engine.NotifyFixationSettled(P, 0)).IsEqualTo(SettleOutcome.Created);
        engine.NotifyFixationEnded(runEndUs);
        engine.OfferShowAfterMs = delay;
        return engine.CurrentOffer!;
    }

    [Fact]
    public void ARestShorterThanTheDelay_NeverShowsAnything()
    {
        // Mutation: show at the settle (ignore the delay) → an offer is up at the settle, red.
        var engine = NewEngine();
        int expired = 0, refused = 0;
        engine.OfferExpired += (_, _, _) => expired++;
        engine.OfferRefused += _ => refused++;

        engine.Update(P, 0, true);
        Check.That(engine.NotifyFixationSettled(P, 0)).IsEqualTo(SettleOutcome.Deferred);
        Check.That(engine.CurrentOffer).IsNull();

        Feed(engine, P, FrameUs, 480_000);
        Check.That(engine.CurrentOffer).IsNull();

        engine.NotifyFixationEnded(480_000);
        Feed(engine, P, 510_000, 3_000_000);

        Check.That(engine.CurrentOffer).IsNull();
        Check.That(expired).IsEqualTo(0);
        Check.That(refused).IsEqualTo(0);
    }

    [Fact]
    public void ARestOfExactlyTheDelay_ShowsAtThatSample_GreaterOrEqual()
    {
        // Mutation: ">=" → ">" at the show → no offer at exactly the delay, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        engine.Update(P, WaitUs - 1, true);
        Check.That(engine.CurrentOffer).IsNull();

        engine.Update(P, WaitUs, true);
        var offer = engine.CurrentOffer;
        Check.That(offer).IsNotNull();
        Check.That(offer!.Point).IsEqualTo(P);
        // "Shown" is the show sample: the linger and everything after it start there, not at the settle.
        Check.That(offer.ExpiresAtUs).IsEqualTo(WaitUs + (ActivationOfferEngine.DefaultOfferExpiryMs * 1000L));
        Check.That(engine.OfferCandidatePoint).IsNull();
    }

    [Fact]
    public void TheCandidate_IsDroppedAtTheRunsEnd()
    {
        // Mutation: drop "_hasCandidate = false" in NotifyFixationEnded → the offer shows after the
        // run ended, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        Check.That(engine.OfferCandidatePoint).IsEqualTo(P);

        engine.NotifyFixationEnded(300_000);

        Check.That(engine.OfferCandidatePoint).IsNull();
        Feed(engine, P, 330_000, 1_500_000);
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void ALiveOldOffer_SurvivesTheCandidate_AndIsReplacedOnlyAtTheShow()
    {
        // Mutation: close the old offer at the candidate (ClearOfferState in the deferred branch) →
        // the old offer is gone during the wait, red.
        var engine = NewEngine();
        int expired = 0;
        engine.OfferExpired += (_, _, _) => expired++;
        var old = OldOfferAtP(engine, runEndUs: 100_000);   // lingers to 1.1 s

        engine.Update(Q, 200_000, true);
        Check.That(engine.NotifyFixationSettled(Q, 200_000)).IsEqualTo(SettleOutcome.Deferred);
        Check.That(engine.CurrentOffer).IsSameReferenceAs(old);

        Feed(engine, Q, 230_000, 680_000);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(old.Id);

        engine.Update(Q, 700_000, true);
        var shown = engine.CurrentOffer!;
        Check.That(shown.Id).IsNotEqualTo(old.Id);
        Check.That(shown.Point).IsEqualTo(Q);
        Check.That(expired).IsEqualTo(0);   // a replacement, as at a settle: never an expiry
    }

    [Fact]
    public void TheOldOffer_ExpiresOnItsOwnClock_WhenTheCandidateIsDropped()
    {
        var engine = NewEngine();
        var expired = new List<ActivationOffer>();
        engine.OfferExpired += (offer, _, _) => expired.Add(offer);
        var old = OldOfferAtP(engine, runEndUs: 100_000);   // deadline 1.1 s

        engine.Update(Q, 200_000, true);
        engine.NotifyFixationSettled(Q, 200_000);
        engine.NotifyFixationEnded(500_000);   // the rest ends 300 ms into the 500 ms wait

        Feed(engine, Q, 530_000, 1_100_000);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(old.Id);

        engine.Update(Q, 1_130_000, true);
        Check.That(engine.CurrentOffer).IsNull();
        Check.That(expired).ContainsExactly(old);
    }

    [Fact]
    public void ABlinkInsideTheRun_DoesNotDropTheCandidate_OnlyTheRunsEndDoes()
    {
        // The run's own rules decide: invalid samples that do not end the run leave the candidate
        // waiting on the sample clock, and it shows at the delay.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        Feed(engine, P, FrameUs, 300_000, valid: false);
        Check.That(engine.OfferCandidatePoint).IsEqualTo(P);
        Feed(engine, P, 330_000, 480_000);
        Check.That(engine.CurrentOffer).IsNull();

        engine.Update(P, 510_000, true);
        Check.That(engine.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void FiveHundred_BehavesExactlyLikeToday_TheOfferShowsAtTheSettle()
    {
        // The regression pin: 500 (the settle age) waits nothing, like the engine default.
        foreach (var engine in new[] { NewEngine(500), new ActivationOfferEngine(Screen, 2.0, 250, 700, 75) })
        {
            engine.Update(P, 0, true);
            Check.That(engine.NotifyFixationSettled(P, 0)).IsEqualTo(SettleOutcome.Created);
            Check.That(engine.CurrentOffer!.ExpiresAtUs).IsEqualTo(ActivationOfferEngine.DefaultOfferExpiryMs * 1000L);
            Check.That(engine.OfferCandidatePoint).IsNull();
        }

        Check.That(ActivationOfferEngine.DefaultOfferShowAfterMs).IsEqualTo(FixationDetector.DefaultFixationSettleMs);
    }

    [Fact]
    public void TheWait_IsTheDelayMinusTheSettleAge_FlooredAtZero()
    {
        // A slower settle (FixationDetectionMs 700) leaves 300 ms of a 1000 ms rest to wait, and a
        // settle past the delay waits nothing.
        var engine = NewEngine();
        engine.SettleAgeMs = 700;
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        engine.Update(P, 299_999, true);
        Check.That(engine.CurrentOffer).IsNull();
        engine.Update(P, 300_000, true);
        Check.That(engine.CurrentOffer).IsNotNull();

        var late = NewEngine();
        late.SettleAgeMs = 1200;
        late.Update(P, 0, true);
        Check.That(late.NotifyFixationSettled(P, 0)).IsEqualTo(SettleOutcome.Created);
    }

    [Fact]
    public void SettlesOnTheLiveOffer_KeepTodaysOutcomes_UnderADelay()
    {
        // OnButton, near miss and P's side are classified against the live offer before the delay is
        // consulted: none of them becomes a candidate.
        var engine = NewEngine();
        var old = OldOfferAtP(engine, runEndUs: 100_000);
        var b = old.Button;
        double ux = (b.X - P.X) / b.DistanceTo(P), uy = (b.Y - P.Y) / b.DistanceTo(P);
        double d = b.DistanceTo(P);

        Check.That(engine.NotifyFixationSettled(b, 200_000)).IsEqualTo(SettleOutcome.OnButton);
        engine.NotifyFixationEnded(220_000);

        var nearMiss = new PhysicalPoint(P.X + ((d - 170) * ux), P.Y + ((d - 170) * uy));   // 170 px from B, 190 from P
        Check.That(engine.NotifyFixationSettled(nearMiss, 250_000)).IsEqualTo(SettleOutcome.Extended);
        engine.NotifyFixationEnded(270_000);

        var pSide = new PhysicalPoint(P.X + (150 * ux), P.Y + (150 * uy));
        Check.That(engine.NotifyFixationSettled(pSide, 300_000)).IsEqualTo(SettleOutcome.Pending);
        Check.That(engine.OfferCandidatePoint).IsNull();
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(old.Id);
    }

    [Fact]
    public void TheFiredLock_StillWins_NoCandidateOverAFiredOffer()
    {
        var engine = NewEngine();
        var old = OldOfferAtP(engine, runEndUs: 100_000);
        Feed(engine, old.Button, 130_000, 1_000_000);   // dwell 700 ms on the button
        Check.That(engine.IsFired).IsTrue();

        Check.That(engine.NotifyFixationSettled(Q, 1_030_000)).IsEqualTo(SettleOutcome.Locked);
        Check.That(engine.OfferCandidatePoint).IsNull();
    }

    [Fact]
    public void Refusals_AreEvaluatedAtTheShow_AndADroppedCandidateRaisesNone()
    {
        var engine = NewEngine();
        var refusals = new List<ActivationOfferRefusal>();
        engine.OfferRefused += refusals.Add;
        engine.SetChromeZones(new PhysicalRect(900, 900, 200, 200));   // P is in chrome

        engine.Update(P, 0, true);
        Check.That(engine.NotifyFixationSettled(P, 0)).IsEqualTo(SettleOutcome.Deferred);
        engine.NotifyFixationEnded(200_000);
        Feed(engine, P, 230_000, 1_000_000);
        Check.That(refusals).IsEmpty();

        engine.NotifyFixationSettled(P, 1_030_000);
        Feed(engine, P, 1_060_000, 1_570_000);
        Check.That(refusals).ContainsExactly(new ActivationOfferRefusal(P, ActivationOfferRefusalReason.InChrome));
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void Clear_DropsAWaitingCandidate()
    {
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        engine.Clear();
        Feed(engine, P, FrameUs, 900_000);
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void NegativeValues_AreRejected()
    {
        var engine = NewEngine();
        Check.ThatCode(() => engine.OfferShowAfterMs = -1).Throws<ArgumentOutOfRangeException>();
        Check.ThatCode(() => engine.SettleAgeMs = -1).Throws<ArgumentOutOfRangeException>();
    }
}
