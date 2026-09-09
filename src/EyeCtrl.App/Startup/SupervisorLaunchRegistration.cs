namespace EyeCtrl.App.Startup;

/// <summary>
/// Answers whether a supervisor launch-at-logon registration actually exists.
/// </summary>
/// <remarks>
/// Split out from <see cref="StartOnBootPolicy"/> so the question has one honest answer in one
/// place. The app's startup reconciliation stops self-registering only when this is true, and
/// stopping wrongly is a lockout: the HKCU Run value is deleted, nothing replaces it, and the
/// user meets an empty desktop at the next logon with no way to launch the app that would give
/// them input.
/// <para>
/// TODO(installer): there is no installer and no Scheduled Task yet — the supervisor's own
/// setup window says as much ("Scheduled Task install is a separate step"), and
/// <c>Supervisor/Program.cs</c> carries the matching TODO. Until the installer workstream lands
/// and creates the task, the honest answer is <see langword="false"/>, which keeps the app
/// registering itself. Whoever adds the task replaces this with a real probe for it.
/// </para>
/// </remarks>
public static class SupervisorLaunchRegistration
{
    /// <summary>
    /// True when something other than the app itself is registered to start it at logon.
    /// Always <see langword="false"/> until the installer creates the Scheduled Task.
    /// </summary>
    public static bool IsRegistered => false;
}
