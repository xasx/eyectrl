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
        public List<RingGoStartEvent> GoStarts { get; } = new();
        public List<RingGoEndEvent> GoEnds { get; } = new();

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
            Vm.RingGoStarted += e => GoStarts.Add(e);
            Vm.RingGoEnded += e => GoEnds.Add(e);
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
        // (a settle during the keep ends it and pins: pinned by Go_G9 below)

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
    public void Hybrid_ApproachWindow_LeavingTheZoneOrExpiring_Releases()
    {
        // The approach is a T = 500 ms window: leaving the going-for-it space (here the no-move zone
        // only, the show message carries no P), or running the window out without entering the hit
        // area, performs the deferred run-end release. Mutant: ignore the window (release immediately,
        // or never expire) → the corresponding half reds.
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

        // Staying inside the zone does not extend the window past 500 ms.
        var rig2 = new Rig();
        rig2.PinAt(P);
        rig2.Offer(b, hitRadiusPx: 150);
        rig2.RunEnds();
        rig2.Frame(new PhysicalPoint(1200, 1000));                // t0: the approach starts
        rig2.Frame(new PhysicalPoint(1200, 1000), steps: 16);     // t0 + 480 ms: still kept
        Check.That(rig2.Releases).IsEmpty();
        Check.That(rig2.Vm.IsPinned).IsTrue();

        rig2.Frame(new PhysicalPoint(1200, 1000));                // t0 + 510 ms: the window ran out

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

    // ── The going-for-it keep (T = 500 ms, the P-B corridor of half-width R) ───────────────────────
    // P = (1000,1000), B = (1400,1000): 400 px apart on a horizontal axis; hit 150, R 80 (zone 230).

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
    public void Go_G1_AGazeInTheCorridorOutsideTheZone_KeepsThePin_ThenTheHitEntryHolds()
    {
        // G1: the run ends with the gaze 40 px from P toward B (360 px from B: outside the 230-px
        // zone, so today it released at once). The pin stays; the hit entry starts the hold with the
        // ring never having left P. Mutant M1: the zone only (no capsule) -> released at once.
        var rig = GoRig();

        rig.Frame(new PhysicalPoint(1040, 1000));

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
        Check.That(rig.GoStarts).HasSize(1);
        Check.That(rig.GoStarts[0].Dist).IsCloseTo(40.0, 1e-9);   // the ring (at P) to the gaze
        Check.That(rig.GoStarts[0].Off!.Value).IsCloseTo(0.0, 1e-9);   // on the axis

        rig.Frame(new PhysicalPoint(1300, 1000));                 // 100 px from B: the hit area

        Check.That(rig.Holds).HasSize(1);
        Check.That(rig.Holds[0].Kind).IsEqualTo(RingHoldStartKind.Approach);
        Check.That(rig.GoEnds).HasSize(1);
        Check.That(rig.GoEnds[0].Reason).IsEqualTo(RingGoEndReason.Hit);
        Check.That(rig.Vm.Position).IsEqualTo(P);
        Check.That(rig.Releases).IsEmpty();
    }

    [Fact]
    public void Go_G2_FourHundredMsInTheCorridor_ThenTheHit_TheRingIsStillAtP()
    {
        // G2: the eye lingers 390 ms in the corridor (today's window was 150 ms), then enters the hit
        // area: the ring is still at P. Mutant M2: T back to 150 ms -> released before the hit.
        var rig = GoRig();
        rig.Frame(new PhysicalPoint(1100, 1010), steps: 14);      // t0 .. t0 + 390 ms

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);

        rig.Frame(new PhysicalPoint(1310, 1000));                 // the hit area

        Check.That(rig.Holds.Select(h => h.Kind)).ContainsExactly(RingHoldStartKind.Approach);
        Check.That(rig.Vm.Position).IsEqualTo(P);
        Check.That(rig.Releases).IsEmpty();
    }

    [Fact]
    public void Go_G3_LeavingTheCorridorSideways_ReleasesOnThatFrame()
    {
        // G3: a lateral of 100 px (> R 80, and 316 px from B: outside the zone too) releases on that
        // very frame with the ring drawn at the gaze. Mutant M3: only T ends the keep. Mutant M7: the
        // capsule half-width is the hit radius (150) -> a 100-px lateral still keeps.
        var rig = GoRig();
        rig.Frame(new PhysicalPoint(1040, 1000));
        Check.That(rig.Releases).IsEmpty();

        var sideways = new PhysicalPoint(1100, 1100);
        rig.Frame(sideways);

        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Releases[0].Cause).IsEqualTo(RingReleaseCause.RunEnd);
        Check.That(rig.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Left);
        Check.That(rig.GoEnds[0].Off!.Value).IsCloseTo(100.0, 1e-9);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(sideways);
    }

    [Fact]
    public void Go_G4_TheTimeLimitBoundary_LastFrameBeforeTKeeps_TheFrameAtTReleases()
    {
        // G4: with the first frame at t0 (the keep begins), a frame at t0 + 499.999 ms still keeps and
        // one at exactly t0 + 500 ms releases (a Timeout). Mutant M4: `>` instead of `>=` at T.
        foreach (var (gapUs, releases) in new[] { (469_999L, false), (470_000L, true) })
        {
            var rig = GoRig();
            rig.Frame(new PhysicalPoint(1100, 1000));             // t0
            rig.Gap(gapUs);                                       // the next frame lands at t0 + gap + 30 ms
            rig.Frame(new PhysicalPoint(1100, 1000));

            Check.WithCustomMessage($"gap {gapUs} us").That(rig.Releases.Count).IsEqualTo(releases ? 1 : 0);
            Check.WithCustomMessage($"gap {gapUs} us").That(rig.Vm.IsPinned).IsEqualTo(!releases);
            if (releases)
            {
                Check.That(rig.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Timeout);
                Check.That(rig.GoEnds[0].Ms).IsCloseTo(500.0, 1e-6);
            }
        }
    }

    [Fact]
    public void Go_G5_ALookBehindP_AlongTheAxis_ReleasesAtOnce_TheSegmentNotTheInfiniteLine()
    {
        // G5: 200 px behind P on the P-B axis is 200 px from the segment (> R 80): released. An infinite
        // line would call it on the way. Mutant M5: the infinite line instead of the segment. Both the
        // first frame after the run end (step a) and a frame during the keep (step b) are checked.
        var behind = new PhysicalPoint(800, 1000);

        var atStart = GoRig();
        atStart.Frame(behind);
        Check.That(atStart.GoStarts).IsEmpty();
        Check.That(atStart.Releases).HasSize(1);
        Check.That(atStart.Vm.Position).IsEqualTo(behind);

        var during = GoRig();
        during.Frame(new PhysicalPoint(1040, 1000));
        during.Frame(behind);
        Check.That(during.Releases).HasSize(1);
        Check.That(during.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Left);
        Check.That(during.Vm.Position).IsEqualTo(behind);
    }

    [Fact]
    public void Go_G6_TheCorridorUsesTheMessagesR_NotTheSetting()
    {
        // G6: the corridor's half-width is the engine's R from the show message (settings say 80).
        // R 105 keeps a 100-px lateral (80 would release); R 40 releases a 60-px lateral (80 would keep).
        // Mutant M6: the setting's R instead of the message's.
        var wide = GoRig(fixationRadiusPx: 105);
        wide.Frame(new PhysicalPoint(1040, 1000));
        wide.Frame(new PhysicalPoint(1100, 1100));                // 100 px lateral; 316 px from B (zone 255)
        Check.That(wide.Releases).IsEmpty();
        Check.That(wide.Vm.IsPinned).IsTrue();

        var narrow = GoRig(fixationRadiusPx: 40);
        narrow.Frame(new PhysicalPoint(1040, 1000));
        narrow.Frame(new PhysicalPoint(1100, 1060));              // 60 px lateral; 341 px from B
        Check.That(narrow.Releases).HasSize(1);
        Check.That(narrow.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Left);
    }

    [Fact]
    public void Go_G7_WithoutTheOffersPoint_TodaysZoneOnlyBehaviourApplies()
    {
        // G7: a show message without P has no corridor: a gaze 40 px from P (360 px from B, outside the
        // zone) releases at once, a gaze inside the zone keeps (and the log's off is unknown).
        var outside = GoRig(withP: false);
        outside.Frame(new PhysicalPoint(1040, 1000));
        Check.That(outside.Releases).HasSize(1);
        Check.That(outside.GoStarts).IsEmpty();

        var inside = GoRig(withP: false);
        inside.Frame(new PhysicalPoint(1200, 1000));
        Check.That(inside.Releases).IsEmpty();
        Check.That(inside.GoStarts).HasSize(1);
        Check.That(inside.GoStarts[0].Off).IsNull();
    }

    [Fact]
    public void Go_G8_TheOfferClosingOrAHidingState_DuringTheKeep_EndsIt()
    {
        // G8: Hide during the keep -> an offer-closed go end and release; a hiding state -> the hidden
        // path (the pin cleared, the ring away). Mutant M8: hide does not end the keep.
        var closed = GoRig();
        closed.Frame(new PhysicalPoint(1040, 1000), steps: 2);
        closed.OfferCloses();
        Check.That(closed.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.OfferClosed);
        Check.That(closed.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.OfferClosed);
        Check.That(closed.Vm.IsPinned).IsFalse();

        var hidden = GoRig();
        hidden.Frame(new PhysicalPoint(1040, 1000), steps: 2);
        hidden.State(InteractionState.Paused);
        Check.That(hidden.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Hidden);
        Check.That(hidden.Vm.IsPinned).IsFalse();
        Check.That(hidden.Vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Go_G9_ASettleDuringTheKeep_EndsIt_AndPinsAsWithoutAKeep()
    {
        // G9 (fixup 01, review F1): the detector's settle needs >= 500 ms, so it reaches the ring on the
        // keep's time-limit frame, while the keep is still active. It ENDS the keep (reason settled; the
        // eye has stopped) and is then processed as without a keep: it pins and glides, so the next
        // offer's keep can start from a pinned ring. Mutant MF1: the pin is still refused during the
        // keep. Mutant MF1a: the pin is accepted but the keep is not ended. Mutant MF1b: the go-end
        // reason is not `settled`.
        var rig = GoRig();
        rig.Frame(new PhysicalPoint(1100, 1000), steps: 17);      // t0 .. t0 + 480 ms: the keep runs
        rig.State(InteractionState.Fixation);                     // the new run, anchored at the breakout

        var settle = new PhysicalPoint(1100, 1000);               // 300 px from B: outside the zone
        rig.Pin(settle);

        Check.That(rig.Pins[^1].Applied).IsTrue();
        Check.That(rig.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Settled);
        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.Frame(settle, steps: 12);                             // the glide lands on the settle
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(settle);
        Check.That(rig.GoEnds).HasSize(1);                        // the keep did not end a second time
    }

    [Fact]
    public void Go_G9b_ANearMissSettleDuringTheKeep_EndsIt_ThenIsRefused_AndTheRingFollows()
    {
        // The settle that ends the keep is a near miss of the live offer (D refuses it): the keep ends
        // as settled and the owed run-end release happens, so the ring follows the gaze instead of
        // staying pinned at P with no keep. Mutant MF1c: no release after the refused pin.
        var rig = GoRig();
        var gaze = new PhysicalPoint(1400, 1190);                 // 190 px from B, near miss
        rig.Frame(gaze, steps: 17);
        rig.State(InteractionState.Fixation);

        rig.Pin(gaze);

        Check.That(rig.Pins[^1].Applied).IsFalse();
        Check.That(rig.Pins[^1].IgnoredNearOffer).IsTrue();
        Check.That(rig.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Settled);
        Check.That(rig.Releases.Select(r => r.Cause)).ContainsExactly(RingReleaseCause.RunEnd);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(gaze);
    }

    [Fact]
    public void Go_G9c_APinWithNoFixationUp_DoesNotEndTheKeep()
    {
        // A pin refused for the state alone (no Fixation reported) is not a settle of a new run: the
        // keep goes on.
        var rig = GoRig();
        rig.Frame(new PhysicalPoint(1100, 1000), steps: 3);

        rig.Pin(new PhysicalPoint(1100, 1000));                   // the machine is in FreeLook

        Check.That(rig.Pins[^1].Applied).IsFalse();
        Check.That(rig.GoEnds).IsEmpty();
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);
    }

    [Fact]
    public void Go_G10_ANearMissPark_TheKeepEndsByT_ThenTheRingFollows()
    {
        // G10: the eye parks 190 px from B (a near miss, inside the zone): the keep holds the ring at P
        // for T, then ends with a Timeout and the ring follows the gaze (no stuck path).
        var rig = GoRig();
        var park = new PhysicalPoint(1400, 1190);
        rig.Frame(park, steps: 17);                               // t0 .. t0 + 480 ms

        Check.That(rig.Releases).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);

        rig.Frame(park);                                          // t0 + 510 ms

        Check.That(rig.GoEnds.Select(e => e.Reason)).ContainsExactly(RingGoEndReason.Timeout);
        Check.That(rig.Releases).HasSize(1);
        Check.That(rig.Vm.IsPinned).IsFalse();
        Check.That(rig.Vm.Position).IsEqualTo(park);

        var next = new PhysicalPoint(1410, 1200);
        rig.Frame(next);
        Check.That(rig.Vm.Position).IsEqualTo(next);              // following
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
