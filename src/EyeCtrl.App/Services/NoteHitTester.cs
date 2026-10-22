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

/// <summary>
/// The named null object headless tests pass instead of omitting the hit-tester argument
/// (T2 fixup 01 M2): <see cref="NoteManager"/>'s constructor treats an absent argument as the
/// production <see cref="NoteHitTester"/>, so "I do not exercise the own-window conjunct" is a
/// visible choice, and the conjunct switches off only for this sentinel type. Returns
/// <see cref="IntPtr.Zero"/> for everything, which no real root window equals.
/// </summary>
internal sealed class InactiveNoteHitTester : INoteHitTester
{
    public static readonly InactiveNoteHitTester Instance = new();

    private InactiveNoteHitTester() { }

    public IntPtr RootWindowAt(PhysicalPoint point) => IntPtr.Zero;
}
