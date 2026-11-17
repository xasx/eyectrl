namespace EyeCtrl.Platform.Tests.Services;

using System.Diagnostics;
using System.IO;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// S3-C: the task-switcher pick and the Quick Actions minimise/maximise must never make the UI
/// thread — which is also the gaze-dwell thread — wait on another process.
///
/// <para>
/// The Win32 calls sit behind <see cref="IWindowActionNative"/>, so these tests drive the real
/// <see cref="Win32WindowManager"/> against a fake that can record the exact order, capture the
/// injected ALT events and simulate a window that has stopped answering. No real window is
/// touched and no <c>SendInput</c> is ever called (the fake stands in for it).
/// </para>
/// </summary>
public class Win32WindowManagerActionTests
{
    private static readonly IntPtr Window = new(0x1111);
    private static readonly IntPtr Other = new(0x2222);

    /// <summary>
    /// The 2026-08-18 fix and the 2026-08-25 fix, behaviourally: a minimised target is restored
    /// BEFORE the switch, and the ALT grant is one atomic down+up pair issued before
    /// <c>SetForegroundWindow</c>.
    /// </summary>
    [Fact]
    public void BringToFront_RestoresBeforeForegrounding_WithOneAtomicAltPairFirst()
    {
        var native = new FakeWindowActionNative();
        native.Minimised.Add(Window);
        var (manager, _, logger) = Create(native);

        manager.BringToFront(Window);

        WaitUntil(() => native.Foregrounded.Contains(Window), "the switch");
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "IsWindow", "IsIconic", "ShowWindow", "SendInput", "SetForegroundWindow");
        Check.That(native.ShowWindowCommands).ContainsExactly(NativeMethods.SW_RESTORE);

        // One SendInput call, two events: down then up. Splitting it is the 2026-08-25 stuck-ALT
        // regression; delivering the up after the focus switch orphans the keyup on the new window.
        Check.That(native.SendInputBatches).HasSize(1);
        var pair = native.SendInputBatches[0];
        Check.That(pair).HasSize(2);
        Check.That(pair[0].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
        Check.That(pair[0].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(pair[0].Union.Keyboard.Flags).IsEqualTo(0u);
        Check.That(pair[1].Type).IsEqualTo(NativeMethods.INPUT_KEYBOARD);
        Check.That(pair[1].Union.Keyboard.VirtualKey).IsEqualTo(NativeMethods.VK_MENU);
        Check.That(pair[1].Union.Keyboard.Flags).IsEqualTo(NativeMethods.KEYEVENTF_KEYUP);

        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith("Task switcher brought window ", StringComparison.Ordinal)
            && e.Message.EndsWith(" to the foreground.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-minimised window must not get a pointless restore call: today's behaviour is the ALT
    /// pair and the switch only.
    /// </summary>
    [Fact]
    public void BringToFront_NormalWindow_SkipsTheRestore()
    {
        var (manager, native, _) = Create(new FakeWindowActionNative());

        manager.BringToFront(Window);

        WaitUntil(() => native.Foregrounded.Contains(Window), "the switch");
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "IsWindow", "IsIconic", "SendInput", "SetForegroundWindow");
        Check.That(native.ShowWindowCommands).IsEmpty();
    }

    /// <summary>
    /// A dead tile handle must not spend the ALT grant into whatever holds the foreground: the
    /// IsWindow re-check stays ahead of SendInput (the 2026-08-25 hazard class).
    /// </summary>
    [Fact]
    public void BringToFront_DeadHwnd_InjectsNoAltPair()
    {
        var native = new FakeWindowActionNative();
        native.GoneWindows.Add(Window);
        var (manager, _, logger) = Create(native);

        manager.BringToFront(Window);

        WaitUntil(() => logger.Entries.Any(e =>
            e.Message.Contains("dead hwnd", StringComparison.Ordinal)), "the dead-handle warning");
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly("IsWindow");
        Check.That(native.SendInputBatches).IsEmpty();
        Check.That(native.Foregrounded).IsEmpty();
    }

    /// <summary>
    /// The whole point of the ticket: the UI-thread entry point returns at once even when the
    /// target stops answering inside the restore call.
    /// </summary>
    [Fact]
    public void BringToFront_ReturnsAtOnce_WhenTheTargetStopsAnswering()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        native.Minimised.Add(Window);
        var (manager, _, _) = Create(native);

        var elapsed = Stopwatch.StartNew();
        manager.BringToFront(Window);
        elapsed.Stop();

        Check.WithCustomMessage(
                "BringToFront blocked the caller: a hung target would freeze the gaze thread.")
            .That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(500);
        // The worker really is stuck inside the cross-process restore, not merely slow to start.
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(native.SendInputBatches).IsEmpty();

        native.ReleaseHang.Set();
    }

    /// <summary>
    /// A stuck pick must not block the next one, and when its target finally answers it must NOT
    /// inject its ALT grant into whatever holds the foreground by then. The abandoned pick is
    /// logged.
    /// </summary>
    [Fact]
    public void AStuckPick_IsAbandoned_WithoutBlockingTheNextPick_AndNeverInjectsItsAltLate()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        native.Minimised.Add(Window);
        var (manager, _, logger) = Create(
            native,
            handoffTimeout: TimeSpan.FromMilliseconds(150),
            startDeadline: TimeSpan.FromSeconds(10));

        manager.BringToFront(Window);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        // A following pick on another window must run while the first is still stuck.
        var elapsed = Stopwatch.StartNew();
        manager.BringToFront(Other);
        Check.That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(500);
        WaitUntil(() => native.Foregrounded.Contains(Other), "the second switch");

        // Release the stuck restore: the abandoned pick must stop at its token check.
        native.ReleaseHang.Set();
        Check.That(native.HangExited.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Thread.Sleep(200);

        Check.That(native.SendInputBatches).HasSize(1);
        Check.That(native.Foregrounded).ContainsExactly(Other);
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("was abandoned", StringComparison.Ordinal));
    }

    /// <summary>
    /// The stale-input property when no later action fires the cancellation: the target recovers
    /// on its own, past the 3 s completion budget, and the pick must still not inject its ALT grant.
    /// </summary>
    [Fact]
    public void AStuckPickThatRecoversWithoutASuccessor_StillDropsItsAlt_AfterTheBudget()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        native.Minimised.Add(Window);
        var (manager, _, logger) = Create(native);

        manager.BringToFront(Window);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        // No second action: the target simply answers after the 3 s budget has run out.
        Thread.Sleep(3500);
        native.ReleaseHang.Set();
        Check.That(native.HangExited.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        WaitUntil(
            () => logger.Entries.Any(e => e.Message.Contains("after the request", StringComparison.Ordinal)),
            "the too-late drop line");

        Check.That(native.SendInputBatches).IsEmpty();
        Check.That(native.Foregrounded).IsEmpty();
    }

    /// <summary>
    /// The coordinator's 3 s budget: a restore that answers after 1.5 s is a slow but alive target —
    /// today it still switched, so it must still get the ALT pair and the foreground call.
    /// </summary>
    [Fact]
    public void APickWhoseRestoreAnswersWithinTheBudget_StillGetsItsAltAndSwitch()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        native.Minimised.Add(Window);
        var (manager, _, logger) = Create(native);

        manager.BringToFront(Window);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Thread.Sleep(1500);
        native.ReleaseHang.Set();

        WaitUntil(() => native.Foregrounded.Contains(Window), "the switch after a slow restore");
        Check.That(native.SendInputBatches).HasSize(1);
        Check.That(logger.Entries.Any(e =>
                e.Message.Contains("after the request", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The stale-input property: a pick that cannot start within its deadline is dropped and
    /// logged rather than fired late. Exercised with a hand-off bound longer than the deadline,
    /// so the wait itself consumes the action's whole allowance.
    /// </summary>
    [Fact]
    public void APickThatCannotStartWithinTheDeadline_IsDroppedAndLogged_NotExecutedLate()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        native.Minimised.Add(Window);
        var (manager, _, logger) = Create(
            native,
            handoffTimeout: TimeSpan.FromMilliseconds(1500),
            startDeadline: TimeSpan.FromSeconds(1));

        manager.BringToFront(Window);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        manager.BringToFront(Other);

        WaitUntil(() => logger.Entries.Any(e =>
            e.Message.Contains("was dropped", StringComparison.Ordinal)), "the drop line");
        // Behavioural teeth (F1): the dropped pick must make NO native call at all, not merely
        // skip its ALT. Removing the drop's return would run IsWindow(Other) and the switch.
        Check.That(native.Calls.Where(c => c.Hwnd == Other)).IsEmpty();
        Check.That(native.Foregrounded).IsEmpty();
        Check.That(native.SendInputBatches).IsEmpty();

        native.ReleaseHang.Set();
        Thread.Sleep(200);
        Check.That(native.Calls.Where(c => c.Hwnd == Other)).IsEmpty();
        Check.That(native.SendInputBatches).IsEmpty();
    }

    /// <summary>
    /// Quick Actions minimise: exactly today's <c>SW_MINIMIZE</c>, now posted with
    /// <c>ShowWindowAsync</c> so a hung window can never stall the caller.
    /// </summary>
    [Fact]
    public void Minimize_PostsTodaysOpAsynchronously_WithoutBlockingTheCaller()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        var (manager, _, _) = Create(native);

        var elapsed = Stopwatch.StartNew();
        manager.Minimize(Window);
        elapsed.Stop();

        Check.That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(500);
        // The blocking seam must never be reached: a fake that would hang there proves it.
        Check.That(native.ShowWindowCommands).IsEmpty();
        Check.That(native.HangEntered.IsSet).IsFalse();
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly("IsWindow", "ShowWindowAsync");
        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(NativeMethods.SW_MINIMIZE);
    }

    /// <summary>
    /// F2: MaximizeOrRestore's non-blocking half is pinned too. A fake that would hang inside the
    /// blocking <c>ShowWindow</c> is never reached; the async twin is.
    /// </summary>
    [Fact]
    public void MaximizeOrRestore_DoesTodayOp_WithoutBlockingTheCaller()
    {
        var native = new FakeWindowActionNative { HangInShowWindow = Window };
        var (manager, _, _) = Create(native);

        var elapsed = Stopwatch.StartNew();
        manager.MaximizeOrRestore(Window);
        elapsed.Stop();

        Check.That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(500);
        Check.That(native.ShowWindowCommands).IsEmpty();
        Check.That(native.HangEntered.IsSet).IsFalse();
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "IsWindow", "IsZoomed", "IsIconic", "ShowWindowAsync");
        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(NativeMethods.SW_MAXIMIZE);
    }

    /// <summary>Quick Actions maximise/restore: the same decision as today, posted asynchronously.</summary>
    [Theory]
    [InlineData(false, false, NativeMethods.SW_MAXIMIZE)]
    [InlineData(true, false, NativeMethods.SW_RESTORE)]
    [InlineData(false, true, NativeMethods.SW_RESTORE)]
    public void MaximizeOrRestore_ChoosesTodaysOp(bool zoomed, bool iconic, int expectedCommand)
    {
        var native = new FakeWindowActionNative();
        if (zoomed) native.Maximised.Add(Window);
        if (iconic) native.Minimised.Add(Window);
        var (manager, _, _) = Create(native);

        manager.MaximizeOrRestore(Window);

        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(expectedCommand);
        Check.That(native.ShowWindowCommands).IsEmpty();
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "IsWindow", "IsZoomed", "IsIconic", "ShowWindowAsync");
    }

    /// <summary>
    /// F5: after a failed <c>SetForegroundWindow</c> the fallback's activating <c>SW_RESTORE</c>
    /// must not run once a later pick has abandoned this one — it would take activation away from
    /// the window the owner picked next. The hang is in the fallback's <c>SW_MINIMIZE</c>, after
    /// the ALT grant, so the pick is alive but abandoned at exactly that point.
    /// </summary>
    [Fact]
    public void AFailedPick_DoesNotRunItsFallbackRestore_AfterItWasAbandoned()
    {
        var native = new FakeWindowActionNative
        {
            HangInShowWindow = Window,
            HangInShowWindowCommand = NativeMethods.SW_MINIMIZE,
        };
        native.RefusedForegrounds.Add(Window);
        native.Minimised.Add(Window); // the pre-foreground restore must not hang
        var (manager, _, logger) = Create(
            native,
            handoffTimeout: TimeSpan.FromMilliseconds(150),
            startDeadline: TimeSpan.FromSeconds(10));

        manager.BringToFront(Window);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Message.Contains("falling back to a minimize/restore toggle", StringComparison.Ordinal));

        // A later pick abandons the stuck one and switches instead.
        manager.BringToFront(Other);
        WaitUntil(() => native.Foregrounded.Contains(Other), "the second switch");

        native.ReleaseHang.Set();
        Check.That(native.HangExited.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Thread.Sleep(200);

        // Only the pre-foreground restore and the fallback's minimise ran; the guarded restore did
        // not. Without the guard the abandoned pick adds a third command (SW_RESTORE).
        Check.That(native.ShowWindowCommands)
            .ContainsExactly(NativeMethods.SW_RESTORE, NativeMethods.SW_MINIMIZE);
    }

    /// <summary>
    /// Serialisation: a second pick waits for the first one to finish, so their ALT pairs and
    /// switches never interleave.
    /// </summary>
    [Fact]
    public void TwoPicks_DoNotInterleaveTheirAltPairs()
    {
        var (manager, native, _) = Create(new FakeWindowActionNative());

        manager.BringToFront(Window);
        manager.BringToFront(Other);

        WaitUntil(() => native.Foregrounded.Count == 2, "both switches");
        var calls = native.Calls;
        Check.That(calls.Select(c => c.Name)).ContainsExactly(
            "IsWindow", "IsIconic", "SendInput", "SetForegroundWindow",
            "IsWindow", "IsIconic", "SendInput", "SetForegroundWindow");
        // SendInput carries no HWND; every other call of the first pick names the first window.
        Check.That(calls.Take(4).Where(c => c.Name != "SendInput").Select(c => c.Hwnd))
            .ContainsOnlyElementsThatMatch(h => h == Window);
        Check.That(calls.Skip(4).Where(c => c.Name != "SendInput").Select(c => c.Hwnd))
            .ContainsOnlyElementsThatMatch(h => h == Other);
    }

    /// <summary>
    /// The session-log analysis reads this exact line; the S3-C fix moves the call to a worker and
    /// must not touch the wording.
    /// </summary>
    [Fact]
    public void TheBringToFrontSuccessLine_WordingIsUnchanged()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));
        Check.WithCustomMessage(
                "the success line the session-log analysis reads changed wording or fields.")
            .That(source.Contains(
                "\"Task switcher brought window {Hwnd} to the foreground.\"", StringComparison.Ordinal))
            .IsTrue();
    }

    private static (Win32WindowManager Manager, FakeWindowActionNative Native, RecordingLogger Logger) Create(
        FakeWindowActionNative native,
        TimeSpan? handoffTimeout = null,
        TimeSpan? startDeadline = null,
        TimeSpan? completionBudget = null)
    {
        var logger = new RecordingLogger();
        var scheduler = new WindowActionScheduler(logger, handoffTimeout, startDeadline, completionBudget);
        return (new Win32WindowManager(logger, new NullIntegrityProbe(), native, scheduler), native, logger);
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition())
                return;
            Thread.Sleep(10);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (EyeCtrl.slnx).");
    }

    /// <summary>
    /// Records every Win32 call in order, captures the injected ALT events, and can pretend one
    /// target has stopped answering. Nothing here reaches the OS.
    /// </summary>
    private sealed class FakeWindowActionNative : IWindowActionNative
    {
        private readonly object _gate = new();
        private readonly List<(string Name, IntPtr Hwnd, int Arg)> _calls = new();
        private readonly List<NativeMethods.INPUT[]> _batches = new();
        private readonly List<IntPtr> _foregrounded = new();
        private readonly List<int> _showWindowCommands = new();
        private readonly List<int> _showWindowAsyncCommands = new();
        private IntPtr _foreground;
        private IntPtr _shell;

        internal HashSet<IntPtr> GoneWindows { get; } = new();
        internal HashSet<IntPtr> Minimised { get; } = new();
        internal HashSet<IntPtr> Maximised { get; } = new();

        /// <summary>The window whose ShowWindow call blocks (the fake "hung" target).</summary>
        internal IntPtr? HangInShowWindow { get; init; }

        /// <summary>When set, only this ShowWindow command blocks; other commands return at once.</summary>
        internal int? HangInShowWindowCommand { get; init; }

        /// <summary>Models the foreground lock refusing the switch for these windows.</summary>
        internal HashSet<IntPtr> RefusedForegrounds { get; } = new();

        internal ManualResetEventSlim HangEntered { get; } = new(false);
        internal ManualResetEventSlim HangExited { get; } = new(false);
        internal ManualResetEventSlim ReleaseHang { get; } = new(false);

        internal IReadOnlyList<(string Name, IntPtr Hwnd, int Arg)> Calls
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        internal IReadOnlyList<NativeMethods.INPUT[]> SendInputBatches
        {
            get { lock (_gate) return _batches.ToArray(); }
        }

        internal IReadOnlyList<IntPtr> Foregrounded
        {
            get { lock (_gate) return _foregrounded.ToArray(); }
        }

        internal IReadOnlyList<int> ShowWindowCommands
        {
            get { lock (_gate) return _showWindowCommands.ToArray(); }
        }

        internal IReadOnlyList<int> ShowWindowAsyncCommands
        {
            get { lock (_gate) return _showWindowAsyncCommands.ToArray(); }
        }

        public bool IsWindow(IntPtr hwnd)
        {
            Record("IsWindow", hwnd, 0);
            return !GoneWindows.Contains(hwnd);
        }

        public bool IsIconic(IntPtr hwnd)
        {
            Record("IsIconic", hwnd, 0);
            return Minimised.Contains(hwnd);
        }

        public bool IsZoomed(IntPtr hwnd)
        {
            Record("IsZoomed", hwnd, 0);
            return Maximised.Contains(hwnd);
        }

        public bool ShowWindow(IntPtr hwnd, int command)
        {
            Record("ShowWindow", hwnd, command);
            lock (_gate) _showWindowCommands.Add(command);

            if (hwnd == HangInShowWindow
                && (HangInShowWindowCommand is not { } hangCommand || command == hangCommand))
            {
                HangEntered.Set();
                // Bounded: a mutant that calls this on the test thread fails an elapsed-time
                // assertion instead of hanging the suite until --blame-hang-timeout aborts it.
                if (!ReleaseHang.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("the fake hung window was never released");
                HangExited.Set();
            }

            return true;
        }

        public bool ShowWindowAsync(IntPtr hwnd, int command)
        {
            Record("ShowWindowAsync", hwnd, command);
            lock (_gate) _showWindowAsyncCommands.Add(command);
            return true;
        }

        public uint SendInput(uint inputCount, NativeMethods.INPUT[] inputs, int size)
        {
            Record("SendInput", IntPtr.Zero, checked((int)inputCount));
            lock (_gate) _batches.Add((NativeMethods.INPUT[])inputs.Clone());
            return inputCount;
        }

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            Record("SetForegroundWindow", hwnd, 0);
            lock (_gate) _foregrounded.Add(hwnd);
            if (!RefusedForegrounds.Contains(hwnd))
            {
                lock (_gate) _foreground = hwnd;
                return true;
            }

            return false;
        }

        public IntPtr GetForegroundWindow()
        {
            Record("GetForegroundWindow", IntPtr.Zero, 0);
            lock (_gate) return _foreground;
        }

        public IntPtr GetShellWindow()
        {
            Record("GetShellWindow", IntPtr.Zero, 0);
            lock (_gate) return _shell;
        }

        /// <summary>Models the shell window; zero (none) unless a test sets it.</summary>
        internal IntPtr ShellWindow
        {
            get { lock (_gate) return _shell; }
            set { lock (_gate) _shell = value; }
        }

        /// <summary>Moves the foreground without the action's own calls (the shell's doing).</summary>
        internal void SetForegroundExternally(IntPtr hwnd)
        {
            lock (_gate) _foreground = hwnd;
        }

        private void Record(string name, IntPtr hwnd, int arg)
        {
            lock (_gate) _calls.Add((name, hwnd, arg));
        }
    }

    /// <summary>Thread-safe in-memory log, so the worker's lines can be asserted.</summary>
    private sealed class RecordingLogger : ILogger<Win32WindowManager>
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        internal IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>The escape path needs a probe; the action tests never reach it.</summary>
    private sealed class NullIntegrityProbe : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(null, false, false);

        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectWindow(IntPtr hwnd) => ProcessTokenFacts.Unknown;
    }
}
