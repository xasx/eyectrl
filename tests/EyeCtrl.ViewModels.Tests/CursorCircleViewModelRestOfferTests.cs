namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Rest activation R1: for a shown offer whose style is <see cref="OfferPlacementStyle.AtRestPoint"/>
/// the ring's hybrid hold stands aside — no keep, approach or freeze hold, no go window, and an
/// immediate release — whatever <see cref="RingHoldPolicy"/> the launch chose. The messages arrive in
/// the order Core's offer-following pin raises them under RestFill. R = 80, H = 120, frames 30 ms.
/// </summary>
public class CursorCircleViewModelRestOfferTests
{
    private const long FrameUs = 30_000;
    private const double R = 80;
    private const double H = 120;
    private static readonly PhysicalPoint P = new(1000, 1000);

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public CursorCircleViewModel Vm { get; }
        public List<RingHoldStartEvent> Holds { get; } = new();
        public List<RingGoStartEvent> GoStarts { get; } = new();
        private long _us;

        public Rig(RingHoldPolicy policy)
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = R;
            Vm = new CursorCircleViewModel(settings, Messenger) { RingHoldPolicy = policy };
            Vm.RingHoldStarted += Holds.Add;
            Vm.RingGoStarted += GoStarts.Add;
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
        public void OfferCloses() => Messenger.Send(new HideActivationButtonMessage());

        public void RestOffer(PhysicalPoint p) => Messenger.Send(
            new ShowActivationButtonMessage(p, H, p, R, OfferPlacementStyle.AtRestPoint));

        public void ButtonOffer(PhysicalPoint b, PhysicalPoint p) => Messenger.Send(
            new ShowActivationButtonMessage(b, 150, p, R));

        /// <summary>The rest offer as Core raises it at the settle: the show, then the pin at P.</summary>
        public void RestAt(PhysicalPoint p)
        {
            Frame(p);
            State(InteractionState.Fixation);
            RestOffer(p);
            Pin(p);
            Frame(p, steps: 10);
        }
    }

    [Theory]
    [InlineData(RingHoldPolicy.None)]
    [InlineData(RingHoldPolicy.HybridFreeze)]
    public void ARestOffer_StartsNoHoldAndNoGoWindow_AndItsCloseReleasesAtOnce(RingHoldPolicy policy)
    {
        // R1-M9 (the hybrid hold runs for rest offers): the gaze inside H freezes a hold, and the run
        // end's release is deferred to the next sample.
        var rig = new Rig(policy);
        rig.RestAt(P);
        Check.That(rig.Vm.IsPinned).IsTrue();
        Check.That(rig.Vm.Position).IsEqualTo(P);

        // The run flips while the user rests (Core keeps the pin: no release while the offer lives).
        rig.State(InteractionState.FreeLook);
        rig.Frame(new PhysicalPoint(P.X + 60, P.Y), steps: 5);
        rig.State(InteractionState.Fixation);
        rig.Frame(P, steps: 5);

        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.GoStarts).IsEmpty();
        Check.That(rig.Vm.Position).IsEqualTo(P);                  // pinned, not following

        // The user looks away: the offer expires, Core hides it and releases the ring.
        rig.State(InteractionState.FreeLook);
        var away = new PhysicalPoint(P.X + 400, P.Y + 300);
        rig.Frame(away);
        rig.OfferCloses();
        rig.Release();

        Check.That(rig.Vm.IsPinned).IsFalse();                     // at once, not on the next sample
        Check.That(rig.Vm.Position).IsEqualTo(away);
        Check.That(rig.Holds).IsEmpty();
        Check.That(rig.GoStarts).IsEmpty();
    }

    [Fact]
    public void AButtonOfferAfterARestOffer_GetsTheHybridBack()
    {
        var rig = new Rig(RingHoldPolicy.HybridFreeze);
        rig.RestAt(P);
        rig.State(InteractionState.FreeLook);
        rig.OfferCloses();
        rig.Release();

        var b = new PhysicalPoint(1400, 1000);
        rig.Frame(new PhysicalPoint(700, 700));
        rig.ButtonOffer(b, new PhysicalPoint(1100, 1000));
        rig.Frame(b);                                               // the eye lands on B: the freeze

        Check.That(rig.Holds).HasSize(1);
        Check.That(rig.Holds[0].Kind).IsEqualTo(RingHoldStartKind.Freeze);
    }

    [Fact]
    public void ARestShowOverAButtonOffersOwedRelease_ReleasesBeforeTheRestPin()
    {
        // A style switch: the button offer's run ended (its release is deferred by the hybrid) and
        // the next show is a rest offer. The owed release happens on the show, not never.
        var rig = new Rig(RingHoldPolicy.HybridFreeze);
        var b = new PhysicalPoint(1400, 1000);
        rig.Frame(P);
        rig.State(InteractionState.Fixation);
        rig.ButtonOffer(b, P);
        rig.Pin(P);
        rig.Frame(P, steps: 10);
        rig.OfferCloses();
        rig.Release();                                              // deferred under the hybrid
        Check.That(rig.Vm.IsPinned).IsTrue();

        rig.RestOffer(new PhysicalPoint(2000, 1500));

        Check.That(rig.Vm.IsPinned).IsFalse();
    }
}
