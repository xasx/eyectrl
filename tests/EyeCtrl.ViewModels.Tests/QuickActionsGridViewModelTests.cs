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

    // ── B8b: stale target clears after Hide/Reload (review 01 M13/M14) ──────────────────────

    /// <summary>Shows the grid and registers the close + one tile target, as the view would.</summary>
    private static QuickActionsGridViewModel ShownWithTargets(WeakReferenceMessenger messenger)
    {
        var action = new QuickAction
        {
            Label = "Copy", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C"
        };
        var vm = CreateVM(messenger, new List<QuickAction> { action });
        messenger.Send(new QuickActionsToggledMessage());
        vm.UpdateTilePosition(action.Id, new PhysicalPoint(500, 500));
        vm.UpdateCloseButtonPosition(new PhysicalPoint(900, 100));
        Check.That(vm.DetectorForTest.Targets).HasSize(2);
        return vm;
    }

    [Fact]
    public void HideOverlay_ClearsTheDwellTargets()
    {
        // M13: dropping ClearTargets from HideOverlay survived the whole suite. Today the hide is
        // also covered by the detector not being fed while hidden, which is why this is a
        // coverage pin rather than a live defect — but the next show must never inherit targets
        // from the previous one.
        var vm = ShownWithTargets(new WeakReferenceMessenger());

        vm.HideOverlay();

        Check.WithCustomMessage("HideOverlay left dwell targets behind")
            .That(vm.DetectorForTest.Targets).IsEmpty();
    }

    [Fact]
    public void Reload_ClearsTheDwellTargets()
    {
        // M14: same gap for the reload. Reload deliberately does NOT seed (0,0) targets — the view
        // re-registers real centres after layout — so anything left here is a stale target.
        var vm = ShownWithTargets(new WeakReferenceMessenger());

        vm.Reload();

        Check.WithCustomMessage("Reload left dwell targets behind")
            .That(vm.DetectorForTest.Targets).IsEmpty();
    }

    // ── B8b F1: the dismiss-hold flag ────────────────────────────────────────────────────────

    [Fact]
    public void HasDwellProgress_IsTrueMidDwell_AndClearsWhenTheHoldExpires()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = ShownWithTargets(messenger);
        var centre = new PhysicalPoint(500, 500);

        Check.That(vm.HasDwellProgress).IsFalse();

        // Two samples: the first enters the target, the second reports positive progress.
        vm.Receive(new GazeCursorMovedMessage(centre, 1_000_000));
        vm.Receive(new GazeCursorMovedMessage(centre, 1_050_000));
        Check.WithCustomMessage("a tile mid-dwell must arm the summon's dismiss hold")
            .That(vm.HasDwellProgress).IsTrue();

        // Leaving keeps the frozen progress during the pause grace (the grid opts into DwellSettings),
        // so the hold survives a glance away...
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 1_100_000));
        Check.WithCustomMessage("the frozen grace progress must keep the dismiss hold armed")
            .That(vm.HasDwellProgress).IsTrue();

        // ...and expires after it, releasing the hold.
        vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 2_100_000));
        Check.WithCustomMessage("the expired grace left the dismiss hold armed")
            .That(vm.HasDwellProgress).IsFalse();
    }

    [Fact]
    public void HideOverlay_ClearsTheDwellProgressHold()
    {
        // A progress value left over from a hidden session must not pin the summon's dismiss hold
        // true for the next show: the grid would then never auto-close again.
        var messenger = new WeakReferenceMessenger();
        var vm = ShownWithTargets(messenger);
        var centre = new PhysicalPoint(500, 500);
        vm.Receive(new GazeCursorMovedMessage(centre, 1_000_000));
        vm.Receive(new GazeCursorMovedMessage(centre, 1_050_000));
        Check.That(vm.HasDwellProgress).IsTrue();

        vm.HideOverlay();

        Check.That(vm.HasDwellProgress).IsFalse();
        Check.That(vm.CloseDwellProgress).IsEqualTo(0);
    }

    // ── B8b F3: the tile cap ────────────────────────────────────────────────────────────────

    [Fact]
    public void CapVisibleTiles_TrimsTheTail_AndIsANoOpUnderTheCap()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = CreateVM(messenger, new List<QuickAction>
        {
            new() { Label = "A", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+A" },
            new() { Label = "B", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+B" },
            new() { Label = "C", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+C" },
            new() { Label = "D", Type = QuickActionType.KeyboardShortcut, Payload = "Ctrl+D" },
        });
        messenger.Send(new QuickActionsToggledMessage());

        vm.CapVisibleTiles(10);
        Check.WithCustomMessage("a cap above the tile count must not drop anything")
            .That(vm.Tiles.Select(t => t.Label)).ContainsExactly("A", "B", "C", "D");

        vm.CapVisibleTiles(2);
        Check.WithCustomMessage("the cap must keep the FIRST tiles and drop the tail")
            .That(vm.Tiles.Select(t => t.Label)).ContainsExactly("A", "B");
    }
}
