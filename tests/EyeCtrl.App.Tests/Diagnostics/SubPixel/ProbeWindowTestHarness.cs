namespace EyeCtrl.App.Tests.Diagnostics.SubPixel;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Diagnostics.SubPixel;

/// <summary>
/// SP-8 fixup 02: host-independent probe geometry. A WPF <c>Window</c>'s top-level HWND is clamped
/// by the OS to the host's maximum track size (about the virtual screen), and WPF writes the
/// clamped size back into <c>Width</c>/<c>Height</c> — so on CI's small runner the probe laid itself
/// out smaller than the tests asked (the corner digit's bottom 764 at a requested 1200, and Start's
/// and End test's hit areas collapsing into each other). The harness raises the window's maximum
/// track size through <c>WM_GETMINMAXINFO</c> and then re-applies the requested size, so the window,
/// its <c>Width</c>/<c>Height</c> and its laid-out content are exactly the requested size on any
/// host. Test-side only: the probe's own code reads its window, never the host screen.
/// </summary>
internal static class ProbeWindowTestHarness
{
    private const int WmGetMinMaxInfo = 0x0024;

    /// <summary>The largest track size the harness grants, physical px: beyond any plausible host.</summary>
    private const int MaxTrackPx = 20000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    /// <summary>
    /// Sets <paramref name="window"/>'s requested size and creates its HWND, then forces that size
    /// through the host's track clamp. Never shows the window; the caller drives phases and layout
    /// exactly as before. The hook lives on the window's own <see cref="HwndSource"/> until it
    /// closes, so every later resize keeps the raised cap too.
    /// </summary>
    public static void EnsureHandleAt(SubPixelProbeWindow window, double widthDip, double heightDip)
    {
        window.Width = widthDip;
        window.Height = heightDip;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        // The initial create happens before this hook can run, so the OS may already have clamped it
        // (and WPF written the clamped size back into Width/Height). Lift the cap, then re-apply the
        // size: this resize consults WM_GETMINMAXINFO again, now with the hook in place.
        var source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException(
                "The probe window has no HwndSource after EnsureHandle; the harness cannot lift the track cap.");
        source.AddHook(RaiseMaxTrackSize);
        window.Width = widthDip;
        window.Height = heightDip;
    }

    private static IntPtr RaiseMaxTrackSize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;

        var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        info.MaxTrackSize.X = MaxTrackPx;
        info.MaxTrackSize.Y = MaxTrackPx;
        Marshal.StructureToPtr(info, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }
}
