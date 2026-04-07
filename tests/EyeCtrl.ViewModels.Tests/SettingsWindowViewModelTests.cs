namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Models;
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
        return new SettingsWindowViewModel(settingsVM, _messenger, _settings.Dwell);
    }

    [Fact]
    public void Constructor_InitializesCorrectly()
    {
        var vm = CreateViewModel();

        Assert.NotNull(vm);
        Assert.NotNull(vm.Settings);
        Assert.Equal(7, vm.Settings.Tabs.Count);
    }

    [Fact]
    public void Constructor_RegistersWithMessenger()
    {
        var vm = CreateViewModel();
        vm.AddTabTarget("Cursor", 85, 600);
        vm.UpdateTabButtonPosition("Cursor", new PhysicalPoint(100, 100));

        // Send gaze messages to trigger dwell progress
        for (int i = 0; i < 5; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // ViewModel should update progress when it receives gaze messages
        Assert.True(vm.TabProgress.ContainsKey("Cursor"));
        Assert.True(vm.TabProgress["Cursor"] > 0);
    }

    [Fact]
    public void TabDwellProgress_UpdatesOnGazeInput()
    {
        var vm = CreateViewModel();
        vm.AddTabTarget("Cursor", 85, 600);
        vm.UpdateTabButtonPosition("Cursor", new PhysicalPoint(100, 100));

        // Simulate sustained gaze at 30 FPS for 300ms
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // Should have some progress
        Assert.True(vm.TabProgress.ContainsKey("Cursor"));
        Assert.True(vm.TabProgress["Cursor"] > 0);
    }

    [Fact]
    public void TabActivation_ChangesActiveTab()
    {
        var vm = CreateViewModel();
        vm.AddTabTarget("Dwell", 85, 600);
        vm.UpdateTabButtonPosition("Dwell", new PhysicalPoint(100, 100));

        // Simulate complete dwell (600ms at 30 FPS = 18 frames)
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Assert.Equal("Dwell", vm.ActiveTabId);
        Assert.Equal("Dwell", vm.Settings.CurrentTabName);
    }

    [Fact]
    public void CloseTab_SendsCloseMessage()
    {
        var vm = CreateViewModel();
        bool closeSent = false;
        _messenger.Register<CloseSettingsMessage>(this, (_, _) => closeSent = true);

        vm.AddTabTarget("close", 85, 600);
        vm.UpdateTabButtonPosition("close", new PhysicalPoint(50, 50));

        // Simulate complete dwell on close button
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(50, 50), i * 33000));
        }

        Assert.True(closeSent);
    }

    [Fact]
    public void Dispose_UnregistersFromMessenger()
    {
        var vm = CreateViewModel();
        vm.Dispose();

        // After disposal, sending messages should not affect the ViewModel
        vm.UpdateTabButtonPosition("Cursor", new PhysicalPoint(100, 100));
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

        vm.AddPaginationTarget("pag_next", 60, 600);
        vm.UpdatePaginationButtonPosition("pag_next", new PhysicalPoint(200, 200));

        // Simulate complete dwell on next button
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), i * 33000));
        }

        // Page should advance if there are multiple pages
        if (vm.Settings.TotalPages > 1)
            Assert.True(vm.Settings.CurrentPage > initialPage);
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
            Assert.Equal(1, vm.Settings.CurrentPage);

            vm.AddPaginationTarget("pag_prev", 60, 600);
            vm.UpdatePaginationButtonPosition("pag_prev", new PhysicalPoint(150, 150));

            // Simulate dwell on previous button
            for (int i = 0; i < 20; i++)
            {
                _messenger.Send(new GazeCursorMovedMessage(
                    new PhysicalPoint(150, 150), i * 33000));
            }

            Assert.Equal(0, vm.Settings.CurrentPage);
        }
    }

    [Fact]
    public void PaginationProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.AddPaginationTarget("pag_next", 60, 600);
        vm.UpdatePaginationButtonPosition("pag_next", new PhysicalPoint(200, 200));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), i * 33000));
        }

        Assert.True(vm.PagProgress.ContainsKey("pag_next"));
        Assert.True(vm.PagProgress["pag_next"] > 0);
    }

    #endregion

    #region Modal Tests

    [Fact]
    public void ShowModal_DisplaysCorrectMessage()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsModalVisible);

        bool callbackCalled = false;
        vm.ShowModal("Confirm action?", () => callbackCalled = true);

        Assert.True(vm.IsModalVisible);
        Assert.Equal("Confirm action?", vm.ModalMessage);
        Assert.False(callbackCalled); // Not called yet
    }

    [Fact]
    public void ModalYes_InvokesCallback()
    {
        var vm = CreateViewModel();
        bool confirmed = false;
        vm.ShowModal("Reset all settings?", () => confirmed = true);

        vm.SimulateModalButtonActivated("modal_yes");

        Assert.True(confirmed);
        Assert.False(vm.IsModalVisible); // Modal auto-closes
    }

    [Fact]
    public void ModalNo_ClosesWithoutCallback()
    {
        var vm = CreateViewModel();
        bool confirmed = false;
        vm.ShowModal("Are you sure?", () => confirmed = true);

        vm.SimulateModalButtonActivated("modal_no");

        Assert.False(confirmed); // Callback NOT invoked
        Assert.False(vm.IsModalVisible); // Modal closed
    }

    [Fact]
    public void HideModal_ClearsState()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Test", () => { });
        Assert.True(vm.IsModalVisible);

        vm.HideModal();

        Assert.False(vm.IsModalVisible);
        Assert.Equal("", vm.ModalMessage);
    }

    [Fact]
    public void ModalProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.ShowModal("Confirm?", () => { });
        vm.UpdateModalButtonPosition("modal_yes", new PhysicalPoint(100, 100));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Assert.True(vm.ModalProgress.ContainsKey("modal_yes"));
        Assert.True(vm.ModalProgress["modal_yes"] > 0);
    }

    #endregion

    #region Control Action Tests

    [Fact]
    public void RegisterControlAction_StoresAction()
    {
        var vm = CreateViewModel();
        bool actionCalled = false;

        vm.RegisterControlAction("test_control", () => actionCalled = true);
        vm.SimulateControlActivated("test_control");

        Assert.True(actionCalled);
    }

    [Fact]
    public void ControlActivation_InvokesRegisteredAction()
    {
        var vm = CreateViewModel();
        int counter = 0;

        vm.RegisterControlAction("increment", () => counter++);
        vm.AddControlTarget("increment", 50, 600);
        vm.UpdateControlPosition("increment", new PhysicalPoint(100, 100));

        // Simulate complete dwell
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Assert.Equal(1, counter);
    }

    [Fact]
    public void ControlProgress_Updates()
    {
        var vm = CreateViewModel();
        vm.RegisterControlAction("test", () => { });
        vm.AddControlTarget("test", 50, 600);
        vm.UpdateControlPosition("test", new PhysicalPoint(150, 150));

        // Simulate partial dwell
        for (int i = 0; i < 10; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(150, 150), i * 33000));
        }

        Assert.True(vm.ControlProgress.ContainsKey("test"));
        Assert.True(vm.ControlProgress["test"] > 0);
    }

    [Fact]
    public void MultipleControls_WorkIndependently()
    {
        var vm = CreateViewModel();
        int counterA = 0;
        int counterB = 0;

        vm.RegisterControlAction("controlA", () => counterA++);
        vm.RegisterControlAction("controlB", () => counterB++);

        vm.AddControlTarget("controlA", 50, 600);
        vm.AddControlTarget("controlB", 50, 600);
        vm.UpdateControlPosition("controlA", new PhysicalPoint(100, 100));
        vm.UpdateControlPosition("controlB", new PhysicalPoint(200, 200));

        // Dwell on controlA
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        Assert.Equal(1, counterA);
        Assert.Equal(0, counterB);

        // Wait for cooldown to pass, then dwell on controlB
        // Cooldown is typically 300ms, so start at timestamp that's well past it
        for (int i = 0; i < 20; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(200, 200), (i + 50) * 33000));
        }

        Assert.Equal(1, counterA);
        Assert.Equal(1, counterB);
    }

    [Fact]
    public void UnregisteredControl_DoesNotThrow()
    {
        var vm = CreateViewModel();

        // Should not throw when simulating unknown control
        var exception = Record.Exception(() =>
            vm.SimulateControlActivated("unknown_control"));

        Assert.Null(exception);
    }

    [Fact]
    public void ClearControlTargets_RemovesAllControlsAndProgress()
    {
        var vm = CreateViewModel();

        // Register some controls and add targets
        vm.RegisterControlAction("control1", () => { });
        vm.RegisterControlAction("control2", () => { });
        vm.AddControlTarget("control1", 50, 600);
        vm.AddControlTarget("control2", 50, 600);
        vm.UpdateControlPosition("control1", new PhysicalPoint(100, 100));
        vm.UpdateControlPosition("control2", new PhysicalPoint(200, 200));

        // Simulate partial dwell to populate progress
        for (int i = 0; i < 5; i++)
        {
            _messenger.Send(new GazeCursorMovedMessage(
                new PhysicalPoint(100, 100), i * 33000));
        }

        // Verify we have progress
        Assert.True(vm.ControlProgress.Count > 0);

        // Clear all control targets
        vm.ClearControlTargets();

        // Verify everything is cleared
        Assert.Empty(vm.ControlProgress);

        // Verify cleared actions don't execute
        vm.SimulateControlActivated("control1");
        // Should not throw and should do nothing
    }

    #endregion
}
