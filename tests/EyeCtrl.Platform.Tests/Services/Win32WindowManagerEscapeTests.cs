namespace EyeCtrl.Platform.Tests.Services;

using System.Diagnostics;
using System.IO;
using EyeCtrl.Core.Input;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Models;
using EyeCtrl.Platform.Input;
using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging;

/// <summary>
/// C10's R4 path, F-ESC: the escape must bring a window forward WITHOUT injecting input, and it
/// must never make the caller — the UI dispatcher, which is also the gaze-dwell thread — wait on
/// the target. The window work runs on <see cref="WindowActionScheduler"/>; every show is posted
/// with <c>ShowWindowAsync</c>; the foreground checks and the fallback poll run on the worker.
///
/// <para>
/// The Win32 calls sit behind <see cref="IWindowActionNative"/>, so these tests drive the real
/// <see cref="Win32WindowManager"/> against a fake that records the exact order, models a target
/// that has stopped answering, and counts any <c>SendInput</c>. No real window is touched and no
/// <c>SendInput</c> is ever called.
/// </para>
///
/// <para>
/// The static wording the sitting analysis reads is pinned at the source level, exactly as it was
/// before the asynchronous seam: the four escape lines and the coordinator's summary keep their
/// fields.
/// </para>
/// </summary>
public class Win32WindowManagerEscapeTests
{
    private static readonly IntPtr Window = new(0x1111);
    private static readonly IntPtr Other = new(0x2222);

    /// <summary>
    /// The whole point of the ticket: the entry point returns at once even while the target is
    /// stuck inside a show call, and the attempt gives up at the completion budget with exactly one
    /// Warning.
    /// </summary>
    [Fact]
    public void BeginBringToFront_ReturnsAtOnce_WhenTheTargetStopsAnswering_AndTimesOutWithOneWarning()
    {
        var native = new FakeEscapeNative { HangInShowWindowAsync = Window };
        native.Minimised.Add(Window);
        var (manager, logger) = Create(
            native,
            startDeadline: TimeSpan.FromSeconds(10),
            completionBudget: TimeSpan.FromMilliseconds(150));

        var callerThread = Environment.CurrentManagedThreadId;
        EscapeWindowResult? result = null;
        var completed = new ManualResetEventSlim();
        var elapsed = Stopwatch.StartNew();
        manager.BeginBringToFront(Window, r =>
        {
            result = r;
            completed.Set();
        });
        elapsed.Stop();

        Check.WithCustomMessage(
                "BeginBringToFront blocked the caller: a hung target would freeze the gaze thread.")
            .That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(500);
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        // Past the budget, the target finally answers: the attempt must stop and say so once.
        Thread.Sleep(250);
        native.ReleaseHang.Set();
        Check.That(completed.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        Check.That(result.HasValue).IsFalse();
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("was abandoned after", StringComparison.Ordinal))).IsEqualTo(1);
        Check.WithCustomMessage("a window call ran on the caller's thread.")
            .That(native.CallThreadIds.Where(id => id == callerThread)).IsEmpty();
    }

    /// <summary>
    /// R1/P1: the realistic hung-window model — <c>ShowWindowAsync</c> returns at once and the
    /// target's thread never processes the restore, so <c>IsIconic</c> stays true for good. The
    /// attempt must end at the budget (one completion, one Warning, one foreground try) and leave
    /// the worker free; a restore poll that ignores the budget would spin here forever (mutant X1).
    /// </summary>
    [Fact]
    public void AHungIconicTarget_WhoseRestoreIsNeverProcessed_GivesUpAtTheBudget()
    {
        var native = new FakeEscapeNative { IgnoreShowWindowAsync = true };
        native.Minimised.Add(Window);
        native.RefuseForeground(Window);
        var (manager, logger) = Create(native, completionBudget: TimeSpan.FromMilliseconds(200));

        EscapeWindowResult? result = null;
        var completions = 0;
        var completed = new ManualResetEventSlim();
        var elapsed = Stopwatch.StartNew();
        manager.BeginBringToFront(Window, r =>
        {
            result = r;
            Interlocked.Increment(ref completions);
            completed.Set();
        });

        Check.WithCustomMessage(
                "a hung minimised target kept the worker polling past its budget (X1).")
            .That(completed.Wait(TimeSpan.FromSeconds(2))).IsTrue();
        elapsed.Stop();

        Check.That(elapsed.ElapsedMilliseconds).IsStrictlyLessThan(2000);
        Check.That(completions).IsEqualTo(1);
        Check.That(result.HasValue).IsFalse();
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("was abandoned after", StringComparison.Ordinal))).IsEqualTo(1);
        // F2: the restore timed out while iconic, so exactly one non-injecting foreground try.
        Check.That(native.Calls.Count(c => c.Name == "SetForegroundWindow" && c.Hwnd == Window))
            .IsEqualTo(1);
        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(NativeMethods.SW_RESTORE);
        Check.That(native.SendInputCalls).IsEqualTo(0u);

        // The worker is free afterwards: nothing keeps polling once the attempt completed.
        var callsAtCompletion = native.Calls.Count;
        Thread.Sleep(100);
        Check.WithCustomMessage("the worker was still polling after the attempt completed.")
            .That(native.Calls.Count).IsEqualTo(callsAtCompletion);
    }

    /// <summary>
    /// F2: when the restore poll times out while the target is still iconic, exactly one
    /// non-blocking <c>SetForegroundWindow</c> is still issued, after the budget. If Windows accepts
    /// it, the existing success line and result fields report the foreground change; no fallback
    /// runs and nothing is injected.
    /// </summary>
    [Fact]
    public void AHungIconicTarget_AfterTheRestoreBudget_StillGetsOneSetForegroundWindow()
    {
        var native = new FakeEscapeNative { IgnoreShowWindowAsync = true };
        native.Minimised.Add(Window);
        var (manager, logger) = Create(native, completionBudget: TimeSpan.FromMilliseconds(200));

        var (result, completions) = RunToCompletion(manager, Window);

        Check.That(completions).IsEqualTo(1);
        Check.That(result.HasValue).IsTrue();
        Check.That(result!.Value).IsEqualTo(new EscapeWindowResult(true, true, false));
        Check.That(native.Calls.Count(c => c.Name == "SetForegroundWindow" && c.Hwnd == Window))
            .IsEqualTo(1);
        Check.WithCustomMessage("the foreground try was not issued after the restore budget.")
            .That(native.ElapsedAt("SetForegroundWindow", Window)).IsGreaterOrEqualThan(150L);
        Check.That(native.Calls.Count(c => c.Name == "IsIconic" && c.Hwnd == Window))
            .IsStrictlyGreaterThan(5);
        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(NativeMethods.SW_RESTORE);
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith("Blocked-input escape: brought window ", StringComparison.Ordinal)
            && e.Message.EndsWith(" to the foreground without injecting input.", StringComparison.Ordinal));
        Check.That(logger.Entries.Any(e =>
                e.Message.Contains("was abandoned after", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// The happy path, through the async completion: the same result fields and the same
    /// Information line the synchronous path wrote, with the restore posted before the switch.
    /// </summary>
    [Fact]
    public void BeginBringToFront_TheHappyPath_ReportsTodaysFieldsAndInformationLine()
    {
        var native = new FakeEscapeNative();
        native.Minimised.Add(Window);
        var (manager, logger) = Create(native);

        var (result, completions) = RunToCompletion(manager, Window);

        Check.That(completions).IsEqualTo(1);
        Check.That(result.HasValue).IsTrue();
        Check.That(result!.Value).IsEqualTo(new EscapeWindowResult(true, true, false));
        Check.That(native.ShowWindowAsyncCommands).ContainsExactly(NativeMethods.SW_RESTORE);
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "GetShellWindow", "IsIconic", "ShowWindowAsync", "IsIconic",
            "SetForegroundWindow", "GetForegroundWindow");
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Information
            && e.Message.StartsWith("Blocked-input escape: brought window ", StringComparison.Ordinal)
            && e.Message.EndsWith(" to the foreground without injecting input.", StringComparison.Ordinal)
            && e.Message.Contains("notepad.exe", StringComparison.Ordinal));
    }

    /// <summary>
    /// Shell target: <c>SetForegroundWindow</c> only. Minimising and restoring the desktop's
    /// Progman window is untried, so the fallback must never run there.
    /// </summary>
    [Fact]
    public void BeginBringToFront_ShellTarget_NeverPostsShowWindow()
    {
        var native = new FakeEscapeNative { ShellWindow = Window };
        native.RefuseForeground(Window);
        var (manager, logger) = Create(native);

        var (result, _) = RunToCompletion(manager, Window);

        Check.That(result.HasValue).IsTrue();
        Check.That(result!.Value).IsEqualTo(new EscapeWindowResult(false, false, false));
        Check.That(native.ShowWindowAsyncCommands).IsEmpty();
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "GetShellWindow", "SetForegroundWindow", "GetForegroundWindow");
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("no minimize/restore is attempted on the shell", StringComparison.Ordinal));
    }

    /// <summary>
    /// The non-injecting fallback keeps its meaning when the calls are posted: minimise then
    /// restore, in that order, and the foreground is checked on the worker afterwards.
    /// </summary>
    [Fact]
    public void BeginBringToFront_RefusedForeground_RunsTheMinimizeThenRestoreFallback_InOrder()
    {
        var native = new FakeEscapeNative { ForegroundOnRestore = true };
        native.RefuseForeground(Window);
        var (manager, logger) = Create(native);

        var (result, _) = RunToCompletion(manager, Window);

        Check.That(result.HasValue).IsTrue();
        Check.That(result!.Value).IsEqualTo(new EscapeWindowResult(true, false, true));
        Check.That(native.ShowWindowAsyncCommands)
            .ContainsExactly(NativeMethods.SW_MINIMIZE, NativeMethods.SW_RESTORE);
        Check.That(native.Calls.Select(c => c.Name)).ContainsExactly(
            "GetShellWindow", "IsIconic", "SetForegroundWindow", "GetForegroundWindow",
            "ShowWindowAsync", "ShowWindowAsync", "GetForegroundWindow");
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("the minimize/restore fallback brought it forward", StringComparison.Ordinal));
    }

    /// <summary>
    /// A target that still refuses after the fallback ends in today's failure warning, after the
    /// short bounded poll — never a stuck worker.
    /// </summary>
    [Fact]
    public void BeginBringToFront_FallbackStillRefused_LogsTodaysFailureWarning()
    {
        var native = new FakeEscapeNative();
        native.RefuseForeground(Window);
        var (manager, logger) = Create(
            native, escapeFallbackWait: TimeSpan.FromMilliseconds(50));

        var (result, _) = RunToCompletion(manager, Window);

        Check.That(result.HasValue).IsTrue();
        Check.That(result!.Value).IsEqualTo(new EscapeWindowResult(false, false, true));
        Check.That(native.ShowWindowAsyncCommands)
            .ContainsExactly(NativeMethods.SW_MINIMIZE, NativeMethods.SW_RESTORE);
        Check.That(logger.Entries).HasElementThatMatches(e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("neither SetForegroundWindow", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two escape requests never run at once: the second waits for the scheduler's hand-off bound,
    /// abandons the stuck first attempt (one Warning), and only then touches its own window.
    /// </summary>
    [Fact]
    public void TwoEscapeRequests_DoNotRunConcurrently_AndTheStuckOneIsAbandonedOnce()
    {
        var native = new FakeEscapeNative { HangInShowWindowAsync = Window };
        native.Minimised.Add(Window);
        var (manager, logger) = Create(
            native,
            handoffTimeout: TimeSpan.FromMilliseconds(300),
            startDeadline: TimeSpan.FromSeconds(10));

        EscapeWindowResult? firstResult = null;
        var firstCompleted = new ManualResetEventSlim();
        manager.BeginBringToFront(Window, r =>
        {
            firstResult = r;
            firstCompleted.Set();
        });
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        EscapeWindowResult? secondResult = null;
        var secondCompleted = new ManualResetEventSlim();
        manager.BeginBringToFront(Other, r =>
        {
            secondResult = r;
            secondCompleted.Set();
        });

        // Inside the hand-off bound the second attempt has not touched its target yet.
        Thread.Sleep(100);
        Check.WithCustomMessage("two escape attempts ran concurrently instead of serialised.")
            .That(native.Calls.Where(c => c.Hwnd == Other)).IsEmpty();

        Check.That(secondCompleted.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(secondResult.HasValue).IsTrue();
        Check.That(secondResult!.Value.ForegroundChanged).IsTrue();

        native.ReleaseHang.Set();
        Check.That(firstCompleted.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(firstResult.HasValue).IsFalse();
        Check.That(native.SendInputCalls).IsEqualTo(0u);
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("was abandoned", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// R1/P6: a request that cannot start within the start deadline is still reported back — the
    /// completion fires exactly once with no result — and never touches its target. Without the
    /// scheduler's drop completion this test waits forever (mutant X4).
    /// </summary>
    [Fact]
    public void AnEscapeDroppedAtTheStartDeadline_CompletesOnceWithNoResult()
    {
        var native = new FakeEscapeNative { HangInShowWindowAsync = Window };
        native.Minimised.Add(Window);
        var (manager, logger) = Create(
            native,
            handoffTimeout: TimeSpan.FromMilliseconds(300),
            startDeadline: TimeSpan.FromMilliseconds(100),
            completionBudget: TimeSpan.FromSeconds(10));

        manager.BeginBringToFront(Window, _ => { });
        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();

        EscapeWindowResult? droppedResult = new EscapeWindowResult(true, true, true);
        var completions = 0;
        var completed = new ManualResetEventSlim();
        manager.BeginBringToFront(Other, r =>
        {
            droppedResult = r;
            Interlocked.Increment(ref completions);
            completed.Set();
        });

        Check.WithCustomMessage("a request dropped at the start deadline never completed (X4).")
            .That(completed.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Thread.Sleep(200);
        native.ReleaseHang.Set();

        Check.That(completions).IsEqualTo(1);
        Check.That(droppedResult.HasValue).IsFalse();
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("was dropped", StringComparison.Ordinal))).IsEqualTo(1);
        Check.That(native.Calls.Where(c => c.Hwnd == Other)).IsEmpty();
        Check.That(native.SendInputCalls).IsEqualTo(0u);
    }

    /// <summary>
    /// F3: the fallback poll (300 ms) is shorter than the scheduler's hand-off bound (500 ms), so a
    /// responsive target that refuses the foreground finishes its normal failure path before an
    /// action queued behind it can abandon it as "target stopped answering". Without the shorter
    /// bound the two lines contradict each other roughly half the time.
    /// </summary>
    [Fact]
    public void APickQueuedDuringTheFallbackPoll_LandsWithoutASpuriousAbandonment()
    {
        var native = new FakeEscapeNative();
        native.RefuseForeground(Window);
        var (manager, logger) = Create(native); // production bounds

        EscapeWindowResult? escapeResult = null;
        var escapeDone = new ManualResetEventSlim();
        manager.BeginBringToFront(Window, r =>
        {
            escapeResult = r;
            escapeDone.Set();
        });

        // Both fallback posts are out: the escape is now inside its foreground poll.
        var pollStarted = Stopwatch.StartNew();
        while (native.ShowWindowAsyncCommands.Count < 2
               && pollStarted.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Check.That(native.ShowWindowAsyncCommands.Count).IsEqualTo(2);

        var pickRequested = Stopwatch.StartNew();
        manager.BringToFront(Other);
        Check.That(escapeDone.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        WaitUntil(
            () => native.Calls.Any(c => c.Name == "SetForegroundWindow" && c.Hwnd == Other),
            "the pick's switch");
        pickRequested.Stop();

        Check.That(escapeResult.HasValue).IsTrue();
        Check.That(escapeResult!.Value).IsEqualTo(new EscapeWindowResult(false, false, true));
        Check.WithCustomMessage("the pick was held up behind the escape's fallback poll.")
            .That(pickRequested.ElapsedMilliseconds).IsStrictlyLessThan(1000);
        Check.WithCustomMessage(
                "the escape was reported as 'target stopped answering' while it was answering.")
            .That(logger.Entries.Any(e =>
                e.Message.Contains("was abandoned", StringComparison.Ordinal))).IsFalse();
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("neither SetForegroundWindow", StringComparison.Ordinal))).IsEqualTo(1);
    }

    /// <summary>
    /// F4: once the minimise has been posted, the restore is always posted too. Here the fake
    /// stalls the minimise past the budget, so the attempt goes stale exactly inside the pair; the
    /// owner's window must still end up restored rather than left minimised.
    /// </summary>
    [Fact]
    public void AFallbackThatGoesStaleBetweenTheTwoPosts_StillRestoresTheWindow()
    {
        var native = new FakeEscapeNative
        {
            HangInShowWindowAsync = Window,
            HangInShowWindowCommand = NativeMethods.SW_MINIMIZE,
        };
        native.RefuseForeground(Window);
        var (manager, logger) = Create(native, completionBudget: TimeSpan.FromMilliseconds(150));

        EscapeWindowResult? result = null;
        var completed = new ManualResetEventSlim();
        manager.BeginBringToFront(Window, r =>
        {
            result = r;
            completed.Set();
        });

        Check.That(native.HangEntered.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Thread.Sleep(250); // past the budget, inside the pair
        native.ReleaseHang.Set();

        Check.That(completed.Wait(TimeSpan.FromSeconds(5))).IsTrue();
        Check.That(native.ShowWindowAsyncCommands)
            .ContainsExactly(NativeMethods.SW_MINIMIZE, NativeMethods.SW_RESTORE);
        Check.That(native.Minimised.Contains(Window)).IsFalse();
        Check.That(result.HasValue).IsFalse();
        Check.That(logger.Entries.Count(e =>
                e.Level == LogLevel.Warning
                && e.Message.Contains("was abandoned after", StringComparison.Ordinal))).IsEqualTo(1);
        Check.That(native.SendInputCalls).IsEqualTo(0u);
    }

    /// <summary>
    /// The source pins the behaviour cannot reach: every foreign-window show is the async form,
    /// nothing in the escape path injects input, and the shell short-circuits before any fallback.
    /// </summary>
    [Fact]
    public void TheEscapeImplementation_IsNonBlocking_NonInjecting_AndShellOnly()
    {
        var source = Source();
        var attempt = ExtractMethod(
            source, "private EscapeWindowResult? RunEscapeAttempt(IntPtr hwnd, WindowActionContext action)");
        var begin = ExtractMethod(
            source, "public void BeginBringToFront(IntPtr hwnd, Action<EscapeWindowResult?> onCompleted)");

        Check.WithCustomMessage("the escape path no longer calls SetForegroundWindow.")
            .That(attempt.Contains("_native.SetForegroundWindow")).IsTrue();
        Check.WithCustomMessage("the escape path lost its non-injecting minimize/restore fallback.")
            .That(attempt.Contains("SW_MINIMIZE") && attempt.Contains("SW_RESTORE")).IsTrue();
        Check.WithCustomMessage(
                "the escape path must not inject ANY input: the injecting BringToFront path is "
                + "silently dropped by the very UIPI block this path exists to get out of.")
            .That(attempt.Contains("SendInput")).IsFalse();
        Check.WithCustomMessage(
                "the escape path delegates to the injecting task-switcher path (it must not: its ALT "
                + "grant is dropped under the block).")
            .That(attempt.Contains("BringToFront(")).IsFalse();
        Check.WithCustomMessage(
                "a synchronous ShowWindow is back in the escape path: it waits on the target's thread, "
                + "so one hung window pins the worker through the state change.")
            .That(attempt.Contains("ShowWindow(")).IsFalse();
        Check.WithCustomMessage("an escape show no longer posts ShowWindowAsync.")
            .That(attempt.Contains("ShowWindowAsync")).IsTrue();
        var shellBranch = attempt.IndexOf("if (isShell)", StringComparison.Ordinal);
        var fallback = attempt.IndexOf("SW_MINIMIZE", StringComparison.Ordinal);
        Check.WithCustomMessage("the shell must short-circuit BEFORE any minimize/restore fallback.")
            .That(shellBranch).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the shell must short-circuit BEFORE any minimize/restore fallback.")
            .That(shellBranch < fallback).IsTrue();

        Check.WithCustomMessage("the escape entry point no longer goes through the scheduler.")
            .That(begin.Contains("_actions.Enqueue")).IsTrue();
        Check.WithCustomMessage("the escape entry point became synchronous again.")
            .That(begin.Contains("completion => onCompleted")).IsTrue();
    }

    /// <summary>
    /// The sitting analysis reads these exact lines. The async seam must not touch wording or
    /// fields.
    /// </summary>
    [Fact]
    public void TheEscapeLogLines_KeepTheirWordingAndFields()
    {
        var source = Source();
        string[] pinned =
        [
            "\"Blocked-input escape: brought window {Hwnd} ({Process}) to the foreground without injecting input.\"",
            "\"Blocked-input escape: SetForegroundWindow returned {SetForeground} for the desktop shell \"",
            "\"Blocked-input escape: SetForegroundWindow returned {SetForeground} for {Hwnd} ({Process}); \"",
            "\"Blocked-input escape: neither SetForegroundWindow ({SetForeground}) nor the \"",
        ];

        foreach (var line in pinned)
        {
            Check.WithCustomMessage($"the escape log line changed or disappeared: {line}")
                .That(source.Contains(line, StringComparison.Ordinal)).IsTrue();
        }

        var coordinator = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.App", "Services", "BlockedInputCoordinator.cs"));
        Check.WithCustomMessage("the coordinator's escape summary line changed wording or fields.")
            .That(coordinator.Contains(
                "\"Blocked-input escape: window {Hwnd} foregroundChanged={ForegroundChanged} \"",
                StringComparison.Ordinal)).IsTrue();
        Check.WithCustomMessage("the coordinator's escape summary line lost its fields.")
            .That(coordinator.Contains(
                "hwnd, escapeResult.ForegroundChanged, escapeResult.SetForegroundWindowSucceeded,",
                StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// The escape target selection is unchanged and still runs before the asynchronous attempt.
    /// </summary>
    [Fact]
    public void TryGetEscapeTarget_UsesThePureSelectionRule_AndTheShellAsFallback()
    {
        var body = ExtractMethod(Source(), "public IntPtr? TryGetEscapeTarget()");

        Check.WithCustomMessage("the escape target is chosen without the pure Z-order/own/IL rule.")
            .That(body.Contains("EscapeTargetRules.SelectTopmost")).IsTrue();
        Check.WithCustomMessage("the desktop/shell is no longer the last-resort target.")
            .That(body.Contains("GetShellWindow")).IsTrue();
    }

    /// <summary>
    /// The production shim must forward the two new reads to the real P/Invokes; a fake-only seam
    /// would leave the foreground poll reading a constant.
    /// </summary>
    [Fact]
    public void Win32WindowActionNative_ForwardsTheForegroundAndShellReads()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "EyeCtrl.Platform", "Services", "Win32WindowActionNative.cs"));

        Check.That(source.Contains(
            "public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();",
            StringComparison.Ordinal)).IsTrue();
        Check.That(source.Contains(
            "public IntPtr GetShellWindow() => NativeMethods.GetShellWindow();",
            StringComparison.Ordinal)).IsTrue();
    }

    private static (EscapeWindowResult? Result, int Completions) RunToCompletion(
        Win32WindowManager manager, IntPtr hwnd)
    {
        EscapeWindowResult? result = null;
        var completions = 0;
        var completed = new ManualResetEventSlim();
        manager.BeginBringToFront(hwnd, r =>
        {
            result = r;
            Interlocked.Increment(ref completions);
            completed.Set();
        });

        Check.WithCustomMessage("the escape completion never fired.")
            .That(completed.Wait(TimeSpan.FromSeconds(10))).IsTrue();
        // A second completion would have to race this read; the scheduler fires it once.
        return (result, completions);
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

    private static (Win32WindowManager Manager, RecordingLogger Logger) Create(
        FakeEscapeNative native,
        TimeSpan? handoffTimeout = null,
        TimeSpan? startDeadline = null,
        TimeSpan? completionBudget = null,
        TimeSpan? escapeFallbackWait = null)
    {
        var logger = new RecordingLogger();
        var scheduler = new WindowActionScheduler(logger, handoffTimeout, startDeadline, completionBudget);
        return (
            new Win32WindowManager(
                logger, new NamedIntegrityProbe(), native, scheduler, escapeFallbackWait),
            logger);
    }

    private static string Source() => File.ReadAllText(Path.Combine(
        RepoRoot(), "src", "EyeCtrl.Platform", "Services", "Win32WindowManager.cs"));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (EyeCtrl.slnx).");
    }

    /// <summary>Returns one method's source text by brace matching from its signature.</summary>
    private static string ExtractMethod(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"The repository source no longer contains '{signature}'.")
            .That(start).IsStrictlyGreaterThan(-1);

        var open = source.IndexOf('{', start);
        Check.That(open).IsStrictlyGreaterThan(-1);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    /// <summary>
    /// Records every Win32 call with its thread, models the async show's effect on the window
    /// state, and can pretend one target has stopped answering inside a call. Nothing here reaches
    /// the OS.
    /// </summary>
    private sealed class FakeEscapeNative : IWindowActionNative
    {
        private readonly object _gate = new();
        private readonly List<(string Name, IntPtr Hwnd, int Arg)> _calls = new();
        private readonly List<int> _callThreadIds = new();
        private readonly List<long> _callAtMs = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<int> _showWindowAsyncCommands = new();
        private IntPtr _foreground;
        private IntPtr _shell;
        private uint _sendInputCalls;

        internal HashSet<IntPtr> Minimised { get; } = new();

        private readonly HashSet<IntPtr> _refusedForegrounds = new();

        /// <summary>The window whose async show blocks (the fake "hung" target).</summary>
        internal IntPtr? HangInShowWindowAsync { get; init; }

        /// <summary>When set, only this show command blocks; other commands return at once.</summary>
        internal int? HangInShowWindowCommand { get; init; }

        /// <summary>Models the shell honouring the fallback's <c>SW_RESTORE</c> by foregrounding it.</summary>
        internal bool ForegroundOnRestore { get; init; }

        /// <summary>
        /// Models a hung target realistically: <c>ShowWindowAsync</c> returns at once but the
        /// target's thread never processes the request, so <c>IsIconic</c> stays true and the
        /// window state never changes.
        /// </summary>
        internal bool IgnoreShowWindowAsync { get; init; }

        internal IntPtr ShellWindow
        {
            get { lock (_gate) return _shell; }
            set { lock (_gate) _shell = value; }
        }

        internal ManualResetEventSlim HangEntered { get; } = new(false);
        internal ManualResetEventSlim ReleaseHang { get; } = new(false);

        internal IReadOnlyList<(string Name, IntPtr Hwnd, int Arg)> Calls
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        internal IReadOnlyList<int> CallThreadIds
        {
            get { lock (_gate) return _callThreadIds.ToArray(); }
        }

        internal IReadOnlyList<int> ShowWindowAsyncCommands
        {
            get { lock (_gate) return _showWindowAsyncCommands.ToArray(); }
        }

        internal uint SendInputCalls
        {
            get { lock (_gate) return _sendInputCalls; }
        }

        /// <summary>When the first matching call was made, in ms since this fake was created.</summary>
        internal long ElapsedAt(string name, IntPtr hwnd)
        {
            lock (_gate)
            {
                for (var i = 0; i < _calls.Count; i++)
                {
                    if (_calls[i].Name == name && _calls[i].Hwnd == hwnd)
                        return _callAtMs[i];
                }
            }

            throw new InvalidOperationException($"No {name} call for {hwnd} was recorded.");
        }

        /// <summary>Models the foreground lock refusing the switch for this window.</summary>
        internal void RefuseForeground(IntPtr hwnd)
        {
            lock (_gate) _refusedForegrounds.Add(hwnd);
        }

        public bool IsWindow(IntPtr hwnd)
        {
            Record("IsWindow", hwnd);
            return true;
        }

        public bool IsIconic(IntPtr hwnd)
        {
            Record("IsIconic", hwnd);
            lock (_gate) return Minimised.Contains(hwnd);
        }

        public bool IsZoomed(IntPtr hwnd)
        {
            Record("IsZoomed", hwnd);
            return false;
        }

        public bool ShowWindow(IntPtr hwnd, int command)
        {
            Record("ShowWindow", hwnd, command);
            return true;
        }

        public bool ShowWindowAsync(IntPtr hwnd, int command)
        {
            Record("ShowWindowAsync", hwnd, command);
            lock (_gate) _showWindowAsyncCommands.Add(command);

            if (hwnd == HangInShowWindowAsync
                && (HangInShowWindowCommand is not { } hangCommand || command == hangCommand))
            {
                HangEntered.Set();
                // Bounded: a mutant that waits on this from the test thread fails an elapsed-time
                // assertion instead of hanging the suite until --blame-hang-timeout aborts it.
                if (!ReleaseHang.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("the fake hung window was never released");
            }

            lock (_gate)
            {
                if (IgnoreShowWindowAsync)
                    return true;

                if (command == NativeMethods.SW_RESTORE)
                {
                    Minimised.Remove(hwnd);
                    if (ForegroundOnRestore)
                        _foreground = hwnd;
                }
                else if (command == NativeMethods.SW_MINIMIZE)
                {
                    Minimised.Add(hwnd);
                }
            }

            return true;
        }

        public uint SendInput(uint inputCount, NativeMethods.INPUT[] inputs, int size)
        {
            Record("SendInput", IntPtr.Zero, checked((int)inputCount));
            lock (_gate) _sendInputCalls++;
            return inputCount;
        }

        public bool SetForegroundWindow(IntPtr hwnd)
        {
            Record("SetForegroundWindow", hwnd);
            lock (_gate)
            {
                if (_refusedForegrounds.Contains(hwnd))
                    return false;

                _foreground = hwnd;
                return true;
            }
        }

        public IntPtr GetForegroundWindow()
        {
            Record("GetForegroundWindow", IntPtr.Zero);
            lock (_gate) return _foreground;
        }

        public IntPtr GetShellWindow()
        {
            Record("GetShellWindow", IntPtr.Zero);
            lock (_gate) return _shell;
        }

        private void Record(string name, IntPtr hwnd, int arg = 0)
        {
            lock (_gate)
            {
                _calls.Add((name, hwnd, arg));
                _callThreadIds.Add(Environment.CurrentManagedThreadId);
                _callAtMs.Add(_clock.ElapsedMilliseconds);
            }
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

    /// <summary>The escape path names the target process in its diagnostics; the tests pin it.</summary>
    private sealed class NamedIntegrityProbe : IInputIntegrityProbe
    {
        public SelfTokenFacts ReadSelf() => new(null, false, false);

        public ProcessTokenFacts InspectForeground() => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectPoint(PhysicalPoint point) => ProcessTokenFacts.Unknown;

        public ProcessTokenFacts InspectWindow(IntPtr hwnd)
            => new("notepad.exe", 0x2000, Elevated: false, HasUIAccess: false, AccessDenied: false);
    }
}
