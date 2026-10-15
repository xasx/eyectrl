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
    private const int AccentEnableTransparentGradient = 2;

    /// <summary>
    /// The blur accent's fixed glass tint (alpha 1, RGB black) — r5's approved look. The user's
    /// opacity is the black dim layer's alpha instead (<see cref="AddDimLayer"/>), because the
    /// accent's own gradient mixes toward a fixed dark grey (about luma 34), not toward black: it
    /// brightens dark content and cannot dim it (sweep 2).
    /// </summary>
    private const int GlassTint = 0x01000000;

    private const int DwmwaSystemBackdropType = 38;

    // DWM_SYSTEMBACKDROP_TYPE (dwmapi.h). Only NONE is used now: ClearDwmBackdrop's hygiene against
    // a system backdrop set by an older build. The Mica/Acrylic system backdrops were deleted with
    // TryApplyMica/TryApplyAcrylicViaDwm (2026-10-14): on this never-activated overlay they only
    // ever showed their flat inactive fallback.
    private const int DwmsbtNone = 1;

    private Rectangle? _solidBackground;
    private Rectangle? _dimLayer;
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
    /// Applies the settings' material, all three through the accent API since 2026-10-14: Mica and
    /// Acrylic are the legacy accent blur (the DWM system backdrops only ever showed their flat
    /// inactive fallback on this never-activated, tool-window overlay), and Solid darken is the
    /// transparent gradient. On the BLUR path the accent's tint is fixed (<see cref="GlassTint"/>)
    /// and the user's opacity drives one pure-black client layer over it (<see cref="AddDimLayer"/>);
    /// on the Solid-darken path the gradient itself carries the opacity. The rgb(18) solid darken
    /// remains the fallback when the accent call fails. Exactly one rectangle is drawn on the blur
    /// path's fallback and on Solid darken's — never both a dim layer and the fallback.
    /// </summary>
    public void ApplyBackdrop(BackdropSettings settings)
    {
        if (!settings.Enabled)
        {
            ClearContent();
            return;
        }

        ClearContent();

        // ClearDwmBackdrop: type NONE, frame collapsed — the state production's Phase 2 always ran
        // from, and hygiene against a system backdrop set by an older build.
        ClearDwmBackdrop();

        var applied = settings.Type switch
        {
            BackdropType.Mica or BackdropType.Acrylic => TryApplyAcrylic(),
            BackdropType.SolidDarken => TryApplyTransparentGradient(settings.Opacity),
            _ => false
        };

        if (applied)
        {
            // The blur's dim is the black layer (the measured dim is exactly multiplicative); the
            // Solid-darken gradient already carries the opacity, so it draws nothing.
            if (settings.Type is BackdropType.Mica or BackdropType.Acrylic)
                AddDimLayer(settings.Opacity);
            return;
        }

        // The accent call failed: the rgb(18) solid darken keeps the overlay usable, at the user's opacity.
        ApplySolidDarken(settings.Opacity);
    }

    /// <summary>
    /// The blur path's dim: one pure-BLACK (0,0,0) client layer at the user's opacity. Black because
    /// the measured dim is exactly multiplicative — an offset grey (rgb 18) would tint it — and one
    /// layer because it sits UNDER the ✕ (<c>DrawHideButton</c> runs after this).
    /// </summary>
    private void AddDimLayer(double opacity)
    {
        _dimLayer = new Rectangle
        {
            Width = Width,
            Height = Height,
            Fill = new SolidColorBrush(Color.FromArgb(
                (byte)(Math.Clamp(opacity, 0.0, 1.0) * 255), 0, 0, 0))
        };
        Canvas.SetLeft(_dimLayer, 0);
        Canvas.SetTop(_dimLayer, 0);
        BackdropCanvas.Children.Add(_dimLayer);
    }

    /// <summary>
    /// The SOLID-DARKEN gradient's black tint at the user's opacity (RGB black, alpha = the opacity).
    /// The blur path's tint is the fixed <see cref="GlassTint"/> instead; there the slider drives
    /// <see cref="AddDimLayer"/>'s layer.
    /// </summary>
    internal static int AccentGradientColor(double opacity) =>
        (int)(Math.Clamp(opacity, 0.0, 1.0) * 255) << 24;

    /// <summary>
    /// The native <c>SetWindowCompositionAttribute</c> call behind <see cref="SetAccentPolicy"/>:
    /// builds the policy and returns the API's BOOL (non-zero = accepted).
    /// </summary>
    private static int NativeSetAccentPolicy(IntPtr hwnd, int state, int flags, int gradient)
    {
        var data = default(WindowCompositionAttributeData);
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = state,
                AccentFlags = flags,
                GradientColor = gradient
            };
            data.SizeOfData = Marshal.SizeOf(accent);
            data.Attribute = 19;
            data.Data = Marshal.AllocHGlobal(data.SizeOfData);
            Marshal.StructureToPtr(accent, data.Data, false);
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (data.Data != IntPtr.Zero)
                Marshal.FreeHGlobal(data.Data);
        }
    }

    /// <summary>
    /// The accent call itself, injectable so the gate can pin the use-site policy headlessly
    /// (production keeps <see cref="NativeSetAccentPolicy"/>; the gate records or fails the call).
    /// </summary>
    internal Func<IntPtr, int, int, int, int> SetAccentPolicy { get; set; } = NativeSetAccentPolicy;

    /// <summary>
    /// The legacy (undocumented) acrylic blur — <c>SetWindowCompositionAttribute</c> with
    /// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c> — production's material for both named backdrop types
    /// since 2026-10-14, at the fixed <see cref="GlassTint"/>. Known quirk of the API: the accent can
    /// visibly lag while a window is dragged; irrelevant for a static, full-screen overlay.
    /// </summary>
    /// <returns>True when the call was accepted (non-zero BOOL), so no darken fallback is needed.</returns>
    internal bool TryApplyAcrylic()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var result = SetAccentPolicy(hwnd, AccentEnableAcrylicBlurBehind, 0x20, GlassTint);
        return result != 0;
    }

    /// <summary>
    /// The see-through dark tint — <c>ACCENT_ENABLE_TRANSPARENTGRADIENT</c> (state 2) with the same
    /// opacity-driven black gradient as the blur. Solid darken's material since 2026-10-14: a plain
    /// rectangle is opaque black whatever its alpha on a non-layered client (DWM ignores the
    /// client's alpha), so the setting did nothing but go black.
    /// </summary>
    /// <returns>True when the call was accepted (non-zero BOOL), so no darken fallback is needed.</returns>
    internal bool TryApplyTransparentGradient(double opacity)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var result = SetAccentPolicy(hwnd, AccentEnableTransparentGradient, 0x20, AccentGradientColor(opacity));
        return result != 0;
    }

    /// <summary>
    /// Switches the accent off (ACCENT_DISABLED, state 0). The window is a singleton: without this,
    /// a show as Mica or Acrylic left the blur and its tint behind, so a later Solid darken show
    /// kept the stale blur plus the darken rectangle.
    /// </summary>
    private void DisableAccent()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        _ = SetAccentPolicy(hwnd, 0, 0, 0);
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
        DisableAccent();
        _solidBackground = null;
        _dimLayer = null;
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
