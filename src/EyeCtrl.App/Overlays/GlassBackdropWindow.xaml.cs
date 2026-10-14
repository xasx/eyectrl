namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public partial class GlassBackdropWindow : OverlayWindow, IGlassBackdropOverlay
{
    private const int AccentEnableAcrylicBlurBehind = 4;

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmwaUseImmersiveDarkMode = 20;

    // DWM_SYSTEMBACKDROP_TYPE (dwmapi.h) — the system material painted behind the window.
    // 0=Auto, 1=None, 2=MainWindow(Mica), 3=TransientWindow(Acrylic, the only one that blurs),
    // 4=TabbedWindow(Mica Alt, wallpaper-adaptive but non-blurring).
    // The previous code used 4 for "Acrylic", which actually selects Mica Alt — so the
    // backdrop was never a glassy blur even when it did render. Acrylic is 3.
    private const int DwmsbtNone = 1;
    private const int DwmsbtMica = 2;
    private const int DwmsbtAcrylic = 3;
    private const int Win11Build22H2 = 22621;

    private Rectangle? _solidBackground;
    private DwellArcButton? _hideButton;

    public GlassBackdropWindow()
    {
        InitializeComponent();
        IsClickThrough = true;
        ExcludeFromScreenCapture = true; // the dimmed backdrop must not be captured as the magnifier content
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(hwnd);

        // The material fix (2026-10-14): a non-layered HwndSource's composition target clears to
        // opaque black, and WPF paints that over whatever DWM puts behind the client — so no
        // material ever showed, whatever the backdrop type (rounds 1-2 measured pure 0,0,0). The
        // clear colour must be transparent before any material is applied.
        if (source?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;

        source?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (msg == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    // ── Show / Hide ──

    public new void Show()
    {
        if (System.Windows.Forms.Screen.PrimaryScreen == null)
            return;
        OverlayPositioning.CoverPrimaryScreen(this);
        base.Show();
        EnsureHandle();
    }

    public new void Hide()
    {
        base.Hide();
        ClearContent();
        HideEscapeHint();
    }

    // ── Escape hint ──

    /// <summary>Shows the "Press Esc to exit" hint (bottom-right; CalibrationWindow's cancel hint
    /// is carded since the Daylight round, so the two no longer mirror each other).</summary>
    public void ShowEscapeHint() => EscapeHint.Visibility = Visibility.Visible;

    /// <summary>Hides the escape hint. Also called from <see cref="Hide"/> so a stale hint never
    /// carries over into an unrelated later use of this window (e.g. the plain action menu, which
    /// never asks for it).</summary>
    public void HideEscapeHint() => EscapeHint.Visibility = Visibility.Collapsed;

    private void EnsureHandle()
    {
        _ = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    }

    // ── Backdrop Type ──

    /// <summary>
    /// Applies the settings' material. Both named types (Mica and Acrylic) are the legacy accent
    /// blur since 2026-10-14: the DWM system backdrops only ever showed their flat inactive
    /// fallback on this never-activated, tool-window overlay, while the accent blur was live on
    /// exactly this window. The solid darken remains the fallback when the accent call fails, at
    /// the user's opacity.
    /// </summary>
    public void ApplyBackdrop(BackdropSettings settings) => ApplyBackdropCore(settings, TryApplyAcrylic);

    /// <summary>
    /// The material decision, with the accent call injectable so it can be pinned headlessly
    /// (production passes <see cref="TryApplyAcrylic"/>; the gate passes recording or failing
    /// stubs).
    /// </summary>
    internal void ApplyBackdropCore(BackdropSettings settings, Func<double, bool> applyAccent)
    {
        if (!settings.Enabled)
        {
            ClearContent();
            return;
        }

        ClearContent();

        // ClearDwmBackdrop first: type NONE, frame collapsed — the state production's Phase 2
        // always ran from. The system-backdrop paths are no longer reached from here.
        if (settings.Type == BackdropType.Mica || settings.Type == BackdropType.Acrylic)
        {
            ClearDwmBackdrop();
            if (applyAccent(settings.Opacity))
                return;
        }

        // The accent failed: the solid darken keeps the overlay usable, at the user's opacity.
        ApplySolidDarken(settings.Opacity);
    }

    /// <summary>
    /// The accent's black tint at the user's opacity — RGB black, alpha = the opacity. This is what
    /// the opacity slider controls on the accent path: the strength of the tint over the live blur
    /// (it used to be the alpha of the solid dark fill).
    /// </summary>
    internal static int AccentGradientColor(double opacity) =>
        (int)(Math.Clamp(opacity, 0.0, 1.0) * 255) << 24;

    /// <summary>
    /// The legacy (undocumented) acrylic blur — <c>SetWindowCompositionAttribute</c> with
    /// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> — production's material for both named backdrop types
    /// since 2026-10-14. Known quirk of the API: the accent can visibly lag while a window is
    /// dragged; irrelevant for a static, full-screen overlay.
    /// </summary>
    /// <returns>True when the call was accepted (no darken fallback needed).</returns>
    internal bool TryApplyAcrylic(double opacity)
    {
        var data = default(WindowCompositionAttributeData);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 0x20,
                GradientColor = AccentGradientColor(opacity)
            };
            data.SizeOfData = Marshal.SizeOf(accent);
            data.Attribute = 19;
            data.Data = Marshal.AllocHGlobal(data.SizeOfData);
            Marshal.StructureToPtr(accent, data.Data, false);
            var result = SetWindowCompositionAttribute(hwnd, ref data);
            return result == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (data.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(data.Data);
        }
    }

    /// <summary>
    /// The DWM system Mica (DWMSBT_MAINWINDOW). No longer reached from <see cref="ApplyBackdrop"/>
    /// since the 2026-10-14 decision — on this never-activated overlay it only ever showed its flat
    /// inactive fallback — but kept as the documented alternative and for the round-2 showcase's
    /// probes.
    /// </summary>
    private bool TryApplyMica()
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return false;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            int backdropType = DwmsbtMica;
            int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            if (result != 0)
                return false;
            ExtendFrameIntoClient(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The DWM system Acrylic (DWMSBT_TRANSIENTWINDOW). No longer reached from
    /// <see cref="ApplyBackdrop"/> since the 2026-10-14 decision (see <see cref="TryApplyMica"/>);
    /// kept as the documented alternative and for the round-2 showcase's probes.
    /// </summary>
    private bool TryApplyAcrylicViaDwm()
    {
        if (Environment.OSVersion.Version.Build < Win11Build22H2)
            return false;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            int backdropType = DwmsbtAcrylic;
            int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            if (result != 0)
                return false;
            ExtendFrameIntoClient(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ClearDwmBackdrop()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int backdropType = DwmsbtNone; // DWMSBT_NONE, not Auto(0): a lingering Auto material would override the SolidDarken fallback.
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            ExtendFrameIntoClient(false);
        }
        catch
        {
        }
    }

    /// <summary>
    /// Extends (or collapses) the DWM frame across the whole client area. A borderless
    /// (WindowStyle=None) window has no non-client region for the system material to paint in,
    /// so without extending the frame the Mica/Acrylic backdrop never covers the window — the
    /// fallback dark fill is all you'd see. All margins = -1 is the documented "extend across
    /// the entire window" sentinel; 0 collapses it back to the default border.
    /// </summary>
    private void ExtendFrameIntoClient(bool extend)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var margins = new Margins
            {
                Left = extend ? -1 : 0,
                Right = extend ? -1 : 0,
                Top = extend ? -1 : 0,
                Bottom = extend ? -1 : 0
            };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
        catch
        {
        }
    }

    private void ApplySolidDarken(double opacity)
    {
        _solidBackground = new Rectangle
        {
            Width = Width,
            Height = Height,
            Fill = new SolidColorBrush(Color.FromArgb(
                (byte)(255 * opacity), 18, 18, 18))
        };
        Canvas.SetLeft(_solidBackground, 0);
        Canvas.SetTop(_solidBackground, 0);
        BackdropCanvas.Children.Add(_solidBackground);
    }

    // ── Hide Button ──

    public void DrawHideButton(PhysicalPoint buttonCenter, double progress = 0)
    {
        ClearButton();
        var dipCenter = new Point(
            DpiHelper.PhysicalToLogicalX(buttonCenter.X),
            DpiHelper.PhysicalToLogicalY(buttonCenter.Y));

        // The same escape-hatch shape as the edge buttons: a bare glyph whose drawn face cannot
        // name itself to UIA, so it carries the name explicitly.
        _hideButton = DwellArcButton.Create(
            dipCenter, UIConstants.ButtonRadius, "\u2715",
            accessibleName: StringTable.Current.T("overlays.edge.close"));
        _hideButton.AddTo(BackdropCanvas);

        if (progress > 0)
            _hideButton.UpdateProgress(progress);
    }

    public void UpdateHideButtonProgress(double progress)
    {
        _hideButton?.UpdateProgress(progress);
    }

    private void ClearContent()
    {
        BackdropCanvas.Children.Clear();
        ClearDwmBackdrop();
        _solidBackground = null;
        ClearButton();
    }

    private void ClearButton()
    {
        if (_hideButton != null)
        {
            // Was a hand-copied element list naming Background, ProgressPath and Symbol, which
            // had fallen five behind AddTo (RimHalo, HaloPath, IconFill, IconStroke, Label).
            // That never orphaned anything at runtime, and the reason is luck rather than
            // design: DrawHideButton's only caller runs ApplyBackdrop first, every ApplyBackdrop
            // path calls ClearContent, and ClearContent opens with Children.Clear() — so this
            // method has only ever met an already-empty canvas. One call-site reordering away
            // from a real leak, which is why it now delegates instead of listing.
            _hideButton.RemoveFrom(BackdropCanvas);
            _hideButton = null;
        }
    }

    // ── Win32 Structs ──

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
