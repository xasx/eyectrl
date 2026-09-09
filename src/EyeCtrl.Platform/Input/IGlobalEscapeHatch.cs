namespace EyeCtrl.Platform.Input;

/// <summary>
/// Caregiver escape hatch (issue #141): a global low-level keyboard listener that observes ESC
/// pressed ANYWHERE in the system — including while a third-party app holds the foreground,
/// which is exactly when a gaze-driven mode (repeat, drag) is running and the previous
/// ComponentDispatcher filters could never fire (they only see EyeCtrl's own UI thread, and
/// every EyeCtrl overlay is non-activating).
/// </summary>
public interface IGlobalEscapeHatch : IDisposable
{
    /// <summary>
    /// Raised when ESC is pressed system-wide. Delivered on a THREAD-POOL thread — never the
    /// hook thread (the hook must return to CallNextHookEx immediately; a slow LL callback
    /// delays keyboard input system-wide). Handlers must not block and must marshal to the UI
    /// thread themselves.
    /// </summary>
    event Action? EscapePressed;

    /// <summary>True while the low-level hook is installed and its pump thread is running.</summary>
    bool IsActive { get; }
}
