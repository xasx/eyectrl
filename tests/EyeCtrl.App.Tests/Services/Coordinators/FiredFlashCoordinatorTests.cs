namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Collections.Generic;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.App.Tests.Services;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// Wave C C1: which fires flash the ring, which flash the magnifier rim, and which flash nothing.
/// The coordinator only calls the two windows' display methods, so recording fakes stand in for the
/// windows; the window side (hidden ring, the step itself, the return to rest) is
/// <c>RingFiredFlashWindowTests</c>. The producers are the REAL ones wherever the rule depends on
/// what they send: the activation button VM (the offer accept), the magnifier VM (fresh entry vs
/// Back) and the keyboard VM (a key fire).
/// </summary>
public class FiredFlashCoordinatorTests
{
    private sealed class RecordingRing : ICursorCircleOverlay
    {
        public int Flashes { get; private set; }
        public void FlashFired() => Flashes++;
        public void Show() { }
        public void Hide() { }
        public void UpdatePosition(PhysicalPoint point) { }
        public void MoveTo(PhysicalPoint point, bool glide) { }
        public void UpdateAppearance(CursorSettings settings) { }
        public void SetPinned(bool pinned) { }
    }

    private sealed class RecordingMagnifier : IManualSelectionOverlay
    {
        public int RimFlashes { get; private set; }
        public void FlashRim() => RimFlashes++;
        public void Show(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void UpdateCenter(PhysicalPoint center, int radius, double magnification,
            PhysicalPoint? gazePoint = null, bool recapture = true) { }
        public void Hide() { }
        public PhysicalPoint GetRealPhysicalPoint(PhysicalPoint gazeOnScreen) => gazeOnScreen;
        public PhysicalPoint EffectiveCaptureCenter =>
            throw new System.NotImplementedException("the flash never reads the capture centre");
    }

    private sealed record Rig(
        FiredFlashCoordinator Coordinator, IMessenger Messenger, EyeCtrlSettings Settings,
        RecordingRing Ring, RecordingMagnifier Magnifier, ManualSelectionOverlayViewModel MagnifierVm);

    private static Rig Make()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var ring = new RecordingRing();
        var magnifier = new RecordingMagnifier();
        var magnifierVm = new ManualSelectionOverlayViewModel(settings, messenger);
        var ctx = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            Messenger = messenger,
            Dispatcher = Dispatcher.CurrentDispatcher,
            Settings = settings,
            ManualSelectionVM = magnifierVm,
            OverlayManager = TestFakes.MakeOverlayManager(cursorCircle: ring, manualSelection: magnifier),
        };
        var coordinator = new FiredFlashCoordinator();
        coordinator.Initialize(ctx);
        return new Rig(coordinator, messenger, settings, ring, magnifier, magnifierVm);
    }

    private static ActivationOffer Offer() =>
        new(Id: 7, Point: new PhysicalPoint(400, 300), Button: new PhysicalPoint(480, 300),
            HitRadiusPx: 40, ExpiresAtUs: long.MaxValue);

    [StaFact]
    public void ADwellFire_FlashesTheRingOnce()
    {
        // Mutation: drop the DwellActivationMessage registration → no flash → RED.
        var rig = Make();

        rig.Messenger.Send(new DwellActivationMessage());

        Check.That(rig.Ring.Flashes).IsEqualTo(1);
        Check.That(rig.Magnifier.RimFlashes).IsEqualTo(0);
    }

    [StaFact]
    public void WithTheSettingOff_NoFireFlashesAnything()
    {
        // Mutation: drop either RingFiredFlashEnabled gate → RED.
        var rig = Make();
        rig.Settings.Dwell.RingFiredFlashEnabled = false;

        rig.Messenger.Send(new DwellActivationMessage());
        rig.Messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(960, 540), 150, 2.0, new PhysicalPoint(400, 300)));

        Check.That(rig.Ring.Flashes).IsEqualTo(0);
        Check.That(rig.Magnifier.RimFlashes).IsEqualTo(0);
    }

    [StaFact]
    public void TheSetting_DefaultsToOn()
    {
        Check.That(new DwellSettings().RingFiredFlashEnabled).IsTrue();
    }

    [StaFact]
    public void AnOfferAccept_DoesNotFlashTheRing_AndTheNextFireStillDoes()
    {
        // The REAL activation button VM: its fire sends the completion and then, in the same call,
        // its DwellActivationMessage — while the ring is still on screen (the hiding state change is
        // posted). Mutation: drop the accept skip → the ring flashes on the accept → RED. The second
        // half pins that the skip is consumed, not sticky: a later ordinary fire flashes.
        var rig = Make();
        var button = new ActivationButtonViewModel(rig.Messenger);
        var cues = 0;
        rig.Messenger.Register<DwellActivationMessage>(this, (_, _) => cues++);
        var offer = Offer();

        rig.Messenger.Send(new ShowActivationButtonMessage(offer.Button, offer.HitRadiusPx, offer.Point));
        rig.Messenger.Send(new ActivationOfferFiredMessage(offer));

        Check.WithCustomMessage("The accept must still announce (the cue sound rides it).")
            .That(cues).IsEqualTo(1);
        Check.WithCustomMessage("An accepted offer must not flash the ring.")
            .That(rig.Ring.Flashes).IsEqualTo(0);

        rig.Messenger.Send(new DwellActivationMessage());
        Check.That(rig.Ring.Flashes).IsEqualTo(1);
        GC.KeepAlive(button);
    }

    [StaFact]
    public void AnAcceptWhileTheSettingIsOff_StillConsumesTheSkip_SoTheNextFireAfterTurningItOnFlashes()
    {
        // fixup-01 R3 (ring review F4): the skip is consumed BEFORE the setting gate. With the gate
        // first, an accept made while the setting is off would leave the skip set, and the first
        // ordinary fire after the user turned the setting back on would lose its flash. Mutation:
        // move the setting gate above the consume → RED.
        var rig = Make();
        var button = new ActivationButtonViewModel(rig.Messenger);
        var offer = Offer();
        rig.Settings.Dwell.RingFiredFlashEnabled = false;

        rig.Messenger.Send(new ShowActivationButtonMessage(offer.Button, offer.HitRadiusPx, offer.Point));
        rig.Messenger.Send(new ActivationOfferFiredMessage(offer));
        Check.That(rig.Ring.Flashes).IsEqualTo(0);

        rig.Settings.Dwell.RingFiredFlashEnabled = true;
        rig.Messenger.Send(new DwellActivationMessage());

        Check.WithCustomMessage("The accept's skip outlived it and ate an unrelated fire's flash.")
            .That(rig.Ring.Flashes).IsEqualTo(1);
        GC.KeepAlive(button);
    }

    [StaFact]
    public void AFreshMagnifierEntry_FlashesTheRimOnce_ButBackFromTheMenuDoesNot()
    {
        // A fresh entry (Recapture) is raised by Core only from an accepted offer; Back from the
        // action menu restores the view without a recapture. Mutation: drop the FlashRim call →
        // RED; drop the Recapture condition → the Back re-entry flashes → RED.
        var rig = Make();
        var view = new PhysicalPoint(960, 540);
        var gaze = new PhysicalPoint(400, 300);

        rig.Messenger.Send(new StartManualSelectionMessage(view, 150, 2.0, gaze));
        Check.That(rig.Magnifier.RimFlashes).IsEqualTo(1);
        Check.That(rig.Ring.Flashes).IsEqualTo(0);

        rig.Messenger.Send(new EndManualSelectionMessage());
        rig.Messenger.Send(new StartManualSelectionMessage(view, 150, 2.0, gaze, Recapture: false));

        Check.WithCustomMessage("Back from the action menu re-shows the view; it is not a new accept.")
            .That(rig.Magnifier.RimFlashes).IsEqualTo(1);
    }

    [StaFact]
    public void AKeyboardKeyFire_FlashesNothing()
    {
        // The REAL keyboard VM: a key dwell completes and the key is activated exactly as the
        // keyboard window does (KeyDwellCompleted → ActivateKey). The typing family never announces
        // a DwellActivationMessage, which is what keeps keys off the flash. Mutation: make
        // ActivateKey announce (Messenger.Send(new DwellActivationMessage())) → RED.
        var rig = Make();
        var keyboard = new KeyboardViewModel(rig.Settings, rig.Messenger);
        keyboard.Configure(keyDwellMs: 300, cooldownMs: 0);
        var fired = new List<string>();
        keyboard.KeyDwellCompleted += id =>
        {
            fired.Add(id);
            keyboard.ActivateKey(id, null);
        };
        var keys = new Dictionary<string, PhysicalPoint> { ["a"] = new PhysicalPoint(100, 100) };

        for (long t = 0; t <= 600_000; t += 50_000)
            keyboard.ProcessKeyGaze(keys, new PhysicalPoint(100, 100), true, t);

        Check.WithCustomMessage("The key dwell never completed, so the test proves nothing.")
            .That(fired).Contains("a");
        Check.That(rig.Ring.Flashes).IsEqualTo(0);
        Check.That(rig.Magnifier.RimFlashes).IsEqualTo(0);
    }

    [Fact]
    public void EventCoordinator_CreatesInitializesAndDisposesTheCoordinator()
    {
        // Source pin over comment-stripped, whitespace-collapsed code, the
        // EventCoordinatorTests.Initialize_WiresTheLostGazeExit precedent: no test runs
        // EventCoordinator.Initialize (it needs the whole window/VM graph). Mutation: drop the
        // construction → the flash never reaches production → RED.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "EventCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("EventCoordinator.Initialize no longer creates and initializes the fired flash.")
            .That(source.Contains("_firedFlash=newFiredFlashCoordinator();_firedFlash.Initialize(context);")).IsTrue();
        Check.WithCustomMessage("EventCoordinator.Dispose no longer disposes the fired flash.")
            .That(source.Contains("_firedFlash?.Dispose();")).IsTrue();
    }

    [StaFact]
    public void AfterDispose_NothingFlashes()
    {
        var rig = Make();
        rig.Coordinator.Dispose();

        rig.Messenger.Send(new DwellActivationMessage());
        rig.Messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(960, 540), 150, 2.0, new PhysicalPoint(400, 300)));

        Check.That(rig.Ring.Flashes).IsEqualTo(0);
        Check.That(rig.Magnifier.RimFlashes).IsEqualTo(0);
    }
}
