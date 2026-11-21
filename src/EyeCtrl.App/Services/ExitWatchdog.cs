namespace EyeCtrl.App.Services;

using System.Globalization;
using System.IO;
using System.Text;
using Serilog;

/// <summary>
/// The bounded safety net for a stuck exit. On 18.11 EyeCtrl 1.9.0 lived about 87 minutes after its
/// own "Shutdown" line and held the single-instance mutex the whole time, so every relaunch exited
/// silently (by design, and without a log line) and stranded an owner who controls the PC only by
/// gaze. Whatever the cause, an exit must never be unbounded.
/// <para>
/// One dedicated <b>background</b> thread waits <see cref="DefaultBudget"/> on the injected clock.
/// When the budget elapses it calls <c>terminate</c> exactly once with the name of the step that is
/// currently running (<see cref="ShutdownSequence.CurrentStep"/>); production's terminate appends one
/// line straight to the log file (Serilog may already be closed) and calls
/// <see cref="Environment.FailFast(string)"/>, which writes an Application-log event and a WER report
/// — so the next occurrence explains itself.
/// </para>
/// <para>
/// The watchdog is deliberately <b>never cancelled by the exit sequence</b>, because the exit does not
/// end when the last step returns: dispatcher and <c>HwndSource</c> teardown, the return from
/// <c>Main</c> and <c>ProcessExit</c> handlers all run afterwards on the UI thread and can hang too.
/// It does <b>not</b> cover DLL detach: <c>ExitProcess</c> terminates every other thread — this one
/// included — before <c>DLL_PROCESS_DETACH</c>, so a hang in ONNX Runtime, DirectML, winmm, Tobii or
/// UIAutomationCore teardown cannot be reported here. That is acceptable: the single-instance mutex is
/// released by the last step, so a detach hang can no longer refuse a relaunch.
/// <see cref="Completed"/> exists only for the headless tests, which cannot let the process die.
/// </para>
/// <para>
/// <b>The mutex is freed before the process is.</b> The single-instance mutex is owned by a keeper
/// thread (<see cref="EyeCtrl.Core.Services.SingleInstanceGuard"/>), and the terminate path signals
/// it — with the guard's bounded wait — BEFORE <see cref="Environment.FailFast(string)"/>. A restart
/// replacement queued on the mutex therefore starts even if the FailFast is delayed by a WER dialog
/// or a dump write, or never completes because a gaze-only owner cannot dismiss the dialog.
/// Closing a handle would not have helped: only <c>ReleaseMutex</c> on the owning thread releases a
/// named mutex.
/// </para>
/// </summary>
internal sealed class ExitWatchdog
{
    /// <summary>How long a shutdown may take before the process is terminated and the step named.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly Func<string> _currentStep;
    private readonly Action<string> _terminate;
    private readonly TimeSpan _budget;

    // The clock's one-shot timer signals the deadline; the dedicated thread does the waiting and the
    // terminate, so the step name is read when the budget actually elapses (not when a timer thread
    // happens to be scheduled, and never on the UI thread).
    private readonly ManualResetEventSlim _budgetElapsed = new(false);
    private readonly ManualResetEventSlim _completed = new(false);
    private ITimer? _timer;
    private int _started;
    private int _terminated;

    /// <summary>Initializes the watchdog. Nothing runs until <see cref="Start"/>.</summary>
    /// <param name="time">The clock; a fake in tests.</param>
    /// <param name="currentStep">Reads the name of the step the exit sequence is inside right now.</param>
    /// <param name="terminate">Called once with that name when the budget elapses.</param>
    /// <param name="budget">Overrides <see cref="DefaultBudget"/> (tests).</param>
    internal ExitWatchdog(TimeProvider time, Func<string> currentStep, Action<string> terminate, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(currentStep);
        ArgumentNullException.ThrowIfNull(terminate);

        _time = time;
        _currentStep = currentStep;
        _terminate = terminate;
        _budget = budget ?? DefaultBudget;
    }

    /// <summary>
    /// Starts the one background thread (idempotent). The timer fires once; the thread is never
    /// joined, because a stuck exit is exactly the case where joining must not happen.
    /// </summary>
    internal void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _timer = _time.CreateTimer(
            static state => ((ExitWatchdog)state!).OnBudgetElapsed(),
            this,
            _budget,
            Timeout.InfiniteTimeSpan);
        new Thread(Run)
        {
            IsBackground = true,
            Name = "EyeCtrl exit watchdog",
        }.Start();
    }

    /// <summary>
    /// Marks the exit as completed so the budget cannot fire. <b>Production must not call this:</b>
    /// the watchdog has to outlive <c>OnExit</c> to cover native teardown, and the process ending is
    /// what really stops it. The headless tests use it to prove the no-call branch.
    /// </summary>
    internal void Completed()
    {
        // Detach and dispose the timer BEFORE signalling, so a clock advanced afterwards cannot fire
        // the deadline at all (the tests advance a fake clock on the same thread that calls this).
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        _completed.Set();
    }

    private void OnBudgetElapsed() => _budgetElapsed.Set();

    private void Run()
    {
        // WaitAny(0 = deadline, 1 = completed). Completed is only ever set by tests; in production
        // this thread parks for the rest of the process's life.
        int signalled = WaitHandle.WaitAny([_budgetElapsed.WaitHandle, _completed.WaitHandle]);
        Interlocked.Exchange(ref _timer, null)?.Dispose();
        if (signalled != 0 || Interlocked.Exchange(ref _terminated, 1) != 0)
            return;

        _terminate(_currentStep());
    }
}

/// <summary>
/// Production's <c>terminate</c> action: the last-resort report for a stuck exit.
/// <para>
/// <b>Two routes, because the two moments differ.</b> While a step hangs, Serilog is still open
/// (<c>App.OnExit</c>'s <c>CloseAndFlush</c> sits in a <c>finally</c> after the step list), and its
/// daily file sink holds the day's file with write access — a direct append to that file fails with
/// <see cref="IOException"/>. So the first route is <c>Log.Fatal</c>, which is synchronous and
/// unbuffered by default: the line is on disk before <see cref="Environment.FailFast(string)"/> runs.
/// After <c>CloseAndFlush</c> the static logger is silent, so the second route appends straight to
/// the day's file — that is the post-step-list stage (dispatcher teardown, <c>Main</c> return).
/// </para>
/// <para>
/// The mutex keeper is then signalled (see <see cref="ExitStuckReport.Terminate(string, string, Action?)"/>),
/// so a queued restart replacement is freed even while this process is in the FailFast. The process is
/// ended with <see cref="Environment.FailFast(string)"/> so Windows records an event and WER can
/// capture a dump. If the keeper could not be reached, or the process dies before it releases, the
/// mutex is abandoned and <c>SingleInstanceGuard.TryAcquire</c> treats that as acquired, so the next
/// relaunch is not refused either way.
/// </para>
/// </summary>
internal static class ExitStuckReport
{
    /// <summary>
    /// How long the Serilog route may take before the direct route is tried too. A hung step can be
    /// inside a Serilog write itself (the sink's own lock), so the primary report is issued from a
    /// short-lived thread with this bound: the line is worth the wait, the process end is not.
    /// </summary>
    internal static readonly TimeSpan LogFatalBudget = TimeSpan.FromSeconds(2);

    /// <summary>Appends the stuck-exit line to the day's log file.</summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    /// <param name="now">The local time (Serilog's daily roll uses local time); injectable for tests.</param>
    internal static void AppendStuckLine(string logsDirectory, string step, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(step);
        AppendLine(logsDirectory, "FTL", $"EyeCtrl exit stuck in step '{step}'", now);
    }

    /// <summary>
    /// Appends the watchdog's one release line to the day's log file — the fallback route for
    /// after <c>Log.CloseAndFlush</c>, when the static logger is silent.
    /// </summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="now">The local time (Serilog's daily roll uses local time); injectable for tests.</param>
    internal static void AppendReleasedLine(string logsDirectory, DateTime now)
        => AppendLine(logsDirectory, "INF", ReleasedMessage, now);

    /// <summary>The one line the watchdog writes after it has signalled the mutex keeper.</summary>
    internal const string ReleasedMessage = "Exit watchdog: released single-instance mutex";

    private static void AppendLine(string logsDirectory, string level, string message, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(logsDirectory);

        // The date is formatted through the invariant culture *explicitly* (never the ambient one):
        // the line is machine-read by the exit-hang investigation, and the file must be the exact
        // one Serilog's daily roll writes (eyectrl-yyyyMMdd.log).
        string stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string line = $"{stamp} [{level}] [{typeof(ExitWatchdog).FullName}] {message}";
        string path = Path.Combine(logsDirectory, $"eyectrl-{day}.log");
        Directory.CreateDirectory(logsDirectory);
        // Same file as Serilog's daily roll; no BOM, because the sink writes UTF-8 without one.
        // This is the fallback for after CloseAndFlush, so it cannot know a size-roll suffix
        // (_001) the closed sink may have used; while the sink is open its own line is used.
        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Reports the stuck exit by both routes (Serilog first, the direct append as the fallback),
    /// best effort, and never throws. Split from <see cref="Terminate"/> so the headless tests can
    /// drive the real reporting without ending the test process.
    /// </summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    internal static void Report(string logsDirectory, string step)
    {
        ArgumentNullException.ThrowIfNull(step);

        // Primary: the open sink (the hung-step stage). A DEDICATED background thread, not the thread
        // pool: a hang that starved the pool must not also lose this line. Bounded, because the hung
        // step may itself hold Serilog's write lock — waiting there forever would defeat the watchdog.
        try
        {
            var logThread = new Thread(() =>
                Log.Fatal("EyeCtrl exit stuck in step '{Step}' — the exit watchdog ended the process", step))
            {
                IsBackground = true,
                Name = "EyeCtrl exit stuck report",
            };
            logThread.Start();
            // Leave a thread that misses the budget to finish or die with the process; the direct
            // route below is the fallback, and nothing here may delay the FailFast further.
            _ = logThread.Join(LogFatalBudget);
        }
        catch
        {
            // A broken sink must not skip the fallback or the FailFast.
        }

        // Fallback: after CloseAndFlush the static logger is silent and the day file is free.
        try
        {
            AppendStuckLine(logsDirectory, step, DateTime.Now);
        }
        catch
        {
            // Locked by the still-open sink, or a full/read-only disk: the FailFast still must run.
        }
    }

    /// <summary>
    /// Reports the line (best effort), signals the mutex keeper so a queued restart replacement is
    /// freed even while the UI thread is hung, then terminates the process. Every stage is
    /// best-effort: <c>FailFast</c> always runs.
    /// </summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    /// <param name="releaseMutex">
    /// Signals the keeper and returns when it has released (or the bounded wait expired).
    /// <see langword="null"/> when no guard was created (a duplicate launch that lost the mutex).
    /// </param>
    internal static void Terminate(string logsDirectory, string step, Action? releaseMutex = null)
        => Terminate(logsDirectory, step, releaseMutex, message => Environment.FailFast(message));

    /// <summary>
    /// The testable core of <see cref="Terminate(string, string, Action?)"/>: file/process effects
    /// are seams, so a HEADLESS test can pin the order — stuck line, keeper release, FailFast.
    /// </summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    /// <param name="releaseMutex">Signals the keeper; <see langword="null"/> when there is no guard.</param>
    /// <param name="failFast">The process-ending action (production: <see cref="Environment.FailFast(string)"/>).</param>
    internal static void Terminate(
        string logsDirectory,
        string step,
        Action? releaseMutex,
        Action<string> failFast)
    {
        ArgumentNullException.ThrowIfNull(failFast);

        try
        {
            Report(logsDirectory, step);
        }
        catch
        {
            // Nothing may skip the FailFast: the process must end even if every reporting route fails.
        }

        // BEFORE the FailFast, and independently of the UI thread: a restart replacement is queued
        // on the mutex, and a WER report/dump or a stuck UI thread must not keep it waiting. The
        // keeper owns the mutex, so this call releases it even though this is not the owner thread.
        if (releaseMutex is not null)
        {
            try
            {
                releaseMutex();
            }
            catch
            {
                // The report of the failure is worth less than the process ending.
            }

            try
            {
                ReportReleased(logsDirectory);
            }
            catch
            {
                // A log line must never delay the FailFast.
            }
        }

        failFast($"EyeCtrl exit stuck in step '{step}'");
    }

    /// <summary>
    /// Logs the one release line by both routes, exactly like <see cref="Report"/>: the open sink
    /// first (bounded, because the hung step may hold Serilog's write lock), then the direct append
    /// for after <c>CloseAndFlush</c>.
    /// </summary>
    private static void ReportReleased(string logsDirectory)
    {
        try
        {
            var logThread = new Thread(() => Log.Information(ReleasedMessage))
            {
                IsBackground = true,
                Name = "EyeCtrl exit release report",
            };
            logThread.Start();
            _ = logThread.Join(LogFatalBudget);
        }
        catch
        {
            // Fall through to the direct append; neither route may block the FailFast.
        }

        try
        {
            AppendReleasedLine(logsDirectory, DateTime.Now);
        }
        catch
        {
            // Locked by the still-open sink, or a full/read-only disk: the FailFast still must run.
        }
    }
}
