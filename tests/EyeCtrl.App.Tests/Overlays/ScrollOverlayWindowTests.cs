namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using Xunit;

/// <summary>
/// Constructs a real <c>ScrollOverlayWindow</c> (WPF <c>Window</c> with XAML) without ever calling
/// <c>Show()</c>, mirroring <see cref="GlassBackdropWindowTests"/> — see
/// <see cref="EyeCtrl.App.Tests.Fixtures.WpfApplicationCollection"/> for why this needs the shared
/// collection and the token-resource Application.
/// </summary>
[Collection("WpfApplication")]
public class ScrollOverlayWindowTests
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [StaFact]
    public void G1_HwndStyle_IsLayeredAndClickThrough()
    {
        // F-S1 (scroll start fix): the full-screen overlay must not swallow the wheel ScrollWorker
        // injects at the anchor. A layered window without WS_EX_TRANSPARENT hit-tests its painted
        // pixels (the crosshair's 0.5 alpha), so the wheel was delivered here and dropped. Mutant:
        // drop IsClickThrough → WS_EX_TRANSPARENT is cleared, red.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ScrollOverlayWindow();
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            Check.That(hwnd).IsNotEqualTo(IntPtr.Zero);

            int style = GetWindowLong(hwnd, GwlExStyle);
            Check.WithCustomMessage($"ex-style 0x{style:X8}: WS_EX_LAYERED must be set")
                .That((style & WsExLayered) != 0).IsTrue();
            Check.WithCustomMessage(
                    $"ex-style 0x{style:X8}: WS_EX_TRANSPARENT must be set — the scroll overlay is "
                    + "feedback-only, and the wheel must pass through it to the app under the eye.")
                .That((style & WsExTransparent) != 0).IsTrue();
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void EscapeHint_IsVisibleWithNonEmptyText()
    {
        // Unlike the backdrop's hint, scroll mode's hint has no show/hide toggle: this window
        // exists only while scroll mode is active (ScrollModeController constructs and closes it
        // around the whole session), so the hint just needs to always render when the window does.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new ScrollOverlayWindow();

        var hint = (TextBlock)typeof(ScrollOverlayWindow)
            .GetField("EscapeHint", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

        Check.That(hint.Visibility).IsNotEqualTo(Visibility.Collapsed);
        Check.That(hint.Text).IsNotEmpty();
    }
}
