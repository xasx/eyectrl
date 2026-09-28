namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Ticket 7, part C: the offer engine, unwired, at the user's geometry (3840×2400, ScaleX 2.0, ring
/// 250, R 75). One test per engine-level row of the ticket; each names the production one-liner
/// mutation that turns it red.
/// <para>
/// The placement puts B 360 px from P on the diagonal (ring/2 + 55 DIP = 180 DIP at ScaleX 2.0), so
/// the clock arithmetic below is hand-checkable: one 30 ms sample credits 30 ms, the dwell is 700 ms,
/// the expiry is 1000 ms and the outside-pause budget is 500 ms.
/// </para>
/// </summary>
public class ActivationOfferEngineTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);
    private const double Scale = 2.0;
    private const double RingDip = 250;
    private const double FixationR = 75;
    private const double OffsetPx = 360;
    private const long FrameUs = 30_000;

    /// <summary>The shipped placement at this geometry: 360 px diagonally from P, toward the centre.</summary>
    private static PhysicalPoint B => new(
        P.X + (OffsetPx / Math.Sqrt(2)), P.Y + (OffsetPx / Math.Sqrt(2)));

    private static ActivationOfferEngine NewEngine(int dwellMs = 700)
        => new(Screen, Scale, RingDip, dwellMs, FixationR);

    /// <summary>A point <paramref name="distancePx"/> from B, on the P→B line (toward P when shorter
    /// than the 360 px offset, away from P when longer).</summary>
    private static PhysicalPoint PointAtDistanceFromB(double distancePx)
    {
        var b = B;
        double unitX = (b.X - P.X) / OffsetPx;
        double unitY = (b.Y - P.Y) / OffsetPx;
        // Toward P: B - d·unit. Past B: B + (d-360)·unit, i.e. still d from B along the line.
        double signed = OffsetPx - distancePx;   // 0 at B; negative past B
        return new PhysicalPoint(P.X + (signed * unitX), P.Y + (signed * unitY));
    }

    /// <summary>
    /// A point on P's side of the no-move zone: 150 px from P toward B, so 210 px from B — outside the
    /// 150 px hit area, inside the 225 px zone, and nearer P than B (the swallowed-settle band).
    /// </summary>
    private static PhysicalPoint PsidePoint()
    {
        var b = B;
        double unitX = (b.X - P.X) / OffsetPx;
        double unitY = (b.Y - P.Y) / OffsetPx;
        return new PhysicalPoint(P.X + (150 * unitX), P.Y + (150 * unitY));
    }

    private static void Feed(ActivationOfferEngine engine, PhysicalPoint point, long fromUs, int samples)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            engine.Update(point, ts, isValid: true);
            ts += FrameUs;
        }
    }

    // ── Creation and one fire ────────────────────────────────────────────────

    [Fact]
    public void Settle_OffersAtThePlacement_AndTheDwellFiresOnce()
    {
        // Mutation: drop the one-fire latch (_fired) → the dwell fires again on every in-hit sample
        // past the threshold, red.
        var engine = NewEngine();
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;

        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        var offer = engine.CurrentOffer!;
        Check.That(offer).IsNotNull();
        Check.That(offer.Point).IsEqualTo(P);
        Check.That(offer.Button.X).IsCloseTo(P.X + 254.558, 1e-3);
        Check.That(offer.Button.Y).IsCloseTo(P.Y + 254.558, 1e-3);
        Check.That(offer.HitRadiusPx).IsEqualTo(LayoutConstants.ActivationButtonHitRadiusPx);
        Check.That(offer.ExpiresAtUs).IsEqualTo(1_000_000);
        Check.That(engine.IsFired).IsFalse();

        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 30);

        Check.That(fired).HasSize(1);                 // the 24th in-hit sample reaches 720 ms of 700
        Check.That(fired[0].Id).IsEqualTo(offer.Id);
        Check.That(engine.IsFired).IsTrue();
    }

    [Fact]
    public void SettlementOutsideTheNoMoveZone_ReplacesTheOffer()
    {
        // Mutation: keep the first offer (drop the replacement) → the new settle's offer never appears.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var first = engine.CurrentOffer!;

        var elsewhere = new PhysicalPoint(2500, 1800);
        engine.NotifyFixationSettled(elsewhere, 200_000);

        Check.That(engine.CurrentOffer!.Id).IsEqualTo(first.Id + 1);
        Check.That(engine.CurrentOffer.Point).IsEqualTo(elsewhere);
    }

    // ── On-button and near-miss classification ───────────────────────────────

    [Fact]
    public void OnButtonSettle_IsNotPending_A_DeclineMakesNoNewOffer()
    {
        // Mutation: remember on-button settles as pending → the decline re-offers at the old button's
        // spot and a new offer appears, red (R1).
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;

        engine.NotifyFixationSettled(offer.Button, 300_000);   // the next run settles ON the button
        engine.Decline();

        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void NearMiss_ExtendsTheLiveOffer_AndNeverReOffers()
    {
        // Mutation: treat a near miss as pending → after the expiry a new offer appears at the near
        // miss, red. Or: drop the near-miss extension → the expiry never moves, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        Check.That(offer.ExpiresAtUs).IsEqualTo(1_000_000);

        var nearMiss = PointAtDistanceFromB(160);   // outside the 150 hit area, nearer B than P
        Check.That(nearMiss.DistanceTo(offer.Button)).IsCloseTo(160, 1e-6);
        Check.That(nearMiss.DistanceTo(P)).IsCloseTo(200, 1e-6);

        engine.NotifyFixationSettled(nearMiss, 500_000);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(offer.Id);            // never replaced
        Check.That(engine.CurrentOffer.ExpiresAtUs).IsEqualTo(1_500_000);   // extended

        // Its samples push the expiry too (decision 10).
        engine.Update(nearMiss, 700_000, true);
        Check.That(engine.CurrentOffer!.ExpiresAtUs).IsEqualTo(1_700_000);

        engine.NotifyFixationEnded(750_000);
        Check.That(engine.CurrentOffer!.ExpiresAtUs).IsEqualTo(1_750_000);

        engine.Update(nearMiss, 1_760_001, true);
        Check.That(engine.CurrentOffer).IsNull();   // and nothing re-offers
    }

    // ── The swallowed settle and its pending re-offer ────────────────────────

    [Fact]
    public void SwallowedSettle_ReOffersWhenTheLiveOfferExpires_WhileItsRunStillHolds()
    {
        // Mutation: drop the pending (no re-offer) → the swallow is a dead end and CurrentOffer is
        // null after the expiry, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        var first = engine.CurrentOffer!;

        var pSide = PsidePoint();   // on P's side of the zone: nearer P than B, outside the hit area
        engine.NotifyFixationSettled(pSide, 300_000);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(first.Id);   // the live offer is kept

        engine.Update(pSide, 1_100_000, true);     // past the 1 s deadline; the run still holds

        Check.That(engine.CurrentOffer).IsNotNull();
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(first.Id + 1);
        Check.That(engine.CurrentOffer.Point).IsEqualTo(pSide);
    }

    [Fact]
    public void ThePendingReOffer_IsClearedByItsRunsEnd()
    {
        // Mutation: never clear the pending on the run end → the expiry re-offers for a run that is
        // over, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        engine.NotifyFixationSettled(PsidePoint(), 300_000);
        engine.NotifyFixationEnded(400_000);

        engine.Update(P, 1_100_000, true);
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void FiredOffer_IsLocked_NotReplacedAndNotReOfferedOver()
    {
        // Mutation: allow a settle to replace a fired offer (drop the lock) → the on-P's-side settle
        // becomes a pending and the decline re-offers, red (R3).
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);
        Check.That(engine.IsFired).IsTrue();

        engine.NotifyFixationSettled(PsidePoint(), 800_000);
        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 900_000);
        engine.Decline();

        Check.That(engine.CurrentOffer).IsNull();
    }

    // ── The valid-time dwell ─────────────────────────────────────────────────

    [Fact]
    public void BlinkInTheDwell_StillFires_ExactlyOnce()
    {
        // Mutation: reset the dwell on any invalid sample instead of pausing it → the 300 ms blink
        // restarts the clock and the fire lands ~360 ms late, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;

        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 13);   // 390 ms credited
        Check.That(engine.IsFired).IsFalse();

        // A 300 ms blink: the samples are invalid and pause the dwell.
        long ts = 420_000;
        for (int i = 0; i < 10; i++)
        {
            engine.Update(offer.Button, ts, isValid: false);
            ts += FrameUs;
        }
        // Resume at 720 ms: 390 credited + 11 more inside samples → 720 credited at t = 1020 ms.
        FuelTheDwell(engine, offer.Button, fromUs: 720_000, inHitSamples: 11);

        Check.That(fired).HasSize(1);
        Check.That(engine.IsFired).IsTrue();
        // (The Mutation that aborts on a >150 ms silence would fire at 1380 ms instead; the assertion
        // is the exactly-once count plus the credited-time arithmetic pinned in DwellProgress.)
        Check.That(engine.DwellProgress).IsEqualTo(1.0);
    }

    [Fact]
    public void ExcursionUnderTheBudget_PausesTheDwell()
    {
        // Mutation: reset on any outside sample (drop the pause budget) → the 200 ms excursion wipes
        // the 390 ms and the dwell completes 200 ms late, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;

        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 13);   // 390 credited
        double before = engine.DwellProgress;

        // 200 ms outside the hit area (valid): a pause, not a reset.
        Feed(engine, P, 420_000, samples: 7);                                    // 420..600 ms
        Check.That(engine.DwellProgress).IsEqualTo(before);

        // Back inside: the inside clock continues from 390 ms.
        FuelTheDwell(engine, offer.Button, fromUs: 630_000, inHitSamples: 12);   // 390+360 = 750
        Check.That(engine.IsFired).IsTrue();
    }

    [Fact]
    public void ExcursionOverTheBudget_ResetsTheDwell()
    {
        // Mutation: pause without a limit (drop the reset) → the 600 ms excursion keeps the 390 ms and
        // the dwell fires early, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;

        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 13);   // 390 credited
        Check.That(engine.IsFired).IsFalse();

        Feed(engine, P, 420_000, samples: 21);                                   // 420..1020 ms outside
        Check.That(engine.DwellProgress).IsEqualTo(0.0);                          // reset mid-stretch

        // 700 ms of fresh inside time: the 24th sample at 1020+30+720 = 1740 ms.
        FuelTheDwell(engine, offer.Button, fromUs: 1_050_000, inHitSamples: 23);
        Check.That(engine.IsFired).IsFalse();
        FuelTheDwell(engine, offer.Button, fromUs: 1_740_000, inHitSamples: 1);
        Check.That(engine.IsFired).IsTrue();
    }

    [Fact]
    public void HitRadius_IsThePhysicalConstant_NotTheSharedButtonRadius()
    {
        // Mutation: use SharedButtonRadius (50 DIP → 100 px) as the hit radius → the 149 px sample
        // falls outside and the dwell never progresses, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        Check.That(engine.CurrentOffer!.HitRadiusPx).IsEqualTo(150.0);
        Check.That(LayoutConstants.ActivationButtonHitRadiusPx).IsEqualTo(150.0);
        Check.That(LayoutConstants.SharedButtonRadius).IsEqualTo(50.0);

        engine.Update(PointAtDistanceFromB(149), FrameUs, isValid: true);
        Check.That(engine.DwellProgress).IsStrictlyGreaterThan(0.0);

        double credited = engine.DwellProgress;
        engine.Update(PointAtDistanceFromB(151), 2 * FrameUs, isValid: true);
        Check.That(engine.DwellProgress).IsEqualTo(credited);   // 151 px: outside, no credit
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Fact]
    public void Expiry_UnusedOffer_IsGoneOneSecondAfterTheOfferedRunEnds()
    {
        // Mutation: expire from the settle instead of the run's end (a fixed settle + 1 s) → the offer
        // is already gone at 1.4 s, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        engine.NotifyFixationEnded(500_000);                    // the offered run ends at 0.5 s

        engine.Update(P, 1_400_000, true);                      // 900 ms after the end
        Check.That(engine.CurrentOffer).IsNotNull();

        engine.Update(P, 1_510_000, true);                      // 1.01 s after the end
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void Expiry_AnExcursionAt900msDoesNotKillTheOffer_AndADwellLookExtendsIt()
    {
        // Mutation: expiry = the run's end + 1 s only (drop the in-hit extension) → the offer is gone
        // when the user looks back, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);
        engine.NotifyFixationEnded(500_000);

        FuelTheDwell(engine, B, fromUs: 700_000, inHitSamples: 7);   // 0.70–0.90 s on the button
        Feed(engine, P, 930_000, samples: 6);                        // 0.93–1.08 s: a 200 ms excursion
        engine.Update(P, 1_200_000, true);

        Check.That(engine.CurrentOffer).IsNotNull();                 // the excursion did not kill it

        FuelTheDwell(engine, B, fromUs: 1_230_000, inHitSamples: 13); // look back: 1.23–1.59 s
        engine.Update(P, 2_500_000, true);                            // 910 ms after the last in-hit
        Check.That(engine.CurrentOffer).IsNotNull();

        engine.Update(P, 2_630_000, true);                            // past 1 s after the last in-hit
        Check.That(engine.CurrentOffer).IsNull();
    }

    // ── Completion paths and teardown ────────────────────────────────────────

    [Fact]
    public void RejectedCompletion_CountsAsDecline_SoTheFiredLockNeverStrands()
    {
        // Mutation: keep the lock on a rejected completion → the next settle is ignored and no new
        // offer ever appears, red (R3).
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);
        Check.That(engine.IsFired).IsTrue();

        engine.Accept(offer.Id + 999);        // a stale id: rejected, and a rejection is a decline

        Check.That(engine.CurrentOffer).IsNull();

        engine.NotifyFixationEnded(1_000_000);
        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 1_100_000);
        Check.That(engine.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void AcceptedOffer_IsCleared_AndDoesNotReOfferAPending()
    {
        // Mutation: don't clear on accept → the old offer stays up and the next settle's classification
        // runs against a dead offer, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);

        engine.Accept(offer.Id);

        Check.That(engine.CurrentOffer).IsNull();
        Check.That(engine.IsFired).IsFalse();
    }

    [Fact]
    public void Teardown_ClearsEvenALockedOffer_AndAFreshSettleGetsAnOffer()
    {
        // Mutation: skip a locked offer in Clear → the old offer survives the teardown, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);
        Check.That(engine.IsFired).IsTrue();

        engine.Clear();

        Check.That(engine.CurrentOffer).IsNull();
        Check.That(engine.IsFired).IsFalse();

        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 1_000_000);
        Check.That(engine.CurrentOffer).IsNotNull();
    }

    // ── Chrome ───────────────────────────────────────────────────────────────

    [Fact]
    public void Chrome_NoOfferWhenPIsInAZone_AndNoneWhenBothSidesIntersect()
    {
        // Mutation: skip the P-in-chrome check → an offer appears in the toolbar band, red. Second
        // half: skip the drop after the failed flip → an offer appears despite the covered flip, red.
        var engine = NewEngine();
        engine.SetChromeZones(new PhysicalRect(0, 0, 3840, 200));   // the top toolbar band

        engine.NotifyFixationSettled(new PhysicalPoint(1000, 100), 0);
        Check.That(engine.CurrentOffer).IsNull();

        // P outside every zone, but both placement sides land in one: P=(320,320) puts the preferred B
        // at ~(574.56, 574.56) and the flipped one at (150,150) after the clamp.
        engine.SetChromeZones(
            new PhysicalRect(424.56, 424.56, 300, 300),
            new PhysicalRect(0, 0, 300, 300));

        engine.NotifyFixationSettled(new PhysicalPoint(320, 320), 100_000);
        Check.That(engine.CurrentOffer).IsNull();
    }

    /// <summary>Feeds <paramref name="inHitSamples"/> valid samples at an in-hit point, one frame apart.</summary>
    private static void FuelTheDwell(
        ActivationOfferEngine engine, PhysicalPoint point, long fromUs, int inHitSamples)
        => Feed(engine, point, fromUs, inHitSamples);
}
