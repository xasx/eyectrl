namespace EyeCtrl.App.Overlays;

using EyeCtrl.App.Services;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
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

    /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED: stops DWM's open/close animation for the window.</summary>
    private const int DwmwaTransitionsForceDisabled = 3;

    /// <summary>DWMWA_CLOAK: DWM skips composing the window entirely while true.</summary>
    private const int DwmwaCloak = 13;

    // DWM_SYSTEMBACKDROP_TYPE (dwmapi.h). Only NONE is used now: ClearDwmBackdrop's hygiene against
    // a system backdrop set by an older build. The Mica/Acrylic system backdrops were deleted with
    // TryApplyMica/TryApplyAcrylicViaDwm (2026-10-14): on this never-activated overlay they only
    // ever showed their flat inactive fallback.
    private const int DwmsbtNone = 1;

    /// <summary>
    /// How soon after <see cref="Hide"/> a <see cref="ShowBackdrop"/> with unchanged settings counts
    /// as the magnifier → ring hand-off (production's two back-to-back dispatcher posts land ~1 ms
    /// apart; a frame of slack keeps it robust under load) and reuses the prepared content instead
    /// of clearing and re-applying it.
    /// </summary>
    private const long HandOffWindowMs = 33;

    private Rectangle? _solidBackground;
    private Rectangle? _dimLayer;
    private DwellArcButton? _hideButton;
    private bool _cloaked;
    private DispatcherTimer? _cloakFallback;
    private EventHandler? _uncloakOnRendering;

    /// <summary>The settings the canvas is currently prepared for, or null when it holds nothing.</summary>
    private (BackdropType Type, double Opacity)? _preparedFor;

    /// <summary>
    /// A monotonic clock for the hand-off window: <see cref="Environment.TickCount64"/>'s ~15.6 ms
    /// resolution turns the 33 ms window into 33-47 ms, so a <see cref="Stopwatch"/> is used instead.
    /// </summary>
    private static readonly Stopwatch HandOffClock = Stopwatch.StartNew();

    /// <summary>When <see cref="Hide"/> last ran; opens the hand-off window (see HandOffWindowMs).</summary>
    private long _hiddenAtMs;

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

        // DWM's open/close transitions animate the window in; the backdrop must appear in ONE
        // finished frame (the owner's flicker), so they are off for this window's lifetime.
        if (hwnd != IntPtr.Zero)
            _ = SetDwmAttribute(hwnd, DwmwaTransitionsForceDisabled, true);
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

    /// <summary>
    /// Blocks until DWM has committed and presented the pending composition. Called once after the
    /// uncloak so the finished frame is on screen before the cloak lifts (a cloak lifted on a
    /// composition that has not been presented yet shows the desktop for a frame).
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    // ── Show / Hide ──

    /// <summary>
    /// Positioning for <see cref="ShowBackdrop"/>, injectable so the headless gates can show the
    /// window OFF-SCREEN (production: <see cref="OverlayPositioning.CoverPrimaryScreen"/>, which
    /// <see cref="Show"/> uses too).
    /// </summary>
    internal Action<Window> PositionForShow { get; set; } = OverlayPositioning.CoverPrimaryScreen;

    /// <summary>
    /// The production show path: everything — position, handle, material, dim layer, ✕ — is prepared
    /// while the window is still HIDDEN, and <see cref="Window.Show"/> runs last, so the FIRST
    /// visible frame is already the finished backdrop (the owner's flicker: show-then-apply put a
    /// bare or glass-only frame on screen first). <see cref="OverlayWindow.OnSourceInitialized"/>
    /// still runs at handle creation, so the never-activate styles and the capture exclusion are in
    /// place before the window can be seen. DWM's window transitions are disabled too, and the window
    /// is CLOAKED until WPF has rendered the prepared content — DWM would otherwise compose its own
    /// first frame of a half-prepared window.
    ///
    /// <para>
    /// <b>The magnifier → ring hand-off.</b> Production calls <see cref="Hide"/> and then this method
    /// within about a millisecond (two back-to-back dispatcher posts in
    /// <c>GazeEngineAdapter</c>). A clear-and-reprepare pair there would put a bare frame on screen
    /// (worse with a cloak: hidden, then cloaked until WPF renders). When the window was hidden within
    /// a frame and the SETTINGS ARE UNCHANGED, the prepared content is still on the canvas, so the
    /// hand-off just shows it — no clear, no re-apply, no cloak, no bare or undimmed frame.
    /// </para>
    /// </summary>
    public void ShowBackdrop(BackdropSettings settings, PhysicalPoint hideButtonCenter)
    {
        if (System.Windows.Forms.Screen.PrimaryScreen == null)
            return;
        PositionForShow(this);   // the layer below sizes itself to the window
        EnsureHandle();          // a real hwnd: the accent needs it, and the CT turns transparent

        if (IsVisible)
        {
            // A show on an already-VISIBLE window must neither re-prepare-and-cloak it (the cloak
            // would blank a window that is on screen until a frame) nor re-send the accent. Unchanged
            // settings are a no-op; changed settings are re-applied in place — one synchronous
            // prepare, so DWM composes only the final state — and never re-cloaked.
            if (_preparedFor is { } current
                && current.Type == settings.Type
                && Math.Abs(current.Opacity - settings.Opacity) < 1e-9)
                return;

            ApplyBackdrop(settings);
            DrawHideButton(hideButtonCenter);
            _preparedFor = (settings.Type, settings.Opacity);
            UpdateHideButtonProgress(0);
            return;
        }

        if (settings.Enabled
            && !IsVisible
            && _preparedFor is { } prepared
            && prepared.Type == settings.Type
            && Math.Abs(prepared.Opacity - settings.Opacity) < 1e-9
            && HandOffClock.ElapsedMilliseconds - _hiddenAtMs <= HandOffWindowMs)
        {
            // The hand-off: the finished content is already prepared, so SHOW it — nothing is
            // cleared, nothing re-applied, nothing cloaked, and there is no bare frame. The ✕'s
            // dwell progress from the interrupted dwell is reset: this is a new show.
            UpdateHideButtonProgress(0);
            base.Show();
            return;
        }

        ApplyBackdrop(settings);
        DrawHideButton(hideButtonCenter); // after the dim layer, so the ✕ sits on top
        _preparedFor = (settings.Type, settings.Opacity);

        // Cloak until WPF has rendered the prepared content.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _cloaked = true;
        _ = SetDwmAttribute(hwnd, DwmwaCloak, true);

        base.Show();
        UncloakAfterFirstRender();
    }

    /// <summary>
    /// Releases the cloak once WPF has rendered at least one frame of the prepared content: the
    /// static <c>CompositionTarget.Rendering</c> event fires before the UI-thread render commit, so
    /// the uncloak runs from a <see cref="DispatcherPriority.Background"/> callback (after the
    /// commit), and one <c>DwmFlush</c> presents the finished frame before the cloak lifts. A 250 ms
    /// fallback uncloaks anyway, so a window that never renders cannot stay cloaked.
    /// </summary>
    private void UncloakAfterFirstRender()
    {
        // Normal priority (NOT Send): the dispatcher does not dispatch Send-priority timer ticks, so
        // a Send fallback would never fire and a window that never rendered would stay cloaked.
        _cloakFallback?.Stop();
        _cloakFallback = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _cloakFallback.Tick += (_, _) => UncloakAndFlush();
        _cloakFallback.Start();

        RemoveUncloakHandler();
        _uncloakOnRendering = (_, _) =>
        {
            RemoveUncloakHandler();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UncloakAndFlush));
        };
        CompositionTarget.Rendering += _uncloakOnRendering;
    }

    /// <summary>
    /// Presents the finished frame and then lifts the cloak — in that order: the render thread
    /// presents asynchronously, so uncloaking first can compose the PRE-render surface (the
    /// glass-only frame the cloak exists to hide). DWM keeps updating a cloaked window's surface,
    /// so the flush presents the finished frame while the window is still cloaked, and only then
    /// does the uncloak make it visible.
    /// </summary>
    private void UncloakAndFlush()
    {
        FlushDwm();
        Uncloak();
    }

    private void RemoveUncloakHandler()
    {
        if (_uncloakOnRendering is { } handler)
        {
            CompositionTarget.Rendering -= handler;
            _uncloakOnRendering = null;
        }
    }

    /// <summary>
    /// Cancels a pending uncloak (the Rendering subscription and the fallback timer) without
    /// touching the cloak bit — for <see cref="OnClosed"/>, where the handle is already gone so
    /// there is nothing left to uncloak, but a stray callback must not fire later.
    /// </summary>
    private void CancelPendingUncloak()
    {
        RemoveUncloakHandler();
        _cloakFallback?.Stop();
        _cloakFallback = null;
    }

    /// <summary>
    /// Releases the cloak, if it is still up, cancelling the pending uncloak first. Safe to call
    /// repeatedly; <see cref="Hide"/> and <see cref="Show"/> call it so a hidden or plainly shown
    /// window can never stay cloaked.
    /// </summary>
    private void Uncloak()
    {
        CancelPendingUncloak();
        if (!_cloaked)
            return;
        _cloaked = false;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            _ = SetDwmAttribute(hwnd, DwmwaCloak, false);
    }

    public new void Show()
    {
        if (System.Windows.Forms.Screen.PrimaryScreen == null)
            return;
        Uncloak(); // a cloaked window would show nothing
        OverlayPositioning.CoverPrimaryScreen(this);
        base.Show();
        EnsureHandle();
    }

    /// <summary>
    /// Hides the window without clearing the prepared content: production hides and re-shows within
    /// a frame (the magnifier → ring hand-off), and <see cref="ShowBackdrop"/> reuses exactly that
    /// prepared content, so clearing here would guarantee a bare frame. The next prepare
    /// (<see cref="ApplyBackdrop"/> → <see cref="ClearContent"/>) replaces it, and a hidden window
    /// composes nothing, so retaining it is invisible. The uncloak is immediate: a hidden window
    /// must never stay cloaked.
    /// </summary>
    public new void Hide()
    {
        Uncloak();
        base.Hide();
        _hiddenAtMs = HandOffClock.ElapsedMilliseconds;
        HideEscapeHint();
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelPendingUncloak(); // no stray uncloak may fire on a closed window
        base.OnClosed(e);
    }

    // ── Escape hint ──

    /// <summary>Shows the "Press Esc to exit" hint (bottom-right; CalibrationWindow's cancel hint
    /// is carded since the Daylight round, so the two no longer mirror each other).</summary>
    public void ShowEscapeHint() => EscapeHint.Visibility = Visibility.Visible;

    /// <summary>Hides the escape hint. Also called from <see cref="Hide"/> so a stale hint never
    /// carries over into an unrelated later use of this window (e.g. the plain action menu, which
    /// never asks for it).</summary>
    public void HideEscapeHint() => EscapeHint.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Creates the HWND if it does not exist yet. The <c>Handle</c> PROPERTY is a no-op before the
    /// window is shown (it returns Zero), which left <see cref="ShowBackdrop"/>'s accent call with
    /// hwnd 0 — refused, silently falling back to the rgb(18) rectangle. The explicit
    /// <c>EnsureHandle()</c> is the one that creates it.
    /// </summary>
    private void EnsureHandle()
    {
        _ = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
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
    /// The DWM attribute writes (transitions disabled, cloak), injectable so the gates can record
    /// them. The default is the real dwmapi call.
    /// </summary>
    internal Func<IntPtr, int, bool, int> SetDwmAttribute { get; set; } = NativeSetDwmAttribute;

    /// <summary>
    /// <c>DwmFlush</c>, injectable so a gate can prove the ordering of the flush against the uncloak
    /// in ONE sequence (the two run back to back, and only their order tells which frame is
    /// presented first). The default is the real dwmapi call.
    /// </summary>
    internal Action FlushDwm { get; set; } = NativeDwmFlush;

    private static void NativeDwmFlush() => _ = DwmFlush();

    private static int NativeSetDwmAttribute(IntPtr hwnd, int attribute, bool value)
    {
        int intValue = value ? 1 : 0;
        return DwmSetWindowAttribute(hwnd, attribute, ref intValue, sizeof(int));
    }

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
    ///
    /// <para>
    /// <b>Flags 2, not 0x20</b> (the measured fix for D1). State 2 re-sent with <c>0x20</c> while the
    /// window is HIDDEN to a window that has already been shown — exactly what production does on every
    /// re-show — falls back to the system accent colour: the owner's re-show read the flat sage
    /// (196,200,196), and the discriminator look pinned it (S-0 production → accent fallback; S-a, the
    /// same call with flags 2 → finished at 0.702; S-b and S-c cleared the order and the disable). Flags
    /// 2 renders identically on a first show (sweep 2: 18.21 vs 18.19), so it is the only difference
    /// D1 needs. State 4 (the blur) keeps <c>0x20</c>: its hidden re-show read the glass fine.
    /// </para>
    /// </summary>
    /// <returns>True when the call was accepted (non-zero BOOL), so no darken fallback is needed.</returns>
    internal bool TryApplyTransparentGradient(double opacity)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var result = SetAccentPolicy(hwnd, AccentEnableTransparentGradient, 0x2, AccentGradientColor(opacity));
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
        _preparedFor = null; // nothing is prepared any more
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
