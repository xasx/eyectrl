namespace EyeCtrl.App.Overlays;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.Controls;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

public partial class GlassBackdropWindow : OverlayWindow
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
    }

    private void EnsureHandle()
    {
        _ = new System.Windows.Interop.WindowInteropHelper(this).Handle;
    }

    // ── Backdrop Type ──

    public void ApplyBackdrop(BackdropSettings settings)
    {
        if (!settings.Enabled)
        {
            ClearContent();
            return;
        }

        ClearContent();

        // Phase 1: Native DWM backdrops (Win11 22H2+). Work best.
        if (settings.Type == BackdropType.Mica && TryApplyMica())
            return;

        if (settings.Type == BackdropType.Acrylic && TryApplyAcrylicViaDwm())
            return;

        // Phase 2: Legacy SetWindowCompositionAttribute acrylic (Win10+)
        ClearDwmBackdrop();
        if (settings.Type == BackdropType.Mica || settings.Type == BackdropType.Acrylic)
        {
            if (TryApplyAcrylic())
                return;
        }

        // Phase 3: Solid darken fallback
        ApplySolidDarken(settings.Opacity);
    }

    private bool TryApplyAcrylic()
    {
        var data = default(WindowCompositionAttributeData);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 0x20,
                GradientColor = 0x01000000
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

        _hideButton = DwellArcButton.Create(dipCenter, UIConstants.ButtonRadius, "\u2715");
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
