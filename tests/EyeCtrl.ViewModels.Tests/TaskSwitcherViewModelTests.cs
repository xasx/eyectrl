namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

public class TaskSwitcherViewModelTests
{
    [Fact]
    public void Receive_Toggled_PopulatesTilesAndBecomesVisible()
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

        // The toggle ALONE must flip IsVisible — that is the show trigger the coordinator
        // watches. (Regression guard: a prior version deferred IsVisible to a View-only
        // callback, so the overlay never appeared even though tiles were populated.)
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).HasSize(2);
        Check.That(vm.Tiles[0].Title).IsEqualTo("Notepad");
        Check.That(vm.Tiles[0].ProcessId).IsEqualTo(100u);
        Check.That(vm.Tiles[1].Title).IsEqualTo("Calculator");
        Check.That(vm.Tiles[1].ProcessId).IsEqualTo(200u);
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

        // First toggle: show.
        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).HasSize(1);

        // Second toggle: hide — IsVisible flips false and tiles are cleared.
        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.Tiles).IsEmpty();
    }

    [Fact]
    public void Toggled_ThenDwellOnPositionedTile_SendsSelectionAndDismisses()
    {
        // End-to-end guard for the real runtime path: toggle shows the overlay, the View
        // reports a tile's laid-out center (UpdateTilePosition creates the dwell target
        // there), and dwelling on it activates → sends the selection → dismisses the overlay.
        var messenger = new WeakReferenceMessenger();
        var hwnd = new IntPtr(42);
        var wm = new FakeWindowManager(new[] { new AppWindow(hwnd, "Notepad", 100, "notepad") });
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        IntPtr? selected = null;
        messenger.Register<TaskSwitcherViewModelTests, TaskSwitcherSelectionMessage>(
            this, (_, m) => selected = m.Hwnd);

        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.IsVisible).IsTrue();

        // The View positions the tile after layout; simulate that (creates the dwell target).
        var center = new PhysicalPoint(500, 400);
        vm.UpdateTilePosition(hwnd, center);

        // Dwell: gaze enters the tile, then remains past the dwell time (700 ms default → µs).
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1000L;
        messenger.Send(new GazeCursorMovedMessage(center, 0));
        messenger.Send(new GazeCursorMovedMessage(center, dwellUs + 1));

        Check.That(selected).IsEqualTo(hwnd);
        Check.That(vm.IsVisible).IsFalse();  // overlay dismissed after a selection
        Check.That(vm.Tiles).IsEmpty();
    }

    [Fact]
    public void Dwell_BeforeAnyTilePositioned_DoesNotActivate()
    {
        // Between the toggle (IsVisible = true) and the View reporting tile positions, no
        // dwell targets exist, so gaze must not activate anything at the (0,0) placeholder.
        var messenger = new WeakReferenceMessenger();
        var hwnd = new IntPtr(7);
        var wm = new FakeWindowManager(new[] { new AppWindow(hwnd, "Notepad", 100, "notepad") });
        var settings = new EyeCtrlSettings();
        using var vm = new TaskSwitcherViewModel(wm, settings, messenger);

        bool anySelection = false;
        messenger.Register<TaskSwitcherViewModelTests, TaskSwitcherSelectionMessage>(
            this, (_, _) => anySelection = true);

        messenger.Send(new TaskSwitcherToggledMessage());

        // Gaze parked at the origin (the placeholder) for well past the dwell time.
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1000L;
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), 0));
        messenger.Send(new GazeCursorMovedMessage(new PhysicalPoint(0, 0), dwellUs + 1));

        Check.That(anySelection).IsFalse();
        Check.That(vm.IsVisible).IsTrue();  // still showing; nothing was selected
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

        public IntPtr? TryGetEligibleForegroundWindow() => null;
        public void Minimize(IntPtr hwnd) { }
        public void MaximizeOrRestore(IntPtr hwnd) { }
        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
