namespace EyeCtrl.Platform.Tests;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Declares this test process's DPI awareness ONCE, before any test runs.
///
/// <para>
/// The shell's image factory (<c>SHCreateItemFromParsingName</c> / <c>IShellItemImageFactory</c>,
/// used by S3-B's real icon extraction test) sets the PROCESS's DPI awareness on first use when the
/// process has none: measured here, the first extraction flipped a fresh test host from
/// DPI_AWARENESS_UNAWARE (SM_CXSCREEN = 1920, virtualized at the owner's 200 %) to system aware
/// (SM_CXSCREEN = 3840, physical). That is a mid-run, process-wide change that cannot be undone, and
/// it made <c>Win32InputSimulatorScrollAtAndRestoreTests.GF1_TheBatchedRestoreLeg…</c> fail whenever a
/// concurrent icon test landed between the test's two <c>GetSystemMetrics</c> reads (1920 to encode,
/// 3840 to decode → 959 instead of 1919): a heisen-test whose result depended on test ordering.
/// </para>
///
/// <para>
/// Declaring the awareness up front removes the flip: every read in the process now agrees, whatever
/// order the tests run in. Per-monitor v2 matches what the WPF app itself runs with; a host that
/// already fixed its awareness (a manifest, a newer test runner) simply refuses the call, which is
/// why the result is ignored.
/// </para>
/// </summary>
internal static class DpiAwarenessInitializer
{
    /// <summary>winuser.h: <c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c> is <c>(HANDLE)-4</c>.</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [ModuleInitializer]
    internal static void DeclarePerMonitorAwareV2() => _ = SetProcessDpiAwarenessContext(PerMonitorAwareV2);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
