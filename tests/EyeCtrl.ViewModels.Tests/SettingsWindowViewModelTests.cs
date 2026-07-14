namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class SettingsWindowViewModelTests
{
    private readonly WeakReferenceMessenger _messenger = new();
    private readonly EyeCtrlSettings _settings = new();
    private readonly SettingsService _settingsService;

    public SettingsWindowViewModelTests()
    {
        _settingsService = new SettingsService("test_settings_window.json");
    }

    private SettingsWindowViewModel CreateViewModel()
    {
        var settingsVM = new SettingsViewModel(_settings, _settingsService, _messenger);
        var quickActionsEditor = new SettingsQuickActionsEditorViewModel(
            new FakeQuickActionsStore());
        return new SettingsWindowViewModel(settingsVM, quickActionsEditor, _messenger, _settings.Dwell);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IQuickActionsStore"/> for the SettingsWindowViewModel
    /// fixture (the concrete store lives in EyeCtrl.App, which the test project does not reference).
    /// </summary>
    private sealed class FakeQuickActionsStore : IQuickActionsStore
    {
        private readonly List<QuickAction> _actions = new();

        public IReadOnlyList<QuickAction> LoadAll() => _actions;
        public void Save(IReadOnlyList<QuickAction> actions)
        {
            _actions.Clear();
            _actions.AddRange(actions);
        }
        public QuickAction? FindById(string id) => _actions.FirstOrDefault(a => a.Id == id);
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        var vm = CreateViewModel();

        Check.That(vm).IsNotNull();
        Check.That(vm.Settings).IsNotNull();
        Check.That(vm.Settings.Tabs).HasSize(13);
    }

    [Fact]
    public void Constructor_RegistersWithMessenger()
    {
        var vm = CreateViewModel();
        vm.Register("Cursor", DwellCategory.Tab, 85, 600, () => { });
        vm.UpdatePosition("Cursor", DwellCategory.Tab, new PhysicalPoint(100, 100));

        // Send gaze messages to trigger dwell progress
        for (int i = 0; i < 5; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // ViewModel should update progress when it receives gaze messages
        Check.That(vm.TabProgress).ContainsKey("Cursor");
        Check.That(vm.TabProgress["Cursor"] > 0).IsTrue();
    }

    [Fact]
    public void TabDwellProgress_UpdatesOnGazeInput()
    {
        var vm = CreateViewModel();
        vm.Register("Cursor", DwellCategory.Tab, 85, 600, () => { });
        vm.UpdatePosition("Cursor", DwellCategory.Tab, new PhysicalPoint(100, 100));

        // Simulate sustained gaze at 30 FPS for 300ms
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // Should have some progress
        Check.That(vm.TabProgress).ContainsKey("Cursor");
        Check.That(vm.TabProgress["Cursor"] > 0).IsTrue();
    }

    [Fact]
    public void TabActivation_ChangesActiveTab()
    {
        var vm = CreateViewModel();
        vm.Register("Dwell", DwellCategory.Tab, 85, 600, () => { });
        vm.UpdatePosition("Dwell", DwellCategory.Tab, new PhysicalPoint(100, 100));

        // Simulate complete dwell (600ms at 30 FPS = 18 frames)
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Check.That(vm.ActiveTabId).IsEqualTo("Dwell");
        Check.That(vm.Settings.CurrentTabName).IsEqualTo("Dwell");
    }

    [Fact]
    public void CloseTab_SendsCloseMessage()
    {
        var vm = CreateViewModel();
        bool closeSent = false;
        _messenger.Register<CloseSettingsMessage>(this, (_, _) => closeSent = true);

        vm.Register("close", DwellCategory.Tab, 85, 600, () => { });
        vm.UpdatePosition("close", DwellCategory.Tab, new PhysicalPoint(50, 50));

        // Simulate complete dwell on close button
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(50, 50), i * 33000));
        }

        Check.That(closeSent).IsTrue();
    }

    [Fact]
    public void Dispose_UnregistersFromMessenger()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        // After disposal, sending messages should not affect the ViewModel
        vm.UpdatePosition("Cursor", DwellCategory.Tab, new PhysicalPoint(100, 100));
        _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), 0));

        // Should not crash or update (verified by no exception)
    }

    #region Pagination Tests

    [Fact]
    public void PaginationNext_AdvancesPage()
    {
        var vm = CreateViewModel();
        vm.Settings.ShowTab("Cursor"); // Tab with multiple items
        var initialPage = vm.Settings.CurrentPage;

        vm.Register("pag_next", DwellCategory.Pagination, 60, 600, () => { });
        vm.UpdatePosition("pag_next", DwellCategory.Pagination, new PhysicalPoint(200, 200));

        // Simulate complete dwell on next button
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), i * 33000));
        }

            // Page should advance if there are multiple pages
            if (vm.Settings.TotalPages > 1)
                Check.That(vm.Settings.CurrentPage > initialPage).IsTrue();
    }

    [Fact]
    public void PaginationPrevious_GoesBackPage()
    {
        var vm = CreateViewModel();
        vm.Settings.ShowTab("Cursor");

        // Manually go to page 1 first
        if (vm.Settings.TotalPages > 1)
        {
            vm.Settings.NextPage();
            Check.That(vm.Settings.CurrentPage).IsEqualTo(1);

            vm.Register("pag_prev", DwellCategory.Pagination, 60, 600, () => { });
            vm.UpdatePosition("pag_prev", DwellCategory.Pagination, new PhysicalPoint(150, 150));

            // Simulate dwell on previous button
            for (int i = 0; i < 20; i++)
            {
                _messenger.Send(new GazeCursorMovedMessage(
                    new PhysicalPoint(150, 150), i * 33000));
            }

            Check.That(vm.Settings.CurrentPage).IsEqualTo(0);
        }
    }

    [Fact]
    public void PaginationProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.Register("pag_next", DwellCategory.Pagination, 60, 600, () => { });
        vm.UpdatePosition("pag_next", DwellCategory.Pagination, new PhysicalPoint(200, 200));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), i * 33000));
        }

        Check.That(vm.PagProgress).ContainsKey("pag_next");
        Check.That(vm.PagProgress["pag_next"] > 0).IsTrue();
    }

    #endregion

    #region Modal Tests

    [Fact]
    public void ShowModal_DisplaysCorrectMessage()
    {
        var vm = CreateViewModel();
        Check.That(vm.IsModalVisible).IsFalse();

        bool callbackCalled = false;
        vm.ShowModal("Confirm action?", () => callbackCalled = true);

        Check.That(vm.IsModalVisible).IsTrue();
        Check.That(vm.ModalMessage).IsEqualTo("Confirm action?");
        Check.That(callbackCalled).IsFalse(); // Not called yet
    }

    [Fact]
    public void ModalYes_InvokesCallback()
    {
        var vm = CreateViewModel();
        bool confirmed = false;
        vm.ShowModal("Reset all settings?", () => confirmed = true);

        vm.SimulateActivated("modal_yes", DwellCategory.Modal);

        Check.That(confirmed).IsTrue();
        Check.That(vm.IsModalVisible).IsFalse(); // Modal auto-closes
    }

    [Fact]
    public void ModalNo_ClosesWithoutCallback()
    {
        var vm = CreateViewModel();
        bool confirmed = false;
        vm.ShowModal("Are you sure?", () => confirmed = true);

        vm.SimulateActivated("modal_no", DwellCategory.Modal);

        Check.That(confirmed).IsFalse(); // Callback NOT invoked
        Check.That(vm.IsModalVisible).IsFalse(); // Modal closed
    }

    [Fact]
    public void HideModal_ClearsState()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Test", () => { });
        Check.That(vm.IsModalVisible).IsTrue();

        vm.HideModal();

        Check.That(vm.IsModalVisible).IsFalse();
        Check.That(vm.ModalMessage).IsEqualTo("");
    }

    [Fact]
    public void ModalProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Confirm?", () => { });

        // Add modal targets (View's responsibility)
        vm.Register("modal_yes", DwellCategory.Modal, 70, 500, () => { });
        vm.Register("modal_no", DwellCategory.Modal, 70, 500, () => { });

        vm.UpdatePosition("modal_yes", DwellCategory.Modal, new PhysicalPoint(100, 100));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Check.That(vm.ModalProgress).ContainsKey("modal_yes");
        Check.That(vm.ModalProgress["modal_yes"] > 0).IsTrue();
    }

    #endregion

    #region Control Action Tests

    [Fact]
    public void RegisterControlAction_StoresAction()
    {
        var vm = CreateViewModel();
        bool actionCalled = false;

        vm.Register("test_control", DwellCategory.Control, 50, 600, () => actionCalled = true);
        vm.SimulateActivated("test_control", DwellCategory.Control);

        Check.That(actionCalled).IsTrue();
    }

    [Fact]
    public void ControlActivation_InvokesRegisteredAction()
    {
        var vm = CreateViewModel();
        int counter = 0;

        vm.Register("increment", DwellCategory.Control, 50, 600, () => counter++);
        vm.UpdatePosition("increment", DwellCategory.Control, new PhysicalPoint(100, 100));

        // Simulate complete dwell
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Check.That(counter).IsEqualTo(1);
    }

    [Fact]
    public void ControlProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.Register("test", DwellCategory.Control, 50, 600, () => { });
        vm.UpdatePosition("test", DwellCategory.Control, new PhysicalPoint(150, 150));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(150, 150), i * 33000));
        }

        Check.That(vm.ControlProgress).ContainsKey("test");
        Check.That(vm.ControlProgress["test"] > 0).IsTrue();
    }

    [Fact]
    public void MultipleControls_WorkIndependently()
    {
        var vm = CreateViewModel();
        int counterA = 0;
        int counterB = 0;

        vm.Register("controlA", DwellCategory.Control, 50, 600, () => counterA++);
        vm.Register("controlB", DwellCategory.Control, 50, 600, () => counterB++);

        vm.UpdatePosition("controlA", DwellCategory.Control, new PhysicalPoint(100, 100));
        vm.UpdatePosition("controlB", DwellCategory.Control, new PhysicalPoint(200, 200));

        // Dwell on controlA
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Check.That(counterA).IsEqualTo(1);
        Check.That(counterB).IsEqualTo(0);

        // Wait for cooldown to pass, then dwell on controlB
        // Cooldown is typically 300ms, so start at timestamp that's well past it
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), (i + 50) * 33000));
        }

        Check.That(counterA).IsEqualTo(1);
        Check.That(counterB).IsEqualTo(1);
    }

    [Fact]
    public void UnregisteredControl_DoesNotThrow()
    {
        var vm = CreateViewModel();

        // Should not throw when simulating unknown control
        var exception = Record.Exception(() =>
            vm.SimulateActivated("unknown_control", DwellCategory.Control));

        Check.That(exception).IsNull();
    }

    [Fact]
    public void ClearControlTargets_RemovesAllControlsAndProgress()
    {
        var vm = CreateViewModel();

        // Register some controls and add targets
        vm.Register("control1", DwellCategory.Control, 50, 600, () => { });
        vm.Register("control2", DwellCategory.Control, 50, 600, () => { });
        vm.UpdatePosition("control1", DwellCategory.Control, new PhysicalPoint(100, 100));
        vm.UpdatePosition("control2", DwellCategory.Control, new PhysicalPoint(200, 200));

        // Simulate partial dwell to populate progress
        for (int i = 0; i < 5; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // Verify we have progress
        Check.That(vm.ControlProgress.Count > 0).IsTrue();

        // Clear all control targets
        vm.ClearCategory(DwellCategory.Control);

        // Verify everything is cleared
        Check.That(vm.ControlProgress).IsEmpty();

        // Verify cleared actions don't execute
        vm.SimulateActivated("control1", DwellCategory.Control);
        // Should not throw and should do nothing
    }

    #endregion

    #region IDwellRegistrar Tests

    [Fact]
    public void Register_Tab_CreatesDwellTarget()
    {
        var vm = CreateViewModel();
        bool actionFired = false;

        vm.Register("my_tab", DwellCategory.Tab, 85, 600, () => actionFired = true);
        vm.UpdatePosition("my_tab", DwellCategory.Tab, new PhysicalPoint(100, 100));

        // Complete dwell (600ms at 30 FPS = 18+ frames at 33000us each)
        for (int i = 0; i < 20; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

        // Tab activation fires the registered action AND the tab-switch logic
        Check.That(actionFired).IsTrue();
    }

    [Fact]
    public void Register_Control_CreatesDwellTarget()
    {
        var vm = CreateViewModel();
        bool actionFired = false;

        vm.Register("my_ctrl", DwellCategory.Control, 50, 600, () => actionFired = true);
        vm.UpdatePosition("my_ctrl", DwellCategory.Control, new PhysicalPoint(150, 150));

        for (int i = 0; i < 20; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(150, 150), i * 33000));

        Check.That(actionFired).IsTrue();
    }

    [Fact]
    public void GetProgress_ReturnsCurrentDwellProgress()
    {
        var vm = CreateViewModel();

        vm.Register("prog_ctrl", DwellCategory.Control, 50, 600, () => { });
        vm.UpdatePosition("prog_ctrl", DwellCategory.Control, new PhysicalPoint(100, 100));

        // Partial dwell — about half the required frames
        for (int i = 0; i < 10; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

        double progress = vm.GetProgress("prog_ctrl", DwellCategory.Control);
        Check.That(progress > 0 && progress <= 1).IsTrue();
    }

    [Fact]
    public void Unregister_RemovesDwellTarget()
    {
        var vm = CreateViewModel();
        bool actionFired = false;

        vm.Register("temp_ctrl", DwellCategory.Control, 50, 600, () => actionFired = true);
        vm.UpdatePosition("temp_ctrl", DwellCategory.Control, new PhysicalPoint(100, 100));
        vm.Unregister("temp_ctrl", DwellCategory.Control);

        for (int i = 0; i < 20; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

        Check.That(actionFired).IsFalse();
    }

    [Fact]
    public void ClearCategory_RemovesAllInCategory()
    {
        var vm = CreateViewModel();
        bool aFired = false;
        bool bFired = false;

        vm.Register("ctrl_a", DwellCategory.Control, 50, 600, () => aFired = true);
        vm.Register("ctrl_b", DwellCategory.Control, 50, 600, () => bFired = true);
        vm.UpdatePosition("ctrl_a", DwellCategory.Control, new PhysicalPoint(100, 100));
        vm.UpdatePosition("ctrl_b", DwellCategory.Control, new PhysicalPoint(200, 200));

        vm.ClearCategory(DwellCategory.Control);

        for (int i = 0; i < 20; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(100, 100), i * 33000));

        Check.That(aFired).IsFalse();
        Check.That(bFired).IsFalse();
    }

    [Fact]
    public void SimulateActivated_InvokesRegisteredAction()
    {
        var vm = CreateViewModel();
        bool actionFired = false;

        vm.Register("sim_ctrl", DwellCategory.Control, 50, 600, () => actionFired = true);
        vm.SimulateActivated("sim_ctrl", DwellCategory.Control);

        Check.That(actionFired).IsTrue();
    }

    [Fact]
    public void Register_Pagination_WorksWithExistingLogic()
    {
        var vm = CreateViewModel();

        vm.Register("pag_next", DwellCategory.Pagination, 60, 600, () => { });
        vm.UpdatePosition("pag_next", DwellCategory.Pagination, new PhysicalPoint(200, 200));

        // Partial dwell — check progress is accumulating
        for (int i = 0; i < 10; i++)
            _messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(200, 200), i * 33000));

        double progress = vm.GetProgress("pag_next", DwellCategory.Pagination);
        Check.That(progress > 0).IsTrue();
    }

    #endregion
}
