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
    public void InitializesWith8Buttons()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        Check.That(vm.Buttons.Count).IsEqualTo(8);
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

        // Pause button symbol should change to play ▶
        var pauseBtn = vm.Buttons.First(b => b.Id == "Pause");
        Check.That(pauseBtn.Symbol).IsEqualTo("\u25B6");
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
    public void SelectionModeChanged_UpdatesSelectionButtonSymbol()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);
        var selectionButton = vm.Buttons.First(b => b.Id == "SelectionMode");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.None));
        Check.That(selectionButton.Symbol).IsEqualTo("\u25CB");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.ManualSelection));
        Check.That(selectionButton.Symbol).IsEqualTo("\u2295");

        _messenger.Send(new SelectionModeChangedMessage(SelectionMode.AutoSelection));
        Check.That(selectionButton.Symbol).IsEqualTo("\u25CE");
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

    // The View (ToolbarWindow) renders only four buttons — Pause, SelectionMode, Keyboard,
    // Settings — and reports each one's real on-screen center via UpdateButtonScreenPosition.
    // The remaining three logical buttons (Calibrate, TaskSwitcher, QuickActions) are never
    // rendered and never positioned, so they must never become live gaze targets. Regression
    // guard for the "(0,0) placeholder" orphan bug: a gaze target created at the origin lets a
    // user whose gaze rests in the top-left screen corner dwell-activate an invisible button.
    [Fact]
    public void GazeAtScreenOrigin_AfterRenderedButtonsPositioned_DoesNotActivateUnrenderedButtons()
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        // Simulate the View reporting real positions for the four rendered buttons only
        // (as ToolbarWindow.UpdateTargetPositions does once layout completes).
        var awayFromOrigin = new PhysicalPoint(500, 500);
        foreach (var id in new[] { "Pause", "SelectionMode", "Keyboard", "Settings" })
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
}
