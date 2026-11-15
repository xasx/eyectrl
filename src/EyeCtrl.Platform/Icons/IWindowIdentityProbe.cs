namespace EyeCtrl.Platform.Icons;

/// <summary>
/// Read-only facts about a window and its process, behind a seam so the icon sources' decisions are
/// testable without a real Store app or a real elevated process (CI is elevated, the owner's build
/// runs UIAccess, a dev run is Medium: the sources must not depend on how the process runs).
/// </summary>
internal interface IWindowIdentityProbe
{
    /// <summary>The window's class name (GetClassName), or null when it cannot be read.</summary>
    string? TryGetClassName(IntPtr hwnd);

    /// <summary>
    /// The process image path (QueryFullProcessImageName with
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> — the limited rights that still work for elevated
    /// and other-user processes), or null.
    /// </summary>
    string? TryGetExecutablePath(uint processId);

    /// <summary>
    /// The window's <c>System.AppUserModel.ID</c> from its property store, or null when the window
    /// has none (every ordinary Win32 window) or the store cannot be read.
    /// </summary>
    string? TryGetAppUserModelId(IntPtr hwnd);
}
