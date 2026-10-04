namespace EyeCtrl.App.Services;

using System;
using EyeCtrl.App.Startup;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Settings;

/// <summary>
/// The one place the App composes supervision: the shared bring-up and the Settings row's toggle,
/// wired to the sanctioned write path and the launch-at-logon policy. Extracted from
/// <c>App.xaml.cs</c> so a test can drive the REAL production wiring against a real settings file
/// — the original defect (an App-writer persist silently discarding the flip) lived in that
/// wiring and survived the suite because every test composed its own in-memory copy.
/// </summary>
public static class SupervisionComposition
{
    /// <summary>
    /// Builds the launch-at-logon reconciliation from <c>StartOnBootPolicy</c>. It reads the
    /// enable flag and the registration probe LIVE at each call, so a handler that runs after a
    /// row toggle reconciles with the current world — not the value captured at startup.
    /// </summary>
    /// <param name="settings">Live settings; <c>General.StartOnBoot</c> is the user's preference.</param>
    /// <param name="startupRegistrar">Where the reconciled decision is applied.</param>
    /// <param name="isLaunchRegistered">Whether something other than the app is registered to
    /// launch it (see <c>SupervisorLaunchRegistration</c>).</param>
    public static Action CreateRunKeyReconciler(
        EyeCtrlSettings settings,
        IStartupRegistrar startupRegistrar,
        Func<bool> isLaunchRegistered) =>
        () => startupRegistrar.SetStartOnBoot(StartOnBootPolicy.ShouldSelfRegister(
            supervised: settings.Supervision.Enabled,
            settings.General.StartOnBoot,
            isLaunchRegistered()));

    /// <summary>
    /// Ticket 07 (re-check addition A): the crash policy's presence predicate, produced HERE so the
    /// fault handler only calls it. A mutant forcing presence true/false (or dropping the freshness
    /// window) is then a composition-level mutation covered by tests, not untestable App glue.
    /// </summary>
    /// <param name="lastDeliveredUtc">When a heartbeat was last DELIVERED; null when never.</param>
    /// <param name="utcNow">The clock read at fault time.</param>
    /// <param name="window">The freshness window (<see cref="HeartbeatPublisher.DeliveryFreshnessWindow"/>).</param>
    public static Func<bool> CreateSupervisorPresencePredicate(
        Func<DateTime?> lastDeliveredUtc,
        Func<DateTime> utcNow,
        TimeSpan window) =>
        () => lastDeliveredUtc() is { } last && utcNow() - last <= window;

    /// <summary>
    /// The production Run-key reconciler: binds the real registration probe. App.xaml.cs uses
    /// this for the startup reconciliation and the StartOnBoot handler, and
    /// <see cref="CreateForProduction"/> uses it for both consumers of the composition, so no
    /// call site passes the probe itself.
    /// </summary>
    public static Action CreateProductionRunKeyReconciler(
        EyeCtrlSettings settings,
        IStartupRegistrar startupRegistrar) =>
        CreateRunKeyReconciler(
            settings, startupRegistrar, () => SupervisorLaunchRegistration.IsRegistered);

    /// <summary>
    /// Composes the bring-up, the toggle and the startup ensure over the given settings file.
    /// Both writers persist through the sanctioned <see cref="SupervisionEnabledWriter.Persist"/>
    /// — the App writer's <see cref="SettingsService.Save"/> would overlay the on-disk Supervision
    /// section and silently discard the flip.
    /// </summary>
    /// <param name="isSupervisorConnected">Cheap cached read of the delivery freshness (no IPC);
    /// the row's truthful status and retry condition. The active pipe probe is
    /// <paramref name="tryReachSupervisor"/>.</param>
    /// <returns>The shared bring-up, the toggle and the write-free startup ensure.</returns>
    public static (SupervisorBringUp BringUp, SupervisionToggleService Toggle, SupervisorStartupEnsure StartupEnsure)
        Create(
        EyeCtrlSettings settings,
        string settingsFilePath,
        IStartupRegistrar startupRegistrar,
        Action enablePublishing,
        Func<bool> launchSupervisor,
        Func<bool> tryReachSupervisor,
        Func<bool> isSupervisorInstalled,
        Func<bool> isLaunchRegistered,
        Func<bool> isSupervisorConnected,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        var reconcileRunKey = CreateRunKeyReconciler(settings, startupRegistrar, isLaunchRegistered);
        return Compose(
            settings, settingsFilePath, reconcileRunKey, enablePublishing, launchSupervisor,
            tryReachSupervisor, isSupervisorInstalled, isSupervisorConnected, runInBackground, postToUi);
    }

    /// <summary>
    /// The composition <c>App.xaml.cs</c> calls: binds the STATIC production probes itself —
    /// <see cref="SupervisorLauncher.TryLaunch"/>,
    /// <see cref="SupervisorLauncher.IsExecutablePresent"/> and
    /// <c>SupervisorLaunchRegistration.IsRegistered</c> — so a call site cannot accidentally pass
    /// a wrong answer for them (a <c>() =&gt; true</c> registration probe would hand the HKCU Run
    /// key to a Scheduled Task that does not exist: a lockout at the next logon). Only the live
    /// instance collaborators (publishing, the pipe probe, marshalling) are injected.
    /// </summary>
    /// <returns>The bring-up, the toggle, and the live Run-key reconciler they share — the
    /// reconciler is returned so a test can drive the exact production binding.</returns>
    public static (SupervisorBringUp BringUp, SupervisionToggleService Toggle, Action ReconcileRunKey)
        CreateForProduction(
            EyeCtrlSettings settings,
            string settingsFilePath,
            IStartupRegistrar startupRegistrar,
            Action enablePublishing,
            Func<bool> tryReachSupervisor,
            Func<bool> isSupervisorConnected,
            Action<Action> runInBackground,
            Action<Action> postToUi)
    {
        var reconcileRunKey = CreateProductionRunKeyReconciler(settings, startupRegistrar);
        var (bringUp, toggle, _) = Compose(
            settings, settingsFilePath, reconcileRunKey, enablePublishing,
            launchSupervisor: SupervisorLauncher.TryLaunch,
            tryReachSupervisor,
            isSupervisorInstalled: () => SupervisorLauncher.IsExecutablePresent,
            isSupervisorConnected, runInBackground, postToUi);
        return (bringUp, toggle, reconcileRunKey);
    }

    private static (SupervisorBringUp BringUp, SupervisionToggleService Toggle, SupervisorStartupEnsure StartupEnsure) Compose(
        EyeCtrlSettings settings,
        string settingsFilePath,
        Action reconcileRunKey,
        Action enablePublishing,
        Func<bool> launchSupervisor,
        Func<bool> tryReachSupervisor,
        Func<bool> isSupervisorInstalled,
        Func<bool> isSupervisorConnected,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        var writer = new SupervisionEnabledWriter(settingsFilePath);

        var bringUp = new SupervisorBringUp(
            settings,
            enablePublishing,
            launchSupervisor,
            writer.Persist,
            reconcileRunKey,
            tryReachSupervisor);

        var startupEnsure = new SupervisorStartupEnsure(
            settings,
            enablePublishing,
            launchSupervisor,
            tryReachSupervisor);

        var toggle = new SupervisionToggleService(
            settings,
            bringUp,
            startupEnsure,
            writer.Persist,
            isSupervisorInstalled,
            isSupervisorConnected,
            reconcileRunKey,
            runInBackground,
            postToUi);

        return (bringUp, toggle, startupEnsure);
    }
}
