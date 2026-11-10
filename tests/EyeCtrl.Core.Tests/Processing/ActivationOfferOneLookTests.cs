namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// F3, rule KE ("one look fills the button", reading-proof report §2): the round button's fill comes
/// from ONE look, only a dwelling look keeps the button alive, and near-miss extensions are capped.
/// Engine-level sample scripts on the standalone engine (its own look segmentation: centroid within R,
/// the detector's 500 ms gap budget), at the owner's geometry and values: 3840×2400, ScaleX 2.0, ring
/// 200 (B 310 px from P, on the diagonal toward the centre), R 80, hit 200, dwell 500, linger 750, one
/// sample per 33 ms (the owner's tracker grid).
/// <para>
/// The first four tests fail on the engine before F3 (the report's "tests that fail today"); the five
/// guards pass before and after. Each test names the mutants it turns red (report §4, M1–M8, plus the
/// ticket's four).
/// </para>
/// </summary>
public class ActivationOfferOneLookTests
{
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);
    private const double Scale = 2.0;
    private const double RingDip = 200;
    private const double FixationR = 80;
    private const int DwellMs = 500;
    private const long FrameUs = 33_000;

    /// <summary>One engine and its sample clock. A look is a still fixation fed one sample per frame;
    /// a look that lasts the settle age is settled, and every look ends through
    /// <see cref="ActivationOfferEngine.NotifyFixationEnded"/>, as the wiring does.</summary>
    private sealed class Script
    {
        public ActivationOfferEngine Engine { get; } = new(Screen, Scale, RingDip, DwellMs, FixationR)
        {
            OfferExpiryMs = ActivationOfferEngine.ProductOfferExpiryMs,
        };

        public long Ts { get; private set; } = 2_000_000;
        public List<(ActivationOffer Offer, long Us)> Fired { get; } = new();
        public List<(ActivationOffer Offer, long Us, double Peak)> Expired { get; } = new();
        public double PeakFill { get; private set; }
        public ActivationOffer Offer { get; private set; } = null!;

        /// <summary>Unit P→B and its perpendicular, set by <see cref="Offered"/>.</summary>
        private double _ux, _uy;

        public Script()
        {
            Engine.OfferFired += o => Fired.Add((o, Ts));
            Engine.OfferExpired += (o, us, peak) => Expired.Add((o, us, peak));
        }

        public PhysicalPoint B => Offer.Button;

        /// <summary>Rests 600 ms at P (the settle at 500 makes the offer), then leaves: the offered run
        /// ends on the last P sample. Returns that sample's time, the last "strong" activity.</summary>
        public long Offered()
        {
            Look(P, 600);
            Offer = Engine.CurrentOffer!;
            Check.That(Offer).IsNotNull();
            double d = Offer.Button.DistanceTo(P);
            _ux = (Offer.Button.X - P.X) / d;
            _uy = (Offer.Button.Y - P.Y) / d;
            return Ts;
        }

        /// <summary>A point <paramref name="dist"/> px from B: "down" (screen), "beyond" (away from P),
        /// "side" (perpendicular to P→B), "toward" (toward P).</summary>
        public PhysicalPoint FromB(double dist, string dir) => dir switch
        {
            "down" => new(B.X, B.Y + dist),
            "beyond" => new(B.X + (_ux * dist), B.Y + (_uy * dist)),
            "side" => new(B.X - (_uy * dist), B.Y + (_ux * dist)),
            "side-" => new(B.X + (_uy * dist), B.Y - (_ux * dist)),
            "toward" => new(B.X - (_ux * dist), B.Y - (_uy * dist)),
            _ => B,
        };

        public void Sample(PhysicalPoint at, bool valid = true)
        {
            Ts += FrameUs;
            Engine.Update(at, Ts, valid);
            if (Engine.CurrentOffer is not null)
                PeakFill = Math.Max(PeakFill, Engine.DwellProgress);
        }

        /// <summary>A still look of <paramref name="ms"/>: settled at the settle age, ended after.</summary>
        public void Look(PhysicalPoint at, int ms)
        {
            long start = Ts;
            bool settled = false;
            while (Ts + FrameUs - start <= ms * 1000L)
            {
                Sample(at);
                if (!settled && Ts - start >= Engine.SettleAgeMs * 1000L)
                {
                    settled = true;
                    Engine.NotifyFixationSettled(at, Ts);
                }
            }
            Engine.NotifyFixationEnded(Ts);
        }

        /// <summary>Reads far from the button for 1.2 s, in looks too short to settle (a settle there
        /// would replace the offer instead of letting it expire).</summary>
        public void Away()
        {
            for (int i = 0; i < 3; i++)
                Look(new PhysicalPoint(2400 + (300 * i), 1800), 400);
        }

        public void Blink(int ms)
        {
            long start = Ts;
            while (Ts + FrameUs - start <= ms * 1000L)
                Sample(B, valid: false);
        }

        /// <summary>Fire time from the first sample after <paramref name="fromUs"/>, in ms.</summary>
        public double FiredAfterMs(long fromUs) => (Fired.Single().Us - fromUs) / 1000.0;
    }

    // ── The four that fail before F3 ────────────────────────────────────────────

    [Fact]
    public void TwoConsecutive270MsLooks_150PxApart_InsideTheHitArea_DoNotFire()
    {
        // Two reading words, both 75 px from B and 150 px from each other: before F3 their 264 + 264 ms
        // (8 samples each) summed past the 500 ms fill and the magnifier opened. Now the second look
        // restarts the fill. Mutants: M1 (never reset), M7 ("moved" against B: both are 75 px from B),
        // no reset — red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(75, "side"), 270);
        s.Look(s.FromB(75, "side-"), 270);
        s.Away();   // far away: the button runs out

        Check.WithCustomMessage("two words in the hit area fired the button").That(s.Fired).IsEmpty();
        Check.That(s.Expired).HasSize(1);
        Check.That(s.Expired[0].Offer.FillRestarts).IsEqualTo(1);
    }

    [Fact]
    public void TheDryRun2SkimPath_TheLineUnderTheWord_At250MsPerWord_DoesNotFire_AndPeaksAtMost60Percent()
    {
        // Dry run 2's F-1: the eye drops to the line 70 px under the word and reads on, 150 px words at
        // 250 ms. Two words land in the hit area, 164 and 170 px from B (as in dry run 2), and the
        // filter's two in-between points per step add three more in-hit samples; before F3 that was
        // 594 ms of fill and it fired. Mutants: M1, M7, no reset — red.
        var s = new Script();
        s.Offered();
        var line = new PhysicalPoint(P.X, P.Y + 70);
        for (int i = 0; i < 10; i++)
        {
            var word = new PhysicalPoint(line.X + (150 * i), line.Y);
            if (i > 0)
            {
                s.Sample(new PhysicalPoint(word.X - 100, word.Y));   // the One-Euro lag: 1/3 and 2/3
                s.Sample(new PhysicalPoint(word.X - 50, word.Y));
            }
            s.Look(word, 250);
        }
        s.Away();

        Check.WithCustomMessage("reading the line under the word fired the button").That(s.Fired).IsEmpty();
        Check.WithCustomMessage($"peak fill {s.PeakFill:P0}").That(s.PeakFill).IsLessOrEqualThan(0.6);
    }

    [Fact]
    public void ATransitSampleThroughTheHitArea_BetweenTwoNearMissLooks_DoesNotExtendTheButton()
    {
        // A saccade between two near-miss spots (230 px beside B, 250 px beyond it) passes 170 px from B:
        // its one in-between sample is not a look at the button, and before F3 it extended the button
        // like one. Mutants: M4 (no 100 ms dwelling test), extensions from immature samples, maturity
        // 100 → 0 — red.
        var s = new Script();
        s.Offered();
        var n1 = s.FromB(230, "side");
        var n2 = s.FromB(250, "beyond");
        s.Look(n1, 600);
        long before = s.Engine.CurrentOffer!.ExpiresAtUs;

        var transit = new PhysicalPoint((n1.X + n2.X) / 2, (n1.Y + n2.Y) / 2);
        Check.That(transit.DistanceTo(s.B)).IsStrictlyLessThan(LayoutConstants.ActivationButtonHitRadiusPx);
        s.Sample(transit);

        Check.WithCustomMessage("the transit sample extended the button")
            .That(s.Engine.CurrentOffer!.ExpiresAtUs).IsEqualTo(before);
    }

    [Fact]
    public void NearMissLooksEvery600Ms_At230To250Px_TheButtonClosesWithinLingerPlusCap()
    {
        // Dry run 2's R3 worst case: slow re-reading of words just outside the hit area. Each settle is a
        // near miss and each extended the button without end (still up after 5 s, the ring held on the old
        // word). Now near-miss extensions stop at linger + 750 ms past the last real activity (the leave
        // from P). Mutants: M5 (no cap), M8 (cap from the last near miss), M4 (the transit samples extend)
        // — red.
        var s = new Script();
        long strong = s.Offered();
        var spots = new[] { s.FromB(230, "side"), s.FromB(250, "beyond") };
        for (int i = 0; i < 8 && s.Expired.Count == 0; i++)
        {
            var at = spots[i % 2];
            var next = spots[(i + 1) % 2];
            s.Look(at, 600);
            s.Sample(new PhysicalPoint((at.X + next.X) / 2, (at.Y + next.Y) / 2));   // the saccade's transit
        }

        Check.WithCustomMessage("the near misses kept the button up").That(s.Expired).HasSize(1);
        long capUs = strong + ((ActivationOfferEngine.ProductOfferExpiryMs + ActivationOfferEngine.NearMissCapMs) * 1000L);
        Check.That(s.Expired[0].Us).IsStrictlyGreaterThan(capUs);
        Check.That(s.Expired[0].Us).IsLessOrEqualThan(capUs + FrameUs);
        Check.That(s.Fired).IsEmpty();
    }

    // ── The five guards (pass before and after) ─────────────────────────────────

    [Fact]
    public void Guard_OnB_FiresAt500MsOfCredit()
    {
        var s = new Script();
        long left = s.Offered();
        s.Look(s.B, 800);

        // The first sample on B credits its 33 ms like every other: the 16th sample reaches 528 ≥ 500.
        Check.That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsEqualTo(16 * 33.0);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void Guard_ALandingAt160Px_Fires()
    {
        foreach (var dir in new[] { "down", "beyond", "side", "toward" })
        {
            var s = new Script();
            long left = s.Offered();
            s.Look(s.FromB(160, dir), 800);
            Check.WithCustomMessage(dir).That(s.Fired).HasSize(1);
            Check.WithCustomMessage(dir).That(s.FiredAfterMs(left)).IsEqualTo(16 * 33.0);
        }
    }

    [Fact]
    public void Guard_ABlinkOf150Ms_HoldsTheFill()
    {
        var s = new Script();
        long left = s.Offered();
        s.Look(s.B, 250);
        s.Blink(150);
        s.Look(s.B, 600);

        // 7 samples (231 ms) before the blink, 4 invalid, then the 9 that reach 528: the fill held.
        Check.That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsEqualTo((7 + 4 + 9) * 33.0);
    }

    [Fact]
    public void Guard_A250MsGlanceOffAndBackToTheSameSpot_ContinuesTheFill()
    {
        // M2 (reset on any new look, even back within R) restarts it: 264 + 264 + 528 → red.
        foreach (var glance in new[] { "P", "beyond", "side" })
        {
            var s = new Script();
            long left = s.Offered();
            s.Look(s.B, 250);
            s.Look(glance == "P" ? P : s.FromB(glance == "beyond" ? 300 : 220, glance), 250);
            s.Look(s.B, 800);

            Check.WithCustomMessage(glance).That(s.Fired).HasSize(1);
            Check.WithCustomMessage(glance).That(s.FiredAfterMs(left)).IsLessOrEqualThan(900);
            Check.WithCustomMessage(glance).That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        }
    }

    [Fact]
    public void Guard_ASlipToANewSpotMoreThanRAway_StillFires()
    {
        // The 5th sitting's #2/#17: 400 ms on B, then a slip to 165 / 190 px below it.
        foreach (var slip in new[] { 165.0, 190.0 })
        {
            var s = new Script();
            s.Offered();
            s.Look(s.FromB(40, "down"), 400);
            s.Look(s.FromB(slip, "down"), 1200);
            Check.WithCustomMessage($"slip {slip}").That(s.Fired).HasSize(1);
        }
    }

    [Fact]
    public void ASlip_IsANewLook_ItWaits100Ms_ThenRestartsTheFill()
    {
        // The slip is a new look more than R away: its first 4 samples (0–99 ms old) credit nothing, the
        // 5th (132 ms) restarts the fill and credits, and 15 more reach 528. Mutant: restart at once
        // (maturity 100 → 0) → it fires 4 samples sooner; M1 → it fires off the old fill. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "down"), 400);
        long slipped = s.Ts;
        s.Look(s.FromB(165, "down"), 1200);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(1);
        Check.That(s.FiredAfterMs(slipped)).IsEqualTo((4 + 16) * 33.0);
    }

    // ── KE's parts, one by one ──────────────────────────────────────────────────

    [Fact]
    public void AMovedLook_AddsNothingBeforeItIs100MsOld()
    {
        // 462 ms on one word, then 99 ms on another word in the hit area, 150 px away, then off. Before
        // its maturity the moved look must not credit: M3 (credit at once, the KC rule) and maturity
        // 100 → 0 with the credit kept fill 462 + 66 → fires. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(75, "side"), 462);
        s.Look(s.FromB(75, "side-"), 99);
        s.Away();

        Check.WithCustomMessage("a 99 ms look completed another word's fill").That(s.Fired).IsEmpty();
    }

    [Fact]
    public void ATransitSampleElsewhereInTheHitArea_NeitherRestartsNorMovesTheFill()
    {
        // 264 ms on B, one in-between sample 150 px off it (inside the hit), then back on B: the fill
        // goes on. Maturity 100 → 0 lets the transit sample restart the fill AND take the anchor, so the
        // return restarts it again → fires later than 0.6 s. Red.
        var s = new Script();
        long left = s.Offered();
        s.Look(s.B, 250);
        s.Sample(s.FromB(150, "side"));
        s.Look(s.B, 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(600);
    }

    [Fact]
    public void TheFillsAnchor_IsTheLooksCentroid_NotItsLandingSample()
    {
        // The look lands 70 px left of where it then stays (within R: one look), glances away, and comes
        // back 60 px right of the stay — 130 px from the landing sample, 60 from the look's centroid. It
        // is the same spot, so the fill goes on. M6 (anchor at the landing sample) restarts it. Red.
        var s = new Script();
        long left = s.Offered();
        var stay = s.FromB(20, "down");
        s.Sample(new PhysicalPoint(stay.X - 70, stay.Y));
        s.Look(stay, 250);
        s.Look(P, 250);
        s.Look(new PhysicalPoint(stay.X + 60, stay.Y), 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(900);
    }

    [Fact]
    public void AMatureInHitSample_ExtendsTheButton_AndRestartsTheCap()
    {
        // The other half of the dwelling-hit rule: a real look at the button (≥ 100 ms) extends it and
        // counts as real activity, so near misses after it get a fresh cap. Mutant: never extend from
        // in-hit samples (or never count them as strong) → the button closes 1.5 s after the leave. Red.
        var s = new Script();
        long left = s.Offered();
        s.Look(s.FromB(230, "side"), 600);
        s.Look(s.FromB(150, "beyond"), 200);   // a 200 ms look inside the hit: real activity
        long strong = s.Ts;
        s.Look(s.FromB(250, "side-"), 600);
        s.Look(s.FromB(230, "side"), 600);
        s.Look(s.FromB(250, "side-"), 600);
        s.Away();

        // The first offer's end (a later near-miss settle with no button up makes a new one).
        Check.That(s.Expired).Not.IsEmpty();
        Check.That(s.Expired[0].Offer.Id).IsEqualTo(1);
        Check.That(s.Expired[0].Us).IsStrictlyGreaterThan(strong + (1500 * 1000L));
        Check.That(s.Expired[0].Us).IsLessOrEqualThan(strong + (1500 * 1000L) + FrameUs);
        Check.That(strong - left).IsStrictlyGreaterThan(700 * 1000L);
    }

    [Fact]
    public void TheDetectorsVerdict_WhenGiven_SegmentsTheLooks()
    {
        // The production wiring passes the fixation detector's own run verdict, so the engine's looks
        // are the detector's runs. Here the verdict says "same run" across a 150 px hop, so there is no
        // new look and no restart: the two halves fill one 500 ms. (Without a verdict the hop is a new look,
        // as the first test shows.) Mutant: ignore the verdict → the hop restarts the fill. Red.
        var s = new Script();
        long left = s.Offered();
        var w1 = s.FromB(75, "side");
        var w2 = s.FromB(75, "side-");
        long ts = s.Ts;
        bool fired = false;
        s.Engine.OfferFired += _ => fired = true;
        for (int i = 0; i < 9; i++)
            s.Engine.Update(w1, ts += FrameUs, true, continuesRun: null, continuesLook: i > 0);
        for (int i = 0; i < 9 && !fired; i++)
            s.Engine.Update(w2, ts += FrameUs, true, continuesRun: null, continuesLook: true);

        Check.That(fired).IsTrue();
        Check.That(s.Engine.CurrentOffer!.FillRestarts).IsEqualTo(0);
        Check.That(left).IsStrictlyLessThan(ts);
    }

    [Fact]
    public void ANewOffer_StartsWithNoAnchor_AndNoRestarts()
    {
        // A look that anchored the last offer's fill must not move the next offer's: the new button's
        // first look is its own anchor, and it fills from its first sample.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(75, "side"), 250);
        s.Away();
        Check.That(s.Expired).HasSize(1);

        long left = s.Offered();
        s.Look(s.FromB(75, "side-"), 800);
        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(left)).IsEqualTo(16 * 33.0);
    }
}
