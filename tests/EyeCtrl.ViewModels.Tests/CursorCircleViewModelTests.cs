namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class CursorCircleViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();

    [Fact]
    public void UpdatesPosition_OnGazeCursorMovedMessage()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 0));

        Check.That(vm.Position.X).IsEqualTo(100);
        Check.That(vm.Position.Y).IsEqualTo(200);
    }

    [Fact]
    public void ReadsAppearance_FromSettings()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleSize = 50;
        settings.Cursor.CircleColor = "#FF0000";
        settings.Cursor.CircleOpacity = 0.8;
        var vm = new CursorCircleViewModel(settings, _messenger);

        Check.That(vm.Size).IsEqualTo(50);
        Check.That(vm.CircleColor).IsEqualTo("#FF0000");
        Check.That(vm.Opacity).IsEqualTo(0.8);
    }

    [Fact]
    public void Paused_KeepsRingVisible_AndFlagsPaused()
    {
        // Paused-trap guard: the ring is the user's evidence that tracking is alive while they look
        // for the resume target. It stays visible (the View dims it) and IsPaused drives the dim.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPaused).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPaused).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPaused).IsFalse();
    }

    // ── The pin (ticket 4): the ring stops on the onset centroid, and the three looks' transitions ──

    [Fact]
    public void Pin_HoldsTheOnsetCentroid_NotTheCurrentOrFirstSample()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        // A sample arrives before the onset; the pin message then carries ticket 3's onset centroid,
        // deliberately different from both that sample and the samples that follow.
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 0));
        var onsetCentroid = new PhysicalPoint(140, 120);
        _messenger.Send(new ShowActivationButtonMessage(onsetCentroid, 75));

        Check.That(vm.IsPinned).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);
    }

    [Fact]
    public void Pinned_StopsFollowingGaze_AndFreeLookFollowsAgainAfterTheFixationEnds()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(140, 120), 75));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(95, 105), 0));

        // Literally still: samples inside the hold must not nudge the ring.
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);

        _messenger.Send(new HideActivationButtonMessage());
        Check.That(vm.IsPinned).IsFalse();

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));
        Check.That(vm.Position.X).IsEqualTo(300);
        Check.That(vm.Position.Y).IsEqualTo(400);
    }

    [Fact]
    public void PausedWhilePinned_Unpins_Dims_AndFollowsGaze()
    {
        // The transition the pause path must not break: the ring is pinned, the user pauses. Paused
        // keeps the ring (dimmed, ticket 1) but it must no longer be pinned — the pin means a hold is
        // being measured, and nothing is measured while paused.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(140, 120), 75));
        Check.That(vm.IsPinned).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));

        Check.That(vm.IsPinned).IsFalse();
        Check.That(vm.IsPaused).IsTrue();
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));
        Check.That(vm.Position.X).IsEqualTo(300);
        Check.That(vm.Position.Y).IsEqualTo(400);
    }

    [Fact]
    public void HiddenRing_DoesNotFollowGaze_EvenUnpinned()
    {
        // Visibility still owns the position gate: an overlay state hides the ring and later samples
        // must not teleport it while hidden.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(140, 120), 0));
        _messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Position.X).IsEqualTo(140);
    }

    [Theory]
    [InlineData(InteractionState.ActionMenuVisible)]
    [InlineData(InteractionState.ManualSelectionActive)]
    [InlineData(InteractionState.ManualSelectionActionMenuVisible)]
    public void HidesDuringOverlayStates(InteractionState state)
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(state));

        Check.That(vm.IsVisible).IsFalse();
    }
    [Fact]
    public void Hides_DuringRepeatMode()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.RepeatMode));

        // The contracting repeat cursor replaces the normal circle while the mode runs (D27).
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void ShowsAgain_WhenReturningToFreeLook_FromActionMenu()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExternalDwellActive_HidesCursor_EvenInFreeLook()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();

        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void ExternalDwell_HidesRing_EvenWhilePaused_AndRestoresIt()
    {
        // Combined-owner check: external-dwell still wins while paused (the cooperating app's own
        // progress replaces the ring), and clearing it restores the paused ring rather than leaving
        // the user with no cursor feedback at all.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsTrue();
    }
}
