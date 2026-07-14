namespace EyeCtrl.Core.Interfaces;

/// <summary>
/// Activates (launches/brings-to-front) a Windows application.
/// Implementations target UWP/Store apps via their Application User Model ID (AUMID)
/// using the COM <c>IApplicationActivationManager</c>.
/// </summary>
public interface IWindowsAppLauncher
{
    /// <summary>
    /// Activates the Windows application identified by the given Application User Model ID.
    /// </summary>
    /// <param name="aumid">The Application User Model ID (AUMID) of the app to activate,
    /// e.g. <c>Microsoft.WindowsCalculator_8wekyb3d8bbwe!App</c>.</param>
    void ActivateApplication(string aumid);
}
