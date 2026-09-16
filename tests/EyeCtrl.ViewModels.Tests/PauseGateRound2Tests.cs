namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// Real-subclass coverage for the pause gate (round-2 review of the 2026-09 security branch).
/// The first version's tests used a synthetic subclass only, which is how two dead opt-ins and
/// one inescapable overlay shipped: the gate must be proven on the surfaces that actually
/// render. Two invariants per surface:
/// <list type="bullet">
/// <item>Surfaces whose dwell is their ONLY dismissal (task switcher tiles, quick actions)
/// keep dwelling while paused — and gate the EFFECT: dismissal happens, OS input does not.</item>
/// <item>Safety surfaces that opted in (toolbar band, modes menu, edge bar) dwell while
/// paused; surfaces that did not (scroll) are fully gated.</item>
/// </list>
/// </summary>
public class PauseGateRound2Tests
{
    private static GazeCursorMovedMessage Sample(long ts, bool paused) =>
        new(new PhysicalPoint(50, 50), ts) { IsPaused = paused };

    private static void Dwell(IMessenger m, bool paused, long t0, long t1)
    {
        m.Send(Sample(t0, paused));
        m.Send(Sample(t1, paused));
    }

    // ── Task switcher: the round-2 BLOCKER ────────────────────────────────────────────────

    [Fact]
    public void TaskSwitcher_PausedTileDwell_Dismisses_WithoutSelecting()
    {
        var messenger = new WeakReferenceMessenger();
        var selections = new List<IntPtr>();
        var recorder = new SelectionRecipient(selections);
        messenger.Register(recorder);
        using var vm = NewTaskSwitcher(messenger);
        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateTilePosition(new IntPtr(4242), new PhysicalPoint(50, 50));

        Dwell(messenger, paused: true, 0, 900_000);

        Check.That(vm.IsVisible).IsFalse();            // escapable: the overlay dismissed
        Check.That(selections).IsEmpty();              // and no window switch (OS input) fired
    }

    [Fact]
    public void TaskSwitcher_UnpausedTileDwell_SelectsAndDismisses()
    {
        var messenger = new WeakReferenceMessenger();
        var selections = new List<IntPtr>();
        var recorder = new SelectionRecipient(selections);
        messenger.Register(recorder);
        using var vm = NewTaskSwitcher(messenger);
        vm.Receive(new TaskSwitcherToggledMessage());
        vm.UpdateTilePosition(new IntPtr(4242), new PhysicalPoint(50, 50));

        Dwell(messenger, paused: false, 0, 900_000);

        Check.That(selections).ContainsExactly(new IntPtr(4242));
        Check.That(vm.IsVisible).IsFalse();
    }

    private static TaskSwitcherViewModel NewTaskSwitcher(IMessenger messenger) =>
        new(new OneWindowManager(), new EyeCtrlSettings(), messenger);

    private sealed class OneWindowManager : IWindowManager
    {
        public IReadOnlyList<AppWindow> EnumerateOpenWindows() =>
            new[] { new AppWindow(new IntPtr(4242), "target", 1234, "target") };
        public void BringToFront(IntPtr hwnd) { }
        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => true;
    }

    // ── Quick actions: same shape ─────────────────────────────────────────────────────────

    [Fact]
    public void QuickActions_PausedTileDwell_Dismisses_WithoutExecuting()
    {
        var messenger = new WeakReferenceMessenger();
        var executed = new List<QuickAction>();
        var recorder = new ExecutionRecipient(executed);
        messenger.Register(recorder);
        using var vm = NewQuickActions(messenger, "act1");
        vm.Receive(new QuickActionsToggledMessage());
        vm.UpdateTilePosition("act1", new PhysicalPoint(50, 50));

        Dwell(messenger, paused: true, 0, 900_000);

        Check.That(vm.IsVisible).IsFalse();            // dismissable, not inert
        Check.That(executed).IsEmpty();                // SendText / Process.Start never fired
    }

    [Fact]
    public void QuickActions_UnpausedTileDwell_ExecutesAndDismisses()
    {
        var messenger = new WeakReferenceMessenger();
        var executed = new List<QuickAction>();
        var recorder = new ExecutionRecipient(executed);
        messenger.Register(recorder);
        using var vm = NewQuickActions(messenger, "act1");
        vm.Receive(new QuickActionsToggledMessage());
        vm.UpdateTilePosition("act1", new PhysicalPoint(50, 50));

        Dwell(messenger, paused: false, 0, 900_000);

        Check.That(executed).HasSize(1);
        Check.That(executed[0].Id).IsEqualTo("act1");
        Check.That(vm.IsVisible).IsFalse();
    }

    private static QuickActionsGridViewModel NewQuickActions(IMessenger messenger, string actionId)
    {
        var action = new QuickAction { Id = actionId, Type = QuickActionType.LaunchExe, Label = "a" };
        return new QuickActionsGridViewModel(new SingleActionStore(action), new EyeCtrlSettings(), messenger);
    }

    private sealed class SingleActionStore(QuickAction action) : IQuickActionsStore
    {
        public IReadOnlyList<QuickAction> LoadAll() => new[] { action };
        public void Save(IReadOnlyList<QuickAction> actions) { }
        public QuickAction? FindById(string id) => id == action.Id ? action : null;
    }

    // ── Opt-ins must be LIVE on the real subclasses (round-2 mutation M5) ────────────────

    [Fact]
    public void Toolbar_PausedSamples_StillDwell_TheUnpauseOptInIsLive()
    {
        // Round-2 mutation M5 flipped the toolbar's DwellActiveWhilePaused to false and NOTHING
        // failed — the old Receive override bypassed the base gate, so the opt-in was dead
        // code. With the override routed through SampleMayDwell, a paused dwell must now
        // activate the Pause button: this test IS the M5 kill.
        var messenger = new WeakReferenceMessenger();
        var toggles = new List<bool>();
        var recorder = new PauseRecipient(toggles);
        messenger.Register(recorder);
        using var vm = new ToolbarViewModel(new EyeCtrlSettings(), messenger);
        vm.SetActive(true);
        vm.UpdateButtonScreenPosition("Pause", new PhysicalPoint(50, 50));

        Dwell(messenger, paused: true, 0, 900_000);

        Check.That(toggles).HasSize(1);
    }

    [Fact]
    public void ModesMenu_PausedSamples_StillAccumulateDwell()
    {
        // The modes menu holds the gaze-reachable pause toggle: gating it would make pause a
        // one-way door. Observable through a row's DwellProgress advancing on PAUSED samples.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ModesMenuViewModel(new EyeCtrlSettings(), messenger);
        vm.Receive(new ModesMenuToggledMessage());
        var rowId = vm.RowDefinitions[0].Id;
        vm.SetRowScreenPositions(new[] { (rowId, new PhysicalPoint(50, 50), 40.0) });

        messenger.Send(Sample(0, paused: true));
        messenger.Send(Sample(200_000, paused: true));

        Check.That(vm.Buttons.First(b => b.Id == rowId).DwellProgress).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public void EdgeBar_PausedSamples_StillDwell_TheReleaseButtonsAreLive()
    {
        var messenger = new WeakReferenceMessenger();
        var activations = 0;
        using var vm = new EdgeButtonBarViewModel(new EyeCtrlSettings(), messenger);
        vm.ShowButtons(new[]
        {
            new EdgeButtonDefinition("cancel", "\u2715", 800,
                _ => activations++, VerticalOffset: 0),
        }, new PhysicalPoint(50, 50));

        Dwell(messenger, paused: true, 0, 900_000);

        Check.That(activations).IsEqualTo(1);
    }

    // ── The ungated claim made true: scroll is actually gated now ─────────────────────────

    [Fact]
    public void Scroll_PausedSamples_AreGated()
    {
        // The scroll override replaced base.Receive entirely, so the round-1 commit's "scroll
        // is default-gated" claim was false. Its output is OS input (velocity → SendInput
        // scroll); paused samples must produce no velocity now.
        var messenger = new WeakReferenceMessenger();
        using var vm = new ScrollOverlayViewModel(messenger, new ScrollSettings(), new DwellSettings());
        vm.EnterScrollMode(new PhysicalPoint(50, 50), new PhysicalPoint(500, 500));

        var velocities = new List<(double Vx, double Vy)>();
        vm.TargetVelocityChanged += (vx, vy) => velocities.Add((vx, vy));

        messenger.Send(Sample(0, paused: true));
        messenger.Send(Sample(200_000, paused: true));

        Check.That(velocities).IsEmpty();
    }

    // internal (not private): ObservableRecipient's RegisterAll generator emits extensions
    // taking these types, which cannot be less accessible than their parameter.
    internal sealed class SelectionRecipient(List<IntPtr> sink)
        : CommunityToolkit.Mvvm.Messaging.IRecipient<Messages.TaskSwitcherSelectionMessage>
    {
        public void Receive(TaskSwitcherSelectionMessage m) => sink.Add(m.Hwnd);
    }

    internal sealed class ExecutionRecipient(List<QuickAction> sink)
        : CommunityToolkit.Mvvm.Messaging.IRecipient<QuickActionSelectionMessage>
    {
        public void Receive(QuickActionSelectionMessage m) => sink.Add(m.Action);
    }

    internal sealed class PauseRecipient(List<bool> sink)
        : CommunityToolkit.Mvvm.Messaging.IRecipient<PauseToggledMessage>
    {
        public void Receive(PauseToggledMessage m) => sink.Add(true);
    }
}
