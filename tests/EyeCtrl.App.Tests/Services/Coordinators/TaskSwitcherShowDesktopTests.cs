namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.Windows.Threading;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// S3-A's App half: <see cref="QuickActionDispatcher.HandleShowDesktop"/>.
///
/// <para>
/// The coordinator decision is pinned here: one non-blocking <c>IWindowManager.MinimizeNonBlocking</c>
/// per handle the switcher session enumerated — never the shell's MinimizeAll/ToggleDesktop, which
/// would also minimise EyeCtrl's own surfaces and take away the owner's only control, and never the
/// blocking <c>Minimize</c>: this loop runs on the UI thread, which is also the gaze-dwell thread,
/// so a hung window must not be able to freeze the owner's control (fixup-01 F1). Every test uses
/// the recording fake; no real window is touched.
/// </para>
/// </summary>
public class TaskSwitcherShowDesktopTests
{
    [Fact]
    public void HandleShowDesktop_MinimisesEveryHandleOnce_InTheOrderItWasGiven_WithoutBlocking()
    {
        var windows = new RecordingWindowManager();
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var dispatcher = Build(windows, switcher);

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage(
            [new IntPtr(1), new IntPtr(2), new IntPtr(3)]));

        Check.That(windows.MinimizedNonBlocking)
            .ContainsExactly(new IntPtr(1), new IntPtr(2), new IntPtr(3));
        Check.WithCustomMessage(
                "Show desktop must never call the BLOCKING Minimize: this loop is the gaze thread, "
                + "and one hung window would freeze every overlay (Pause included).")
            .That(windows.MinimizedBlocking).IsEmpty();
        Check.That(windows.OtherOps).IsEqualTo(0);
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void HandleShowDesktop_IsAnOrdinaryPerTileAction_NotAShellWideMinimizeAll()
    {
        // The guard against the forbidden shortcut: the only IWindowManager member the action may
        // call is the non-blocking minimise. A regression that reached for the shell (or for a
        // "minimise everything" helper) would show up as a different op, or as a call the empty
        // list cannot explain.
        var windows = new RecordingWindowManager();
        var dispatcher = Build(windows, new TestFakes.FakeTaskSwitcherOverlay());

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage([new IntPtr(7)]));

        Check.That(windows.MinimizedNonBlocking).ContainsExactly(new IntPtr(7));
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

        Check.That(windows.MinimizedNonBlocking).ContainsExactly(new IntPtr(1), new IntPtr(3));
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    [Fact]
    public void HandleShowDesktop_WithNoWindows_StillDismisses()
    {
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var windows = new RecordingWindowManager();
        var dispatcher = Build(windows, switcher);

        dispatcher.HandleShowDesktop(new TaskSwitcherShowDesktopMessage([]));

        Check.That(windows.MinimizedNonBlocking).IsEmpty();
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    /// <summary>
    /// Fixup-01 F3: the message → registration → handler round trip, through the REAL messenger.
    /// The direct-call tests above stay green if the <c>Register</c> line is deleted, so without
    /// this one a deleted registration would leave the tile closing the switcher and minimising
    /// nothing — a silent regression the owner would meet at the gaze check.
    /// </summary>
    [StaFact]
    public void TheDesktopMessage_ReachesTheHandlerThroughTheMessenger()
    {
        var windows = new RecordingWindowManager();
        var switcher = new TestFakes.FakeTaskSwitcherOverlay();
        var dispatcher = Dispatcher.CurrentDispatcher;
        var context = TestFakes.MakeContext(new TestFakes.FakeInputSimulator()) with
        {
            WindowManager = windows,
            OverlayManager = TestFakes.MakeOverlayManager(taskSwitcher: switcher),
            Dispatcher = dispatcher,
        };
        using var coordinator = new QuickActionDispatcher();
        coordinator.Initialize(context);

        // The registration posts to the UI dispatcher, so sending alone must not minimise yet.
        context.Messenger.Send(new TaskSwitcherShowDesktopMessage(
            [new IntPtr(4), new IntPtr(5)]));
        Check.That(windows.MinimizedNonBlocking).IsEmpty();

        Pump(dispatcher);

        Check.That(windows.MinimizedNonBlocking).ContainsExactly(new IntPtr(4), new IntPtr(5));
        Check.That(windows.MinimizedBlocking).IsEmpty();
        Check.That(switcher.HideCalls).IsEqualTo(1);
    }

    /// <summary>
    /// Runs the dispatcher's queued work up to and including a lower-priority continuation. On
    /// this thread <c>Dispatcher.Invoke</c> would run inline and never touch the queue, so a frame
    /// is the only way to execute the posted handler without a real message loop.
    /// </summary>
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
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
    /// minimised, split by which method was used (the blocking and non-blocking members must not be
    /// interchangeable for "show desktop"). <see cref="Throwing"/> makes one handle throw, for the
    /// containment test.
    /// </summary>
    private sealed class RecordingWindowManager : EyeCtrl.Core.Interfaces.IWindowManager
    {
        internal List<IntPtr> MinimizedNonBlocking { get; } = new();
        internal List<IntPtr> MinimizedBlocking { get; } = new();
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

        public void Minimize(IntPtr hwnd) => MinimizedBlocking.Add(hwnd);

        public void MinimizeNonBlocking(IntPtr hwnd)
        {
            if (Throwing == hwnd)
                throw new InvalidOperationException("this fake handle refuses to minimise");
            MinimizedNonBlocking.Add(hwnd);
        }

        public void MaximizeOrRestore(IntPtr hwnd) => OtherOps++;
        public bool CloseWindow(IntPtr hwnd) { OtherOps++; return false; }
    }
}
