namespace EyeCtrl.App.Services;

using EyeCtrl.App.Overlays;
using EyeCtrl.Core;
using EyeCtrl.Core.Adaptation;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

/// <summary>
/// Everything <c>App.OnExit</c> tears down, as nullable values: the list is built from the App's
/// fields at exit time, and an instance that never got as far as constructing a service (a duplicate
/// launch that lost the single-instance mutex) exits with the same sequence and the missing steps as
/// no-ops.
/// </summary>
internal sealed class ShutdownDependencies
{
    internal SplashThreadHandle? SplashHandle { get; init; }

    internal HeartbeatPublisher? HeartbeatPublisher { get; init; }

    internal AppRestartCoordinator? RestartCoordinator { get; init; }

    internal DispatcherTimer? HeartbeatTimer { get; init; }

    internal SetupNoticeWatch? SetupNoticeWatch { get; init; }

    internal SupervisorRequestService? SupervisorRequests { get; init; }

    internal DisplayGeometryWatcher? DisplayGeometryWatcher { get; init; }

    internal TrayIconManager? TrayIconManager { get; init; }

    internal LagMarkHost? LagMarkHost { get; init; }

    internal RingArmLog? RingArmLog { get; init; }

    internal BlockedInputCoordinator? BlockedInput { get; init; }

    internal EventCoordinator? EventCoordinator { get; init; }

    internal ServiceProvider? ServiceProvider { get; init; }

    internal StrandingLedger? StrandingLedger { get; init; }

    internal SingleInstanceGuard? SingleInstance { get; init; }
}

/// <summary>
/// The real <c>App.OnExit</c> step list, extracted so it can be read and run headlessly. Order is the
/// contract: the exit watchdog first (it must cover every step that follows), the single-instance
/// mutex release last (a restart child is queued on it and starts initialising the instant it is
/// released, so everything this instance still owns must already be gone).
/// </summary>
internal static class ExitSequencePlan
{
    /// <summary>The first step's name: the watchdog that reports any later step that hangs.</summary>
    internal const string StartWatchdogStep = "start exit watchdog";

    /// <summary>The last step's name: releasing the mutex lets a restart replacement start.</summary>
    internal const string ReleaseMutexStep = "release single-instance mutex";

    /// <summary>Builds the exit sequence in its production order.</summary>
    /// <param name="deps">The App's teardown participants; all nullable.</param>
    /// <param name="terminate">
    /// The watchdog's terminate action — production passes <see cref="ExitStuckReport.Terminate"/>;
    /// tests pass a recorder so no process is ever killed.
    /// </param>
    /// <param name="time">The watchdog's clock; <see cref="TimeProvider.System"/> when omitted.</param>
    /// <returns>The sequence, not yet run.</returns>
    internal static ShutdownSequence Create(
        ShutdownDependencies deps,
        Action<string> terminate,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(terminate);

        var sequence = new ShutdownSequence();

        // FIRST, and never cancelled: if any later step (or native teardown after this method
        // returns) has not finished 15 s from now, name it in the log and end the process, which
        // abandons the mutex rather than holding every relaunch hostage.
        sequence.Add(StartWatchdogStep, () =>
            new ExitWatchdog(
                time ?? TimeProvider.System,
                () => sequence.CurrentStep,
                terminate).Start());

        // Moved into the step list (it was the bare statement before the old Step helper): a hang in
        // the splash's marshalled dispose must also be attributable.
        sequence.Add("dispose splash handle", () => deps.SplashHandle?.Dispose());

        // Stamp the final shuttingDown:true payload BEFORE tearing down services — the supervisor
        // needs this authenticated clean-shutdown signal within its 5s deadline to distinguish a
        // graceful exit (no restart) from a crash. Cancels the writer loop and drains it (bounded).
        // Carries the restart intent to the supervisor. False for every ordinary exit —
        // and also for a self-restart, where a replacement is already coming up and a
        // supervisor-launched second instance would just lose the race for the mutex.
        sequence.Add("signal shutting down", () => deps.HeartbeatPublisher?.SignalShuttingDown(
            restartRequested: deps.RestartCoordinator?.SupervisorRelaunchRequested ?? false));

        // Stop ticking before the publisher goes away: a tick racing Dispose would call Stamp on
        // a disposed publisher from the dispatcher.
        sequence.Add("stop heartbeat timer", () => deps.HeartbeatTimer?.Stop());

        // C9 S6f: the late-record watch is bounded and app-owned; release its timer/watcher now.
        sequence.Add("dispose setup notice watch", () => deps.SetupNoticeWatch?.Dispose());

        // Stop taking Supervisor requests before the exit save: a request mid-flight now sees the
        // pipe close, and the Supervisor parks it and re-sends to the next App.
        sequence.Add("dispose supervisor request service", () => deps.SupervisorRequests?.Dispose());

        // Before the coordinators: SystemEvents holds a static reference to the handler, so an
        // un-unsubscribed watcher outlives the app object and would fire into disposed services.
        sequence.Add("dispose display geometry watcher", () => deps.DisplayGeometryWatcher?.Dispose());

        sequence.Add("dispose tray icon", () => deps.TrayIconManager?.Dispose());
        sequence.Add("dispose lag mark host", () => deps.LagMarkHost?.Dispose());
        sequence.Add("dispose ring arm log", () => deps.RingArmLog?.Dispose());
        sequence.Add("dispose blocked-input coordinator", () => deps.BlockedInput?.Dispose());
        sequence.Add("dispose event coordinator", () => deps.EventCoordinator?.Dispose());

        // F5: the audio diagnostics' COM listeners outlive every service instance — unregister
        // them while the process is still alive rather than letting WASAPI call into a dying CCW.
        sequence.Add("unregister audio diagnostics", AudioSessionDiagnostics.Shutdown);

        if (deps.ServiceProvider is { } services)
        {
            sequence.Add("stop gaze engine", () => services.GetRequiredService<GazeEngine>().Stop());
            // Best-effort by design: Save throws on validation failure, and a failed final save
            // must not cost the caller its remaining teardown (previously this throw skipped
            // provider disposal, the mutex release and the log flush).
            sequence.Add("save settings", () =>
                services.GetRequiredService<SettingsService>().Save(services.GetRequiredService<EyeCtrlSettings>()));
            sequence.Add("persist adaptation state", () => services.GetRequiredService<AdaptationLearner>().Persist()); // best-effort (§11); never throws
            // (User dictionary is saved by EventCoordinator.Dispose above, via PredictionStackFactory.)

            // Instrument 2's final verdict, before the overlays go away. Instrument 1 reports from
            // GazeEngineAdapter.Dispose just below. A step now, so a hang here is named like the rest.
            sequence.Add("report ring quantization", () =>
            {
                if (services.GetRequiredService<IOverlayManager>().CursorCircle is IRingQuantizationInstrument instrumented)
                    instrumented.ReportQuantization();
            });

            sequence.Add("dispose gaze adapter", () => services.GetRequiredService<GazeEngineAdapter>().Dispose());
            sequence.Add("dispose gaze engine", () => services.GetRequiredService<GazeEngine>().Dispose());

            // Wave D D1: the stranding ledger's final summary and session-marker delete. After the
            // gaze adapter (no state transition can follow) and before the mutex release below: a
            // queued restart child reads the marker the instant the mutex is free, so a later
            // delete would make it report this clean session as unclean.
            sequence.Add("write stranding summary", () => deps.StrandingLedger?.EndSession());

            sequence.Add("dispose external dwell service", () => services.GetRequiredService<ExternalDwellService>().Dispose());
            // Deactivate the adaptation runtime explicitly while the provider is still alive (live-pass
            // finding, 2026-10-16): DI disposal disposes resolved singletons one by one, and by
            // AdaptationActivation's turn the provider is already disposed — StopRuntime's first resolve
            // threw ObjectDisposedException, logged as "Failed to cleanly deactivate the adaptation
            // runtime" with the whole teardown skipped. Dispose is idempotent (Deactivate no-ops once
            // inactive), so the singleton's later disposal inside provider disposal is a no-op.
            sequence.Add("deactivate adaptation runtime", () => services.GetRequiredService<AdaptationActivation>().Dispose());
            // The session-long password detector (started in OnStartup): remove its system-wide UIA focus
            // hook while the provider is alive. Stop is idempotent, so the container's later Dispose is a no-op.
            sequence.Add("stop password-field detector", () => services.GetRequiredService<ISensitiveContextDetector>().Stop());
            sequence.Add("dispose service provider", services.Dispose); // Disposes all IDisposable singletons including ManualSelectionDwellService
        }

        sequence.Add("dispose heartbeat publisher", () => deps.HeartbeatPublisher?.Dispose());

        // Release the single-instance mutex LAST. A restart child is queued on it
        // (Task 6's waiting acquire) and starts initialising the instant it is released, so
        // everything this instance still owns — the settings file, the Tobii device, the
        // external-dwell pipe — must already be released. Releasing it at the TOP of OnExit
        // was harmless only while a second instance exited immediately instead of waiting.
        // The step signals the guard's keeper thread, which owns the mutex and calls ReleaseMutex
        // on it; that is what frees a waiter even when this (UI) thread is hung.
        sequence.Add(ReleaseMutexStep, () => deps.SingleInstance?.Dispose());

        return sequence;
    }
}
