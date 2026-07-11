namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class TaskSwitcherViewModelTests
{
    [Fact]
    public void Receive_Toggled_PopulatesTilesFromWindowManager()
    {
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(new[]
        {
            new AppWindow(new IntPtr(1), "Notepad", 100, "notepad"),
            new AppWindow(new IntPtr(2), "Calculator", 200, "calc")
        });
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        // The VM auto-registers as IRecipient<TaskSwitcherToggledMessage> via
        // ObservableRecipient.IsActive = true (set in MessengerViewModelBase ctor).
        messenger.Send(new TaskSwitcherToggledMessage());

        // H3 fix: ShowOverlay() populates tiles but does NOT set IsVisible —
        // ActivateAfterLayout() (called by the View after positioning) does.
        Check.That(vm.Tiles).HasSize(2);
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles[0].Title).IsEqualTo("Notepad");
        Check.That(vm.Tiles[0].ProcessId).IsEqualTo(100u);
        Check.That(vm.Tiles[1].Title).IsEqualTo("Calculator");
        Check.That(vm.Tiles[1].ProcessId).IsEqualTo(200u);

        // After the View activates, the overlay becomes visible.
        vm.ActivateAfterLayout();
        Check.That(vm.IsVisible).IsTrue();
    }

    [Fact]
    public void Receive_ToggledTwice_HidesOnSecondToggle()
    {
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(new[]
        {
            new AppWindow(new IntPtr(1), "Notepad", 100, "notepad")
        });
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        // First toggle: show (populate tiles, then activate — mirrors the View's flow).
        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.Tiles).HasSize(1);
        vm.ActivateAfterLayout();
        Check.That(vm.IsVisible).IsTrue();

        // Second toggle: hide — IsVisible flips false and tiles are cleared.
        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    /// <summary>
    /// Minimal IWindowManager fake: returns a fixed window list and records BringToFront calls.
    /// No System.Windows.* types (ViewModels.Tests targets plain net10.0).
    /// </summary>
    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        public FakeWindowManager(IReadOnlyList<AppWindow> windows) => _windows = windows;

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;

        public void BringToFront(IntPtr hwnd) { }
    }
}
