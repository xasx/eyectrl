namespace EyeCtrl.Core.Interfaces;

/// <summary>What one non-injecting foreground attempt found and did.</summary>
/// <param name="ForegroundChanged">True when the target window held the foreground after the
/// attempt (the only success that matters to the owner).</param>
/// <param name="SetForegroundWindowSucceeded"><c>SetForegroundWindow</c>'s own return. It is
/// recorded even when the fallback was needed, because the two facts diagnose different
/// failures.</param>
/// <param name="UsedShowWindowFallback">True when the attempt also ran the minimize/restore
/// toggle.</param>
public readonly record struct EscapeWindowResult(
    bool ForegroundChanged, bool SetForegroundWindowSucceeded, bool UsedShowWindowFallback);

/// <summary>
/// R4's way out of a UIPI block, deliberately without <c>SendInput</c>: the ALT-tap grant the task
/// switcher normally uses is itself injected input and is dropped in exactly the state this path
/// exists to escape.
///
/// <para>
/// A target is only eligible when its token is not above EyeCtrl's own — bringing an elevated
/// window forward would be a second blocked attempt. The desktop/shell window is the last-resort
/// target when no application window is eligible.
/// </para>
/// </summary>
public interface IEscapeWindowService
{
    /// <summary>
    /// Finds the window the owner most plausibly wants back: the topmost visible, non-EyeCtrl,
    /// non-elevated application window, else the shell/desktop window.
    /// </summary>
    /// <returns>The window handle, or null when even the shell window is unavailable.</returns>
    IntPtr? TryGetEscapeTarget();

    /// <summary>
    /// Brings <paramref name="hwnd"/> to the foreground without injecting input:
    /// <c>SetForegroundWindow</c>, then a <c>ShowWindow</c> minimize/restore toggle when the
    /// foreground did not change. Never calls <c>SendInput</c>.
    /// </summary>
    /// <param name="hwnd">The target window handle from <see cref="TryGetEscapeTarget"/>.</param>
    /// <returns>Whether the target actually ended up in the foreground, and which path was used.</returns>
    EscapeWindowResult TryBringToFront(IntPtr hwnd);
}
