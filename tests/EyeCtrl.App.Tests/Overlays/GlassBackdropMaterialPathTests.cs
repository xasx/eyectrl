namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The 2026-10-14 material fix, headless: the window makes its composition target transparent (H3:
/// WPF's opaque black clear colour painted over every material), and <c>ApplyBackdrop</c> takes the
/// legacy accent blur for BOTH named types. The accent call itself is an injectable seam
/// (<see cref="GlassBackdropWindow.SetAccentPolicy"/>), so the tests drive the REAL
/// <c>ApplyBackdrop</c> and pin: the use-site policy (state 4, flags 0x20, the tint at the user's
/// opacity), the non-zero-is-success BOOL (review-05 B1), the solid darken when the setter is
/// refused, and the accent being disabled when a later Solid darken apply clears the window (B3).
///
/// <para>
/// No window is shown: the handle is created with <see cref="WindowInteropHelper.EnsureHandle"/>
/// (SourceInitialized runs), and the default setter is the real
/// <c>SetWindowCompositionAttribute</c>, which is accepted on a never-shown HWND.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class GlassBackdropMaterialPathTests
{
    [StaFact]
    public void TheCompositionTarget_IsTransparent_WhenTheSourceExists()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var target = HwndSource.FromHwnd(hwnd)!.CompositionTarget!;

            Check.WithCustomMessage(
                    $"the composition target is {target.BackgroundColor}; WPF paints opaque black over the "
                    + "material unless it is transparent (the rounds-1/2 pure-black readout).")
                .That(target.BackgroundColor).IsEqualTo(Colors.Transparent);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaTheory]
    [InlineData(BackdropType.Mica)]
    [InlineData(BackdropType.Acrylic)]
    public void ApplyBackdrop_WithTheRealSetter_IsAccepted_AndLeavesNoDarken_AndNoDwmBackdrop(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var settings = new BackdropSettings { Type = type, Enabled = true, Opacity = 0.3 };

            // The REAL production path with the REAL native setter. The call is accepted on a
            // never-shown HWND, so a darken rectangle here means the BOOL is misread (B1).
            window.ApplyBackdrop(settings);

            Check.WithCustomMessage(
                    "a darken rectangle means the accent's non-zero-is-success BOOL was misread (B1).")
                .That(SolidRectangles(window)).IsEqualTo(0);

            var hresult = DwmGetWindowAttribute(hwnd, DwmwaSystemBackdropType, out var attribute, sizeof(int));
            Check.WithCustomMessage($"DwmGetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE) failed: 0x{hresult:X8}.")
                .That(hresult).IsEqualTo(0);
            Check.WithCustomMessage(
                    $"the DWM system-backdrop attribute is {attribute}; ApplyBackdrop leaves it NONE(1), "
                    + "never Mica(2)/Acrylic(3).")
                .That(attribute).IsEqualTo(DwmBackdropNone);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaTheory]
    [InlineData(BackdropType.Mica)]
    [InlineData(BackdropType.Acrylic)]
    public void ApplyBackdrop_PinsTheUseSitePolicy_AndNeedsNoDarkenWhenAccepted(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;
            var settings = new BackdropSettings { Type = type, Enabled = true, Opacity = 0.3 };

            window.ApplyBackdrop(settings);

            // ClearContent disables the accent first (B3), then the named type enables the blur at
            // the use-site tint: state 4, flags 0x20, black at alpha 76 (0.3).
            Check.WithCustomMessage($"expected [disable, acrylic], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[0]).IsEqualTo((0, 0, 0));
            Check.WithCustomMessage("the use-site policy is not the acrylic blur at the user's tint.")
                .That(recorder.Calls[1]).IsEqualTo((4, 0x20, 0x4C000000));
            Check.WithCustomMessage("the accent was accepted: no darken fallback may be drawn.")
                .That(SolidRectangles(window)).IsEqualTo(0);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WhenTheAccentPolicyIsRefused_DrawsTheSolidDarkenFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 0 };
            window.SetAccentPolicy = recorder.Call;
            var settings = new BackdropSettings { Type = BackdropType.Acrylic, Enabled = true, Opacity = 0.4 };

            window.ApplyBackdrop(settings);

            Check.WithCustomMessage($"expected [disable, acrylic], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[1].State).IsEqualTo(4);
            Check.WithCustomMessage("the accent was refused: the solid darken must keep the overlay usable.")
                .That(SolidRectangles(window)).IsEqualTo(1);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WithSolidDarken_UsesTheTransparentGradient_AtTheUsersAlpha()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;
            var settings = new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 };

            window.ApplyBackdrop(settings);

            // State 2 (ACCENT_ENABLE_TRANSPARENTGRADIENT) at the user's alpha: a see-through dark
            // tint, because a plain rectangle is opaque black on a non-layered client.
            Check.WithCustomMessage($"expected [disable, gradient], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[0]).IsEqualTo((0, 0, 0));
            Check.WithCustomMessage("the Solid darken policy is not the transparent gradient at the user's alpha.")
                .That(recorder.Calls[1]).IsEqualTo((2, 0x20, 0x4C000000));
            Check.WithCustomMessage("the gradient was accepted: no darken rectangle may be drawn.")
                .That(SolidRectangles(window)).IsEqualTo(0);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WhenTheTransparentGradientIsRefused_DrawsTheRectangleFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 0 };
            window.SetAccentPolicy = recorder.Call;
            var settings = new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.4 };

            window.ApplyBackdrop(settings);

            Check.WithCustomMessage($"expected [disable, gradient], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[1].State).IsEqualTo(2);
            Check.WithCustomMessage("the gradient was refused: the rectangle keeps the overlay usable.")
                .That(SolidRectangles(window)).IsEqualTo(1);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_SwitchingToSolidDarken_DisablesTheStaleBlur_ThenTints()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Acrylic, Enabled = true, Opacity = 0.3 });
            window.ApplyBackdrop(
                new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 });

            // [disable, blur] then [disable, gradient]: the second apply must switch the stale blur
            // OFF (state 0) before the transparent gradient lands, or the old blur plus its tint
            // would survive into Solid darken.
            Check.WithCustomMessage($"unexpected accent-call sequence: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(4);
            Check.That(recorder.Calls[0]).IsEqualTo((0, 0, 0));
            Check.That(recorder.Calls[1].State).IsEqualTo(4);
            Check.That(recorder.Calls[2]).IsEqualTo((0, 0, 0));
            Check.That(recorder.Calls[3].State).IsEqualTo(2);
            Check.WithCustomMessage("only the first, Acrylic apply may enable the blur.")
                .That(recorder.Calls.Count(call => call.State == 4)).IsEqualTo(1);
            Check.WithCustomMessage("the gradient was accepted: Solid darken draws no rectangle.")
                .That(SolidRectangles(window)).IsEqualTo(0);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [Fact]
    public void AccentGradientColor_IsBlackAtTheUsersOpacity()
    {
        // 0.3 -> alpha 76 (0x4C); 0.9 -> 229 (0xE5); 1.5 clamps to 255 (0xFF); -1 clamps to 0.
        Check.That(GlassBackdropWindow.AccentGradientColor(0.3)).IsEqualTo(0x4C000000);
        Check.That(GlassBackdropWindow.AccentGradientColor(0.9)).IsEqualTo(unchecked((int)0xE5000000));
        Check.That(GlassBackdropWindow.AccentGradientColor(1.5)).IsEqualTo(unchecked((int)0xFF000000));
        Check.That(GlassBackdropWindow.AccentGradientColor(-1)).IsEqualTo(0);
    }

    /// <summary>The solid-darken fallback's rectangle count on the backdrop canvas.</summary>
    private static int SolidRectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().Count();

    /// <summary>Records the accent-policy calls the window makes and answers with a configured BOOL.</summary>
    private sealed class RecordingAccentPolicy
    {
        public List<(int State, int Flags, int Gradient)> Calls { get; } = new();
        public int Result { get; set; } = 1;

        public int Call(IntPtr hwnd, int state, int flags, int gradient)
        {
            Calls.Add((state, flags, gradient));
            return Result;
        }
    }

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmBackdropNone = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
