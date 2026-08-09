namespace EyeCtrl.Core.Models;

/// <summary>The toggle decision for the Maximize/Restore button.</summary>
public enum WindowStateOp
{
    /// <summary>Maximize the window.</summary>
    Maximize,
    /// <summary>Restore the window to its normal or prior size.</summary>
    Restore
}

/// <summary>
/// Pure deciding logic for the Maximize/Restore Quick Actions toolbar button. No Win32
/// dependency — the caller reads <c>IsZoomed</c>/<c>IsIconic</c> and passes the booleans here.
/// </summary>
public static class WindowStateOps
{
    /// <summary>
    /// Minimized windows un-minimize first (IsIconic takes precedence), so a window
    /// minimized-from-maximized restores rather than maximizing a second time.
    /// </summary>
    public static WindowStateOp DecideWindowOp(bool isZoomed, bool isIconic)
        => (isZoomed, isIconic) switch
        {
            (_, true) => WindowStateOp.Restore,  // minimized → un-minimize first
            (true, _) => WindowStateOp.Restore,  // maximized → restore
            _ => WindowStateOp.Maximize          // normal → maximize
        };
}
