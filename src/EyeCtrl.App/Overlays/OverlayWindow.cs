namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public bool IsClickThrough { get; init; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetOverlayInteraction(clickThrough: IsClickThrough, focusable: false);
    }

    /// <summary>
    /// Adjusts the overlay's extended window style at runtime.
    /// </summary>
    /// <param name="clickThrough">When true, mouse input passes through to windows below.</param>
    /// <param name="focusable">
    /// When true the window can be activated and receive keyboard input (e.g. for Esc handling
    /// or focusable buttons). Independent of <paramref name="clickThrough"/>, so a window can be
    /// keyboard-focusable yet still let the mouse pass through (used during calibration capture).
    /// </param>
    public void SetOverlayInteraction(bool clickThrough, bool focusable)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;

        if (clickThrough) style |= WS_EX_TRANSPARENT;
        else style &= ~WS_EX_TRANSPARENT;

        if (focusable) style &= ~WS_EX_NOACTIVATE;
        else style |= WS_EX_NOACTIVATE;

        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }
}
