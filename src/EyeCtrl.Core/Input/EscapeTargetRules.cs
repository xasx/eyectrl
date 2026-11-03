namespace EyeCtrl.Core.Input;

/// <summary>One top-level window offered to R4's way out, in Z-order (top first).</summary>
/// <param name="Hwnd">The window handle.</param>
/// <param name="ProcessId">Its process id.</param>
/// <param name="IsShell">True for the desktop/shell window, the last-resort target.</param>
/// <param name="Facts">The process's token facts; unknown for the shell and our own windows.</param>
public readonly record struct EscapeCandidate(
    IntPtr Hwnd, uint ProcessId, bool IsShell, ProcessTokenFacts Facts);

/// <summary>
/// R4's target selection, pure so the three rules the reviewer asked for are testable without a
/// desktop: the Z-order walk, skipping EyeCtrl's own windows, and skipping windows whose token is
/// above ours (bringing an elevated window forward would be a second blocked attempt). The shell is
/// kept only as the last resort.
/// </summary>
public static class EscapeTargetRules
{
    /// <summary>
    /// Picks the first eligible window in Z-order, else the shell. Never an EyeCtrl window (its
    /// own process), never a window above EyeCtrl's reach.
    /// </summary>
    /// <param name="zOrder">Top-level windows, top first (the order <c>EnumWindows</c> returns).</param>
    /// <param name="self">EyeCtrl's own token facts.</param>
    /// <param name="ownProcessId">EyeCtrl's process id.</param>
    /// <returns>The chosen window handle, or null when nothing is eligible.</returns>
    public static IntPtr? SelectTopmost(
        IReadOnlyList<EscapeCandidate> zOrder, SelfTokenFacts self, uint ownProcessId)
    {
        EscapeCandidate? shell = null;

        foreach (var candidate in zOrder)
        {
            if (candidate.Hwnd == IntPtr.Zero)
                continue;

            if (candidate.ProcessId == ownProcessId)
                continue; // never target EyeCtrl's own overlay

            if (candidate.IsShell)
            {
                shell ??= candidate;
                continue;
            }

            if (InputIntegrityRules.BlocksInjectedInput(candidate.Facts, self))
                continue; // a higher-integrity window is a second blocked attempt

            return candidate.Hwnd;
        }

        return shell?.Hwnd;
    }
}
