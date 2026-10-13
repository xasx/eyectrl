namespace EyeCtrl.ViewModels.Tests;

using System.ComponentModel;
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
    public void Paused_HidesRing_AndFlagsPaused()
    {
        // Paused hides the ring (owner ruling 2026-10-07, superseding the visible-dim look of
        // ticket 1): the PauseIndicator strip is the pause feedback and the edge-summon dwell stays
        // active while paused, so resume needs no cursor. IsPaused still flags the state and drives
        // the View's dim as belt and braces.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPaused).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.IsPaused).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPaused).IsFalse();
    }

    // ── The pin (ticket 7): the ring glides to the settle centroid and stops there ──
    //
    // The pin's lifetime is Core's: PinRingMessage on an accepted settle, ReleaseRingMessage on every
    // exit from Fixation. The activation button's show/hide are NOT part of it (it shows at onset and
    // its lifetime is its own), which is what keeps the ring following gaze while the button lingers.

    /// <summary>Sends the gaze frames a running glide needs: one per step, starting the clock on the first.</summary>
    private static void SendFrame(WeakReferenceMessenger messenger, PhysicalPoint position, long timestampUs) =>
        messenger.Send(new GazeCursorMovedMessage(position, timestampUs));

    [Fact]
    public void Pin_GlidesToTheSettleCentroid_NotTheCurrentOrFirstSample()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        // A sample arrives before the pin; the pin message then carries Core's settle centroid,
        // deliberately different from both that sample and the samples that follow.
        SendFrame(_messenger, new PhysicalPoint(100, 100), 0);
        var settleCentroid = new PhysicalPoint(140, 120);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(settleCentroid));

        Check.That(vm.IsPinned).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(100);   // no teleport: the glide starts from where it was

        SendFrame(_messenger, new PhysicalPoint(101, 100), 30_000);   // starts the clock
        SendFrame(_messenger, new PhysicalPoint(105, 100), 150_000);  // 120 ms elapsed: landed

        Check.That(vm.IsPinned).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);
    }

    [Fact]
    public void Pinned_StopsFollowingGaze_AndFreeLookFollowsAgainAfterTheFixationEnds()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        SendFrame(_messenger, new PhysicalPoint(100, 100), 0);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        SendFrame(_messenger, new PhysicalPoint(140, 120), 30_000);
        SendFrame(_messenger, new PhysicalPoint(140, 120), 200_000);  // glide landed

        SendFrame(_messenger, new PhysicalPoint(300, 400), 230_000);
        SendFrame(_messenger, new PhysicalPoint(95, 105), 260_000);

        // Literally still: samples inside the hold must not nudge the ring.
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);

        _messenger.Send(new ReleaseRingMessage());
        Check.That(vm.IsPinned).IsFalse();

        SendFrame(_messenger, new PhysicalPoint(300, 400), 290_000);
        Check.That(vm.Position.X).IsEqualTo(300);
        Check.That(vm.Position.Y).IsEqualTo(400);
    }

    [Fact]
    public void Glide_EasesMonotonicallyOver120ms_AndEndsExactlyOnThePin()
    {
        // Mutation: set the drawn position directly on the pin (drop the interpolation, apply the pin
        // in Receive(PinRingMessage)) → no MoveTo frame is ever raised and the first assertion reds.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        var start = new PhysicalPoint(100, 100);
        var pin = new PhysicalPoint(300, 200);

        SendFrame(_messenger, start, 0);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(pin));
        SendFrame(_messenger, new PhysicalPoint(101, 100), 0);   // first frame: starts the glide at `start`
        Check.That(vm.MoveTo.Glide).IsTrue();
        Check.That(vm.MoveTo.Target).IsEqualTo(start);
        Check.That(vm.Position).IsEqualTo(start);                // not at the pin yet

        var drawnX = new List<double>();
        foreach (long ts in new[] { 30_000L, 60_000L, 90_000L, 119_000L })
        {
            SendFrame(_messenger, new PhysicalPoint(102, 100), ts);
            drawnX.Add(vm.MoveTo.Target.X);
        }

        for (int i = 0; i < drawnX.Count; i++)
        {
            Check.WithCustomMessage($"frame {i} at {drawnX[i]} must be strictly inside (100, 300)")
                 .That(drawnX[i]).IsStrictlyGreaterThan(100);
            Check.WithCustomMessage($"frame {i} at {drawnX[i]} must be strictly inside (100, 300)")
                 .That(drawnX[i]).IsStrictlyLessThan(300);
        }
        for (int i = 1; i < drawnX.Count; i++)
            Check.WithCustomMessage($"frame {i}: {drawnX[i]} must advance past {drawnX[i - 1]}")
                 .That(drawnX[i]).IsStrictlyGreaterThan(drawnX[i - 1]);

        SendFrame(_messenger, new PhysicalPoint(103, 100), 120_000);   // the clock reaches the duration
        Check.That(vm.Position).IsEqualTo(pin);                        // ends EXACTLY on the pin
        Check.That(vm.IsPinned).IsTrue();
    }

    [Fact]
    public void SnapOut_OnRelease_TheDrawnPositionIsTheGazeOnTheNextSample()
    {
        // Mutation: glide on release (keep easing toward the old pin after the release) → the release
        // leaves the ring at the midpoint and the next sample's assertion reds.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        SendFrame(_messenger, new PhysicalPoint(100, 100), 0);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(300, 200)));
        SendFrame(_messenger, new PhysicalPoint(101, 100), 30_000);
        SendFrame(_messenger, new PhysicalPoint(102, 100), 60_000);   // mid-glide

        _messenger.Send(new ReleaseRingMessage());
        Check.That(vm.IsPinned).IsFalse();

        SendFrame(_messenger, new PhysicalPoint(900, 700), 90_000);
        Check.That(vm.Position.X).IsEqualTo(900);   // jumped straight to gaze, no sweep
        Check.That(vm.Position.Y).IsEqualTo(700);
    }

    [Fact]
    public void CancelledGlide_ReleaseMidGlide_SnapsToGaze_NothingFreezes()
    {
        // Mutation: keep interpolating to the old pin after a release → the frame after the release
        // still advances toward the pin and no frame ever reaches the live gaze.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        SendFrame(_messenger, new PhysicalPoint(100, 100), 0);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(300, 200)));
        SendFrame(_messenger, new PhysicalPoint(105, 100), 30_000);
        SendFrame(_messenger, new PhysicalPoint(110, 100), 60_000);   // mid-glide

        var lastGlideFrame = vm.MoveTo;
        _messenger.Send(new ReleaseRingMessage());

        SendFrame(_messenger, new PhysicalPoint(500, 500), 200_000);
        SendFrame(_messenger, new PhysicalPoint(600, 600), 230_000);
        Check.That(vm.Position.X).IsEqualTo(600);   // following gaze again, not the stale pin
        Check.That(vm.Position.Y).IsEqualTo(600);
        // Nothing kept interpolating: the release wrote no further glide frame.
        Check.That(vm.MoveTo).IsEqualTo(lastGlideFrame);
    }

    [Fact]
    public void Saccade_WithNoPinEdge_IsFollowedWithinOneSample()
    {
        // Mutation: glide on any large jump (drive the glide from the jump size, not the pin edge) →
        // the 400 px FreeLook jump becomes a glide frame and Position stays behind, red.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        SendFrame(_messenger, new PhysicalPoint(100, 100), 0);
        SendFrame(_messenger, new PhysicalPoint(500, 100), 30_000);

        Check.That(vm.Position.X).IsEqualTo(500);   // followed in one sample
        Check.That(vm.MoveTo.Glide).IsFalse();      // and not as a glide
        Check.That(vm.IsPinned).IsFalse();
    }

    [Fact]
    public void ActivationButtonShowAndHide_DoNotPinOrUnpinTheRing()
    {
        // Mutation: re-point the ring at the button's show/hide (the old contract) → the show pins
        // at onset, red on the first assertion.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(140, 120), 75));
        Check.That(vm.IsPinned).IsFalse();          // onset is not the pin edge
        Check.That(vm.Position).IsEqualTo(new PhysicalPoint(0, 0));

        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        Check.That(vm.IsPinned).IsTrue();

        _messenger.Send(new HideActivationButtonMessage());
        Check.That(vm.IsPinned).IsTrue();           // the button's hide is not the release
    }

    [Fact]
    public void PausedWhilePinned_Unpins_Hides_AndStopsFollowingGaze()
    {
        // The transition the pause path must not break: the ring is pinned, the user pauses. Paused
        // must no longer be pinned — the pin means a hold is being measured, and nothing is measured
        // while paused — and since 2026-10-07 it also hides the ring, so a later sample must not
        // move the (hidden) ring's position either.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        SendFrame(_messenger, new PhysicalPoint(140, 120), 0);
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        Check.That(vm.IsPinned).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));

        Check.That(vm.IsPinned).IsFalse();
        Check.That(vm.IsPaused).IsTrue();
        Check.That(vm.IsVisible).IsFalse();

        SendFrame(_messenger, new PhysicalPoint(300, 400), 30_000);
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);
    }

    [Fact]
    public void StateChangedToAStateThatIsNotFixationOrFreeLook_AlsoUnpins()
    {
        // Belt and braces (re-check N2): the keyboard toggle calls OnFixationEnded directly while
        // suppression stops the detector, so a release keyed on the detector would strand the pin.
        // Core raises ReleaseRing on the state transition; the VM additionally unpins structurally on
        // any state that is neither Fixation nor FreeLook (FreeLook is where the pin outlives the
        // fixation while an offer is alive; Core's ReleaseRing frees it there). Mutation: drop the
        // StateChanged unpin → this reds.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        Check.That(vm.IsPinned).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.ManualSelectionActive));

        Check.That(vm.IsPinned).IsFalse();
    }

    [Fact]
    public void FreeLookWhilePinned_KeepsThePinAndTheRingStill_UntilCoreReleases()
    {
        // Live check 2026-10-02: while the user holds the activation button the fixation flips
        // Fixation/FreeLook about every 0.3 s. A VM that unpinned on every FreeLook snapped the ring to
        // the gaze and re-pinned it, over the button. Core owns the release (offer closed, or the run
        // ended with no offer). Mutation: unpin on FreeLook again → the first assertion reds.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        var pin = new PhysicalPoint(140, 120);

        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(pin));
        SendFrame(_messenger, new PhysicalPoint(150, 125), 0);
        SendFrame(_messenger, new PhysicalPoint(150, 125), 1_000_000);   // the glide has landed

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        SendFrame(_messenger, new PhysicalPoint(600, 700), 1_030_000);   // the gaze is on the button now
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        SendFrame(_messenger, new PhysicalPoint(610, 705), 1_060_000);

        Check.That(vm.IsPinned).IsTrue();
        Check.That(vm.Position).IsEqualTo(pin);

        _messenger.Send(new ReleaseRingMessage());                        // the offer closed

        Check.That(vm.IsPinned).IsFalse();
        Check.That(vm.Position).IsEqualTo(new PhysicalPoint(610, 705));
    }

    [Fact]
    public void PinRing_IsIgnored_InFreeLook()
    {
        // The VM keeps a pin through FreeLook since the ring follows the offer, so a pin that lands
        // after a dispatcher-side exit to FreeLook (the race Receive(PinRingMessage) guards) would
        // persist until Core's next release. Core only pins in Fixation. Mutation: accept a pin in
        // FreeLook → IsPinned is true, red.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));

        Check.That(vm.IsPinned).IsFalse();
    }

    [Fact]
    public void PinRing_IsIgnored_UnlessTheLastReportedStateIsFixation()
    {
        // Review A, F4: the exit is posted from the dispatcher, so a Release/StateChanged pair can land
        // between the gaze thread's state check and its pin post; the ring would then stay frozen with
        // no later release on the keyboard path. Mutation: drop the guard in Receive(PinRingMessage) →
        // the paused pin sticks, red.
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        Check.That(vm.IsPinned).IsFalse();

        // A real onset's StateChanged(Fixation) precedes its settle's pin, and then it pins.
        _messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        _messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        Check.That(vm.IsPinned).IsTrue();
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
    public void ExternalDwell_WhilePaused_RingStaysHidden()
    {
        // Combined-owner check: while paused the ring is hidden by the state itself, so an external
        // dwell claiming and then releasing the cursor leaves it hidden — clearing the external dwell
        // restores only what the state allows, and Paused allows nothing.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void ResumeFromPaused_ReappearsAtLiveGaze_NotAtThePrePausePosition()
    {
        // The hidden window keeps its last drawn Left/Top; without the snap in UpdateVisibility the
        // first shown frame after a (possibly minutes-long) pause presents the pre-pause position.
        // Mutation: drop the snap → Position stays 100,100, red.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        SendFrame(messenger, new PhysicalPoint(100, 100), 0);
        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsVisible).IsFalse();

        SendFrame(messenger, new PhysicalPoint(500, 400), 30_000);   // gaze moves while hidden

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(500);   // drawn at live gaze on the first shown frame
        Check.That(vm.Position.Y).IsEqualTo(400);
    }

    [Fact]
    public void Reappear_WritesPositionBeforeVisibility_TheShownWindowIsAlreadyAtLiveGaze()
    {
        // Order check (review round 1, I1): the activation halo pins "position, then show" as the
        // required order — showing first presents the previous position for a frame. Same contract
        // here: at the moment IsVisible flips true, Position must ALREADY be the live gaze.
        // Mutation: write IsVisible before the snap in UpdateVisibility → the observer of the
        // IsVisible change still reads the pre-pause position, red.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        SendFrame(messenger, new PhysicalPoint(100, 100), 0);
        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        SendFrame(messenger, new PhysicalPoint(500, 400), 30_000);   // gaze moved while hidden

        var positionWhenShown = new PhysicalPoint(-1, -1);
        void OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CursorCircleViewModel.IsVisible) && vm.IsVisible)
                positionWhenShown = vm.Position;
        }
        vm.PropertyChanged += OnPropertyChanged;
        try
        {
            messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        }
        finally
        {
            vm.PropertyChanged -= OnPropertyChanged;
        }

        Check.That(vm.IsVisible).IsTrue();
        Check.That(positionWhenShown.X).IsEqualTo(500);   // already at live gaze when the show fired
        Check.That(positionWhenShown.Y).IsEqualTo(400);
    }

    [Fact]
    public void ReappearWhilePinned_TheRingStaysExactlyAtThePin()
    {
        // Review round 1, I2 — the snap's pin exemption (mutation (c) survived without this): while
        // an offer is alive the ring is pinned at the offer's P, and no visibility flip may move it;
        // the pin owns the position until Core releases. External dwell is the flip that can hide
        // and re-show the ring while the pin survives (FreeLook keeps it). Mutation: drop the
        // !IsPinned guard in UpdateVisibility → the dwell clear snaps the pinned ring to gaze, red.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        SendFrame(messenger, new PhysicalPoint(140, 120), 0);
        messenger.Send(new StateChangedMessage(InteractionState.Fixation));
        messenger.Send(new PinRingMessage(new PhysicalPoint(140, 120)));
        SendFrame(messenger, new PhysicalPoint(140, 120), 30_000);
        SendFrame(messenger, new PhysicalPoint(140, 120), 1_000_000);   // the glide has landed
        Check.That(vm.IsPinned).IsTrue();

        // The external dwell hides the ring; the gaze moves away while it is active.
        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();
        SendFrame(messenger, new PhysicalPoint(600, 700), 2_000_000);

        // The dwell ends: the pinned ring reappears AT THE PIN, not at the gaze.
        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.IsPinned).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(140);
        Check.That(vm.Position.Y).IsEqualTo(120);
    }

    [Fact]
    public void ExternalDwellClear_ReappearsAtLiveGaze_NotAtThePreDwellPosition()
    {
        // The reappear snap's other trigger (review round 1 minor: neither non-pause reappear path
        // had a test): a cooperating app's dwell ends and the ring comes back — at the live gaze,
        // not at the position from before the app took the cursor. Same hidden-window reasoning as
        // the paused resume; this is the ExternalDwellActiveMessage-driven variant.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        SendFrame(messenger, new PhysicalPoint(200, 250), 0);
        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();

        SendFrame(messenger, new PhysicalPoint(700, 550), 30_000);   // gaze moves while external dwell owns it

        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Position.X).IsEqualTo(700);
        Check.That(vm.Position.Y).IsEqualTo(550);
    }
}
