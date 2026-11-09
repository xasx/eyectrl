namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// The ring-arm look build's hybrid hold (spec: ring-stutter-investigation/replay-2, "The hybrid,
/// precisely", and the ring-arm-look-build ticket's "What the product must mirror EXACTLY").
/// <see cref="RingHoldPolicy.None"/> is the default and today's behaviour, covered by
/// <see cref="CursorCircleViewModelTests"/>; these tests pin <see cref="RingHoldPolicy.HybridFreeze"/>
/// rule by rule. Coordinates are physical px, R = 80, frames 30 ms.
/// </summary>
public class CursorCircleViewModelHybridTests
{
    private const long FrameUs = 30_000;
    private const double R = 80;
    private static readonly PhysicalPoint P = new(1000, 1000);

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public CursorCircleViewModel Vm { get; }
        public List<RingPinProcessedEvent> Pins { get; } = new();
        public List<RingReleaseProcessedEvent> ProcessedReleases { get; } = new();
        public List<RingReleaseEvent> Releases { get; } = new();
        public List<RingHoldStartEvent> Holds { get; } = new();
        public List<RingHoldEndEvent> HoldEnds { get; } = new();
        public List<RingStayStartEvent> StayStarts { get; } = new();
        public List<RingStayEndEvent> StayEnds { get; } = new();

        private long _us;

        public Rig(RingHoldPolicy policy = RingHoldPolicy.HybridFreeze)
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = R;
            Vm = new CursorCircleViewModel(settings, Messenger) { RingHoldPolicy = policy };
            Vm.RingPinProcessed += e => Pins.Add(e);
            Vm.RingReleaseProcessed += e => ProcessedReleases.Add(e);
            Vm.RingReleased += e => Releases.Add(e);
            Vm.RingHoldStarted += e => Holds.Add(e);
            Vm.RingHoldEnded += e => HoldEnds.Add(e);
            Vm.RingStayStarted += e => StayStarts.Add(e);
            Vm.RingStayEnded += e => StayEnds.Add(e);
        }

        public void Frame(PhysicalPoint position, int steps = 1)
        {
            for (int i = 0; i < steps; i++)
            {
                _us += FrameUs;
                Messenger.Send(new GazeCursorMovedMessage(position, _us));
            }
        }

        public void State(InteractionState state) => Messenger.Send(new StateChangedMessage(state));
        public void Pin(PhysicalPoint point) => Messenger.Send(new PinRingMessage(point));
        public void Release() => Messenger.Send(new ReleaseRingMessage());
        public void Offer(PhysicalPoint button, double hitRadiusPx) =>
            Messenger.Send(new ShowActivationButtonMessage(button, hitRadiusPx));
        /// <summary>An offer as the adapter publishes it: B, the hit radius, P and the engine's effective R.</summary>
        public void OfferAt(PhysicalPoint button, double hitRadiusPx, PhysicalPoint offerPoint,
            double? fixationRadiusPx = R) =>
            Messenger.Send(new ShowActivationButtonMessage(button, hitRadiusPx, offerPoint, fixationRadiusPx));

        /// <summary>Lets <paramref name="micros"/> of sample time pass with no sample (the next frame lands later).</summary>
        public void Gap(long micros) => _us += micros;
        public void OfferCloses() => Messenger.Send(new HideActivationButtonMessage());
        public void ExternalDwell(bool active) => Messenger.Send(new ExternalDwellActiveMessage(active));

        /// <summary>Pins the ring the way Core's run-only policy does: a fixation and a settle at the point.</summary>
        public void PinAt(PhysicalPoint point)
        {
            Frame(point);
            State(InteractionState.Fixation);
            Pin(point);
            Frame(point, steps: 10);
        }

        /// <summary>The run-end pair Core's run-only policy raises, in order: the release, then the state.</summary>
        public void RunEnds()
        {
            Release();
            State(InteractionState.FreeLook);
        }
    }

    [Fact]
    public void Hybrid_Stay_KeepsThePinThroughTheButtonVisit_ThenReleasesAfterTheGraceOutsideTheZone()
    {
        // The run ends while the landing gaze sits inside the offer's no-move zone (hit 150 + R 80)
        // but outside the hit area: the pin STAYS, the eye's entry into the hit area changes nothing
        // (no hold), a slip back to the zone's edge changes nothing, and only 500 ms clearly outside the
        // zone lets the ring follow.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);

        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // 200 px from B: in the zone, not in the hit

        Check.That(rig.StayStarts).HasSize(1);
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);                 // still at the pin

        rig.State(InteractionState.Fixation);                     // the next run forms under the stay
        rig.Frame(new PhysicalPoint(1300, 1000));                 // 100 px from B: into the hit area
        rig.Frame(new PhysicalPoint(1320, 1010), steps: 3);       // the eye dwells on the button...
        rig.Frame(new PhysicalPoint(1200, 1000), steps: 30);      // ...and on the zone's edge for 900 ms

        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);                 // the ring never left the word

        rig.Frame(new PhysicalPoint(900, 1300), steps: 17);       // clearly elsewhere: t0 .. t0 + 480 ms
        Check.That(rig.StayEnds).IsEmpty();

        rig.Frame(new PhysicalPoint(900, 1300));                  // t0 + 510 ms

        Check.That(rig.StayEnds).HasSize(1);
        Check.That(rig.StayEnds[0].Reason).IsEqualTo(RingStayEndReason.Left);
        Check.That(rig.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(900, 1300));   // snapped to the gaze
    }

    [Fact]
    public void Hybrid_Freeze_StopsTheFollowingRingWhereItIsDrawn_BeforeTheFollow()
    {
        // Any other entry into the hit area stops the ring where it is DRAWN, before that sample's
        // follow — no glide back to a stale pin. Mutant: drop the freeze (let the ring follow) → the
        // ring rides the gaze over the button.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));                   // the ring follows here
        rig.Offer(b, hitRadiusPx: 150);

        rig.Frame(new PhysicalPoint(600, 600));                   // the eye lands on B

        Check.That(rig.Holds).HasSize(1);
        Check.That(rig.Holds[0].Kind).IsEqualTo(RingHoldStartKind.Freeze);
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));   // frozen where it was drawn

        rig.Frame(new PhysicalPoint(700, 650), steps: 3);         // the eye moves inside the hit area
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));   // the ring does not follow
    }

    [Fact]
    public void Hybrid_HoldIgnoresSettlesAndRunEnds()
    {
        // "Hold: while holding, settles and run ends are ignored, so there is no re-pin and no snap."
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // the freeze starts the hold
        rig.State(InteractionState.Fixation);                     // the run at the button forms

        rig.Pin(new PhysicalPoint(500, 500));                     // a settle mid-hold: ignored
        Check.That(rig.Pins[^1].Applied).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));

        rig.Release();                                            // a run end mid-hold: ignored
        Check.That(rig.ProcessedReleases[^1].ReleasedNow).IsFalse();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.State(InteractionState.FreeLook);                     // the run flips under the hold

        Check.That(rig.HoldEnds).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));
    }

    [Fact]
    public void Hybrid_UnprovenExit_BeyondTheZone_ReleasesOnTheFirstSampleOutside()
    {
        // Before the hold has proven itself (< 3 in-hit samples), the first sample BEYOND the no-move
        // zone (hit 150 + R 80 = 230 px) releases, so a fly-through does not freeze the ring for half
        // a second (GA2). Mutant: apply the grace regardless of the count / swallow exits beyond the
        // zone → no end on the first outside sample. (Was 200 px from B before the edge-tolerant
        // hold: a slip inside the zone no longer ends an unproven hold, see GA1/GA3.)
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze: 1 in-hit sample

        rig.Frame(new PhysicalPoint(860, 600));                   // 260 px from B: beyond the zone

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Unproven);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(860, 600));   // snapped to the gaze
    }

    [Fact]
    public void Hybrid_UnprovenSlip_InsideTheZone_KeepsTheHold()
    {
        // GA1: one in-hit sample, then a sample 190 px from B (outside the 150-px hit area, inside
        // the 230-px zone): the ring stays frozen, the hold stays on, nothing is released. Coming
        // back into the hit area keeps holding. Mutant MA1: the unproven exit ignores the zone.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze: 1 in-hit sample

        rig.Frame(new PhysicalPoint(790, 600));                   // 190 px from B: a slip

        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.HoldEnds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();

        rig.Frame(new PhysicalPoint(620, 600), steps: 3);         // back in the hit area

        Check.That(rig.HoldEnds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));
    }

    [Fact]
    public void Hybrid_UnprovenSlip_InsideTheZone_EndsAfterTheGrace()
    {
        // GA3: an unproven hold whose gaze stays outside the hit area (inside the zone) is let go
        // after the grace, as Unproven; just under it, still holding. The first outside sample is at
        // t0, the 17th (480 ms later) is still within it, the 18th (510 ms) is not. Mutant MA3: no
        // grace for unproven slips (the ring stays stuck).
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze: 1 in-hit sample

        rig.Frame(new PhysicalPoint(790, 600), steps: 17);        // t0 .. t0 + 480 ms

        Check.That(rig.HoldEnds).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.Frame(new PhysicalPoint(790, 600));                   // t0 + 510 ms

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Unproven);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(790, 600));
    }

    [Fact]
    public void Hybrid_OfferClose_DuringAnUnprovenSlip_EndsTheHoldAndReleases()
    {
        // GA5: the offer closes while an unproven hold slips inside the zone: OfferClosed, released.
        // Mutant MA4: offer-closed skipped while slipping.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze: 1 in-hit sample
        rig.Frame(new PhysicalPoint(790, 600), steps: 2);         // slipping, inside the zone
        Check.That(rig.HoldEnds).IsEmpty();

        rig.OfferCloses();

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.OfferClosed);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(790, 600));
    }

    [Fact]
    public void Hybrid_SettlePin_InsideALiveOffersZone_IsIgnored()
    {
        // GD1: a near-miss settle 200 px from B (inside hit 150 + R 80, nearer B than P) while the
        // offer lives: no pin, no glide; the ring keeps following. Mutant MD1: the zone check removed.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.OfferAt(b, hitRadiusPx: 150, new PhysicalPoint(300, 300));
        rig.State(InteractionState.Fixation);

        rig.Pin(new PhysicalPoint(800, 600));                     // 200 px from B, 583 from P: a near miss

        Check.That(rig.Pins).HasSize(1);
        Check.That(rig.Pins[0].Applied).IsFalse();
        Check.That(rig.Pins[0].IgnoredNearOffer).IsTrue();
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(300, 300));

        rig.Frame(new PhysicalPoint(320, 310));                   // no glide: the next sample is followed

        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(320, 310));
    }

    [Fact]
    public void Hybrid_SettlePin_OutsideTheZoneOrWithoutAnOffer_GlidesAsToday()
    {
        // GD2: a pin 300 px from B (the offer's own settle at P is 310 px away) and a pin with no
        // live offer are unchanged. Mutant MD2: every pin ignored while an offer lives.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.OfferAt(b, hitRadiusPx: 150, new PhysicalPoint(300, 300));
        rig.State(InteractionState.Fixation);

        rig.Pin(new PhysicalPoint(900, 600));                     // 300 px from B

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.Vm.IsPinned).IsTrue();

        var rig2 = new Rig();
        rig2.Frame(new PhysicalPoint(300, 300));
        rig2.State(InteractionState.Fixation);

        rig2.Pin(new PhysicalPoint(800, 600));                    // the same point, but no offer

        Check.That(rig2.Pins[^1].Applied).IsTrue();
        Check.That(rig2.Vm.IsPinned).IsTrue();
    }

    [Fact]
    public void None_SettlePin_InsideAnOffersZone_StillGlides()
    {
        // GD3: policy None keeps today's behaviour bit for bit. Mutant MD3: D applied regardless of
        // the policy.
        var rig = new Rig(RingHoldPolicy.None);
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.OfferAt(b, hitRadiusPx: 150, new PhysicalPoint(300, 300));
        rig.State(InteractionState.Fixation);

        rig.Pin(new PhysicalPoint(800, 600));                     // 200 px from B

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.Vm.IsPinned).IsTrue();
    }

    [Fact]
    public void Hybrid_OfferClose_EndsTheHoldAndReleases()
    {
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze
        rig.Frame(new PhysicalPoint(620, 600), steps: 3);         // proven

        rig.OfferCloses();

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.OfferClosed);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(620, 600));
    }

    [Fact]
    public void Hybrid_HidingState_EndsTheHoldSilently_AndTheStateSnaps()
    {
        // "StateChanged to a hiding state: drop pending and approach, end the hold silently; the
        // existing code clears the pin and snaps."
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze
        rig.Frame(new PhysicalPoint(620, 600), steps: 3);         // proven

        rig.State(InteractionState.Paused);

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Hidden);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HidingState);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.IsVisible).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(620, 600));
    }

    [Fact]
    public void Hybrid_Stay_LeavingTheZone_ReleasesOnlyAfterTheGrace_AndStayingInsideNeverExpires()
    {
        // The stay has no time limit while the gaze is in the zone (here the no-move zone only, the
        // show message carries no P), and a step out of the zone starts the grace instead of releasing
        // at once. Mutants M3 (G = 0) and M4 (a time limit T restored) → the corresponding half reds.
        var b = new PhysicalPoint(1400, 1000);
        var rig = new Rig();
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: the stay starts

        rig.Frame(new PhysicalPoint(1000, 1000));                 // out of the zone (400 px from B)

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.Frame(new PhysicalPoint(1000, 1000), steps: 17);      // the first outside sample + 510 ms

        Check.That(rig.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Holds).IsEmpty();

        // Staying inside the zone keeps the pin far past the old 500 ms window.
        var rig2 = new Rig();
        rig2.PinAt(P);
        rig2.Offer(b, hitRadiusPx: 150);
        rig2.RunEnds();
        rig2.Frame(new PhysicalPoint(1200, 1000), steps: 40);     // 1.2 s on the zone's edge
        Check.That(rig2.Releases).IsEmpty();
        Check.That(rig2.Vm.IsPinned).IsTrue();
        Check.That(rig2.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Hybrid_PendingLandingOnTheButton_StaysAtThePin()
    {
        // The eye reaches the hit area on the very cursor that resolves the pending run-end: the stay
        // begins with the ring right where it was pinned (no hold: the stay covers the button too).
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();

        rig.Frame(new PhysicalPoint(1300, 1000));                 // 100 px from B: inside the hit area

        Check.That(rig.StayStarts).HasSize(1);
        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Hybrid_OfferCloseDuringTheStay_Releases()
    {
        // X5 (fixup 01), now for the stay: the offer closes (a fire, an expiry, a teardown); the
        // deferred run-end release happens then. Mutant M8: the stay ignores the Hide → no release.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: the stay starts

        rig.OfferCloses();

        Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.OfferClosed);
        Check.That(rig.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.OfferClosed);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(1200, 1000));
    }

    [Fact]
    public void Hybrid_ExternalDwellHide_ReleasesAHeldPin()
    {
        // A cooperating app's target hides the ring while the hybrid holds. That hide is NOT a state
        // change, so nothing else snapped the pin (fixup 01 F2): the owed release must happen here,
        // or the ring stays pinned 1000+ px from the eye until Core's next release. Review 01's probe
        // sequence: dwell on → a sample at (2000,1500) → off → 10 samples.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // the freeze starts the hold

        rig.ExternalDwell(active: true);
        Check.That(rig.Vm.IsVisible).IsFalse();
        rig.Frame(new PhysicalPoint(2000, 1500));                 // the gaze moves while hidden
        rig.ExternalDwell(active: false);
        rig.Frame(new PhysicalPoint(2000, 1500), steps: 10);

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Hidden);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(2000, 1500));
    }

    [Fact]
    public void Hybrid_ExternalDwellHide_ReleasesAPendingRunEnd()
    {
        // Same hide with a deferred run-end in flight: the release the hybrid was waiting to resolve
        // must not be lost (fixup 01 F2). Same probe sequence as above.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();                                            // the release is deferred: pending

        Check.That(rig.Vm.IsPinned).IsTrue();                     // still at the pin, waiting for a sample

        rig.ExternalDwell(active: true);
        rig.Frame(new PhysicalPoint(2000, 1500));                 // the gaze moves while hidden
        rig.ExternalDwell(active: false);
        rig.Frame(new PhysicalPoint(2000, 1500), steps: 10);

        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(2000, 1500));
    }

    [Fact]
    public void Hybrid_ExternalDwellHide_ReleasesAStayingPin()
    {
        // The F2c survivor (fixup 02), now for the stay: the same hide must cover a run end inside the
        // stay zone with the eye not in the hit area. Same probe sequence.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: the stay is active

        // The arrangement guard: the stay really was on when the hide arrived — no hold, no release
        // yet, and the pin still up.
        Check.That(rig.StayStarts).HasSize(1);
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.ExternalDwell(active: true);
        rig.Frame(new PhysicalPoint(2000, 1500));                 // the gaze moves while hidden
        rig.ExternalDwell(active: false);
        rig.Frame(new PhysicalPoint(2000, 1500), steps: 10);

        Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Hidden);
        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(2000, 1500));
    }

    [Fact]
    public void Hybrid_SettlePin_OnPsSideOfTheZone_PinsAndGlidesAsToday()
    {
        // GD4: a settle inside the no-move zone but nearer P than B is the engine's "swallowed" one (the
        // owner looking back at the target): the ring pins and glides as before. B=(600,600),
        // P=(300,300) (424 px apart); the point 225 px from B toward P is 199 px from P. Mutant MD4:
        // drop the nearer-B condition (ignore every in-zone settle).
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        var p = new PhysicalPoint(300, 300);
        rig.Frame(p);
        rig.OfferAt(b, hitRadiusPx: 150, p);
        rig.State(InteractionState.Fixation);
        var swallowed = new PhysicalPoint(600 - 225 / Math.Sqrt(2), 600 - 225 / Math.Sqrt(2));
        Check.That(swallowed.DistanceTo(b)).IsCloseTo(225.0, 1e-9);                  // arrangement: in the zone (230)
        Check.That(swallowed.DistanceTo(p)).IsStrictlyLessThan(swallowed.DistanceTo(b));   // ...and nearer P

        rig.Pin(swallowed);

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.Pins[^1].IgnoredNearOffer).IsFalse();
        Check.That(rig.Vm.IsPinned).IsTrue();
    }

    [Fact]
    public void Hybrid_SettlePin_WithoutTheOffersPoint_IsNeverIgnored()
    {
        // A sender that does not carry P cannot tell a near miss from a swallowed settle: D stays off
        // (today's pin), rather than guessing.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.State(InteractionState.Fixation);

        rig.Pin(new PhysicalPoint(800, 600));                     // would be a near miss

        Check.That(rig.Pins[^1].Applied).IsTrue();
    }

    [Fact]
    public void Hybrid_ZoneRadius_ComesFromTheOffersMessage_ForTheSlipAndForTheNearMissPin()
    {
        // GZ1: the engine's effective R (adaptation) rides the show message; the VM's settings say 80
        // (zone 230) but the offer says R 40 (zone 190) or R 160 (zone 310). Both A's slip tolerance and
        // D's near-miss ignore use the message's radius. Mutant MZ1: use the settings value.
        var b = new PhysicalPoint(600, 600);
        var p = new PhysicalPoint(300, 300);

        // A, R 40 (zone 190): a slip 210 px from B is beyond the zone, so the unproven hold ends at once.
        var narrow = new Rig();
        narrow.Frame(p);
        narrow.OfferAt(b, hitRadiusPx: 150, p, fixationRadiusPx: 40);
        narrow.Frame(new PhysicalPoint(600, 600));                // freeze: 1 in-hit sample
        narrow.Frame(new PhysicalPoint(810, 600));                // 210 px: beyond 190 (inside the settings' 230)
        Check.That(narrow.HoldEnds).HasSize(1);
        Check.That(narrow.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Unproven);

        // A, R 160 (zone 310): a slip 260 px from B is tolerated (the settings' 230 would end it).
        var wide = new Rig();
        wide.Frame(p);
        wide.OfferAt(b, hitRadiusPx: 150, p, fixationRadiusPx: 160);
        wide.Frame(new PhysicalPoint(600, 600));
        wide.Frame(new PhysicalPoint(860, 600));                  // 260 px
        Check.That(wide.HoldEnds).IsEmpty();
        Check.That(wide.Vm.IsPinned).IsTrue();

        // D, R 160: a near-miss settle 260 px from B (nearer B than P) is ignored; with R 40 it is
        // outside the zone and pins.
        var wideD = new Rig();
        wideD.Frame(p);
        wideD.OfferAt(b, hitRadiusPx: 150, p, fixationRadiusPx: 160);
        wideD.State(InteractionState.Fixation);
        wideD.Pin(new PhysicalPoint(860, 600));
        Check.That(wideD.Pins[^1].Applied).IsFalse();

        var narrowD = new Rig();
        narrowD.Frame(p);
        narrowD.OfferAt(b, hitRadiusPx: 150, p, fixationRadiusPx: 40);
        narrowD.State(InteractionState.Fixation);
        narrowD.Pin(new PhysicalPoint(790, 600));                 // 190 px: inside the settings' zone, on the zone edge of 190
        Check.That(narrowD.Pins[^1].Applied).IsFalse();           // exactly at 190: still in
        narrowD.Pin(new PhysicalPoint(800, 600));                 // 200 px: beyond 190 (inside the settings' 230)
        Check.That(narrowD.Pins[^1].Applied).IsTrue();
    }

    [Fact]
    public void Hybrid_UnprovenSlip_ExactlyAtTheZoneRadius_IsStillTolerated()
    {
        // F5: ">" means beyond: a sample at exactly hit 150 + R 80 = 230 px keeps the hold; one a pixel
        // further ends it. Mutant MX2: ">" -> ">=".
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.Frame(new PhysicalPoint(600, 600));                   // freeze: 1 in-hit sample

        rig.Frame(new PhysicalPoint(830, 600));                   // exactly 230 px

        Check.That(rig.HoldEnds).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.Frame(new PhysicalPoint(831, 600));                   // 231 px

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Unproven);
    }

    [Fact]
    public void Hybrid_Grace_ExactlyFiveHundredMs_Ends_AndOneMicrosecondLessDoesNot()
    {
        // F5: the grace is ">= 500 ms outside the hit area". The first outside sample is at t0; a sample
        // at exactly t0 + 500 ms ends the hold, one at t0 + 499.999 ms does not. Mutant MX3: ">=" -> ">".
        foreach (var (gapUs, ends) in new[] { (469_999L, false), (470_000L, true) })
        {
            var rig = new Rig();
            var b = new PhysicalPoint(600, 600);
            rig.Frame(new PhysicalPoint(300, 300));
            rig.Offer(b, hitRadiusPx: 150);
            rig.Frame(new PhysicalPoint(600, 600));               // freeze
            rig.Frame(new PhysicalPoint(790, 600));               // t0: the first sample outside
            rig.Gap(gapUs);                                       // the next frame lands at t0 + gap + 30 ms
            rig.Frame(new PhysicalPoint(790, 600));

            Check.WithCustomMessage($"gap {gapUs} us").That(rig.HoldEnds.Count).IsEqualTo(ends ? 1 : 0);
        }
    }

    // ── The stay zone (F2, 5th sitting 2026-11-09, owner option A) ─────────────────────────────────
    // The owner's geometry: hit 200 (LayoutConstants since F2), R 80, so B's no-move zone is 280 px;
    // P = (1000,1000), B = (1310,1000): |B-P| = 310, the placement floor hit + R + 30 at ring 200.

    private const double OwnerHit = 200;
    private static readonly PhysicalPoint OwnerB = new(1310, 1000);

    /// <summary>A pinned ring at P, a live offer at the owner's geometry, the run ended, and the gaze
    /// resolving the pending release at <paramref name="landing"/> (the stay starts there when it is in Z).</summary>
    private static Rig OwnerStay(PhysicalPoint landing, double? fixationRadiusPx = R)
    {
        var rig = new Rig();
        rig.PinAt(P);
        rig.OfferAt(OwnerB, OwnerHit, P, fixationRadiusPx);
        rig.RunEnds();
        rig.Frame(landing);
        return rig;
    }

    private static PhysicalPoint Below(PhysicalPoint b, double dist) => new(b.X, b.Y + dist);

    [Fact]
    public void Stay_S1_OnTheButton_ThenOnItsRim_TheRingStaysOnTheWord()
    {
        // Report test 1 + 2: the eye on B (5 samples, or only 2: the old proven/unproven split), then on
        // the rim 240 px from B (outside the 200-px hit, inside the 280-px zone) for 600 ms. Today:
        // `HoldEnd Left` / `Unproven` and a snap onto the button. Mutant M1 (the stay keyed back to the
        // hit radius) → released after 500 ms on the rim.
        foreach (int onB in new[] { 5, 2 })
        {
            var rig = OwnerStay(OwnerB);
            rig.Frame(OwnerB, steps: onB - 1);
            rig.Frame(Below(OwnerB, 240), steps: 20);

            Check.WithCustomMessage($"{onB} samples on B").That(rig.Releases).IsEmpty();
            Check.That(rig.StayEnds).IsEmpty();
            Check.That(rig.Holds).IsEmpty();
            Check.That(rig.Vm.IsPinned).IsTrue();
            Check.That(rig.Vm.Position).IsEqualTo(P);
        }
    }

    [Fact]
    public void Stay_S3_ANearMissSettleDuringTheStay_IsRefused_AndTheStayGoesOn()
    {
        // Report test 3: a settle 250 px below B (in the zone, much nearer B than P) during the stay.
        // Today: the keep ends `Settled` and the ring follows. Mutant M5 (a near-miss settle releases
        // again) → a release, the ring at the gaze.
        var nearMiss = Below(OwnerB, 250);
        var rig = OwnerStay(nearMiss);
        rig.Frame(nearMiss, steps: 15);
        rig.State(InteractionState.Fixation);

        rig.Pin(nearMiss);
        rig.Frame(nearMiss, steps: 10);

        Check.That(rig.Pins[^1].Applied).IsFalse();
        Check.That(rig.Pins[^1].IgnoredNearOffer).IsTrue();
        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Stay_S4_ARunEndInTheZone_NeverInTheHit_StaysPastTheOldTimeLimit()
    {
        // Report test 4: the run ends with the gaze 250 px from B (in the zone, never in the hit) and
        // it stays there 700 ms. Today: the keep times out at T = 500 ms and snaps. Mutant M4 (T
        // restored) → released at 500 ms.
        var rim = Below(OwnerB, 250);
        var rig = OwnerStay(rim);
        rig.Frame(rim, steps: 23);                                // 720 ms

        Check.That(rig.StayStarts).HasSize(1);
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Stay_S5_ClearlyOutsideTheZone_ReleasesAtTheGrace_NotBefore()
    {
        // Report test 5: during the stay the gaze goes 400 px off the corridor (and far from B). With the
        // first outside sample at t0, a frame at t0 + 499.999 ms still stays, one at t0 + 500 ms releases
        // onto the gaze. Today the keep's `Left` releases on the first outside frame. Mutants M3 (G = 0)
        // and `>=` → `>`.
        var away = new PhysicalPoint(1150, 1400);                 // 400 px off the P-B axis, 431 from B
        foreach (var (gapUs, releases) in new[] { (469_999L, false), (470_000L, true) })
        {
            var rig = OwnerStay(OwnerB);
            rig.Frame(away);                                      // t0
            Check.That(rig.Releases).IsEmpty();
            rig.Gap(gapUs);                                       // the next frame lands at t0 + gap + 30 ms
            rig.Frame(away);

            Check.WithCustomMessage($"gap {gapUs} us").That(rig.Releases.Count).IsEqualTo(releases ? 1 : 0);
            Check.WithCustomMessage($"gap {gapUs} us").That(rig.Vm.IsPinned).IsEqualTo(!releases);
            if (releases)
            {
                Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Left);
                Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.RunEnd);
                Check.That(rig.Vm.Position).IsEqualTo(away);
            }
        }
    }

    [Fact]
    public void Stay_S6_ABriefExcursion_ResetsTheGrace()
    {
        // 300 ms clearly outside, back into the zone for one sample, 300 ms outside again: never 500 ms
        // outside in one stretch, so the ring stays. Mutant: the outside timer not reset in the zone.
        var away = new PhysicalPoint(1150, 1400);
        var rig = OwnerStay(OwnerB);
        rig.Frame(away, steps: 10);
        rig.Frame(OwnerB);
        rig.Frame(away, steps: 10);

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Stay_S7_TheZoneRadius_ComesFromTheOffersMessage()
    {
        // The zone is hit + the message's R: R 160 (zone 360) keeps a gaze 330 px below B for 600 ms; the
        // settings' R 80 (zone 280) would release it. Mutant M6 (the setting's R) → released.
        var wide = OwnerStay(Below(OwnerB, 330), fixationRadiusPx: 160);
        wide.Frame(Below(OwnerB, 330), steps: 20);
        Check.That(wide.Releases).IsEmpty();
        Check.That(wide.Vm.IsPinned).IsTrue();

        var narrow = OwnerStay(OwnerB, fixationRadiusPx: 40);     // zone 240
        narrow.Frame(Below(OwnerB, 260), steps: 20);              // 260 px: outside 240, inside the settings' 280
        Check.That(narrow.Releases).HasSize(1);
    }

    [Fact]
    public void Stay_S8_APsSideSettleInTheZone_RePins_AndTheStayGoesOn()
    {
        // A settle in the zone but nearer P than B (the swallowed settle, the owner looking back at the
        // word) re-pins as today, and the stay goes on at the new pin: a later look at the button's rim
        // keeps the ring there. Mutant: every settle during the stay ends it.
        var q = new PhysicalPoint(1060, 1030);                   // in the corridor, nearer P
        var rig = OwnerStay(q);
        rig.Frame(q, steps: 15);
        rig.State(InteractionState.Fixation);

        rig.Pin(q);
        rig.Frame(q, steps: 12);                                  // the glide lands on q

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(q);

        rig.RunEnds();                                            // q's run ends: ignored under the stay
        rig.Frame(Below(OwnerB, 240), steps: 20);

        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(q);
    }

    [Fact]
    public void Stay_S9_ASettleOutsideTheZone_EndsTheStay_AndPinsThere()
    {
        // The eye stops clearly elsewhere and the detector settles there before the grace ran out: the
        // stay ends (settled elsewhere) and the ring glides to the new settle. No release in between.
        var elsewhere = new PhysicalPoint(700, 1500);
        var rig = OwnerStay(OwnerB);
        rig.Frame(elsewhere, steps: 9);                           // 270 ms outside: inside the grace
        rig.State(InteractionState.Fixation);

        rig.Pin(elsewhere);

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Settled);
        Check.That(rig.Releases).IsEmpty();

        rig.Frame(elsewhere, steps: 12);
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(elsewhere);
        Check.That(rig.StayEnds).HasSize(1);
    }

    [Fact]
    public void Stay_S10_APinWithNoFixationUp_ChangesNothing()
    {
        // A pin refused for the state alone (no Fixation reported) is not a settle: the stay goes on.
        var rig = OwnerStay(OwnerB);
        rig.Pin(new PhysicalPoint(700, 1500));                    // the machine is in FreeLook

        Check.That(rig.Pins[^1].Applied).IsFalse();
        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Stay_S11_TheOfferClosingOrAHidingState_EndsTheStay()
    {
        // The Hide ends the stay with its release (offer-closed); a hiding state takes the hidden path.
        // Mutant M8: the stay ignores the Hide.
        var closed = OwnerStay(OwnerB);
        closed.Frame(OwnerB, steps: 5);
        closed.OfferCloses();
        Check.That(closed.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.OfferClosed);
        Check.That(closed.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.OfferClosed);
        Check.That(closed.Vm.IsPinned).IsFalse();
        Check.That(closed.Vm.Position).IsEqualTo(OwnerB);

        var hidden = OwnerStay(OwnerB);
        hidden.State(InteractionState.Paused);
        Check.That(hidden.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Hidden);
        Check.That(hidden.Vm.IsPinned).IsFalse();
        Check.That(hidden.Vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Stay_S12_AFireHidesTheRing_TheHideThenTheHidingState()
    {
        // Guard 6: the fire posts the Hide and StateChanged(ManualSelectionActive) together. The Hide
        // releases onto the gaze at B; the state change, delivered in the same dispatcher burst, hides
        // the ring before any frame is drawn. After both, the ring is hidden and not pinned.
        var rig = OwnerStay(OwnerB);
        rig.State(InteractionState.Fixation);
        rig.Frame(OwnerB, steps: 15);

        rig.OfferCloses();
        rig.State(InteractionState.ManualSelectionActive);

        Check.That(rig.Vm.IsVisible).IsFalse();
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.OfferClosed);
    }

    [Fact]
    public void Stay_S13_ANewShowReKeysTheZone()
    {
        // A new show while staying (its own B and P) re-keys Z: a gaze in the NEW zone but far outside the
        // old one keeps the ring. Mutant M7 (the show ignored while staying) → released after the grace.
        var rig = OwnerStay(OwnerB);
        var b2 = new PhysicalPoint(1310, 1600);
        rig.OfferAt(b2, OwnerHit, new PhysicalPoint(1000, 1600));
        rig.Frame(Below(b2, 240), steps: 20);                     // 840 px below the old B

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Stay_S14_TheOfferClosesWhileASettlesRunStillHolds_ThePinStays_AndTheNextOfferGetsItsStay()
    {
        // The F3 hop: the swallowed settle re-pins at q during the stay and its run still holds when the
        // old offer closes and the re-offer shows (Hide, then Show). Core's RunOnly pin is live, so the
        // stay ends offer-closed WITHOUT a release; q's own run end then starts a stay keyed to the new
        // offer. Mutant: the Hide always releases → the ring snaps to the gaze.
        var q = new PhysicalPoint(1060, 1030);
        var rig = OwnerStay(q);
        rig.Frame(q, steps: 15);
        rig.State(InteractionState.Fixation);
        rig.Pin(q);
        rig.Frame(q, steps: 12);

        rig.OfferCloses();
        var b2 = new PhysicalPoint(1370, 1030);
        rig.OfferAt(b2, OwnerHit, q);

        Check.That(rig.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.OfferClosed);
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.Frame(q, steps: 3);
        rig.RunEnds();
        rig.Frame(Below(b2, 240), steps: 20);                     // the new button's rim

        Check.That(rig.StayStarts).HasSize(2);
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(q);
    }

    // ── The stay's corridor (half-width 1.25 R around the segment P-B) ──────────────────────────────
    // P = (1000,1000), B = (1400,1000): 400 px apart on a horizontal axis; hit 150, R 80 (zone 230), so
    // the corridor reaches where B's zone does not.

    private static readonly PhysicalPoint GoB = new(1400, 1000);

    /// <summary>A pinned ring at P, a live offer carrying P and R, and the run ended (the deferred release waits).</summary>
    private static Rig GoRig(double? fixationRadiusPx = R, bool withP = true)
    {
        var rig = new Rig();
        rig.PinAt(P);
        if (withP)
            rig.OfferAt(GoB, hitRadiusPx: 150, P, fixationRadiusPx);
        else
            rig.Offer(GoB, hitRadiusPx: 150);
        rig.RunEnds();
        return rig;
    }

    [Fact]
    public void Corridor_C1_AGazeInTheCorridorOutsideTheZone_StartsTheStay_AndTheHitChangesNothing()
    {
        // The run ends with the gaze 40 px from P toward B (360 px from B: outside the 230-px zone). The
        // pin stays; the hit entry and the dwell keep it. Mutant M2: the zone only (no capsule) → the
        // run end releases at once.
        var rig = GoRig();

        rig.Frame(new PhysicalPoint(1040, 1000));

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
        Check.That(rig.StayStarts).HasSize(1);
        Check.That(rig.StayStarts[0].Dist).IsCloseTo(40.0, 1e-9);   // the ring (at P) to the gaze
        Check.That(rig.StayStarts[0].Off!.Value).IsCloseTo(0.0, 1e-9);   // on the axis

        rig.Frame(new PhysicalPoint(1100, 1010), steps: 14);      // 420 ms in the corridor
        rig.Frame(new PhysicalPoint(1300, 1000), steps: 20);      // the hit area, 600 ms

        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.StayEnds).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);
        Check.That(rig.Releases).IsEmpty();
    }

    [Fact]
    public void Corridor_C2_ALookBehindP_IsOutside_TheSegmentNotTheInfiniteLine()
    {
        // 200 px behind P on the P-B axis is 200 px from the segment (> 1.25 R = 100): outside Z. As the
        // run end's landing it releases at once (no stay); during a stay it runs the grace out. Mutant:
        // the infinite line instead of the segment.
        var behind = new PhysicalPoint(800, 1000);

        var atStart = GoRig();
        atStart.Frame(behind);
        Check.That(atStart.StayStarts).IsEmpty();
        Check.That(atStart.Releases).HasSize(1);
        Check.That(atStart.Vm.Position).IsEqualTo(behind);

        var during = GoRig();
        during.Frame(new PhysicalPoint(1040, 1000));
        during.Frame(behind, steps: 18);                          // t0 .. t0 + 510 ms
        Check.That(during.Releases).HasSize(1);
        Check.That(during.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Left);
        Check.That(during.StayEnds[0].Off!.Value).IsCloseTo(200.0, 1e-9);
        Check.That(during.Vm.Position).IsEqualTo(behind);
    }

    [Fact]
    public void Corridor_C3_TheCorridorUsesTheMessagesR_NotTheSetting()
    {
        // The corridor's half-width is 1.25 × the engine's R from the show message (settings say 80, so
        // the settings' corridor is 100). R 105 gives 131 px: a 120-px lateral stays (the settings' 100
        // would release); R 40 gives 50 px: a 60-px lateral is outside (the settings' 100 would keep).
        // Mutant M6: the setting's R instead of the message's.
        var wide = GoRig(fixationRadiusPx: 105);
        wide.Frame(new PhysicalPoint(1040, 1000));
        wide.Frame(new PhysicalPoint(1100, 1120), steps: 20);     // 120 px lateral; 323 px from B (zone 255)
        Check.That(wide.Releases).IsEmpty();
        Check.That(wide.Vm.IsPinned).IsTrue();

        var narrow = GoRig(fixationRadiusPx: 40);
        narrow.Frame(new PhysicalPoint(1040, 1000));
        narrow.Frame(new PhysicalPoint(1100, 1060), steps: 20);   // 60 px lateral; 306 px from B (zone 190)
        Check.That(narrow.Releases).HasSize(1);
        Check.That(narrow.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Left);
    }

    [Fact]
    public void Corridor_C4_AtR80_TheBoundary_Keeps99PxOffTheAxis_AndReleases101()
    {
        // At R = 80, 1.25 R = 100 px. A gaze 99 px off the axis (315.9 px from B, outside the 230-px zone)
        // stays for 600 ms; one 101 px off runs the grace out. Mutant: the factor at 1.0 → the 99-px gaze
        // releases; at 1.3 → the 101-px gaze stays.
        var kept = GoRig();
        kept.Frame(new PhysicalPoint(1040, 1000));
        kept.Frame(new PhysicalPoint(1100, 1099), steps: 20);

        Check.That(kept.Releases).IsEmpty();
        Check.That(kept.StayEnds).IsEmpty();

        var released = GoRig();
        released.Frame(new PhysicalPoint(1040, 1000));
        released.Frame(new PhysicalPoint(1100, 1101), steps: 20);

        Check.That(released.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.RunEnd);
        Check.That(released.StayEnds.Select(e => e.Reason)).ContainsExactly(RingStayEndReason.Left);
        Check.That(released.StayEnds[0].Off!.Value).IsCloseTo(101.0, 1e-9);
        Check.That(released.Vm.IsPinned).IsFalse();
    }

    [Fact]
    public void Corridor_C5_WithoutTheOffersPoint_TheZoneAloneApplies()
    {
        // A show message without P has no corridor: a gaze 40 px from P (360 px from B, outside the zone)
        // releases at once, a gaze inside the zone stays (and the log's off is unknown). The corridor's
        // 1.25 must not leak into the zone: 250 px from B is outside hit + R (230) though inside
        // hit + 1.25 R (287.5), and it releases too.
        var outside = GoRig(withP: false);
        outside.Frame(new PhysicalPoint(1040, 1000));
        Check.That(outside.Releases).HasSize(1);
        Check.That(outside.StayStarts).IsEmpty();

        var nearZone = GoRig(withP: false);
        nearZone.Frame(new PhysicalPoint(1150, 1000));            // 250 px from B
        Check.That(nearZone.Releases).HasSize(1);
        Check.That(nearZone.StayStarts).IsEmpty();

        var inside = GoRig(withP: false);
        inside.Frame(new PhysicalPoint(1200, 1000));
        Check.That(inside.Releases).IsEmpty();
        Check.That(inside.StayStarts).HasSize(1);
        Check.That(inside.StayStarts[0].Off).IsNull();
    }

    [Fact]
    public void None_IsTheDefault_AndTodayEmitsTheRawRecords()
    {
        // Arms with hold=None keep today's paths exactly; the log records still fire for the App's
        // event lines, carrying the drawn-to-target distance the readout aggregates.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);
        Check.That(vm.RingHoldPolicy).IsEqualTo(RingHoldPolicy.None);

        var pins = new List<RingPinProcessedEvent>();
        var releases = new List<RingReleaseProcessedEvent>();
        vm.RingPinProcessed += e => pins.Add(e);
        vm.RingReleaseProcessed += e => releases.Add(e);

        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 30_000));
        messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        messenger.Send(new PinRingMessage(new PhysicalPoint(400, 500)));

        Check.That(pins).HasSize(1);
        Check.That(pins[0].Applied).IsTrue();
        Check.That(pins[0].Target).IsEqualTo(new PhysicalPoint(400, 500));
        Check.That(pins[0].Dist).IsCloseTo(500.0, 1e-9);          // 300-400-500: the drawn ring to the pin

        messenger.Send(new PinRingMessage(new PhysicalPoint(450, 550)));   // mid-hold: applied normally
        Check.That(pins).HasSize(2);

        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), 60_000));
        messenger.Send(new ReleaseRingMessage());

        Check.That(releases).HasSize(1);
        Check.That(releases[0].ReleasedNow).IsTrue();
        Check.That(releases[0].Dist).IsCloseTo(Math.Sqrt(2 * 100 * 100), 1e-9);   // drawn (100,100) → gaze (200,200)
        Check.That(vm.IsPinned).IsFalse();
        Check.That(vm.Position).IsEqualTo(new PhysicalPoint(200, 200));

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        messenger.Send(new PinRingMessage(new PhysicalPoint(700, 700)));   // FreeLook: refused
        Check.That(pins).HasSize(3);
        Check.That(pins[2].Applied).IsFalse();

        messenger.Send(new ReleaseRingMessage());                 // a release with nothing pinned
        Check.That(releases).HasSize(2);
        Check.That(releases[1].ReleasedNow).IsFalse();
    }
}
