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

        Assert.Equal(100, vm.Position.X);
        Assert.Equal(200, vm.Position.Y);
    }

    [Fact]
    public void ReadsAppearance_FromSettings()
    {
        var settings = new EyeCtrlSettings();
        settings.Cursor.CircleSize = 50;
        settings.Cursor.CircleColor = "#FF0000";
        settings.Cursor.CircleOpacity = 0.8;
        var vm = new CursorCircleViewModel(settings, _messenger);

        Assert.Equal(50, vm.Size);
        Assert.Equal("#FF0000", vm.CircleColor);
        Assert.Equal(0.8, vm.Opacity);
    }

    [Fact]
    public void HidesWhenPaused_ShowsWhenResumed()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Assert.True(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.False(vm.IsVisible);

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsVisible);
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

        Assert.False(vm.IsVisible);
    }

    [Fact]
    public void ShowsAgain_WhenReturningToFreeLook_FromActionMenu()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.ActionMenuVisible));
        Assert.False(vm.IsVisible);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void ExternalDwellActive_HidesCursor_EvenInFreeLook()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsVisible);

        messenger.Send(new ExternalDwellActiveMessage(true));
        Assert.False(vm.IsVisible);

        messenger.Send(new ExternalDwellActiveMessage(false));
        Assert.True(vm.IsVisible);
    }

    [Fact]
    public void LeavingExternalTarget_DoesNotShowCursor_WhilePaused()
    {
        // Combined-owner check: clearing external-dwell must not override the paused state.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        messenger.Send(new ExternalDwellActiveMessage(true));
        Assert.False(vm.IsVisible);

        messenger.Send(new ExternalDwellActiveMessage(false));
        Assert.False(vm.IsVisible);
    }
}
