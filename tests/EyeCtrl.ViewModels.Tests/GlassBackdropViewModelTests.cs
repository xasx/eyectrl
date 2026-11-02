namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class GlassBackdropViewModelTests
{
    [Fact]
    public void Receive_ShowBackdropMessage_SetsIsVisibleAndDwellTarget()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);

        messenger.Send(new ShowBackdropMessage(buttonCenter));

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.HideButtonCenter).IsEqualTo(buttonCenter);
    }

    [Fact]
    public void Receive_HideBackdropMessage_SetsIsVisibleFalse()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        messenger.Send(new ShowBackdropMessage(new PhysicalPoint(1860, 40)));

        messenger.Send(new HideBackdropMessage());

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void DwellOnHideButton_SendsBackdropDismissMessage()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        messenger.Send(new ShowBackdropMessage(buttonCenter));
        BackdropDismissMessage? received = null;
        messenger.Register<BackdropDismissMessage>(this, (_, m) => received = m);

        // Simulate gaze inside hide button for the full dwell duration, at a realistic cadence.
        long now = 1_000_000;
        long dwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond;
        DwellDrive.Hold(messenger, buttonCenter, now, now + dwellUs);

        Check.That(received).IsNotNull();
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        var vm = new GlassBackdropViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new GlassBackdropViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());
        Check.That(ex).IsNull();
    }

    [Fact]
    public void Gaze_WhenNotVisible_DoesNotThrow()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);

        var ex = Record.Exception(() =>
            messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 1_000_000)));

        Check.That(ex).IsNull();
    }

    [Fact]
    public void Gaze_AwayFromHideButton_ResetsProgress()
    {
        var settings = new EyeCtrlSettings();
        // The reset-on-leave rule is the grace-0 behaviour; the default grace now HOLDS the progress
        // instead (DwellPauseGraceWiringTests covers that side).
        settings.Dwell.DwellPauseGraceMs = 0;
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        var farAway = new PhysicalPoint(1, 1);
        messenger.Send(new ShowBackdropMessage(buttonCenter));

        long now = 1_000_000;
        long halfDwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        DwellDrive.Hold(messenger, buttonCenter, now, now + halfDwellUs);
        Check.That(vm.HideButtonProgress).IsStrictlyGreaterThan(0);

        // Move gaze away — progress should reset
        messenger.Send(new GazeCursorMovedMessage(farAway, now + halfDwellUs + 1));

        Check.That(vm.HideButtonProgress).IsEqualTo(0);
    }

    [Fact]
    public void Dwell_OnHideButton_IncrementsProgress()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        messenger.Send(new ShowBackdropMessage(buttonCenter));

        long now = 1_000_000;
        long halfDwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        DwellDrive.Hold(messenger, buttonCenter, now, now + halfDwellUs);

        Check.That(vm.HideButtonProgress).IsStrictlyGreaterThan(0.4);
        Check.That(vm.HideButtonProgress).IsStrictlyLessThan(0.6);
    }

    // C5 H3: the hide button follows the edge-bar/DwellArcButton pattern — a look-away inside the
    // pause grace holds the progress AND marks the hold paused, so the window can dim the arc.
    [Fact]
    public void LeavingTheHideButtonMidDwell_MarksTheHoldPaused_AndReturningClearsIt()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        var farAway = new PhysicalPoint(1, 1);
        messenger.Send(new ShowBackdropMessage(buttonCenter));

        long now = 1_000_000;
        long halfDwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        DwellDrive.Hold(messenger, buttonCenter, now, now + halfDwellUs);
        Check.That(vm.IsDwellPaused).IsFalse();

        messenger.Send(new GazeCursorMovedMessage(farAway, now + halfDwellUs + 1));
        Check.WithCustomMessage("a look-away inside the grace must mark the hide button's hold paused")
            .That(vm.IsDwellPaused).IsTrue();
        Check.That(vm.HideButtonProgress).IsStrictlyGreaterThan(0.0);

        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now + halfDwellUs + 31));
        Check.That(vm.IsDwellPaused).IsFalse();
    }

    [Fact]
    public void AnExpiredHideButtonHold_ClearsThePausedCue()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        var farAway = new PhysicalPoint(1, 1);
        messenger.Send(new ShowBackdropMessage(buttonCenter));

        long now = 1_000_000;
        long halfDwellUs = settings.Dwell.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        DwellDrive.Hold(messenger, buttonCenter, now, now + halfDwellUs);
        long leftAt = now + halfDwellUs + 1;
        messenger.Send(new GazeCursorMovedMessage(farAway, leftAt));
        Check.That(vm.IsDwellPaused).IsTrue();

        // Past the default 500 ms grace: the hold is gone, so the cue must clear.
        messenger.Send(new GazeCursorMovedMessage(farAway, leftAt + 600 * MathConstants.MicrosecondsPerMillisecond));

        Check.That(vm.IsDwellPaused).IsFalse();
        Check.That(vm.HideButtonProgress).IsEqualTo(0);
    }

    [Fact]
    public void ShowAndHideBackdropMessages_ClearThePausedCue()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);

        vm.IsDwellPaused = true;
        messenger.Send(new HideBackdropMessage());
        Check.WithCustomMessage("hiding the backdrop must clear a held look").That(vm.IsDwellPaused).IsFalse();

        vm.IsDwellPaused = true;
        messenger.Send(new ShowBackdropMessage(buttonCenter));
        Check.WithCustomMessage("a fresh show must start undimmed").That(vm.IsDwellPaused).IsFalse();
    }
}
