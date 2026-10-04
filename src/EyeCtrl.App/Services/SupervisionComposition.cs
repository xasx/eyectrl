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
    /// Composes the bring-up and the toggle over the given settings file. Both persist through the
    /// sanctioned <see cref="SupervisionEnabledWriter.Persist"/> — the App writer's
    /// <see cref="SettingsService.Save"/> would overlay the on-disk Supervision section and
    /// silently discard the flip.
    /// </summary>
    /// <returns>The shared bring-up and the Settings-row toggle, wired to the same sanctioned
    /// writer and the same live Run-key reconciler.</returns>
    public static (SupervisorBringUp BringUp, SupervisionToggleService Toggle) Create(
        EyeCtrlSettings settings,
        string settingsFilePath,
        IStartupRegistrar startupRegistrar,
        Action enablePublishing,
        Func<bool> launchSupervisor,
        Func<bool> tryReachSupervisor,
        Func<bool> isSupervisorInstalled,
        Func<bool> isLaunchRegistered,
        Action<Action> runInBackground,
        Action<Action> postToUi)
    {
        var writer = new SupervisionEnabledWriter(settingsFilePath);
        var reconcileRunKey = CreateRunKeyReconciler(settings, startupRegistrar, isLaunchRegistered);

        var bringUp = new SupervisorBringUp(
            settings,
            enablePublishing,
            launchSupervisor,
            writer.Persist,
            reconcileRunKey,
            tryReachSupervisor);

        var toggle = new SupervisionToggleService(
            settings,
            bringUp,
            writer.Persist,
            isSupervisorInstalled,
            reconcileRunKey,
            runInBackground,
            postToUi);

        return (bringUp, toggle);
    }
}
