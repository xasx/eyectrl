namespace EyeCtrl.Core.Diagnostics;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
/// Turns Windows Error Reporting's crash dialog off for THIS process only, so a crash or an
/// <see cref="Environment.FailFast(string)"/> can never leave a modal dialog that a gaze-only
/// owner cannot dismiss.
/// <para>
/// The process-scoped WER flag is deliberately the only switch. <c>SetErrorMode</c> /
/// <c>SEM_NOGPFAULTERRORBOX</c> is never used: the error mode is inherited by child processes, and
/// EyeCtrl starts other programs for the owner. Nothing is written to the registry, so an
/// installed build and a dev build alike set the flag in their own startup path, and no uninstall
/// cleanup exists to forget.
/// </para>
/// <para>
/// Call it as the first statement of every EyeCtrl process that can crash (App, Supervisor, Setup),
/// before anything that can <c>FailFast</c>. It never throws: a failed HRESULT (or a missing
/// export) is returned in the outcome for the caller to log as one WRN.
/// </para>
/// </summary>
public static class CrashDialogPolicy
{
    /// <summary>
    /// <c>WER_FAULT_REPORTING_NO_UI</c> from the Windows SDK's <c>werapi.h</c> (<c>0x20</c>):
    /// fault-reporting UI is not shown for this process.
    /// </summary>
    public const uint WerFaultReportingNoUi = 0x20;

    /// <summary>The one INF line an entry point logs once the flag was accepted.</summary>
    public const string OffLine = "Crash dialog: off (WER no-UI)";

    /// <summary>
    /// Applies <see cref="WerFaultReportingNoUi"/> to the current process. Never throws: the
    /// outcome carries either the success line or one WRN line with the failed HRESULT.
    /// </summary>
    /// <returns>The outcome; call <see cref="CrashDialogOutcome.Report"/> with the process's two log sinks.</returns>
    public static CrashDialogOutcome Disable() => Disable(WerSetFlags);

    /// <summary>
    /// The test seam: <paramref name="werSetFlags"/> replaces the native call, so a headless test
    /// can drive the failure path without touching WER.
    /// </summary>
    internal static CrashDialogOutcome Disable(Func<uint, uint> werSetFlags)
    {
        try
        {
            uint hresult = werSetFlags(WerFaultReportingNoUi);
            // FAILED(hr) is hr < 0, the Windows HRESULT convention; S_OK is 0.
            return (int)hresult < 0
                ? CrashDialogOutcome.Failed(unchecked((int)hresult))
                : CrashDialogOutcome.Accepted();
        }
        catch (Exception ex)
        {
            // A missing export (a stripped-down Windows) or a broken side-by-side load must not
            // take the process down: report it like any other failed HRESULT.
            return CrashDialogOutcome.Failed(Marshal.GetHRForException(ex));
        }
    }

    // kernel32.dll exports WerSetFlags (verified with dumpbin against the SDK's werapi.h, which
    // declares it in Kernel32.lib); the DWORD return value is the HRESULT. The flag value is
    // WER_FAULT_REPORTING_NO_UI (32) from werapi.h, not from memory.
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WerSetFlags(uint dwFlags);
}

/// <summary>
/// The outcome of <see cref="CrashDialogPolicy.Disable"/>: whether WER accepted the no-UI flag,
/// and the one line to log either way.
/// </summary>
public sealed class CrashDialogOutcome
{
    private CrashDialogOutcome(bool succeeded, int hResult)
    {
        Succeeded = succeeded;
        HResult = hResult;
        Line = succeeded
            ? CrashDialogPolicy.OffLine
            : "Crash dialog: WER no-UI could not be set (HRESULT 0x"
              + unchecked((uint)hResult).ToString("X8", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>True when WER accepted <see cref="CrashDialogPolicy.WerFaultReportingNoUi"/>.</summary>
    public bool Succeeded { get; }

    /// <summary>The failed HRESULT as a signed 32-bit value; <c>0</c> on success.</summary>
    public int HResult { get; }

    /// <summary>The single line to log: the INF wording on success, the WRN wording otherwise.</summary>
    public string Line { get; }

    /// <summary>
    /// Writes <see cref="Line"/> to exactly one sink — <paramref name="info"/> on success,
    /// <paramref name="warning"/> on failure — so every entry point logs one line and the failure
    /// can never be dropped as an information message.
    /// </summary>
    /// <param name="info">The process's information sink (a logger, once it exists).</param>
    /// <param name="warning">The process's warning sink.</param>
    public void Report(Action<string> info, Action<string> warning)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(warning);

        if (Succeeded)
            info(Line);
        else
            warning(Line);
    }

    internal static CrashDialogOutcome Accepted() => new(true, 0);

    internal static CrashDialogOutcome Failed(int hResult) => new(false, hResult);
}
