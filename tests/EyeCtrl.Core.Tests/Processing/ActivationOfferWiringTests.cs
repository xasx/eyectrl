namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Ticket 8: the offer engine WIRED into the gaze pipeline — the product-level rows of the activation
/// plan's acceptance table that the plan says to re-run through the wired path. Each test names the
/// production one-liner mutation that turns it red.
/// <para>
/// The harness drives the real <see cref="GazeEngine"/> with synthetic samples at the user's geometry
/// (3840×2400, ScaleX 2.0, ring 250, R 90, dwell 700), so the offer's B, the settle, the expiry and
/// the teardowns all travel the production path.
/// </para>
/// </summary>
public class ActivationOfferWiringTests
{
    private const long FrameUs = 30_000;
    private static readonly PhysicalRect Screen = new(0, 0, 3840, 2400);
    private static readonly PhysicalPoint P = new(1000, 1000);

    private sealed class Harness : IDisposable
    {
        public ScriptedGazeProvider Provider { get; } = new();
        public GazeEngine Engine { get; }
        public InteractionManager Im => Engine.InteractionManager;
        public ActivationOfferEngine Offers => Im.ActivationOffers!;

        public List<ActivationOffer> Fired { get; } = new();
        public List<ActivationOffer> Shown { get; } = new();
        public int Closed;
        public int Pinned;
        public int Released;
        public List<PhysicalPoint> Menus { get; } = new();

        private long _us;

        public Harness(EffectiveSettingsProvider? effective = null, EyeCtrlSettings? settings = null)
        {
            settings ??= new EyeCtrlSettings();
            settings.Cursor.FilterMode = GazeFilterMode.Direct;
            settings.Dwell.FixationRadiusPixels = 90;
            settings.Dwell.FixationDetectionMs = 300;
            settings.Dwell.ActivationButtonDwellMs = 700;
            Engine = new GazeEngine(Provider, new RecordingInputSimulator(), settings, effectiveSettings: effective);
            Engine.ConfigureActivationOffers(Screen, scale: 2.0);

            Im.ActivationOfferFired += (offer, _) => Fired.Add(offer);
            Im.ActivationOfferShown += offer => Shown.Add(offer);
            Im.ActivationOfferClosed += () => Closed++;
            Im.PinRing += _ => Pinned++;
            Im.ReleaseRing += () => Released++;
            Im.ShowActionMenu += point => Menus.Add(point);
        }

        public void Feed(PhysicalPoint point, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        public void FeedInvalid(int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                Provider.Push(new GazePoint(0, 0, _us, false));
            }
        }

        /// <summary>Saccading stream far from everything: no run settles, and the offer's linger runs out.</summary>
        public void FeedAway(int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                var point = i % 2 == 0 ? new PhysicalPoint(3400, 2200) : new PhysicalPoint(600, 300);
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        /// <summary>Saccades between two points, both far from the button: the 500 ms reset budget runs
        /// out without any new fixation settling (a stationary away point would settle and replace).</summary>
        public void FeedSaccades(PhysicalPoint a, PhysicalPoint b, int samples)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += FrameUs;
                var point = i % 2 == 0 ? a : b;
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        /// <summary>Holds P until the settle (20 samples ≈ 600 ms) and returns the created offer.</summary>
        public ActivationOffer SettleAt(PhysicalPoint point)
        {
            Feed(point, samples: 20);
            var offer = Im.ActivationOffers?.CurrentOffer;
            Check.That(offer).IsNotNull();
            return offer!;
        }

        public void Dispose() => Engine.Dispose();
    }

    // ── One dwell activates, at P ────────────────────────────────────────────

    [Fact]
    public void SettleThenButtonDwell_FiresOnce_WithTheOffersP_AndAcceptClearsIt()
    {
        // Mutation: resolve the completion at the live fixation point / drop the one-fire latch.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        Check.That(offer.Point).IsEqualTo(P);

        h.Feed(offer.Button, samples: 25);                    // 750 ms on the button

        Check.That(h.Fired).HasSize(1);
        Check.That(h.Fired[0].Id).IsEqualTo(offer.Id);
        Check.That(h.Fired[0].Point).IsEqualTo(P);            // the click lands at P, not at B
        Check.That(h.Offers.IsFired).IsTrue();

        // The adapter's accept travels the volatile-request path.
        h.Im.RequestActivationOfferAccept(h.Fired[0].Id);
        h.Feed(offer.Button, samples: 1);

        Check.That(h.Offers.CurrentOffer).IsNull();
        Check.That(h.Closed).IsEqualTo(1);
    }

    [Fact]
    public void BlinkMidDwell_StillFiresExactlyOnce()
    {
        // Decision 9 / ticket 3's ledger: a 300 ms blink pauses the dwell (the gap budget), it does not
        // restart it. Mutation: reset the dwell on any invalid sample → the credited time never reaches
        // 700 and the fire list stays empty.
        using var h = new Harness();
        var offer = h.SettleAt(P);

        h.Feed(offer.Button, samples: 11);                    // 330 ms
        h.FeedInvalid(samples: 10);                           // a 300 ms blink
        h.Feed(offer.Button, samples: 18);                    // 540 ms more → past 700 credited

        Check.That(h.Fired).HasSize(1);
    }

    [Fact]
    public void FixationEndsOnTheMoveToTheButton_TheOfferSurvives_AndTheDwellCompletes()
    {
        // B4: the fixation that formed on the button may end before the dwell completes. Hiding the
        // button on OnFixationEnded (the old decoy) would take the offer with it; the plan's row.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        var b = offer.Button;

        h.Feed(b, samples: 25);

        Check.That(h.Fired).HasSize(1);
        Check.That(h.Released).IsEqualTo(1);                  // the fixation did end on the way there
        Check.That(h.Closed).IsEqualTo(0);                    // ...and the button stayed up
    }

    // ── Ring free, button up ─────────────────────────────────────────────────

    [Fact]
    public void FixationEnd_ReleasesTheRing_ButKeepsTheButtonUp()
    {
        // B1: the ring's lifetime is the Fixation state's; the button's is the offer's linger. Mutation:
        // release the ring on the button's hide, or hide the button on the fixation end → one of the
        // two assertions goes red.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        Check.That(h.Pinned).IsEqualTo(1);

        h.Feed(offer.Button, samples: 1);                     // the move ends the fixation

        Check.That(h.Released).IsEqualTo(1);
        Check.That(h.Closed).IsEqualTo(0);
        Check.That(h.Offers.CurrentOffer).IsNotNull();
    }

    // ── Expiry ───────────────────────────────────────────────────────────────

    [Fact]
    public void UntouchedOffer_ExpiresAbout750MsAfterTheHoldEnds()
    {
        // Decision 3's linger, on the sample clock, measured from the offered run's end: the offer is
        // gone 750 ms after the last sample of the hold, and the close is published.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        Check.That(h.Offers.OfferExpiryMs).IsEqualTo(ActivationOfferEngine.ProductOfferExpiryMs);

        h.FeedAway(samples: 40);                              // 1.2 s of saccades

        Check.That(h.Offers.CurrentOffer).IsNull();
        Check.That(h.Closed).IsEqualTo(1);
        Check.That(h.Fired).IsEmpty();
    }

    // ── Excursion bound ──────────────────────────────────────────────────────

    [Fact]
    public void ExcursionBound_200MsPauses_600MsResets()
    {
        // Decision 9: a 200 ms slip pauses the fill (the dwell completes after 700 ms of INSIDE time);
        // a 600 ms slip resets it. Mutation: reset on any outside sample → the pause half fails to
        // fire; or pause without limit → the reset half fires.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        var b = offer.Button;
        var away = new PhysicalPoint(b.X + 400, b.Y);

        h.Feed(b, samples: 10);                               // 300 ms
        h.Feed(away, samples: 7);                             // a 210 ms slip: pause only
        h.Feed(b, samples: 14);                               // 420 ms more → 300 + ~420 ≥ 700
        Check.That(h.Fired).HasSize(1);

        // Reset half, on a fresh offer. The slip must be a SACCADE: a stationary away point would
        // settle into a new fixation within the 600 ms and replace the offer, which the dwell reset is
        // not what suppresses the fire then.
        using var h2 = new Harness();
        var offer2 = h2.SettleAt(P);
        var b2 = offer2.Button;
        var away2a = new PhysicalPoint(b2.X + 800, b2.Y);
        var away2b = new PhysicalPoint(b2.X, b2.Y + 800);

        h2.Feed(b2, samples: 10);                             // 300 ms
        h2.FeedSaccades(away2a, away2b, samples: 20);         // a 600 ms slip: reset to 0
        h2.Feed(b2, samples: 20);                             // 600 ms — below the 700 threshold
        Check.That(h2.Fired).IsEmpty();
    }

    // ── Stale id and rejection ───────────────────────────────────────────────

    [Fact]
    public void LateCompletionForAStaleOffer_DoesNotTouchTheLiveOffer()
    {
        // Review A F2 at the wired level: offer 1 fires, a teardown clears it, offer 2 is live, and the
        // late completion for offer 1 must neither open the menu nor accept or decline offer 2.
        // Mutation: drop the liveness check (`IsActivationOfferLive`) → the menu opens at P1, red.
        using var h = new Harness();
        var offer1 = h.SettleAt(P);
        h.Feed(offer1.Button, samples: 25);                   // fire offer 1
        Check.That(h.Fired).HasSize(1);

        h.Im.RequestActivationOfferClear();
        h.Feed(offer1.Button, samples: 1);                    // the teardown lands
        Check.That(h.Offers.CurrentOffer).IsNull();

        var p2 = new PhysicalPoint(2600, 1800);
        var offer2 = h.SettleAt(p2);

        h.Im.OnActivationButtonDwelled(offer1);

        Check.That(h.Menus).IsEmpty();
        Check.That(h.Im.FixationPoint).IsEqualTo(offer2.Point);
        Check.That(h.Offers.CurrentOffer).IsNotNull();
        Check.That(h.Offers.CurrentOffer!.Id).IsEqualTo(offer2.Id);
    }

    [Fact]
    public void PauseDuringTheResolve_ThenResume_AFreshSettleGetsAnOffer()
    {
        // R3: a fired offer, then Pause before the dispatch, then resume — the fired offer must not
        // stay locked, or no offer could ever be made again. Mutation: the teardown clear does
        // nothing → the lock survives and the fresh settle makes no offer, red.
        using var h = new Harness();
        var offer1 = h.SettleAt(P);
        h.Feed(offer1.Button, samples: 25);                   // fire
        Check.That(h.Offers.IsFired).IsTrue();

        h.Im.TogglePause();                                   // the dispatcher request, not yet applied
        h.Im.TogglePause();                                   // resume
        h.Feed(P, samples: 1);                                // the clear lands

        var p2 = new PhysicalPoint(2600, 1800);
        var offer2 = h.SettleAt(p2);

        Check.That(h.Offers.CurrentOffer).IsNotNull();
        Check.That(h.Offers.CurrentOffer!.Id).IsEqualTo(offer2.Id);
        Check.That(offer2.Id).IsNotEqualTo(offer1.Id);
    }

    // ── Threads ──────────────────────────────────────────────────────────────

    [Fact]
    public void TeardownFromTheDispatcher_IsAppliedByTheGazeThread()
    {
        // N3: the request lands on the gaze thread, not where it was made. Mutation: clear the engine
        // synchronously in RequestActivationOfferClear → the "still live" assertion goes red (the
        // first sample would never get to apply it).
        using var h = new Harness();
        var offer = h.SettleAt(P);

        h.Im.RequestActivationOfferClear();
        Check.WithCustomMessage("the teardown must not mutate gaze-thread state on the caller's thread")
            .That(h.Offers.CurrentOffer).IsNotNull();
        Check.That(h.Closed).IsEqualTo(0);

        h.Feed(offer.Button, samples: 1);

        Check.That(h.Offers.CurrentOffer).IsNull();
        Check.That(h.Closed).IsEqualTo(1);
    }

    // ── Chrome ───────────────────────────────────────────────────────────────

    [Fact]
    public void ChromeSettle_PinsTheRing_ButMakesNoOffer()
    {
        // Decision 8: over EyeCtrl's own controls the ring still says "you're holding", and no button
        // is offered. Mutation: make the offer regardless of chrome → an offer appears and is shown.
        using var h = new Harness();
        h.Im.SetActivationChromeZones(new PhysicalRect(P.X - 100, P.Y - 100, 200, 200));

        h.Feed(P, samples: 20);

        Check.That(h.Pinned).IsEqualTo(1);
        Check.That(h.Offers.CurrentOffer).IsNull();
        Check.That(h.Shown).IsEmpty();
    }

    // ── The product's constants ──────────────────────────────────────────────

    [Fact]
    public void ProductExpiry_IsTheUsers750Ms()
    {
        // Decision 3, set by the user from the replay report. Mutation: keep the engine's 1 s default
        // → red.
        using var h = new Harness();
        h.Feed(P, samples: 1);

        Check.That(ActivationOfferEngine.ProductOfferExpiryMs).IsEqualTo(750);
        Check.That(h.Offers.OfferExpiryMs).IsEqualTo(750);
    }

    // ── The dwell knob ───────────────────────────────────────────────────────

    [Fact]
    public void DwellKnob_EffectiveFill_ReachesTheButtonThreshold()
    {
        // The plan's row: with a non-zero effective-dwell fill, the button threshold follows the
        // EFFECTIVE value. Mutation: read the raw setting when building the engine → the engine's
        // dwell stays 700 and stops tracking the detector's effective value, red.
        var settings = new EyeCtrlSettings();
        var effective = new EffectiveSettingsProvider(settings.Adaptation);
        settings.Adaptation.Enabled = true;
        using var h = new Harness(effective, settings);

        effective.SetMomentaryFill(
            AdaptationKnob.DwellTime, DwellContext.Other, forgivingFill: 1.0, tighteningFill: 0.0);
        h.Feed(P, samples: 1);

        double effectiveDwell = h.Engine.ActivationDwellMsForTest;
        Check.WithCustomMessage("the fill did not move the effective dwell; the test premise is void")
            .That(effectiveDwell).IsNotEqualTo((double)settings.Dwell.ActivationButtonDwellMs);
        Check.That(h.Offers.DwellMs).IsEqualTo((int)effectiveDwell);
    }

    // ── No re-anchor, no chase ───────────────────────────────────────────────

    [Fact]
    public void NearbySettleOnPsSide_IsSwallowed_AndNeverMovesTheOffer()
    {
        // Decision 10 / R2: a settle inside the no-move zone nearer P than B extends the live offer, it
        // never re-offers. P2 sits 122 px along P→B (238 px from B): outside the fixation radius (a new
        // run), inside the 240 px hit + R zone, and nearer P than B. Mutation: let a settle inside the
        // zone replace the offer → the id changes and the point moves, red.
        using var h = new Harness();
        var offer = h.SettleAt(P);

        var p2 = new PhysicalPoint(P.X + (122 / Math.Sqrt(2)), P.Y + (122 / Math.Sqrt(2)));
        h.Feed(p2, samples: 20);                              // a full run at P2, settling

        Check.That(h.Offers.CurrentOffer).IsNotNull();
        Check.That(h.Offers.CurrentOffer!.Id).IsEqualTo(offer.Id);
        Check.That(h.Offers.CurrentOffer.Point).IsEqualTo(P);
    }

    [Fact]
    public void OnButtonSettleThenDecline_MakesNoNewOffer()
    {
        // The plan's row: a settle inside B's hit area is never remembered as a pending re-offer — a
        // declined on-button dwell would otherwise re-offer at the old button's own spot. The decline
        // goes through the raw request (the engine's view), not DeclineOffer: the latter ends the run
        // first, and a run end clears any pending regardless of how the settle was classified, which
        // would mask the classification under test. Mutation: classify an on-button settle as pending
        // → the still-holding run's pending re-offers at B, red.
        using var h = new Harness();
        var offer = h.SettleAt(P);
        var b = offer.Button;

        h.Feed(b, samples: 20);                               // the on-button run settles inside this feed
        Check.That(h.Offers.CurrentOffer!.Id).IsEqualTo(offer.Id);

        h.Im.RequestActivationOfferDecline(offer.Id);
        h.Feed(b, samples: 3);

        Check.That(h.Offers.CurrentOffer).IsNull();
        Check.That(h.Shown).HasSize(1);                       // the original show only
    }

    // ── Teardowns ────────────────────────────────────────────────────────────

    [Fact]
    public void Teardowns_EachPathClearsTheOffer()
    {
        // M5's list, by mechanism: TogglePause and EnterRepeatMode are InteractionManager requests;
        // the keyboard/external-dwell/calibration group all take the SuppressFixation rising edge.
        // Mutation (per path): skip that path's request → the offer survives and the assertion is red.

        // TogglePause
        using (var h = new Harness())
        {
            var offer = h.SettleAt(P);
            h.Im.TogglePause();
            h.Feed(offer.Button, samples: 1);
            Check.That(h.Offers.CurrentOffer).IsNull();
        }

        // EnterRepeatMode (only from FreeLook: end the fixation by moving to the button first)
        using (var h = new Harness())
        {
            var offer = h.SettleAt(P);
            h.Feed(offer.Button, samples: 1);                 // state → FreeLook, offer up
            h.Im.EnterRepeatMode(InteractionAction.LeftClick);
            h.Feed(offer.Button, samples: 1);
            Check.That(h.Offers.CurrentOffer).IsNull();
        }

        // SuppressFixation (keyboard show, external dwell, calibration)
        using (var h = new Harness())
        {
            var offer = h.SettleAt(P);
            h.Engine.SuppressFixation = true;
            h.Feed(offer.Button, samples: 1);
            Check.That(h.Offers.CurrentOffer).IsNull();
        }
    }
}
