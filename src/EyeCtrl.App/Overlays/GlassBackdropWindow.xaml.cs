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
    private const int DwmsbtAcrylic = 4;
    private const int Win11Build22H2 = 22621;

    private Rectangle? _solidBackground;
    private DwellArcButton? _hideButton;

    public GlassBackdropWindow()
    {
        InitializeComponent();
        IsClickThrough = true;
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

    // ── Show / Hide ──

    public new void Show()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        if (screen == null)
            return;
        Left = screen.Bounds.X / DpiHelper.ScaleX;
        Top = screen.Bounds.Y / DpiHelper.ScaleY;
        Width = screen.Bounds.Width / DpiHelper.ScaleX;
        Height = screen.Bounds.Height / DpiHelper.ScaleY;
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
            int backdropType = 2;
            int result = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
            return result == 0;
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
            return result == 0;
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
            int backdropType = 0;
            DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));
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
            BackdropCanvas.Children.Remove(_hideButton.Background);
            BackdropCanvas.Children.Remove(_hideButton.ProgressPath);
            if (_hideButton.Symbol != null)
                BackdropCanvas.Children.Remove(_hideButton.Symbol);
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
