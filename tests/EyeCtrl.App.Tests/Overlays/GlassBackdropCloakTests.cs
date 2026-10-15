namespace EyeCtrl.App.Tests.Overlays;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Services;
using EyeCtrl.Core.Settings;
using EyeCtrl.Tests.TestSupport;
using Xunit;

/// <summary>
/// The flicker fix's second half, headless: DWM's window transitions are disabled at handle
/// creation, and <c>ShowBackdrop</c> CLOAKS the window before it is shown (DWM would otherwise
/// compose its own first frame of a half-prepared window), uncloaking only after WPF has rendered a
/// frame — with a 250 ms fallback, and an immediate uncloak in <c>Hide()</c>. Both DWM writes go
/// through the <c>SetDwmAttribute</c> seam, so the gates record attribute, value and whether the
/// window was visible at the moment of the call. The window runs OFF-SCREEN (the
/// <c>PositionForShow</c> seam), so nothing is painted on the owner's screen.
/// </summary>
[Collection("WpfApplication")]
public class GlassBackdropCloakTests
{
    [StaFact]
    public void TheTransitionsAreDisabled_AtHandleCreation()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        try
        {
            var recorded = new List<(int Attribute, bool Value)>();
            var real = window.SetDwmAttribute;
            window.SetDwmAttribute = (hwnd, attribute, value) =>
            {
                recorded.Add((attribute, value));
                return real(hwnd, attribute, value);
            };

            _ = new WindowInteropHelper(window).EnsureHandle(); // raises SourceInitialized

            Check.WithCustomMessage(
                    $"expected a (DWMWA_TRANSITIONS_FORCEDISABLED, true) write at handle creation, saw: "
                    + string.Join(" | ", recorded))
                .That(recorded).Contains((3, true));
        }
        finally
        {
            try { window.Close(); } catch { /* never shown; nothing to clean beyond the handle */ }
        }
    }

    [StaFact]
    public void ShowBackdrop_CloaksBeforeTheShow_AndUncloaksAfterTheFirstFrame_BothShows()
    {
        var run = RunOffScreen(0.3, shows: 2);

        // Each show: cloak(true) while hidden, then cloak(false) after a frame — and the visible
        // window is uncloaked by the time the pump ends.
        Check.WithCustomMessage($"unexpected cloak sequence: {string.Join(" | ", run.Cloaks)}")
            .That(run.Cloaks).HasSize(4);
        Check.That(run.Cloaks[0]).IsEqualTo((true, false));   // (cloaked, visibleAtCall)
        Check.That(run.Cloaks[1]).IsEqualTo((false, true));
        Check.That(run.Cloaks[2]).IsEqualTo((true, false));
        Check.That(run.Cloaks[3]).IsEqualTo((false, true));
        Check.That(run.Window.IsVisible).IsTrue();
    }

    [StaFact]
    public void Hide_UncloaksImmediately_BeforeAnyFrame()
    {
        var run = RunOffScreen(0.3, shows: 1, hideImmediately: true);

        // cloak(true) at the show, then Hide()'s uncloak — with no dispatcher pump in between.
        // Uncloak runs BEFORE base.Hide (so no hidden cloak can even briefly exist), which is why
        // the window is still visible at that call; what matters is that no frame was needed.
        Check.WithCustomMessage($"unexpected cloak sequence: {string.Join(" | ", run.Cloaks)}")
            .That(run.Cloaks).HasSize(2);
        Check.That(run.Cloaks[0]).IsEqualTo((true, false));
        Check.That(run.Cloaks[1]).IsEqualTo((false, true));
        Check.That(run.Window.IsVisible).IsFalse();
    }

    [StaFact]
    public void TheCloakKeepsTheNeverActivateStyles_AndTheCaptureExclusion()
    {
        var run = RunOffScreen(0.3, shows: 1, pumpAfterShow: false);

        var hwnd = new WindowInteropHelper(run.Window).Handle;
        const int GwlExStyle = -20;
        const int WsExTransparent = 0x00000020;
        const int WsExNoActivate = 0x08000000;
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        Check.WithCustomMessage($"cloaked window lost WS_EX_NOACTIVATE: exstyle 0x{exStyle:X8}")
            .That((exStyle & WsExNoActivate) != 0).IsTrue();
        Check.WithCustomMessage($"cloaked window lost click-through: exstyle 0x{exStyle:X8}")
            .That((exStyle & WsExTransparent) != 0).IsTrue();
        Check.That(GetWindowDisplayAffinity(hwnd, out var affinity)).IsTrue();
        Check.WithCustomMessage($"cloaked window lost its capture exclusion: affinity 0x{affinity:X}")
            .That(affinity).IsEqualTo(0x00000011u);

        try { run.Window.Hide(); } catch { /* the window may already be gone */ }
        try { run.Window.Close(); } catch { /* the window may already be gone */ }
    }

    private static (GlassBackdropWindow Window, List<(bool Cloaked, bool VisibleAtCall)> Cloaks) RunOffScreen(
        double opacity, int shows, bool hideImmediately = false, bool pumpAfterShow = true)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var window = new GlassBackdropWindow();
        var cloaks = new List<(bool Cloaked, bool VisibleAtCall)>();

        var real = window.SetDwmAttribute;
        window.SetDwmAttribute = (hwnd, attribute, value) =>
        {
            if (attribute == 13) cloaks.Add((value, window.IsVisible));
            return real(hwnd, attribute, value);
        };
        window.PositionForShow = w =>
        {
            OverlayPositioning.CoverPrimaryScreen(w);
            w.Left = -40000;
            w.Top = -40000;
        };

        var settings = new BackdropSettings { Type = BackdropType.Mica, Enabled = true, Opacity = opacity };
        window.ShowBackdrop(settings, new PhysicalPoint(100, 100));
        if (pumpAfterShow) LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
        if (hideImmediately)
        {
            window.Hide();
        }
        else if (shows == 2)
        {
            window.Hide();
            window.ShowBackdrop(settings, new PhysicalPoint(100, 100));
            if (pumpAfterShow) LiveCheck.PumpFor(TimeSpan.FromMilliseconds(400));
        }

        return (window, cloaks);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
