namespace EyeCtrl.Platform.Icons;

using System.Windows.Media;

/// <summary>
/// Provides a Task Switcher tile's icon as a WPF <see cref="ImageSource"/>. Lives in
/// <c>EyeCtrl.Platform</c> because the resolution is Win32/COM interop and the shell image factory
/// is per window; the implementation never throws and never returns null — the last source is a
/// neutral default glyph, so a tile can never show an empty hole or another app's icon.
/// </summary>
public interface IAppIconProvider
{
    /// <summary>
    /// Resolves the icon of one WINDOW (not of its process: every Store app shares the
    /// <c>ApplicationFrameHost</c> process, and only the window says which app it hosts).
    /// </summary>
    /// <param name="hwnd">The window handle to read the app identity and window icon from.</param>
    /// <param name="processId">The window's owning process, used for the limited-rights exe path.</param>
    /// <returns>The window's icon; a neutral default glyph when no source can produce one.</returns>
    ImageSource GetIcon(IntPtr hwnd, uint processId);
}
