namespace EyeCtrl.Core.Interfaces;

using EyeCtrl.Core.Models;

/// <summary>Enumerates installed apps (packaged AUMIDs) for the Quick Actions app picker.</summary>
public interface IInstalledAppProvider
{
    /// <summary>Returns installed apps; may be empty if enumeration is unavailable. Never throws.</summary>
    IReadOnlyList<InstalledApp> GetInstalledApps();
}
