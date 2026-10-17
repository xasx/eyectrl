namespace EyeCtrl.App.Services;

using System.Runtime.InteropServices;
using EyeCtrl.Core.Models;

/// <summary>
/// The production <see cref="INoteHitTester"/> (P2 D2): <c>WindowFromPoint</c> +
/// <c>GetAncestor(GA_ROOT)</c>, the same pattern ExternalDwellService uses for its chip check.
/// The app is PerMonitorV2, so physical coordinates are correct as-is. Note that
/// <c>WindowFromPoint</c> skips <c>WS_EX_TRANSPARENT</c> windows — EyeCtrl's click-through
/// overlays are invisible to it, which is exactly why the suspend and occluder conjuncts exist
/// alongside this one (P2 D1/D3) rather than this gate being trusted alone.
/// </summary>
internal sealed class NoteHitTester : INoteHitTester
{
    public IntPtr RootWindowAt(PhysicalPoint point)
    {
        var hwnd = WindowFromPoint(new POINT { X = (int)Math.Round(point.X), Y = (int)Math.Round(point.Y) });
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;
        var root = GetAncestor(hwnd, GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    private const uint GA_ROOT = 2;
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
