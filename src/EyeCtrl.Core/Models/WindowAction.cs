namespace EyeCtrl.Core.Models;

/// <summary>Window-management actions fired from the toolbar's window-actions region.</summary>
public enum WindowAction
{
    /// <summary>Minimize the target window.</summary>
    Minimize,
    /// <summary>Toggle maximize/restore based on the window's current state.</summary>
    MaximizeRestore,
    /// <summary>Close the target window.</summary>
    Close
}
