namespace EyeCtrl.Core.Input;

using EyeCtrl.Core.Models;

/// <summary>
/// The OS cursor's current position, or <c>null</c> when it cannot be read (an input desktop that
/// is not ours, a locked session, a harness without a cursor). R2's verification treats an
/// unreadable cursor as "no evidence": it neither raises nor clears a block.
/// </summary>
public interface ICursorPositionProbe
{
    /// <summary>Reads the current cursor position in physical screen pixels.</summary>
    /// <returns>The position, or <c>null</c> when the read failed.</returns>
    PhysicalPoint? TryGetCursorPosition();
}

/// <summary>
/// One process's access-relevant token facts, as R1's pre-flight integrity check reads them.
/// </summary>
/// <param name="ProcessName">The process's file name (e.g. <c>Taskmgr.exe</c>), or null when the
/// process could not even be named.</param>
/// <param name="IntegrityLevel">The token's integrity level RID (0x1000 low, 0x2000 medium,
/// 0x3000 high, 0x4000 system, 0x5000 protected), or null when it could not be read.</param>
/// <param name="Elevated">Whether the token is elevated (a full administrator token), or null when
/// unknown.</param>
/// <param name="HasUIAccess">Whether the token carries UIAccess, or null when unknown.</param>
/// <param name="AccessDenied">True when the process token itself could not be opened. For an
/// unprivileged caller that means "higher than ours": Windows refuses the query before it will
/// answer, which is exactly the class of process that can block injected input.</param>
public readonly record struct ProcessTokenFacts(
    string? ProcessName,
    int? IntegrityLevel,
    bool? Elevated,
    bool? HasUIAccess,
    bool AccessDenied)
{
    /// <summary>Nothing could be read: no process, no integrity level, no answer.</summary>
    public static ProcessTokenFacts Unknown { get; } = new(null, null, null, null, false);
}

/// <summary>EyeCtrl's own token facts, read once at startup and reused for every R1 comparison.</summary>
/// <param name="IntegrityLevel">Our integrity level RID, or null when it could not be read.</param>
/// <param name="Elevated">Whether our token is elevated.</param>
/// <param name="HasUIAccess">Whether our token carries UIAccess (a future installed build).</param>
public readonly record struct SelfTokenFacts(int? IntegrityLevel, bool Elevated, bool HasUIAccess);

/// <summary>
/// R1's read-only view of the token facts that decide whether Windows will accept EyeCtrl's
/// injected input: our own token, the foreground window's process, and the process under a target
/// point. Implementations must be side-effect free and safe to call from the input thread; all
/// failures degrade to <see cref="ProcessTokenFacts.Unknown"/> rather than throwing.
/// </summary>
public interface IInputIntegrityProbe
{
    /// <summary>Reads EyeCtrl's own token facts. Cached by the implementation.</summary>
    SelfTokenFacts ReadSelf();

    /// <summary>Reads the current foreground window's process token facts.</summary>
    ProcessTokenFacts InspectForeground();

    /// <summary>Reads the process under <paramref name="point"/> ("what would this click land on").</summary>
    /// <param name="point">The target in physical screen pixels.</param>
    ProcessTokenFacts InspectPoint(PhysicalPoint point);

    /// <summary>Reads one window's process token facts, without moving or activating anything.</summary>
    /// <param name="hwnd">The window handle; zero returns <see cref="ProcessTokenFacts.Unknown"/>.</param>
    ProcessTokenFacts InspectWindow(IntPtr hwnd);
}
