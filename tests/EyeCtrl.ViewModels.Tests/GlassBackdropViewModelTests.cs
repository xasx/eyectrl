namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using Xunit;

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

        Assert.True(vm.IsVisible);
        Assert.Equal(buttonCenter, vm.HideButtonCenter);
    }

    [Fact]
    public void Receive_HideBackdropMessage_SetsIsVisibleFalse()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        messenger.Send(new ShowBackdropMessage(new PhysicalPoint(1860, 40)));

        messenger.Send(new HideBackdropMessage());

        Assert.False(vm.IsVisible);
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

        // Simulate gaze inside hide button for full dwell duration
        long now = 1_000_000;
        long dwellUs = UIConstants.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond;
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now));
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now + dwellUs));

        Assert.NotNull(received);
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        var vm = new GlassBackdropViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var vm = new GlassBackdropViewModel(new EyeCtrlSettings(), new WeakReferenceMessenger());
        vm.Dispose();
        var ex = Record.Exception(() => vm.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Gaze_WhenNotVisible_DoesNotThrow()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);

        var ex = Record.Exception(() =>
            messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 1_000_000)));

        Assert.Null(ex);
    }

    [Fact]
    public void Gaze_AwayFromHideButton_ResetsProgress()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);
        var buttonCenter = new PhysicalPoint(1860, 40);
        var farAway = new PhysicalPoint(1, 1);
        messenger.Send(new ShowBackdropMessage(buttonCenter));

        long now = 1_000_000;
        long halfDwellUs = UIConstants.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now));
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now + halfDwellUs));
        Assert.True(vm.HideButtonProgress > 0);

        // Move gaze away — progress should reset
        messenger.Send(new GazeCursorMovedMessage(farAway, now + halfDwellUs + 1));

        Assert.Equal(0, vm.HideButtonProgress);
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
        long halfDwellUs = UIConstants.BackdropHideButtonDwellMs * MathConstants.MicrosecondsPerMillisecond / 2;
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now));
        messenger.Send(new GazeCursorMovedMessage(buttonCenter, now + halfDwellUs));

        Assert.True(vm.HideButtonProgress > 0.4 && vm.HideButtonProgress < 0.6);
    }
}
