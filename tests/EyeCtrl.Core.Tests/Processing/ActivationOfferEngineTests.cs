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
        engine.Decline(engine.CurrentOffer!.Id);

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
        // Two guards enforce this — the pending clear and the run-active flag — so the mutation must
        // drop both (keep the pending AND leave `_runActive` true on the end) to red this test; either
        // guard alone leaves the behavior intact. The on-button-companion case that a single guard
        // would miss is covered by APendingFromAnEndedRun_IsNotReOffered_... above.
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
        // The fired offer is still the live one: a settle elsewhere must not have replaced it (the
        // lock). Without this the mutation that drops the lock ends on the decline's null and passes.
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(offer.Id);

        engine.Decline(engine.CurrentOffer!.Id);

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

    [Fact]
    public void SilenceLongerThanThePauseBudget_ResetsTheDwell()
    {
        // Review A F5: a silence over the pause budget is the same evidence as invalid samples, so it
        // must abort the dwell's accumulation. Mutation: drop the silence reset (pause from delivered
        // samples only) → the dwell keeps its 390 ms and reads 456/700, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 13);   // 390 ms
        Check.That(engine.DwellProgress).IsCloseTo(390.0 / 700, 1e-9);

        // 810 ms with no samples at all, then the gaze is back on the button.
        engine.Update(offer.Button, 1_200_000, isValid: true);

        // The silence aborts the dwell and the resuming sample credits NOTHING, exactly as the
        // detector's fresh run does after its gap abort (review A, R2-3's companion nit).
        Check.That(engine.DwellProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void BudgetZero_OneDroppedFrameIsNotAnExcursion_ButTwoOrABlinkOrAnOutsideSampleResets()
    {
        // Review A N7/N8: the reset-semantics comparison run rests on these semantics, so pin them.
        // Mutation N7: drop the frame floor from the in-hit silence check (compare against the budget
        // alone) → the 60 ms step resets and the StrictlyGreaterThan assertion reds. Mutation N8: drop
        // the `<= 0` clause in PauseDwell (reset only from the second outside sample) → the blink does
        // not reset and its IsEqualTo(0) reds.
        var engine = NewEngine();
        engine.OutsidePauseBudgetMs = 0;
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;

        engine.Update(offer.Button, FrameUs, isValid: true);          // 30 ms
        double afterOneFrame = engine.DwellProgress;
        Check.That(afterOneFrame).IsStrictlyGreaterThan(0.0);

        engine.Update(offer.Button, 90_000, isValid: true);           // one dropped frame: 60 ms
        Check.That(engine.DwellProgress).IsStrictlyGreaterThan(afterOneFrame);

        engine.Update(offer.Button, 180_000, isValid: true);          // two dropped frames: 90 ms
        Check.That(engine.DwellProgress).IsEqualTo(0.0);              // a silence resets

        engine.Update(offer.Button, 210_000, isValid: true);
        Check.That(engine.DwellProgress).IsStrictlyGreaterThan(0.0);

        engine.Update(offer.Button, 240_000, isValid: false);         // a blink sample
        Check.That(engine.DwellProgress).IsEqualTo(0.0);

        engine.Update(offer.Button, 270_000, isValid: true);
        Check.That(engine.DwellProgress).IsStrictlyGreaterThan(0.0);

        engine.Update(P, 300_000, isValid: true);                     // any outside sample
        Check.That(engine.DwellProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void ASilenceThatEndsOffTheButton_StillResetsTheDwell()
    {
        // Review A R2-3 / probe P11: the gaze was away from the button for 400 ms of silence plus
        // 210 ms of outside samples — over the 500 ms budget — so the dwell must reset. Mutation: date
        // the miss from the first outside sample → the 400 ms before it is lost, the dwell continues
        // from 390 ms and the closing assertion reds.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 13);   // 390 ms
        Check.That(engine.DwellProgress).IsCloseTo(390.0 / 700, 1e-9);

        Feed(engine, P, 790_000, samples: 7);   // 400 ms of silence, then 210 ms off the button

        engine.Update(offer.Button, 1_000_000, isValid: true);
        Check.That(engine.DwellProgress).IsCloseTo(30.0 / 700, 1e-9);   // reset, one frame credited
    }

    [Fact]
    public void AcceptOnAMatchingUnfiredOffer_DeclinesIt()
    {
        // Review A N5: a completion for a live but unfired offer is a protocol violation, and a
        // rejected completion counts as a decline. Mutation: make Accept a no-op unless fired → the
        // offer survives and CurrentOffer is not null, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        engine.Update(offer.Button, FrameUs, isValid: true);
        Check.That(engine.IsFired).IsFalse();

        engine.Accept(offer.Id);

        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void FixationRadiusPx_IsSettable_SoTheNoMoveZoneFollowsIt()
    {
        // Review A, ticket-8 nit: R is a live setting and an adaptation knob, so it must be settable in
        // place. Mutation: make the setter a no-op → the widened zone never classifies and the settle
        // replaces the offer, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;

        engine.FixationRadiusPx = 150;   // the zone grows from 225 to 300 around B
        Check.That(engine.FixationRadiusPx).IsEqualTo(150.0);

        // 280 px from B, 80 px from P: outside the R75 zone, inside the R150 one, on P's side.
        var wideZonePoint = PointAtDistanceFromB(280);
        engine.NotifyFixationSettled(wideZonePoint, 100_000);

        Check.That(engine.CurrentOffer!.Id).IsEqualTo(offer.Id);   // kept, not replaced
    }

    [Fact]
    public void AnOfferForARunStillHolding_IsAlive_AndDiesOneSecondAfterTheRunEnds()
    {
        // Review A F3a / E1: decision 3's "it goes 1 s after you look away", not 1 s after the settle.
        // Mutation: drop the per-sample extension while the offered run holds → the offer dies at 1 s
        // under a user who is still holding, red.
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        Feed(engine, P, FrameUs, samples: 66);      // 30 ms .. 1.98 s: the user holds at P
        engine.Update(P, 2_000_000, isValid: true);
        Check.That(engine.CurrentOffer).IsNotNull();

        engine.NotifyFixationEnded(2_000_000);
        engine.Update(P, 2_900_000, isValid: true);   // 900 ms after the end
        Check.That(engine.CurrentOffer).IsNotNull();

        engine.Update(P, 3_050_000, isValid: true);   // past the linger
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void AFiredOffer_IsNotExpired_ByOutsideSamples_AndAcceptStillWorks()
    {
        // Review A F3c / E12: the fired lock's "cannot be expired" half. Mutation: allow a fired offer
        // to expire → it is gone after the outside stretch and the accept finds nothing, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);
        Check.That(engine.IsFired).IsTrue();

        Feed(engine, P, 800_000, samples: 40);      // 1.2 s of samples well outside the hit area

        Check.That(engine.CurrentOffer).IsNotNull();
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(offer.Id);

        engine.Accept(offer.Id);
        Check.That(engine.CurrentOffer).IsNull();
        Check.That(engine.IsFired).IsFalse();
    }

    [Fact]
    public void APendingFromAnEndedRun_IsNotReOffered_WhenALaterRunSettlesOnTheButton()
    {
        // Review A F3b / E10: the pending dies with its run, so a later on-button run cannot resurrect
        // it. Mutation: drop the pending clear alone (E10) → the later run's settle re-arms
        // `_runActive`, the decline re-offers at the old run's point and a new offer appears, red.
        // (The companion test below, ThePendingReOffer_IsClearedByItsRunsEnd, needs both guards
        // dropped, because there `_runActive` stays false.)
        var engine = NewEngine();
        engine.Update(P, 0, true);
        engine.NotifyFixationSettled(P, 0);

        engine.NotifyFixationSettled(PsidePoint(), 300_000);    // run X: swallowed, pending
        engine.NotifyFixationEnded(400_000);                    // run X ends
        engine.NotifyFixationSettled(B, 500_000);               // run Y settles ON the button

        engine.Decline(engine.CurrentOffer!.Id);

        Check.That(engine.CurrentOffer).IsNull();               // nothing re-offers at the old point
    }

    // ── Completion paths and teardown ────────────────────────────────────────

    [Fact]
    public void AStaleCompletionForAnOlderOffer_DoesNotTouchTheLiveOne()
    {
        // Review A F2 / probe P3: a completion naming an offer that is already gone must do nothing —
        // it must not decline whatever happens to be live now. Mutation: decline on any stale id (the
        // old Accept behavior) → offer 2 is cleared, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var first = engine.CurrentOffer!;
        FuelTheDwell(engine, first.Button, fromUs: FrameUs, inHitSamples: 24);   // fires
        engine.Clear();                                     // a teardown during the resolve
        engine.NotifyFixationEnded(1_000_000);

        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 1_100_000);
        var second = engine.CurrentOffer!;
        Check.That(second.Id).IsNotEqualTo(first.Id);

        engine.Accept(first.Id);                            // the late completion for offer 1

        Check.That(engine.CurrentOffer).IsNotNull();
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(second.Id);
    }

    [Fact]
    public void RejectedCompletion_CountsAsDecline_WhenItNamesTheLiveOffer()
    {
        // R3's other half: the state-based rejection (the IM calls Decline(snapshot.Id) when a pause or
        // keyboard toggle landed during the dispatch) releases the fired lock, so a new settle is
        // offered again. Mutation: keep a fired offer on decline (skip it in ExpireOrDecline) → the
        // next settle is Locked and no offer appears, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var offer = engine.CurrentOffer!;
        FuelTheDwell(engine, offer.Button, fromUs: FrameUs, inHitSamples: 24);
        Check.That(engine.IsFired).IsTrue();

        engine.Decline(offer.Id);                           // the rejected completion, named

        Check.That(engine.CurrentOffer).IsNull();

        engine.NotifyFixationEnded(1_000_000);
        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 1_100_000);
        Check.That(engine.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void ADeclineForAnOlderOffer_DoesNotTouchTheLiveOne()
    {
        // F2's guard on the decline path too. Mutation: drop the id guard in Decline(id) → offer 2 is
        // cleared, red.
        var engine = NewEngine();
        engine.NotifyFixationSettled(P, 0);
        var first = engine.CurrentOffer!;
        engine.Clear();
        engine.NotifyFixationEnded(1_000_000);
        engine.NotifyFixationSettled(new PhysicalPoint(2500, 1800), 1_100_000);
        var second = engine.CurrentOffer!;

        engine.Decline(first.Id);

        Check.That(engine.CurrentOffer!.Id).IsEqualTo(second.Id);
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

    [Fact]
    public void TwoShortExcursions600MsApart_OnlyPauseTheDwell_NeverResetIt()
    {
        // Ticket 7 review A's re-check, R13 (carried into ticket 8 as item 10): decision 9 says
        // slipping off the button pauses the fill for up to 0.5 s, and only a longer look away resets
        // it. Two wobbles more than the frame floor apart must not combine into a reset. The first
        // excursion's miss is cleared by the first in-hit sample of the next stretch; without that
        // clear, the second excursion dates from the first and resets (mutation at :380), so the
        // 990/1500 assertion below goes red.
        var engine = NewEngine(dwellMs: 1500);
        engine.NotifyFixationSettled(P, 0);
        var b = engine.CurrentOffer!.Button;

        Feed(engine, b, FrameUs, samples: 13);              // 30–390 ms: 390 credited
        Check.That(engine.DwellProgress).IsCloseTo(390.0 / 1500, 1e-9);

        Feed(engine, P, 420_000, samples: 7);               // excursion 1: 420–600 ms
        Feed(engine, b, 630_000, samples: 20);              // 630–1200 ms: 990 credited
        Check.That(engine.DwellProgress).IsCloseTo(990.0 / 1500, 1e-9);

        Feed(engine, P, 1_230_000, samples: 7);             // excursion 2: 1230–1410 ms
        Check.WithCustomMessage("excursion 2 reset the dwell: separate wobbles must only pause it")
            .That(engine.DwellProgress).IsCloseTo(990.0 / 1500, 1e-9);

        Feed(engine, b, 1_440_000, samples: 16);            // 1440–1890 ms: 1470 credited, no fire
        Check.That(engine.IsFired).IsFalse();

        engine.Update(b, 1_920_000, isValid: true);         // exactly once, on the 1500 ms sample
        Check.That(engine.IsFired).IsTrue();
    }

    /// <summary>Feeds <paramref name="inHitSamples"/> valid samples at an in-hit point, one frame apart.</summary>
    private static void FuelTheDwell(
        ActivationOfferEngine engine, PhysicalPoint point, long fromUs, int inHitSamples)
        => Feed(engine, point, fromUs, inHitSamples);
}
