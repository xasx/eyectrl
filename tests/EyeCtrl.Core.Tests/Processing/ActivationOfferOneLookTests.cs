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
/// <para>
/// F3 fixup-01 (KE-D2, reading-proof-2): a look on the drawn disc (within 80 px of B) is never a look
/// elsewhere, so the KE tests' "reading words" sit 120 px from B, off the disc. The last section holds
/// reading-proof-2's six tests that fail on F3 (pursuit, offset pursuit, hops, land-and-correct, held
/// near miss, F5) and pins for review-01's surviving mutants Y7 and Y8.
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
        public ActivationOfferEngine Engine { get; }

        /// <summary>The sample period of this script (33 ms, or 11 111 µs for 90 Hz).</summary>
        public long FrameUs { get; }

        public long Ts { get; private set; } = 2_000_000;
        public List<(ActivationOffer Offer, long Us)> Fired { get; } = new();
        public List<(ActivationOffer Offer, long Us, double Peak)> Expired { get; } = new();
        public double PeakFill { get; private set; }
        public ActivationOffer Offer { get; private set; } = null!;

        /// <summary>Unit P→B and its perpendicular, set by <see cref="Offered"/>.</summary>
        private double _ux, _uy;

        public Script(long frameUs = ActivationOfferOneLookTests.FrameUs, int dwellMs = DwellMs)
        {
            FrameUs = frameUs;
            Engine = new(Screen, Scale, RingDip, dwellMs, FixationR) { OfferExpiryMs = ActivationOfferEngine.ProductOfferExpiryMs };
            Engine.OfferFired += o => Fired.Add((o, Ts));
            Engine.OfferExpired += (o, us, peak) => Expired.Add((o, us, peak));
        }

        public PhysicalPoint B => Offer.Button;

        /// <summary>Rests 600 ms at P (the settle at 500 makes the offer), then leaves: the offered run
        /// ends on the last P sample. Returns that sample's time, the last "strong" activity.</summary>
        public long Offered() => OfferedAt(P);

        /// <summary><see cref="Offered"/> at another word: a settle there makes (or replaces) the offer.</summary>
        public long OfferedAt(PhysicalPoint word)
        {
            Look(word, 600);
            Offer = Engine.CurrentOffer!;
            Check.That(Offer).IsNotNull();
            Check.That(Offer.Point).IsEqualTo(word);
            double d = Offer.Button.DistanceTo(word);
            _ux = (Offer.Button.X - word.X) / d;
            _uy = (Offer.Button.Y - word.Y) / d;
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

        /// <summary>One sample. <paramref name="raw"/> is the raw point passed alongside (null: none, so every
        /// new look counts as entered by a jump, as for the replays).</summary>
        public void Sample(PhysicalPoint at, bool valid = true, PhysicalPoint? raw = null)
        {
            Ts += FrameUs;
            Engine.Update(at, Ts, valid, rawPoint: raw);
            if (Engine.CurrentOffer is not null)
                PeakFill = Math.Max(PeakFill, Engine.DwellProgress);
        }

        /// <summary>A still look of <paramref name="ms"/>: settled at the settle age, ended after. With
        /// <paramref name="withRaw"/> the raw point (the same point: no filter here) is passed too.</summary>
        public void Look(PhysicalPoint at, int ms, bool withRaw = false)
        {
            long start = Ts;
            bool settled = false;
            while (Ts + FrameUs - start <= ms * 1000L)
            {
                Sample(at, raw: withRaw ? at : null);
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

        /// <summary>A point offset from B by (<paramref name="dx"/>, <paramref name="dy"/>) screen px.</summary>
        public PhysicalPoint At(double dx, double dy) => new(B.X + dx, B.Y + dy);

        /// <summary>Samples the tracker lost: the clock moves on and the engine sees nothing (the gap
        /// interpolator holds them back in production).</summary>
        public void Lose(int samples) => Ts += samples * FrameUs;

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
        // Two reading words inside the hit area but off the drawn disc, both 120 px from B and 240 px
        // from each other: before F3 their 264 + 264 ms (8 samples each) summed past the 500 ms fill and
        // the magnifier opened. Now the second look restarts the fill. (F3 fixup-01: words 75 px from B
        // are on the disc and count as one look — see the disc tests below.) Mutants: M1 (never reset),
        // M7 ("moved" against B: both are 120 px from B), no reset, a disc as wide as the hit — red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(120, "side"), 270);
        s.Look(s.FromB(120, "side-"), 270);
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
        // 462 ms on one word, then 99 ms on another word in the hit area, 240 px away (both off the disc), then off. Before
        // its maturity the moved look must not credit: M3 (credit at once, the KC rule) and maturity
        // 100 → 0 with the credit kept fill 462 + 66 → fires. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(120, "side"), 462);
        s.Look(s.FromB(120, "side-"), 99);
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
        // are the detector's runs. Here the verdict says "same run" across a 240 px hop off the disc, so there is no
        // new look and no restart: the two halves fill one 500 ms. (Without a verdict the hop is a new look,
        // as the first test shows.) Mutant: ignore the verdict → the hop restarts the fill. Red.
        var s = new Script();
        long left = s.Offered();
        var w1 = s.FromB(120, "side");
        var w2 = s.FromB(120, "side-");
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
        // first look is its own anchor, and it fills from its first sample. The old offer is REPLACED by a
        // settle elsewhere while its fill still has an owner (the 500 ms rest is not yet a 500 ms absence,
        // so ResetFill has not run): only MakeOffer's clear protects the new one. Mutant X3 (MakeOffer
        // keeps the anchor) → the new first look waits and counts a restart. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(120, "side"), 250);
        long left = s.OfferedAt(new PhysicalPoint(2400, 1300));
        Check.That(s.Expired).IsEmpty();

        s.Look(s.FromB(120, "side-"), 800);
        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(left)).IsEqualTo(16 * 33.0);
    }

    // ── F3 fixup-01: KE-D2 (reading-proof-2) ────────────────────────────────────

    /// <summary>Follows the rising fill line, closed loop on the engine's progress: the disc is 200 px
    /// tall at scale 2.0, and the fill rises from 95 px below B to 95 px above it over one dwell.</summary>
    private static void PursueTheFill(Script s, double offsetX, int maxSamples = 300)
    {
        for (int i = 0; i < maxSamples && s.Fired.Count == 0; i++)
        {
            var at = new PhysicalPoint(s.B.X + offsetX, s.B.Y + 95 - (190 * s.Engine.DwellProgress));
            s.Sample(at, raw: at);
        }
    }

    [Theory]
    [InlineData(33_000L, 500)]
    [InlineData(11_111L, 500)]
    [InlineData(33_000L, 600)]
    [InlineData(11_111L, 600)]
    public void FollowingTheRisingFill_UpTheDisc_Fires(long frameUs, int dwellMs)
    {
        // Review-01 F1: the eye follows the fill line; the detector splits the pursuit into looks, and under F3
        // each one restarted the fill — the button never fired. Now looks on the disc never restart, and a
        // look reached without a raw jump is the same look. Mutants: no disc rule AND no raw rule, a disc as
        // small as 0 — red.
        var s = new Script(frameUs, dwellMs);
        long left = s.Offered();
        PursueTheFill(s, offsetX: 0);

        Check.WithCustomMessage($"{frameUs} µs / {dwellMs}: peak {s.PeakFill:P0}").That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(dwellMs + 70);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Theory]
    [InlineData(33_000L, 500)]
    [InlineData(11_111L, 500)]
    [InlineData(33_000L, 600)]
    [InlineData(11_111L, 600)]
    public void FollowingTheFill_With120PxTrackerOffset_OffTheDisc_Fires(long frameUs, int dwellMs)
    {
        // The owner's reported gaze is often well off target: the same pursuit 120 px to the side never
        // touches the disc, so only the raw-jump rule keeps the fill (the eye slid, it did not jump).
        // Mutants: no raw rule, a jump threshold of 30 px (sample steps count as jumps), raw points treated
        // as filtered — red.
        var s = new Script(frameUs, dwellMs);
        long left = s.Offered();
        PursueTheFill(s, offsetX: 120);

        Check.WithCustomMessage($"{frameUs} µs / {dwellMs}: peak {s.PeakFill:P0}").That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(dwellMs + 70);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void WithoutARawPoint_TheOffsetPursuitRestarts_AsInF3()
    {
        // Null raw (the replays, a standalone engine) counts every new look as entered by a jump: F3's
        // behaviour, so the replays are unchanged. Mutant N10 (null raw = "smooth") → it fires. Red.
        var s = new Script();
        s.Offered();
        for (int i = 0; i < 60 && s.Fired.Count == 0; i++)
            s.Sample(new PhysicalPoint(s.B.X + 120, s.B.Y + 95 - (190 * s.Engine.DwellProgress)));

        Check.That(s.Fired).IsEmpty();
    }

    [Fact]
    public void HopsOf100PxEvery300Ms_BetweenThreeSpotsOnTheDisc_FireAtOnce()
    {
        // Review-01 probe E: each hop is a raw jump to a new look 100 px away, but all three spots lie on the
        // disc (50 / 50 / 60 px from B), so they are one look. Mutants: no disc rule (N3), the disc at 40 px
        // — red.
        var s = new Script();
        long left = s.Offered();
        var spots = new[] { s.FromB(50, "side"), s.FromB(50, "side-"), s.FromB(60, "down") };
        for (int i = 0; i < 6 && s.Fired.Count == 0; i++)
            s.Look(spots[i % 3], 300, withRaw: true);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(DwellMs + 70);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void LandingAt180Px_ThenCorrectingOntoB_KeepsTheFill()
    {
        // Review-01 F3: land off the disc, then correct onto the button. Under F3 the correction restarted
        // the fill (+0.4–0.6 s); the corrected look is on the disc, so it carries on. Mutant N3 → red.
        var s = new Script();
        long left = s.Offered();
        s.Look(s.FromB(180, "down"), 300, withRaw: true);
        s.Look(s.B, 800, withRaw: true);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.FiredAfterMs(left)).IsLessOrEqualThan(DwellMs + 70);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void HeldJustOutsideTheHitFor2s_AfterALookAtB_TheButtonWaits()
    {
        // Review-01 F2: 400 ms on B, then a steady look 240 px off (a near miss) for 2 s, then back. Under F3
        // the cap closed the button at 1.5 s past the look at B, and the return found nothing. cap2: the
        // one near-miss look that follows a look at the button extends without the cap. Mutant: no cap2 →
        // red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "down"), 400);
        s.Look(s.FromB(240, "down"), 2000);
        s.Look(s.B, 800);

        Check.That(s.Expired).IsEmpty();
        Check.That(s.Fired).HasSize(1);
    }

    [Fact]
    public void AfterTheFillWasLost_ALookElsewhere_FillsAtOnce_AndCountsNoRestart()
    {
        // Review-01 F5: 300 ms on B, then 561 ms away (the fill resets after 500 ms), then a look elsewhere
        // in the hit area (150 px beside B, off the disc, > R from the old anchor). There is no fill to lose,
        // so it neither waits 100 ms nor counts a restart. Mutant N9 (ResetFill keeps the owner) → it waits 4
        // samples and the offer reads fillRestarts=1. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "down"), 300);
        s.Look(s.FromB(300, "beyond"), 300);
        s.Look(s.FromB(300, "side"), 270);
        long back = s.Ts;
        s.Look(s.FromB(150, "side"), 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(back)).IsEqualTo(16 * 33.0);
    }

    [Fact]
    public void TheAnchorFollowsTheOwningLooksCentroid_NotItsPointAtTheTakeOver()
    {
        // Review-01 Y7: a look off the disc drifts 130 px while it lasts (one look: each sample stays within R
        // of the running centroid), glances away, and comes back to where the drift ended — 65 px from the
        // look's centroid, 106 px from where it was when it took the fill. It is the same spot: the fill goes
        // on. Mutant Y7 (anchor frozen at the take-over) → a restart. Red.
        var s = new Script();
        long left = s.Offered();
        var q = s.FromB(140, "side");
        var beyond = s.FromB(1, "beyond");
        double bx = beyond.X - s.B.X, by = beyond.Y - s.B.Y;   // unit away from the word
        for (int i = 0; i < 12; i++)
            s.Sample(new PhysicalPoint(q.X + (bx * 130 * i / 11), q.Y + (by * 130 * i / 11)));
        var end = new PhysicalPoint(q.X + (bx * 130), q.Y + (by * 130));
        Check.That(end.DistanceTo(s.B)).IsStrictlyLessThan(LayoutConstants.ActivationButtonHitRadiusPx);
        s.Look(P, 250);
        s.Look(end, 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        Check.That(s.FiredAfterMs(left)).IsEqualTo((12 + 7 + 4) * 33.0);
    }

    [Fact]
    public void ALookThatSlidesOffTheDiscBeforeItMatures_RestartsFromItsOwnCredit()
    {
        // Review-01 Y8 (report D3): the new look's first sample is on the disc and within R of the anchor (it
        // credits), then it slides off and away before 100 ms (the next samples are held); at maturity it takes
        // the fill over from its OWN credit (33 ms), not from 0. Mutant Y8 (restart to a literal 0) → one
        // sample later. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "side-"), 300);   // the anchor, on the disc's far side
        long slid = s.Ts;
        s.Sample(s.FromB(60, "side"));       // a new look (100 px from the anchor), on the disc: credits
        s.Sample(s.FromB(90, "side"));       // centroid 75: still on the disc, credits
        s.Sample(s.FromB(120, "side"));      // centroid 90: off the disc, moved, young: held
        s.Look(s.FromB(150, "side"), 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(1);
        // 2 credited, 2 held (66 / 99 ms), the 5th restarts at its own 66 + 33, then 13 more reach 528.
        Check.That(s.FiredAfterMs(slid)).IsEqualTo(18 * 33.0);
    }

    // ── F3 fixup-02 (review-02) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(33_000L, 2)]
    [InlineData(11_111L, 9)]
    public void AReadingSaccadeHiddenByLostSamples_StillCountsAsAJump(long frameUs, int lost)
    {
        // Review-02 N1: the tracker drops samples during saccades. Two words off the disc, 120 px from B and
        // 150 px apart, separated by about 100 ms of lost samples: the second word's first raw point has no
        // neighbour in the 80 ms window, so without the last point before the gap the saccade read as "no
        // jump", the second word carried the fill on, and reading fired again. Mutant: drop hasPrev → red.
        var s = new Script(frameUs);
        s.Offered();
        s.Look(s.At(-75, 93.7), 330, withRaw: true);
        s.Lose(lost);
        s.Look(s.At(75, 93.7), 330, withRaw: true);

        Check.That(s.Fired).IsEmpty();
        Check.That(s.Engine.CurrentOffer!.FillRestarts).IsEqualTo(1);
    }

    [Theory]
    [InlineData(33_000L, 2)]
    [InlineData(11_111L, 9)]
    public void ASlideAcrossLostSamples_IsStillOneLook(long frameUs, int lost)
    {
        // The control: the same two spots, but the eye slides from one to the other (raw steps of 15 px)
        // and the samples are lost mid-slide with the eye barely moving (15 px across the gap). No jump, so
        // the fill carries on and fires.
        var s = new Script(frameUs);
        s.Offered();
        s.Look(s.At(-75, 93.7), 330, withRaw: true);
        for (int i = 1; i <= 10 && s.Fired.Count == 0; i++)
        {
            if (i == 5)
                s.Lose(lost);
            var at = s.At(-75 + (15 * i), 93.7);
            s.Sample(at, raw: at);
        }
        for (int i = 0; i < 40 && s.Fired.Count == 0; i++)
            s.Sample(s.At(75, 93.7), raw: s.At(75, 93.7));

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void A90HzSaccadeSpreadOverSevenSamples_ReadsAsAJump()
    {
        // Review-02 Z7: at 90 Hz a saccade of 120 px can arrive as 7 samples of about 17 px. The 80 ms window
        // must hold all of them (the ring holds 32), so the new look began with a jump and takes the fill
        // over. Mutant: a 4-entry ring (33 ms at 90 Hz: 51 px) → no jump, no restart. Red.
        var s = new Script(11_111L);
        s.Offered();
        s.Look(s.At(-75, 93.7), 330, withRaw: true);
        for (int i = 1; i <= 7; i++)
        {
            var at = s.At(-75 + (17.14 * i), 93.7);
            s.Sample(at, raw: at);
        }
        s.Look(s.At(45, 93.7), 300, withRaw: true);

        Check.That(s.Fired).IsEmpty();
        Check.That(s.Engine.CurrentOffer!.FillRestarts).IsEqualTo(1);
    }

    [Fact]
    public void An600PxPerSecondSlideOffTheDisc_IsNotAJump()
    {
        // Review-02 Z5: a slide at 600 px/s between two spots off the disc moves 48 px in the 80 ms window
        // (no jump: the fill carries on) but 72 px in 120 ms. Mutant: the window at 120 ms → a jump, a
        // restart. Red.
        var s = new Script(11_111L);
        s.Offered();
        s.Look(s.At(-75, 93.7), 330, withRaw: true);
        for (int i = 1; i <= 22 && s.Fired.Count == 0; i++)
        {
            var at = s.At(-75 + (6.667 * i), 93.7);
            s.Sample(at, raw: at);
        }
        for (int i = 0; i < 60 && s.Fired.Count == 0; i++)
            s.Sample(s.At(72, 93.7), raw: s.At(72, 93.7));

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
    }

    [Fact]
    public void ANewLookAtB_ReArmsCap2_ForTheNextHeldNearMiss()
    {
        // Review-02 Z9: on B, held just outside for 2 s (the exempt look), on B again, then held just outside
        // at ANOTHER spot for 2 s, then back: the second look at B re-arms the exemption, so the button is
        // still up. Mutant: a look at B does not clear the exempt look → the second hold is capped and the
        // button closes. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "down"), 300);
        s.Look(s.FromB(240, "down"), 2000);
        s.Look(s.FromB(40, "down"), 300);
        s.Look(s.FromB(240, "side"), 2000);
        s.Look(s.B, 800);

        Check.That(s.Expired).IsEmpty();
        Check.That(s.Fired).HasSize(1);
    }

    [Fact]
    public void ANewOffer_DoesNotInheritCap2_FromALookAtTheOldButton()
    {
        // Review-02 Z16: a look at offer 1's button arms the exemption; a settle elsewhere makes offer 2; a
        // near miss held at offer 2 without ever looking at its button is capped: it closes at linger + cap
        // after the new offer's run. Mutant: MakeOffer keeps the arm → offer 2 stays up. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.FromB(40, "down"), 300);
        long strong = s.OfferedAt(new PhysicalPoint(2400, 1300));
        s.Look(s.FromB(240, "beyond"), 2500);

        Check.That(s.Expired).HasSize(1);
        long capUs = strong + ((ActivationOfferEngine.ProductOfferExpiryMs + ActivationOfferEngine.NearMissCapMs) * 1000L);
        Check.That(s.Expired[0].Us).IsStrictlyGreaterThan(capUs);
        Check.That(s.Expired[0].Us).IsLessOrEqualThan(capUs + FrameUs);
    }

    [Fact]
    public void AfterASilenceLostTheFill_ALookElsewhere_FillsAtOnce_AndCountsNoRestart()
    {
        // Review-02 Z11, F5 at the SILENCE site: 300 ms on B, 528 ms with no samples, then two samples on the
        // disc (accepted by the fill, so the silence branch loses the fill), then a jump to a spot off the
        // disc, > R from the old anchor. The lost fill has no owner: the spot fills at once and counts no
        // restart. Mutant: the silence branch keeps the owner → the spot waits and counts a restart. Red.
        var s = new Script();
        s.Offered();
        s.Look(s.B, 300);
        s.Lose(15);                               // 528 ms between samples: past the 500 ms budget
        s.Sample(s.FromB(40, "side-"));
        s.Sample(s.FromB(40, "side-"));
        long side = s.Ts;
        s.Look(s.FromB(150, "side"), 800);

        Check.That(s.Fired).HasSize(1);
        Check.That(s.Fired[0].Offer.FillRestarts).IsEqualTo(0);
        // 33 ms from the second disc sample, then 15 samples reach 528.
        Check.That(s.FiredAfterMs(side)).IsEqualTo(15 * 33.0);
    }
}
