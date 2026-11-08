namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;

/// <summary>
/// Rest activation R1: the offer engine's <see cref="OfferPlacementStyle.AtRestPoint"/> style, unwired,
/// at the owner's geometry (3840×2400 at ScaleX 2.0) and at the CI-sized one (1920×1200 at 1.0). R is
/// 80 px in both (the radius is physical, never DPI-converted), so H = 1.5 R = 120 px, the grace G is
/// 500 ms and the fill F is 700 ms.
/// <para>
/// The frames are 25 ms so the clock is hand-checkable: each in-area sample credits exactly 25 ms, and
/// the 28th in-area sample after the settle reaches F. Each test names the mutant (R1-Mn) or the
/// production one-liner that turns it red.
/// </para>
/// </summary>
public class ActivationOfferEngineRestTests
{
    private const double R = 80;
    private const double H = ActivationOfferEngine.RestAreaFactor * R;   // 120 px
    private const int FillMs = 700;
    private const long FrameUs = 25_000;
    private const int SamplesToFill = 28;                                  // 28 × 25 ms = 700 ms

    /// <summary>The two geometries every theory runs at: the owner's and the CI-sized screen.</summary>
    public static TheoryData<int, int, double> Geometries => new()
    {
        { 3840, 2400, 2.0 },
        { 1920, 1200, 1.0 },
    };

    private static PhysicalRect ScreenOf(int width, int height) => new(0, 0, width, height);

    /// <summary>A resting point well inside the screen and clear of every edge.</summary>
    private static PhysicalPoint RestPointOf(int width, int height) => new(width * 0.4, height * 0.45);

    private static ActivationOfferEngine NewRestEngine(int width, int height, double scale, int dwellMs = FillMs)
        => new(ScreenOf(width, height), scale, ringSizeDip: 200, dwellMs, R, style: OfferPlacementStyle.AtRestPoint);

    private static ActivationOfferEngine NewButtonEngine(int width, int height, double scale)
        => new(ScreenOf(width, height), scale, ringSizeDip: 200, FillMs, R);

    /// <summary>The settle at <paramref name="point"/>: the sample, then the settle for the same instant
    /// (the engine's documented call order).</summary>
    private static SettleOutcome Settle(ActivationOfferEngine engine, PhysicalPoint point, long us)
    {
        engine.Update(point, us, isValid: true);
        return engine.NotifyFixationSettled(point, us);
    }

    /// <summary>Feeds <paramref name="samples"/> valid samples at <paramref name="point"/>, one frame
    /// apart, starting one frame after <paramref name="afterUs"/>; returns the last timestamp.</summary>
    private static long Feed(ActivationOfferEngine engine, PhysicalPoint point, long afterUs, int samples)
    {
        long us = afterUs;
        for (int i = 0; i < samples; i++)
        {
            us += FrameUs;
            engine.Update(point, us, isValid: true);
        }
        return us;
    }

    private static PhysicalPoint Offset(PhysicalPoint p, double dx, double dy = 0) => new(p.X + dx, p.Y + dy);

    // ── Settle → offer ───────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void Settle_OffersAtTheRestPoint_WithBEqualToP_AndTheRestArea(int width, int height, double scale)
    {
        // R1-M7 (placed through ActivationButtonPlacement) moves B off P; R1-M1 (H = 1.0 R) shrinks the
        // radius to 80.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);

        var outcome = Settle(engine, p, 0);

        var offer = engine.CurrentOffer!;
        Check.That(outcome).IsEqualTo(SettleOutcome.Created);
        Check.That(offer.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        Check.That(offer.Point).IsEqualTo(p);
        Check.That(offer.Button).IsEqualTo(p);
        Check.That(offer.HitRadiusPx).IsEqualTo(H);
        Check.That(offer.ExpiresAtUs).IsEqualTo(ActivationOfferEngine.RestFillGraceMs * 1000L);
        Check.That(engine.DwellProgress).IsEqualTo(0.0);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void WithoutASettle_NoOfferIsMade(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        Feed(engine, RestPointOf(width, height), 0, samples: 60);   // 1.5 s at the point, no settle
        Check.That(engine.CurrentOffer).IsNull();
    }

    [Fact]
    public void TheRestStyle_RunsTheGraceAsPauseBudgetAndExpiry_AndTheButtonDefaultIsUnchanged()
    {
        var rest = NewRestEngine(3840, 2400, 2.0);
        Check.That(rest.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        Check.That(rest.OutsidePauseBudgetMs).IsEqualTo(500);
        Check.That(rest.OfferExpiryMs).IsEqualTo(500);
        Check.That(rest.RestAreaRadiusPx).IsEqualTo(H);

        var button = NewButtonEngine(3840, 2400, 2.0);
        Check.That(button.Style).IsEqualTo(OfferPlacementStyle.Button);
        Check.That(button.OutsidePauseBudgetMs).IsEqualTo(ActivationOfferEngine.DefaultOutsidePauseBudgetMs);
        Check.That(button.OfferExpiryMs).IsEqualTo(ActivationOfferEngine.DefaultOfferExpiryMs);
    }

    // ── The fill ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void TheFill_FiresAtExactlyF_OfInAreaCredit_AndOnlyOnce(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, p, 0, SamplesToFill - 1);           // 675 ms
        Check.That(fired).IsEmpty();
        Check.That(engine.DwellProgress).IsCloseTo(675.0 / 700.0, 1e-9);

        us = Feed(engine, p, us, samples: 1);                       // 700 ms
        Check.That(fired).HasSize(1);
        Check.That(fired[0].Point).IsEqualTo(p);

        Feed(engine, p, us, samples: 20);
        Check.That(fired).HasSize(1);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AnExcursionUnderG_PausesTheFill_AndResumesWithoutLoss(int width, int height, double scale)
    {
        // R1-M2 (G = 0): the first outside sample resets the fill (and the zero expiry kills the offer).
        var engine = NewRestEngine(width, height, scale);
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, p, 0, samples: 10);                              // 250 ms credited
        us = Feed(engine, Offset(p, 2.0 * H), us, samples: 12);                 // 300 ms away
        Check.That(engine.CurrentOffer).IsNotNull();
        Check.That(engine.DwellProgress).IsCloseTo(250.0 / 700.0, 1e-9);        // paused, not lost

        us = Feed(engine, p, us, samples: SamplesToFill - 11);                  // 17 more: 675 ms in all
        Check.That(fired).IsEmpty();
        Feed(engine, p, us, samples: 1);
        Check.That(fired).HasSize(1);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ABlinkUnderG_PausesTheFill_AndTheOfferSurvives(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, p, 0, samples: 10);                  // 250 ms
        us += 400_000;                                              // a 400 ms silence (a blink)
        engine.Update(p, us, isValid: true);

        Check.That(engine.CurrentOffer).IsNotNull();
        // The resuming sample credits its frame cap (66 ms), never the blink itself, and nothing is lost.
        Check.That(engine.DwellProgress).IsCloseTo((250.0 + FixationDetector.AgeCreditCapMs) / 700.0, 1e-9);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AnExcursionOverG_ResetsTheFill_AndTheOfferExpires(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var expired = new List<(ActivationOffer Offer, double Peak)>();
        engine.OfferExpired += (o, _, peak) => expired.Add((o, peak));
        var p = RestPointOf(width, height);
        var offer = Settle(engine, p, 0) == SettleOutcome.Created ? engine.CurrentOffer! : null;

        long us = Feed(engine, p, 0, samples: 10);                  // last in-area sample at 250 ms
        us = Feed(engine, Offset(p, 2.0 * H), us, samples: 20);     // 750 → 500 ms past it

        Check.That(us - 250_000).IsEqualTo(500_000);
        Check.That(engine.CurrentOffer).IsNotNull();                // exactly G: not yet past
        Feed(engine, Offset(p, 2.0 * H), us, samples: 1);

        Check.That(engine.CurrentOffer).IsNull();
        Check.That(expired).HasSize(1);
        Check.That(expired[0].Offer.Id).IsEqualTo(offer!.Id);
        Check.That(expired[0].Peak).IsCloseTo(250.0 / 700.0, 1e-9);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ABlinkOverG_CancelsTheOffer(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        int expired = 0;
        engine.OfferExpired += (_, _, _) => expired++;
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, p, 0, samples: 10);
        engine.Update(p, us + 600_000, isValid: true);              // 600 ms of silence

        Check.That(engine.CurrentOffer).IsNull();
        Check.That(expired).IsEqualTo(1);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void OutOfAreaSamples_DoNotExtendTheExpiry_NorDoesTheRunEnd(int width, int height, double scale)
    {
        // R1-M3 (expiry extended by out-of-area samples): the offered run's samples off the rest
        // point would keep the offer alive past G.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, p, 0, samples: 10);                  // last in-area sample at 250 ms
        var near = Offset(p, 1.6 * R);                              // the run's own wander, outside H
        us = Feed(engine, near, us, samples: 8);                    // 450 ms
        engine.NotifyFixationEnded(us);                             // the run ends: no extension
        us = Feed(engine, near, us, samples: 13);                   // 775 ms

        Check.That(engine.CurrentOffer).IsNull();
    }

    // ── Wander ───────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void WanderAt1point4R_KeepsTheFill_And1point6R_PausesIt(int width, int height, double scale)
    {
        // R1-M1 (H = 1.0 R): 1.4 R is outside the area and pauses instead of filling.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        long us = Feed(engine, Offset(p, 0, 1.4 * R), 0, samples: 10);
        Check.That(engine.DwellProgress).IsCloseTo(250.0 / 700.0, 1e-9);

        Feed(engine, Offset(p, 0, 1.6 * R), us, samples: 6);
        Check.That(engine.DwellProgress).IsCloseTo(250.0 / 700.0, 1e-9);
        Check.That(engine.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void TheRestArea_FollowsAnInPlaceRadiusChange()
    {
        var engine = NewRestEngine(3840, 2400, 2.0);
        var p = RestPointOf(3840, 2400);
        Settle(engine, p, 0);

        engine.FixationRadiusPx = 100;

        Check.That(engine.CurrentOffer!.HitRadiusPx).IsEqualTo(150.0);
        Feed(engine, Offset(p, 140), 0, samples: 4);                // inside the new 150 px area
        Check.That(engine.DwellProgress).IsCloseTo(100.0 / 700.0, 1e-9);
    }

    // ── Settles ──────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASettleWithinH_IsTheSameRest_AndDoesNotRestartTheFill(int width, int height, double scale)
    {
        // R1-M6 (a same-spot settle restarts the fill): the id or the progress would change.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        var offer = engine.CurrentOffer!;
        long us = Feed(engine, p, 0, samples: 10);

        var outcome = Settle(engine, Offset(p, 0.9 * H), us + FrameUs);

        Check.That(outcome).IsEqualTo(SettleOutcome.OnButton);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(offer.Id);
        Check.That(engine.CurrentOffer.Button).IsEqualTo(p);
        Check.That(engine.DwellProgress).IsCloseTo(275.0 / 700.0, 1e-9);   // + that sample's own credit
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASettleBeyondH_ReplacesTheOffer(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        var first = engine.CurrentOffer!;
        long us = Feed(engine, p, 0, samples: 10);

        var elsewhere = Offset(p, 1.2 * H);
        var outcome = Settle(engine, elsewhere, us + FrameUs);

        Check.That(outcome).IsEqualTo(SettleOutcome.Replaced);
        Check.That(engine.CurrentOffer!.Id).IsEqualTo(first.Id + 1);
        Check.That(engine.CurrentOffer.Button).IsEqualTo(elsewhere);
        Check.That(engine.DwellProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void ASettleOnAFiredOffer_StaysLocked()
    {
        var engine = NewRestEngine(3840, 2400, 2.0);
        var p = RestPointOf(3840, 2400);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, SamplesToFill);
        Check.That(engine.IsFired).IsTrue();

        Check.That(Settle(engine, Offset(p, 3 * H), us + FrameUs)).IsEqualTo(SettleOutcome.Locked);
    }

    // ── Refusals: exactly as in the Button style ─────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void Refusals_MatchTheButtonStyle(int width, int height, double scale)
    {
        var p = RestPointOf(width, height);
        var chrome = new PhysicalRect(p.X - 50, p.Y - 50, 100, 100);
        var farOff = new PhysicalPoint(-500, p.Y);                       // past the 200 px clamp band
        var summon = new PhysicalPoint(-120, p.Y);                       // in the clamp band, but summoning

        foreach (var make in new Func<ActivationOfferEngine>[]
                 {
                     () => NewRestEngine(width, height, scale),
                     () => NewButtonEngine(width, height, scale),
                 })
        {
            var reasons = new List<ActivationOfferRefusalReason>();
            var inChrome = make();
            inChrome.OfferRefused += r => reasons.Add(r.Reason);
            inChrome.SetChromeZones(chrome);
            Check.That(Settle(inChrome, p, 0)).IsEqualTo(SettleOutcome.NoOffer);

            var offScreen = make();
            offScreen.OfferRefused += r => reasons.Add(r.Reason);
            Check.That(Settle(offScreen, farOff, 0)).IsEqualTo(SettleOutcome.NoOffer);

            var band = make();
            band.OfferRefused += r => reasons.Add(r.Reason);
            band.SetSummonBand(new ActivationSummonBand(ActivationSummonEdge.Left));
            Check.That(Settle(band, summon, 0)).IsEqualTo(SettleOutcome.NoOffer);

            var suspended = make();
            suspended.OfferRefused += r => reasons.Add(r.Reason);
            suspended.SetOffersSuspended(true);
            Check.That(Settle(suspended, p, 0)).IsEqualTo(SettleOutcome.NoOffer);

            Check.That(reasons).ContainsExactly(
                ActivationOfferRefusalReason.InChrome,
                ActivationOfferRefusalReason.OffScreen,
                ActivationOfferRefusalReason.OffScreen,
                ActivationOfferRefusalReason.Suspended);
        }
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void SuspensionRising_ClosesALiveRestOffer(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        Settle(engine, RestPointOf(width, height), 0);

        engine.SetOffersSuspended(true);

        Check.That(engine.CurrentOffer).IsNull();
    }

    // ── The corner clamp (orchestrator decision 2026-11-08: the rest centre is the RAW P) ──

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ACornerSettle150PxPastTheBezel_FillsAndFires_WithTheCompletionAtPPrime(int width, int height, double scale)
    {
        // R1-M11 (rest centre = P′): the gaze stays at the raw P, 150 px from P′ > H, and never fills.
        var engine = NewRestEngine(width, height, scale);
        var fired = new List<ActivationOffer>();
        engine.OfferFired += fired.Add;
        var raw = new PhysicalPoint(-150, -150);
        var clamped = new PhysicalPoint(0, 0);

        Check.That(Settle(engine, raw, 0)).IsEqualTo(SettleOutcome.Created);
        var offer = engine.CurrentOffer!;
        Check.That(offer.Point).IsEqualTo(clamped);
        Check.That(offer.Button).IsEqualTo(raw);

        Feed(engine, raw, 0, SamplesToFill);

        Check.That(fired).HasSize(1);
        Check.That(fired[0].Point).IsEqualTo(clamped);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASettleInTheSummonBand_IsStillRefused(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var refusals = new List<ActivationOfferRefusal>();
        engine.OfferRefused += refusals.Add;
        engine.SetSummonBand(new ActivationSummonBand(ActivationSummonEdge.Top));
        var summon = new PhysicalPoint(width / 2.0, -150);

        Check.That(Settle(engine, summon, 0)).IsEqualTo(SettleOutcome.NoOffer);
        Check.That(engine.CurrentOffer).IsNull();
        Check.That(refusals).HasSize(1);
        Check.That(refusals[0].Reason).IsEqualTo(ActivationOfferRefusalReason.OffScreen);
    }

    // ── Chrome re-validation checks P′ only ──────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void Revalidation_KeepsTheOfferForChromeNearby_AndClosesItForChromeOverP(int width, int height, double scale)
    {
        // The Button style's "hit circle intersects chrome" test would close it on the nearby chrome.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);

        engine.SetChromeZones(new PhysicalRect(p.X + 40, p.Y - 50, 200, 100));   // inside H, not over P
        engine.RevalidateChromeZones();
        Check.That(engine.CurrentOffer).IsNotNull();

        engine.SetChromeZones(new PhysicalRect(p.X - 10, p.Y - 10, 20, 20));     // over P
        engine.RevalidateChromeZones();
        Check.That(engine.CurrentOffer).IsNull();
    }

    // ── The re-arm latch ─────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AfterAFireAndAccept_ASettleAtTheSameSpot_IsRefusedRearm(int width, int height, double scale)
    {
        // R1-M4 (no latch): the same rest would be offered again and refill.
        var engine = NewRestEngine(width, height, scale);
        var refusals = new List<ActivationOfferRefusal>();
        engine.OfferRefused += refusals.Add;
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, SamplesToFill);
        engine.Accept(engine.CurrentOffer!.Id);
        Check.That(engine.CurrentOffer).IsNull();
        Check.That(engine.RearmLatch).IsEqualTo(p);

        us = Feed(engine, Offset(p, 0.5 * H), us, samples: 10);    // still on the spot
        var outcome = Settle(engine, Offset(p, 0.5 * H), us + FrameUs);

        Check.That(outcome).IsEqualTo(SettleOutcome.NoOffer);
        Check.That(engine.CurrentOffer).IsNull();
        Check.That(refusals).HasSize(1);
        Check.That(refusals[0].Reason).IsEqualTo(ActivationOfferRefusalReason.Rearm);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASampleBeyondH_ClearsTheLatch_AndTheNextSettleOffersAgain(int width, int height, double scale)
    {
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, SamplesToFill);
        engine.Accept(engine.CurrentOffer!.Id);

        // An INVALID sample far away is no evidence the eyes left: the latch stays.
        us += FrameUs;
        engine.Update(Offset(p, 3 * H), us, isValid: false);
        Check.That(engine.RearmLatch).IsEqualTo(p);

        us = Feed(engine, Offset(p, 1.1 * H), us, samples: 1);
        Check.That(engine.RearmLatch).IsNull();

        Check.That(Settle(engine, p, us + FrameUs)).IsEqualTo(SettleOutcome.Created);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void TheLatchBoundary_IsInclusive_ASampleAtExactlyHKeepsIt_AndASettleThereIsRefused(int width, int height, double scale)
    {
        // Review-01 X4 (the latch clears at exactly H, `>=`): the sample at exactly H would clear it.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, SamplesToFill);
        engine.Accept(engine.CurrentOffer!.Id);
        var atH = Offset(p, H);
        Check.That(atH.DistanceTo(p)).IsEqualTo(H);                // exactly, no rounding

        us = Feed(engine, atH, us, samples: 3);
        Check.That(engine.RearmLatch).IsEqualTo(p);
        Check.That(Settle(engine, atH, us + FrameUs)).IsEqualTo(SettleOutcome.NoOffer);

        Feed(engine, Offset(p, H + 1), us + FrameUs, samples: 1);
        Check.That(engine.RearmLatch).IsNull();
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AFiredCornerRest_RefusesARe_SettleAtTheSameRawP_AsRearm(int width, int height, double scale)
    {
        // Review-01 X9 (the refusal measured from the clamped P′): the raw P is 212 px from P′, beyond
        // H, so the re-settle would be offered again.
        var engine = NewRestEngine(width, height, scale);
        var refusals = new List<ActivationOfferRefusal>();
        engine.OfferRefused += refusals.Add;
        var raw = new PhysicalPoint(-150, -150);
        Settle(engine, raw, 0);
        long us = Feed(engine, raw, 0, SamplesToFill);
        Check.That(engine.IsFired).IsTrue();
        engine.Accept(engine.CurrentOffer!.Id);
        Check.That(engine.RearmLatch).IsEqualTo(raw);

        Check.That(Settle(engine, raw, us + FrameUs)).IsEqualTo(SettleOutcome.NoOffer);
        Check.That(refusals).HasSize(1);
        Check.That(refusals[0].Reason).IsEqualTo(ActivationOfferRefusalReason.Rearm);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASampleTheWiringMarksAsNotClearing_LeavesTheLatch_HoweverFar(int width, int height, double scale)
    {
        // The wiring passes mayClearRearmLatch: false outside FreeLook/Fixation (the magnifier, menus).
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, SamplesToFill);
        engine.Accept(engine.CurrentOffer!.Id);

        for (int i = 0; i < 20; i++)
        {
            us += FrameUs;
            engine.Update(Offset(p, 5 * H), us, isValid: true, mayClearRearmLatch: false);
        }

        Check.That(engine.RearmLatch).IsEqualTo(p);
        Check.That(Settle(engine, p, us + FrameUs)).IsEqualTo(SettleOutcome.NoOffer);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void AnExpiry_SetsNoLatch(int width, int height, double scale)
    {
        // R1-M5 (an expiry sets the latch). The expiry is found by an in-area sample after a long
        // blink, so no far sample clears a latch the mutant would have set.
        var engine = NewRestEngine(width, height, scale);
        var p = RestPointOf(width, height);
        Settle(engine, p, 0);
        long us = Feed(engine, p, 0, samples: 10);
        us += 600_000;
        engine.Update(p, us, isValid: true);
        Check.That(engine.CurrentOffer).IsNull();

        Check.That(engine.RearmLatch).IsNull();
        Check.That(Settle(engine, p, us + FrameUs)).IsEqualTo(SettleOutcome.Created);
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ACloseByCommand_SetsTheLatch(int width, int height, double scale)
    {
        var p = RestPointOf(width, height);
        var closes = new Action<ActivationOfferEngine>[]
        {
            e => e.Clear(),
            e => e.Decline(e.CurrentOffer!.Id),
            e => e.SetOffersSuspended(true),
            e => { e.SetChromeZones(new PhysicalRect(p.X - 5, p.Y - 5, 10, 10)); e.RevalidateChromeZones(); },
        };

        foreach (var close in closes)
        {
            var engine = NewRestEngine(width, height, scale);
            Settle(engine, p, 0);
            long us = Feed(engine, p, 0, samples: 5);

            close(engine);
            engine.SetOffersSuspended(false);
            engine.SetChromeZones();

            Check.That(engine.CurrentOffer).IsNull();
            Check.That(engine.RearmLatch).IsEqualTo(p);
            Check.That(Settle(engine, p, us + FrameUs)).IsEqualTo(SettleOutcome.NoOffer);
        }
    }

    [Fact]
    public void TheButtonStyle_NeverLatches()
    {
        var engine = NewButtonEngine(3840, 2400, 2.0);
        var p = RestPointOf(3840, 2400);
        Settle(engine, p, 0);
        engine.Clear();
        engine.SeedRearmLatch(p);

        Check.That(engine.RearmLatch).IsNull();
        Check.That(Settle(engine, p, FrameUs)).IsEqualTo(SettleOutcome.Created);
    }
}
