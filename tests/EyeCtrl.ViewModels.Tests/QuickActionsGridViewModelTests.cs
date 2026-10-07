namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

[Collection("DpiHelper Tests (ViewModels)")]
public class QuickActionsGridViewModelTests
{
    /// <summary>
    /// Minimal IQuickActionsStore fake backed by a mutable list. Matches the repo's
    /// no-mocking-library testing convention (nested private class).
    /// </summary>
    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private List<QuickAction> _actions;
        public FakeQuickActionsStore(List<QuickAction> actions) => _actions = actions;
        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions) => _actions = actions.ToList();
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    // Each test builds a fresh WeakReferenceMessenger (sibling TaskSwitcherViewModelTests
    // does the same) instead of touching the WeakReferenceMessenger.Default singleton.
    // That removes all cross-test leak risk without needing UnregisterAll/Cleanup.
    private static QuickActionsGridViewModel CreateVM(
        WeakReferenceMessenger messenger,
        List<QuickAction>? actions = null)
    {
        var store = new FakeQuickActionsStore(actions ?? new List<QuickAction>());
        var settings = new EyeCtrlSettings();
        return new QuickActionsGridViewModel(store, settings, messenger);
    }

    [Fact]
    public void Constructor_IsNotVisible_AndTilesEmpty()
    {
        var vm = CreateVM(new WeakReferenceMessenger());
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    [Fact]
    public void Receive_ToggleWhenHidden_BecomesVisible()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = CreateVM(messenger, new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" }
        });

        // Send via the messenger the VM registered with (same path as TaskSwitcherViewModelTests).
        messenger.Send(new QuickActionsToggledMessage());

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).HasSize(1);
    }

    [Fact]
    public void Receive_ToggleWhenVisible_HidesOverlay()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = CreateVM(messenger);

        messenger.Send(new QuickActionsToggledMessage()); // show
        messenger.Send(new QuickActionsToggledMessage()); // hide

        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void Receive_ToggleWhenDisabled_DoesNotShowOverlay()
    {
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        settings.QuickActions.Enabled = false;
        var store = new FakeQuickActionsStore(new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C", Enabled = true }
        });
        var vm = new QuickActionsGridViewModel(store, settings, messenger);

        messenger.Send(new QuickActionsToggledMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    [Fact]
    public void Reload_OnlyIncludesEnabledActions()
    {
        var messenger = new WeakReferenceMessenger();
        var actions = new List<QuickAction>
        {
            new() { Label = "A", Type = QuickActionType.CannedText, Payload = "hello", Enabled = true },
            new() { Label = "B", Type = QuickActionType.MediaControl, Payload = "PlayPause", Enabled = false },
        };
        var vm = CreateVM(messenger, actions);

        messenger.Send(new QuickActionsToggledMessage());

        Check.That(vm.Tiles).HasSize(1);
        Check.That(vm.Tiles[0].Action.Label).IsEqualTo("A");
    }

    [Fact]
    public void Reload_UsesTheHostsPresetLabelFunction()
    {
        var messenger = new WeakReferenceMessenger();
        var actions = new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C", PresetId = "kbd.copy" },
        };
        var vm = CreateVM(messenger, actions);
        vm.PresetLabelFor = _ => "Kopieren";

        messenger.Send(new QuickActionsToggledMessage());

        Check.That(vm.Tiles[0].Label).IsEqualTo("Kopieren");
        Check.That(vm.Tiles[0].Action.Label).IsEqualTo("Copy"); // stored data is untouched
    }

    [Fact]
    public void SimulateTargetActivated_SendsSelectionMessage()
    {
        var messenger = new WeakReferenceMessenger();
        var action = new QuickAction { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" };
        var vm = CreateVM(messenger, new List<QuickAction> { action });

        QuickActionSelectionMessage? received = null;
        messenger.Register<QuickActionsGridViewModelTests, QuickActionSelectionMessage>(
            this, (_, m) => received = m);

        messenger.Send(new QuickActionsToggledMessage());
        // Drive activation through the public test hook, not reflection — same pattern as
        // ToolbarViewModel.SimulateButtonActivated.
        vm.SimulateTargetActivated(action.Id);

        Check.That(received).IsNotNull();
        Check.That(received!.Action).IsSameReferenceAs(action);
        Check.That(vm.IsVisible).IsFalse();
    }

    [Fact]
    public void SimulateTargetActivated_CloseTarget_HidesWithoutSelection()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = CreateVM(messenger, new List<QuickAction>
        {
            new() { Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" }
        });

        QuickActionSelectionMessage? received = null;
        messenger.Register<QuickActionsGridViewModelTests, QuickActionSelectionMessage>(
            this, (_, m) => received = m);

        messenger.Send(new QuickActionsToggledMessage());
        vm.SimulateTargetActivated(QuickActionsGridViewModel.CloseTargetId);

        Check.That(vm.IsVisible).IsFalse();
        Check.That(received).IsNull();   // close button dismisses, fires no selection
    }
}
