namespace EyeCtrl.App.Services;

using System;
using System.Diagnostics;

/// <summary>The child process a restart launched, narrowed to what the parent needs.</summary>
public interface IRestartChild
{
    /// <summary>True if the process has already exited.</summary>
    bool HasExited { get; }

    /// <summary>Terminates the process. Used only to clean up a child that never became ready.</summary>
    void Kill();
}

/// <summary>Why a self-restart did or did not get far enough to exit on.</summary>
public enum RestartOutcome
{
    /// <summary>A replacement is running and queued for the mutex. Safe to exit.</summary>
    Started,

    /// <summary>The running executable's path could not be determined.</summary>
    NoExePath,

    /// <summary>Starting the replacement threw.</summary>
    LaunchFailed,

    /// <summary>The replacement exited before announcing itself.</summary>
    ChildExitedEarly,

    /// <summary>The replacement is alive but never announced itself in time.</summary>
    ChildNotReady,
}

/// <summary>
/// Restarts EyeCtrl without a supervisor, by launching a replacement and waiting for it
/// to prove it started.
///
/// <see cref="Begin"/> BLOCKS for up to the readiness timeout and must therefore never
/// run on the dispatcher: a frozen UI thread means no gaze processing, which would take
/// the user's control of the machine away for the duration — the exact outcome this
/// feature exists to prevent. <see cref="AppRestartCoordinator"/> runs it on a background
/// thread and marshals the result back.
///
/// Every dependency is injected so all five outcomes are exercised headlessly; the
/// production wiring is in <see cref="CreateDefault"/>.
/// </summary>
public sealed class SelfRestartStrategy
{
    private readonly Func<string?> _resolveExePath;
    private readonly Func<string, string, IRestartChild> _launch;
    private readonly Func<EventWaitHandle, TimeSpan, bool> _waitForReady;
    private readonly TimeSpan _readyWait;

    /// <summary>Creates a strategy with explicit seams (tests supply fakes).</summary>
    /// <param name="resolveExePath">Returns the running executable's path, or null.</param>
    /// <param name="launch">Starts a process: (exePath, arguments) => child.</param>
    /// <param name="waitForReady">
    /// Waits on the PARENT-OWNED ready event (created by <see cref="Begin"/> before the
    /// launch) for up to the given timeout; returns true if the child signalled. Production
    /// waits on the real named event; tests ignore the handle and return a canned result.
    /// </param>
    /// <param name="readyWait">How long to wait for that signal.</param>
    public SelfRestartStrategy(
        Func<string?> resolveExePath,
        Func<string, string, IRestartChild> launch,
        Func<EventWaitHandle, TimeSpan, bool> waitForReady,
        TimeSpan readyWait)
    {
        _resolveExePath = resolveExePath;
        _launch = launch;
        _waitForReady = waitForReady;
        _readyWait = readyWait;
    }

    /// <summary>The production wiring: the real apphost path, Process.Start, and the named event.</summary>
    public static SelfRestartStrategy CreateDefault() =>
        new(
            // The stable single-file bundle apphost path — Assembly.Location is empty for
            // a single-file publish, which is how this app ships.
            resolveExePath: () => Environment.ProcessPath,
            launch: (exePath, args) =>
            {
                var process = Process.Start(new ProcessStartInfo(exePath, args) { UseShellExecute = false })
                    ?? throw new InvalidOperationException("Process.Start returned no process.");
                return new ProcessRestartChild(process);
            },
            // The parent OWNS the ready event for the whole handoff: it creates the event
            // (unset) BEFORE launch, passes that same handle into waitForReady, and disposes
            // it after the wait returns. That ownership is what makes the signal reliable:
            //
            //  - No stale-Set false positive: the event is a fresh, unset object the parent
            //    controls the lifetime of, so a leftover event from a prior run can never be
            //    observed. (new EventWaitHandle(false, ManualReset, name) only applies
            //    initialState on CREATION — opening an existing Set event keeps it Set.)
            //  - No signal-before-open false negative: the event exists from before the child
            //    is launched, so however fast the child opens+sets, there is something to open.
            //
            // The child's side (RestartHandoff.SignalReady) OPENs this event rather than
            // creating it — see that method for why a missing event is a clean no-op.
            waitForReady: (readyEvent, timeout) =>
            {
                try { return readyEvent.WaitOne(timeout); }
                catch { return false; }
            },
            readyWait: RestartHandoff.ReadyWait);

    /// <summary>
    /// Launches a replacement and waits for it to announce itself. Blocks — see the
    /// class remarks. The caller may only shut down on <see cref="RestartOutcome.Started"/>.
    /// </summary>
    /// <returns>What happened, for the caller to act on and to tell the user.</returns>
    public RestartOutcome Begin()
    {
        var exePath = _resolveExePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return RestartOutcome.NoExePath;
        }

        // Create the ready event BEFORE launching, so the child always has something to
        // open+set, and a leftover event from a prior run can never be observed. Created
        // unset; the child is the only thing that ever Sets it. Disposed after the wait
        // regardless of outcome — the event's job is done once the parent has decided.
        using var readyEvent = new EventWaitHandle(
            initialState: false, EventResetMode.ManualReset, RestartHandoff.ReadyEventName);

        IRestartChild child;
        try
        {
            child = _launch(exePath, RestartHandoff.Argument);
        }
        catch
        {
            return RestartOutcome.LaunchFailed;
        }

        if (_waitForReady(readyEvent, _readyWait))
        {
            return RestartOutcome.Started;
        }

        // No signal. Either it died on the way up, or it is wedged. A wedged child is
        // still queued for the mutex, so leaving it alive would mean a zombie that
        // springs to life the next time this instance exits — kill it.
        if (child.HasExited)
        {
            return RestartOutcome.ChildExitedEarly;
        }

        try { child.Kill(); } catch { /* already gone; nothing to clean up */ }
        return RestartOutcome.ChildNotReady;
    }

    /// <summary>A user-facing explanation for a failed restart. Always states the app is still running.</summary>
    /// <param name="outcome">The outcome to describe.</param>
    /// <returns>Text suitable for a modal.</returns>
    public static string Describe(RestartOutcome outcome) => outcome switch
    {
        RestartOutcome.Started => "Restarting EyeCtrl…",
        RestartOutcome.NoExePath =>
            "Restart failed: EyeCtrl could not determine its own program path. "
            + "EyeCtrl is still running.",
        RestartOutcome.LaunchFailed =>
            "Restart failed: the new EyeCtrl could not be started. "
            + "EyeCtrl is still running.",
        RestartOutcome.ChildExitedEarly =>
            "Restart failed: the new EyeCtrl closed immediately. "
            + "EyeCtrl is still running.",
        RestartOutcome.ChildNotReady =>
            "Restart failed: the new EyeCtrl did not start in time. "
            + "EyeCtrl is still running.",
        _ => "Restart failed. EyeCtrl is still running.",
    };

    private sealed class ProcessRestartChild(Process process) : IRestartChild
    {
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch { return true; }   // handle gone: treat as exited
            }
        }

        public void Kill() => process.Kill(entireProcessTree: true);
    }
}
