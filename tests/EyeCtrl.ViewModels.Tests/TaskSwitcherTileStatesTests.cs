namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S2-B: the three states the switcher used to hide — a minimised window (its tile now says
/// "Minimiert"), a session with no window at all (the empty message) and windows beyond the
/// grid's capacity (the "Weitere Fenster: N" chip).
///
/// <para>
/// Everything here drives the real show path (the toggle the toolbar sends), so the flags are
/// asserted on the session the owner would actually get. The overflow count is the one number
/// with a rule worth pinning twice: it is the enumerated tile count MINUS the faces the View
/// registered as dwell targets — the same "reachable" the T3 open line writes — so the chip can
/// never claim a window that has no tile, nor hide one that has none.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherTileStatesTests
{
    private static readonly PhysicalPoint CloseCenter = new(1000, 1100);

    [Fact]
    public void MinimisedWindow_CarriesTheFlag_OnItsTile_AndOnlyThere()
    {
        // The enumeration reads IsIconic per window (Win32WindowManager); the VM must carry it to
        // the tile the owner looks at, and must not smear it onto its neighbours.
        var messenger = new WeakReferenceMessenger();
        var windows = new[]
        {
            new AppWindow(new IntPtr(1), "Editor", 100, "editor"),
            new AppWindow(new IntPtr(2), "Chat", 200, "chat", IsMinimized: true),
            new AppWindow(new IntPtr(3), "Mail", 300, "mail"),
        };
        using var vm = new TaskSwitcherViewModel(new FakeWindowManager(windows, foreground: null),
            new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles.Select(t => t.IsMinimized))
            .ContainsExactly(false, true, false);
        Check.That(vm.Tiles[1].Title).IsEqualTo("Chat");
    }

    [Fact]
    public void NoWindowAtAll_ReportsEmpty_AndStillDismissesThroughTheCloseDisc()
    {
        // A session whose enumeration is empty is the one S2-B message case left after S3-A: one
        // window that IS the foreground one now draws the desktop tile instead (it can still be
        // minimised), so "no windows" means exactly that. The VM's job: say the grid is empty (the
        // View's message), create no tile target, and keep the ✕ live — a fullscreen overlay with
        // neither a tile nor an escape traps a gaze-only user.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var selections = new List<IntPtr>();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager([], foreground: null),
            settings, messenger);

        messenger.Register<TaskSwitcherTileStatesTests, TaskSwitcherSelectionMessage>(
            this, (_, m) => selections.Add(m.Hwnd));

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).IsEmpty();
        Check.That(vm.HasDesktopTile).IsFalse();
        Check.That(vm.PanelItems).IsEmpty();
        Check.That(vm.IsEmpty).IsTrue();
        Check.That(vm.DetectorForTest.Targets).IsEmpty();

        // The View's post-layout pass with nothing to register: only the close disc appears.
        vm.UpdateCloseButtonPosition(CloseCenter);
        Check.That(vm.DetectorForTest.Targets.Select(t => t.Id))
            .ContainsExactly(TaskSwitcherViewModel.CloseTargetId);
        Check.That(vm.OverflowCount).IsEqualTo(0);   // no window is hidden behind an overflow

        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1000L;
        DwellDrive.Hold(messenger, CloseCenter, 0, dwellUs + 1);

        Check.That(vm.IsVisible).IsFalse();
        Check.That(selections).IsEmpty();
    }

    [Fact]
    public void WindowsArePresent_ReportNotEmpty()
    {
        var messenger = new WeakReferenceMessenger();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager([new AppWindow(new IntPtr(1), "Editor", 100, "editor")], foreground: null),
            new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles).HasSize(1);
        Check.That(vm.IsEmpty).IsFalse();
    }

    [Fact]
    public void OverflowCount_IsEnumeratedMinusReachable_NeverNegative()
    {
        // 64 windows, 63 faces reachable: the 64th window has no tile, and the chip says so.
        var (vm, messenger) = Open(64);
        using var _ = vm;
        messenger.Send(new TaskSwitcherToggledMessage());

        Register(vm, 63);

        Check.That(vm.OverflowCount).IsEqualTo(1);
        Check.That(vm.HasOverflow).IsTrue();

        // Exactly at capacity: nothing is hidden, so no chip.
        var (full, fullMessenger) = Open(63);
        using var __ = full;
        fullMessenger.Send(new TaskSwitcherToggledMessage());
        Register(full, 63);
        Check.That(full.OverflowCount).IsEqualTo(0);
        Check.That(full.HasOverflow).IsFalse();

        // Four windows lost to the viewport (a shorter screen, a bigger keep-out): four, not one.
        var (shortScreen, shortMessenger) = Open(64);
        using var ___ = shortScreen;
        shortMessenger.Send(new TaskSwitcherToggledMessage());
        Register(shortScreen, 60);
        Check.That(shortScreen.OverflowCount).IsEqualTo(4);

        // A re-registration of the same face replaces its entry — it must not inflate "reachable".
        var (reregistered, reregisteredMessenger) = Open(64);
        using var ____ = reregistered;
        reregisteredMessenger.Send(new TaskSwitcherToggledMessage());
        Register(reregistered, 63);
        reregistered.UpdateTilePosition(new IntPtr(1), new PhysicalRect(40, 200, 184, 104));
        Check.That(reregistered.OverflowCount).IsEqualTo(1);
    }

    [Fact]
    public void OverflowCount_StaysZero_UntilTheLayoutPass_MakesReachabilityKnown()
    {
        // Reachability is geometric: before the View has registered a single face, nothing is
        // known and the chip must not claim every window is hidden.
        var (vm, messenger) = Open(64);
        using var _ = vm;
        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.OverflowCount).IsEqualTo(0);
        Check.That(vm.HasOverflow).IsFalse();

        Register(vm, 63);
        Check.That(vm.HasOverflow).IsTrue();
    }

    [Fact]
    public void HasOverflow_NotifiesWhenTheCountChanges()
    {
        // The chip's Visibility binds to HasOverflow, not to the count: without the derived
        // property's change notification the chip would be decided once, at load, and never move.
        var (vm, messenger) = Open(64);
        using var _ = vm;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        messenger.Send(new TaskSwitcherToggledMessage());
        Register(vm, 63);

        Check.That(raised).Contains(nameof(TaskSwitcherViewModel.OverflowCount));
        Check.That(raised).Contains(nameof(TaskSwitcherViewModel.HasOverflow));
    }

    [Fact]
    public void HideClearsTheStates_SoTheNextSessionDoesNotInheritThem()
    {
        // The VM is a DI singleton: a leftover chip or empty message would describe the session
        // before it.
        var (vm, messenger) = Open(64);
        using var _ = vm;
        messenger.Send(new TaskSwitcherToggledMessage());
        Register(vm, 63);
        Check.That(vm.HasOverflow).IsTrue();

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.IsVisible).IsFalse();
        Check.That(vm.HasOverflow).IsFalse();
        Check.That(vm.IsEmpty).IsFalse();
    }

    /// <summary>A VM over <paramref name="windowCount"/> ordinary windows, hidden until toggled.</summary>
    private static (TaskSwitcherViewModel Vm, IMessenger Messenger) Open(int windowCount)
    {
        var messenger = new WeakReferenceMessenger();
        var windows = Enumerable.Range(1, windowCount)
            .Select(i => new AppWindow(new IntPtr(i), $"Window {i}", (uint)(100 + i), "app"))
            .ToArray();
        var vm = new TaskSwitcherViewModel(new FakeWindowManager(windows, foreground: null),
            new EyeCtrlSettings(), messenger);
        return (vm, messenger);
    }

    /// <summary>
    /// The View's post-layout pass for the first <paramref name="reachable"/> tiles: one distinct
    /// visible face each, then the close disc (which is where the count becomes final).
    /// </summary>
    private static void Register(TaskSwitcherViewModel vm, int reachable)
    {
        for (var i = 0; i < reachable; i++)
        {
            var column = i % 9;
            var row = i / 9;
            vm.UpdateTilePosition(new IntPtr(i + 1),
                new PhysicalRect(60 + (column * 200), 186 + (row * 120), 184, 104));
        }

        vm.UpdateCloseButtonPosition(CloseCenter);
    }

    /// <summary>Minimal IWindowManager fake: a fixed list, a settable foreground, nothing else.</summary>
    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows, IntPtr? foreground)
        {
            _windows = windows;
            Foreground = foreground;
        }

        internal IntPtr? Foreground { get; }

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;

        public void BringToFront(IntPtr hwnd) { }

        public IntPtr? TryGetEligibleForegroundWindow() => Foreground;

        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }

        public void MaximizeOrRestore(IntPtr hwnd) { }

        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
