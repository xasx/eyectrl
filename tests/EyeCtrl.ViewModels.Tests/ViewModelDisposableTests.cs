namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Tests to verify that all ViewModels properly implement IDisposable
/// and correctly clean up message handler registrations.
/// </summary>
public class ViewModelDisposableTests
{
    [Fact]
    public void CursorCircleViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void CursorCircleViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 0));
        Assert.Equal(100, vm.Position.X);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));
        Assert.Equal(100, vm.Position.X); // Position should not change
    }

    [Fact]
    public void CursorCircleViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        // Should not throw when called multiple times
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void ActivationButtonViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void ActivationButtonViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(100, 200)));
        Assert.True(vm.IsVisible);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideActivationButtonMessage());
        Assert.True(vm.IsVisible); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void ActivationButtonViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(settings, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void ManualSelectionOverlayViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionOverlayViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void ManualSelectionOverlayViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionOverlayViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 100, 2.0, new PhysicalPoint(500, 500)));
        Assert.True(vm.IsVisible);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new EndManualSelectionMessage());
        Assert.True(vm.IsVisible); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void ManualSelectionOverlayViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionOverlayViewModel(settings, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void ActionMenuViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void ActionMenuViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        var actions = new[] { InteractionAction.LeftClick, InteractionAction.RightClick };
        messenger.Send(new ShowActionMenuMessage(
            new PhysicalPoint(500, 500), actions, false, 800, new PhysicalPoint(500, 500), 0, 50));
        Assert.True(vm.IsVisible);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideActionMenuMessage());
        Assert.True(vm.IsVisible); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void ActionMenuViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ActionMenuViewModel(settings, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void ToolbarViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void ToolbarViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Assert.True(vm.IsPaused);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Assert.True(vm.IsPaused); // Should remain paused since message wasn't processed
    }

    [Fact]
    public void ToolbarViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void KeyboardViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(settings, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void KeyboardViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new ShowKeyboardMessage());
        Assert.True(vm.IsVisible);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideKeyboardMessage());
        Assert.True(vm.IsVisible); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void KeyboardViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(settings, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void ManualSelectionGazeIndicatorViewModel_ImplementsIDisposable()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void ManualSelectionGazeIndicatorViewModel_UnregistersMessagesOnDispose()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 100, 2.0, new PhysicalPoint(500, 500)));
        Assert.True(vm.IsVisible);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new EndManualSelectionMessage());
        Assert.True(vm.IsVisible); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void ManualSelectionGazeIndicatorViewModel_DisposeIsIdempotent()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void SettingsViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json"));
        var vm = new SettingsViewModel(settings, settingsService, messenger);

        Assert.IsAssignableFrom<IDisposable>(vm);
    }

    [Fact]
    public void SettingsViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var settingsService = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "settings.json"));
        var vm = new SettingsViewModel(settings, settingsService, messenger);

        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
        ((IDisposable)vm).Dispose();
    }

    [Fact]
    public void MultipleViewModels_CanBeDisposedIndependently()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();

        var vm1 = new CursorCircleViewModel(settings, messenger);
        var vm2 = new ToolbarViewModel(settings, messenger);
        var vm3 = new KeyboardViewModel(settings, messenger);

        // Dispose in different order
        ((IDisposable)vm2).Dispose();
        ((IDisposable)vm1).Dispose();
        ((IDisposable)vm3).Dispose();

        // All should be disposed without errors
        Assert.IsAssignableFrom<IDisposable>(vm1);
        Assert.IsAssignableFrom<IDisposable>(vm2);
        Assert.IsAssignableFrom<IDisposable>(vm3);
    }

    [Fact]
    public void DisposedViewModel_DoesNotRespondToMessages()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var cursorVm = new CursorCircleViewModel(settings, messenger);
        var toolbarVm = new ToolbarViewModel(settings, messenger);

        // Verify initial state - cursor should be visible by default
        Assert.True(cursorVm.IsVisible);
        Assert.False(toolbarVm.IsPaused);

        // Dispose cursor VM but keep toolbar VM active
        ((IDisposable)cursorVm).Dispose();

        // Send messages - only toolbar should respond
        messenger.Send(new StateChangedMessage(InteractionState.Paused));

        // CursorVM should NOT respond (still visible), ToolbarVM SHOULD respond (now paused)
        Assert.True(cursorVm.IsVisible); // Should remain visible since disposed
        Assert.True(toolbarVm.IsPaused); // Should respond and be paused

        // Clean up
        ((IDisposable)toolbarVm).Dispose();
    }
}
