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
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using Xunit;

/// <summary>
/// The final material design, headless (the sweep-3b production change): the composition target is
/// transparent (H3), the blur accent (state 4, flags 0x20) carries the fixed
/// <c>GlassTint</c> 0x01000000, and the user's opacity drives ONE pure-black client layer over it
/// (<c>AddDimLayer</c>) — exactly one rectangle on the blur path, the rgb(18) fallback only when the
/// accent is refused, and no rectangle at all on Solid darken (its gradient carries the alpha).
/// At the widened range's floor (0.0) NOTHING is drawn on any path — pure glass, and with the
/// accent refused the alpha-0 rgb(18) sheet is skipped too (the refused client is opaque black
/// whatever is drawn, so the sheet changes no pixels).
///
/// <para>
/// The accent call is injectable (<see cref="GlassBackdropWindow.SetAccentPolicy"/>), so the tests
/// drive the REAL <c>ApplyBackdrop</c> and inspect the canvas. Geometry runs at the user's 2.0 so
/// the layer's size is the screen in DIP (<c>CoverPrimaryScreen</c> is called the way
/// <c>Show()</c> does); the DpiHelper restore hook puts the scale back afterwards.
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
    [InlineData(BackdropType.Glass)]
    public void ApplyBackdrop_BlurAt030_SendsTheFixedGlassTint_AndAddsOneBlackDimLayerBelowTheClose(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(2.0, 2.0); // the user's scale; the assembly hook restores it
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            OverlayPositioning.CoverPrimaryScreen(window); // Show() sizes the window this way
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = type, Enabled = true, Opacity = 0.3 });

            Check.WithCustomMessage($"expected [disable, blur], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.WithCustomMessage("the blur accent's tint must be the FIXED glass tint, not the opacity.")
                .That(recorder.Calls[1]).IsEqualTo((4, 0x20, 0x01000000));

            var rects = Rectangles(window);
            Check.WithCustomMessage("the blur path draws exactly one layer: the dim.").That(rects).HasSize(1);
            Check.WithCustomMessage($"the dim must be pure black at 0.3, saw {Fill(rects[0])}.")
                .That(Fill(rects[0])).IsEqualTo(Color.FromArgb(0x4C, 0, 0, 0));
            Check.That(rects[0].Width).IsEqualTo(window.Width);
            Check.That(rects[0].Height).IsEqualTo(window.Height);

            // Below the ✕: DrawHideButton runs after ApplyBackdrop in production and in ShowBackdropIfEnabled.
            window.DrawHideButton(new PhysicalPoint(100, 100));
            var children = ((Canvas)window.FindName("BackdropCanvas")!).Children;
            var glyph = children.OfType<TextBlock>().Single(text => text.Text == "\u2715");
            Check.WithCustomMessage("the ✕ must sit above the dim layer.")
                .That(children.IndexOf(glyph)).IsStrictlyGreaterThan(children.IndexOf(rects[0]));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaTheory]
    [InlineData(BackdropType.Glass)]
    public void ApplyBackdrop_BlurAt060_ScalesTheDimLayer_ButKeepsTheGlassTintFixed(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = type, Enabled = true, Opacity = 0.6 });

            Check.WithCustomMessage("the tint stays the fixed glass tint at any opacity.")
                .That(recorder.Calls[1]).IsEqualTo((4, 0x20, 0x01000000));
            var rects = Rectangles(window);
            Check.That(rects).HasSize(1);
            Check.WithCustomMessage($"the dim must be pure black at 0.6 (alpha 153), saw {Fill(rects[0])}.")
                .That(Fill(rects[0])).IsEqualTo(Color.FromArgb(153, 0, 0, 0));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_BlurAt000_DrawsNoLayer_OnTheAcceptedPath()
    {
        // The widened floor: 0.0 is "no darkening" — pure glass, NO dim layer (not one at alpha 0:
        // the owner chose no darkening, and a phantom layer would be the thing gates special-case).
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.0 });

            Check.WithCustomMessage($"expected [disable, blur], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[1]).IsEqualTo((4, 0x20, 0x01000000));
            Check.WithCustomMessage("0.0 is pure glass: zero rectangles.")
                .That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_BlurAt000_WhenTheBlurIsRefused_DrawsNothing()
    {
        // The floor's refusal decision: the rgb(18) fallback exists so a failed blur still dims the
        // desktop for legibility — at 0.0 the user asked for no darkening. With the accent refused
        // the non-layered client is opaque black whatever is drawn (DWM ignores the alpha without
        // an accent; round 2 measured it flat), so the alpha-0 sheet changes no pixels: skipping it
        // is one fewer element, same result.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 0 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.0 });

            Check.That(recorder.Calls[1].State).IsEqualTo(4);          // the blur was attempted…
            Check.WithCustomMessage("…and refused: at 0.0 even the fallback is not drawn.")
                .That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_BlurAt095_DimsToTheAlmostBlackAlpha()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.95 });

            Check.That(recorder.Calls[1]).IsEqualTo((4, 0x20, 0x01000000));   // the tint stays fixed
            var rects = Rectangles(window);
            Check.That(rects).HasSize(1);
            Check.WithCustomMessage($"the dim must be (byte)(0.95 x 255) = 242 at 0.95, saw {Fill(rects[0])}.")
                .That(Fill(rects[0])).IsEqualTo(Color.FromArgb(242, 0, 0, 0));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WithSolidDarkenAt000_SendsTheFullyTransparentGradient_AndNoRectangle()
    {
        // The owner-accepted "Solid darken at 0.0 shows nothing": the accepted state-2 path with a
        // fully transparent gradient (alpha 0) — not a special case, just the gradient at 0.0.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.0 });

            Check.WithCustomMessage($"expected [disable, gradient], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.WithCustomMessage("Solid darken at 0.0 is the transparent gradient on state 2.")
                .That(recorder.Calls[1]).IsEqualTo((2, 0x2, 0x00000000));
            Check.That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WithSolidDarkenAt095_SendsTheAlmostBlackGradient()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.95 });

            Check.WithCustomMessage("0.95 leaves (byte)(0.95 x 255) = 242, about 5 % of the desktop.")
                .That(recorder.Calls[1]).IsEqualTo((2, 0x2, unchecked((int)0xF2000000)));
            Check.That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WhenTheBlurIsRefused_DrawsOnlyTheRgb18Fallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 0 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.4 });

            Check.That(recorder.Calls[1].State).IsEqualTo(4);
            var rects = Rectangles(window);
            Check.WithCustomMessage("exactly one rectangle: the fallback, never a dim on top of it.")
                .That(rects).HasSize(1);
            Check.WithCustomMessage($"the fallback keeps its rgb(18) fill, saw {Fill(rects[0])}.")
                .That(Fill(rects[0])).IsEqualTo(Color.FromArgb(102, 18, 18, 18));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaTheory]
    [InlineData(BackdropType.Glass)]
    public void ApplyBackdrop_WithTheRealSetter_AddsTheDimLayer_AndNoDwmBackdrop(BackdropType type)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();

            // The REAL production path with the REAL native setter; accepted on a never-shown HWND.
            window.ApplyBackdrop(new BackdropSettings { Type = type, Enabled = true, Opacity = 0.3 });

            var rects = Rectangles(window);
            Check.WithCustomMessage("the accepted blur draws exactly the dim layer (no fallback).")
                .That(rects).HasSize(1);
            Check.That(Fill(rects[0])).IsEqualTo(Color.FromArgb(0x4C, 0, 0, 0));

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
    public void ApplyBackdrop_WithSolidDarken_UsesTheTransparentGradient_AndDrawsNoRectangle()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 });

            Check.WithCustomMessage($"expected [disable, gradient], saw: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(2);
            Check.That(recorder.Calls[0]).IsEqualTo((0, 0, 0));
            // Flags 2, not 0x20 (D1): 0x20 re-sent while HIDDEN to an already-shown window falls back
            // to the system accent colour — the owner's flat sage (196,200,196) on every re-show.
            Check.WithCustomMessage("Solid darken's gradient must carry the opacity, with the D1 flags.")
                .That(recorder.Calls[1]).IsEqualTo((2, 0x2, 0x4C000000));
            Check.WithCustomMessage("the gradient carries the dim: no rectangle.").That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_WhenSolidDarkenGradientIsRefused_DrawsTheRectangleFallback()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 0 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.4 });

            Check.That(recorder.Calls[1].State).IsEqualTo(2);
            Check.WithCustomMessage("the gradient was refused: the rectangle keeps the overlay usable.")
                .That(Rectangles(window)).HasSize(1);
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ApplyBackdrop_SwitchingToSolidDarken_ClearsTheStaleBlurAndItsDimLayer()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            new WindowInteropHelper(window).EnsureHandle();
            var recorder = new RecordingAccentPolicy { Result = 1 };
            window.SetAccentPolicy = recorder.Call;

            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.Glass, Enabled = true, Opacity = 0.3 });
            Check.That(Rectangles(window)).HasSize(1); // the dim layer
            window.ApplyBackdrop(new BackdropSettings { Type = BackdropType.SolidDarken, Enabled = true, Opacity = 0.3 });

            // [disable, blur] then [disable, gradient]: the stale blur goes OFF (state 0) before the
            // gradient lands, and the stale dim layer is gone with the canvas clear.
            Check.WithCustomMessage($"unexpected accent-call sequence: {string.Join(" | ", recorder.Calls)}")
                .That(recorder.Calls).HasSize(4);
            Check.That(recorder.Calls[0]).IsEqualTo((0, 0, 0));
            Check.That(recorder.Calls[1].State).IsEqualTo(4);
            Check.That(recorder.Calls[2]).IsEqualTo((0, 0, 0));
            Check.That(recorder.Calls[3].State).IsEqualTo(2);
            Check.WithCustomMessage("no dim rectangle may survive into Solid darken.")
                .That(Rectangles(window)).IsEmpty();
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [Fact]
    public void AccentGradientColor_IsBlackAtTheUsersOpacity()
    {
        // The SOLID-DARKEN gradient's alpha (the blur's tint is the fixed GlassTint):
        // 0.0 -> 0 (the widened floor: fully transparent, "shows nothing"); 0.3 -> 0x4C;
        // 0.95 -> 0xF2 = (byte)(0.95 x 255); 1.5 clamps to 0xFF; -1 clamps to 0.
        Check.That(GlassBackdropWindow.AccentGradientColor(0.0)).IsEqualTo(0);
        Check.That(GlassBackdropWindow.AccentGradientColor(0.3)).IsEqualTo(0x4C000000);
        Check.That(GlassBackdropWindow.AccentGradientColor(0.95)).IsEqualTo(unchecked((int)0xF2000000));
        Check.That(GlassBackdropWindow.AccentGradientColor(1.5)).IsEqualTo(unchecked((int)0xFF000000));
        Check.That(GlassBackdropWindow.AccentGradientColor(-1)).IsEqualTo(0);
    }

    /// <summary>The rectangles on the backdrop canvas, in z-order.</summary>
    private static List<Rectangle> Rectangles(GlassBackdropWindow window) =>
        ((Canvas)window.FindName("BackdropCanvas")!).Children.OfType<Rectangle>().ToList();

    /// <summary>A rectangle's fill colour.</summary>
    private static Color Fill(Rectangle rectangle) => ((SolidColorBrush)rectangle.Fill).Color;

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
