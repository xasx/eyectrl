namespace EyeCtrl.App.Services;

using System.Globalization;
using System.IO;
using System.Text;

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
/// The watchdog is deliberately <b>never cancelled by the exit sequence</b>: it must also cover
/// native teardown after <c>App.OnExit</c> returns (DLL detach of ONNX Runtime/DirectML, winmm,
/// Tobii, UIAutomationCore), which can hang just as well. Its thread is background and dies with the
/// process when the process actually ends; <see cref="Completed"/> exists only for the headless
/// tests, which cannot let the process die.
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
/// Production's <c>terminate</c> action: the last-resort report for a stuck exit. Serilog's sink may
/// already be closed (or was never opened), so the line is appended straight to the day's log file;
/// the process is then ended with <see cref="Environment.FailFast(string)"/> so Windows records an
/// event and WER can capture a dump. A killed holder abandons the single-instance mutex, which
/// <c>SingleInstanceGuard.TryAcquire</c> treats as acquired, so the next relaunch is not refused.
/// </summary>
internal static class ExitStuckReport
{
    /// <summary>Appends the stuck-exit line to the day's log file.</summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    /// <param name="now">The local time (Serilog's daily roll uses local time); injectable for tests.</param>
    internal static void AppendStuckLine(string logsDirectory, string step, DateTime now)
    {
        ArgumentException.ThrowIfNullOrEmpty(logsDirectory);
        ArgumentNullException.ThrowIfNull(step);

        string line = string.Create(
            CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff} [FTL] [{typeof(ExitWatchdog).FullName}] EyeCtrl exit stuck in step '{step}'");
        string path = Path.Combine(logsDirectory, $"eyectrl-{now:yyyyMMdd}.log");
        Directory.CreateDirectory(logsDirectory);
        // Same file as Serilog's daily roll; no BOM, because the sink writes UTF-8 without one.
        File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Appends the line (best effort) and then terminates the process.</summary>
    /// <param name="logsDirectory">The directory <c>LoggingInitializer</c> writes to.</param>
    /// <param name="step">The name of the step that never returned.</param>
    internal static void Terminate(string logsDirectory, string step)
    {
        try
        {
            AppendStuckLine(logsDirectory, step, DateTime.Now);
        }
        catch
        {
            // A full or read-only disk must not skip the FailFast: the process must still end.
        }

        Environment.FailFast($"EyeCtrl exit stuck in step '{step}'");
    }
}
