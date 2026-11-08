namespace EyeCtrl.Core.Tests.Processing;

using EyeCtrl.Core;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.Core.Tests.Flow;

/// <summary>
/// Rest activation R1, wired: the ring pin through <see cref="InteractionManager"/> (the effective
/// pin policy follows the attached engine's style), the style setting through <see cref="GazeEngine"/>
/// (default, rebuild, the effective dwell), and the full chain from a rest to the magnifier.
/// </summary>
public class RestActivationWiringTests
{
    private const long FrameUs = 25_000;
    private const double R = 80;

    public static TheoryData<int, int, double> Geometries => new()
    {
        { 3840, 2400, 2.0 },
        { 1920, 1200, 1.0 },
    };

    // ── InteractionManager: the pin follows the rest offer ───────────────────

    /// <summary>An InteractionManager on the hybrid's launch arm (RunOnly) with an attached engine,
    /// driven sample by sample the way GazeEngine drives it.</summary>
    private sealed class ImHarness
    {
        public InteractionManager Im { get; } = new() { RingPinPolicy = RingPinPolicy.RunOnly };
        public List<PhysicalPoint> Pins { get; } = new();
        public List<bool> PinsAtOffer { get; } = new();
        public int Releases;
        public long Us;

        public ImHarness(ActivationOfferEngine engine)
        {
            Im.PinRing += p => { Pins.Add(p); PinsAtOffer.Add(Im.RingPinnedAtOffer); };
            Im.ReleaseRing += () => Releases++;
            Im.AttachActivationOffers(engine);
        }

        public void Sample(PhysicalPoint p, bool valid = true)
        {
            Us += FrameUs;
            Im.NoteGazeSample(valid, Us);
            Im.NoteActivationGazeSample(p, Us, valid);
        }

        public void OnsetAndSettle(PhysicalPoint p)
        {
            Im.OnFixationStarted(p);
            Sample(p);
            Im.OnFixationSettled(p);
        }
    }

    private static ActivationOfferEngine RestEngine(int w, int h, double scale) =>
        new(new PhysicalRect(0, 0, w, h), scale, 200, dwellMs: 700, R, style: OfferPlacementStyle.AtRestPoint);

    private static ActivationOfferEngine ButtonEngine(int w, int h, double scale) =>
        new(new PhysicalRect(0, 0, w, h), scale, 200, dwellMs: 700, R);

    [Theory]
    [MemberData(nameof(Geometries))]
    public void UnderRestFill_ThePinIsOfferFollowing_PinnedAtPOnTheShow(int w, int h, double scale)
    {
        var x = new ImHarness(RestEngine(w, h, scale));
        var p = new PhysicalPoint(w * 0.4, h * 0.45);
        Check.That(x.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);

        x.OnsetAndSettle(p);

        Check.That(x.Pins).ContainsExactly(p);
        Check.That(x.PinsAtOffer).ContainsExactly(true);
        Check.That(x.Im.HasPublishedActivationOffer).IsTrue();
    }

    [Theory]
    [MemberData(nameof(Geometries))]
    public void ASlipUnderG_KeepsThePinThroughTheFreeLookFixationFlip_AndTheExpiryReleasesIt(int w, int h, double scale)
    {
        // R1-M8 (the pin policy stays the hybrid's RunOnly): the fixation's end releases the ring.
        var x = new ImHarness(RestEngine(w, h, scale));
        var p = new PhysicalPoint(w * 0.4, h * 0.45);
        x.OnsetAndSettle(p);
        for (int i = 0; i < 8; i++) x.Sample(p);

        // The slip: the run ends (FreeLook), the gaze is outside H for 200 ms, then it is back and a
        // new run forms and settles within H.
        x.Im.OnFixationEnded();
        for (int i = 0; i < 8; i++) x.Sample(new PhysicalPoint(p.X + (1.8 * R), p.Y));
        x.Im.OnFixationStarted(p);
        x.Sample(p);
        x.Im.OnFixationSettled(p);
        Check.That(x.Releases).IsEqualTo(0);
        Check.That(x.Pins).HasSize(1);                             // the same rest: no re-pin

        // Then the user really looks away: past G the offer expires and the ring is released.
        x.Im.OnFixationEnded();
        for (int i = 0; i < 21; i++) x.Sample(new PhysicalPoint(p.X + (4 * R), p.Y));

        Check.That(x.Im.ActivationOffers!.CurrentOffer).IsNull();
        Check.That(x.Releases).IsEqualTo(1);
    }

    [Fact]
    public void AStateExit_ReleasesTheRestPin()
    {
        var x = new ImHarness(RestEngine(3840, 2400, 2.0));
        x.OnsetAndSettle(new PhysicalPoint(1500, 1100));

        x.Im.TogglePause();

        Check.That(x.Releases).IsEqualTo(1);
    }

    [Fact]
    public void UnderOfferButton_TheLaunchArmsPolicyApplies()
    {
        var x = new ImHarness(ButtonEngine(3840, 2400, 2.0));
        Check.That(x.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.RunOnly);

        x.Im.AttachActivationOffers(RestEngine(3840, 2400, 2.0));
        Check.That(x.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);

        x.Im.AttachActivationOffers(ButtonEngine(3840, 2400, 2.0));
        Check.That(x.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.RunOnly);
    }

    [Fact]
    public void ARebuild_CarriesTheDroppedRestOffersSpot_AsTheLatch()
    {
        var x = new ImHarness(RestEngine(3840, 2400, 2.0));
        var p = new PhysicalPoint(1500, 1100);
        x.OnsetAndSettle(p);

        var rebuilt = RestEngine(3840, 2400, 2.0);
        x.Im.AttachActivationOffers(rebuilt);

        Check.That(rebuilt.RearmLatch).IsEqualTo(p);
        Check.That(x.Im.HasPublishedActivationOffer).IsFalse();
    }

    [Fact]
    public void OnlyFreeLookAndFixationSamples_ClearTheLatch_TheMagnifiersDoNot()
    {
        // R1-M12 (any-state samples clear the latch): the magnifier's far samples would clear it, and
        // the rest at P would refill the moment the magnifier closes.
        var x = new ImHarness(RestEngine(3840, 2400, 2.0));
        x.Im.SelectionMode = SelectionMode.ManualSelection;
        var refusals = new List<ActivationOfferRefusalReason>();
        x.Im.ActivationOfferRefused += r => refusals.Add(r.Reason);
        var p = new PhysicalPoint(1500, 1100);
        var fired = new List<ActivationOffer>();
        x.Im.ActivationOfferFired += (offer, _) => fired.Add(offer);
        x.OnsetAndSettle(p);
        for (int i = 0; i < 28; i++) x.Sample(p);
        Check.That(fired).HasSize(1);

        Check.That(x.Im.OnActivationButtonDwelled(fired[0])).IsEqualTo(ActivationCompletionResult.Accepted);
        Check.That(x.Im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);
        x.Im.ApplyPendingActivationOfferRequests();               // the accept lands
        for (int i = 0; i < 20; i++) x.Sample(new PhysicalPoint(p.X + 600, p.Y + 400));   // roaming in the magnifier
        Check.That(x.Im.ActivationOffers!.RearmLatch).IsEqualTo(p);

        x.Im.OnManualSelectionBack();                              // closed: back to FreeLook
        x.Im.ApplyPendingActivationOfferRequests();
        Check.That(x.Im.CurrentState).IsEqualTo(InteractionState.FreeLook);
        x.Sample(new PhysicalPoint(p.X + 30, p.Y));                // still near P
        x.OnsetAndSettle(p);

        Check.That(x.Im.ActivationOffers.CurrentOffer).IsNull();
        Check.That(refusals).ContainsExactly(ActivationOfferRefusalReason.Rearm);

        // A FreeLook sample beyond H clears it; the next settle offers.
        x.Im.OnFixationEnded();
        x.Sample(new PhysicalPoint(p.X + 200, p.Y));
        Check.That(x.Im.ActivationOffers.RearmLatch).IsNull();
        x.OnsetAndSettle(p);
        Check.That(x.Im.ActivationOffers.CurrentOffer).IsNotNull();
    }

    [Fact]
    public void ACornerRest_ReturnsToItsRawP_WhenTheRunAfterAChromeSettleEnds()
    {
        // Review-01 X8 (the FreeLook re-pin at P′ instead of the raw P): a clamped corner rest pins the
        // ring at the raw settle; a chrome settle during its life moves the pin off the offer, and the
        // run's end must put it back where the rest's ring was, not on the clamped completion point.
        var x = new ImHarness(RestEngine(3840, 2400, 2.0));
        var raw = new PhysicalPoint(-150, -150);
        var chromeSettle = new PhysicalPoint(600, 600);
        x.Im.SetActivationChromeZones(new PhysicalRect(500, 500, 200, 200));

        x.OnsetAndSettle(raw);
        var offer = x.Im.ActivationOffers!.CurrentOffer!;
        Check.That(offer.Point).IsEqualTo(new PhysicalPoint(0, 0));
        Check.That(offer.Button).IsEqualTo(raw);
        Check.That(x.Pins).ContainsExactly(raw);

        x.Im.OnFixationEnded();                                    // a glance to the toolbar-like chrome
        x.Im.OnFixationStarted(chromeSettle);
        x.Sample(chromeSettle);
        x.Im.OnFixationSettled(chromeSettle);                      // refused in chrome: pins at the settle
        Check.That(x.Pins[^1]).IsEqualTo(chromeSettle);
        Check.That(x.PinsAtOffer[^1]).IsFalse();

        x.Im.OnFixationEnded();                                    // back to FreeLook while the rest lives

        Check.That(x.Im.ActivationOffers.CurrentOffer).IsNotNull();
        Check.That(x.Pins[^1]).IsEqualTo(raw);
        Check.That(x.PinsAtOffer[^1]).IsTrue();
    }

    [Fact]
    public void AStyleSwitchUnderALiveRestOffer_StillReleasesItsPin()
    {
        // The old offer's close is published under the policy it was shown with; publishing it under
        // the new RunOnly would strand the ring at P in FreeLook.
        var x = new ImHarness(RestEngine(3840, 2400, 2.0));
        x.OnsetAndSettle(new PhysicalPoint(1500, 1100));
        x.Im.OnFixationEnded();
        Check.That(x.Releases).IsEqualTo(0);

        x.Im.AttachActivationOffers(ButtonEngine(3840, 2400, 2.0));

        Check.That(x.Releases).IsEqualTo(1);
    }

    // ── GazeEngine: the setting ──────────────────────────────────────────────

    private sealed class EngineHarness : IDisposable
    {
        public ScriptedGazeProvider Provider { get; } = new();
        public GazeEngine Engine { get; }
        public InteractionManager Im => Engine.InteractionManager;
        private long _us;

        public EngineHarness(EyeCtrlSettings settings)
        {
            settings.Cursor.FilterMode = GazeFilterMode.Direct;
            Engine = new GazeEngine(Provider, new RecordingInputSimulator(), settings);
            Engine.InteractionManager.RingPinPolicy = RingPinPolicy.RunOnly;   // the hybrid's arm
            Engine.ConfigureActivationOffers(new PhysicalRect(0, 0, 3840, 2400), scale: 2.0);
        }

        public void Feed(PhysicalPoint point, int samples, long frameUs = 30_000)
        {
            for (int i = 0; i < samples; i++)
            {
                _us += frameUs;
                Provider.Push(new GazePoint(point.X, point.Y, _us, true));
            }
        }

        public void Dispose() => Engine.Dispose();
    }

    [Fact]
    public void TheDefaultSettings_AreRestFill_AndTheConfiguredEngineIsTheRestStyle()
    {
        var settings = new EyeCtrlSettings();
        Check.That(settings.Dwell.ActivationStyle).IsEqualTo(ActivationStyle.RestFill);

        using var h = new EngineHarness(settings);
        h.Feed(new PhysicalPoint(100, 100), samples: 1);

        var offers = h.Im.ActivationOffers!;
        Check.That(offers.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        Check.That(offers.OfferExpiryMs).IsEqualTo(ActivationOfferEngine.RestFillGraceMs);
        Check.That(offers.DwellMs).IsEqualTo(700);
        Check.That(h.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.OfferFollowing);
    }

    [Fact]
    public void AStyleChange_RebuildsTheEngine_OnTheNextSample()
    {
        var settings = new EyeCtrlSettings();
        using var h = new EngineHarness(settings);
        h.Feed(new PhysicalPoint(100, 100), samples: 1);
        var rest = h.Im.ActivationOffers!;

        settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
        Check.That(h.Im.ActivationOffers).IsSameReferenceAs(rest);  // requested, not applied off-thread
        h.Feed(new PhysicalPoint(100, 100), samples: 1);

        var button = h.Im.ActivationOffers!;
        Check.That(button).Not.IsSameReferenceAs(rest);
        Check.That(button.Style).IsEqualTo(OfferPlacementStyle.Button);
        Check.That(button.OfferExpiryMs).IsEqualTo(ActivationOfferEngine.ProductOfferExpiryMs);
        Check.That(h.Im.EffectiveRingPinPolicy).IsEqualTo(RingPinPolicy.RunOnly);

        settings.Dwell.ActivationStyle = ActivationStyle.RestFill;
        h.Feed(new PhysicalPoint(100, 100), samples: 1);
        Check.That(h.Im.ActivationOffers!.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
    }

    [Fact]
    public void TheEffectiveDwell_ReadsRestFillMsUnderRestFill_AndTheButtonDwellUnderOfferButton()
    {
        // Different values on purpose: equal defaults (700/700) would hide a swapped mapping.
        var settings = new EyeCtrlSettings();
        settings.Dwell.RestFillMs = 650;
        settings.Dwell.ActivationButtonDwellMs = 900;
        using var h = new EngineHarness(settings);
        h.Feed(new PhysicalPoint(100, 100), samples: 1);

        Check.That(h.Engine.ActivationDwellMsForTest).IsEqualTo(650);
        Check.That(h.Im.ActivationOffers!.DwellMs).IsEqualTo(650);

        settings.Dwell.ActivationStyle = ActivationStyle.OfferButton;
        h.Feed(new PhysicalPoint(100, 100), samples: 1);

        Check.That(h.Engine.ActivationDwellMsForTest).IsEqualTo(900);
        Check.That(h.Im.ActivationOffers!.DwellMs).IsEqualTo(900);

        // And a RestFillMs edit moves the live rest engine in place once the style is back.
        settings.Dwell.ActivationStyle = ActivationStyle.RestFill;
        h.Feed(new PhysicalPoint(100, 100), samples: 1);
        settings.Dwell.RestFillMs = 1200;
        h.Feed(new PhysicalPoint(100, 100), samples: 1);
        Check.That(h.Engine.ActivationDwellMsForTest).IsEqualTo(1200);
        Check.That(h.Im.ActivationOffers!.DwellMs).IsEqualTo(1200);
    }

    // ── The full chain ───────────────────────────────────────────────────────

    [Fact]
    public void ARestAtP_FillsAndFires_AndTheCompletionOpensTheMagnifierAtP_ThenTheSpotIsLatched()
    {
        var settings = new EyeCtrlSettings { SelectionMode = SelectionMode.ManualSelection };
        settings.Dwell.FixationRadiusPixels = R;
        using var h = new EngineHarness(settings);
        var p = new PhysicalPoint(1500, 1100);
        var fired = new List<ActivationOffer>();
        var magnifierAt = new List<PhysicalPoint>();
        h.Im.ActivationOfferFired += (offer, _) => fired.Add(offer);
        h.Im.StartManualSelection += magnifierAt.Add;

        h.Feed(p, samples: 20);                                    // 600 ms: onset, settle, offer
        var offer = h.Im.ActivationOffers!.CurrentOffer!;
        Check.That(offer.Style).IsEqualTo(OfferPlacementStyle.AtRestPoint);
        Check.That(offer.Button).IsEqualTo(p);
        Check.That(fired).IsEmpty();

        h.Feed(p, samples: 25);                                    // 750 ms more: past F = 700

        Check.That(fired).HasSize(1);
        Check.That(h.Im.OnActivationButtonDwelled(fired[0])).IsEqualTo(ActivationCompletionResult.Accepted);
        Check.That(h.Im.CurrentState).IsEqualTo(InteractionState.ManualSelectionActive);
        Check.That(magnifierAt).ContainsExactly(p);

        h.Feed(p, samples: 1);                                     // the accept lands on the gaze thread
        Check.That(h.Im.ActivationOffers!.CurrentOffer).IsNull();
        Check.That(h.Im.ActivationOffers.RearmLatch).IsEqualTo(p);

        // In the magnifier the gaze roams far from P: GazeEngine's per-sample state snapshot keeps
        // those samples from clearing the latch (R1-M12), so back in FreeLook a rest at P is refused.
        var far = new PhysicalPoint(p.X + 700, p.Y + 500);
        h.Feed(far, samples: 30);
        h.Im.OnManualSelectionBack();
        h.Feed(p, samples: 25);                                    // a whole new run: onset, settle
        Check.That(h.Im.CurrentState).IsEqualTo(InteractionState.Fixation);
        Check.That(h.Im.ActivationOffers.CurrentOffer).IsNull();
        Check.That(h.Im.ActivationOffers.RearmLatch).IsEqualTo(p);

        // Looking away in FreeLook clears it; the next rest at P is offered again.
        h.Feed(far, samples: 3);
        Check.That(h.Im.ActivationOffers.RearmLatch).IsNull();
        h.Feed(p, samples: 25);
        Check.That(h.Im.ActivationOffers.CurrentOffer?.Button).IsEqualTo(p);
    }
}
