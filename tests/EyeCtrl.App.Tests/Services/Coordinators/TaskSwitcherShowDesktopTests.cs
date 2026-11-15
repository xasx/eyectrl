namespace EyeCtrl.App.Tests.Services.Coordinators;

using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S3-A's App half: <see cref="QuickActionDispatcher.HandleShowDesktop"/>.
///
/// <para>
/// The coordinator decision is pinned here: one <c>IWindowManager.Minimize</c> per handle the
/// switcher session enumerated — never the shell's MinimizeAll/ToggleDesktop, which would also
/// minimise EyeCtrl's own surfaces and take away the owner's only control. Every test uses the
/// recording fake; no real window is touched.
/// </para>
/// </summary>
public class TaskSwitcherShowDesktopTests
{
    [Fact]
    public void HandleShowDesktop_MinimisesEveryHandleOnce_InTheOrderItWasGiven()
    {
        var windows = new RecordingWindowManager();
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var dispatcher = Build(windows, switcher);

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage(
            [new IntPtr(1), new IntPtr(2), new IntPtr(3)]));

        Check.That(windows.Minimized).ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.That(windows.OtherOps).IsEqualTo(0);
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void HandleShowDesktop_IsAnOrdinaryPerTileAction_NotAShellWideMinimizeAll()
    {
        // The guard against the forbidden shortcut: the only IWindowManager member the action may
        // call is Minimize. A regression that reached for the shell (or for a "minimise everything"
        // helper) would show up as a different op, or as a call the empty list cannot explain.
        var windows = new RecordingWindowManager();
        var dispatcher = Build(windows, new TestFakes.FakeTaskSwitcherOverlay());

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage([new IntPtr(7)]));

        Check.That(windows.Minimized).ContainsExactly(new IntPtr(7));
        Check.That(windows.EligibilityQueries).IsEqualTo(0);   // no foreground lookup, no shell path
        Check.That(windows.OtherOps).IsEqualTo(0);
    }

    [Fact]
    public void HandleShowDesktop_OneBadHandle_DoesNotAbandonTheRest_AndNeverThrows()
    {
        // Each minimise is individually guarded, and the queued handler must never let an
        // exception reach the dispatcher (the gaze thread's continuation).
        var windows = new RecordingWindowManager { Throwing = new IntPtr(2) };
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var dispatcher = Build(windows, switcher);

        Check.ThatCode(() => dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage(
                [new IntPtr(1), new IntPtr(2), new IntPtr(3)])))
            .DoesNotThrow();

        Check.That(windows.Minimized).ContainsExactly(new IntPtr(1), new IntPtr(3));
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void HandleShowDesktop_WithNoWindows_StillDismisses()
    {
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var windows = new RecordingWindowManager();
        var dispatcher = Build(windows, switcher);

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage([]));

        Check.That(windows.Minimized).IsEmpty();
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    private static QuickActionDispatcher Build(
        RecordingWindowManager windows, TestFakes.FakeTaskSwitcherOverlay switcher)
    {
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            WindowManager = windows,
            OverlayManager = TestFakes.MakeOverlayManager(taskSwitcher: switcher),
        };
        var dispatcher = new QuickActionDispatcher();
        dispatcher.Initialize(context);
        return dispatcher;
    }

    /// <summary>
    /// Fake <see cref="EyeCtrl.Core.Interfaces.IWindowManager"/> that records which handles were
    /// minimised. <see cref="Throwing"/> makes one handle throw, for the containment test.
    /// </summary>
    private sealed class RecordingWindowManager : EyeCtrl.Core.Interfaces.IWindowManager
    {
        internal List<IntPtr> Minimized { get; } = new();
        internal IntPtr? Throwing { get; init; }
        internal int OtherOps { get; private set; }
        internal int EligibilityQueries { get; private set; }

        public IReadOnlyList<EyeCtrl.Core.Models.AppWindow> EnumerateOpenWindows() => [];

        public void BringToFront(IntPtr hwnd) => OtherOps++;

        public IntPtr? TryGetEligibleForegroundWindow()
        {
            EligibilityQueries++;
            return null;
        }

        public void Minimize(IntPtr hwnd)
        {
            if (Throwing == hwnd)
                throw new InvalidOperationException("this fake handle refuses to minimise");
            Minimized.Add(hwnd);
        }

        public void MaximizeOrRestore(IntPtr hwnd) => OtherOps++;
        public bool CloseWindow(IntPtr hwnd) { OtherOps++; return false; }
    }
}
