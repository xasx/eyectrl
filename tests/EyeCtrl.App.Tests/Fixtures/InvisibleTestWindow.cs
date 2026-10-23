namespace EyeCtrl.App.Tests.Fixtures;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;

/// <summary>
/// Makes a REAL window shown by a test invisible and non-activating, BEFORE its first
/// <c>Show()</c>. The owner controls the PC only by eye-tracking: a test that paints a window on
/// the screen (a toolbar, a keyboard, a banner, a ring) takes the foreground, covers what they are
/// looking at and can trigger their gaze dwell. Every test that calls a showing API on a real window
/// goes through this helper (or the live-check opt-in, or an allow-list entry with a reason);
/// <c>NoVisibleTestWindowGuardTests</c> scans the test sources and fails otherwise.
///
/// <para>
/// <see cref="Prepare{T}"/> is the default and is F6's pattern: all the windows it is used for
/// (Toolbar, EdgeButtonBar, EyeCareBanner, EyeCareExercise, ActionMenu, Keyboard) are layered
/// (<c>AllowsTransparency=True</c>), so <c>Opacity = 0</c> makes them paint nothing while layout,
/// geometry, hit-testing, animations and events run identically; <c>ShowActivated = false</c> keeps
/// them from taking the foreground. It is only safe while production never writes the window's
/// <c>Opacity</c> (or animates it) after the show — a fade-in would raise it again.
/// <c>InvisibleTestWindowTests</c> pins that for each of those window types by scanning the product
/// source.
/// </para>
///
/// <para>
/// <see cref="PrepareCloaked{T}"/> is for a window whose product code DOES write its own Opacity
/// (<c>ActivationButtonWindow</c> fades in on <c>ShowAt</c>; no test needs this today: its halo tests never call
/// <c>Show()</c> and must NOT create the handle early, see below): it is cloaked through DWM
/// (<c>DWMWA_CLOAK</c>), which hides a window from composition whatever its Opacity does and keeps
/// its position, size and visuals — the same mechanism the product's <c>GlassBackdropWindow</c> uses
/// to hide a first frame. It creates the window handle early (<c>EnsureHandle</c>), so use it only
/// where no test step depends on the handle not existing yet. THAT HAS BITTEN: a window that already has an HWND
/// stores <c>Left</c>/<c>Top</c> through the real monitor's DPI (whole device pixels), so a test that fakes the scale
/// with <c>DpiHelper.Initialize</c> and reads <c>Left</c>/<c>Top</c> back (FixationHaloRenderTests'
/// "drawn centre = B") is off by one pixel on a machine whose real DPI differs (CI at 1.0).
/// </para>
/// </summary>
internal static class InvisibleTestWindow
{
    private const int DwmaCloak = 13;
    private const int DwmaCloaked = 14;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    /// <summary>Opacity 0 and no activation, applied before the first show. Returns the window.</summary>
    internal static T Prepare<T>(T window) where T : Window
    {
        window.Opacity = 0;
        window.ShowActivated = false;
        RuntimeWindowMonitor.MarkPrepared(window);
        return window;
    }

    /// <summary>
    /// No activation, and the window is cloaked by DWM before its first show (its Opacity is left
    /// alone, because the window's own code writes it). Returns the window.
    /// </summary>
    internal static T PrepareCloaked<T>(T window) where T : Window
    {
        window.ShowActivated = false;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var on = 1;
        _ = DwmSetWindowAttribute(handle, DwmaCloak, ref on, sizeof(int));
        RuntimeWindowMonitor.MarkPrepared(window);
        return window;
    }

    /// <summary>
    /// For <c>GlassBackdropWindow</c>, which is NOT layered (<c>AllowsTransparency=False</c>, so Opacity does
    /// not hide it) and cloaks itself around its own show: it keeps the product's full-screen sizing but is
    /// positioned at -30000 through the existing <c>PositionForShow</c> seam, the way the backdrop tests already
    /// show it, and it never takes the foreground. It holds only for <c>ShowBackdrop</c> (which calls
    /// <c>PositionForShow</c> first); the window's plain <c>Show()</c> uncloaks and re-covers the screen without the
    /// seam, so <c>NoVisibleTestWindowGuardTests</c> rejects a <c>PrepareOffScreen</c> window that is shown any other
    /// way. Use only where the test asserts nothing about an absolute on-screen position. Returns the window.
    /// </summary>
    internal static GlassBackdropWindow PrepareOffScreen(GlassBackdropWindow window)
    {
        window.ShowActivated = false;
        window.PositionForShow = w =>
        {
            OverlayPositioning.CoverPrimaryScreen(w);
            w.Left = -30000;
            w.Top = -30000;
        };
        // a DISTINCT tag: the runtime check does not take this window's word for it, it judges its rectangle (a plain Show() would land on a monitor)
        RuntimeWindowMonitor.MarkPrepared(window, offScreen: true);
        return window;
    }

    /// <summary>
    /// True when DWM reports the (never shown) window as cloaked by the application; null when DWM
    /// cannot say (no composition, no handle) — the caller skips instead of asserting then.
    /// </summary>
    internal static bool? IsCloaked(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return null;
        return DwmGetWindowAttribute(handle, DwmaCloaked, out var value, sizeof(int)) == 0 ? value != 0 : null;
    }
}
