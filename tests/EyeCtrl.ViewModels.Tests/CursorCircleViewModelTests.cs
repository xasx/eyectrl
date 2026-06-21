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
    public void HidesWhenPaused_ShowsWhenResumed()
    {
        var settings = new EyeCtrlSettings();
        var vm = new CursorCircleViewModel(settings, _messenger);
        Check.That(vm.IsVisible).IsTrue();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsVisible).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsVisible).IsTrue();
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
    public void LeavingExternalTarget_DoesNotShowCursor_WhilePaused()
    {
        // Combined-owner check: clearing external-dwell must not override the paused state.
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        messenger.Send(new ExternalDwellActiveMessage(true));
        Check.That(vm.IsVisible).IsFalse();

        messenger.Send(new ExternalDwellActiveMessage(false));
        Check.That(vm.IsVisible).IsFalse();
    }
}
