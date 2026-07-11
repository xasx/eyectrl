namespace EyeCtrl.App.Services;
using System.Windows.Media;

/// <summary>
/// Provides per-process application icons as WPF <see cref="ImageSource"/> instances,
/// for display on Task Switcher tiles. Implemented in <c>EyeCtrl.App</c> because
/// <see cref="ImageSource"/> is a WPF-only type and the lower-tier projects
/// (Core, ViewModels, Platform) do not reference WPF.
/// </summary>
public interface IAppIconProvider
{
    /// <summary>
    /// Returns the associated application icon for the given process, or null when
    /// no icon is available (e.g. the process is inaccessible or has exited).
    /// </summary>
    /// <param name="processId">Win32 process id whose executable icon should be returned.</param>
    /// <returns>An <see cref="ImageSource"/> for the process executable's icon, or null.</returns>
    ImageSource? GetIcon(uint processId);
}
