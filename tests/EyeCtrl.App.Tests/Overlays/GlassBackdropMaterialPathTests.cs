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
/// legacy accent blur for BOTH named types — the DWM system backdrops only ever showed their flat
/// inactive fallback on this never-activated, tool-window overlay. The solid darken stays the
/// fallback when the accent fails, and the user's opacity rides the accent's black tint.
///
/// <para>
/// No window is shown: the handle is created with <see cref="WindowInteropHelper.EnsureHandle"/>
/// (SourceInitialized runs), the accent is real on the production-path test (the call works without
/// a visible window), and the decision itself is driven through the injectable
/// <c>ApplyBackdropCore</c> seam with recording stubs — the recording subclass a WPF XAML window
/// cannot have (LoadComponent rejects a derived instance).
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
    public void ApplyBackdrop_WithTheNamedTypes_NeverSetsTheDwmSystemBackdrop(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            var settings = new BackdropSettings { Type = type, Enabled = true, Opacity = 0.3 };

            // The REAL production path. Its accent call needs a composed window and is refused
            // headlessly (the darken fallback then draws; that transition is pinned through the
            // injectable seam below). What this test pins is that the DWM system backdrop is never
            // applied any more: the attribute stays NONE after ClearDwmBackdrop.
            window.ApplyBackdrop(settings);

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

    [StaFact]
    public void ApplyBackdropCore_WithTheAccentAccepted_LeavesNoDarkenFill_AndPassesTheOpacity()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var settings = new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = 0.3 };
            var recorded = new List<double>();

            window.ApplyBackdropCore(settings, opacity => { recorded.Add(opacity); return true; });

            Check.WithCustomMessage("the accent must receive the user's opacity.")
                .That(recorded).ContainsExactly(0.3);
            Check.WithCustomMessage("the accent succeeded: no darken fallback may be drawn.")
                .That(SolidRectangles(window)).IsEqualTo(0);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdropCore_WhenTheAccentFails_DrawsTheSolidDarkenFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var settings = new BackdropSettings { Type = BackdropType.Acrylic, Enabled = true, Opacity = 0.4 };
            var recorded = new List<double>();

            window.ApplyBackdropCore(settings, opacity => { recorded.Add(opacity); return false; });

            Check.WithCustomMessage("the accent must have been attempted, at the user's opacity.")
                .That(recorded).ContainsExactly(0.4);
            Check.WithCustomMessage("the accent failed: the solid darken must keep the overlay usable.")
                .That(SolidRectangles(window)).IsEqualTo(1);
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

    private const int DwmwaSystemBackdropType = 38;
    private const int DwmBackdropNone = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
