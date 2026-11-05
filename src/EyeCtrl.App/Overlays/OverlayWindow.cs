namespace EyeCtrl.App.Overlays;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.Controls.Theme;
using Serilog;

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

    /// <summary>
    /// The undocumented (read-only) z-band query: 1 = ZBID_DESKTOP, 2 = ZBID_UIACCESS,
    /// 16 = ZBID_SYSTEM_TOOLS. Used by <see cref="LogBand"/> only; callers must tolerate a
    /// failure (older builds, or a window the shell does not band).
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowBand", SetLastError = true)]
    private static extern bool GetWindowBandNative(IntPtr hwnd, out uint band);

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
    /// Whether this window joins the UIAccess z-band (ZBID_UIACCESS, band 2) on its first show.
    /// True for every overlay; the settings window overrides it false because it is an ordinary
    /// window (Q13), deliberately outside the C9 F2 overlay-band scope.
    /// </summary>
    protected virtual bool EntersUiAccessBand => true;

    /// <summary>
    /// The HWND the one-time band raise already ran for (<see cref="IntPtr.Zero"/> before that).
    /// Keyed on the handle rather than a bool so a recreated HWND would still get the raise; the
    /// ring team's constraint 1 forbids anything more frequent (a per-frame raise is the stutter
    /// and z-order thrash bug).
    /// </summary>
    private IntPtr _uiAccessBandHwnd;

    /// <summary>
    /// How many times this window called the native topmost insert, for the headless band gates:
    /// once from the HWND's creation, plus any explicit <see cref="RaiseToTop"/> (real ordering
    /// triggers only). It never grows from a position update.
    /// </summary>
    internal int BandRaiseCount { get; private set; }

    /// <summary>
    /// The native side of the band raise, injectable so the headless gates can record the call
    /// (HWND, insert-after, flags) without a shown window. Production is the real
    /// <c>SetWindowPos</c>.
    /// </summary>
    internal Action<IntPtr, IntPtr, uint> SetWindowPosForBand { get; set; } = NativeSetWindowPosForBand;

    private static void NativeSetWindowPosForBand(IntPtr hwnd, IntPtr insertAfter, uint flags) =>
        _ = SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags);

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

        if (EntersUiAccessBand)
            RaiseIntoUiAccessBandOnce();
    }

    /// <summary>
    /// C9 F2: gives the overlay the z-band the shell cannot cover. With
    /// <c>ShowInTaskbar="False"</c> WPF gives a window a hidden, non-topmost owner, so an overlay
    /// starts in ZBID_DESKTOP (band 1) despite being <c>WS_EX_TOPMOST</c>; Start, Search and the
    /// notification centre live above that band. One
    /// <c>SetWindowPos(HWND_TOPMOST, NOMOVE|NOSIZE|NOACTIVATE)</c> moves it into ZBID_UIACCESS
    /// (band 2) — the band every overlay that already called <see cref="RaiseToTop"/> sits in
    /// (S8 A2 evidence, <c>C:\t\s8\a2-bands.csv</c>).
    ///
    /// <para>
    /// Called from <see cref="OnSourceInitialized"/>: the HWND exists and <c>Show()</c> creates it
    /// BEFORE the first visible frame, so the window never appears in band 1. ShowWindow uses
    /// SW_SHOWNA for a non-activating overlay and does not change the z-order, so the band sticks.
    /// Once per window lifetime, never from a position update: the ring moves on every gaze frame,
    /// and raising there is exactly the re-assert storm this must not reintroduce.
    /// </para>
    /// </summary>
    internal void RaiseIntoUiAccessBandOnce()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;          // no HWND yet: the HWND-creation call does it
        if (hwnd == _uiAccessBandHwnd) return;    // already raised for this HWND

        _uiAccessBandHwnd = hwnd;
        RaiseIntoBand(hwnd);
        LogBand(hwnd);
    }

    /// <summary>Per-overlay startup diagnostic: one read-only line with the band the HWND landed in.</summary>
    private void LogBand(IntPtr hwnd)
    {
        try
        {
            if (GetWindowBandNative(hwnd, out var band))
            {
                Log.Information(
                    "Overlay band: {Overlay} hwnd=0x{Hwnd:X} band={Band}",
                    GetType().Name, hwnd.ToInt64(), band);
            }
            else
            {
                Log.Information(
                    "Overlay band: {Overlay} hwnd=0x{Hwnd:X} band=unknown (GetWindowBand failed, win32={Error})",
                    GetType().Name, hwnd.ToInt64(), Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex)
        {
            // A diagnostic must never be able to break an overlay's first show.
            Log.Information(
                "Overlay band: {Overlay} hwnd=0x{Hwnd:X} band=unknown ({Error})",
                GetType().Name, hwnd.ToInt64(), ex.GetType().Name);
        }
    }

    /// <summary>One topmost insert with the exact flags the band fix permits.</summary>
    private void RaiseIntoBand(IntPtr hwnd)
    {
        BandRaiseCount++;
        SetWindowPosForBand(hwnd, HWND_TOPMOST, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Re-inserts this window at the top of the top-most Z-order band without moving, resizing, or
    /// activating it. Keeps an overlay above sibling top-most overlays shown after it — e.g. the
    /// manual-selection gaze indicator, which must draw above the opaque magnifier even though its
    /// own ViewModel may have shown it first (a second Show() is a no-op that would not re-raise it).
    ///
    /// <para>
    /// For REAL ordering triggers only (a strip re-shown above notes, the gaze dot above the
    /// magnifier / edge bar). The band membership itself is established once by
    /// <see cref="RaiseIntoUiAccessBandOnce"/>; never call this from a position update or a
    /// per-frame path — that is the stutter/z-order-thrash bug, and the focusable:false styles
    /// plus SWP_NOACTIVATE here are what keep the overlay from stealing the foreground.
    /// </para>
    /// </summary>
    public void RaiseToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            RaiseIntoBand(hwnd);
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
