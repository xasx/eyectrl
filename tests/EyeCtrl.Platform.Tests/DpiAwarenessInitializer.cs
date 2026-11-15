namespace EyeCtrl.Platform.Tests;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Declares this test process's DPI awareness ONCE, before any test runs.
///
/// <para>
/// WPF's first imaging use (here: the icon tests' <c>Imaging.CreateBitmapSourceFromHBitmap</c> /
/// <c>FromHIcon</c>; in a WPF app, the first media or <c>HwndSource</c> use at startup) sets the
/// PROCESS's DPI awareness when the process has none: measured on this machine, a fresh test host
/// went from DPI_AWARENESS_UNAWARE (SM_CXSCREEN = 1920, virtualized at the owner's 200 %) to SYSTEM
/// aware (SM_CXSCREEN = 3840, physical). The shell's image calls themselves leave awareness alone
/// (measured with a probe that does not touch WPF) — it is WPF that flips it.
/// </para>
///
/// <para>
/// That mid-run, process-wide and irreversible change made
/// <c>Win32InputSimulatorScrollAtAndRestoreTests.GF1_TheBatchedRestoreLeg…</c> fail whenever a
/// concurrent icon test landed between the test's two <c>GetSystemMetrics</c> reads (1920 to encode,
/// 3840 to decode → 959 instead of 1919): a heisen-test whose result depended on test ordering.
/// Declaring the awareness up front removes the flip — every read in the process now agrees, whatever
/// order the tests run in — and <b>system aware matches the product</b>: EyeCtrl.App declares no DPI
/// awareness in either manifest, so WPF makes it system-aware at startup. A host that already fixed
/// its awareness (a manifest, a newer test runner) refuses the call, which is why the result is
/// ignored.
/// </para>
/// </summary>
internal static class DpiAwarenessInitializer
{
    /// <summary>winuser.h: <c>DPI_AWARENESS_CONTEXT_SYSTEM_AWARE</c> is <c>(HANDLE)-2</c>.</summary>
    private static readonly IntPtr SystemAware = new(-2);

    [ModuleInitializer]
    internal static void DeclareSystemAware() => _ = SetProcessDpiAwarenessContext(SystemAware);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
