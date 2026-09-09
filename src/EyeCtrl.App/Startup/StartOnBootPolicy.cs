namespace EyeCtrl.App.Startup;

public static class StartOnBootPolicy
{
    /// <summary>
    /// Whether the app should register itself in the HKCU Run key.
    /// </summary>
    /// <remarks>
    /// The supervised case is the dangerous one. Handing launch-at-logon to the supervisor is
    /// correct ONLY once the supervisor actually owns it — the caller translates a false return
    /// into deleting the Run value, so returning false while nothing else starts the app leaves
    /// the user with no application at the next logon and no input channel to fix it with.
    /// <paramref name="supervisorOwnsLaunchAtLogon"/> is that missing evidence; without it the
    /// app keeps its own registration and the single-instance mutex arbitrates any transient
    /// double-launch.
    /// </remarks>
    /// <param name="supervised">Whether supervised process lifecycle is enabled.</param>
    /// <param name="startOnBoot">The user's launch-at-logon preference.</param>
    /// <param name="supervisorOwnsLaunchAtLogon">
    /// True only when a supervisor launch registration demonstrably exists. See
    /// <see cref="SupervisorLaunchRegistration"/>.
    /// </param>
    public static bool ShouldSelfRegister(bool supervised, bool startOnBoot, bool supervisorOwnsLaunchAtLogon)
        => startOnBoot && !(supervised && supervisorOwnsLaunchAtLogon);
}
