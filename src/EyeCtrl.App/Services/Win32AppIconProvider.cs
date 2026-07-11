namespace EyeCtrl.App.Services;

using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Win32-backed implementation of <see cref="IAppIconProvider"/> that extracts each
/// process's associated executable icon via <see cref="Icon.ExtractAssociatedIcon"/>
/// and converts it to a WPF <see cref="BitmapSource"/>. Used by the Task Switcher.
/// </summary>
public class Win32AppIconProvider : IAppIconProvider
{
    /// <summary>
    /// Returns the executable icon for the given process, or null when the process
    /// cannot be queried or has no associated icon.
    /// </summary>
    /// <param name="processId">Win32 process id whose executable icon should be returned.</param>
    /// <returns>A <see cref="BitmapSource"/> for the process icon, or null.</returns>
    public ImageSource? GetIcon(uint processId)
    {
        try
        {
            using var proc = Process.GetProcessById((int)processId);
            var path = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(path))
                return null;

            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null)
                return null;

            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch
        {
            // Win32Exception (Access Denied) for elevated/other-user processes,
            // or the process has exited. Fall back to null (no icon).
            return null;
        }
    }
}
