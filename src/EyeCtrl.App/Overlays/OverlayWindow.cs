namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.Controls.Theme;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// Gives every overlay the themed font family. Every window class in this assembly
    /// derives from OverlayWindow (see <c>NoWindowClass_BypassesOverlayWindow</c>), so this
    /// single assignment reaches the whole UI (design spec §8.8).
    ///
    /// A base constructor rather than TextElement.FontFamilyProperty.OverrideMetadata: that
    /// is process-wide and can only be called once, which collides across tests in one
    /// runner. And it runs before the derived class's InitializeComponent, so a deliberate
    /// FontFamily in a window's XAML still wins.
    ///
    /// Read from Tokens rather than a resource lookup because Tokens is static and needs no
    /// WPF Application — see Tokens' own doc comment.
    ///
    /// This assignment sets a LOCAL value, which outranks a Style setter — an implicit Style
    /// that later sets FontFamily on a derived OverlayWindow would be silently ignored here,
    /// unlike FontSize/FontWeight set the same way.
    /// </summary>
    public OverlayWindow()
    {
        FontFamily = Tokens.FontStandard;
    }

    public bool IsClickThrough { get; init; }

    /// <summary>
    /// When true, this window is excluded from screen capture via WDA_EXCLUDEFROMCAPTURE. The
    /// ManualSelection magnifier and action-menu preview grab the screen with GDI CopyFromScreen;
    /// any overlay composited over the captured region (glass backdrop, magnifier, gaze indicator,
    /// cursor circle, activation button, action menu) would otherwise be baked into that capture —
    /// e.g. the dark backdrop showing instead of the real content. Excluded windows stay fully
    /// visible to the user; only screen capture (and screen recordings) skip them.
    /// </summary>
    public bool ExcludeFromScreenCapture { get; init; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // This is the ONLY place every overlay gets its interaction flags. A subclass constructor
        // cannot do it — SetOverlayInteraction early-returns until the HWND exists. That used to
        // happen only at Show(); since the flicker fix a window may ensure its handle up front
        // (GlassBackdropWindow.ShowBackdrop prepares the material before it becomes visible), and
        // this handler runs then, so the flags and the capture exclusion are in place before the
        // window can be seen either way. The focusable:false here is what gives all overlays
        // WS_EX_NOACTIVATE, i.e. what stops them stealing the foreground; do not weaken it
        // without checking the callers that resolve the user's real foreground window.
        SetOverlayInteraction(clickThrough: IsClickThrough, focusable: false);

        if (ExcludeFromScreenCapture)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
    }

    /// <summary>
    /// Re-inserts this window at the top of the top-most Z-order band without moving, resizing, or
    /// activating it. Keeps an overlay above sibling top-most overlays shown after it — e.g. the
    /// manual-selection gaze indicator, which must draw above the opaque magnifier even though its
    /// own ViewModel may have shown it first (a second Show() is a no-op that would not re-raise it).
    /// </summary>
    public void RaiseToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
