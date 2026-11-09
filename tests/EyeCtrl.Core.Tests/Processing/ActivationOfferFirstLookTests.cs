namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Round-button B1: <see cref="ActivationOffer.FirstLookMs"/> — the time on the SAMPLE clock from the
/// offer being shown to its first valid sample inside the button's hit area, carried by the offer the
/// fire and expiry events hand out (the <c>Offer fired</c> / <c>Offer expired unused</c> lines write
/// it as <c>firstLookMs=</c>). Same geometry as <see cref="ActivationOfferEngineTests"/> (3840×2400,
/// ScaleX 2.0, ring 250, R 75: B is 360 px from P on the diagonal), with the settle at a non-zero
/// timestamp so an absolute-clock mutant cannot pass by accident.
/// </summary>
public class ActivationOfferFirstLookTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);
    private const double Scale = 2.0;
    private const double RingDip = 250;
    private const double FixationR = 75;
    private const long FrameUs = 30_000;
    private const long ShownUs = 5_000_000;

    private static ActivationOfferEngine NewEngine(int dwellMs = 700)
        => new(Screen, Scale, RingDip, dwellMs, FixationR);

    /// <summary>Settles at P at <see cref="ShownUs"/> and returns the live offer.</summary>
    private static ActivationOffer Settle(ActivationOfferEngine engine)
    {
        engine.Update(P, ShownUs, true);
        engine.NotifyFixationSettled(P, ShownUs);
        var offer = engine.CurrentOffer;
        Check.That(offer).IsNotNull();
        Check.That(offer!.ShownAtUs).IsEqualTo(ShownUs);
        Check.That(offer.FirstLookMs).IsNull();
        Check.That(offer.FirstLookLogValue).IsEqualTo("none");
        return offer;
    }

    private static long Feed(ActivationOfferEngine engine, PhysicalPoint point, long fromUs, int samples, bool valid = true)
    {
        long ts = fromUs;
        for (int i = 0; i < samples; i++)
        {
            engine.Update(point, ts, valid);
            ts += FrameUs;
        }
        return ts;
    }

    [Fact]
    public void AFiredOffer_CarriesTheShowToFirstLookTime_SetOnceOnTheFirstInHitSample()
    {
        // 10 samples still at P (outside the hit area), then onto B: the first in-hit sample is the
        // 11th after the show, at +330 ms. Mutants: measure from the wrong sample (the previous one →
        // 300), or overwrite on every in-hit sample (→ the fire sample's time) — both red.
        var engine = NewEngine();
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;
        var offer = Settle(engine);

        long ts = Feed(engine, P, ShownUs + FrameUs, 10);
        Check.That(engine.CurrentOffer!.FirstLookMs).IsNull();

        engine.Update(offer.Button, ts, true);
        Check.That(engine.CurrentOffer!.FirstLookMs).IsEqualTo(330.0);
        Check.That(engine.CurrentOffer!.FirstLookLogValue).IsEqualTo("330");

        Feed(engine, offer.Button, ts + FrameUs, 30);   // past the 700 ms dwell → fires

        Check.That(fired).HasSize(1);
        Check.WithCustomMessage("the fired offer carries the FIRST look, not the latest in-hit sample")
            .That(fired[0].FirstLookMs).IsEqualTo(330.0);
        Check.That(fired[0].ShownAtUs).IsEqualTo(ShownUs);
    }

    [Fact]
    public void ABlinkGapBeforeTheFirstLook_CountsUpToTheFirstValidInHitSample()
    {
        // The eyes go to the button through a blink: invalid samples AT B (the tracker's held
        // position) are not a look, and neither is the silence that follows. The first look is the
        // first VALID in-hit sample, here +30 (P) +5 invalid frames +200 ms silence → +410 ms.
        // Mutants: count invalid samples (→ 60), or date the look by the previous sample (→ 180).
        var engine = NewEngine();
        var offer = Settle(engine);

        long ts = Feed(engine, P, ShownUs + FrameUs, 1);                        // +30, at P
        ts = Feed(engine, offer.Button, ts, 5, valid: false);                   // +60..+180 invalid at B
        Check.WithCustomMessage("an invalid sample at B is not a look")
            .That(engine.CurrentOffer!.FirstLookMs).IsNull();

        ts = ts - FrameUs + 230_000;                                            // last invalid +180, then 230 ms of silence
        engine.Update(offer.Button, ts, true);                                  // +410: the first valid in-hit sample

        Check.That(engine.CurrentOffer!.FirstLookMs).IsEqualTo(410.0);
    }

    [Fact]
    public void FirstLook_AtExactlyTheHitRadius_CountsAsInside_LikeTheDwellsInclusiveHitTest()
    {
        // The engine's own hit test is `distance <= HitRadiusPx` (the dwell credits a sample exactly
        // on the rim), and the first look must use the same one. 150 is a multiple of B.Y's ulp, so
        // B.Y + 150 is exact and the distance is exactly the radius (asserted below).
        var engine = NewEngine();
        var offer = Settle(engine);
        double r = offer.HitRadiusPx;

        var justOutside = new PhysicalPoint(offer.Button.X, Math.BitIncrement(offer.Button.Y + r));
        var onTheRim = new PhysicalPoint(offer.Button.X, offer.Button.Y + r);
        Check.That(justOutside.DistanceTo(offer.Button)).IsStrictlyGreaterThan(r);
        Check.That(onTheRim.DistanceTo(offer.Button)).IsEqualTo(r);

        engine.Update(justOutside, ShownUs + FrameUs, true);
        Check.WithCustomMessage("a sample one ulp outside the rim is not a look")
            .That(engine.CurrentOffer!.FirstLookMs).IsNull();

        engine.Update(onTheRim, ShownUs + (2 * FrameUs), true);
        Check.WithCustomMessage("a sample exactly on the rim is inside, as the dwell counts it")
            .That(engine.CurrentOffer!.FirstLookMs).IsEqualTo(60.0);
    }

    [Fact]
    public void AnExpiredOfferNeverLookedAt_CarriesNone()
    {
        // The "popped up while reading" case: the offer's run ends and the gaze stays at P until the
        // linger runs out. The expiry event's offer has no first look.
        var engine = NewEngine();
        var expired = new List<ActivationOffer>();
        engine.OfferExpired += (offer, _, _) => expired.Add(offer);
        Settle(engine);

        engine.NotifyFixationEnded(ShownUs + FrameUs);
        Feed(engine, P, ShownUs + (2 * FrameUs), 50);   // 1.5 s > the 1 s linger

        Check.That(expired).HasSize(1);
        Check.That(expired[0].FirstLookMs).IsNull();
        Check.That(expired[0].FirstLookLogValue).IsEqualTo("none");
    }

    [Fact]
    public void AnExpiredOfferThatWasGlancedAt_CarriesItsFirstLook()
    {
        // A short look at B (+90 ms, three samples), then away: the expiry still reports when the
        // first look was.
        var engine = NewEngine();
        var expired = new List<ActivationOffer>();
        engine.OfferExpired += (offer, _, _) => expired.Add(offer);
        var offer = Settle(engine);

        long ts = Feed(engine, P, ShownUs + FrameUs, 2);                 // +30, +60 at P
        ts = Feed(engine, offer.Button, ts, 3);                          // +90..+150 on B
        engine.NotifyFixationEnded(ts);
        Feed(engine, P, ts, 50);                                         // away past the linger

        Check.That(expired).HasSize(1);
        Check.That(expired[0].FirstLookMs).IsEqualTo(90.0);
        Check.That(expired[0].FirstLookLogValue).IsEqualTo("90");
    }

    [Fact]
    public void AReplacementOffer_StartsItsOwnClock()
    {
        // A settle outside the no-move zone replaces the offer: the new one is shown at the
        // replacing settle and has no first look of its own yet, whatever the old one had.
        var engine = NewEngine();
        var first = Settle(engine);
        long ts = Feed(engine, first.Button, ShownUs + FrameUs, 2);      // first look on the old offer
        Check.That(engine.CurrentOffer!.FirstLookMs).IsEqualTo(30.0);

        engine.NotifyFixationEnded(ts);
        var elsewhere = new PhysicalPoint(2500, 1500);
        engine.Update(elsewhere, ts, true);
        engine.NotifyFixationSettled(elsewhere, ts);

        var replacement = engine.CurrentOffer!;
        Check.That(replacement.Id).IsNotEqualTo(first.Id);
        Check.That(replacement.ShownAtUs).IsEqualTo(ts);
        Check.That(replacement.FirstLookMs).IsNull();
    }

    // ── M1 (B1 review-01 F2): the show is dated by the sample that SHOWS the button ──

    [Fact]
    public void ADelayedShow_IsDatedByTheCrossingSample_AndTheFirstLookCountsFromIt()
    {
        // B3's show delay at the product's 1000 ms (the settle age is the detector's 500, so the
        // candidate waits 500 ms after the settle). The button appears on the first sample at least
        // 500 ms after the settle — +510 at 30 ms frames — and the first look one frame later is 30 ms
        // after the SHOW. Mutant: date the show by the candidate's settle → ShownAtUs = the settle and
        // firstLookMs = 540, red. (C4, "the previous sample", is equivalent on this path: Update has
        // already advanced the sample clock to the crossing sample when the candidate shows; the
        // re-offer test below is the one it reds.)
        var engine = new ActivationOfferEngine(Screen, Scale, RingDip, 700, FixationR) { OfferShowAfterMs = 1000 };
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;

        engine.Update(P, ShownUs, true);
        Check.That(engine.NotifyFixationSettled(P, ShownUs)).IsEqualTo(SettleOutcome.Deferred);

        long showUs = ShownUs;
        for (int i = 0; i < 40 && engine.CurrentOffer is null; i++)
        {
            showUs += FrameUs;
            engine.Update(P, showUs, true);
        }

        var offer = engine.CurrentOffer;
        Check.That(offer).IsNotNull();
        Check.That(showUs).IsEqualTo(ShownUs + 510_000);
        Check.WithCustomMessage("the show is the crossing sample, not the settle")
            .That(offer!.ShownAtUs).IsEqualTo(showUs);

        engine.Update(offer.Button, showUs + FrameUs, true);   // the first look, one frame after the show
        Check.That(engine.CurrentOffer!.FirstLookMs).IsEqualTo(30.0);

        Feed(engine, offer.Button, showUs + (2 * FrameUs), 30);   // past the 700 ms dwell → fires
        Check.That(fired).HasSize(1);
        Check.WithCustomMessage("firstLookMs = first look − show, not first look − settle")
            .That(fired[0].FirstLookMs).IsEqualTo(30.0);
        Check.That(fired[0].FirstLookLogValue).IsEqualTo("30");
    }

    [Fact]
    public void APendingReOffer_IsDatedByTheExpirySample_NotThePreviousOne()
    {
        // The other MakeOffer-from-Update path: the live offer expires, and the swallowed settle's
        // pending point takes over on that same sample, BEFORE the sample clock advances to it. P's
        // side of the no-move zone: 150 px from P toward B, 210 px from B (ActivationOfferEngineTests'
        // PsidePoint). Its samples neither hit nor extend, so the 1 s deadline (ShownUs + 1 s) passes
        // on the 24th sample, at +1020. Mutant C4 (ShownAtUs = the previous sample's timestamp) →
        // the re-offer is dated +990 and the next frame's first look reads 60, red.
        var engine = NewEngine();
        var first = Settle(engine);
        double unitX = (first.Button.X - P.X) / P.DistanceTo(first.Button);
        double unitY = (first.Button.Y - P.Y) / P.DistanceTo(first.Button);
        var pSide = new PhysicalPoint(P.X + (150 * unitX), P.Y + (150 * unitY));

        // The pending settle comes with no sample before it: a sample of the still-Offered run would
        // push the deadline (ActivationOfferEngineTests' swallowed-settle test has the same shape).
        long ts = ShownUs + 300_000;
        Check.That(engine.NotifyFixationSettled(pSide, ts)).IsEqualTo(SettleOutcome.Pending);

        for (int i = 0; i < 60 && engine.CurrentOffer!.Id == first.Id; i++)
        {
            ts += FrameUs;
            engine.Update(pSide, ts, true);
        }

        var reOffer = engine.CurrentOffer!;
        Check.That(reOffer.Id).IsEqualTo(first.Id + 1);
        Check.That(reOffer.Point).IsEqualTo(pSide);
        Check.That(ts).IsEqualTo(ShownUs + 1_020_000);
        Check.WithCustomMessage("the re-offer is shown on the expiry sample")
            .That(reOffer.ShownAtUs).IsEqualTo(ts);

        engine.Update(reOffer.Button, ts + FrameUs, true);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(reOffer.Id);
        Check.That(engine.CurrentOffer.FirstLookMs).IsEqualTo(30.0);
    }
}
