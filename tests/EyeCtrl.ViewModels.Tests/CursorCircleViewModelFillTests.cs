namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Rest activation R2: the ring's fill read-out. <see cref="CursorCircleViewModel.FillProgress"/>
/// follows the offer engine's <c>ButtonDwellProgress</c> only while the ring is pinned to a live
/// rest-style offer, and <see cref="CursorCircleViewModel.IsFillPaused"/> is derived from the
/// frames: a progress that stops rising with the gaze outside the rest area H is the grace. R = 80,
/// H = 120, frames 30 ms.
/// </summary>
public class CursorCircleViewModelFillTests
{
    private const long FrameUs = 30_000;
    private const double R = 80;
    private const double H = 120;
    private static readonly PhysicalPoint P = new(1000, 1000);

    private sealed class Rig
    {
        public WeakReferenceMessenger Messenger { get; } = new();
        public CursorCircleViewModel Vm { get; }
        private long _us;

        public Rig()
        {
            var settings = new EyeCtrlSettings();
            settings.Dwell.FixationRadiusPixels = R;
            Vm = new CursorCircleViewModel(settings, Messenger);
        }

        public void Frame(PhysicalPoint position, double progress = 0, bool gap = false)
        {
            _us += FrameUs;
            Messenger.Send(new GazeCursorMovedMessage(
                new GazeFrame(position, _us, gap, 0, progress)));
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
            Frame(p);
        }
    }

    [Fact]
    public void ARestOffersFill_FollowsTheFrames()
    {
        var rig = new Rig();
        rig.RestAt(P);

        rig.Frame(P, 0.25);
        Check.That(rig.Vm.FillProgress).IsCloseTo(0.25, 1e-12);
        Check.That(rig.Vm.IsFillPaused).IsFalse();

        rig.Frame(P, 0.5);
        Check.That(rig.Vm.FillProgress).IsCloseTo(0.5, 1e-12);

        rig.Frame(P, 1.0);
        Check.That(rig.Vm.FillProgress).IsCloseTo(1.0, 1e-12);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }

    [Fact]
    public void AButtonOffer_NeverWritesTheFill()
    {
        // R2-M4: without the style gate, a button offer's dwell progress paints the ring's fill too.
        var rig = new Rig();
        var b = new PhysicalPoint(1400, 1000);
        rig.Frame(P);
        rig.State(InteractionState.Fixation);
        rig.ButtonOffer(b, P);
        rig.Pin(P);

        rig.Frame(b, 0.4);
        rig.Frame(b, 0.8);

        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }

    [Fact]
    public void TheFill_IsZeroBeforeTheRestOfferIsPinned()
    {
        var rig = new Rig();
        rig.Frame(P);
        rig.State(InteractionState.Fixation);
        rig.RestOffer(P);                    // shown, no pin yet (Core raises the pin next)

        rig.Frame(P, 0.4);
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void TheFill_IsClearedByTheOffersHide_Release_AStateHidingTheRing_AndANewShow()
    {
        // R2-M7: FillProgress not reset on hide leaves the previous arc drawn over the next state.
        var rig = new Rig();
        rig.RestAt(P);

        rig.Frame(P, 0.6);
        Check.That(rig.Vm.FillProgress).IsCloseTo(0.6, 1e-12);

        rig.OfferCloses();
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
        Check.That(rig.Vm.IsFillPaused).IsFalse();

        // A fresh offer, then a release.
        rig.State(InteractionState.Fixation);
        rig.RestOffer(P);
        rig.Pin(P);
        rig.Frame(P, 0.3);
        Check.That(rig.Vm.FillProgress).IsCloseTo(0.3, 1e-12);
        rig.Release();
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);

        // A fresh offer, then a hiding state.
        rig.State(InteractionState.Fixation);
        rig.RestOffer(P);
        rig.Pin(P);
        rig.Frame(P, 0.3);
        rig.State(InteractionState.Paused);
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
        Check.That(rig.Vm.IsFillPaused).IsFalse();

        // A fresh offer, then a NEW show: the arc must not carry the previous offer's sweep.
        rig.State(InteractionState.Fixation);
        rig.RestOffer(P);
        rig.Pin(P);
        rig.Frame(P, 0.3);
        rig.RestOffer(P);
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
    }

    [Fact]
    public void AnExternalDwellHidingTheRing_AlsoClearsTheFill()
    {
        var rig = new Rig();
        rig.RestAt(P);
        rig.Frame(P, 0.5);

        rig.Messenger.Send(new ExternalDwellActiveMessage(true));

        Check.That(rig.Vm.IsVisible).IsFalse();
        Check.That(rig.Vm.FillProgress).IsEqualTo(0.0);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }

    [Fact]
    public void AGapFrame_KeepsTheLastValueAndThePausedLook()
    {
        var rig = new Rig();
        rig.RestAt(P);
        rig.Frame(P, 0.4);

        rig.Frame(P, 0, gap: true);

        Check.That(rig.Vm.FillProgress).IsCloseTo(0.4, 1e-12);

        // A gap carries no evidence of an excursion either: it must not start the pause clock.
        rig.Frame(P, 0.4, gap: true);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }

    [Fact]
    public void AnExcursion_PausesTheFill_AndTheReturnClearsIt()
    {
        // The engine credits only samples inside H; the progress stands still while the gaze is in
        // the grace. Two still, out-of-area frames are the pause; a rising in-area frame ends it.
        var rig = new Rig();
        rig.RestAt(P);
        rig.Frame(P, 0.4);

        var outside = new PhysicalPoint(P.X + H + 15, P.Y);
        rig.Frame(outside, 0.4);
        Check.That(rig.Vm.IsFillPaused).IsFalse();      // one still frame is not evidence yet

        rig.Frame(outside, 0.4);
        Check.That(rig.Vm.IsFillPaused).IsTrue();

        rig.Frame(outside, 0.4);
        Check.That(rig.Vm.IsFillPaused).IsTrue();

        rig.Frame(P, 0.55);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
        Check.That(rig.Vm.FillProgress).IsCloseTo(0.55, 1e-12);
    }

    [Fact]
    public void AStillProgressInsideTheRestArea_IsNotAPause()
    {
        // A duplicate clock tick inside H (no credited time) is not the grace.
        var rig = new Rig();
        rig.RestAt(P);
        rig.Frame(P, 0.4);
        rig.Frame(P, 0.4);
        rig.Frame(P, 0.4);

        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }

    [Fact]
    public void AnExcursionAfterTheFiredOne_IsStillNotAPause()
    {
        // Progress 1.0 is the fired offer waiting for accept/decline; it never reads as paused.
        var rig = new Rig();
        rig.RestAt(P);
        rig.Frame(P, 1.0);

        var outside = new PhysicalPoint(P.X, P.Y + H + 40);
        rig.Frame(outside, 1.0);
        rig.Frame(outside, 1.0);

        Check.That(rig.Vm.FillProgress).IsCloseTo(1.0, 1e-12);
        Check.That(rig.Vm.IsFillPaused).IsFalse();
    }
}
