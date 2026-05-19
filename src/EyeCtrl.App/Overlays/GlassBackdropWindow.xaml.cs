namespace EyeCtrl.App.Overlays;

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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

    private Rectangle? _solidBackground;
    private Ellipse? _hideButtonCircle;
    private Ellipse? _hideButtonProgressArc;
    private TextBlock? _hideButtonSymbol;
    private PhysicalPoint _hideButtonCenter;
    private BackdropType _currentType;

    public GlassBackdropWindow()
    {
        IsClickThrough = true;
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

        if (settings.Type == BackdropType.Mica && TryApplyMica())
        {
            _currentType = BackdropType.Mica;
        }
        else if (settings.Type == BackdropType.Mica || settings.Type == BackdropType.Acrylic)
        {
            if (TryApplyAcrylic())
                _currentType = BackdropType.Acrylic;
            else
                ApplySolidDarken(settings.Opacity);
        }
        else
        {
            ApplySolidDarken(settings.Opacity);
        }
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
                AccentFlags = 2,
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

    private void ApplySolidDarken(double opacity)
    {
        _currentType = BackdropType.SolidDarken;
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
        _hideButtonCenter = buttonCenter;
        ClearButton();
        var dipX = DpiHelper.PhysicalToLogicalX(buttonCenter.X) - UIConstants.ButtonRadius;
        var dipY = DpiHelper.PhysicalToLogicalY(buttonCenter.Y) - UIConstants.ButtonRadius;
        var dipSize = UIConstants.ButtonRadius * 2;

        _hideButtonCircle = new Ellipse
        {
            Width = dipSize,
            Height = dipSize,
            Fill = new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)),
            Stroke = new SolidColorBrush(Color.FromArgb(100, 100, 100, 100)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(_hideButtonCircle, dipX);
        Canvas.SetTop(_hideButtonCircle, dipY);
        BackdropCanvas.Children.Add(_hideButtonCircle);

        if (progress > 0)
        {
            var circumference = Math.PI * (dipSize - 4);
            _hideButtonProgressArc = new Ellipse
            {
                Width = dipSize - 4,
                Height = dipSize - 4,
                Stroke = Brushes.Lime,
                StrokeThickness = 4,
                StrokeDashArray = new DoubleCollection { circumference * progress, circumference },
                RenderTransform = new RotateTransform(-90, (dipSize - 4) / 2, (dipSize - 4) / 2)
            };
            Canvas.SetLeft(_hideButtonProgressArc, dipX + 2);
            Canvas.SetTop(_hideButtonProgressArc, dipY + 2);
            BackdropCanvas.Children.Add(_hideButtonProgressArc);
        }

        _hideButtonSymbol = new TextBlock
        {
            Text = "\u2715",
            FontSize = 22,
            Foreground = Brushes.White,
            Width = dipSize,
            Height = dipSize,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(_hideButtonSymbol, dipX);
        Canvas.SetTop(_hideButtonSymbol, dipY + (dipSize - 22) / 2 - 2);
        BackdropCanvas.Children.Add(_hideButtonSymbol);
    }

    public void UpdateHideButtonProgress(double progress)
    {
        if (_hideButtonProgressArc == null)
        {
            DrawHideButton(_hideButtonCenter, progress);
            return;
        }
        var dipSize = UIConstants.ButtonRadius * 2;
        var circumference = Math.PI * (dipSize - 4);
        _hideButtonProgressArc.StrokeDashArray = new DoubleCollection { circumference * progress, circumference };

        if (progress <= 0 && _hideButtonProgressArc != null)
        {
            BackdropCanvas.Children.Remove(_hideButtonProgressArc);
            _hideButtonProgressArc = null;
        }
    }

    private void ClearContent()
    {
        BackdropCanvas.Children.Clear();
        _solidBackground = null;
        ClearButton();
    }

    private void ClearButton()
    {
        if (_hideButtonCircle != null)
            BackdropCanvas.Children.Remove(_hideButtonCircle);
        if (_hideButtonProgressArc != null)
            BackdropCanvas.Children.Remove(_hideButtonProgressArc);
        if (_hideButtonSymbol != null)
            BackdropCanvas.Children.Remove(_hideButtonSymbol);
        _hideButtonCircle = null;
        _hideButtonProgressArc = null;
        _hideButtonSymbol = null;
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
