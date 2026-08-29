namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    [Fact]
    public void InitializesWithTheEightCoreButtons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);

        Check.That(vm.Buttons.Select(b => b.Id)).ContainsExactly(
            "Pause", "SelectionMode", "Keyboard", "Calibrate",
            "Settings", "TaskSwitcher", "QuickActions", "ResetAdaptation");
    }

    [Fact]
    public void ToolbarViewModel_HasNoWindowActionButtons()
    {
        // Window actions moved to the Quick Actions toolbar (2026-08-09 migration). A regression
        // that re-adds them here would restore a non-removable no-confirm Close.
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        Check.That(vm.Buttons.Select(b => b.Id))
             .Not.Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
    }

    [Fact]
    public void PauseButton_SendsPauseToggledMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<PauseToggledMessage>(_messenger, (_, _) => received = true);

        // Find the Pause button and simulate activation
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        vm.SimulateButtonActivated("Pause");

        Check.That(received).IsTrue();
    }

    [Fact]
    public void ResetAdaptationButton_SendsResetAdaptationMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        bool received = false;
        _messenger.Register<ResetAdaptationMessage>(_messenger, (_, _) => received = true);

        vm.SimulateButtonActivated("ResetAdaptation");

        Check.That(received).IsTrue();
    }

    [Fact]
    public void UpdatesPausedState_OnStateChanged()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.IsPaused).IsFalse();

        _messenger.Send(new StateChangedMessage(InteractionState.Paused));
        Check.That(vm.IsPaused).IsTrue();

        // Pause button icon key should change to Play
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Check.That(pauseBtn.IconKey).IsEqualTo("Play");
    }

    [Fact]
    public void SelectionModeButton_SendsSelectionModeChangedMessage()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        SelectionMode? received = null;
        _messenger.Register<SelectionModeChangedMessage>(_messenger, (_, msg) => received = msg.Mode);

        vm.SimulateButtonActivated("SelectionMode");

        Check.That(received).IsNotNull();
        Check.That(received).IsEqualTo(SelectionMode.ManualSelection);
    }

    [Fact]
    public void SelectionModeChanged_UpdatesSelectionButtonIconKey()
    {
        // No explicit RegisterAll: the constructor already auto-registers every IRecipient<T> via
        // MessengerViewModelBase's IsActive = true (ObservableRecipient). Registering again here
        // throws — WeakReferenceMessenger rejects a duplicate recipient/message-type registration.
        var vm = new ToolbarViewModel(_settings, _messenger);
        var selectionButton = vm.Buttons.First(b => b.Id == "SelectionMode");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.None));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.None);

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.ManualSelection));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.ManualSelection);

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.AutoSelection));
        Check.That(selectionButton.IconKey).IsEqualTo(SelectionModeIcons.AutoSelection);
    }

    [Fact]
    public void SelectionModeIcons_RoundTripEveryMode()
    {
        foreach (var mode in Enum.GetValues<SelectionMode>())
            Check.WithCustomMessage($"{mode} lost its identity through the icon key")
                .That(SelectionModeIcons.ToMode(SelectionModeIcons.ForMode(mode))).IsEqualTo(mode);
    }

    [Fact]
    public void SelectionModeIcons_EmitDistinctKeysPerMode()
    {
        // With no label the icon is the button's primary state channel. The fill swap stays as a
        // secondary one (Task 5's SetSelectionModeState), but three modes sharing an icon would
        // leave colour carrying the distinction alone, which is what this design moved away from.
        var keys = Enum.GetValues<SelectionMode>().Select(SelectionModeIcons.ForMode).ToList();
        Check.That(keys.Distinct(StringComparer.Ordinal)).HasSize(keys.Count);
    }

    [Fact]
    public void SelectionModeCycle_VisitsEveryEnumValueAndWraps()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);
        var seen = new List<SelectionMode>();
        messenger.Register<SelectionModeChangedMessage>(this, (_, m) =>
        {
            seen.Add(m.Mode);
            vm.Receive(m);
        });

        int count = Enum.GetValues<SelectionMode>().Length;
        for (int i = 0; i <= count; i++)
            vm.SimulateButtonActivated("SelectionMode");

        Check.That(seen.Distinct().Count()).IsEqualTo(count);
        Check.That(seen[count]).IsEqualTo(seen[0]);
    }

    // The View (ToolbarWindow) renders five buttons — Pause, SelectionMode, Keyboard, Settings,
    // ResetAdaptation — and reports each one's real on-screen center via UpdateButtonScreenPosition.
    // The remaining three logical buttons (Calibrate, TaskSwitcher, QuickActions) are never
    // rendered and never positioned, so they must never become live gaze targets. Regression
    // guard for the "(0,0) placeholder" orphan bug: a gaze target created at the origin lets a
    // user whose gaze rests in the top-left screen corner dwell-activate an invisible button.
    [Fact]
    public void GazeAtScreenOrigin_AfterRenderedButtonsPositioned_DoesNotActivateUnrenderedButtons()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        // Simulate the View reporting real positions for the five rendered buttons only
        // (as ToolbarWindow.UpdateTargetPositions does once layout completes).
        var awayFromOrigin = new PhysicalPoint(500, 500);
        foreach (var id in new[] { "Pause", "SelectionMode", "Keyboard", "Settings", "ResetAdaptation" })
            vm.UpdateButtonScreenPosition(id, awayFromOrigin);

        bool unrenderedButtonFired = false;
        messenger.Register<CalibrationRequestedMessage>(this, (_, _) => unrenderedButtonFired = true);
        messenger.Register<TaskSwitcherToggledMessage>(this, (_, _) => unrenderedButtonFired = true);
        messenger.Register<QuickActionsToggledMessage>(this, (_, _) => unrenderedButtonFired = true);

        // Rest gaze on the screen origin for well beyond the toolbar dwell time.
        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        var origin = new PhysicalPoint(0, 0);
        vm.Receive(new GazeCursorMovedMessage(origin, 0));
        vm.Receive(new GazeCursorMovedMessage(origin, dwellUs));

        Check.That(unrenderedButtonFired).IsFalse();
    }

    // Positive counterpart: a button the View has positioned still activates on dwell at its
    // center, proving the lazy target-creation path produces a working gaze target.
    [Fact]
    public void GazeDwellOnPositionedButton_ActivatesIt()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        bool paused = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => paused = true);

        long dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(paused).IsTrue();
    }

    // Target-lifecycle contract (Task 7): a hidden/calibrating toolbar must not dwell — Close is
    // no-confirm, so a gaze pass over where Close *used to be* must not fire it on the current
    // foreground window. SetActive(false) gates Receive AND clears in-progress dwell state.
    [Fact]
    public void SetActive_False_HiddenToolbarDoesNotDwell()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);
        vm.SetActive(false);  // hidden

        bool fired = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(fired).IsFalse();
    }

    // No instant-activation on resume — ResetAllState cleared the stale EnteredTimestamp on hide,
    // so a single sample immediately after reactivation cannot complete a dwell.
    [Fact]
    public void SetActive_True_AfterHide_DoesNotInstantlyActivate()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Pause", center);

        // Partially dwell, then hide mid-dwell.
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, 100_000)); // partial (100ms < 700ms dwell)
        vm.SetActive(false);
        vm.SetActive(true);

        bool fired = false;
        messenger.Register<PauseToggledMessage>(this, (_, _) => fired = true);

        // A single sample immediately after resume must NOT complete a dwell — the stale
        // EnteredTimestamp was cleared by ResetAllState on hide.
        vm.Receive(new GazeCursorMovedMessage(center, 200_000));
        Check.That(fired).IsFalse();
    }
}
