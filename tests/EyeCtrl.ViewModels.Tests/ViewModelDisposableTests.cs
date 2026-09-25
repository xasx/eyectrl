namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Tests to verify that all ViewModels properly implement IDisposable
/// and correctly clean up message handler registrations.
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class ViewModelDisposableTests
{
    [Fact]
    public void CursorCircleViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void CursorCircleViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new CursorCircleViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 200), 0));
        Check.That(vm.Position.X).IsEqualTo(100);

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(300, 400), 0));
        Check.That(vm.Position.X).IsEqualTo(100); // Position should not change
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
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(messenger);

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void ActivationButtonViewModel_UnregistersMessagesOnDispose()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new ShowActivationButtonMessage(new PhysicalPoint(100, 200)));
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideActivationButtonMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void ActivationButtonViewModel_DisposeIsIdempotent()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ActivationButtonViewModel(messenger);

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

        Check.That(vm is IDisposable).IsTrue();
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
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new EndManualSelectionMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
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

        Check.That(vm is IDisposable).IsTrue();
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
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideActionMenuMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
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

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void ToolbarViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsPaused).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new StateChangedMessage(InteractionState.FreeLook));
        Check.That(vm.IsPaused).IsTrue(); // Should remain paused since message wasn't processed
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

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void KeyboardViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new KeyboardViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new ShowKeyboardMessage());
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideKeyboardMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
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

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void ManualSelectionGazeIndicatorViewModel_UnregistersMessagesOnDispose()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ManualSelectionGazeIndicatorViewModel(messenger);

        // Verify VM receives messages before disposal
        messenger.Send(new StartManualSelectionMessage(
            new PhysicalPoint(500, 500), 100, 2.0, new PhysicalPoint(500, 500)));
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new EndManualSelectionMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
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
    public void GlassBackdropViewModel_ImplementsIDisposable()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);

        Check.That(vm is IDisposable).IsTrue();
    }

    [Fact]
    public void GlassBackdropViewModel_UnregistersMessagesOnDispose()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);

        // Verify VM receives messages before disposal
        var buttonCenter = new PhysicalPoint(1860, 40);
        messenger.Send(new ShowBackdropMessage(buttonCenter));
        Check.That(vm.IsVisible).IsTrue();

        // Dispose the ViewModel
        ((IDisposable)vm).Dispose();

        // After disposal, messages should not be processed
        messenger.Send(new HideBackdropMessage());
        Check.That(vm.IsVisible).IsTrue(); // Should remain visible since message wasn't processed
    }

    [Fact]
    public void GlassBackdropViewModel_DisposeIsIdempotent()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var vm = new GlassBackdropViewModel(settings, messenger);

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
        Check.That(vm1 is IDisposable).IsTrue();
        Check.That(vm2 is IDisposable).IsTrue();
        Check.That(vm3 is IDisposable).IsTrue();
    }

    [Fact]
    public void DisposedViewModel_DoesNotRespondToMessages()
    {
        var settings = new EyeCtrlSettings();
        var messenger = new WeakReferenceMessenger();
        var cursorVm = new CursorCircleViewModel(settings, messenger);
        var toolbarVm = new ToolbarViewModel(settings, messenger);

        // Verify initial state - cursor should be visible by default
        Check.That(cursorVm.IsVisible).IsTrue();
        Check.That(toolbarVm.IsPaused).IsFalse();

        // Dispose cursor VM but keep toolbar VM active
        ((IDisposable)cursorVm).Dispose();

        // Send messages - only toolbar should respond
        messenger.Send(new StateChangedMessage(InteractionState.Paused));

        // CursorVM should NOT respond (still visible), ToolbarVM SHOULD respond (now paused)
        Check.That(cursorVm.IsVisible).IsTrue(); // Should remain visible since disposed
        Check.That(toolbarVm.IsPaused).IsTrue(); // Should respond and be paused

        // Clean up
        ((IDisposable)toolbarVm).Dispose();
    }
}
