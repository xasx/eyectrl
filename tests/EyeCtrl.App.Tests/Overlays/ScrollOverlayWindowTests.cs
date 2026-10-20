namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shapes;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
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
    public void F2_UnarmedCrosshair_IsProminent_AndDimsOnArming()
    {
        // F2 (fixup 01): the arming cue. Until the eye visits the dead zone the crosshair is drawn
        // at full opacity and double thickness ("look at the cross to start"); after arming it
        // returns to today's 0.5 / 1. The window reads vm.IsArmed through its DataContext in
        // UpdateFeedback. Mutant: drop the cue → the unarmed assertions red.
        WpfApplicationGate.EnsureApplicationWithTokens();
        var settings = new EyeCtrlSettings();
        var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
        vm.EnterScrollMode(new PhysicalPoint(1000, 1000), new PhysicalPoint(3000, 600));
        var window = new ScrollOverlayWindow();
        try
        {
            window.Place(vm);
            window.UpdateFeedback(0, 0, 0);

            Line h = Crosshair(window, "AnchorCrosshairH");
            Line v = Crosshair(window, "AnchorCrosshairV");
            Line haloH = Crosshair(window, "AnchorCrosshairHaloH");
            Line haloV = Crosshair(window, "AnchorCrosshairHaloV");
            Check.That(vm.IsArmed).IsFalse();
            Check.That(h.Opacity).IsCloseTo(1.0, 1e-9);
            Check.That(v.Opacity).IsCloseTo(1.0, 1e-9);
            Check.That(h.StrokeThickness).IsCloseTo(2.0, 1e-9);
            Check.That(v.StrokeThickness).IsCloseTo(2.0, 1e-9);
            // Fixup 02 (the cue's contrast on white pages): a halo under-stroke, 2 DIP wider than
            // its line, only while unarmed. Mutant: drop the toggle → the halo stays Collapsed.
            Check.WithCustomMessage("the unarmed halo must be visible")
                .That(haloH.Visibility).IsEqualTo(Visibility.Visible);
            Check.That(haloV.Visibility).IsEqualTo(Visibility.Visible);
            Check.WithCustomMessage("the halo must be 2 DIP wider than the line")
                .That(haloH.StrokeThickness).IsCloseTo(h.StrokeThickness + 2, 1e-9);
            Check.That(haloV.StrokeThickness).IsCloseTo(v.StrokeThickness + 2, 1e-9);

            for (long t = 0; t <= 150_000; t += 30_000)   // arm it: a 150-ms fixation on the cross
                vm.Receive(new GazeCursorMovedMessage(new PhysicalPoint(1000, 1000), t));
            Check.That(vm.IsArmed).IsTrue();
            window.UpdateFeedback(0, 0, 0);

            Check.WithCustomMessage("the armed look has no halo")
                .That(haloH.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(haloV.Visibility).IsEqualTo(Visibility.Collapsed);

            Check.That(h.Opacity).IsCloseTo(0.5, 1e-9);
            Check.That(v.Opacity).IsCloseTo(0.5, 1e-9);
            Check.That(h.StrokeThickness).IsCloseTo(1.0, 1e-9);
            Check.That(v.StrokeThickness).IsCloseTo(1.0, 1e-9);
        }
        finally
        {
            window.Close();
        }
    }

    [StaTheory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void G8_TheStartCircle_ShowsWhileUnarmedOnly(double scale)
    {
        // C4: a faint circle of 2 x StartRadiusDip DIP around the cross, centred on the anchor,
        // drawn only until scrolling has started. Window constructed, never shown. Mutants: M12 the
        // circle never shown → the unarmed assertions red; M13 shown after arming → the armed
        // assertion red.
        WpfApplicationGate.EnsureApplicationWithTokens();
        DpiHelper.Initialize(scale, scale);
        var settings = new EyeCtrlSettings();
        var vm = new ScrollOverlayViewModel(new WeakReferenceMessenger(), settings.Scroll, settings.Dwell);
        var anchor = new PhysicalPoint(1000, 1000);
        vm.EnterScrollMode(anchor, new PhysicalPoint(3000, 600));
        var window = new ScrollOverlayWindow();
        try
        {
            window.Place(vm);
            window.UpdateFeedback(0, 0, 0);

            double expected = 2 * ScrollOverlayViewModel.StartRadiusDip;
            double anchorX = DpiHelper.PhysicalToLogicalX(anchor.X);
            double anchorY = DpiHelper.PhysicalToLogicalY(anchor.Y);
            foreach (var ring in new[] { window.StartCircle, window.StartCircleHalo })
            {
                Check.WithCustomMessage($"scale {scale}: the unarmed start circle must be visible")
                    .That(ring.Visibility).IsEqualTo(Visibility.Visible);
                Check.That(ring.Width).IsCloseTo(expected, 1e-9);
                Check.That(ring.Height).IsCloseTo(expected, 1e-9);
                Check.That(Canvas.GetLeft(ring) + ring.Width / 2).IsCloseTo(anchorX, 1e-9);
                Check.That(Canvas.GetTop(ring) + ring.Height / 2).IsCloseTo(anchorY, 1e-9);
            }

            for (long t = 0; t <= 150_000; t += 30_000)
                vm.Receive(new GazeCursorMovedMessage(anchor, t));
            Check.That(vm.IsArmed).IsTrue();
            window.UpdateFeedback(0, 0, 0);

            Check.WithCustomMessage("the start circle disappears the moment scrolling starts")
                .That(window.StartCircle.Visibility).IsEqualTo(Visibility.Collapsed);
            Check.That(window.StartCircleHalo.Visibility).IsEqualTo(Visibility.Collapsed);
        }
        finally
        {
            DpiHelper.Initialize(1.0, 1.0);
            window.Close();
        }
    }

    private static Line Crosshair(Window window, string fieldName) =>
        (Line)typeof(ScrollOverlayWindow)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(window)!;

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
