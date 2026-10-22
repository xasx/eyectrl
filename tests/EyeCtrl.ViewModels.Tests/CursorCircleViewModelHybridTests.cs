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
    public void Hybrid_ApproachKeep_HoldsWhereThePinWas_ThenReleasesAfterTheGrace()
    {
        // The run ends while the landing gaze sits inside the offer's no-move zone (hit 150 + R 80)
        // but outside the hit area: the pin is kept for up to 150 ms, and when the eye crosses into
        // the hit area the hold begins with the ring still where it was pinned.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);

        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // 200 px from B: in the zone, not in the hit

        Check.That(rig.Holds).IsEmpty();                          // approaching, not holding yet
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);                 // still at the pin

        rig.State(InteractionState.Fixation);                     // the next run forms under the approach
        rig.Pin(new PhysicalPoint(900, 900));                     // a settle during the approach is ignored
        Check.That(rig.Pins[^1].Applied).IsFalse();

        rig.Frame(new PhysicalPoint(1300, 1000));                 // 100 px from B: into the hit area

        Check.That(rig.Holds).HasSize(1);
        Check.That(rig.Holds[0].Kind).IsEqualTo(RingHoldStartKind.Approach);
        Check.That(rig.Vm.Position).IsEqualTo(P);                 // the ring stays where it was pinned

        rig.Frame(new PhysicalPoint(1320, 1010), steps: 3);       // the eye dwells on the button...
        Check.That(rig.Vm.Position).IsEqualTo(P);                 // ...and the ring does not follow

        rig.Frame(new PhysicalPoint(1200, 1000), steps: 10);      // 300 ms outside: within the grace
        Check.That(rig.HoldEnds).IsEmpty();

        rig.Frame(new PhysicalPoint(1200, 1000), steps: 20);      // past 500 ms outside in total

        Check.That(rig.HoldEnds).HasSize(1);
        Check.That(rig.HoldEnds[0].Reason).IsEqualTo(RingHoldEndReason.Left);
        Check.That(rig.HoldEnds[0].HeldMs).IsStrictlyGreaterThan(500);
        Check.That(rig.Releases[^1].Cause).IsEqualTo(RingReleaseCause.HoldEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(1200, 1000));   // snapped to the gaze
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
        // GD1: a settle 200 px from B (inside hit 150 + R 80) while the offer lives: no pin, no
        // glide; the ring keeps following. Mutant MD1: the zone check removed.
        var rig = new Rig();
        var b = new PhysicalPoint(600, 600);
        rig.Frame(new PhysicalPoint(300, 300));
        rig.Offer(b, hitRadiusPx: 150);
        rig.State(InteractionState.Fixation);

        rig.Pin(new PhysicalPoint(800, 600));                     // 200 px from B

        Check.That(rig.Pins).HasSize(1);
        Check.That(rig.Pins[0].Applied).IsFalse();
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
        rig.Offer(b, hitRadiusPx: 150);
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
        rig.Offer(b, hitRadiusPx: 150);
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
    public void Hybrid_ApproachWindow_LeavingTheZoneOrExpiring_Releases()
    {
        // The approach is a 150 ms window: leaving the no-move zone, or running it out without
        // entering the hit area, performs the deferred run-end release. Mutant: ignore the window
        // (release immediately, or never expire) → the corresponding half reds.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: approach starts

        rig.Frame(new PhysicalPoint(1000, 1000));                 // back out of the zone (400 px from B)

        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Holds).IsEmpty();

        // Staying inside the zone does not extend the window past 150 ms.
        var rig2 = new Rig();
        rig2.PinAt(P);
        rig2.Offer(b, hitRadiusPx: 150);
        rig2.RunEnds();
        rig2.Frame(new PhysicalPoint(1200, 1000));                // t0: the approach starts
        rig2.Frame(new PhysicalPoint(1200, 1000), steps: 5);      // 150 ms later, still in the zone

        Check.That(rig2.Releases).HasSize(1);
        Check.That(rig2.Vm.IsPinned).IsFalse();
        Check.That(rig2.Holds).IsEmpty();
    }

    [Fact]
    public void Hybrid_PendingLandingOnTheButton_KeepsThePin()
    {
        // The approach's other landing: the eye reaches the hit area on the very cursor that resolves
        // the pending run-end. The hold begins with the ring right where it was pinned.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();

        rig.Frame(new PhysicalPoint(1300, 1000));                 // 100 px from B: inside the hit area

        Check.That(rig.Holds).HasSize(1);
        Check.That(rig.Holds[0].Kind).IsEqualTo(RingHoldStartKind.Keep);
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Hybrid_OfferCloseDuringTheApproach_Releases()
    {
        // X5 (fixup 01): the offer can close inside the 150 ms approach window (a teardown); the
        // deferred run-end release must happen then, not wait for the window's expiry. Mutant:
        // drop the approach-clear branch in Receive(HideActivationButtonMessage) → no release.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: the approach starts

        rig.OfferCloses();

        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.OfferClosed);
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
    public void Hybrid_ExternalDwellHide_ReleasesAnApproachingPin()
    {
        // The F2c survivor (fixup 02): the same hide must cover the APPROACH window too — a run end
        // inside the keep zone with the eye not yet in the hit area. Until the fixup-01 release this
        // strand was invisible to both the holding and the pending tests. Same probe sequence.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.PinAt(P);
        rig.Offer(b, hitRadiusPx: 150);
        rig.RunEnds();
        rig.Frame(new PhysicalPoint(1200, 1000));                 // in the zone: the approach is active

        // The arrangement guard: the approach really was pending when the hide arrived — no hold, no
        // release yet, and the pin still up.
        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.ExternalDwell(active: true);
        rig.Frame(new PhysicalPoint(2000, 1500));                 // the gaze moves while hidden
        rig.ExternalDwell(active: false);
        rig.Frame(new PhysicalPoint(2000, 1500), steps: 10);

        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(new PhysicalPoint(2000, 1500));
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
