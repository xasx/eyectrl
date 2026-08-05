namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class ToolbarViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();

    // Buttons must match what the View renders at startup: the window-actions region is included
    // only when ShowWindowActions is on. Without the ctor's setting gate, Buttons would report 11
    // while the View rendered 8 — a misleading source of truth. Uses a fresh settings instance per
    // case so the ShowWindowActions mutation can't bleed between Theory data points.
    [Theory]
    [InlineData(true, 11)]
    [InlineData(false, 8)]
    public void InitializesWithButtonsMatchingShowWindowActions(bool showWindowActions, int expectedCount)
    {
        var settings = new EyeCtrlSettings { Toolbar = { ShowWindowActions = showWindowActions } };
        var vm = new ToolbarViewModel(settings, _messenger);
        Check.That(vm.Buttons.Count).IsEqualTo(expectedCount);
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

    // Window-management toolbar region (Task 6): dwelling each new button id fires the matching
    // WindowActionRequestedMessage. Mirrors GazeDwellOnPositionedButton_ActivatesIt above — the
    // View reports positions per id, the VM creates dwell targets per id, so a missing dispatch
    // case would let the dwell complete silently with no message.
    [Theory]
    [InlineData("Minimize", WindowAction.Minimize)]
    [InlineData("MaximizeRestore", WindowAction.MaximizeRestore)]
    [InlineData("Close", WindowAction.Close)]
    public void OnTargetActivated_WindowButton_SendsWindowActionMessage(string id, WindowAction expected)
    {
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        WindowAction? received = null;
        messenger.Register<WindowActionRequestedMessage>(this, (_, msg) => received = msg.Action);

        // OnTargetActivated is protected; drive it via Receive on a positioned button, mirroring
        // GazeDwellOnPositionedButton_ActivatesIt (ToolbarViewModelTests.cs:144-160).
        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition(id, center);
        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(received).IsEqualTo(expected);
    }

    [Fact]
    public void ToolbarViewModel_ContainsWindowActionButtons()
    {
        // Pins Step 3 (VM array) independently of the dispatch switch — UpdateButtonScreenPosition
        // would create a target for any id, so the dwell test alone can't catch a missing array entry.
        var vm = new ToolbarViewModel(_settings, new WeakReferenceMessenger());
        Check.That(vm.Buttons.Select(b => b.Id).ToArray())
             .Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
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

    // ── Task 9: ShowWindowActions live-reconciliation ──────────────────────────────────
    //
    // SetWindowActionsVisible(false) must remove the three window-action buttons from Buttons
    // AND _buttonLookup, AND drop any positioned dwell target so a gaze resting where Close used
    // to be cannot fire it on the now-current foreground window (the hidden-toolbar hazard,
    // applied to per-id region removal). The reset of the toolbar (Pause/SelectionMode/…)
    // must be untouched.

    [Fact]
    public void SetWindowActionsVisible_False_RemovesAllThreeFromButtonsAndLookup()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);

        vm.SetWindowActionsVisible(false);

        Check.That(vm.Buttons.Select(b => b.Id))
             .Not.Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
        // Buttons count drops from 11 → 8 (only the window-action trio is removed).
        Check.That(vm.Buttons.Count).IsEqualTo(8);
    }

    [Fact]
    public void SetWindowActionsVisible_False_RemovedIdsAreNotDwellable()
    {
        // Close is no-confirm — a gaze pass over where Close *used to be* must not fire it after
        // the toggle-off. Position Close (creating its dwell target), then remove the region and
        // confirm the gaze sample no longer activates.
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Close", center);
        vm.SetWindowActionsVisible(false);

        WindowAction? received = null;
        messenger.Register<WindowActionRequestedMessage>(this, (_, msg) => received = msg.Action);

        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(received).IsNull();
    }

    [Fact]
    public void SetWindowActionsVisible_True_AfterRemoval_ReaddsAllThree()
    {
        var vm = new ToolbarViewModel(_settings, _messenger);

        vm.SetWindowActionsVisible(false);
        vm.SetWindowActionsVisible(true);

        // Re-added with the canonical symbols (round-trips through SymbolFor).
        Check.That(vm.Buttons.Select(b => b.Id).ToArray())
             .Contains(new[] { "Minimize", "MaximizeRestore", "Close" });
        Check.That(vm.Buttons.Count).IsEqualTo(11);

        var close = vm.Buttons.First(b => b.Id == "Close");
        Check.That(close.Symbol).IsEqualTo("\u2715");
    }

    [Fact]
    public void SetWindowActionsVisible_True_WhenAlreadyPresent_IsIdempotent()
    {
        // Region starts present (default). Calling with true again must not double-add — the
        // safety-critical invariant is that each id appears exactly once in Buttons/_buttonLookup,
        // so a dwell progress broadcast hits exactly one target.
        var vm = new ToolbarViewModel(_settings, _messenger);

        vm.SetWindowActionsVisible(true);
        vm.SetWindowActionsVisible(true);

        Check.That(vm.Buttons.Count(b => b.Id == "Close")).IsEqualTo(1);
        Check.That(vm.Buttons.Count).IsEqualTo(11);
    }

    [Fact]
    public void SetWindowActionsVisible_False_WhenAlreadyAbsent_IsIdempotent()
    {
        // Mirrors the True idempotent case from the other direction: removing an already-removed
        // region must not throw or corrupt state.
        var vm = new ToolbarViewModel(_settings, _messenger);

        vm.SetWindowActionsVisible(false);
        vm.SetWindowActionsVisible(false);

        Check.That(vm.Buttons.Count).IsEqualTo(8);
    }

    [Fact]
    public void SetWindowActionsVisible_False_LeavesCoreButtonsIntact()
    {
        // The toggle must not perturb the always-on region (Pause/SelectionMode/Keyboard/…).
        // A regression that nukes the whole Buttons collection would break the toolbar even with
        // the region off.
        var vm = new ToolbarViewModel(_settings, _messenger);

        vm.SetWindowActionsVisible(false);

        Check.That(vm.Buttons.Select(b => b.Id).ToArray())
             .Contains(new[] { "Pause", "SelectionMode", "Keyboard", "Settings", "ResetAdaptation" });
    }

    [Fact]
    public void SetWindowActionsVisible_False_DropsPositionedButtonSoReaddStartsFresh()
    {
        // Position Close, toggle off (which drops _positionedButtons[id] + DwellTarget), toggle
        // on, then position Close again. The second UpdateButtonScreenPosition must take the
        // first-position path (AddTarget), proving _positionedButtons was cleared on removal —
        // if it had leaked, AddTarget would be skipped and the re-added Close would have no target.
        var messenger = new WeakReferenceMessenger();
        var vm = new ToolbarViewModel(_settings, messenger);

        var center = new PhysicalPoint(500, 500);
        vm.UpdateButtonScreenPosition("Close", center);
        vm.SetWindowActionsVisible(false);
        vm.SetWindowActionsVisible(true);

        WindowAction? received = null;
        messenger.Register<WindowActionRequestedMessage>(this, (_, msg) => received = msg.Action);

        vm.UpdateButtonScreenPosition("Close", center);
        var dwellUs = (_settings.Dwell.ToolbarButtonDwellMs + 100) * 1000L;
        vm.Receive(new GazeCursorMovedMessage(center, 0));
        vm.Receive(new GazeCursorMovedMessage(center, dwellUs));

        Check.That(received).IsEqualTo(WindowAction.Close);
    }
}
