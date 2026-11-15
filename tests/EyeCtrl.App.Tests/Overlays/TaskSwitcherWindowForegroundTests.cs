namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;

/// <summary>
/// S2-A reads the foreground window in the switcher's show path and leaves that window out. The
/// read is only meaningful while the owner's app still holds the foreground when the switcher
/// opens, and that rests on one Win32 fact: every overlay is <c>WS_EX_NOACTIVATE</c>, so showing
/// the fullscreen switcher cannot activate it. This pins the fact on the real switcher window.
///
/// <para>
/// The window is constructed and its HWND created — the same handle-creation path <c>Show()</c>
/// takes, where <see cref="OverlayWindow"/> applies the interaction flags — but it is NEVER shown:
/// a shown overlay is the user's only input surface, and showing a fullscreen opaque window would
/// cover the owner's desktop.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class TaskSwitcherWindowForegroundTests
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StaFact]
    public void TheSwitcher_CannotTakeTheForeground_SoTheOwnersAppKeepsIt()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new TaskSwitcherWindow();
        try
        {
            // Headless HWND creation: OnSourceInitialized runs, so the base class's flags are
            // applied exactly as they are before the window can first be seen.
            OverlayWindowTestHarness.EnsureHandleAt(window, 1920, 1080);
            var hwnd = new WindowInteropHelper(window).Handle;
            Check.That(hwnd).IsNotEqualTo(IntPtr.Zero);

            var style = GetWindowLong(hwnd, GwlExStyle);
            Check.WithCustomMessage(
                    $"ex-style 0x{style:X8}: WS_EX_NOACTIVATE must be set. Without it opening the "
                    + "switcher could make EyeCtrl the foreground, the excluded \"current\" window "
                    + "would be the switcher itself, and the owner's app would keep no tile.")
                .That((style & WsExNoActivate) != 0).IsTrue();

            // The switcher must not override the base interaction: a subclass that passed
            // focusable: true would clear exactly the flag asserted above (the LagMarkWindow pin).
            Check.WithCustomMessage("TaskSwitcherWindow must inherit OverlayWindow's WS_EX_NOACTIVATE, "
                    + "not weaken it with its own OnSourceInitialized")
                .That(typeof(TaskSwitcherWindow).GetMethod(
                    "OnSourceInitialized", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .IsNull();
        }
        finally
        {
            window.Close();
        }
    }
}
