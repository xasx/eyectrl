namespace EyeCtrl.Supervisor.Supervision;

using System;
using EyeCtrl.Core.Settings;

/// <summary>
/// Ticket 04 re-check 2 R2-1 (owned by ticket 06): the two <c>SupervisorApp</c> decisions that
/// carry ticket 04's safety guarantees, extracted so tests can drive them without composing a WPF
/// <c>Application</c>.
/// </summary>
/// <remarks>
/// <para><b>The enabled predicate must read the disk.</b> The machine's gate is a
/// <see cref="SupervisionEnabledTracker"/> over the settings file, never the in-memory
/// <c>EyeCtrlSettings</c> snapshot that the setup window edits on open and on every unsaved toggle
/// click (r1 F2: a running Supervisor must not ignore the row's Off, and window clicks must not
/// drive recovery).</para>
/// <para><b>The startup launch must see a running EyeCtrl.</b> The decision consults
/// <see cref="AppAlreadyRunningProbe"/> (default <see cref="DefaultAppInstanceProbe"/>), so a
/// Supervisor started while the app runs stays in IdleWatch to adopt instead of launching a
/// duplicate that dies on the app's single-instance mutex and books strikes (r1 F1).</para>
/// </remarks>
internal sealed class SupervisorComposition
{
    /// <summary>The production probe: true while an EyeCtrl.App instance holds its mutex.</summary>
    public static Func<bool> DefaultAppInstanceProbe { get; } = () => AppInstanceProbe.IsRunning();

    /// <summary>The supervision state machine, with the tracker-backed enabled predicate.</summary>
    public SupervisionStateMachine Machine { get; }
    /// <summary>The tracker the machine's predicate and the deferred-launch gate read.</summary>
    public SupervisionEnabledTracker EnabledTracker { get; }
    /// <summary>The probe actually used: the injected one, or <see cref="DefaultAppInstanceProbe"/>.</summary>
    public Func<bool> AppAlreadyRunningProbe { get; }
    /// <summary>What the probe reported (false when the launch is otherwise unconfigured).</summary>
    public bool AppAlreadyRunning { get; }
    /// <summary>Whether Initialize emits the startup <c>LaunchRequest</c>.</summary>
    public bool ShouldEmitStartupLaunch { get; }

    private SupervisorComposition(
        SupervisionStateMachine machine,
        SupervisionEnabledTracker enabledTracker,
        Func<bool> appAlreadyRunningProbe,
        bool appAlreadyRunning,
        bool shouldEmitStartupLaunch)
    {
        Machine = machine;
        EnabledTracker = enabledTracker;
        AppAlreadyRunningProbe = appAlreadyRunningProbe;
        AppAlreadyRunning = appAlreadyRunning;
        ShouldEmitStartupLaunch = shouldEmitStartupLaunch;
    }

    /// <param name="settings">The startup settings snapshot (thresholds, enabled, StartOnBoot).</param>
    /// <param name="settingsPath">The file the enabled predicate re-reads on every evaluation.</param>
    /// <param name="appAlreadyRunning">App-instance probe; null = <see cref="DefaultAppInstanceProbe"/>.
    /// Injectable so tests never touch the production app mutex.</param>
    /// <param name="writerMutexName">Test seam for the tracker's settings-writer mutex name.</param>
    public static SupervisorComposition Create(
        EyeCtrlSettings settings,
        string settingsPath,
        Func<bool>? appAlreadyRunning = null,
        string? writerMutexName = null)
    {
        var tracker = new SupervisionEnabledTracker(settingsPath, settings.Supervision.Enabled, writerMutexName);
        var machine = new SupervisionStateMachine(
            maxRetries: settings.Supervision.MaxRetries,
            windowSeconds: settings.Supervision.CrashWindowSeconds,
            isSupervisionEnabled: () => tracker.Current);

        var probe = appAlreadyRunning ?? DefaultAppInstanceProbe;
        // The probe only matters when the launch is otherwise configured on — the policy
        // short-circuits there anyway, and a disabled configuration must never probe at all. This
        // also lets a test assert the default binding without opening the production app mutex.
        var appIsRunning = settings.Supervision.Enabled && settings.General.StartOnBoot && probe();
        var shouldEmit = StartupLaunchPolicy.ShouldLaunchAtStartup(
            settings.Supervision.Enabled, settings.General.StartOnBoot, appIsRunning);

        return new SupervisorComposition(machine, tracker, probe, appIsRunning, shouldEmit);
    }
}
