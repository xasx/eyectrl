namespace EyeCtrl.Platform.Services;

using System.Runtime.InteropServices;
using EyeCtrl.Core.Interfaces;

/// <summary>
/// Activates UWP/Store Windows applications via the shell's
/// <c>IApplicationActivationManager</c> COM interface. Used by the Quick Actions
/// toolbar's <c>LaunchWindowsApp</c> action type.
/// </summary>
public sealed class WindowsAppLauncher : IWindowsAppLauncher
{
    /// <summary>
    /// Activates the Windows application identified by the given Application User Model ID.
    /// </summary>
    /// <param name="aumid">The Application User Model ID (AUMID) of the app to activate.</param>
    public void ActivateApplication(string aumid)
    {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        var hr = mgr.ActivateApplication(aumid, null, ACTIVATEOPTIONS.AO_NONE, out _);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            ACTIVATEOPTIONS options, out uint processId);
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    private enum ACTIVATEOPTIONS : uint { AO_NONE = 0, AO_DESIGNMODE = 1, AO_NOERRORUI = 2, AO_NOSPLASHSCREEN = 4 }
}
