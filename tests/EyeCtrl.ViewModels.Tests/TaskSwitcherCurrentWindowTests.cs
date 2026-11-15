namespace EyeCtrl.ViewModels.Tests;

using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S2-A: the window the owner is in when the switcher opens gets no tile, so the window used
/// before it becomes the first tile — Windows Alt-Tab's behaviour. The "current window" is the
/// foreground window read at the open through
/// <see cref="IWindowManager.TryGetEligibleForegroundWindow"/> (fail-closed: the shell, the
/// desktop and EyeCtrl's own windows come back null and exclude nothing).
///
/// <para>
/// Every test drives the real <see cref="TaskSwitcherViewModel"/> show path — the same call the
/// toolbar's toggle makes — so the assertions are about the tiles the owner would actually see,
/// not about a helper. The fake foreground is per-test state that can be changed between
/// sessions, which is what proves the read is not cached.
/// </para>
/// </summary>
[Collection("DpiHelper Tests (ViewModels)")]
public class TaskSwitcherCurrentWindowTests
{
    private static readonly PhysicalPoint CloseCenter = new(1000, 1100);

    [Fact]
    public void ForegroundWindow_GetsNoTile_AndTheNextWindowBecomesTheFirst()
    {
        // The ordinary case: the enumeration is z-order top-down, so the window the owner is in
        // is the first entry. Leaving it out promotes the window used before it to tile one.
        var messenger = new WeakReferenceMessenger();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager(Windows(1, 2, 3), foreground: new IntPtr(1)),
            new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles.Select(t => t.Hwnd)).ContainsExactly(new IntPtr(2), new IntPtr(3));
        Check.That(vm.Tiles[0].Title).IsEqualTo("Window 2");   // the next one in z-order is first
    }

    [Fact]
    public void ForegroundWindow_IsExcludedByHwnd_NotByPosition()
    {
        // An always-on-top window can sit ABOVE the foreground in z-order (Task Manager "always
        // on top", picture-in-picture — accepted in the ticket). The rule is hwnd equality, so
        // the middle window goes and the order of the rest is untouched — an implementation that
        // simply dropped tile one would leave the window the owner is in on screen.
        var messenger = new WeakReferenceMessenger();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager(Windows(1, 2, 3), foreground: new IntPtr(2)),
            new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles.Select(t => t.Hwnd)).ContainsExactly(new IntPtr(1), new IntPtr(3));
    }

    [Fact]
    public void NoEligibleForeground_ShowsEveryWindow()
    {
        // Fail-closed: the desktop, the shell or one of EyeCtrl's own windows in front means
        // TryGetEligibleForegroundWindow returns null — nothing may be left out.
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(Windows(1, 2, 3), foreground: null);
        using var vm = new TaskSwitcherViewModel(wm, new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles.Select(t => t.Hwnd))
            .ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(wm.ForegroundReads).IsEqualTo(1);   // still read, at the open
    }

    [Fact]
    public void ForegroundNotInTheEnumeratedList_ShowsEveryWindow()
    {
        // The foreground can be a window the enumeration filters out (tool window, owned window,
        // invisible). An hwnd that matches no tile excludes nothing.
        var messenger = new WeakReferenceMessenger();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager(Windows(1, 2, 3), foreground: new IntPtr(99)),
            new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles.Select(t => t.Hwnd))
            .ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
    }

    [Fact]
    public void OnlyTheForegroundWindow_LeavesNoTile_AndTheCloseDiscStillDismisses()
    {
        // The single-window desktop: the one window is the one the owner is in, so the grid is
        // empty. S2-B adds the empty message; here the contract is "zero tiles, no crash, and the
        // ✕ is still a live escape" — a fullscreen overlay with no tile and no working close
        // would trap a gaze-only user.
        var messenger = new WeakReferenceMessenger();
        var settings = new EyeCtrlSettings();
        var selections = new List<IntPtr>();
        using var vm = new TaskSwitcherViewModel(
            new FakeWindowManager(Windows(7), foreground: new IntPtr(7)),
            settings, messenger);

        messenger.Register<TaskSwitcherCurrentWindowTests, TaskSwitcherSelectionMessage>(
            this, (_, m) => selections.Add(m.Hwnd));

        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.IsVisible).IsTrue();
        Check.That(vm.Tiles).IsEmpty();

        // The View's post-layout pass with an empty grid: no tile to register, then the close disc.
        // (TaskSwitcherLayout.Compute(tileCount: 0) returns a zero block — pinned in its own tests.)
        vm.UpdateCloseButtonPosition(CloseCenter);

        // The landing guard has no anchor here (no sample preceded the show) and the close disc
        // is never gated anyway; a hold past the dwell time must dismiss without switching.
        var dwellUs = settings.Dwell.ToolbarButtonDwellMs * 1000L;
        DwellDrive.Hold(messenger, CloseCenter, 0, dwellUs + 1);

        Check.That(vm.IsVisible).IsFalse();
        Check.That(selections).IsEmpty();
    }

    [Fact]
    public void Foreground_IsReadAgainForEachSession_NeverCached()
    {
        // Two sessions, a different window in front each time: "current" is the foreground at
        // THIS open, not the first one ever seen. A cached read would leave the second session's
        // editor on screen (and hide a window the owner is not using).
        var messenger = new WeakReferenceMessenger();
        var wm = new FakeWindowManager(Windows(1, 2, 3), foreground: new IntPtr(1));
        using var vm = new TaskSwitcherViewModel(wm, new EyeCtrlSettings(), messenger);

        messenger.Send(new TaskSwitcherToggledMessage());
        Check.That(vm.Tiles.Select(t => t.Hwnd)).ContainsExactly(new IntPtr(2), new IntPtr(3));

        messenger.Send(new TaskSwitcherToggledMessage());   // closes
        Check.That(vm.IsVisible).IsFalse();

        wm.Foreground = new IntPtr(3);                      // the owner moved to another window
        messenger.Send(new TaskSwitcherToggledMessage());

        Check.That(vm.Tiles.Select(t => t.Hwnd)).ContainsExactly(new IntPtr(1), new IntPtr(2));
        Check.That(wm.ForegroundReads).IsEqualTo(2);        // once per open, never stashed
    }

    private static IReadOnlyList<AppWindow> Windows(params int[] hwnds) =>
        hwnds.Select(h => new AppWindow(new IntPtr(h), $"Window {h}", (uint)(100 + h), "app")).ToArray();

    /// <summary>
    /// Minimal IWindowManager fake: a fixed window list plus a settable, counted foreground
    /// answer. The count is how "read at every open, never cached" becomes observable.
    /// </summary>
    private sealed class FakeWindowManager : IWindowManager
    {
        private readonly IReadOnlyList<AppWindow> _windows;

        internal FakeWindowManager(IReadOnlyList<AppWindow> windows, IntPtr? foreground)
        {
            _windows = windows;
            Foreground = foreground;
        }

        internal IntPtr? Foreground { get; set; }

        internal int ForegroundReads { get; private set; }

        public IReadOnlyList<AppWindow> EnumerateOpenWindows() => _windows;

        public void BringToFront(IntPtr hwnd) { }

        public IntPtr? TryGetEligibleForegroundWindow()
        {
            ForegroundReads++;
            return Foreground;
        }

        public void Minimize(IntPtr hwnd) { }
        public void MinimizeNonBlocking(IntPtr hwnd) { }

        public void MaximizeOrRestore(IntPtr hwnd) { }

        public bool CloseWindow(IntPtr hwnd) => false;
    }
}
